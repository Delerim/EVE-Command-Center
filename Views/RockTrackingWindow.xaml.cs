using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class RockTrackingWindow:Window
{
    private readonly RockTrackingService _service;
    private readonly string _pilot;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(1)};
    public RockTrackingWindow(RockTrackingService service,string pilot,string ore)
    {
        _service=service;_pilot=pilot;InitializeComponent();Heading.Text=pilot+" | ROCK TRACKING";
        var rocks=service.Get(pilot);
        Ore1.Text=rocks[0].Ore.Length>0?rocks[0].Ore:ore;Ore2.Text=rocks[1].Ore.Length>0?rocks[1].Ore:ore;
        Volume1.Text=rocks[0].LeftM3>0?rocks[0].LeftM3.ToString("0.##"):"";Volume2.Text=rocks[1].LeftM3>0?rocks[1].LeftM3.ToString("0.##"):"";
        Share1.Text=rocks[0].Share.ToString();Share2.Text=rocks[1].Share.ToString();
        _timer.Tick+=(_,_)=>Update();_timer.Start();Closed+=(_,_)=>_timer.Stop();Update();
    }
    private void Update(){var r=_service.Get(_pilot);Left1.Text=r[0].Text;Left1.ToolTip=r[0].Note;Left2.Text=r[1].Text;Left2.ToolTip=r[1].Note;if(!_service.Enabled)Status.Text="Tracking is disabled. Enable ROCKS in the overview before setting volumes.";}
    private void Set_Click(object sender,RoutedEventArgs e)
    {
        int lane=int.Parse(((System.Windows.Controls.Button)sender).Tag.ToString()!);
        if(!_service.Enabled){Status.Text="Enable ROCKS in the overview first.";return;}
        if(!double.TryParse((lane==0?Volume1:Volume2).Text,NumberStyles.Number,CultureInfo.CurrentCulture,out double volume)||!double.TryParse((lane==0?Share1:Share2).Text,NumberStyles.Number,CultureInfo.CurrentCulture,out double share)){Status.Text="Enter volume and yield share as numbers.";return;}
        try{_service.Set(_pilot,lane,(lane==0?Ore1:Ore2).Text,volume,share,DateTime.UtcNow);Status.Text=$"Laser {lane+1} reset from your scan. Only subsequent normal mining is deducted.";Update();}catch(Exception ex){Status.Text=ex.Message;}
    }
    private void Pause_Click(object sender,RoutedEventArgs e){int lane=int.Parse(((System.Windows.Controls.Button)sender).Tag.ToString()!);_service.Pause(_pilot,lane);Status.Text=$"Laser {lane+1} paused. Its yield share stays reserved; rescan and set volume to restart.";Update();}
}
