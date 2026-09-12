using System.IO;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckRockTracking()
    {
        var dir=Path.Combine(Path.GetTempPath(),"ecc-rocks-"+Guid.NewGuid());var tracker=new RockTrackingService(dir);var now=DateTime.UtcNow;
        Check(!tracker.Enabled,"Rock tracking is opt-in");
        tracker.Enable(true);tracker.Set("Pilot",0,"Zeolites",1000,1,now);tracker.Set("Pilot",1,"Zeolites",2000,1,now);
        tracker.Record("Pilot","Zeolites",100,false,now.AddSeconds(1));
        Check(tracker.Get("Pilot")[0].LeftM3==950&&tracker.Get("Pilot")[1].LeftM3==1950,"Same-ore laser estimates split normal yield without double subtraction");
        tracker.Record("Pilot","Zeolites",500,true,now.AddSeconds(2));tracker.Record("Pilot","Zeolites",500,false,now.AddSeconds(-1));
        Check(tracker.Get("Pilot")[0].LeftM3==950,"Critical bonus and pre-scan history do not deplete tracked rocks");
        tracker.Pause("Pilot",0);tracker.Record("Pilot","Zeolites",100,false,now.AddSeconds(3));
        Check(tracker.Get("Pilot")[0].LeftM3==950&&tracker.Get("Pilot")[1].LeftM3==1900,"Pausing one lane does not redirect its share to the other rock");
        tracker.Set("Pilot",0,"Coesite",1000,1,now);tracker.Record("Pilot","Coesite",150,false,now.AddSeconds(4));
        Check(tracker.Get("Pilot")[0].LeftM3==850&&tracker.Get("Pilot")[1].LeftM3==1900,"Different ore types route only to matching rock estimates");
        tracker.Record("Pilot","Coesite",2000,false,now.AddSeconds(5));
        Check(tracker.Get("Pilot")[0].LeftM3==0,"Rock estimates clamp at zero");
        tracker.Enable(false);tracker.Record("Pilot","Zeolites",100,false,now.AddSeconds(6));
        Check(tracker.Get("Pilot")[1].LeftM3==1900,"Disabled tracking preserves values without consuming mining logs");
        tracker.Enable(true);tracker.Set("Pilot",0,"Zeolites",1000,1,now);
        var restarted=new RockTrackingService(dir);
        Check(!restarted.Get("Pilot")[0].Active,"Restarted tracking pauses to avoid replaying historical logs");
        tracker.Record("Pilot","Zeolites",null,false,now.AddSeconds(7));
        Check(!tracker.Get("Pilot")[0].Active,"Unavailable ore volume pauses estimates instead of inventing depletion");
        Directory.Delete(dir,true);
    }
}
