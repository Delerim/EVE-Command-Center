using System.Text.Json.Serialization;
namespace EveCommandCenter.Models;

public sealed class MarketTransaction
{
    [JsonPropertyName("transaction_id"), JsonRequired] public long Id { get; set; }
    [JsonPropertyName("date"), JsonRequired] public DateTimeOffset Date { get; set; }
    [JsonPropertyName("type_id"), JsonRequired] public int TypeId { get; set; }
    [JsonPropertyName("quantity"), JsonRequired] public long Quantity { get; set; }
    [JsonPropertyName("unit_price"), JsonRequired] public decimal UnitPrice { get; set; }
    [JsonPropertyName("location_id"), JsonRequired] public long LocationId { get; set; }
    [JsonPropertyName("client_id"), JsonRequired] public long ClientId { get; set; }
    [JsonPropertyName("journal_ref_id"), JsonRequired] public long JournalRefId { get; set; }
    [JsonPropertyName("is_buy"), JsonRequired] public bool Buy { get; set; }
    [JsonPropertyName("is_personal"), JsonRequired] public bool Personal { get; set; }
    public string? ResolvedName { get; set; }
    public string? ResolvedLocation { get; set; }
    [JsonIgnore] public decimal Total => checked(Quantity * UnitPrice);
    [JsonIgnore] public string Name => ResolvedName ?? Services.IndustryCatalog.Name(TypeId);
    [JsonIgnore] public string Location => ResolvedLocation ?? "Location name unavailable";
    [JsonIgnore] public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=32";
    [JsonIgnore] public string Side => Buy ? "Purchase" : "Sale";
}
public sealed record MarketTransactionRecord(long OwnerId, DateTimeOffset Observed, MarketTransaction Transaction);
