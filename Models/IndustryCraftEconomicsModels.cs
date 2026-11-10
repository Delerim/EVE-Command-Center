namespace EveCommandCenter.Models;

public sealed class CraftEconomicsSettings
{
    public string Hub { get; set; } = "Jita";
    public string SystemName { get; set; } = "";
    public long? SystemId { get; set; }
    public decimal? FacilityTaxPercent { get; set; }
    public decimal JobBonusPercent { get; set; }
    public decimal SccPercent { get; set; } = 4;
    public decimal AlphaPercent { get; set; }
    public decimal TaxPercent { get; set; } = 7.5m;
    public decimal BrokerPercent { get; set; } = 3;
    public decimal ExtraPerRun { get; set; }
    public bool InstantSale { get; set; }
}

public sealed record CraftMarketQuote(decimal? Buy, decimal? Sell, long BuyVolume, long SellVolume,
    DateTimeOffset ObservedUtc, DateTimeOffset ExpiresUtc);

public sealed class CraftPricingState
{
    public CraftEconomicsSettings Settings { get; set; } = new();
    public Dictionary<string, CraftMarketQuote> Quotes { get; set; } = new();
    public Dictionary<int, decimal> AdjustedPrices { get; set; } = new();
    public Dictionary<long, decimal> ManufacturingIndices { get; set; } = new();
    public Dictionary<string, long> Systems { get; set; } = new();
    public DateTimeOffset AdjustedExpiresUtc { get; set; }
    public DateTimeOffset IndicesExpiresUtc { get; set; }
}
