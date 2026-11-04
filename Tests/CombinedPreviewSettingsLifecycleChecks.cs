using System;
using System.IO;
using System.Reflection;
using EveCommandCenter.Services;
using Forms=System.Windows.Forms;
internal static partial class Program {
    private static void CheckCombinedPreviewSettingsLifecycle() {
        using var source=new Forms.Form {Text="Synthetic overview source",Width=280,Height=180};
        source.Show();
        using var settings=new SettingsService(Path.Combine(Path.GetTempPath(),"ecc-overview-"+Guid.NewGuid().ToString("N")+".json"));
        using var manager=new OverviewClientService(new WindowDiscoveryService(),settings);
        int windows=Forms.Application.OpenForms.Count;
        typeof(OverviewClientService).GetMethod("OnFound",BindingFlags.Instance|BindingFlags.NonPublic)!
            .Invoke(manager,new object[]{new EveWindow(source.Handle,"EVE - Synthetic","Synthetic")});
        Check(manager.GetHwndForCharacter("Synthetic")==source.Handle,"Overview tracks clients without a standalone preview");
        manager.SetOverviewCapture(true); manager.SetOverviewCapture(false); manager.ReapplySettings();
        Check(Forms.Application.OpenForms.Count==windows,"Snapshot toggles and settings cannot create separate preview windows");
        manager.UpdateCharacterSystem("Synthetic","Raren"); manager.UpdateCharacterSystem("Synthetic","Joppaya");
        Check(manager.GetOverviewTelemetry("Synthetic").SystemTransitionActive,"Combined overview retains system transition telemetry");
        source.Hide();
        manager.IncrementAlertBadge("Synthetic","critical");
        Check(manager.GetOverviewTelemetry("Synthetic").AlertCount==1,"Combined overview retains alert badges without legacy windows");
        Check(typeof(OverviewClientService).Assembly.GetType("EveCommandCenter.Views.ThumbnailWindow")==null,"Standalone preview type is removed from production assembly");
    }
}
