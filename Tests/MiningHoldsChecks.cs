using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;
using System.Windows;
using System.Windows.Controls;
using System.Reflection;

internal static partial class Program
{
    private static void CheckMiningHolds(string? render = null)
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var hull = new EveUniverseType { DogmaAttributes = new()
        {
            new() { AttributeId = 1556, Value = 31500 },
            new() { AttributeId = 3187, Value = 5 },
            new() { AttributeId = 3198, Value = 2.5 }
        }};
        Check(MiningHoldCapacity.For(hull, new[] {
            new EveSkillEntry {SkillId=17940, ActiveSkillLevel=5},
            new EveSkillEntry {SkillId=22551, ActiveSkillLevel=5}}) == 44296.875,
            "Mackinaw hold applies both ship-sourced skill bonuses");
        Check(MiningHoldCapacity.For(new(), Array.Empty<EveSkillEntry>()) == 0, "Missing capacity never defaults to a Hulk hold");
        Check(MiningHoldCapacity.IsExhumer(22544) && MiningHoldCapacity.IsExhumer(22546) && MiningHoldCapacity.IsExhumer(22548) &&
            !MiningHoldCapacity.IsExhumer(28606), "Live mining holds are scoped to the three exhumers");
        Check(MiningHoldCapacity.HasLiveMiningHold(28606), "Orca exposes a live mining hold as well as cached fleet storage");
        Check(MiningHoldCapacity.For(new EveUniverseType { DogmaAttributes = new() {
            new() { AttributeId=1556, Value=150000 }, new() { AttributeId=3212, Value=5 } } },
            new[] { new EveSkillEntry { SkillId=29637, ActiveSkillLevel=5 } }) == 187500,
            "Orca mining capacity includes Industrial Command Ships bonus from dogma");
        var tracker = new LiveMiningHold();
        tracker.Configure("A", 42, now);
        (double, double) Volumes(string _) => (10, .1);
        void Add(string kind, long units = 0) => tracker.Record(new("A", now.AddSeconds(1), kind, "Ore", units), now.AddSeconds(1));
        Add("mined", 100);
        Check(tracker.Present("A", 10000, Volumes).Percent == 10, "Live mining adds physical volume");
        Add("compress", 100);
        Check(Math.Abs(tracker.Present("A", 10000, Volumes).Percent - .1) < .0001, "Compression replaces raw volume with compressed volume");
        Add("full");
        Check(tracker.Present("A", 10000, Volumes).Percent == 100, "Full notification anchors to actual ship capacity");
        Add("compress", 100);
        Check(Math.Abs(tracker.Present("A", 10000, Volumes).Percent - 90.1) < .0001, "Compression reduces an anchored full hold");
        tracker.Empty("A", now.AddSeconds(2));
        Check(tracker.Present("A", 10000, Volumes).Percent == 0, "Explicit empty correction resets accumulated contents");
        Add("mined", 100);
        Check(tracker.Present("A", 10000, Volumes).Percent == 0, "Old events cannot resurrect unloaded ore");
        tracker.Configure("A", 43, now);
        Add("mined", 100);
        Check(tracker.Present("A", 10000, _ => (0, 0)).VolumeText == "Waiting for volumes", "Missing volumes remain unknown rather than showing a false empty hold");
        Check(tracker.Present("A", 0, Volumes).VolumeText == "Capacity unavailable", "Missing capacity is explicit");
        tracker.Configure("A", 44, now);
        Check(tracker.Present("A", 10000, Volumes).Percent == 0, "Changing ship resets its estimate");
        tracker.Record(new("A", now.AddSeconds(1), "mined", "Ore", 100), now.AddMinutes(10));
        Check(tracker.Present("A", 10000, Volumes).Percent == 0, "Replayed stale log lines do not fill the current ship");
        Check(LiveMiningHold.Parse("[ 2026.10.20 12:00:00 ] (notify) Successfully compressed Brimful Sylvite into 1,234 Compressed Brimful Sylvite.", "A") is { Kind: "compress", Units: 1234, Ore: "Brimful Sylvite" },
            "Real compression log format preserves resource and units");
        Check(LiveMiningHold.Parse("[ 2026.10.20 12:00:00 ] (notify) Other Pilot compressed 123 Ore using your compression services.", "A") == null,
            "Other pilots using an Orca compressor do not change its own hold");
        Check(LiveMiningHold.Parse("[ 2026.10.20 12:00:00 ] (notify) Ship's cargo hold is full.", "A")?.Kind == "full",
            "Full-hold notification format is recognised");
        Check(LiveMiningHold.Parse("[ 2026.10.20 12:00:00 ] (chat) Ship's cargo hold is full.", "A") == null, "Chat text cannot anchor inventory");
        var pulls = new List<MiningStatusRings.Pull>();
        for (int n = 0; n < 6; n++)
        {
            pulls.Add(new(now.AddSeconds(n * 12), 80, "Ore", false));
            pulls.Add(new(now.AddSeconds(n * 12 + 2), 90, "Ore", false));
        }
        var healthy = MiningStatusRings.Build(2, false, false, pulls, now.AddSeconds(64));
        Check(healthy.All(r => r.Accent == "#65D5B6"), "Independent regular streams learn separate green rings");
        pulls.Add(new(now.AddSeconds(72), 80, "Ore", false));
        pulls.Add(new(now.AddSeconds(84), 80, "Ore", false));
        var stopped = MiningStatusRings.Build(2, false, false, pulls, now.AddSeconds(85));
        Check(stopped[0].Accent == "#65D5B6" && stopped[1].Accent == "#E88991", "Only the stopped stream turns red");
        pulls.Add(new(now.AddSeconds(86), 90, "Ore", false));
        Check(MiningStatusRings.Build(2, false, false, pulls, now.AddSeconds(87)).All(r => r.Accent == "#65D5B6"),
            "Resuming stream recovers its own ring");
        Check(MiningStatusRings.Build(2, false, true, pulls, now.AddSeconds(120)).All(r => r.Accent == "#829C9F"), "Muted rings do not show alarm red");
        Check(MiningStatusRings.Build(2, true, false, pulls, now).Count == 0 &&
            MiningStatusRings.Build(-1, false, false, pulls, now).Count == 0, "Orca drones and unknown fits do not invent laser rings");
        Check(MiningStatusRings.Build(1, false, false, Array.Empty<MiningStatusRings.Pull>(), now).Single().Timing == "Learning", "Single-laser learning never invents a second ring");
        var critical = pulls.Take(12).Append(new MiningStatusRings.Pull(now.AddSeconds(72), 800, "Ore", true));
        Check(MiningStatusRings.Build(2, false, false, critical, now.AddSeconds(73))[0].Accent == "#65D5B6", "Critical pull advances a uniquely matching stream without changing its yield baseline");
        var identical = Enumerable.Range(0, 6).SelectMany(n => new[] {
            new MiningStatusRings.Pull(now.AddSeconds(n * 12), 80, "Ore", false),
            new MiningStatusRings.Pull(now.AddSeconds(n * 12), 80, "Ore", false)});
        Check(MiningStatusRings.Build(2, false, false, identical, now.AddSeconds(65)).All(r => r.CycleText == "12s"),
            "Coincident identical pulls learn their common cycle without inventing physical slot identity");
        Check(MiningStatusRings.Build(2, false, false, pulls, now.AddMinutes(20)).All(r => r.Timing == "Learning"),
            "Stale streams expire rather than producing indefinite inferred alarms");
        var mixedOre = Enumerable.Range(0, 8).SelectMany(n => new[] {
            new MiningStatusRings.Pull(now.AddSeconds(n * 24), n == 5 ? 53 : 54, "Bitumens", false),
            new MiningStatusRings.Pull(now.AddSeconds(n * 24), 162, "Bitumens", true),
            new MiningStatusRings.Pull(now.AddSeconds(n * 24 + 1), 54, "Brimful Bitumens", false),
            new MiningStatusRings.Pull(now.AddSeconds(n * 24 + 1), 162, "Brimful Bitumens", true) }).ToArray();
        var mixedRows = MiningStatusRings.Build(2, false, false, mixedOre, now.AddSeconds(170));
        Check(mixedRows.All(r => r.CycleText == "24s" && r.Accent == "#65D5B6") && mixedRows.Select(r => r.OreText).Distinct().Count() == 2,
            "ORE strip miners learn alternating ore streams despite supplemental critical yields and unit rounding");
        var oneContinues = mixedOre.Append(new MiningStatusRings.Pull(now.AddSeconds(192), 54, "Brimful Bitumens", false))
            .Append(new MiningStatusRings.Pull(now.AddSeconds(216), 54, "Brimful Bitumens", false));
        var mixedStopped = MiningStatusRings.Build(2, false, false, oneContinues, now.AddSeconds(217));
        Check(mixedStopped.Count(r => r.Accent == "#E88991") == 1 && mixedStopped.Count(r => r.Accent == "#65D5B6") == 1,
            "Only the stopped ore stream turns red while the other resource continues");
        var singleRunning=Enumerable.Range(0,16).Select(n=>new MiningStatusRings.Pull(now.AddSeconds(n*24),54,"Bitumens",false));
        var singleRows=MiningStatusRings.Build(2,false,false,singleRunning,now.AddSeconds(361));
        Check(singleRows.Count(r=>r.Accent=="#65D5B6")==1 && singleRows.Count(r=>r.Timing=="Learning")==1,
            "Two fitted modules alone do not invent a second active stream");
        foreach (int stagger in new[] {8, 11, 13, 16})
        {
            var phased = Enumerable.Range(0, 16).SelectMany(n=>new[] {
                new MiningStatusRings.Pull(now.AddSeconds(n*24),54,"Bitumens",false),
                new MiningStatusRings.Pull(now.AddSeconds(n*24+stagger),54,"Bitumens",false) }).ToArray();
            var rows = MiningStatusRings.Build(2,false,false,phased,now.AddSeconds(16*24-1));
            Check(rows.All(r=>r.CycleText=="24s" && r.Accent=="#65D5B6"),
                $"Staggered identical miners ({stagger}s offset) both retain their full cycle");
            var continuing = phased.Concat(Enumerable.Range(16,4).Select(n=>new MiningStatusRings.Pull(now.AddSeconds(n*24),54,"Bitumens",false)));
            rows=MiningStatusRings.Build(2,false,false,continuing,now.AddSeconds(19*24+1));
            Check(rows.Count(r=>r.Accent=="#E88991")==1 && rows.Count(r=>r.Accent=="#65D5B6")==1,
                $"Staggered miners ({stagger}s offset) keep the stopped stream separate");
        }
        int[] replay0 = new[] {0,13,24,37,48,62,72,86,96,110,120,134,144,159,170,183,194,207,218,231,242,255,266,279,290,303,315,327,339,351,363,375,387,400,411,424,436,448,460};
        int[] replay1 = new[] {0,8,24,33,48,57,72,81,96,105,120,129,145,154,170,178,194,202,218,226,242,250,266,274,290,298,315,323,339,347,363,371,387,396,412,420,436,444,460,469};
        foreach(var offsets in new[] {replay0,replay1})
        {
            var replay=offsets.Select(t=>new MiningStatusRings.Pull(now.AddSeconds(t),54,"Bitumens",false)).ToArray();
            bool stable=true;
            for(int second=120;second<=offsets[^1];second++)
            {
                var rows=MiningStatusRings.Build(2,false,false,replay,now.AddSeconds(second));
                stable &= rows.All(r=>r.Accent=="#65D5B6" && double.TryParse(r.CycleText.TrimEnd('s'), out var cycle) && cycle>=23 && cycle<=25);
            }
            Check(stable,"Recorded staggered mining cadence remains green and visible at every replayed second");
        }
        var ship = new EveCurrentShipView {ShipItemId=100, ShipTypeId=28606};
        var assets = new List<EveAssetItem> {
            new() {ItemId=100, TypeId=28606},
            new() {ItemId=200, TypeId=1, LocationId=100, LocationFlag="FleetHangar", Quantity=3, IsSingleton=false},
            new() {ItemId=201, TypeId=1, LocationId=100, LocationFlag="FleetHangar", Quantity=1, IsSingleton=true},
            new() {ItemId=202, TypeId=1, LocationId=200, LocationFlag="Unlocked", Quantity=100},
            new() {ItemId=203, TypeId=1, LocationId=100, LocationFlag="Cargo", Quantity=100}
        };
        Task<EveUniverseType> Type(int id) => Task.FromResult(id == 28606
            ? new EveUniverseType {DogmaAttributes=new() {new() {AttributeId=912,Value=40000}}}
            : new EveUniverseType {Volume=10,PackagedVolume=2});
        var fleet = MiningHoldSnapshots.ReadAsync(ship, assets, now, Type).GetAwaiter().GetResult();
        Check(fleet.Complete && fleet.UsedM3 == 16 && fleet.BaseCapacityM3 == 40000,
            "Fleet snapshot uses direct hold contents and packaged/singleton volume without nested double counting");
        assets.Add(new() {ItemId=204,TypeId=1,LocationId=100,LocationFlag="SpecializedOreHold",Quantity=500,IsSingleton=false});
        Task<EveUniverseType> MiningType(int id) => id==28606 ? Task.FromResult(new EveUniverseType {DogmaAttributes=new() {new() {AttributeId=1556,Value=150000}}}) : Type(id);
        var miningInventory=MiningHoldSnapshots.ReadAsync(ship,assets,now,MiningType,"SpecializedOreHold",1556).GetAwaiter().GetResult();
        Check(miningInventory.Complete && miningInventory.UsedM3==1000 && miningInventory.BaseCapacityM3==150000,
            "Orca mining inventory includes compressed cargo already aboard and excludes fleet/cargo holds");
        assets.RemoveAt(0);
        Check(!MiningHoldSnapshots.ReadAsync(ship, assets, now, Type).GetAwaiter().GetResult().Complete,
            "Ship absent from snapshot cannot be declared an empty fleet hangar");
        var unknown = MiningHoldSnapshots.Present("Fleet", new(), now);
        Check(unknown.VolumeText == "Unavailable", "Unobserved Orca fleet hold is not reported empty");
        var stale = MiningHoldSnapshots.Present("Fleet", new() {Complete=true, UsedM3=100, BaseCapacityM3=40000, ObservedUtc=now.AddHours(-1)}, now);
        Check(stale.VolumeText.StartsWith("Old:") && stale.Accent == "#829C9F", "Stale fleet inventory remains visibly stale");
        if (render == null) return;

