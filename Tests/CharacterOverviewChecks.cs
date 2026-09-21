using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;
internal static partial class Program
{
    private static MiningFleetOverviewWindow CheckCharacterOverview(string finalMode="")
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

        string updatedText =
            ((TextBlock)window.FindName("UpdatedText")).Text;

        Check(
            updatedText.Contains("v") &&
            updatedText.Contains("EVE ") &&
            updatedText.Contains("LOCAL "),
            "Overview header exposes version, EVE time and local time");
        Visibility State(object card,string name)=>(Visibility)card.GetType().GetProperty(name)!.GetValue(card)!;
        Check(items.Items.Cast<object>().All(c=>State(c,"MiningVisibility")==Visibility.Collapsed&&State(c,"PreviewVisibility")==Visibility.Visible),"Character mode hides mining rows and exposes compact previews");
        double FrameHeight() {
            var root=(FrameworkElement)window.Content;
            root.Measure(new System.Windows.Size(1600,double.PositiveInfinity));
            root.Arrange(new Rect(0,0,1600,root.DesiredSize.Height));root.UpdateLayout();
            var presenter=(ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(0);
            return System.Windows.Media.VisualTreeHelper.GetChild(presenter,0) is FrameworkElement element?element.ActualHeight:0;
        }
        double measuredCharacterHeight=FrameHeight();
        var initial=items.Items[0];
        var cardType=initial.GetType();

        Check(
            cardType.GetProperty("CritM3Text") != null,
            "Mining cards expose the latest critical-pull m3 value");

        string[] initialCharacterOrder=items.Items.Cast<object>()
            .Select(c=>(string)cardType.GetProperty("Character")!.GetValue(c)!)
            .ToArray();
        var intel=(Dictionary<string,EveMiningShipIntel>)typeof(MiningFleetOverviewWindow)
            .GetField("_pilotIntel",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
            .GetValue(window)!;
        intel["Pilot C"]=new EveMiningShipIntel {
            CharacterName="Pilot C",
            CurrentShip=new EveCurrentShipView {ShipTypeId=28606,TypeName="Orca"}
        };
        Refresh();
        Check(items.Items.Cast<object>()
                .Select(c=>(string)cardType.GetProperty("Character")!.GetValue(c)!)
                .SequenceEqual(initialCharacterOrder),
            "Combined overview keeps character tile positions stable when live pilot data changes");

        double characterWidth=(double)cardType.GetProperty("CardWidth")!.GetValue(initial)!;
        double characterHeight=(double)cardType.GetProperty("CardMinHeight")!.GetValue(initial)!;
        cardType.GetProperty("Status")!.SetValue(initial,"IDLE");
        Refresh();
        Check(ReferenceEquals(initial,items.Items[0])&&(string)cardType.GetProperty("Status")!.GetValue(initial)! != "IDLE","Refresh updates status in place without destroying hover, tooltip or live preview owners");
        prefs.CharacterOverviewLivePreview=true;Refresh();
        Check(items.Items.Cast<object>().All(c=>(bool)cardType.GetProperty("LivePreview")!.GetValue(c)!),"Live preview preference reaches every character card");
        prefs.CharacterOverviewMiningMode=true;Refresh();
        Check((double)cardType.GetProperty("CardWidth")!.GetValue(items.Items[0])! == characterWidth && (double)cardType.GetProperty("CardMinHeight")!.GetValue(items.Items[0])! == characterHeight,"Character and mining modes share the same frame dimensions");
        Check(items.Items.Cast<object>().All(c=>!(bool)cardType.GetProperty("LivePreview")!.GetValue(c)!),"Mining mode disables native preview surfaces");
        Check(Math.Abs(FrameHeight()-measuredCharacterHeight)<1,"Actual rendered card heights stay unchanged when switching modes");
        Check(items.Items.Count==3&&items.Items.Cast<object>().All(c=>State(c,"MiningVisibility")==Visibility.Visible&&State(c,"PreviewVisibility")==Visibility.Collapsed),"Mining mode keeps idle clients clickable while showing their mining details");
        prefs.CharacterOverviewCombatMode="PVE";Refresh();
        Check(items.Items.Count==3&&items.Items.Cast<object>().All(c=>State(c,"CombatVisibility")==Visibility.Visible&&State(c,"MiningVisibility")==Visibility.Collapsed),"PvE mode includes idle clients and hides mining statistics");
        Check(Math.Abs(FrameHeight()-measuredCharacterHeight)<1,"PvE cards preserve the same measured frame height");
        prefs.CharacterOverviewCombatMode="PVP";Refresh();
        Check(items.Items.Cast<object>().All(c=>!(bool)cardType.GetProperty("LivePreview")!.GetValue(c)!)&&Math.Abs(FrameHeight()-measuredCharacterHeight)<1,"PvP mode keeps frame size and releases live previews");
        prefs.CharacterOverviewCombatMode="";
        clients=clients.Take(2).ToArray();Refresh();
        Check(items.Items.Count==2,"Closed clients leave the combined overview");
        prefs.CombinedCharacterOverview=false;Refresh();
        Check(items.Items.Count==0,"Separate mode preserves the existing mining-only filter");
        prefs.CombinedCharacterOverview=true;prefs.CharacterOverviewMiningMode=false;prefs.CharacterOverviewLivePreview=false;Refresh();
        void SetVisualState(int index,string status,bool active) {
            var current=items.Items[index];
            var next=typeof(object).GetMethod("MemberwiseClone",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(current,null)!;
            cardType.GetProperty("Status")!.SetValue(next,status);cardType.GetProperty("IsActive")!.SetValue(next,active);
            cardType.GetMethod("UpdateFrom")!.Invoke(current,new[]{next});FrameHeight();
        }
        SetVisualState(0,"IDLE",true);
        var border=(Border)System.Windows.Media.VisualTreeHelper.GetChild((ContentPresenter)items.ItemContainerGenerator.ContainerFromIndex(0),0);
        Check(border.BorderBrush.ToString()=="#FFE85C66"&&border.Effect!=null,"An active client's alarm colour remains visible alongside selection feedback");
        SetVisualState(0,"MINING",true);SetVisualState(1,"IDLE",false);
        foreach(var name in new[]{"_timer","_pilotIntelTimer","_plexMarketTimer"})((DispatcherTimer)typeof(MiningFleetOverviewWindow).GetField(name,System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(window)!).Stop();
        if(finalMode.Length>0) {
            string ts=DateTime.UtcNow.ToString("yyyy.MM.dd HH:mm:ss");
            foreach(var pilot in new[]{"Pilot A","Pilot B"}) {
                tracker.Combat.Observe(pilot,$"[ {ts} ] (combat) <color=0xff00ffff><b>18000</b> to target - Heavy Pulse Laser II - Hits");
                tracker.Combat.Observe(pilot,$"[ {ts} ] (combat) <color=0xffcc0000><b>3600</b> from target - Missile - Hits");
                tracker.RecordBounty(pilot,1250000);
            }
            tracker.Combat.Observe("Pilot B",$"[ {ts} ] (combat) Warp scramble attempt from Enemy's Ship to you!");
            prefs.CharacterOverviewCombatMode=finalMode;Refresh();
            typeof(MiningFleetOverviewWindow).GetMethod("ApplyCombinedMode",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(window,null);
        }
        return window;
    }
}
