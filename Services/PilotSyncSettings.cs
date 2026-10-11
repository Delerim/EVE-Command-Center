using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public sealed class PilotSyncPreference
{
    public long PilotId { get; set; }
    public bool Wallet { get; set; } = true;
    public bool Market { get; set; }
    public bool Industry { get; set; } = true;
    public bool Planetary { get; set; } = true;
    public bool Pilot { get; set; } = true;
    public bool Clones { get; set; } = true;
    public bool ShowMarket { get; set; }
    public bool ShowWallet { get; set; } = true;
    public bool ShowIndustry { get; set; } = true;
}
public sealed class PilotSyncSettings
{
    private readonly string _path;
    private Dictionary<long, PilotSyncPreference> _rows = new();
    public string Error { get; private set; } = "";
    public Dictionary<long, string> Activity { get; } = new();
    public PilotSyncSettings(string? directory = null)
    {
        _path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center"), "pilot-sync.json");
        try
        {
            if (File.Exists(_path))
            {
                var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? throw new InvalidDataException("Empty sync settings");
                if (state.Version != 1 || state.Pilots.Any(p => p.PilotId <= 0) || state.Pilots.Select(p => p.PilotId).Distinct().Count() != state.Pilots.Count) throw new InvalidDataException("Unsupported sync settings");
                _rows = state.Pilots.ToDictionary(p => p.PilotId);
            }
        }
        catch (Exception ex) { Error = "Sync settings could not be loaded; automatic sync is paused. " + ex.GetType().Name; }
    }
    private sealed class State { public int Version { get; set; } = 1; public List<PilotSyncPreference> Pilots { get; set; } = new(); }
    public PilotSyncPreference Get(long id) => _rows.TryGetValue(id, out var row) ? JsonSerializer.Deserialize<PilotSyncPreference>(JsonSerializer.Serialize(row))! : new() { PilotId = id };
    public bool Enabled(long id, string feature) => Error.Length == 0 && _rows.TryGetValue(id, out var p) && feature switch
    { "wallet" => p.Wallet, "market" => p.Market, "industry" => p.Industry, "pi" => p.Planetary, "pilot" => p.Pilot, "clones" => p.Clones, _ => false };
    public void EnsurePilots(IEnumerable<EvePilotProfile> pilots, IReadOnlySet<long> previousMarket)
    {
        if (Error.Length > 0) return;
        var rows = _rows.Values.ToList();
        foreach (var pilot in pilots.Where(p => !_rows.ContainsKey(p.CharacterId)))
        { bool market = previousMarket.Contains(pilot.CharacterId); rows.Add(new() { PilotId = pilot.CharacterId, Market = market, ShowMarket = market }); }
        if (rows.Count != _rows.Count) Save(rows);
    }
    public void Save(IEnumerable<PilotSyncPreference> rows)
    {
        if (Error.Length > 0) throw new InvalidOperationException(Error);
        var next = _rows.ToDictionary(p => p.Key, p => p.Value);
        foreach (var row in rows)
        { if (row.PilotId <= 0) throw new InvalidOperationException("Choose a linked pilot."); next[row.PilotId] = JsonSerializer.Deserialize<PilotSyncPreference>(JsonSerializer.Serialize(row))!; }
        string json = JsonSerializer.Serialize(new State { Pilots = next.Values.ToList() });
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path + ".tmp", json); File.Move(_path + ".tmp", _path, true); _rows = next;
    }
}
