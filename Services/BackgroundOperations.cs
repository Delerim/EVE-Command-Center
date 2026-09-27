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
    public PlanetaryService Planetary { get; }
    public IndustryService Industry { get; }
    public OmegaService Omega { get; }
    public BackgroundPilotRefresh Pilots { get; }
    private DateTimeOffset _nextAccess;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly string _file;
    private Dictionary<string, DateTimeOffset> _moonNotified;
    private DateTimeOffset _nextMoon, _nextContracts;
    private bool _busy;
    private bool _moonBusy;
    private bool _contractBusy;
    private bool _accessBusy;
    public string? MoonError { get; private set; }
    public void ScheduleRefresh() { _nextMoon = _nextContracts = default; }

    private BackgroundOperations()
    {
        Moons = new MoonReportService(Sso);
        Contracts = new ContractService(Sso);
        Access = new CorporationAccessService(Sso);
        Planetary = new PlanetaryService(Sso);
        Industry = new IndustryService(Sso);
        Omega = new OmegaService(Sso);
        Omega.Alert += (title,message) => OperatingToast.Notify(title,message,OpenOmega,"OMEGA RENEWAL");
        Industry.Alert += (title,message) => OperatingToast.Notify(title,message,OpenIndustry,"INDUSTRY READY");
        Pilots = new BackgroundPilotRefresh(Sso);
        if (!Access.State.SetupCompleted)
        {
            Access.State.MoonCharacterId = Moons.SelectedCharacterId;
            Access.State.ContractCharacterId = Contracts.State.CharacterId;
        }
        _file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "operating-alerts.json");
        try { _moonNotified = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(_file)) ?? new(); }
        catch { _moonNotified = new(); }
        Moons.Refreshed += MoonRefreshed;
        Contracts.NewContracts += NewContracts;
        Contracts.AcceptedContracts += AcceptedContracts;
        _timer.Tick += async (_, _) => { CheckMoonEvents(); await PollAsync(); };
        EveSsoService.CharacterLinked += CharacterLinked;
        _timer.Start();
        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(async () => await PollAsync()));
    }

    private void CharacterLinked(long characterId)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_lifetime.IsCancellationRequested) return;
            _nextAccess = _nextMoon = _nextContracts = default;
            Industry.Due(); Omega.Due(); Planetary.State.NextRefresh = default;
            // The existing paced poll picks these up; no parallel SSO refresh burst.
        }));
    }
    private async Task PollAsync()
    {
        if (_busy || _lifetime.IsCancellationRequested) return;
        _busy = true;
        try
        {
            if (!Planetary.Busy)
            {
                try { NotificationCenterService.Current.SyncPi(Planetary.State,DateTimeOffset.UtcNow); } catch(Exception ex){EsiDiagnostics.Write("PI notification sync: "+ex.GetType().Name);}
                var alerts=PlanetaryAlerts.Observe(Planetary.State,DateTimeOffset.UtcNow);
                if(Planetary.State.DesktopAlerts) foreach(var group in alerts.GroupBy(a=>a.Split('|')[0].Trim())) OperatingToast.Notify(group.Key+" | PI",string.Join("\n",group.Take(5)),OpenPlanetary,"PLANETARY INDUSTRY");
                Planetary.Save();
            }
            Omega.CheckAlerts();
            _ = Omega.RefreshMarketAsync(_lifetime.Token);
            _ = Omega.RefreshAsync(_lifetime.Token);
            _ = Industry.RefreshAsync(_lifetime.Token);
            _ = Planetary.RefreshAsync(_lifetime.Token);
            _ = Pilots.RefreshAsync(_lifetime.Token);
            var pilots = await Sso.LoadPilotsAsync();
            var now = DateTimeOffset.UtcNow;
            if (now >= _nextAccess && !_accessBusy)
            {
                _nextAccess = now.AddMinutes(10);
                _ = RefreshAccessAsync(pilots);
            }

            if (now >= _nextMoon && !_moonBusy)
            {
                _nextMoon = now.AddMinutes(1);
                var pilot = pilots.FirstOrDefault(p => p.CharacterId == Moons.SelectedCharacterId);
                if (pilot != null && Access.CanReadMoons && pilot.CharacterId == Access.State.MoonCharacterId)
                    _ = RefreshMoonsAsync(pilot, now);
            }
            if (now >= _nextContracts && !_contractBusy)
            {
                _nextContracts = now.AddMinutes(5);
                var pilot = pilots.FirstOrDefault(p => p.CharacterId == Contracts.State.CharacterId);
                if (pilot != null && Access.CanReadContracts && pilot.CharacterId == Access.State.ContractCharacterId)
                    _ = RefreshContractsAsync(pilot, now);
            }

        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Operations] " + ex.Message); }
        finally { _busy = false; }
    }

    private async Task RefreshAccessAsync(IReadOnlyList<EvePilotProfile> pilots)
    {
        _accessBusy = true;
        try { bool moons = Access.CanReadMoons, contracts = Access.CanReadContracts; await Access.ValidateAsync(pilots, _lifetime.Token); if (moons != Access.CanReadMoons || contracts != Access.CanReadContracts) ScheduleRefresh(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EsiDiagnostics.Write("Access refresh failed: " + ex.GetType().Name); }
        finally { _accessBusy = false; }
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
        _contractBusy = true;
        try { await Contracts.RefreshAsync(pilot, _lifetime.Token); _nextContracts = Contracts.NextCheckUtc > now.AddMinutes(5) ? Contracts.NextCheckUtc : now.AddMinutes(5); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); _nextContracts = Contracts.NextCheckUtc > now.AddMinutes(5) ? Contracts.NextCheckUtc : now.AddMinutes(5); }
        finally { _contractBusy = false; }
    }

    private void MoonRefreshed()
    {
        if (!Access.CanReadMoons) return;
        CheckMoonEvents();
        _nextMoon = Moons.NextRefreshUtc;
        var prefix = Moons.SelectedCharacterId + ":";
        var keys = Moons.OperatingAlerts.Select(a => prefix + a.Key).ToHashSet();
        foreach (var key in _moonNotified.Keys.Where(k => k.StartsWith(prefix) && !keys.Contains(k)).ToArray()) _moonNotified.Remove(key);
        if (Moons.DesktopNotificationsEnabled)
            foreach (var alert in Moons.OperatingAlerts)
            {
                var key = prefix + alert.Key;
                bool fuel = alert.Key == "fuel:all";
                if (_moonNotified.TryGetValue(key, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromHours(fuel ? 24 : 6)) continue;
                _moonNotified[key] = DateTimeOffset.UtcNow;
                OperatingToast.Notify(alert.StructureName, alert.Message, () => { if (fuel) OpenFuel(); else OpenMoons(alert.StructureName); }, fuel ? "STATION FUEL" : "MOON ALERT");
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
    public void OpenNotifications()
    {
        try { NotificationCenterService.Current.SyncPi(Planetary.State,DateTimeOffset.UtcNow); } catch(Exception ex){EsiDiagnostics.Write("PI notification sync: "+ex.GetType().Name);}
        OpenModule("notifications");
    }
    private static Window? OpenModule(string key) => (System.Windows.Application.Current as App)?.OpenCommandCenterModule(key);
    public void OpenOmega() => OpenModule("omega");
    public void OpenIndustry() => OpenModule("industry");
    public void OpenPlanetary() => OpenModule("pi");
    public void OpenMoons(string? search = null)
    {
        if (OpenModule("moons") is MoonReportWindow window && search != null) window.FocusStructure(search);
    }
    public void OpenFuel()
    {
        if (OpenModule("moons") is MoonReportWindow window) window.FocusFuel();
    }
    public void OpenContracts(ContractRow? row = null)
    {
        if (OpenModule("contracts") is ContractsWindow window && row != null) window.OpenContents(row);
    }
    public void Dispose()
    {
        _timer.Stop();
        EveSsoService.CharacterLinked -= CharacterLinked;
        _lifetime.Cancel();
        Moons.Refreshed -= MoonRefreshed;
        Contracts.NewContracts -= NewContracts;
        Contracts.AcceptedContracts -= AcceptedContracts;
        OperatingToast.Clear();
        // In-flight requests observe cancellation before application shutdown.
    }
}
