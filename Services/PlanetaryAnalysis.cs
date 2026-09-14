using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public static class PlanetaryAnalysis
{
    private sealed class Catalog { public List<PiType> Types { get; set; } = new(); public List<PiRecipe> Recipes { get; set; } = new(); }
    private static readonly Catalog Data = Load();
    public static readonly Dictionary<int, PiType> Types = Data.Types.ToDictionary(t => t.Id);
    public static readonly Dictionary<int, PiRecipe> Recipes = Data.Recipes.ToDictionary(t => t.Id);
    private static Catalog Load()
    {
        using var stream = typeof(PlanetaryAnalysis).Assembly.GetManifestResourceStream("EveCommandCenter.Resources.pi-catalog.json")!;
        return JsonSerializer.Deserialize<Catalog>(stream)!;
    }
    public static int Tier(int id) => Types.GetValueOrDefault(id)?.Group switch { 1042 => 1, 1034 => 2, 1040 => 3, 1041 => 4, _ => 0 };
    internal static PiType Type(int id) => Types.GetValueOrDefault(id) ?? new() { Id = id, Name = "Type " + id };
    internal static double Num(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.TryGetDouble(out double n) ? n : 0;
    private static DateTimeOffset? Date(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.TryGetDateTimeOffset(out var d) ? d : null;
    internal static JsonElement[] Array(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToArray() : System.Array.Empty<JsonElement>();
    private static Dictionary<int, double> Contents(JsonElement p) => Array(p, "contents").GroupBy(c => (int)Num(c, "type_id")).ToDictionary(g => g.Key, g => g.Sum(c => Num(c, "amount")));
    private static string Time(double seconds) => !double.IsFinite(seconds) ? "Unknown" : seconds <= 0 ? "Now" : TimeSpan.FromSeconds(seconds) is var t ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}m" : "";
    public static Dictionary<int, double> Stock(PiState state)
    {
        if (state.ContainerId == 0) return new();
        var parents = new HashSet<long> { state.ContainerId };
        bool changed;
        do { changed = false; foreach (var a in state.Assets) if (parents.Contains(a.LocationId)) changed |= parents.Add(a.ItemId); } while (changed);
        return state.Assets.Where(a => parents.Contains(a.LocationId)).GroupBy(a => a.TypeId).ToDictionary(g => g.Key, g => g.Sum(a => (double)a.Quantity));
    }
    public static PiAnalysis Build(PiState state, DateTimeOffset now)
    {
        var result = new PiAnalysis();
        var factoryProducts = new Dictionary<int, PiProductTotal>();
        var production = new Dictionary<int, (double rate, double extracted)>();
        var stock = Stock(state);
        foreach (var item in stock.OrderByDescending(k => Tier(k.Key)).ThenBy(k => Type(k.Key).Name))
        {
            var type = Type(item.Key);
            var quote = state.Prices.GetValueOrDefault(item.Key);
            int tier = Tier(item.Key);
            result.Stock.Add(new() { Color = tier >= 2 ? "#FFD166" : "#74D6C9", Status = quote?.Buy is {} buy ? buy.ToString("N2") + " ISK" : "Unavailable", Remaining = quote?.Buy is {} value ? (value * item.Value).ToString("N0") + " ISK" : "--", Name = type.Name, Icon = type.Icon, Quantity = item.Value.ToString("N0"), Detail = tier >= 2 ? $"T{tier} | Sale stock" : tier == 1 ? "T1 | Factory feed" : type.Group == 1027 ? "Command centre | Equipment" : "Raw / other", Rate = (item.Value * type.Volume).ToString("N1") + " m3", Next = quote == null ? "Price pending" : "Price: " + quote.Checked.ToLocalTime().ToString("dd MMM HH:mm") });
        }
        foreach (var colony in state.Colonies.OrderBy(c => c.Character).ThenBy(c => c.Planet))
        {
            if (colony.Layout.ValueKind != JsonValueKind.Object) continue;
            var pins = Array(colony.Layout, "pins"); var routes = Array(colony.Layout, "routes");
            bool factoryWorld = !pins.Any(p => p.TryGetProperty("extractor_details", out _));
            var byId = pins.ToDictionary(p => (long)Num(p, "pin_id"));
            var stores = byId.ToDictionary(p => p.Key, p => Contents(p.Value));
            var demands = new Dictionary<(long pin, int type), double>();
            var factories = new Dictionary<long, PiRecipe>();
            foreach (var pin in pins)
            {
                int schematic = (int)Num(pin, "schematic_id");
                if (schematic == 0 && pin.TryGetProperty("factory_details", out var f)) schematic = (int)Num(f, "schematic_id");
                if (!Recipes.TryGetValue(schematic, out var recipe) || recipe.Cycle <= 0) continue;
                long id = (long)Num(pin, "pin_id"); factories[id] = recipe;
                foreach (var input in recipe.Inputs)
                {
                    var sources = routes.Where(r => (long)Num(r, "destination_pin_id") == id && (int)Num(r, "content_type_id") == input.Key).Select(r => (long)Num(r, "source_pin_id")).Distinct().ToArray();
                    foreach (var source in sources)
                    {
                        var key = (source, input.Key);
                        demands[key] = demands.GetValueOrDefault(key) + input.Value * 3600 / recipe.Cycle / Math.Max(1, sources.Length);
                    }
                }
            }
            int attention = 0, extractors = 0;
            double nextAction = double.PositiveInfinity;
            foreach (var pin in pins)
            {
                long id = (long)Num(pin, "pin_id"); var type = Type((int)Num(pin, "type_id"));
                var row = new PiRow { IsFactory = type.Group == 1028, Name = type.Name, Icon = type.Icon, Colony = colony, Detail = colony.Character + " | " + colony.Planet + " | pin " + id };
                if (pin.TryGetProperty("extractor_details", out var extractor))
                {
                    extractors++;
                    double cycle = Num(extractor, "cycle_time"), quantity = Num(extractor, "qty_per_cycle");
                    var expiry = Date(pin, "expiry_time"); var install = Date(pin, "install_time"); var last = Date(pin, "last_cycle_start");
                    bool active = expiry > now && cycle > 0;
                    int product = (int)Num(extractor, "product_type_id");
                    double elapsed = install.HasValue && expiry.HasValue ? Math.Max(0, ((now < expiry ? now : expiry.Value) - install.Value).TotalSeconds) : 0;
                    double extracted = cycle > 0 ? Math.Floor(elapsed / cycle) * quantity : 0;
                    var totals = production.GetValueOrDefault(product);
                    production[product] = (totals.rate + (active ? quantity * 3600 / cycle : 0), totals.extracted + extracted);
                    row.Name = Type(product).Name + " extractor"; row.Icon = Type(product).Icon;
                    row.Status = active ? "EXTRACTING (EST.)" : "RESTART / CHECK";
                    row.Color = active ? "#74D6C9" : "#FFD166";
                    row.Quantity = $"{quantity:N0} nominal units/cycle";
                    row.Rate = cycle > 0 ? $"{quantity * 3600 / cycle:N0} nominal units/h" : "No program";
                    row.Remaining = expiry.HasValue ? Time((expiry.Value - now).TotalSeconds) + " to program end" : "No expiry reported";
                    row.Next = active && (last ?? install) is { } start ? Time(cycle - Math.Max(0, (now - start).TotalSeconds) % cycle) + " to next cycle (est.)" : "--";
                    if (!active) attention++;
                    if (expiry.HasValue) nextAction = Math.Min(nextAction, Math.Max(0, (expiry.Value - now).TotalSeconds));
                }
                else if (factories.TryGetValue(id, out var recipe))
                {
                    bool HasActiveFeed(int product)
                    {
                        bool Supply(long destination, int input, HashSet<(long, int)> path, bool allowStock)
                        {
                            if (!path.Add((destination, input)) || !byId.TryGetValue(destination, out var target)) return false;
                            if (allowStock && stores[destination].GetValueOrDefault(input) > demands.GetValueOrDefault((destination, input)) * Math.Max(0, (now - colony.LastUpdate).TotalHours)) return true;
                            if (target.TryGetProperty("extractor_details", out var extractorFeed) && (int)Num(extractorFeed, "product_type_id") == input && Num(extractorFeed, "cycle_time") > 0 && Date(target, "expiry_time") > now) return true;
                            if (factories.TryGetValue(destination, out var upstream) && upstream.Outputs.ContainsKey(input))
                            {
                                return upstream.Inputs.All(i =>
                                    routes.Any(r => (long)Num(r, "destination_pin_id") == destination && (int)Num(r, "content_type_id") == i.Key) &&
                                    (stores[destination].GetValueOrDefault(i.Key) >= i.Value || Supply(destination, i.Key, new(path), false)));
                            }
                            foreach (var route in routes.Where(r => (long)Num(r, "destination_pin_id") == destination && (int)Num(r, "content_type_id") == input))
                            {
                                long source = (long)Num(route, "source_pin_id");
                                if (Supply(source, input, new(path), true)) return true;
                            }
                            return false;
                        }
                        // A route-backed producer must be present; direct inventory is handled by the runway calculation.
                        return routes.Where(r => (long)Num(r, "destination_pin_id") == id && (int)Num(r, "content_type_id") == product)
                            .Any(r => Supply((long)Num(r, "source_pin_id"), product, new() { (id, product) }, false));
                    }
                    bool routed = recipe.Inputs.Keys.All(product => routes.Any(r => (long)Num(r, "destination_pin_id") == id && (int)Num(r, "content_type_id") == product))
                        && recipe.Outputs.Keys.All(product => routes.Any(r => (long)Num(r, "source_pin_id") == id && (int)Num(r, "content_type_id") == product));
                    bool extractingFeed = routed && recipe.Inputs.Keys.All(HasActiveFeed);
                    bool extractionWaiting = routed && !extractingFeed && recipe.Outputs.Keys.All(t=>Tier(t)==1) && pins.Any(p=>p.TryGetProperty("extractor_details",out var x) && Num(x,"cycle_time")>0 && Date(p,"expiry_time")>now);
                    double hours = double.PositiveInfinity;
                    foreach (var input in recipe.Inputs)
                    {
                        var sources = routes.Where(r => (long)Num(r, "destination_pin_id") == id && (int)Num(r, "content_type_id") == input.Key).Select(r => (long)Num(r, "source_pin_id")).Distinct().ToArray();
                        double supplyHours = stores[id].GetValueOrDefault(input.Key) / (input.Value * 3600 / recipe.Cycle);
                        foreach (var source in sources)
                        {
                            double demand = demands.GetValueOrDefault((source, input.Key));
                            if (demand > 0 && stores.TryGetValue(source, out var contents)) supplyHours += contents.GetValueOrDefault(input.Key) / demand / Math.Max(1, sources.Length);
                        }
                        hours = Math.Min(hours, supplyHours);
                    }
                    // Supply is a snapshot, not evidence that a factory is actively routed/running now.
                    double ageHours = Math.Max(0, (now - colony.LastUpdate).TotalHours);
                    double projected = double.IsFinite(hours) ? Math.Max(0, hours - ageHours) : 0;
                    bool ran = Date(pin, "last_cycle_start").HasValue || recipe.Outputs.Keys.Any(product => stores.Values.Any(c => c.GetValueOrDefault(product) > 0));
                    bool finishing = routed && Date(pin, "last_cycle_start") is {} started && started <= now && started.AddSeconds(recipe.Cycle) > now;
                    bool collect = factoryWorld && routed && !extractingFeed && !finishing && projected <= 0 && ran;
                    row.Name = recipe.Name; row.Status = !routed ? "CHECK ROUTES" : extractingFeed ? "WAITING FOR UPSTREAM PRODUCTION" : projected > 0 ? "SUPPLIED (EST.)" : finishing ? "FINISHING CURRENT CYCLE" : collect ? "COLLECT / REFILL (EST.)" : extractionWaiting ? "WAITING FOR MATCHING EXTRACTOR OUTPUT" : "NOT STARTED / CHECK INPUTS";
                    row.Color = collect ? "#80BFFF" : routed && (extractingFeed || extractionWaiting || finishing || projected > 0) ? "#74D6C9" : "#FFD166";
                    row.Quantity = string.Join(" + ", recipe.Inputs.Select(i => $"{i.Value:N0} {Type(i.Key).Name}"));
                    row.Rate = string.Join(" + ", recipe.Outputs.Select(i => $"{i.Value * 3600 / recipe.Cycle:N0} {Type(i.Key).Name}/h capacity"));
                    row.Remaining = finishing ? "Finishing current cycle before collection" : collect ? "Input run complete; collect products and reload" : extractionWaiting ? "Extractor active; required material is not supplied by its current routed output" : extractingFeed ? "Routed upstream supply; intermittent processing is normal" : Time(projected * 3600) + " input runway (est.)";
                    var last = Date(pin, "last_cycle_start");
                    row.Next = (projected > 0 || finishing) && last.HasValue ? Time(recipe.Cycle - Math.Max(0, (now - last.Value).TotalSeconds) % recipe.Cycle) + " to cycle (est.)" : extractionWaiting ? "Waiting for matching raw material" : extractingFeed ? "Waiting for upstream cycle" : "Check in game";
                    if (!routed || (!extractingFeed && !extractionWaiting && projected <= 0 && !collect && !finishing)) attention++;
                    if (!extractingFeed && !extractionWaiting) nextAction = Math.Min(nextAction, projected * 3600);
                }
                else
                {
                    double used = stores[id].Sum(c => c.Value * Type(c.Key).Volume);
                    row.Status = type.Group == 1028 ? "CONFIGURE RECIPE" : type.Capacity > 0 ? "STORAGE SNAPSHOT" : "PIN";
                    if (type.Group == 1028) { row.Color = "#FFD166"; attention++; }
                    row.Quantity = string.Join(" | ", stores[id].Select(c => $"{Type(c.Key).Name}: {c.Value:N0}"));
                    row.Remaining = type.Capacity > 0 ? $"{used:N1} / {type.Capacity:N0} m3" : "";
                    row.Rate = type.Capacity > 0 ? $"{used / type.Capacity:P0} full" : "";
                }
                result.Pins.Add(row);
            }
            var snapshotStores = stores;
            var projection = PlanetaryProjection.Build(colony, now);
            stores = projection.Stores;
            foreach(var row in result.Pins.Where(r=>ReferenceEquals(r.Colony,colony)&&r.Status=="STORAGE SNAPSHOT"))
            {
                var pin = pins.First(p=>row.Detail.EndsWith("pin " + (long)Num(p,"pin_id")));
                var content=stores[(long)Num(pin,"pin_id")];
                row.Quantity=string.Join(" | ",content.Where(x=>x.Value>0).Select(x=>$"{Type(x.Key).Name}: {x.Value:N0}"));
                row.Status="STORAGE (EST.)";
                double used=content.Sum(x=>x.Value*Type(x.Key).Volume),cap=Type((int)Num(pin,"type_id")).Capacity;
                row.Remaining=$"{used:N1} / {cap:N0} m3 estimated";row.Rate=cap>0?$"{used/cap:P0} full (est.)":"";
            }
            foreach (var pin in pins.Where(p => Type((int)Num(p, "type_id")).Group == 1030))
            {
                long id = (long)Num(pin, "pin_id"); double capacity = Type((int)Num(pin, "type_id")).Capacity;
                var inputs = demands.Where(d => d.Key.pin == id && Tier(d.Key.type) == 1 && Type(d.Key.type).Volume > 0).ToArray();
                if (inputs.Length == 0 || capacity <= 0) continue;
                var relevant = inputs.Select(i => i.Key.type).ToHashSet();
                double otherVolume = stores[id].Where(c => !relevant.Contains(c.Key) && !(Tier(c.Key)>=2 && !routes.Any(r=>(long)Num(r,"source_pin_id")==id && (int)Num(r,"content_type_id")==c.Key && factories.ContainsKey((long)Num(r,"destination_pin_id"))))).Sum(c => c.Value * Type(c.Key).Volume);
                double volumePerHour = inputs.Sum(i => i.Value * Type(i.Key.type).Volume);
                double low = 0, high = Math.Max(0, capacity - otherVolume) / volumePerHour;
                for (int step = 0; step < 50; step++)
                {
                    double mid = (low + high) / 2;
                    double used = inputs.Sum(i => Math.Max(stores[id].GetValueOrDefault(i.Key.type), i.Value * mid) * Type(i.Key.type).Volume);
                    if (used + otherVolume <= capacity) low = mid; else high = mid;
                }
                double fullHours = low;
                foreach (var input in inputs)
                    result.Refills.Add(new() { CharacterId = colony.CharacterId, PlanetId = colony.PlanetId, Colony = colony.Character + " | " + colony.Planet, Pin = id, TypeId = input.Key.type, Name = Type(input.Key.type).Name, Snapshot = snapshotStores[id].GetValueOrDefault(input.Key.type), Current = stores[id].GetValueOrDefault(input.Key.type), Target = Math.Floor(input.Value * fullHours) });
            }
            var factoryRows = result.Pins.Where(r => ReferenceEquals(r.Colony, colony) && r.IsFactory).ToArray();
            int collection = factoryRows.Count(r => r.Status.StartsWith("COLLECT"));
            if (factoryRows.Length > 0)
            {
                var outputs = factories.Values.SelectMany(r => r.Outputs.Keys).Distinct().ToArray();
                foreach (int product in outputs.Where(p => Tier(p) > 0))
                {
                    if (!factoryProducts.TryGetValue(product, out var total)) factoryProducts[product] = total = new() { TypeId=product, Name=Type(product).Name, Tier=Tier(product) };
                    var producers = factories.Where(f => f.Value.Outputs.ContainsKey(product)).ToArray();
                    total.Factories += producers.Length;
                    // Capacity is deliberately not actual throughput: input availability and cycle timing can limit it.
                    total.Capacity += producers.Sum(f => f.Value.Outputs[product] * 3600 / f.Value.Cycle);
                    total.SnapshotOldest = total.SnapshotOldest == default || colony.LastUpdate < total.SnapshotOldest ? colony.LastUpdate : total.SnapshotOldest;

                }
                string products = string.Join(" | ", outputs.Select(product => $"{Type(product).Name}: {stores.Values.Sum(c => c.GetValueOrDefault(product)):N0}"));
                result.Factories.Add(new() { Colony = colony, Name = colony.Planet, Icon = colony.Portrait, Detail = colony.Character,
                    Quantity = $"{factoryRows.Length} factories | {collection} collect/refill | {factoryRows.Count(r => r.Color == "#FFD166")} need attention",
                    Status = factoryRows.Any(r => r.Color == "#FFD166") ? "NEEDS ATTENTION" : collection > 0 ? "COLLECT / REFILL (EST.)" : "PRODUCING / WAITING",
                    Color = factoryRows.Any(r => r.Color == "#FFD166") ? "#FFD166" : collection > 0 ? "#80BFFF" : "#74D6C9",
                    Rate = products, Remaining = Time(nextAction), Next = "Stored output snapshot; intermediate products may still be in use" });
            }
            // Count every commodity in storage, including feedstock on planets that do not produce it.
            foreach (var store in stores.Where(k => Type((int)Num(byId[k.Key], "type_id")).Capacity > 0 || factories.ContainsKey(k.Key)))
                foreach (var item in store.Value.Where(x=>Tier(x.Key)>0))
                {
                    if(!factoryProducts.TryGetValue(item.Key,out var total))factoryProducts[item.Key]=total=new(){TypeId=item.Key,Name=Type(item.Key).Name,Tier=Tier(item.Key)};
                    total.Stored+=item.Value;
                    total.Snapshot+=snapshotStores[store.Key].GetValueOrDefault(item.Key);
                    total.SnapshotOldest=total.SnapshotOldest==default||colony.LastUpdate<total.SnapshotOldest?colony.LastUpdate:total.SnapshotOldest;
                    if(factories.ContainsKey(store.Key) || routes.Any(r=>(long)Num(r,"source_pin_id")==store.Key&&(int)Num(r,"content_type_id")==item.Key&&factories.TryGetValue((long)Num(r,"destination_pin_id"),out var consumer)&&consumer.Inputs.ContainsKey(item.Key)))total.Reserved+=item.Value;
                }
            if(extractors>0)
            {
                var haul=result.Hauls.FirstOrDefault(h=>h.CharacterId==colony.CharacterId);
                if(haul==null){haul=new(){CharacterId=colony.CharacterId,Character=colony.Character};result.Hauls.Add(haul);}
                haul.Volume+=stores.Where(k=>Type((int)Num(byId[k.Key],"type_id")).Capacity>0).Sum(k=>k.Value.Where(x=>Tier(x.Key)==1 && !routes.Any(r=>(long)Num(r,"source_pin_id")==k.Key && (int)Num(r,"content_type_id")==x.Key && factories.ContainsKey((long)Num(r,"destination_pin_id")))).Sum(x=>x.Value*Type(x.Key).Volume));
            }
            result.Colonies.Add(new() { Colony = colony, Name = colony.Planet, Icon = colony.Portrait, Detail = colony.Character + " | " + colony.PlanetType,
                Status = colony.Error.Length > 0 ? "STALE / REFRESH FAILED" : attention > 0 ? $"{attention} NEED ATTENTION" : collection > 0 ? $"{collection} COLLECT / REFILL (EST.)" : "MONITORING",
                Color = attention > 0 || colony.Error.Length > 0 ? "#FFD166" : collection > 0 ? "#80BFFF" : "#74D6C9",
                Quantity = $"{extractors} extractors | {factories.Count} factories", Remaining = Time(nextAction), SecondsUntilAction=double.IsFinite(nextAction)?nextAction:null,
                Next = (projection.Limited ? "Projection capped; verify in game | " : "Projected contents; verify before hauling | ") + "Snapshot updated " + colony.LastUpdate.ToLocalTime().ToString("dd MMM HH:mm") + " | Last checked " + colony.Fetched.ToLocalTime().ToString("dd MMM HH:mm") + (now - colony.LastUpdate > TimeSpan.FromHours(6) ? " | Older snapshot: open colony in EVE to update amounts" : ""), Rate = "Fetched " + colony.Fetched.ToLocalTime().ToString("dd MMM HH:mm") });
        }
        foreach (var item in production.Where(p => p.Key > 0)) result.Production.Add(new() { Name = Type(item.Key).Name, Icon = Type(item.Key).Icon, Rate = $"{item.Value.rate:N0} nominal units/h", Quantity = $"{item.Value.extracted:N0} projected units", Detail = "Current extractor programs only; nominal cycle yield, not a mined ledger" });
        result.FactoryTiers = factoryProducts.Values.GroupBy(p => p.Tier).OrderBy(g => g.Key)
            .Select(g => new PiTierSummary { Tier=g.Key, Products=g.OrderByDescending(p => p.Collect > 0).ThenByDescending(p => p.Collect).ThenBy(p => p.Name).ToList() }).ToList();
        var available = new Dictionary<int, double>(stock);
        foreach (var refill in result.Refills)
        {
            refill.StockAvailable = available.GetValueOrDefault(refill.TypeId);
            refill.Allocated = Math.Min(refill.Need, refill.StockAvailable);
            available[refill.TypeId] = available.GetValueOrDefault(refill.TypeId) - refill.Allocated;
        }
        result.StockBudget=result.Refills.GroupBy(r=>r.TypeId).Select(g=>new PiStockBudget{Name=Type(g.Key).Name,Icon=Type(g.Key).Icon,Available=stock.GetValueOrDefault(g.Key),Required=g.Sum(r=>r.Need)}).OrderBy(x=>x.Name).ToList();
        return result;
    }
}
