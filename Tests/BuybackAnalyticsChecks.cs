using System;
using System.Collections.Generic;
using System.Linq;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckBuybackAnalytics()
    {
        DateTime mon = new(2026, 10, 12);
        Check(BuybackAnalytics.Range(new DateTime(2026, 10, 18), "Week") ==
              (mon, mon.AddDays(7)), "Buyback week follows Monday-to-Sunday UTC");
        Check(BuybackAnalytics.Range(mon, "Custom", new DateTime(2026, 10, 17), new DateTime(2026, 10, 15)) ==
              (new DateTime(2026, 10, 15), new DateTime(2026, 10, 18)),
              "Custom buyback range includes final day and tolerates reversed dates");

        ContractRow Accepted(long id, DateTime at, decimal amount, string issuer, long corp = 42) => new()
        {
            CorporationId = corp, Issuer = issuer, JaniceUrl = "https://janice.e-351.com/a/example",
            Contract = new CorporationContract
            {
                Id = id, IssuerId = id, AssigneeId = corp, Type = "item_exchange",
                Status = "finished", Price = amount, Accepted = new DateTimeOffset(at, TimeSpan.Zero),
                Issued = new DateTimeOffset(at.AddDays(-1), TimeSpan.Zero)
            }
        };
        var records = new List<ContractRow>
        {
            Accepted(1, mon.AddDays(1), 1000, "Miner A"),
            Accepted(2, mon.AddDays(2), 1500, "Miner A"),
            Accepted(3, mon.AddDays(3), 500, "Miner B"),
            Accepted(4, mon.AddDays(-3), 1200, "Miner B"),
            Accepted(5, mon.AddDays(7), 800, "Miner C"), // exclusive end
            Accepted(6, mon.AddDays(1), 9000, "Other corporation", 88),
            Accepted(7, mon.AddDays(1), 777, "Nonbuyback")
        };
        records[6].JaniceUrl = null;
        records.Add(records[0]); // ESI snapshots may include duplicate observations.
        var items = new Dictionary<string, List<ContractItem>>
        {
            [BuybackAnalytics.Key(records[0])] = new()
            {
                new ContractItem { Name = "Compressed Zeolites", Quantity = 2, UnitVolume = 10, Included = true },
                new ContractItem { Name = "Compressed Coesite", Quantity = 1, UnitVolume = 30, Included = true },
                new ContractItem { Name = "Unrelated outgoing item", Quantity = 999, UnitVolume = 10, Included = false }
            }
        };
        var result = BuybackAnalytics.Build(records, 42, mon, mon.AddDays(7), items);
        Check(result.Count == 3 && result.ValueIsk == 3000m && result.PreviousValueIsk == 1200m,
            "Analytics deduplicates by contract ID and uses accepted buybacks in exact period");
        Check(result.Suppliers.First().Label == "Miner A" && result.Suppliers.First().Isk == 2500m,
            "Supplier ranking uses actual accepted contract prices");
        var pendingNow = new ContractRow { CorporationId = 42, Contract = new CorporationContract { Id = 900, Status = "outstanding" } };
        var pendingSnapshot = BuybackAnalytics.Build(records, 42, mon, mon.AddDays(7), items,
            "Week", new[] { pendingNow, pendingNow });
        Check(pendingSnapshot.Outstanding == 1,
            "Live outstanding KPI deduplicates current ESI contracts regardless of selected historical period");
        Check(result.IndexedContracts == 1 && Math.Abs(result.KnownVolumeM3 - 50) < 0.00001 &&
              result.ContentCoveragePercent == 33,
            "Item mix includes only indexed included ESI items, with coverage explicitly shown");
        Check(result.Contents.Any(x => x.Label == "Zeolites" && Math.Abs(x.CubicMetres - 20) < 0.00001) &&
              result.Contents.Any(x => x.Label == "Coesite" && Math.Abs(x.CubicMetres - 30) < 0.00001),
            "Ore volumes are grouped from ESI contents rather than guessed from ISK");
        Check(result.TimeBuckets.Count == 7 && result.TimeBuckets.Sum(x => x.Value) == 3000m,
            "Trend buckets sum to the accepted contract price without creating extra values");
        var saved = new ContractState { CachedContractItems = items };
        var restored = System.Text.Json.JsonSerializer.Deserialize<ContractState>(
            System.Text.Json.JsonSerializer.Serialize(saved));
        Check(restored != null && restored.CachedContractItems[BuybackAnalytics.Key(records[0])][0].UnitVolume == 10,
            "Verified contract item volumes survive local JSON archive round-tripping");
        var monthHistory = new List<ContractRow> {
            Accepted(50, new DateTime(2026, 9, 1), 500m, "September"),
            Accepted(51, new DateTime(2026, 8, 31), 900m, "August")
        };
        var october = BuybackAnalytics.Build(monthHistory, 42, new DateTime(2026, 10, 1),
            new DateTime(2026, 11, 1), items, "Month");
        Check(october.PreviousValueIsk == 500m,
            "Monthly comparison covers the previous calendar month, not extra August days");
        var none = BuybackAnalytics.Build(records, 42, mon.AddYears(1), mon.AddYears(1).AddDays(7), items);
        Check(none.Count == 0 && none.TimeBuckets.Count == 7 && none.IndexedContracts == 0,
            "Missing history produces a zero-current-period view, not a fabricated forecast");
    }
}
