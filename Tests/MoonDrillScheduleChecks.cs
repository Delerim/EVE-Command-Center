using EveCommandCenter.Models;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckMoonDrillSchedule()
    {
        var now = new DateTimeOffset(2026,9,29,12,0,0,TimeSpan.Zero);
        var state = new MoonReportState { LastRefreshUtc = now };
        for(int id=1;id<=5;id++)
        {
            state.Structures.Add(new EsiCorporationStructure { StructureId=id, Name="Drill "+id, Services=new(){new(){Name="Moon Drilling",State="online"}} });
            state.Profiles[id]=new MoonProfile {StructureId=id,MoonId=id,MoonName="Moon "+id,SystemName="Test"};
        }
        state.Pulls["active"] = new() {StructureId=1,SeenInLatestExtractionList=true,ExtractionStartUtc=now.AddDays(-2),ChunkArrivalUtc=now.AddDays(5)};
        state.Pulls["ready"] = new() {StructureId=2,SeenInLatestExtractionList=true,ExtractionStartUtc=now.AddDays(-7),ChunkArrivalUtc=now.AddHours(-1)};
        state.Pulls["old"] = new() {StructureId=3,SeenInLatestExtractionList=false,ExtractionStartUtc=now.AddDays(-8),ChunkArrivalUtc=now.AddDays(-1)};
        state.Pulls["future"] = new() {StructureId=4,SeenInLatestExtractionList=true,ExtractionStartUtc=now.AddHours(2),ChunkArrivalUtc=now.AddDays(7)};
        state.Profiles[6] = new() {StructureId=6,MoonId=6,MoonName="Removed drill"};
        // No CSV and no ESI discovery: old persisted structures must not appear.
        Check(MoonDrillSchedule.Build(state,now).Count == 0,
            "Legacy local structures remain hidden until fresh ESI discovery");
        // Successful ESI discovery lists idle drills even with no CSV.
        foreach (var profile in state.Profiles.Values)
            state.EsiKnownStructureIds.Add(profile.StructureId);
        var rows = MoonDrillSchedule.Build(state,now);
        Check(rows.Count==6 && rows.Count(r=>r.NotSet)==2,"Drill schedule includes idle drills and ignores historical pulls as current schedules");
        var active=rows.Single(r=>r.StructureId==1);
        Check(active.Status=="RUNNING" && active.Duration=="7d 0h 0m" && active.Elapsed=="2d 0h 0m" && active.Remaining=="5d 0h 0m","Drill schedule computes full duration, elapsed and remaining time");
        Check(rows.Single(r=>r.StructureId==2).Status=="READY TO FRACTURE" && rows.Single(r=>r.StructureId==2).Remaining=="0d 0h 0m","Arrived chunks remain set and ready rather than overdue or unset");
        Check(rows.Single(r=>r.StructureId==4).Status=="SCHEDULED" && rows.Single(r=>r.StructureId==4).Elapsed=="0d 0h 0m","Future starts do not produce negative elapsed time");
        Check(rows.Single(r=>r.StructureId==6).Status=="NOT VISIBLE","Missing structures are distinguished from unset drills");
        Check(rows[0].NotSet,"Unset drills sort first");
        // October must sort before December despite their alphabetical order.
        var oct = new MoonDrillRow(20, "Test", "October", "Test", "RUNNING", "online",
            new DateTimeOffset(2026,10,25,12,0,0,TimeSpan.Zero),
            new DateTimeOffset(2026,10,27,12,0,0,TimeSpan.Zero), "2d 0h 0m", "1d 1h 0m", "0d 23h 0m");
        var dec = oct with { Start = new DateTimeOffset(2026,12,1,12,0,0,TimeSpan.Zero),
            Arrival = new DateTimeOffset(2026,12,5,12,0,0,TimeSpan.Zero),
            Duration = "4d 0h 0m", Remaining = "3d 0h 0m" };
        var byStart = new[] { dec, oct }.OrderBy(x => x.Start).ToArray();
        Check(byStart[0] == oct && byStart[1] == dec &&
              oct.DurationMinutes == 2880 && dec.DurationMinutes == 5760 &&
              oct.ElapsedMinutes == 1500 && oct.RemainingMinutes == 1380,
            "Drill calendar sorts real dates October before December and durations by minutes");
        var dateSorter = new EveCommandCenter.Views.TableSorting.Values("Start",
            System.ComponentModel.ListSortDirection.Ascending);
        var durationSorter = new EveCommandCenter.Views.TableSorting.Values("RemainingMinutes",
            System.ComponentModel.ListSortDirection.Ascending);
        Check(dateSorter.Compare(oct, dec) < 0 && durationSorter.Compare(oct, dec) < 0,
            "Shared table sorter respects typed UTC dates and numeric duration values");
        var refreshed = new MoonReportState { LastRefreshUtc = now.AddHours(-1) };
        var schedules = Enumerable.Range(1, 127).Select(id => new EsiMoonExtraction
        {
            StructureId = id, MoonId = id, ExtractionStartTime = now.AddDays(-2),
            ChunkArrivalTime = now.AddDays(5), NaturalDecayTime = now.AddDays(7)
        }).ToArray();
        MoonReportService.ApplyExtractionSchedules(refreshed, schedules);
        Check(MoonDrillSchedule.Build(refreshed, now).Count(r => r.Status == "RUNNING") == 127,
            "All 127 extraction schedules are visible before asynchronous moon-name enrichment begins");
        MoonReportService.ApplyExtractionSchedules(refreshed, schedules);
        Check(refreshed.Pulls.Count == 127 && refreshed.Pulls.Values.All(p => p.SeenInLatestExtractionList),
            "Repeated extraction refresh preserves all schedules without duplicate pulls");
        MoonReportService.ApplyExtractionSchedules(refreshed, schedules.Take(64).ToArray());
        Check(refreshed.Pulls.Values.Count(p => p.SeenInLatestExtractionList) == 64 && refreshed.Pulls.Count == 127,
            "A complete changed extraction response removes current membership while retaining history");
        state.LastRefreshUtc=null;
        Check(MoonDrillSchedule.Build(state,now).Single(r=>r.StructureId==5).Status=="NOT CHECKED","Unrefreshed data cannot assert that an extraction is not set");
    }
}
