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
    public CorporationAccessService Access { get; }
    private DateTimeOffset _nextAccess;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly string _file;
    private Dictionary<string, DateTimeOffset> _moonNotified;
    private DateTimeOffset _nextMoon, _nextContracts;
    private bool _busy;
    private bool _moonBusy;
    private MoonReportWindow? _moonWindow;
    private ContractsWindow? _contractsWindow;
    public string? MoonError { get; private set; }
    public void ScheduleRefresh() { _nextMoon = _nextContracts = default; }

    private BackgroundOperations()
    {
        Moons = new MoonReportService(Sso);
        Contracts = new ContractService(Sso);
        Access = new CorporationAccessService(Sso);
        if (!Access.State.SetupCompleted)
        {
            Access.State.MoonCharacterId = Moons.SelectedCharacterId;
            Access.State.ContractCharacterId = Contracts.State.CharacterId;
        }
        Access.Changed += () =>
        {
            if (!Access.CanReadMoons) _moonWindow?.Close();
            if (!Access.CanReadContracts) _contractsWindow?.Close();
        };
        _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "operating-alerts.json");
        try { _moonNotified = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_file)) ?? new(); }
        catch { _moonNotified = new(); }
        Moons.Refreshed += MoonRefreshed;
        Contracts.NewContracts += NewContracts;
        Contracts.AcceptedContracts += AcceptedContracts;
        _timer.Tick += async (_, _) => { CheckMoonEvents(); await PollAsync(); };
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
            if (now >= _nextAccess)
            {
                _nextAccess = now.AddMinutes(10);
                await Access.ValidateAsync(pilots, _lifetime.Token);
            }
            var refreshes = new List<Task>();
            if (now >= _nextMoon && !_moonBusy)
            {
                _nextMoon = now.AddMinutes(61);
                var pilot = pilots.FirstOrDefault(p => p.CharacterId == Moons.SelectedCharacterId);
                if (pilot != null && Access.CanReadMoons && pilot.CharacterId == Access.State.MoonCharacterId)
                    _ = RefreshMoonsAsync(pilot, now);
            }
            if (now >= _nextContracts)
            {
                _nextContracts = now.AddMinutes(1);
                var pilot = pilots.FirstOrDefault(p => p.CharacterId == Contracts.State.CharacterId);
                if (pilot != null && Access.CanReadContracts && pilot.CharacterId == Access.State.ContractCharacterId)
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
        _moonBusy = true;
        try { await Moons.RefreshAsync(pilot, null, _lifetime.Token); MoonError = null; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OperationCanceledException) { MoonError = ex.Message; _nextMoon = now.AddMinutes(10); }
        finally { _moonBusy = false; }
    }
    private async Task RefreshContractsAsync(EvePilotProfile pilot, DateTimeOffset now)
    {
        try { await Contracts.RefreshAsync(pilot, _lifetime.Token); _nextContracts = Contracts.NextCheckUtc > now.AddMinutes(1) ? Contracts.NextCheckUtc : now.AddMinutes(1); }
        catch (Exception ex) when (ex is not OperationCanceledException) { System.Diagnostics.Debug.WriteLine(ex.Message); _nextContracts = Contracts.NextCheckUtc > now.AddMinutes(10) ? Contracts.NextCheckUtc : now.AddMinutes(10); }
    }

    private void MoonRefreshed()
    {
        if (!Access.CanReadMoons) return;
        CheckMoonEvents();
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
        if (!Access.CanReadContracts) return;
        foreach (var row in rows)
            OperatingToast.Notify(row.Issuer + " - " + row.Location, row.PriceText + " | Click to inspect contents",
                () => OpenContracts(row), "NEW CONTRACT");
    }
    private void AcceptedContracts(IReadOnlyList<ContractRow> rows)
    {
        if (!Access.CanReadContracts) return;
        foreach (var row in rows)
            OperatingToast.Notify(row.Issuer + " | " + row.PriceText, "Accepted by " + row.Acceptor + " | " + row.AcceptedText,
                () => OpenContracts(row), "CONTRACT ACCEPTED");
    }
    private void PersistAlerts()
    {
        try { File.WriteAllText(_file, JsonSerializer.Serialize(_moonNotified)); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
    }
    private void CheckMoonEvents()
    {
        if (!Access.CanReadMoons) return;
        var events = MoonMilestones.Observe(Moons.GetSnapshot(), Moons.SelectedCharacterId, _moonNotified, DateTimeOffset.UtcNow);
        PersistAlerts();
        if (Moons.DesktopNotificationsEnabled)
            foreach (var item in events)
                OperatingToast.Notify(item.Structure, item.Message, () => OpenMoons(item.Structure), item.Title);
    }
    public void ReportGlistening(string pilot, string ore, DateTime timestamp)
    {
        if (!ore.Contains("Glistening", StringComparison.OrdinalIgnoreCase) || DateTime.UtcNow - timestamp > TimeSpan.FromMinutes(2)) return;
        var key = "live-glistening:" + ore;
        if (_moonNotified.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(6)) return;
        _moonNotified[key] = DateTimeOffset.UtcNow;
        PersistAlerts();
        if (Moons.DesktopNotificationsEnabled)
            OperatingToast.Notify(pilot + " | " + ore, "Glistening ore detected in a live mining log. The corporation ledger will identify the moon when available.",
                () => { if (Access.CanReadMoons) OpenMoons(); }, "GLISTENING ORE DETECTED");
    }
    public void OpenMoons(string? search = null)
    {
        if (!Access.CanReadMoons) { new ClientSetupWindow().ShowDialog(); return; }
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
        if (!Access.CanReadContracts) { new ClientSetupWindow().ShowDialog(); return; }
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
        Contracts.AcceptedContracts -= AcceptedContracts;
        OperatingToast.Clear();
        // In-flight requests observe cancellation before application shutdown.
    }
}
