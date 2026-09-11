using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static PiState CheckPlanetary()
    {
        var recipe = PlanetaryAnalysis.Recipes.Values.First(r => r.Name == "Mechanical Parts");
        var inputs = recipe.Inputs.Keys.ToArray();
        Check(PlanetaryAnalysis.Recipes.Count == 68 && recipe.Inputs.Count == 2, "Bundled CCP PI recipes include both Mechanical Parts inputs");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var state = new PiState { ContainerId = 900, StockFetched = now, Assets = new()
        {
            new() { ItemId=901, LocationId=900, TypeId=999, Quantity=1 },
            new() { ItemId=902, LocationId=901, TypeId=inputs[0], Quantity=1000 },
            new() { ItemId=903, LocationId=123, TypeId=inputs[0], Quantity=999999 }
        }};
        Check(PlanetaryAnalysis.Stock(state)[inputs[0]] == 1000, "PI stock includes nested contents but excludes unrelated station cargo");
        var pins = new object[]
        {
            new { pin_id=1L, type_id=2256, contents=System.Array.Empty<object>() },
            new { pin_id=2L, type_id=2470, schematic_id=recipe.Id, last_cycle_start=now, contents=System.Array.Empty<object>() }
        };
        var routes = inputs.Select(i => new { source_pin_id=1L, destination_pin_id=2L, content_type_id=i, quantity=40 }).ToArray();
        state.Colonies.Add(new() { CharacterId=1, Character="Test Pilot", PlanetId=40000001, Planet="Test I", PlanetType="temperate", LastUpdate=now, Fetched=now, Layout=JsonSerializer.SerializeToElement(new { pins, routes }) });
        var analysis = PlanetaryAnalysis.Build(state, now);
        Check(analysis.Refills.Count == 2 && analysis.Refills.Sum(r => r.Target) == 52630, "A 10000 m3 launchpad holds 52630 combined P1 units, not that amount per input");
        Check(analysis.Refills.Sum(r => r.Target * PlanetaryAnalysis.Types[r.TypeId].Volume) <= 10000, "Balanced refill targets fit the actual launchpad capacity");
        state.Colonies.Add(new() { CharacterId=2, Character="Second Pilot", PlanetId=40000002, Planet="Test II", LastUpdate=now, Fetched=now, Layout=state.Colonies[0].Layout });
        analysis = PlanetaryAnalysis.Build(state, now);
        Check(analysis.Refills.Where(r => r.TypeId == inputs[0]).Sum(r => r.Allocated) == 1000, "Shared PI stock is allocated once across all colonies");
        var extractor = new { pin_id=3L, type_id=2848, install_time=now.AddHours(-2), expiry_time=now.AddHours(-1), last_cycle_start=now.AddHours(-1), extractor_details=new { cycle_time=900, qty_per_cycle=100, product_type_id=2267 }, contents=System.Array.Empty<object>() };
        state.Colonies.Add(new() { CharacterId=3, Character="Extractor Pilot", PlanetId=40000003, Planet="Test III", LastUpdate=now, Fetched=now, Layout=JsonSerializer.SerializeToElement(new { pins=new[]{extractor}, routes=System.Array.Empty<object>() }) });
        analysis = PlanetaryAnalysis.Build(state, now);
        Check(analysis.Pins.Any(p => p.Status == "RESTART / CHECK"), "Expired PI programs are flagged for restart");
        Check(analysis.Production.Single().Quantity.StartsWith("400") && analysis.Production.Single().Rate.StartsWith("0"), "Nominal extraction projection stops at program expiry");
        Check(analysis.Pins.Any(p => p.Status == "CHECK INPUTS"), "Empty factory inputs are flagged without inventing future production");
        return state;
    }
    private static void CheckMiningRates()
    {
        var end = DateTime.UtcNow;
        var pulls = Enumerable.Range(0, 5).SelectMany(i => new[] {new MiningRateEstimator.Pull(end.AddSeconds((i-4)*24.1),540,540),new MiningRateEstimator.Pull(end.AddSeconds((i-4)*24.1+1),540,540)}).ToArray();
        var result = MiningRateEstimator.Calculate(pulls, end.AddSeconds(1));
        Check(Math.Abs(result.Base - 1080/24.1) < 0.001, "Two 540 m3 miners finishing one second apart produce 44.81 m3/s, not an inflated endpoint rate");
        var critical = pulls.ToArray(); critical[^1] = critical[^1] with { Actual=1080 };
        var crit = MiningRateEstimator.Calculate(critical,end.AddSeconds(1));
        Check(Math.Abs(crit.Base-result.Base)<0.001 && crit.Actual > crit.Base, "Critical bonus increases REAL while leaving normalized BASE unchanged");
        Check(MiningRateEstimator.Calculate(pulls,end.AddMinutes(10)).Base == 0, "Stopped mining does not retain a live production rate indefinitely");
        var bonusLine = pulls.Concat(new[] { new MiningRateEstimator.Pull(end, 0, 1620) });
        var additional = MiningRateEstimator.Calculate(bonusLine, end.AddSeconds(1));
        Check(Math.Abs(additional.Base-result.Base)<0.001 && Math.Abs(additional.Actual-result.Actual-1620/additional.Seconds)<0.001, "Separate additional critical log entry adds only bonus volume to REAL");
        Check(MiningRateEstimator.Calculate(pulls.Take(2),end).Base == 0, "A first paired pull cannot become an enormous instantaneous rate");
    }
}
