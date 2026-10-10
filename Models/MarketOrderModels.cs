using System.Text.Json.Serialization;

namespace EveCommandCenter.Models;

public class MarketOrder
{
    [JsonPropertyName("order_id"), JsonRequired] public long OrderId { get; set; }
    [JsonPropertyName("type_id"), JsonRequired] public int TypeId { get; set; }
    [JsonPropertyName("location_id"), JsonRequired] public long LocationId { get; set; }
    [JsonPropertyName("region_id"), JsonRequired] public long RegionId { get; set; }
    [JsonPropertyName("price"), JsonRequired] public decimal Price { get; set; }
    [JsonPropertyName("volume_total"), JsonRequired] public long Total { get; set; }
    [JsonPropertyName("volume_remain"), JsonRequired] public long Remaining { get; set; }
    [JsonPropertyName("issued"), JsonRequired] public DateTimeOffset Issued { get; set; }
    [JsonPropertyName("duration"), JsonRequired] public int Duration { get; set; }
    [JsonPropertyName("escrow")] public decimal? Escrow { get; set; }
    [JsonPropertyName("min_volume")] public long? MinimumVolume { get; set; }
    [JsonPropertyName("range")] public string? Range { get; set; }
    [JsonPropertyName("is_buy_order")] public bool Buy { get; set; }
    [JsonPropertyName("is_corporation"), JsonRequired] public bool Corporation { get; set; }
    [JsonIgnore] public decimal RemainingValue => checked(Price * Remaining);
    public string? ResolvedName { get; set; }
    public string? ResolvedLocation { get; set; }
    [JsonIgnore] public string Name => ResolvedName ?? Services.IndustryCatalog.Name(TypeId);
    [JsonIgnore] public string Location => ResolvedLocation ?? "Location name unavailable";
    [JsonIgnore] public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=32";
    [JsonIgnore] public string Side => Buy ? "BUY" : "SELL";
    [JsonIgnore] public DateTimeOffset ExpiryTime => Issued.AddDays(Duration);
    [JsonIgnore] public string Expiry => Issued.AddDays(Duration).UtcDateTime.ToString("dd MMM HH:mm 'UTC'");
}

public sealed class MarketOrderObservation
{
    public MarketOrder Order { get; set; } = new();
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset MissingSince { get; set; }
}

public sealed class MarketCharacterSnapshot
{
    public long CharacterId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset Updated { get; set; }
    public DateTimeOffset NextRefresh { get; set; }
    public string Error { get; set; } = "Not refreshed";
    public List<MarketOrder> Orders { get; set; } = new();
    public List<MarketOrderObservation> Unobserved { get; set; } = new();
}

public sealed class MarketOrdersState
{
    public int SchemaVersion { get; set; } = 1;
    public HashSet<long> SelectedCharacters { get; set; } = new();
    public List<MarketCharacterSnapshot> Characters { get; set; } = new();
}

public sealed class HistoricalMarketOrder : MarketOrder
{
    [JsonPropertyName("state"), JsonRequired] public string State { get; set; } = "";
}
public sealed record HistoricalOrderRecord(long OwnerId, DateTimeOffset Observed, HistoricalMarketOrder Order);
