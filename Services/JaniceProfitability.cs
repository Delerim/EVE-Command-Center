using System.Text.Json;

namespace EveCommandCenter.Services;

public enum JanicePriceSide { Buy, Split, Sell }

public sealed record JaniceOrePrice(string Name, long Quantity, decimal Buy, decimal Split, decimal Sell)
{
    public decimal Value(JanicePriceSide side) => side switch
    {
        JanicePriceSide.Buy => Buy, JanicePriceSide.Split => Split, _ => Sell
    };
}

public sealed record JaniceSnapshot(string Code, DateTimeOffset DatasetTime, IReadOnlyList<JaniceOrePrice> Items);

public sealed record JaniceSimulationSettings(
    decimal PayoutPercent = 90, JanicePriceSide PayoutSide = JanicePriceSide.Buy,
    JanicePriceSide ReferenceSide = JanicePriceSide.Sell, decimal MaximumDiscountPercent = 10,
    decimal DirectSaleFeesPercent = 0, decimal DirectCosts = 0, decimal CorpCosts = 0);

public sealed record JaniceSimulationLine(string Name, long Quantity, decimal JitaBuy, decimal JitaSplit,
    decimal JitaSell, decimal Payout, decimal NetPayout, decimal Reference, decimal Difference,
    decimal DiscountPercent, decimal RequiredPayoutPercent, bool MeetsLimit)
{
    public string Status => MeetsLimit ? "WITHIN LIMIT" : "EXCESS DISCOUNT";
    public string StatusColor => MeetsLimit ? "#74D6C9" : "#FFAD83";
    public string DifferenceColor => Difference >= 0 ? "#74D6C9" : "#FFAD83";
}

public sealed record JaniceSimulation(IReadOnlyList<JaniceSimulationLine> Lines)
{
    public decimal Payout => Lines.Sum(x => x.NetPayout);
    public decimal Reference => Lines.Sum(x => x.Reference);
    public decimal Difference => Payout - Reference;
    public decimal DiscountPercent => (1 - Payout / Reference) * 100;
    public bool MeetsLimit => Lines.Count > 0 && Lines.All(x => x.MeetsLimit);
    public decimal RequiredPayoutPercent => Lines.Max(x => x.RequiredPayoutPercent);
}

public static class JaniceProfitability
{
    // immediatePrices are the unscaled snapshot. effectivePrices already contain
    // the appraisal percentage, and applying 90% again would discount twice.
    public static JaniceSnapshot Parse(JsonElement appraisal)
    {
        if (!appraisal.TryGetProperty("pricerMarket", out var market) ||
            !market.TryGetProperty("id", out var id) || id.GetInt32() != 2)
            throw new InvalidOperationException("Use a Jita 4-4 appraisal for this comparison.");
        if (appraisal.TryGetProperty("failures", out var failures) && !string.IsNullOrWhiteSpace(failures.GetString()))
            throw new InvalidOperationException("Janice could not price all input. Fix the appraisal before simulating.");
        if (!appraisal.TryGetProperty("datasetTime", out var time) || !time.TryGetDateTimeOffset(out var timestamp))
            throw new InvalidOperationException("The appraisal has no price snapshot date.");
        var items = new List<JaniceOrePrice>();
        foreach (var item in appraisal.GetProperty("items").EnumerateArray())
        {
            long quantity = item.GetProperty("amount").GetInt64();
            string name = item.GetProperty("itemType").GetProperty("name").GetString() ?? "";
            if (quantity <= 0 || string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("Every appraisal line needs an item and a positive quantity.");
            var prices = item.GetProperty("immediatePrices");
            decimal Read(string key) => prices.TryGetProperty(key, out var p) && p.TryGetDecimal(out var value) && value > 0
                ? value : throw new InvalidOperationException($"Missing {key} for {name}; no profitability decision is available.");
            items.Add(new(name, quantity, Read("buyPriceTotal"), Read("splitPriceTotal"), Read("sellPriceTotal")));
        }
        if (items.Count == 0) throw new InvalidOperationException("The appraisal has no priced items.");
        return new(appraisal.GetProperty("code").GetString() ?? "", timestamp, items);
    }

    public static JaniceSimulation Calculate(JaniceSnapshot snapshot, JaniceSimulationSettings settings)
    {
        if (settings.PayoutPercent <= 0 || settings.PayoutPercent > 1000 ||
            settings.MaximumDiscountPercent < 0 || settings.MaximumDiscountPercent >= 100 ||
            settings.DirectSaleFeesPercent < 0 || settings.DirectSaleFeesPercent >= 100 ||
            settings.DirectCosts < 0 || settings.CorpCosts < 0 ||
            !Enum.IsDefined(settings.PayoutSide) || !Enum.IsDefined(settings.ReferenceSide))
            throw new ArgumentException("Check percentages and costs. Discounts and fees must be below 100%; costs cannot be negative.");
        if (snapshot.Items.Count == 0 || snapshot.Items.Any(x => x.Quantity <= 0 || x.Buy <= 0 || x.Split <= 0 || x.Sell <= 0))
            throw new ArgumentException("Complete positive prices and quantities are required for every line.");
        decimal totalReference = snapshot.Items.Sum(x => x.Value(settings.ReferenceSide));
        if (totalReference * (1 - settings.DirectSaleFeesPercent / 100) <= settings.DirectCosts)
            throw new ArgumentException("Direct sale costs consume the whole reference value; choose valid route costs.");
        decimal directRemaining = settings.DirectCosts, corpRemaining = settings.CorpCosts;
        var lines = new List<JaniceSimulationLine>();
        for (int i = 0; i < snapshot.Items.Count; i++)
        {
            var item = snapshot.Items[i];
            decimal grossReference = item.Value(settings.ReferenceSide);
            // Share each route's whole-basket costs once by reference value.
            decimal directCost = i == snapshot.Items.Count - 1 ? directRemaining : settings.DirectCosts * grossReference / totalReference;
            decimal corpCost = i == snapshot.Items.Count - 1 ? corpRemaining : settings.CorpCosts * grossReference / totalReference;
            directRemaining -= directCost; corpRemaining -= corpCost;
            decimal reference = grossReference * (1 - settings.DirectSaleFeesPercent / 100) - directCost;
            decimal payoutBase = item.Value(settings.PayoutSide);
            decimal payout = payoutBase * settings.PayoutPercent / 100;
            decimal net = payout - corpCost;
            decimal acceptable = reference * (1 - settings.MaximumDiscountPercent / 100);
            decimal requiredPercent = (acceptable + corpCost) / payoutBase * 100;
            lines.Add(new(item.Name, item.Quantity, item.Buy, item.Split, item.Sell, payout,
                net, reference, net - reference, (1 - net / reference) * 100, requiredPercent, net >= acceptable));
        }
        return new(lines);
    }
}
