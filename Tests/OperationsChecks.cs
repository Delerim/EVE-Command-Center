using System.Reflection;
using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static async Task CheckSaberlashFit()
    {
        // Independent fixture from the supplied Skiff fit: 3 MLUs, extender,
        // 2 multispectrums, EM amplifier, 2 CDFE II rigs and MC-805 implant.
        EveUniverseType Type(int id, string name, int group, params (int Id, double Value)[] attrs) => new()
        {
            TypeId = id, Name = name, GroupId = group,
            DogmaAttributes = attrs.Select(a => new EveDogmaAttributeValue { AttributeId = a.Id, Value = a.Value }).ToList()
        };
        var sso = new EveSsoService();
        var types = (Dictionary<int, EveUniverseType>)typeof(EveSsoService).GetField("_typeDetails", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(sso)!;
        types[22546] = Type(22546, "Skiff", 543, (263,6500), (265,6000), (9,6500), (271,1), (272,.5), (273,.6), (274,.8), (267,.4), (268,.9), (269,.75), (270,.65), (113,.67), (111,.67), (109,.67), (110,.67));
        types[1] = Type(1, "Large F-S9 Regolith Compact Shield Extender", 38, (72,2200));
        types[2] = Type(2, "Multispectrum Shield Hardener II", 77, (984,-32.5), (985,-32.5), (986,-32.5), (987,-32.5));
        types[3] = Type(3, "'Prospector' EM Shield Amplifier", 295, (984,-37.5));
        types[4] = Type(4, "Medium Core Defense Field Extender II", 0, (337,20));
        types[5] = Type(5, "Mining Laser Upgrade II", 0);
        var skills = new EveSkillsResponse { Skills = new[] { 3419,3394,3392,17940,22551,12365 }.Select(id => new EveSkillEntry { SkillId=id, ActiveSkillLevel=5 }).ToList() };
        var implant = Type(6, "Inherent Implants 'Noble' Mechanic MC-805", 0, (327,5));
        var calculate = typeof(EveSsoService).GetMethod("CalculateFitDefenseAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var unboosted = await (Task<EveFitDefenseStats>)calculate.Invoke(sso, new object[] {22546, new[]{1,2,2,3,4,4,5,5,5}, skills, new[]{implant}, CancellationToken.None})!;
        var fit = unboosted.ApplyShieldCommandBoost(19.6875, 0);
        Check(Math.Abs(fit.OmniEhp - 122833) < 3, "Saberlash complete fit reproduces the 122833 EHP screenshot");
        Check(Math.Abs(fit.ShieldHp - 24365) < 1 && Math.Abs(fit.StructureHp - 8531) < 1, "Skills, duplicate rigs and MC-805 produce the screenshot HP buffers");
        Check(Math.Abs(fit.ShieldAverageResonance-unboosted.ShieldAverageResonance)<1e-12, "Capacity-only burst leaves fitted resistances unchanged");
        Check(Math.Abs(EveFitDefenseStats.StackedResonance(1,new[]{-50d,-50d}) - .5*(1-.5*.86911998)) < 1e-8, "Second resistance module uses the independent 0.86911998 reference coefficient");
        var cached = JsonSerializer.Deserialize<EveMiningShipIntel>(JsonSerializer.Serialize(new EveMiningShipIntel { CharacterId=1, CharacterName="Test", SyncedUtc=DateTimeOffset.UtcNow, Defense=unboosted, CurrentShip=new(){ TypeName="Orca" } }))!;
        Check(cached.Defense.Available && cached.SyncedUtc != default && cached.IsOrca, "Overview cache preserves fitting, timestamp and Orca identity");
    }

    private static void CheckCycleAndFuel()
    {
        var now = new DateTimeOffset(2026,9,11,12,0,0,TimeSpan.Zero);
        var state = new MoonReportState();
        MoonPullRecord Pull(string id, long structure, int days, bool jackpot=false) => new() { Id=id, StructureId=structure, StructureName="Anchor "+structure, SystemName=structure==1?"Raren":"Mazitah", FracturedUtc=now.AddDays(days), ChunkArrivalUtc=now.AddDays(days), JackpotObserved=jackpot, MinedM3ByOre=new(){["Zeolites"]=1000} };
        foreach (var pull in new[]{Pull("a",1,-60,true),Pull("b",1,-10),Pull("c",2,-9,true),Pull("d",2,-20,true)}) state.Pulls[pull.Id]=pull;
        state.CycleAnchorStructureId=MoonCycle.ChooseAnchor(state.Pulls.Values);
        var cycle=MoonCycle.Build(state,now,_=>0);
        Check(cycle.Start==now.AddDays(-10) && cycle.Fractured==2 && cycle.Jackpots==1 && cycle.MinedM3==2000, "Cycle totals start at the latest fracture of the persistent Raren anchor");
        state.Pulls["new"]=Pull("new",1,0);
        Check(MoonCycle.Build(state,now,_=>0).Fractured==1, "Next anchor fracture resets cycle totals without deleting history");
        Check(MoonCycle.Build(new(),now,_=>0).Start==null, "Missing Raren history is explicit rather than using all-time counts");
        var structures=new[]{new EsiCorporationStructure{StructureId=1,Name="Low",FuelExpires=now.AddDays(79)},new EsiCorporationStructure{StructureId=2,Name="Empty",FuelExpires=now.AddDays(-1)},new EsiCorporationStructure{StructureId=3,Name="Enough",FuelExpires=now.AddDays(80)},new EsiCorporationStructure{StructureId=4,Name="Unknown"}};
        var alerts=MoonOperatingAlert.Evaluate(structures,Array.Empty<EsiMoonExtraction>(),now);
        Check(alerts.Count==1 && alerts[0].Key=="fuel:all" && alerts[0].Message.StartsWith("2 of 4"), "One combined fuel alert includes only stations strictly below 80 days");
        Check(new StationFuelRow{Now=now}.Status=="UNKNOWN" && !new StationFuelRow{Now=now,Expires=now.AddDays(80)}.NeedsFuel, "Unknown fuel is not empty and the 80-day boundary is respected");
    }
}
