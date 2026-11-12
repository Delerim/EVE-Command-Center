using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public static class IndustryCraftEconomics
{
    public sealed record Estimate(long Runs, long Output, decimal? Materials, decimal? JobCost,
        decimal? Extras, decimal? GrossSale, decimal? Broker, decimal? Tax, string Note)
    {
        public int ProductTypeId { get; init; }
        public decimal TaxRate { get; init; }
        public decimal BrokerRate { get; init; }
        public bool HasBrokerFee { get; init; }
        public bool PerItemBreakEvenValid { get; init; } = true;
        public decimal? TotalCost => Materials + JobCost + Extras;
        public decimal? Fees => Broker + Tax;
        public decimal? NetSale => GrossSale - Fees;
        public decimal? Profit => NetSale.HasValue && TotalCost.HasValue ? decimal.Round(NetSale.Value-TotalCost.Value,2,MidpointRounding.AwayFromZero) : null;
        public decimal? Margin => NetSale > 0 ? Profit / NetSale * 100 : null;
        public decimal? BreakEvenUnit => !PerItemBreakEvenValid || !TotalCost.HasValue || Output <= 0 ? null :
            HasBrokerFee ? Math.Max((TotalCost.Value+100)/(1-TaxRate),TotalCost.Value/(1-TaxRate-BrokerRate))/Output : TotalCost/(1-TaxRate)/Output;
        public string Status => Profit == null ? "NEEDS PRICING / SETUP" : Profit > 0 ? "PROJECTED PROFIT" : Profit < 0 ? "PROJECTED LOSS" : "BREAK EVEN";
        public string Color => Profit == null ? "#EDBC73" : Profit > 0 ? "#77DEC0" : Profit < 0 ? "#FF9096" : "#B9CDD0";
        public string ProfitText => Money(Profit);
        public string MaterialsText => Money(Materials);
        public string JobCostText => Money(JobCost);
        public string GrossSaleText => Money(GrossSale);
        public string FeesText => Money(Fees);
        public string MarginText => Margin?.ToString("+0.0;-0.0;0.0") + (Margin.HasValue ? "%" : "—");
        public string Breakdown => $"{Runs:N0} runs / {Output:N0} output units\nMaterials (replacement asks): {Money(Materials)}\nJob installation: {Money(JobCost)}\nBPC / hauling / extras: {Money(Extras)}\nTotal input cost: {Money(TotalCost)}\nGross output value: {Money(GrossSale)}\nSell broker fee: {Money(Broker)}\nSales tax: {Money(Tax)}\nNet sale receipts: {Money(NetSale)}\nProjected profit: {Money(Profit)}\nBreak-even per item: {Money(BreakEvenUnit)}\n{Note}";
    }
    public static string Money(decimal? value) => value.HasValue ? value.Value.ToString("N2") + " ISK" : "Pending";
    public static string Key(string hub, int type) => hub + ":" + type;
    public static bool Fresh(CraftMarketQuote q, DateTimeOffset now) => q.ObservedUtc != default && q.ObservedUtc <= now.AddMinutes(5) && q.ExpiresUtc > now;
    public static void Validate(CraftEconomicsSettings s)
    {
        if (s.Hub is not ("Jita" or "Amarr") || s.TaxPercent < 0 || s.BrokerPercent < 0 || s.TaxPercent + s.BrokerPercent >= 100 ||
            s.FacilityTaxPercent is < 0 or > 100 || s.JobBonusPercent is < 0 or > 100 || s.SccPercent is < 0 or > 100 ||
            s.AlphaPercent is < 0 or > 100 || s.ExtraPerRun < 0)
            throw new InvalidOperationException("Use non-negative costs and rates; sales tax + broker fees must be below 100%.");
    }
    public static Estimate Calculate(IndustryCraftSuggestions.Suggestion suggestion, long runs,
        CraftPricingState prices, CraftEconomicsSettings settings, DateTimeOffset now)
    {
        Validate(settings);
        if (runs <= 0 || runs > suggestion.RemainingRuns)
            return new(runs, 0, null, null, null, null, null, null, "Run count exceeds this copy's unplanned runs.");
        var recipe = suggestion.Recipe;
        try
        {
            decimal? materialCost = 0, eiv = 0;
            var observedQuotes = new List<CraftMarketQuote>();
            bool depth = false;
            foreach (var material in recipe.Materials)
            {
                long amount = IndustryCraftSuggestions.Material(material.Value, runs, suggestion.Blueprint.ME);
                var quote = prices.Quotes.GetValueOrDefault(Key(settings.Hub, material.Key));
                if (quote != null) observedQuotes.Add(quote);
                decimal? ask = quote != null && Fresh(quote, now) && quote.Sell > 0 ? quote.Sell : null;
                materialCost += amount * ask;
                if (ask.HasValue && quote!.SellVolume < amount) depth = true;
                eiv += prices.AdjustedPrices.TryGetValue(material.Key, out var adjusted) && adjusted > 0 ? (decimal)material.Value * runs * adjusted : null;
            }
            // EIV uses base ME0 recipe quantities, independently of actual ME-reduced inputs.
            decimal? job = null;
            if (prices.AdjustedExpiresUtc > now && prices.IndicesExpiresUtc > now && settings.SystemId.HasValue &&
                prices.ManufacturingIndices.TryGetValue(settings.SystemId.Value, out var index) && index is >= 0 and <= 1 && settings.FacilityTaxPercent.HasValue)
                job = eiv * (index * (1 - settings.JobBonusPercent / 100) +
                    (settings.FacilityTaxPercent + settings.SccPercent + settings.AlphaPercent) / 100);
            long output = checked((long)((decimal)recipe.Products.Single().Value * runs));
            var product = prices.Quotes.GetValueOrDefault(Key(settings.Hub, recipe.Products.Single().Key));
            if (product != null) observedQuotes.Add(product);
            decimal? price = product != null && Fresh(product, now) ? settings.InstantSale ? product.Buy : product.Sell : null;
            if (price <= 0) price = null;
            decimal? gross = output * price;
            if (product != null && (settings.InstantSale ? product.BuyVolume : product.SellVolume) < output) depth = true;
            var note = new List<string>();
            if (materialCost == null || gross == null) note.Add("Missing or expired hub quotes; refresh prices.");
            if (job == null) note.Add("Set build system + facility tax and refresh prices to calculate job costs.");
            if (depth) note.Add("Top-price volume is smaller than this batch; actual execution may cost more or return less.");
            if (observedQuotes.Count > 0) note.Add($"Oldest quote check {observedQuotes.Min(q => q.ObservedUtc).UtcDateTime:dd MMM HH:mm} UTC; earliest expiry {observedQuotes.Min(q => q.ExpiresUtc).UtcDateTime:dd MMM HH:mm} UTC.");
            note.Add($"{settings.Hub} indicative station prices. Owned materials included at replacement value. Material facility/rig bonuses excluded. Initial listing fees only (100 ISK minimum); relisting and price movement excluded.");
            return new(runs, output, materialCost, job, settings.ExtraPerRun * runs, gross,
                settings.InstantSale ? 0 : gross.HasValue ? Math.Max(100,gross.Value * settings.BrokerPercent / 100) : null,
                gross * settings.TaxPercent / 100, string.Join(" ", note))
            {ProductTypeId=recipe.Products.Single().Key,TaxRate=settings.TaxPercent/100,BrokerRate=settings.BrokerPercent/100,HasBrokerFee=!settings.InstantSale};
        }
        catch (OverflowException) { return new(runs, 0, null, null, null, null, null, null, "Batch value exceeds the supported calculation range."); }
    }
    public static Estimate Combine(IEnumerable<Estimate> estimates)
    {
        var rows=estimates.ToArray();
        decimal? Sum(Func<Estimate,decimal?> field) => rows.Any(r=>field(r)==null) ? null : rows.Sum(r=>field(r)!.Value);
        decimal? broker=rows.Any(r=>r.GrossSale==null)?null:rows.GroupBy(r=>r.ProductTypeId).Sum(g=>g.First().HasBrokerFee?Math.Max(100,g.Sum(r=>r.GrossSale!.Value)*g.First().BrokerRate):0);
        return new(rows.Sum(r=>r.Runs),rows.Sum(r=>r.Output),Sum(r=>r.Materials),Sum(r=>r.JobCost),Sum(r=>r.Extras),
            Sum(r=>r.GrossSale),broker,Sum(r=>r.Tax),"Combined estimate; each copy's rounding/job fees are separate, with one sell listing per output type. " + string.Join(" ",rows.Select(r=>r.Note).Distinct()))
            {PerItemBreakEvenValid=rows.Length>0 && rows.All(r=>r.ProductTypeId>0) && rows.Select(r=>r.ProductTypeId).Distinct().Count()==1,
                TaxRate=rows.FirstOrDefault()?.TaxRate??0,BrokerRate=rows.FirstOrDefault()?.BrokerRate??0,HasBrokerFee=rows.FirstOrDefault()?.HasBrokerFee??false};
    }
}