        var window = CheckCharacterOverview();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var prefs = (MiningDashboardPreferences)typeof(MiningFleetOverviewWindow).GetField("_prefs", flags)!.GetValue(window)!;
        prefs.CharacterOverviewMiningMode = true;
        prefs.FleetOverviewAutoFit = false;
        var intel = (Dictionary<string, EveMiningShipIntel>)typeof(MiningFleetOverviewWindow).GetField("_pilotIntel", flags)!.GetValue(window)!;
        intel["Pilot A"] = new() {CurrentShip=new() {TypeName="Orca",ShipTypeId=28606,ShipItemId=1},MiningLaserCount=0,MiningHoldCapacityM3=187500};
        intel["Pilot B"] = new() {CurrentShip=new() {TypeName="Mackinaw",ShipTypeId=22548,ShipItemId=2},MiningLaserCount=2,MiningHoldCapacityM3=44296.875};
        typeof(MiningFleetOverviewWindow).GetMethod("RefreshCards", flags)!.Invoke(window, null);
        var items = (ItemsControl)window.FindName("MinerItems");
        foreach (var card in items.Items)
        {
            var type = card.GetType();
            type.GetProperty("DetailedMiningVisibility")!.SetValue(card, Visibility.Visible);
            type.GetProperty("PreviewVisibility")!.SetValue(card, Visibility.Collapsed);
            type.GetProperty("MiningVisibility")!.SetValue(card, Visibility.Visible);
            type.GetProperty("CombatVisibility")!.SetValue(card, Visibility.Collapsed);
            type.GetProperty("StatusRings")!.SetValue(card, stopped.Select(r => r with { CrystalText = "ORE", OreText = "Brimful Bitumens" }).ToArray());
            type.GetProperty("RingsVisibility")!.SetValue(card, Visibility.Visible);
            type.GetProperty("LaserFallbackVisibility")!.SetValue(card, Visibility.Collapsed);
            type.GetProperty("HoldRows")!.SetValue(card, new[] {new MiningHoldSnapshots.Row("Mining","~9,250 / 11,500 m\u00b3",80.4,"#E0BA68","Live estimate")});
        }
        var orcaCard = items.Items[0]; var orcaType = orcaCard.GetType();
        orcaType.GetProperty("StatusRings")!.SetValue(orcaCard, Array.Empty<MiningStatusRings.Row>());
        orcaType.GetProperty("RingsVisibility")!.SetValue(orcaCard, Visibility.Collapsed);
        orcaType.GetProperty("LaserFallbackVisibility")!.SetValue(orcaCard, Visibility.Visible);
        orcaType.GetProperty("LaserText")!.SetValue(orcaCard, "DRONE MINING");
        orcaType.GetProperty("HoldVisibility")!.SetValue(orcaCard, Visibility.Visible);
        orcaType.GetProperty("HoldRows")!.SetValue(orcaCard, new[] {
            new MiningHoldSnapshots.Row("Mining", "~12,500 / 187,500 m\u00b3", 6.67, "#65D5B6", "Live estimate"),
            new MiningHoldSnapshots.Row("Fleet", "Cached: 30,772 / 40,000 m\u00b3", 76.93, "#65D5B6", "Cached inventory") });
        items.Items.Refresh();
        window.Width = 800;
        Render(window, render);
        IEnumerable<DependencyObject> Walk(DependencyObject parent)
        {
            for (int i=0; i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i); yield return child;
                foreach(var nested in Walk(child)) yield return nested;
            }
        }
        var frames=Walk(items).OfType<Border>().Where(b=>b.CornerRadius.TopLeft==7 && b.ActualWidth>150).ToArray();
        Check(frames.Length>=2 && frames.Max(b=>b.ActualHeight)-frames.Min(b=>b.ActualHeight)<1,
            "Orca with two holds and exhumer with rings have equal rendered card heights");
        var pills=Walk(items).OfType<Border>().Where(b=>b.CornerRadius.TopLeft==8 && b.DataContext is MiningStatusRings.Row).ToArray();
        Check(pills.Length==2 && Math.Abs(pills[0].TranslatePoint(new Point(), items).Y-pills[1].TranslatePoint(new Point(), items).Y)<1,
            "Both mining rings fit side by side at normal card width");
        var miningBars=Walk(items).OfType<Border>().Where(b=>b.CornerRadius.TopLeft==4 && b.DataContext is MiningHoldSnapshots.Row r && r.Label=="Mining").ToArray();
        Check(miningBars.Length==2 && pills.Length==2 && miningBars[1].TranslatePoint(new Point(), items).Y-(pills[0].TranslatePoint(new Point(), items).Y+pills[0].ActualHeight)<24,
            "Mining hold sits directly below the rings without a reserved second-hold gap");
        Check(Walk(items).OfType<TextBlock>().Any(t=>t.Text.StartsWith("Cached: 30,772")),
            "Compact Orca still displays its separately labelled fleet inventory");
        window.Close();
    }
}
