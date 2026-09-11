using System.Diagnostics;
using System.Windows.Threading;

namespace EveCommandCenter.Services;

/// <summary>App-owned release checks, independent of visible windows and ESI polling.</summary>
public sealed class ReleaseMonitor : IDisposable
{
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _allowPreRelease;
    private readonly Func<UpdateService, bool> _show;
    private readonly Func<bool, CancellationToken, Task<UpdateService?>> _check;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly HashSet<string> _prompted = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy, _disposed;

    public ReleaseMonitor(Func<bool> enabled, Func<bool> allowPreRelease, Func<UpdateService, bool> show,
        Func<bool, CancellationToken, Task<UpdateService?>>? check = null)
    {
        _enabled = enabled; _allowPreRelease = allowPreRelease; _show = show;
        _check = check ?? FetchAsync;
        _timer.Tick += OnTick;
    }

    public void Start() { if (_disposed) return; _timer.Start(); _ = CheckAsync(); }
    private async void OnTick(object? sender, EventArgs e) => await CheckAsync();
    private static async Task<UpdateService?> FetchAsync(bool preview, CancellationToken token)
    {
        var service = new UpdateService();
        return await service.CheckForUpdateAsync(preview, token) ? service : null;
    }

    public async Task CheckAsync()
    {
        if (_disposed || _busy || !_enabled()) return;
        _busy = true;
        try
        {
            var release = await _check(_allowPreRelease(), _lifetime.Token);
            if (_disposed || !_enabled() || release?.UpdateAvailable != true || release.LatestVersion is not { } version || _prompted.Contains(version)) return;
            // If another update dialog is open, defer instead of piling up windows.
            if (_show(release)) _prompted.Add(version);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex) { Debug.WriteLine("[Release monitor] " + ex.Message); }
        finally { _busy = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Tick -= OnTick;
        _lifetime.Cancel(); _lifetime.Dispose();
    }
}
