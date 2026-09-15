using EveCommandCenter.Services;
internal static partial class Program
{
    private static void CheckCombatTelemetry()
    {
        var now=new DateTime(2026,9,15,20,0,0,DateTimeKind.Utc);var telemetry=new CombatTelemetry(()=>now);
        string Line(string body,DateTime? time=null)=>$"[ {(time??now):yyyy.MM.dd HH:mm:ss} ] (combat) {body}";
        telemetry.Observe("A",Line("<color=0xff00ffff><b>3000</b> to target - Heavy Pulse Laser II - Hits"));
        telemetry.Observe("A",Line("<color=0xffcc0000><b>900</b> from attacker - Missile - Hits"));
        var first=telemetry.Snapshot("a");
        Check(first.OutDps==100&&first.InDps==30&&first.PeakIn==900&&first.LastWeapon=="Heavy Pulse Laser II","Combat rates use logged damage over 30 seconds and retain observed weapon text");
        Check(telemetry.Snapshot("B").OutDps==0&&telemetry.Snapshot("B").AgeSeconds==null,"Combat telemetry stays independent per toon and distinguishes no observations");
        telemetry.Observe("A",Line("Warp scramble attempt from you to Enemy's Ship!"));
        Check(!telemetry.Snapshot("A").RecentThreat,"Outgoing scramble messages never flag incoming tackle");
        telemetry.Observe("A",Line("Warp scramble attempt from Enemy's Ship to you!"));
        Check(telemetry.Snapshot("A").RecentThreat,"Incoming scramble attempt produces a recent observation");
        now=now.AddSeconds(31);
        Check(telemetry.Snapshot("A").OutDps==0&&!telemetry.Snapshot("A").RecentThreat,"Combat rates and warning highlights expire without claiming that tackle ended");
        telemetry.Observe("A",Line("<color=0xff00ffff><b>9000</b> to target",now.AddMinutes(-5)));
        Check(telemetry.Snapshot("A").OutDps==0,"Replayed old combat logs cannot create a current DPS spike");
        telemetry.Observe("A",Line("<color=0xff00ffff><b>999999999999999999999</b> to target"));
        Check(telemetry.Snapshot("A").OutDps==0,"Oversized damage values do not crash telemetry parsing");
        telemetry.Repair("B",600,true);telemetry.Repair("B",300,false);
        Check(telemetry.Snapshot("B").RepIn==20&&telemetry.Snapshot("B").RepOut==10,"Remote repair directions have independent HP-per-second rates");
        var tracker=new StatTrackerService();tracker.RecordDamage("NPC test",2000,false,true);
        Check(tracker.GetBountyRate("NPC test")==0&&tracker.GetBountySession("NPC test")==0,"NPC damage is never counted as bounty ISK");
        tracker.RecordBounty("NPC test",500000);
        Check(tracker.GetBountySession("npc TEST")==500000,"Actual bounty events update the same case-insensitive pilot");
        tracker.RecordRepair("NPC test",1000,true,"capacitor");
        Check(tracker.Combat.Snapshot("NPC test").RepIn==0,"Capacitor transfers do not inflate HP repair rates");
        using(var monitor=new LogMonitorService()) {
            for(int i=0;i<3;i++) {
                monitor.Start(System.IO.Path.GetTempPath(),chatEnabled:false,gameEnabled:false);
                var loop=(Task)typeof(LogMonitorService).GetField("_monitorTask",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(monitor)!;
                Check(!loop.IsCompleted,"Log reader exposes its running loop rather than a completed placeholder");
                monitor.Stop();Check(loop.IsCompletedSuccessfully,"Stopping a log reader completes its real loop before restart");
            }
        }
        telemetry.Remove("a");Check(telemetry.Snapshot("A").AgeSeconds==null,"Removing a pilot releases its combat observations");
    }
}
