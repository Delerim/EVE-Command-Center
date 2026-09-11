using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Views;

namespace EveCommandCenter.Services;

/// <summary>App-owned polling. Views subscribe to data, never own monitoring.</summary>
public sealed class BackgroundOperations : IDisposable
{
    private static BackgroundOperations? _current;
    public static BackgroundOperations Current => _current ??= new();
    public static void Stop() { _current?.Dispose(); _current = null; }
    public EveSsoService Sso { get; } = new();
    public MoonReportService Moons { get; }
    public ContractService Contracts { get; }
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly string _file;
    private Dictionary<string, DateTimeOffset> _moonNotified;
    private DateTimeOffset _nextMoon, _nextContracts;
    private bool _busy;
    private MoonReportWindow? _moonWindow;
    private ContractsWindow? _contractsWindow;
    public string? MoonError { get; private set; }

    private BackgroundOperations()
    {
        Moons = new MoonReportService(Sso);
        Contracts = new ContractService(Sso);
        _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "operating-alerts.json");
        try { _moonNotified = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_file)) ?? new(); }
        catch { _moonNotified = new(); }
        Moons.Refreshed += MoonRefreshed;
        Contracts.NewContracts += NewContracts;
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(async () => await PollAsync()));
    }

    private async Task PollAsync()
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        _busy = true;
        try
        {
            var pilots = await Sso.LoadPilotsAsync();
            var now = DateTimeOffset.UtcNow;
            var refreshes = new List<Task>();
            if (now >= _nextMoon)
            {
                _nextMoon = now.AddMinutes(61);
                var pilot = pilots.FirstOrDefault(p => p.CharacterId == Moons.SelectedCharacterId);
                if (pilot != null && MoonReportService.HasRequiredScopes(pilot))
                    refreshes.Add(RefreshMoonsAsync(pilot, now));
            }
            if (now >= _nextContracts)
            {
                _nextContracts = now.AddMinutes(30);
                var pilot = pilots.FirstOrDefault(p => p.CharacterId == Contracts.State.CharacterId);
                if (pilot != null && ContractService.CanRead(pilot))
                    refreshes.Add(RefreshContractsAsync(pilot, now));
            }
            await Task.WhenAll(refreshes);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Operations] " + ex.Message); }
        finally { _busy = false; }
    }

    private async Task RefreshMoonsAsync(EvePilotProfile pilot, DateTimeOffset now)
    {
        try { await Moons.RefreshAsync(pilot, null, _lifetime.Token); MoonError = null; }
        catch (Exception ex) when (ex is not OperationCanceledException) { MoonError = ex.Message; _nextMoon = now.AddMinutes(10); }
    }
    private async Task RefreshContractsAsync(EvePilotProfile pilot, DateTimeOffset now)
    {
        try { await Contracts.RefreshAsync(pilot, _lifetime.Token); }
        catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Debug.WriteLine(ex.Message); _nextContracts = now.AddMinutes(10); }
    }

    private void MoonRefreshed()
    {
        _nextMoon = DateTimeOffset.UtcNow.AddMinutes(61);
        var prefix = Moons.SelectedCharacterId + ":";
        var keys = Moons.OperatingAlerts.Select(a => prefix + a.Key).ToHashSet();
        foreach (var key in _moonNotified.Keys.Where(k => k.StartsWith(prefix) && !keys.Contains(k)).ToArray()) _moonNotified.Remove(key);
        if (Moons.DesktopNotificationsEnabled)
            foreach (var alert in Moons.OperatingAlerts)
            {
                var key = prefix + alert.Key;
                if (_moonNotified.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(6)) continue;
                _moonNotified[key] = DateTimeOffset.UtcNow;
                OperatingToast.Notify(alert.StructureName, alert.Message, () => OpenMoons(alert.StructureName));
            }
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_moonNotified)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }
    private void NewContracts(IReadOnlyList<ContractRow> rows)
    {
        foreach (var row in rows)
            OperatingToast.Notify(row.Issuer + " - " + row.Location, row.PriceText + " | Click to inspect contents",
                () => OpenContracts(row), "NEW CONTRACT");
    }
    public void OpenMoons(string? search = null)
    {
        if (_moonWindow == null)
        {
            _moonWindow = new MoonReportWindow();
            _moonWindow.Closed += (_, _) => _moonWindow = null;
        }
        _moonWindow.Show();
        if (_moonWindow.WindowState == WindowState.Minimized) _moonWindow.WindowState = WindowState.Normal;
        _moonWindow.Activate();
        if (search != null) _moonWindow.FocusStructure(search);
    }
    public void OpenContracts(ContractRow? row = null)
    {
        if (_contractsWindow == null)
        {
            _contractsWindow = new ContractsWindow();
            _contractsWindow.Closed += (_, _) => _contractsWindow = null;
        }
        _contractsWindow.Show();
        if (_contractsWindow.WindowState == WindowState.Minimized) _contractsWindow.WindowState = WindowState.Normal;
        _contractsWindow.Activate();
        if (row != null) _contractsWindow.OpenContents(row);
    }
    public void Dispose()
    {
        _timer.Stop();
        _lifetime.Cancel();
        Moons.Refreshed -= MoonRefreshed;
        Contracts.NewContracts -= NewContracts;
        OperatingToast.Clear();
        // In-flight requests observe cancellation before application shutdown.
    }
}
