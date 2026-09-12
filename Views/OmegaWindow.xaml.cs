using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class OmegaWindow:Window
{
    private readonly OmegaService _service=BackgroundOperations.Current.Omega;
    private readonly EveSsoService _sso=BackgroundOperations.Current.Sso;
    private readonly CancellationTokenSource _life=new();
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMinutes(1)};
    public OmegaWindow(){InitializeComponent();_service.Changed+=Update;_timer.Tick+=(_,_)=>Update();Loaded+=async(_,_)=>{Update();_timer.Start();await _service.RefreshAsync(_life.Token);};Closed+=(_,_)=>{_timer.Stop();_service.Changed-=Update;_life.Cancel();};}
    private void Update()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(Update);return;}
        long id=(Pilots.SelectedItem as OmegaPilot)?.Id??0;
        if(!Pilots.Items.Cast<OmegaPilot>().Select(p=>p.Id).Order().SequenceEqual(_service.Pilots.Select(p=>p.Id).Order()))
        {
            Pilots.ItemsSource=_service.Pilots.OrderBy(p=>p.Expiry??DateTimeOffset.MaxValue).ThenBy(p=>p.Name).ToArray();
            Pilots.SelectedItem=_service.Pilots.FirstOrDefault(p=>p.Id==id);if(Pilots.SelectedItem==null&&Pilots.Items.Count>0)Pilots.SelectedIndex=0;
        }
        else Pilots.Items.Refresh();
        Summary.Text=$"{_service.Pilots.Count} pilots | {_service.Pilots.Count(p=>p.Expiry.HasValue)} dates recorded | {_service.Pilots.Count(p=>p.Expiry<DateTimeOffset.UtcNow.AddDays(7))} recorded dates due within 7 days";
    }
    private void Selected(object sender,SelectionChangedEventArgs e)
    {
        if(Pilots.SelectedItem is not OmegaPilot p)return;Account.Text=p.Account;Expiry.SelectedDate=p.Expiry?.LocalDateTime.Date;Time.Text=p.Expiry?.ToLocalTime().ToString("HH:mm")??"00:00";
        var lines=new List<string>{p.Name+" | Home: "+p.Home,p.Error.Length>0?p.Error:"Clone snapshot "+p.Updated.ToLocalTime().ToString("dd MMM HH:mm")};
        if(p.Clones.ValueKind==System.Text.Json.JsonValueKind.Object&&p.Clones.TryGetProperty("jump_clones",out var clones))
            foreach(var c in clones.EnumerateArray())lines.Add(IndustryCatalog.Text(c,"name")+" | "+IndustryCatalog.Text(c,"location_type")+" "+IndustryCatalog.Num(c,"location_id")+" | Implants: "+(c.TryGetProperty("implants",out var implants)?string.Join(", ",implants.EnumerateArray().Select(i=>IndustryCatalog.Name(i.GetInt32()))):"None"));
        Clones.Text=string.Join(Environment.NewLine+Environment.NewLine,lines);
    }
    private void Save_Click(object sender,RoutedEventArgs e)
    {
        if(Pilots.SelectedItem is not OmegaPilot p||Expiry.SelectedDate is not {} date){StatusText.Text="Select a pilot and enter the expiry from your launcher.";return;}
        if(!TimeSpan.TryParseExact(Time.Text,@"hh\:mm",System.Globalization.CultureInfo.InvariantCulture,out var time)){StatusText.Text="Use a local time in HH:mm format.";return;}
        p.Account=Account.Text.Trim();p.Expiry=new DateTimeOffset(DateTime.SpecifyKind(date.Date+time,DateTimeKind.Local));p.ReportedStatus="Omega";
        if(p.Account.Length>0)foreach(var other in _service.Pilots.Where(x=>x.Account.Equals(p.Account,StringComparison.OrdinalIgnoreCase))){other.Expiry=p.Expiry;other.ReportedStatus=p.ReportedStatus;}
        _service.Save();Update();StatusText.Text="Manual subscription date saved. Verify it in the launcher after renewals.";
    }
    private void Clear_Click(object sender,RoutedEventArgs e){if(Pilots.SelectedItem is OmegaPilot p){p.Expiry=null;p.ReportedStatus="Unknown";_service.Save();Update();}}
    private async void Link_Click(object sender,RoutedEventArgs e)
    {
        try{var pilots=await _sso.LoadPilotsAsync();var p=pilots.FirstOrDefault(x=>x.CharacterId==(Pilots.SelectedItem as OmegaPilot)?.Id);if(p==null)return;await _sso.AddCharacterAsync(_life.Token,p.Scopes.Append(OmegaService.Scope));_service.Due();await _service.RefreshAsync(_life.Token);}catch(Exception ex){StatusText.Text=ex.Message;}
    }
}
