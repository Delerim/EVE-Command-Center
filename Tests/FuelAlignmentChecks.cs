using System;
using System.Collections.Generic;
using System.Linq;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckFuelAlignment()
    {
        var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset target = now.AddHours(100);
        Check(StationFuelAlignment.BlocksToAlign(target, target, 5) == 0,
            "Fuel alignment returns zero at the target time");
        Check(StationFuelAlignment.BlocksToAlign(now.AddHours(40), target, 5) == 300,
            "Raw estimated top-up uses exact timestamps");
        Check(StationFuelAlignment.BlocksToAlign(now.AddHours(40), target, 2.5) == 150,
            "Raw calculation supports different service consumption rates");
        Check(StationFuelAlignment.BlocksToAlign(now.AddHours(99.5), target, 5) == 3,
            "Raw time-based estimate rounds up fractional blocks");
        Check(StationFuelAlignment.BlocksToAlign(now, target, null) == null,
            "Missing burn rate must never fabricate a quantity");

        EsiCorporationStructure Make(long id, double hours) => new()
        {
            StructureId = id, Name = "Example drill " + id,
            FuelExpires = now.AddHours(hours),
            Services = new() { new EsiStructureService {Name="Moon Drilling", State="online"} }
        };
        EveAssetItem Fuel(long id, long count) => new()
        {
            LocationId = id, LocationFlag="StructureFuel", TypeId=4051, Quantity=count
        };
        var types = new Dictionary<int,string> { {4051,"Example Fuel Block"} };
        var structures = new[] {Make(1,100), Make(2,40), Make(3,50), Make(4,99), Make(5,40), Make(6,40)};
        var fuel = new[] {Fuel(1,500), Fuel(2,200), Fuel(4,495), Fuel(5,400)};
        var result = StationFuelAlignment.Build(structures, fuel, types, now, now);
        Check(result.Single(r=>r.StructureId==1).FuelToAdd == "0 / aligned",
            "Latest ESI expiry remains the reference");
        Check(result.Single(r=>r.StructureId==2).FuelToAdd.Contains("+300") &&
              result.Single(r=>r.StructureId==2).FuelToAdd.Contains("500 total"),
            "Same-rate station aligns exactly to the reference stock total");
        Check(result.Single(r=>r.StructureId==4).FuelToAdd.Contains("+5") &&
              result.Single(r=>r.StructureId==4).FuelToAdd.Contains("500 total"),
            "One-hour expiry difference is one five-block tick");
        Check(result.Single(r=>r.StructureId==5).FuelToAdd.Contains("+600") &&
              result.Single(r=>r.StructureId==5).FuelToAdd.Contains("1,000 total"),
            "Different-rate station aligns by time, not by copied stock totals");
        Check(result.Single(r=>r.StructureId==3).FuelToAdd == "Unknown" &&
              result.Single(r=>r.StructureId==6).FuelToAdd == "Unknown",
            "Absent ESI stock cannot produce fabricated top-ups");
        var stale = StationFuelAlignment.Build(structures, fuel, types, now.AddHours(-3), now);
        Check(stale.Single(r=>r.StructureId==2).FuelToAdd == "Unknown",
            "Stale ESI fuel quantities never produce a top-up");

        // Realistic screenshot regression: 2,350/2,445 blocks at ~5 blocks/h,
        // aligned to 10,290 (not ~10,301 from fractional rate extrapolation).
        var large = new[] {Make(11,2058), Make(12,2057), Make(13,470), Make(14,489)};
        var largeFuel = new[] {Fuel(11,10290), Fuel(12,10285), Fuel(13,2350), Fuel(14,2445)};
        var aligned = StationFuelAlignment.Build(large, largeFuel, types, now, now);
        Check(aligned.Single(r=>r.StructureId==13).FuelToAdd.Contains("+7,940") &&
              aligned.Single(r=>r.StructureId==13).FuelToAdd.Contains("10,290 total"),
            "2,350 fuel blocks align by adding 7,940 to reach 10,290");
        Check(aligned.Single(r=>r.StructureId==14).FuelToAdd.Contains("+7,845") &&
              aligned.Single(r=>r.StructureId==14).FuelToAdd.Contains("10,290 total"),
            "2,445 fuel blocks align by adding 7,845 to reach 10,290");
        Check(aligned.Single(r=>r.StructureId==12).FuelToAdd.Contains("+5") &&
              aligned.Single(r=>r.StructureId==12).FuelToAdd.Contains("10,290 total"),
            "Other reference stocks 5 blocks behind are topped up by one tick");
    }
}
