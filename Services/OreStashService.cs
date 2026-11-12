using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed class OreStashState
{
    public OreWatchSettings Settings { get; set; } = new();
    public List<OreStashAdvice> Rows { get; set; } = new();
    public Dictionary<int, OreAlertMemory> Alerts { get; set; } = new();
    public DateTimeOffset? AssetsObserved { get; set; }
    public DateTimeOffset? AssetsExpire { get; set; }
    public DateTimeOffset? Checked { get; set; }
}
public sealed class OreAlertMemory
{
    public bool InSell { get; set; }
    public DateTimeOffset LastAlert { get; set; }
}

public sealed class OreStashService : IDisposable
{
    private readonly IOreStashSource _source;
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private DateTimeOffset _next;
    private int _revision;
    public OreStashState State { get; private set; } = new();
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "Choose your main character. No alts or corporation hangars are scanned.";
    public bool LastScanSucceeded { get; private set; }
    public event Action? Changed;
    public event Action<IReadOnlyList<OreStashAdvice>>? SellingPoint;
    public event Action<IReadOnlyList<OreStashAdvice>>? Observed;

    public OreStashService(IOreStashSource source, string? folder = null)
    {
        _source = source;
        _file = Path.Combine(folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center"), "ore-stash-watch.json");
        try
        {
            State = JsonSerializer.Deserialize<OreStashState>(File.ReadAllText(_file)) ?? new();
            OreStashEngine.Validate(State.Settings); State.Rows ??= new(); State.Alerts ??= new();
        }
        catch { State = new(); }
        if (State.Settings.CharacterId > 0) Status = "Saved stash is unverified until a new scan succeeds.";
    }
    public Task<IReadOnlyList<EvePilotProfile>> PilotsAsync() => _source.PilotsAsync();
    public void Configure(OreWatchSettings settings)
    {
        OreStashEngine.Validate(settings);
        bool changedPilot = settings.CharacterId != State.Settings.CharacterId;
        _revision++; State.Settings = settings; LastScanSucceeded = false;
        if (changedPilot) { State.Rows.Clear(); State.Alerts.Clear(); State.Checked = State.AssetsObserved = State.AssetsExpire = null; }
        Status = !settings.Enabled ? "Ore monitoring paused." : "Settings saved; scan required.";
        _next = default; Save(); Changed?.Invoke();
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(State)); File.Move(_file + ".tmp", _file, true);
    }
    public async Task RefreshAsync(CancellationToken ct = default, bool force = false)
    {
        if (_lifetime.IsCancellationRequested || !State.Settings.Enabled || State.Settings.CharacterId <= 0 ||
            (!force && DateTimeOffset.UtcNow < _next) || !await _gate.WaitAsync(0, ct)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        var token = linked.Token;
        int revision = _revision; var settings = State.Settings;
        Busy = true; LastScanSucceeded = false; Status = "Loading main character's personal assets (ESI cached)..."; Changed?.Invoke();
        _next = DateTimeOffset.UtcNow.AddMinutes(10);
        try
        {
            var pilot = (await _source.PilotsAsync()).SingleOrDefault(x => x.CharacterId == settings.CharacterId)
                ?? throw new InvalidOperationException("The selected main character is not linked. Choose it or reconnect in Setup.");
            if (!pilot.Scopes.Contains("esi-assets.read_assets.v1")) throw new InvalidOperationException("Reconnect the main character in Setup to grant personal asset access.");
            var snapshot = await _source.AssetsAsync(pilot, token);
            if (snapshot.CharacterId != settings.CharacterId) throw new InvalidOperationException("Inventory owner did not match your selected main character.");
            Status = "Identifying ore in personal hangars, containers and ship holds..."; Changed?.Invoke();
            var types = await _source.OreTypesAsync(snapshot.Items.Select(x => x.TypeId), token);
            var positions = OreStashEngine.Positions(snapshot, types);
            var books = new Dictionary<int, OreOrderBook>();
            int unavailable = 0;
            foreach (int id in positions.Select(x => x.MarketTypeId > 0 ? x.MarketTypeId : x.TypeId).Distinct())
            {
                try { books[id] = await _source.BookAsync(id, token); }
                catch (OperationCanceledException) { throw; }
                catch { unavailable++; }
            }
            var locations = new Dictionary<long, string>();
            foreach (long location in positions.Select(x => x.LocationId).Distinct())
            {
                if (location == snapshot.CurrentShipId) { locations[location] = "Current ship"; continue; }
                try { locations[location] = await _source.LocationAsync(location, pilot, token) ?? $"Location {location}"; }
                catch (OperationCanceledException) { throw; }
                catch { locations[location] = $"Location {location} (name unavailable)"; }
            }
            var named = positions.Select(x => x with { Place = locations.GetValueOrDefault(x.LocationId, "Unknown location") + " / " + x.Place }).ToArray();
            var now = DateTimeOffset.UtcNow;
            var rows = OreStashEngine.Evaluate(snapshot, named, books, settings, now);
            if (revision != _revision || token.IsCancellationRequested) return;
            State.Rows = rows.ToList(); State.AssetsObserved = snapshot.ObservedUtc; State.AssetsExpire = snapshot.ExpiresUtc; State.Checked = now;
            LastScanSucceeded = true;
            Status = $"{pilot.CharacterName} only | {rows.Count} ore types | {rows.Count(x => x.Advice == "SELL")} SELL, {rows.Count(x => x.Advice == "ASSESS")} ASSESS, {rows.Count(x => x.Advice == "DON'T SELL")} DON'T SELL. " +
                $"Personal assets only; cached cargo/ore/fleet holds, not live telemetry. Refresh ~10 minutes." +
                (unavailable > 0 ? $" {unavailable} price books unavailable; those rows require assessment." : "") +
                (snapshot.CurrentShipId == 0 ? " Current ship identity unavailable; in-space cargo coverage may be incomplete." : "");
            var alerts = ObserveAlerts(rows, settings, State.Alerts, now);
            Save(); Observed?.Invoke(rows);
            if (settings.Notifications && alerts.Count > 0) SellingPoint?.Invoke(alerts);
        }
        catch (OperationCanceledException) { LastScanSucceeded = false; Status = "Scan cancelled; cached stash is unverified."; }
        catch (Exception ex) { LastScanSucceeded = false; Status = "SCAN FAILED: " + ex.Message + " Cached stash is unverified; no selling alerts."; }
        finally { Busy = false; _gate.Release(); Changed?.Invoke(); }
    }
    internal static IReadOnlyList<OreStashAdvice> ObserveAlerts(IReadOnlyList<OreStashAdvice> rows, OreWatchSettings settings,
        Dictionary<int, OreAlertMemory> memory, DateTimeOffset now)
    {
        var alerts = new List<OreStashAdvice>();
        foreach (int removed in memory.Keys.Except(rows.Select(x => x.TypeId)).ToArray()) memory[removed].InSell = false;
        foreach (var row in rows)
        {
            if (!memory.TryGetValue(row.TypeId, out var old)) memory[row.TypeId] = old = new();
            // Unavailable data never rearms a good-price alert. Require a real move
            // at least two percentage points outside SELL before rearming.
            if (row.DataVerified && row.GapPercent > settings.SellGapPercent + 2) old.InSell = false;
            bool good = row.DataVerified && row.Advice == "SELL" && row.CorpPayout >= settings.MinimumAlertIsk;
            if (good && !old.InSell && settings.Notifications && now - old.LastAlert >= TimeSpan.FromHours(24))
            {
                alerts.Add(row); old.LastAlert = now;
                old.InSell = true;
            }
        }
        return alerts;
    }
    public void Dispose() { _lifetime.Cancel(); if (_source is IDisposable disposable) disposable.Dispose(); }
}
