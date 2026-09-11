using System.Text.Json;
using System.Text.Json.Serialization;

namespace EveCommandCenter.Models;

public sealed class PiType
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double Volume { get; set; }
    public double Capacity { get; set; }
    public int Group { get; set; }
    public bool Commodity { get; set; }
    public string Icon => $"https://images.evetech.net/types/{Id}/icon?size=32";
}
public sealed class PiRecipe
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Cycle { get; set; }
    public Dictionary<int, double> Inputs { get; set; } = new();
    public Dictionary<int, double> Outputs { get; set; } = new();
}
public sealed class PiColony
{
    public long CharacterId { get; set; }
    public string Character { get; set; } = "";
    public long PlanetId { get; set; }
    public string Planet { get; set; } = "";
    public int? UpgradeLevel { get; set; }
    public string PlanetType { get; set; } = "";
    public DateTimeOffset LastUpdate { get; set; }
    public DateTimeOffset Fetched { get; set; }
    public JsonElement Layout { get; set; }
    public string Error { get; set; } = "";
    public string Portrait => $"https://images.evetech.net/characters/{CharacterId}/portrait?size=32";
}
public sealed class PiContainer
{
    public long Id { get; set; }
    public long Station { get; set; }
    public string Name { get; set; } = "";
    public string Label => $"{Name} | location {Station} | {Id}";
}
public sealed class PiState
{
    public Dictionary<int, PiQuote> Prices { get; set; } = new();
    public long StockCharacterId { get; set; }
    public long ContainerId { get; set; }
    public DateTimeOffset NextRefresh { get; set; }
    public DateTimeOffset StockFetched { get; set; }
    public string StockError { get; set; } = "";
    public List<PiColony> Colonies { get; set; } = new();
    public List<PiContainer> Containers { get; set; } = new();
    public List<EveAssetItem> Assets { get; set; } = new();
    public Dictionary<long, string> PilotStatus { get; set; } = new();
}
public sealed class PiQuote
{
    public double? Buy { get; set; }
    public DateTimeOffset Checked { get; set; }
}
public sealed class PiRow
{
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Status { get; set; } = "";
    public string Color { get; set; } = "#74D6C9";
    public string Icon { get; set; } = "";
    public string Quantity { get; set; } = "";
    public string Rate { get; set; } = "";
    public string Next { get; set; } = "";
    public string Remaining { get; set; } = "";
    public PiColony? Colony { get; set; }
}
public sealed class PiRefill
{
    public long CharacterId { get; set; }
    public long PlanetId { get; set; }
    public string Colony { get; set; } = "";
    public long Pin { get; set; }
    public int TypeId { get; set; }
    public string Name { get; set; } = "";
    public double Current { get; set; }
    public double Target { get; set; }
    public double Need => Math.Max(0, Target - Current);
    public double Allocated { get; set; }
    public double Missing => Math.Max(0, Need - Allocated);
    public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=32";
}
public sealed class PiAnalysis
{
    public List<PiRow> Colonies { get; set; } = new();
    public List<PiRow> Pins { get; set; } = new();
    public List<PiRow> Production { get; set; } = new();
    public List<PiRow> Stock { get; set; } = new();
    public List<PiRefill> Refills { get; set; } = new();
}
