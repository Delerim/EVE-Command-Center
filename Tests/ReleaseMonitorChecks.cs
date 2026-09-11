using System.Reflection;
using System.Text.Json;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static async Task CheckReleaseMonitor()
    {
        UpdateService Release(string version)
        {
            var service = new UpdateService();
            var json = JsonSerializer.Serialize(new { tag_name="v"+version, draft=false, prerelease=false,
                html_url="https://github.com/Delerim/EVE-Command-Center/releases/tag/v"+version, body="Test release",
                assets=new[]{new{name="EVE.Command.Center.exe",browser_download_url="https://github.com/Delerim/EVE-Command-Center/releases/download/v"+version+"/EVE.Command.Center.exe"}} });
            typeof(UpdateService).GetMethod("ReadRelease",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(service,new object[]{json,false});
            return service;
        }
        bool enabled=true, dialogBusy=false, preview=false;
        int calls=0, prompts=0;
        var release=Release("999.0.0");
        using var monitor=new ReleaseMonitor(()=>enabled,()=>preview,_=>{if(dialogBusy)return false;prompts++;return true;},
            (pre,ct)=>{calls++;Check(pre==preview,"Release monitor forwards the prerelease preference");return Task.FromResult<UpdateService?>(release);});
        await monitor.CheckAsync(); await monitor.CheckAsync();
        Check(prompts==1,"A skipped release is prompted only once per running session");
        release=Release("999.1.0"); dialogBusy=true;
        await monitor.CheckAsync();
        Check(prompts==1,"Existing update dialog defers another popup");
        dialogBusy=false; await monitor.CheckAsync();
        Check(prompts==2,"A newer release prompts after the previous dialog closes");
        enabled=false; int before=calls; await monitor.CheckAsync();
        Check(calls==before,"Disabling automatic checks prevents background network requests");
        var pending=new TaskCompletionSource<UpdateService?>(); int inFlightCalls=0;
        using var inFlight=new ReleaseMonitor(()=>true,()=>false,_=>{prompts++;return true;},(_,_)=>{inFlightCalls++;return pending.Task;});
        var first=inFlight.CheckAsync(); await inFlight.CheckAsync();
        Check(inFlightCalls==1,"Timer checks cannot overlap an in-flight request");
        inFlight.Dispose(); pending.SetResult(release); await first;
        Check(prompts==2,"Shutdown suppresses a late release popup");
    }
}
