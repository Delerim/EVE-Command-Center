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
    public OmegaWindow(){InitializeComponent(); EmbeddedModuleHost.RefreshWhenVisible(this, Update);Alerts.IsChecked=_service.Budget.Alerts;ReloadOffers();_service.Changed+=Update;_timer.Tick+=(_,_)=>Update();Loaded+=async(_,_)=>{Update();_timer.Start();await _service.RefreshAsync(_life.Token);};Closed+=(_,_)=>{_timer.Stop();_service.Changed-=Update;_life.Cancel();};Update();}
    private void Update()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(Update);return;}
        if(_life.IsCancellationRequested || EmbeddedModuleHost.IsInactive(this))return;
        long id=(Pilots.SelectedItem as OmegaPilot)?.Id??0;
        if(!Pilots.Items.Cast<OmegaPilot>().Select(p=>p.Id).Order().SequenceEqual(_service.Pilots.Select(p=>p.Id).Order()))
        {
            Pilots.ItemsSource=_service.Pilots.OrderBy(p=>p.Expiry??DateTimeOffset.MaxValue).ThenBy(p=>p.Name).ToArray();
            Pilots.SelectedItem=_service.Pilots.FirstOrDefault(p=>p.Id==id);if(Pilots.SelectedItem==null&&Pilots.Items.Count>0)Pilots.SelectedIndex=0;
        }
        else Pilots.Items.Refresh();
        var accounts=OmegaPlanning.Accounts(_service.Pilots);var now=DateTimeOffset.UtcNow;
        TrackedTile.Text=accounts.Count.ToString();DueTile.Text=accounts.Count(p=>p.Expiry>now&&p.Expiry<now.AddDays(30)).ToString();ExpiredTile.Text=accounts.Count(p=>p.Expiry<=now).ToString();
        PlexTile.Text=_service.Budget.PlexSell is >0?(_service.Budget.PlexSell.Value/1000000).ToString("N2")+"M ISK":"Unavailable";
        Summary.Text=$"{_service.Pilots.Count} pilots | {accounts.Count(p=>!p.Expiry.HasValue)} accounts without dates | Select a pilot to manage dates and clone details";
        RefreshBudget();
        if(Pilots.SelectedItem is OmegaPilot selected) RefreshClones(selected);
    }
    private void Selected(object sender,SelectionChangedEventArgs e)
    {
        if(Pilots.SelectedItem is not OmegaPilot p)return;SelectedTitle.Text=p.Name;SelectedStatus.Text=p.OmegaStatus+" | "+p.JumpCount+" jump clones";Account.Text=p.Account;Expiry.SelectedDate=p.Expiry?.LocalDateTime.Date;Time.Text=p.Expiry?.ToLocalTime().ToString("HH:mm")??"00:00";
        RefreshClones(p);
    }
    private void RefreshClones(OmegaPilot p)
    {
        CloneSummary.Text = "Home: " + p.Home + "  |  " + (p.Error.Length > 0 ? p.Error : "Updated " + p.Updated.ToLocalTime().ToString("dd MMM HH:mm"));
        var rows = new List<CloneRow>();
        if (p.Clones.ValueKind == System.Text.Json.JsonValueKind.Object && p.Clones.TryGetProperty("jump_clones", out var clones))
            foreach (var c in clones.EnumerateArray())
            {
                string name = IndustryCatalog.Text(c, "name");
                var implants = c.TryGetProperty("implants", out var items) ? items.EnumerateArray().Select(i => new ImplantRow(i.GetInt32(), p.ImplantNames.GetValueOrDefault(i.GetInt32(), IndustryCatalog.Name(i.GetInt32())))).ToArray() : Array.Empty<ImplantRow>();
                rows.Add(new(string.IsNullOrWhiteSpace(name) ? "Clone name unavailable" : name, p.LocationNames.GetValueOrDefault(IndustryCatalog.Num(c, "location_id"), "Location name unavailable"), implants));
            }
        CloneCards.ItemsSource = rows;
        CloneEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void RefreshClones_Click(object sender, RoutedEventArgs e)
    {
        RefreshClonesButton.IsEnabled = false;
        try
        {
            _service.Due();
            await _service.RefreshAsync(_life.Token);
            Update();
            StatusText.Text = "Clone refresh requested. Names are shown when supplied by EVE; refresh errors appear above.";
        }
        finally { RefreshClonesButton.IsEnabled = true; }
    }
    private sealed record ImplantRow(int TypeId, string Name) { public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=32"; }
    private sealed record CloneRow(string Name, string Location, ImplantRow[] Implants)
    { public string ImplantSummary => Implants.Length == 0 ? "No implants" : $"{Implants.Length} implants"; }
    private void Save_Click(object sender,RoutedEventArgs e)
    {
        if(Pilots.SelectedItem is not OmegaPilot p||Expiry.SelectedDate is not {} date){StatusText.Text="Select a pilot and enter the expiry from your launcher.";return;}
        if(!TimeSpan.TryParseExact(Time.Text,@"hh\:mm",System.Globalization.CultureInfo.InvariantCulture,out var time)){StatusText.Text="Use a local time in HH:mm format.";return;}
        p.Account=Account.Text.Trim();p.Expiry=new DateTimeOffset(DateTime.SpecifyKind(date.Date+time,DateTimeKind.Local));p.ReportedStatus="Omega";
        if(p.Account.Length>0)foreach(var other in _service.Pilots.Where(x=>x.Account.Equals(p.Account,StringComparison.OrdinalIgnoreCase))){other.Expiry=p.Expiry;other.ReportedStatus=p.ReportedStatus;}
        _service.Save();_service.CheckAlerts();Update();StatusText.Text="Manual subscription date saved. Verify it in the launcher after renewals.";
    }
    private void Clear_Click(object sender,RoutedEventArgs e){if(Pilots.SelectedItem is OmegaPilot p){p.Expiry=null;p.ReportedStatus="Unknown";_service.Save();Update();}}
    private async void Link_Click(object sender,RoutedEventArgs e)
    {
        try{var pilots=await _sso.LoadPilotsAsync();var p=pilots.FirstOrDefault(x=>x.CharacterId==(Pilots.SelectedItem as OmegaPilot)?.Id);if(p==null)return;await _sso.AddCharacterAsync(_life.Token,p.Scopes.Append(OmegaService.Scope),p.CharacterId);_service.Due();await _service.RefreshAsync(_life.Token);}catch(Exception ex){StatusText.Text=ex.Message;}
    }
}
