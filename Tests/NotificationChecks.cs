using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckNotificationCenter()
    {
        var now=DateTimeOffset.UtcNow;var state=new PiState();
        for(int i=1;i<=2;i++)state.Colonies.Add(new(){CharacterId=7,Character="Pilot",PlanetId=i,Planet="Planet "+i,LastUpdate=now,Fetched=now,Layout=JsonSerializer.SerializeToElement(new{pins=new[]{new{pin_id=i,type_id=2848,install_time=now.AddDays(-1),expiry_time=now.AddHours(3),extractor_details=new{cycle_time=60,qty_per_cycle=100,product_type_id=2267}}},routes=Array.Empty<object>()})});
        Check(PlanetaryAlerts.Observe(state,now).Count==1,"PI four-hour warning groups two planets into one toon reminder");
        state=JsonSerializer.Deserialize<PiState>(JsonSerializer.Serialize(state))!;
        Check(PlanetaryAlerts.Observe(state,now.AddMinutes(10)).Count==0,"PI warning stage survives persistence without repeating");
        Check(PlanetaryAlerts.Observe(state,now.AddHours(2.5)).Count==1,"PI one-hour threshold sends one further toon reminder");
        var dir=Path.Combine(Path.GetTempPath(),"ecc-notices-"+Guid.NewGuid());var center=new NotificationCenterService(dir);
        center.SyncPi(state,now);
        Check(center.Items.Count(x=>x.Active)==1&&center.Items[0].Detail.Contains("Planet 2"),"Notification centre includes existing warnings grouped per toon");
        center.MarkRead();center.SyncPi(state,now.AddMinutes(1));
        Check(center.Items.All(x=>x.Read),"PI countdown changes preserve the read state of an existing issue");
        var restored=new NotificationCenterService(dir);
        Check(restored.Items.Count==1&&restored.Items[0].Active,"Current notifications survive restart");
        state.Colonies.Clear();center.SyncPi(state,now);
        Check(center.Items.All(x=>!x.Active),"Resolved PI issues leave current list");
        center.Items.AddRange(Enumerable.Range(0,350).Select(i=>new CenterNotification{Id=i.ToString(),Updated=now}));center.MarkRead();
        Check(center.Items.Count<=300,"Historical notification count is bounded");
        Directory.Delete(dir,true);
    }
}
