using System.Drawing;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using EveCommandCenter.Interop;

namespace EveCommandCenter.Services;

/// <summary>Owns a cached bitmap. Readers take independent copies under the same
/// lock as disposal, so replacing a capture cannot invalidate an ongoing paint.</summary>
public sealed class FrozenFrame : IDisposable
{
    private readonly object _gate = new();
    private Bitmap? _bitmap;
    public FrozenFrame(Bitmap bitmap) => _bitmap = bitmap;
    public Bitmap? Copy() { lock (_gate) return _bitmap == null ? null : (Bitmap)_bitmap.Clone(); }
    public void Dispose() { lock (_gate) { _bitmap?.Dispose(); _bitmap = null; } }
}

/// <summary>Best-effort snapshots. Never performs PrintWindow on the UI/hook thread.
/// Native captures cannot be cancelled safely; at most two may be outstanding
/// across service instances, and one per HWND. Busy ticks are dropped, not queued.</summary>
public sealed class FrozenFrameService : IDisposable
{
    private static readonly SemaphoreSlim Workers = new(2, 2);
    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, FrozenFrame> _frames = new();
    private readonly Dictionary<IntPtr, Capture> _inFlight = new();
    private sealed class Capture { public bool Forgotten; }
    private readonly DispatcherTimer _captureTimer;
    private Func<IntPtr[]>? _hwndProvider;
    private WinEventHookService? _winEvents;
    private bool _disposed;
    private int _cursor;
    public int MaximumFrameWidth { get; set; }
    private readonly Func<IntPtr, Bitmap?> _capture;
    private readonly Func<IntPtr, bool> _canCapture;

    public FrozenFrameService() : this(CaptureWindow, hwnd => User32.IsWindow(hwnd) && !User32.IsIconic(hwnd) && !User32.IsHungAppWindow(hwnd)) { }
    internal FrozenFrameService(Func<IntPtr, Bitmap?> capture, Func<IntPtr, bool> canCapture)
    {
        _capture = capture;
        _canCapture = canCapture;
        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _captureTimer.Tick += (_, _) => ScheduleCaptures();
    }
    public void SetCaptureInterval(TimeSpan interval) { if (_captureTimer.Interval != interval) _captureTimer.Interval = interval; }
    public void SetPeriodicCaptureEnabled(bool enabled)
    {
        lock (_gate) if (_disposed) return;
        if (enabled && !_captureTimer.IsEnabled) { _captureTimer.Start(); ScheduleCaptures(); }
        else if (!enabled) _captureTimer.Stop();
    }
    public void Start(Func<IntPtr[]> hwndProvider, WinEventHookService? winEvents = null)
    {
        _hwndProvider = hwndProvider;
        if (_winEvents != null) _winEvents.WindowMinimizeStart -= OnMinimizeStart;
        _winEvents = winEvents;
        if (_winEvents != null) _winEvents.WindowMinimizeStart += OnMinimizeStart;
    }
    private void OnMinimizeStart(IntPtr hwnd)
    {
        // This event is not a guarantee that the game is still drawable. If it
        // has already minimized, keep the previous frame instead of blocking it.
        try { if (_hwndProvider?.Invoke().Contains(hwnd) == true) TryCapture(hwnd); }
        catch (Exception ex) { DiagnosticsService.LogDwm($"[Capture] Schedule failed: {ex.Message}"); }
    }
    public FrozenFrame? GetLastFrame(IntPtr hwnd) { lock (_gate) return _frames.GetValueOrDefault(hwnd); }
    public void Forget(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(hwnd, out var capture)) capture.Forgotten = true;
            if (_frames.Remove(hwnd, out var frame)) frame.Dispose();
        }
    }
    private void ScheduleCaptures()
    {
        IntPtr[] hwnds;
        try { hwnds = _hwndProvider?.Invoke() ?? System.Array.Empty<IntPtr>(); }
        catch { return; }
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var hwnd in _frames.Keys.Except(hwnds).ToArray()) Forget(hwnd);
        }
        // Rotate admission so a large fleet does not always capture its first two clients.
        for (int i = 0; i < hwnds.Length; i++) TryCapture(hwnds[(_cursor + i) % hwnds.Length]);
        if (hwnds.Length > 0) _cursor = (_cursor + 2) % hwnds.Length;
    }
    internal int PendingCaptures { get { lock (_gate) return _inFlight.Count; } }
    internal void TryCapture(IntPtr hwnd)
    {
        if (!_canCapture(hwnd)) return;
        Capture capture;
        lock (_gate)
        {
            if (_disposed || _inFlight.ContainsKey(hwnd) || !Workers.Wait(0)) return;
            capture = new Capture();
            _inFlight.Add(hwnd, capture);
        }
        try
        {
            // Dedicated background threads: an unreturning native call must not
            // consume the shared pool used by ESI, discovery and other services.
            new Thread(() => RunCapture(hwnd, capture)) { IsBackground = true, Name = "Preview snapshot" }.Start();
        }
        catch { lock (_gate) _inFlight.Remove(hwnd); Workers.Release(); throw; }
    }
    private void RunCapture(IntPtr hwnd, Capture capture)
    {
        var watch = Stopwatch.StartNew();
        Bitmap? bitmap = null;
        DiagnosticsService.LogDwm($"[Capture] BEGIN HWND={hwnd}");
        try
        {
            lock (_gate) if (_disposed || capture.Forgotten) return;
            if (!_canCapture(hwnd)) return;
            bitmap = _capture(hwnd);
            int width=MaximumFrameWidth;
            if(bitmap!=null && width>0 && bitmap.Width>width) {
                var small=new Bitmap(bitmap,width,Math.Max(1,(int)((double)bitmap.Height*width/bitmap.Width)));
                bitmap.Dispose();bitmap=small;
            }
            lock (_gate)
            {
                if (_disposed || capture.Forgotten || bitmap == null || !_canCapture(hwnd)) return;
                if (_frames.Remove(hwnd, out var old)) old.Dispose();
                _frames[hwnd] = new FrozenFrame(bitmap);
                bitmap = null; // ownership transferred
            }
        }
        catch (Exception ex) { DiagnosticsService.LogDwm($"[Capture] FAILED HWND={hwnd}: {ex.Message}"); }
        finally
        {
            bitmap?.Dispose();
            lock (_gate) _inFlight.Remove(hwnd);
            Workers.Release();
            DiagnosticsService.LogDwm($"[Capture] END HWND={hwnd} elapsed={watch.ElapsedMilliseconds}ms");
        }
    }
    private static Bitmap? CaptureWindow(IntPtr hwnd)
    {
        if (!User32.GetWindowRect(hwnd, out var rect)) return null;
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000) return null;
        // PrintWindow draws full resolution. Every failure path must release
        // both the full-size bitmap and HDC, exactly once.
        using var full = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(full))
        {
            var hdc = graphics.GetHdc();
            try { if (!User32.PrintWindow(hwnd, hdc, User32.PW_RENDERFULLCONTENT)) return null; }
            finally { graphics.ReleaseHdc(hdc); }
        }
        double scale = Math.Min(1, Math.Min(1280.0 / width, 720.0 / height));
        var result = new Bitmap(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(full, 0, 0, result.Width, result.Height);
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var frame in _frames.Values) frame.Dispose();
            _frames.Clear();
        }
        _captureTimer.Stop();
        if (_winEvents != null) _winEvents.WindowMinimizeStart -= OnMinimizeStart;
        // Do not wait for a game thread on shutdown. In-flight workers own their
        // bitmaps until PrintWindow returns and discard them when disposed.
    }
}
