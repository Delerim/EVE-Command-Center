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
        var rows = MoonDrillSchedule.Build(state,now);
        Check(rows.Count==6 && rows.Count(r=>r.NotSet)==2,"Drill schedule includes idle drills and ignores historical pulls as current schedules");
        var active=rows.Single(r=>r.StructureId==1);
        Check(active.Status=="RUNNING" && active.Duration=="7d 0h 0m" && active.Elapsed=="2d 0h 0m" && active.Remaining=="5d 0h 0m","Drill schedule computes full duration, elapsed and remaining time");
        Check(rows.Single(r=>r.StructureId==2).Status=="READY TO FRACTURE" && rows.Single(r=>r.StructureId==2).Remaining=="0d 0h 0m","Arrived chunks remain set and ready rather than overdue or unset");
        Check(rows.Single(r=>r.StructureId==4).Status=="SCHEDULED" && rows.Single(r=>r.StructureId==4).Elapsed=="0d 0h 0m","Future starts do not produce negative elapsed time");
        Check(rows.Single(r=>r.StructureId==6).Status=="NOT VISIBLE","Missing structures are distinguished from unset drills");
        Check(rows[0].NotSet,"Unset drills sort first");
        state.LastRefreshUtc=null;
        Check(MoonDrillSchedule.Build(state,now).Single(r=>r.StructureId==5).Status=="NOT CHECKED","Unrefreshed data cannot assert that an extraction is not set");
    }
}
