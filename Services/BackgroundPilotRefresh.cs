using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>App-owned pilot snapshots, independent of open windows.</summary>
public sealed class BackgroundPilotRefresh
{
    public Func<long, bool> ShouldSync { get; set; } = _ => true;

    public ConcurrentDictionary<long, EvePilotSummary> Summaries { get; } = new();
    public ConcurrentDictionary<long, EveMiningShipIntel> Intel { get; } = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _due = new();
    private int _busy;
    public event Action? Changed;
    private readonly string _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "PilotData");
    private readonly EveSsoService sso;
    public BackgroundPilotRefresh(EveSsoService source)
    {
        sso = source;
        try { foreach (var pair in JsonSerializer.Deserialize<Dictionary<long, EvePilotSummary>>(File.ReadAllText(Path.Combine(_directory, "background-pilot-summaries.json"))) ?? new()) Summaries[pair.Key] = pair.Value; } catch { }
        try { foreach (var item in JsonSerializer.Deserialize<List<EveMiningShipIntel>>(File.ReadAllText(Path.Combine(_directory, "background-pilot-intel.json"))) ?? new()) Intel[item.CharacterId] = item; } catch { }
    }
    public async Task RefreshAsync(CancellationToken lifetime)
    {
        if (lifetime.IsCancellationRequested || Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            var pilots = await sso.LoadPilotsAsync();
            using var workers = new SemaphoreSlim(3, 3);
            var duePilots = pilots.Where(p => ShouldSync(p.CharacterId) && _due.GetValueOrDefault(p.CharacterId) <= DateTimeOffset.UtcNow).OrderBy(p => _due.GetValueOrDefault(p.CharacterId)).ToArray();
            await Task.WhenAll(duePilots.Select(async pilot =>
            {
                await workers.WaitAsync(lifetime);
                try
                {
                    lifetime.ThrowIfCancellationRequested();

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    timeout.CancelAfter(TimeSpan.FromMinutes(3));
                    try
                    {
                        Summaries[pilot.CharacterId] = await sso.GetSummaryAsync(pilot, timeout.Token);
                        Changed?.Invoke();
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                    catch (Exception ex) { EsiDiagnostics.Write($"Pilot {pilot.CharacterId} summary deferred: {ex.GetType().Name}"); }
                    using var fitTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                    fitTimeout.CancelAfter(TimeSpan.FromMinutes(3));
                    try
                    {
                        var intel = await sso.GetMiningShipIntelAsync(pilot, fitTimeout.Token);
                        Intel.TryGetValue(pilot.CharacterId, out var old);
                        Intel[pilot.CharacterId] = MergeSnapshot(old, intel);
                        Changed?.Invoke();
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                    catch (Exception ex) { EsiDiagnostics.Write($"Pilot {pilot.CharacterId} fit deferred: {ex.GetType().Name}"); }
                    _due[pilot.CharacterId] = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                finally { workers.Release(); }
            }));
            foreach (var id in Summaries.Keys.Where(id => !pilots.Any(p => p.CharacterId == id)).ToArray()) Summaries.TryRemove(id, out _);
            foreach (var id in Intel.Keys.Where(id => !pilots.Any(p => p.CharacterId == id)).ToArray()) Intel.TryRemove(id, out _);
            Directory.CreateDirectory(_directory);
            // Each file has one background writer. Keep successful summaries across refresh failures.
            await SaveCacheAsync("background-pilot-summaries.json", JsonSerializer.Serialize(Summaries));
            await SaveCacheAsync("background-pilot-intel.json", JsonSerializer.Serialize(Intel.Values));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EsiDiagnostics.Write("Pilot refresh deferred: " + ex.GetType().Name); }
        finally { Volatile.Write(ref _busy, 0); }
    }
    internal static EveMiningShipIntel MergeSnapshot(EveMiningShipIntel? previous, EveMiningShipIntel current)
    {
        if (previous?.CurrentShip.ShipItemId == current.CurrentShip.ShipItemId && !current.Defense.Available)
            current.Defense = previous.Defense;
        if (previous?.CurrentShip.ShipItemId == current.CurrentShip.ShipItemId)
        {
            if (previous.MiningHoldInventory.ObservedUtc > current.MiningHoldInventory.ObservedUtc)
                current.MiningHoldInventory = previous.MiningHoldInventory;
            if (previous.FleetHangar.ObservedUtc > current.FleetHangar.ObservedUtc)
                current.FleetHangar = previous.FleetHangar;
        }
        return current;
    }

    private async Task SaveCacheAsync(string name, string json)
    {
        string path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
    }

}
