using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using EveCommandCenter.Interop;
using Screen = System.Windows.Forms.Screen;

namespace EveCommandCenter.Services;

public sealed record SavedWindowLayout
{
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int WorkspaceLeft { get; init; }
    public int WorkspaceTop { get; init; }
    public string Monitor { get; init; } = "";
    public double Dpi { get; init; } = 96;
    public bool Maximized { get; init; }
    public bool Valid => Width is > 0 and < 50000 && Height is > 0 and < 50000
        && Math.Abs((long)Left) < 1000000 && Math.Abs((long)Top) < 1000000
        && Math.Abs((long)WorkspaceLeft) < 1000000 && Math.Abs((long)WorkspaceTop) < 1000000
        && double.IsFinite(Dpi) && Dpi is >= 48 and <= 768;
}

/// <summary>One saved native placement per panel type, independent of changing titles.
/// WINDOWPLACEMENT coordinates stay in workspace units throughout save and restore.</summary>
public sealed class WindowLayoutService : IDisposable
{
    public static WindowLayoutService Current { get; } = new();
    private static bool _installed;
    private readonly string _path;
    private readonly Dictionary<string, SavedWindowLayout> _saved;
    private readonly Dictionary<Window, Action> _attached = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private bool _dirty, _disposed;
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "MiningFleetOverviewWindow", "ThumbnailWindow", "TextOverlayWindow", "StatOverlayWindow",
        "CropWindow", "CropPickerOverlay", "OperatingToast", "QuickSwitchWheel", "BroadcastHudWindow"
    };
    public WindowLayoutService(string? directory = null)
    {
        _path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center"), "window-layouts.json");
        try
        {
            var read = JsonSerializer.Deserialize<Dictionary<string, SavedWindowLayout>>(File.ReadAllText(_path));
            _saved = (read ?? new()).Where(p => p.Value != null && p.Value.Valid).Take(128).ToDictionary(p => p.Key, p => p.Value);
        }
        catch { _saved = new(); }
        _saveTimer.Tick += (_, _) => Flush();
    }
    public static bool ShouldRemember(Type type) => type != typeof(Window) && typeof(Window).IsAssignableFrom(type)
        && type.Namespace == "EveCommandCenter.Views" && !Excluded.Contains(type.Name);
    public static void Install() => Install(Current);
    internal static void Install(WindowLayoutService service)
    {
        if (_installed) return;
        _installed = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
        {
            if (sender is Window window && ReferenceEquals(args.OriginalSource, window) && ShouldRemember(window.GetType()))
                service.Attach(window, window.GetType().FullName!);
        }));
    }
    public SavedWindowLayout? Get(string key) => _saved.GetValueOrDefault(key);
    public void Attach(Window window, string key)
    {
        if (_disposed || _attached.ContainsKey(window)) return;
        bool ready = false;
        void Save()
        {
            if (!ready || _disposed) return;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var placement = Placement();
            if (!User32.GetWindowPlacement(hwnd, ref placement)) return;
            var screen = Screen.FromHandle(hwnd);
            var area = Workspace(screen, hwnd);
            var rect = placement.rcNormalPosition;
            bool maximized = placement.showCmd == 3 || (window.WindowState == WindowState.Minimized && (placement.flags & 2) != 0);
            var value = new SavedWindowLayout { Left = rect.Left, Top = rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top,
                Monitor = screen.DeviceName, WorkspaceLeft = area.Left, WorkspaceTop = area.Top, Dpi = MonitorDpi(screen), Maximized = maximized };
            if (!value.Valid || (_saved.TryGetValue(key, out var old) && old == value)) return;
            _saved[key] = value;
            _dirty = true;
            _saveTimer.Stop(); _saveTimer.Start();
        }
        void Restore()
        {
            if (ready) return;
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            if (_saved.TryGetValue(key, out var saved))
            {
                var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == saved.Monitor) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
                var area = Workspace(screen, hwnd);
                double dpi = MonitorDpi(screen);
                // A smaller replacement monitor must still expose the title bar and resize handles.
                window.MinWidth = Math.Min(window.MinWidth, area.Width * 96 / dpi);
                window.MinHeight = Math.Min(window.MinHeight, area.Height * 96 / dpi);
                var bounds = Fit(saved, area, dpi, window.MinWidth, window.MinHeight);
                var placement = Placement();
                placement.rcNormalPosition = new DwmApi.RECT { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom };
                // Honour the app's explicit start-minimised setting, but never persist minimisation.
                placement.showCmd = window.WindowState == WindowState.Minimized ? 7u : saved.Maximized ? 3u : 1u;
                placement.flags = window.WindowState == WindowState.Minimized && saved.Maximized ? 2u : 0u;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                User32.SetWindowPlacement(hwnd, ref placement);
            }
            ready = true;
        }
        EventHandler source = (_, _) => Restore();
        RoutedEventHandler loaded = (_, _) => { Restore(); Save(); };
        EventHandler changed = (_, _) => Save();
        SizeChangedEventHandler resized = (_, _) => Save();
        EventHandler? closed = null;
        Action detach = () =>
        {
            window.SourceInitialized -= source; window.Loaded -= loaded;
            window.LocationChanged -= changed; window.StateChanged -= changed; window.SizeChanged -= resized;
            window.Closed -= closed;
        };
        closed = (_, _) => { Save(); Flush(); detach(); _attached.Remove(window); };
        _attached.Add(window, detach);
        window.SourceInitialized += source; window.Loaded += loaded;
        window.LocationChanged += changed; window.StateChanged += changed; window.SizeChanged += resized; window.Closed += closed;
        if (_saved.ContainsKey(key)) window.WindowStartupLocation = WindowStartupLocation.Manual;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) { Restore(); Save(); }
    }
    internal static System.Drawing.Rectangle Fit(SavedWindowLayout saved, System.Drawing.Rectangle area, double dpi, double minWidth, double minHeight)
    {
        double scale = dpi / saved.Dpi;
        int width = Math.Min(area.Width, Math.Max((int)Math.Ceiling(minWidth * dpi / 96), (int)Math.Round(saved.Width * scale)));
        int height = Math.Min(area.Height, Math.Max((int)Math.Ceiling(minHeight * dpi / 96), (int)Math.Round(saved.Height * scale)));
        int left = Math.Clamp(area.Left + (int)Math.Round((saved.Left - saved.WorkspaceLeft) * scale), area.Left, area.Right - width);
        int top = Math.Clamp(area.Top + (int)Math.Round((saved.Top - saved.WorkspaceTop) * scale), area.Top, area.Bottom - height);
        return new(left, top, width, height);
    }
    private static System.Drawing.Rectangle Workspace(Screen screen, IntPtr hwnd)
    {
        var area = screen.WorkingArea;
        if ((User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE) & User32.WS_EX_TOOLWINDOW) == 0)
            area.Location = screen.Bounds.Location;
        return area;
    }
    private static User32.WINDOWPLACEMENT Placement() => new() { length = (uint)Marshal.SizeOf<User32.WINDOWPLACEMENT>() };
    private static double MonitorDpi(Screen screen)
    {
        try
        {
            var point = new User32.POINT { X = screen.Bounds.Left + screen.Bounds.Width / 2, Y = screen.Bounds.Top + screen.Bounds.Height / 2 };
            if (GetDpiForMonitor(MonitorFromPoint(point, 2), 0, out uint x, out _) == 0 && x > 0) return x;
        }
        catch { }
        return 96;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(User32.POINT point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, uint type, out uint x, out uint y);
    public void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_saved));
            File.Move(_path + ".tmp", _path, true);
            _dirty = false;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Window layout] " + ex.Message); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        Flush(); _disposed = true;
        foreach (var detach in _attached.Values) detach();
        _attached.Clear();
    }
}
