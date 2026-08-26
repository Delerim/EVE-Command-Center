using System;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Models;

public sealed class EveWalletTransactionEntry
{
    [JsonPropertyName("client_id")]
    public long ClientId { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    [JsonPropertyName("is_buy")]
    public bool IsBuy { get; set; }

    [JsonPropertyName("is_personal")]
    public bool IsPersonal { get; set; }

    [JsonPropertyName("journal_ref_id")]
    public long JournalRefId { get; set; }

    [JsonPropertyName("location_id")]
    public long LocationId { get; set; }

    [JsonPropertyName("quantity")]
    public long Quantity { get; set; }

    [JsonPropertyName("transaction_id")]
    public long TransactionId { get; set; }

    [JsonPropertyName("type_id")]
    public int TypeId { get; set; }

    [JsonPropertyName("unit_price")]
    public decimal UnitPrice { get; set; }
}

public sealed class EveWalletTransactionView
{
    public long TransactionId { get; init; }
    public int TypeId { get; init; }
    public string Date { get; init; } = "";
    public string Direction { get; init; } = "";
    public string Item { get; init; } = "";
    public string Quantity { get; init; } = "";
    public string UnitPrice { get; init; } = "";
    public string Total { get; init; } = "";
    public decimal SignedTotalValue { get; init; }
    public bool IsBuy { get; init; }

    public string AmountForeground =>
        SignedTotalValue > 0
            ? "#58D3B4"
            : SignedTotalValue < 0
                ? "#E87979"
                : "#9DB5AF";

    public string RowBackground =>
        SignedTotalValue > 0
            ? "#10211D"
            : SignedTotalValue < 0
                ? "#211719"
                : "#0D171A";
}

public sealed class EveWalletOverview
{
    public decimal TodayIncome { get; init; }
    public decimal TodaySpent { get; init; }
    public decimal TodayNet { get; init; }

    public decimal WeekIncome { get; init; }
    public decimal WeekSpent { get; init; }
    public decimal WeekNet { get; init; }

    public decimal MarketBought { get; init; }
    public decimal MarketSold { get; init; }
    public decimal MarketNet { get; init; }

    public long PlexBought { get; init; }
    public long PlexSold { get; init; }
    public decimal PlexBuyIsk { get; init; }
    public decimal PlexSellIsk { get; init; }
    public decimal PlexNetIsk { get; init; }
    public decimal PlexAverageBuy { get; init; }
    public decimal PlexAverageSell { get; init; }

    public int JournalCount { get; init; }
    public int TransactionCount { get; init; }
    public int PlexTransactionCount { get; init; }
}
