using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>App-owned pilot snapshots, independent of open windows.</summary>
public sealed class BackgroundPilotRefresh
{
    public Dictionary<long, EvePilotSummary> Summaries { get; } = new();
    public Dictionary<long, EveMiningShipIntel> Intel { get; } = new();
    private readonly Dictionary<long, DateTimeOffset> _due = new();
    private bool _busy;
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
        if (_busy || lifetime.IsCancellationRequested) return;
        _busy = true;
        try
        {
            var pilots = await sso.LoadPilotsAsync();
            foreach (var pilot in pilots.OrderBy(p => _due.GetValueOrDefault(p.CharacterId)))
            {
                lifetime.ThrowIfCancellationRequested();
                if (_due.GetValueOrDefault(pilot.CharacterId) > DateTimeOffset.UtcNow) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                try
                {
                    Summaries[pilot.CharacterId] = await sso.GetSummaryAsync(pilot, timeout.Token);
                    Changed?.Invoke();
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception ex) { EsiDiagnostics.Write($"Pilot {pilot.CharacterId} summary deferred: {ex.GetType().Name}"); }
                try
                {
                    var intel = await sso.GetMiningShipIntelAsync(pilot, timeout.Token);
                    if (!Intel.TryGetValue(pilot.CharacterId, out var old) || old.CurrentShip.ShipItemId != intel.CurrentShip.ShipItemId || intel.Defense.Available)
                        Intel[pilot.CharacterId] = intel;
                    Changed?.Invoke();
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
                catch (Exception ex) { EsiDiagnostics.Write($"Pilot {pilot.CharacterId} fit deferred: {ex.GetType().Name}"); }
                _due[pilot.CharacterId] = DateTimeOffset.UtcNow.AddMinutes(1);
            }
            foreach (var id in Summaries.Keys.Where(id => !pilots.Any(p => p.CharacterId == id)).ToArray()) Summaries.Remove(id);
            foreach (var id in Intel.Keys.Where(id => !pilots.Any(p => p.CharacterId == id)).ToArray()) Intel.Remove(id);
            Directory.CreateDirectory(_directory);
            // Each file has one background writer. Keep successful summaries across refresh failures.
            await SaveCacheAsync("background-pilot-summaries.json", JsonSerializer.Serialize(Summaries));
            await SaveCacheAsync("background-pilot-intel.json", JsonSerializer.Serialize(Intel.Values));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { EsiDiagnostics.Write("Pilot refresh deferred: " + ex.GetType().Name); }
        finally { _busy = false; }
    }
    private async Task SaveCacheAsync(string name,string json)
    {
        string path=Path.Combine(_directory,name);
        await File.WriteAllTextAsync(path+".tmp",json);
        File.Move(path+".tmp",path,true);
    }

}
