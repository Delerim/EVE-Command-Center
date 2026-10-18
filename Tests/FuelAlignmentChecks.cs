using System;
using System.Collections.Generic;
using System.Linq;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckFuelAlignment()
    {
        var now = new DateTimeOffset(2026,10,1,12,0,0,TimeSpan.Zero);
        var target = now.AddHours(100);
        Check(StationFuelAlignment.BlocksToAlign(target, target, 5) == 0,
            "Fuel alignment returns zero when already aligned");
        Check(StationFuelAlignment.BlocksToAlign(now.AddHours(40), target, 5) == 300,
            "Fuel alignment top-up uses exact timestamps and station-specific burn");
        Check(StationFuelAlignment.BlocksToAlign(now.AddHours(40), target, 2.5) == 150,
            "Fuel alignment supports different running-service burn rates");
        Check(StationFuelAlignment.BlocksToAlign(now.AddHours(99.5), target, 5) == 3,
            "Fuel alignment rounds fractional blocks up");
        Check(StationFuelAlignment.BlocksToAlign(now, target, null) == null,
            "Missing burn rate never fabricates a quantity");
        EsiCorporationStructure Make(long id, int hours) => new()
        {
            StructureId = id, Name = "Example drill " + id, FuelExpires = now.AddHours(hours),
            Services = new() {new EsiStructureService {Name="Moon Drilling",State="online"}}
        };
        var structures = new[] {Make(1,100),Make(2,40),Make(3,50)};
        var fuel = new[]
        {
            new EveAssetItem{LocationId=1,LocationFlag="StructureFuel",TypeId=4051,Quantity=500},
            new EveAssetItem{LocationId=2,LocationFlag="StructureFuel",TypeId=4051,Quantity=200}
        };
        var result = StationFuelAlignment.Build(structures, fuel,
            new Dictionary<int,string>{{4051,"Example Fuel Block"}},now,now);
        Check(result.Single(r=>r.StructureId==1).FuelToAdd == "0 / aligned",
            "Latest expiry is the alignment target");
        Check(result.Single(r=>r.StructureId==2).FuelToAdd.Contains("300"),
            "Fresh ESI expiry and quantity yield an explicitly estimated top-up");
        Check(result.Single(r=>r.StructureId==3).FuelToAdd == "Unknown",
            "Unknown fuel-bay inventory produces no guessed top-up");
    }
}
