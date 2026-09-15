using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EveCommandCenter.Services;
using EveCommandCenter.Views;
internal static partial class Program
{
    private static MiningFleetOverviewWindow CheckCharacterOverview()
    {
        var tracker=new StatTrackerService();
        var prefs=new MiningDashboardPreferences{CombinedCharacterOverview=true};
        var clients=new[]{new EveWindow(IntPtr.Zero,"EVE - Pilot A","Pilot A"),new EveWindow(IntPtr.Zero,"EVE - Pilot B","Pilot B"),new EveWindow(IntPtr.Zero,"EVE - Pilot C","Pilot C")};
        var window=new MiningFleetOverviewWindow(tracker,new MiningIdleWatchdogService(tracker),prefs,()=>clients);
        BackgroundOperations.Stop();
        void Refresh()=>typeof(MiningFleetOverviewWindow).GetMethod("RefreshCards",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null);
        var items=(ItemsControl)window.FindName("MinerItems");
        Refresh();
        Check(items.Items.Count==3,"Combined overview includes open clients without any mining pulls");
        Visibility State(object card,string name)=>(Visibility)card.GetType().GetProperty(name)!.GetValue(card)!;
        Check(items.Items.Cast<object>().All(c=>State(c,"MiningVisibility")==Visibility.Collapsed&&State(c,"PreviewVisibility")==Visibility.Visible),"Character mode hides mining rows and exposes compact previews");
        prefs.CharacterOverviewMiningMode=true;Refresh();
        Check(items.Items.Count==3&&items.Items.Cast<object>().All(c=>State(c,"MiningVisibility")==Visibility.Visible&&State(c,"PreviewVisibility")==Visibility.Collapsed),"Mining mode keeps idle clients clickable while showing their mining details");
        clients=clients.Take(2).ToArray();Refresh();
        Check(items.Items.Count==2,"Closed clients leave the combined overview");
        prefs.CombinedCharacterOverview=false;Refresh();
        Check(items.Items.Count==0,"Separate mode preserves the existing mining-only filter");
        prefs.CombinedCharacterOverview=true;prefs.CharacterOverviewMiningMode=false;Refresh();
        foreach(var name in new[]{"_timer","_pilotIntelTimer","_plexMarketTimer"})((DispatcherTimer)typeof(MiningFleetOverviewWindow).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!).Stop();
        return window;
    }
}
