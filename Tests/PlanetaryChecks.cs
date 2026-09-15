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
        Check(analysis.StockBudget.All(b=>b.Required==analysis.Refills.Where(r=>r.Name==b.Name).Sum(r=>r.Need)), "Stock budget sums top-ups across both pilots without duplicating reserves");
        Check(analysis.Refills.All(r=>r.StockAfter>=0)&&analysis.StockBudget.All(b=>b.Shortfall>=0), "Insufficient reserves never produce negative remaining stock");
        state.Assets[1].Quantity=100000;
        var stocked=PlanetaryAnalysis.Build(state,now);
        var materialRows=stocked.Refills.Where(r=>r.TypeId==inputs[0]).ToArray();
        Check(materialRows[0].StockAfter==73685&&materialRows[1].StockAvailable==73685&&materialRows[1].StockAfter==47370, "Each planet allocation advances the shared stock balance");
        Check(stocked.StockBudget.Single(b=>b.Name==materialRows[0].Name).Remaining==materialRows[^1].StockAfter, "Final row balance agrees with the stock budget after all refills");
        state.Assets[1].Quantity=1000;
        var extractor = new { pin_id=3L, type_id=2848, install_time=now.AddHours(-2), expiry_time=now.AddHours(-1), last_cycle_start=now.AddHours(-1), extractor_details=new { cycle_time=900, qty_per_cycle=100, product_type_id=2267 }, contents=System.Array.Empty<object>() };
        state.Colonies.Add(new() { CharacterId=3, Character="Extractor Pilot", PlanetId=40000003, Planet="Test III", LastUpdate=now, Fetched=now, Layout=JsonSerializer.SerializeToElement(new { pins=new[]{extractor}, routes=System.Array.Empty<object>() }) });
        analysis = PlanetaryAnalysis.Build(state, now);
        var extractorGroups = EveCommandCenter.Views.PlanetaryExtractors.Build(analysis, new HashSet<string>(), now);
        Check(extractorGroups.Count == 1 && extractorGroups[0].Planets.Count == 1, "Extractor view excludes factory-only planets and groups by pilot");
        var expiredView = extractorGroups[0].Planets[0].Pins.Single();
        Check(expiredView.Status == "RESTART DUE" && expiredView.Next == "--" && expiredView.Remaining == "Now", "Expired extractor view stops its cycle countdown");
        var runningView = EveCommandCenter.Views.PlanetaryExtractors.Build(analysis, new HashSet<string>(), now.AddMinutes(-90))[0].Planets[0].Pins.Single();
        Check(runningView.Status == "UNDER 1 HOUR" && runningView.Quantity == "0d 0h 15m 0s" && runningView.Remaining == "0d 0h 30m 0s", "Extractor view derives cycle length and program time from the snapshot");
        Check(!EveCommandCenter.Views.PlanetaryExtractors.Build(analysis, new HashSet<string>{ "closed:extractors:pilot:3" }, now)[0].Expanded, "Extractor view retains collapsed pilot groups");
        Check(analysis.Pins.Any(p => p.Status == "RESTART / CHECK"), "Expired PI programs are flagged for restart");
        Check(analysis.Production.Single().Quantity.StartsWith("400") && analysis.Production.Single().Rate.StartsWith("0"), "Nominal extraction projection stops at program expiry");
        Check(analysis.Pins.Any(p => p.Status == "CHECK ROUTES"), "Missing factory output routes are flagged");
        var groups = EveCommandCenter.Views.PlanetaryGroups.Build(analysis, new HashSet<string>());
        Check(groups.Count == 3 && groups.Sum(g => g.Planets.Count(p => p.FactoryWorld)) == 2, "PI groups distinguish factory worlds from extraction colonies");
        Check(PlanetaryAnalysis.Tier(inputs[0]) == 1 && PlanetaryAnalysis.Tier(recipe.Outputs.Keys.First()) == 2, "PI stock tiers distinguish factory feed from sale stock");
        var basic = PlanetaryAnalysis.Recipes.Values.First(r => r.Name == "Toxic Metals");
        int raw = basic.Inputs.Keys.Single(), output = basic.Outputs.Keys.Single();
        var feedPins = new object[] {
            new { pin_id=1L, type_id=2256, contents=System.Array.Empty<object>() },
            new { pin_id=2L, type_id=2470, schematic_id=basic.Id, contents=System.Array.Empty<object>() },
            new { pin_id=3L, type_id=2848, install_time=now, expiry_time=now.AddDays(2), extractor_details=new { cycle_time=900, qty_per_cycle=100, product_type_id=raw }, contents=System.Array.Empty<object>() }
        };
        var feedRoutes = new[] { new { source_pin_id=3L, destination_pin_id=1L, content_type_id=raw }, new { source_pin_id=1L, destination_pin_id=2L, content_type_id=raw }, new { source_pin_id=2L, destination_pin_id=1L, content_type_id=output } };
        var feeding = new PiState { Colonies = new() { new() { CharacterId=4, PlanetId=44, LastUpdate=now, Layout=JsonSerializer.SerializeToElement(new { pins=feedPins, routes=feedRoutes }) } } };
        var healthy = PlanetaryAnalysis.Build(feeding, now);
        Check(healthy.Pins.Any(p => p.Status == "WAITING FOR UPSTREAM PRODUCTION" && p.Color == "#74D6C9") && healthy.Colonies.Single().Status == "MONITORING", "Routed active extraction keeps intermittently supplied basic factories healthy");
        Check(PlanetaryAnalysis.Build(feeding, now.AddDays(3)).Pins.Any(p => p.Status == "NOT STARTED / CHECK INPUTS"), "Expired extraction no longer hides empty factory inputs");
        // A running extractor with the wrong product must not masquerade as a completed factory world.
        var mismatched = JsonSerializer.SerializeToElement(new { pins=feedPins, routes=feedRoutes }).GetRawText()
            .Replace("\"product_type_id\":" + raw, "\"product_type_id\":2306")
            .Replace("\"schematic_id\":" + basic.Id, "\"last_cycle_start\":\"" + now.AddHours(-2).ToString("O") + "\",\"schematic_id\":" + basic.Id);
        feeding.Colonies[0].Layout = JsonDocument.Parse(mismatched).RootElement.Clone();
        var stalledExtraction = PlanetaryAnalysis.Build(feeding, now);
        Check(!stalledExtraction.Pins.Any(p => p.Status.StartsWith("COLLECT")) && stalledExtraction.Colonies.Single().Status == "MONITORING" && stalledExtraction.Pins.Any(p=>p.Status=="WAITING FOR MATCHING EXTRACTOR OUTPUT"), "Active extraction keeps empty basic processors out of refill alerts while exposing mismatched supply");
        var robotics = PlanetaryAnalysis.Recipes.Values.First(r => r.Name == "Robotics");
        var upstreamRecipes = robotics.Inputs.Keys.Select(product => PlanetaryAnalysis.Recipes.Values.First(r => r.Outputs.ContainsKey(product))).ToArray();
        var chainPins = new List<object> { new { pin_id=10L, type_id=2256, contents=upstreamRecipes.SelectMany(r => r.Inputs.Keys).Distinct().Select(t => new { type_id=t, amount=10000 }).ToArray() }, new { pin_id=20L, type_id=2470, schematic_id=robotics.Id, contents=System.Array.Empty<object>() } };
        var chainRoutes = new List<object>();
        for (int i=0; i<upstreamRecipes.Length; i++)
        {
            long id = 30+i; var r = upstreamRecipes[i];
            chainPins.Add(new { pin_id=id, type_id=2470, schematic_id=r.Id, contents=System.Array.Empty<object>() });
            foreach (int input in r.Inputs.Keys) chainRoutes.Add(new { source_pin_id=10L, destination_pin_id=id, content_type_id=input });
            foreach (int product in r.Outputs.Keys) { chainRoutes.Add(new { source_pin_id=id, destination_pin_id=10L, content_type_id=product }); chainRoutes.Add(new { source_pin_id=10L, destination_pin_id=20L, content_type_id=product }); }
        }
        chainRoutes.Add(new { source_pin_id=20L, destination_pin_id=10L, content_type_id=robotics.Outputs.Keys.Single() });
        var chain = new PiColony { CharacterId=5, PlanetId=55, LastUpdate=now, Layout=JsonSerializer.SerializeToElement(new { pins=chainPins, routes=chainRoutes }) };
        var chainState = new PiState { Colonies = new() { chain } };
        Check(PlanetaryAnalysis.Build(chainState,now).Pins.Any(p => p.Name == "Robotics" && p.Status == "WAITING FOR UPSTREAM PRODUCTION" && p.Color == "#74D6C9"), "T3 factories waiting for supplied T2 factories through storage remain healthy");
        Check(PlanetaryAnalysis.Build(chainState,now.AddYears(1)).Pins.Any(p => p.Name == "Robotics" && p.Status == "NOT STARTED / CHECK INPUTS"), "Exhausted upstream stock does not hide a stalled production chain");
        var complete = JsonSerializer.SerializeToElement(new { pins = chainPins, routes = chainRoutes });
        // Record a prior cycle on all factory pins, but remove the starter stock.
        var json = System.Text.Json.Nodes.JsonNode.Parse(complete.GetRawText())!;
        foreach (var p in json["pins"]!.AsArray())
        {
            p!["contents"] = new System.Text.Json.Nodes.JsonArray();
            if (p["schematic_id"] != null) p["last_cycle_start"] = now.AddDays(-3).ToString("O");
        }
        chain.Layout = JsonSerializer.SerializeToElement(json);
        var collected = PlanetaryAnalysis.Build(chainState, now);
        Check(collected.Pins.Where(p => p.IsFactory).All(p => p.Status.StartsWith("COLLECT")) && collected.Colonies.Single().Color == "#80BFFF", "Completed correctly routed factory runs flag collection instead of attention");
        Check(collected.Factories.Single().Quantity.StartsWith("3 factories") && collected.Factories.Single().Status.StartsWith("COLLECT"), "Factory summary counts facilities and collection readiness per planet");
        var stockNode = json["pins"]![0]!;
        stockNode["contents"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["type_id"] = robotics.Inputs.Keys.First(), ["amount"] = 100 },
            new System.Text.Json.Nodes.JsonObject { ["type_id"] = robotics.Outputs.Keys.Single(), ["amount"] = 30 },
            new System.Text.Json.Nodes.JsonObject { ["type_id"] = 2398, ["amount"] = 500 });
        chain.Layout = JsonSerializer.SerializeToElement(json);
        var outputSummary = PlanetaryAnalysis.Build(chainState, now);
        var products = outputSummary.FactoryTiers.SelectMany(t => t.Products).ToArray();
        Check(products.Single(p => p.TypeId == robotics.Inputs.Keys.First()).Reserved == 100 && products.Single(p => p.TypeId == robotics.Inputs.Keys.First()).Collect == 0, "T2 inventory routed to T3 remains reserved rather than collectable");
        Check(products.Single(p => p.TypeId == robotics.Outputs.Keys.Single()).Collect == 30, "Final T3 stock is counted once as available for collection");
        Check(outputSummary.FactoryTiers.Select(t => t.Tier).SequenceEqual(new[] {1,2,3}) && products.Single(p => p.TypeId == robotics.Outputs.Keys.Single()).Capacity == 3, "Factory output is grouped by tier with recipe-based hourly capacity");
        Check(products.Single(p=>p.TypeId==2398).Stored==500,"Stored T1 feedstock is counted even on planets that only manufacture higher tiers");
        var processorNode = json["pins"]!.AsArray().First(p => p!["pin_id"]!.GetValue<long>() == 20)!;
        processorNode["contents"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type_id"] = robotics.Inputs.Keys.First(), ["amount"] = 10 });
        chain.Fetched = now; chain.LastUpdate = now;
        chain.Layout = JsonSerializer.SerializeToElement(json);
        var buffered = PlanetaryAnalysis.Build(chainState, now);
        var bufferedT2 = buffered.FactoryTiers.SelectMany(t=>t.Products).Single(p=>p.TypeId==robotics.Inputs.Keys.First());
        Check(bufferedT2.Stored==110 && bufferedT2.Reserved==110 && bufferedT2.Collect==0, "T2 processor buffers appear in snapshots and remain reserved");
        Check(buffered.Refills.All(r=>PlanetaryAnalysis.Tier(r.TypeId)==1) && buffered.Refills.Sum(r=>r.Target * PlanetaryAnalysis.Types[r.TypeId].Volume)>9000, "T1 refill targets do not allocate launchpad space to routed T2 inputs");
        Check(buffered.Colonies.Single().Next.Contains("Last checked") && buffered.Colonies.Single().Next.Contains("Snapshot updated"), "PI distinguishes a successful recent check from older colony contents");
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
