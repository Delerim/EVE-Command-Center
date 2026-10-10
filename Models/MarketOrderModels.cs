using System.Text.Json.Serialization;

namespace EveCommandCenter.Models;

public sealed class MarketOrder
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
    [JsonPropertyName("is_buy_order")] public bool Buy { get; set; }
    [JsonPropertyName("is_corporation"), JsonRequired] public bool Corporation { get; set; }
    [JsonIgnore] public decimal RemainingValue => checked(Price * Remaining);
    [JsonIgnore] public string Name => Services.IndustryCatalog.Name(TypeId);
    [JsonIgnore] public string Side => Buy ? "BUY" : "SELL";
    [JsonIgnore] public string Expiry => Issued.AddDays(Duration).UtcDateTime.ToString("dd MMM HH:mm 'UTC'");
}

public sealed class MarketCharacterSnapshot
{
    public long CharacterId { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset Updated { get; set; }
    public DateTimeOffset NextRefresh { get; set; }
    public string Error { get; set; } = "Not refreshed";
    public List<MarketOrder> Orders { get; set; } = new();
}

public sealed class MarketOrdersState
{
    public int SchemaVersion { get; set; } = 1;
    public HashSet<long> SelectedCharacters { get; set; } = new();
    public List<MarketCharacterSnapshot> Characters { get; set; } = new();
}
