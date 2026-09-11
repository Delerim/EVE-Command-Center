using System.Text.Json.Serialization;

namespace EveCommandCenter.Models;

public sealed class CorporationContract
{
    [JsonPropertyName("acceptor_id")] public long AcceptorId { get; set; }
    [JsonPropertyName("date_accepted")] public DateTimeOffset? Accepted { get; set; }
    [JsonPropertyName("date_completed")] public DateTimeOffset? Completed { get; set; }
    public bool WasAccepted => Accepted.HasValue || Status is "in_progress" or "finished" or "finished_issuer" or "finished_contractor";
    [JsonPropertyName("contract_id")] public long Id { get; set; }
    [JsonPropertyName("issuer_id")] public long IssuerId { get; set; }
    [JsonPropertyName("assignee_id")] public long AssigneeId { get; set; }
    [JsonPropertyName("start_location_id")] public long LocationId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("price")] public decimal? Price { get; set; }
    [JsonPropertyName("date_expired")] public DateTimeOffset Expires { get; set; }
    [JsonPropertyName("date_issued")] public DateTimeOffset Issued { get; set; }
}

public sealed class ContractRow
{
    public string Acceptor { get; set; } = "Not reported";
    public string AcceptedText => Contract.Accepted?.ToLocalTime().ToString("dd MMM yyyy HH:mm") ?? "-";
    public CorporationContract Contract { get; set; } = new();
    public long CorporationId { get; set; }
    public long ReaderCharacterId { get; set; }
    public string Issuer { get; set; } = "";
    public string Location { get; set; } = "";
    public string? JaniceUrl { get; set; }
    public decimal? JaniceBuy { get; set; }
    public decimal? ExpectedPrice { get; set; }
    public string Result { get; set; } = "CHECK NEEDED";
    public string Reason { get; set; } = "";
    public bool HasMismatch { get; set; }
    public string LocationCheck { get; set; } = "Unverified";
    public string PriceCheck { get; set; } = "Unverified";
    public decimal? ActualBuyPercent => JaniceBuy > 0 && Contract.Price.HasValue ? Contract.Price.Value / JaniceBuy.Value * 100 : null;
    public string ActualPercentText => ActualBuyPercent.HasValue ? ActualBuyPercent.Value.ToString("0.00") + "%" : "Unknown";
    public bool Passed => Result == "CHECKS PASSED";
    public string ResultColor => Passed ? "#74D6C9" : HasMismatch ? "#FF7676" : "#FFD166";
    public string RowColor => Passed ? "#0D2C27" : HasMismatch ? "#351D24" : "#302C1D";
    public string PriceText => Contract.Price.HasValue ? Contract.Price.Value.ToString("N2") + " ISK" : "Unavailable";
    public string ExpectedText => ExpectedPrice.HasValue ? ExpectedPrice.Value.ToString("N2") + " ISK" : "Unavailable";
    public string DifferenceText => ExpectedPrice.HasValue && Contract.Price.HasValue ? (Contract.Price.Value - ExpectedPrice.Value).ToString("+0.00;-0.00;0.00") + " ISK" : "-";
    public string ExpiresText => Contract.Expires.ToLocalTime().ToString("dd MMM HH:mm");
}

public sealed class ContractItem
{
    [JsonPropertyName("type_id")] public int TypeId { get; set; }
    [JsonPropertyName("quantity")] public long Quantity { get; set; }
    [JsonPropertyName("is_included")] public bool Included { get; set; }
    [JsonPropertyName("is_blueprint_copy")] public bool BlueprintCopy { get; set; }
    public string Name { get; set; } = "";
    public double UnitVolume { get; set; }
    public double Volume => Quantity * UnitVolume;
    public string Direction => Included ? "YOU RECEIVE" : "YOU PROVIDE";
    public string Kind => BlueprintCopy ? "Blueprint copy" : "Item";
    public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=64";
}

public sealed class ContractState
{
    public List<ContractRow> History { get; set; } = new();
    public HashSet<long> HistoryBaselines { get; set; } = new();
    public Dictionary<long, HashSet<long>> AcceptedNotified { get; set; } = new();
    public long CharacterId { get; set; }
    public long CorporationId { get; set; }
    public string CorporationName { get; set; } = "";
    public bool NotificationsEnabled { get; set; } = true;
    public decimal BuyPercent { get; set; } = 90;
    public decimal TolerancePercent { get; set; } = 0.1m;
    public DateTimeOffset? LastRefreshUtc { get; set; }
    public List<ContractRow> Rows { get; set; } = new();
    public Dictionary<long, HashSet<long>> SeenByCorporation { get; set; } = new();
}
