using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class IndustryWindow : Window
{
    private readonly IndustryService _service=BackgroundOperations.Current.Industry;
    private readonly EveSsoService _sso=BackgroundOperations.Current.Sso;
    private readonly CancellationTokenSource _life=new();
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromSeconds(30)};
    private IndustryPilot? Selected=>Pilots.SelectedItem as IndustryPilot;
    private int _scan;
    private CancellationTokenSource? _scanCancellation;
    private bool _quoting;
    private string? _costKey;
    private string CostKey(IndustryPilot p,IndustryRecipe r,int runs)=>$"{p.Id}:{r.Blueprint}:{r.Activity}:{runs}";
    private string ActivityFilter => (ActivityTabs?.SelectedItem as TabItem)?.Tag?.ToString() ?? "all";
    private async void Activity_Selected(object sender,SelectionChangedEventArgs e)
    {
        if(!IsInitialized || e.Source!=ActivityTabs)return;
        UpdateDetails();await Scan();
    }
    public IndustryWindow()
    {
        InitializeComponent();Alerts.IsChecked=_service.State.Alerts;
        _service.Changed+=Update;_timer.Tick+=(_,_)=>UpdateDetails();
        Loaded+=async(_,_)=>{Update();_timer.Start();await _service.RefreshAsync(_life.Token);};
        Closed+=(_,_)=>{_timer.Stop();_service.Changed-=Update;_life.Cancel();_scanCancellation?.Cancel();};
    }
    private void Update()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(Update);return;}
        long id=Selected?.Id??0;
        if(!Pilots.Items.Cast<IndustryPilot>().Select(p=>p.Id).SequenceEqual(_service.State.Pilots.Select(p=>p.Id)))
        {
            Pilots.ItemsSource=null;Pilots.ItemsSource=_service.State.Pilots.ToArray();
            Pilots.SelectedItem=_service.State.Pilots.FirstOrDefault(p=>p.Id==id)??_service.State.Pilots.FirstOrDefault();
        }
        else Pilots.Items.Refresh();
        UpdateDetails();
    }
    private void UpdateDetails()
    {
        StatusText.Text=_service.Status+(_service.Busy?" | "+EsiDiagnostics.Status:"");
        if(Selected is not {} p)return;
        var jobs=IndustryCatalog.Jobs(p,DateTimeOffset.UtcNow);Jobs.ItemsSource=jobs.Where(j=>IndustryActivities.Matches(ActivityFilter,j.ActivityCode)).ToList();
        ActivityContext.Text=(ActivityTabs.SelectedItem as TabItem)?.Header+" | "+Jobs.Items.Count+" jobs | Selected pilot: "+p.Name;
        Dashboard.Text=$"{p.Name} | {jobs.Count(j=>j.Status=="active")} active jobs | {jobs.Count(j=>j.Status.Contains("READY")||j.Status=="ready")} ready to deliver | {p.Blueprints.Count} blueprints";
        ManufacturingStat.Text=jobs.Count(j=>j.Status=="active"&&j.Activity=="Manufacturing").ToString();
        ResearchStat.Text=jobs.Count(j=>j.Status=="active"&&j.Activity is "TE research" or "ME research" or "Copying").ToString();
        InventionStat.Text=jobs.Count(j=>j.Status=="active"&&j.Activity is "Invention" or "Reaction").ToString();
        ReadyStat.Text=jobs.Count(j=>j.Status.Contains("READY")||j.Status=="ready").ToString();
        Snapshot.Text=p.Error.Length>0?p.Error:$"Snapshot {p.Updated.ToLocalTime():dd MMM HH:mm}";
        var relevant=IndustryCatalog.Recipes.SelectMany(r=>r.Skills.Keys).ToHashSet();
        Skills.ItemsSource=p.Skills.Where(s=>relevant.Contains(s.Key)||new[]{"Industry","Advanced Industry","Mass Production","Advanced Mass Production","Laboratory Operation","Advanced Laboratory Operation","Science","Research","Metallurgy"}.Contains(IndustryCatalog.Name(s.Key))).Select(s=>new {Name=IndustryCatalog.Name(s.Key),Level=s.Value,Role=IndustryCatalog.Name(s.Key) switch {"Industry"=>$"{s.Value*4}% manufacturing time reduction","Advanced Industry"=>$"{s.Value*3}% industry time reduction","Mass Production" or "Advanced Mass Production"=>$"+{s.Value} manufacturing slots","Laboratory Operation" or "Advanced Laboratory Operation"=>$"+{s.Value} research slots",_=>"Recipe / industry skill"}}).OrderBy(s=>s.Name).ToList();
    }
    private async void Pilot_Selected(object sender,SelectionChangedEventArgs e){UpdateDetails();await Scan();}
    private async void Scan_Click(object sender,RoutedEventArgs e)=>await Scan();
    private async Task Scan()
    {
        _scanCancellation?.Cancel();
        int generation=++_scan;
        if(Selected is not {} selected)return;
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _scanCancellation=cancellation;
        var p=new IndustryPilot{Id=selected.Id,Name=selected.Name,Blueprints=selected.Blueprints.ToList(),
            Assets=selected.Assets.ToList(),Skills=new(selected.Skills),Updated=selected.Updated};
        int runs=int.TryParse(Runs.Text,out var n)?Math.Clamp(n,1,10000):1;string filter=Search.Text.Trim();bool all=AllRecipes.IsChecked==true,ready=ReadyOnly.IsChecked==true;
        var previousRecipe=(Recipes.SelectedItem as IndustryPlan)?.Recipe;
        ScanStatus.Text="Scanning bundled CCP recipes against this pilot's stock...";
        var owned=p.Blueprints.Select(b=>(int)IndustryCatalog.Num(b,"type_id")).ToHashSet();
        var recipes=IndustryCatalog.Recipes.Where(r=>IndustryActivities.Matches(ActivityFilter,r.Activity)&&(all||owned.Contains(r.Blueprint))&&(filter.Length==0||r.Name.Contains(filter,StringComparison.OrdinalIgnoreCase)||r.Products.Keys.Any(t=>IndustryCatalog.Name(t).Contains(filter,StringComparison.OrdinalIgnoreCase)))).ToArray();
        // Planner scan has no network requests. Each recipe is an independent alternative, not an allocated shopping basket.
        var quotes=new Dictionary<int,IndustryQuote>(_service.State.Quotes);
        List<IndustryPlan> plans;
        try { plans=await Task.Run(()=>recipes.Select(r=>{cancellation.Token.ThrowIfCancellationRequested();return IndustryCatalog.Plan(p,r,runs,quotes);}).Where(r=>!ready||r.Status=="READY").OrderBy(r=>r.Status=="READY"?0:1).ThenBy(r=>r.Name).ToList(),cancellation.Token); }
        catch(OperationCanceledException){return;}
        catch(Exception ex){if(generation==_scan)ScanStatus.Text="Recipe scan failed: "+ex.GetType().Name;return;}
        finally {if(ReferenceEquals(_scanCancellation,cancellation))_scanCancellation=null;}
        await Dispatcher.InvokeAsync(() =>
        {
        if(generation!=_scan||_life.IsCancellationRequested)return;
        Recipes.ItemsSource=plans.Take(1000).ToArray();
        ScanStatus.Text=$"{plans.Count:N0} matches; showing up to 1,000. Hangar/container materials across this pilot's locations; hauling may be required. Each recipe is checked independently. Facility/material bonuses excluded.";
        if(plans.Count>0)Recipes.SelectedItem=Recipes.Items.Cast<IndustryPlan>().FirstOrDefault(r=>r.Recipe==previousRecipe)??Recipes.Items[0];else {Materials.ItemsSource=null;RequiredSkills.ItemsSource=null;ProfitRows.ItemsSource=null;_costKey=null;PlanTitle.Text="No matching recipes";PlanState.Text="";PlanActivity.Text="";PlanDetail.Text="Upgrade this toon in Settings for all features, refresh blueprints, or enable All recipes.";}
        });
    }
    private void Recipe_Selected(object sender,SelectionChangedEventArgs e)=>ShowPlan();
    private void ShowPlan()
    {
        if(Selected is not {} p||Recipes.SelectedItem is not IndustryPlan row)return;
        int runs=int.TryParse(Runs.Text,out var n)?Math.Clamp(n,1,10000):1;
        var plan=IndustryCatalog.Plan(p,row.Recipe,runs,_service.State.Quotes);
        PlanTitle.Text=plan.Name;Materials.ItemsSource=plan.Materials;
        PlanState.Text=plan.Status;PlanState.Foreground=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(plan.Color)!;
        PlanActivity.Text=plan.Activity;PlanActivity.Foreground=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(plan.ActivityColor)!;
        PlanDetail.Text=plan.Blueprint+" | "+plan.Status+"\n"+plan.Products+"\n"+plan.Duration+"\n"+plan.Skills+"\n"+plan.Economics;
        RequiredSkills.ItemsSource=plan.Recipe.Skills.Select(s=>new {Name=IndustryCatalog.Name(s.Key),Trained=p.Skills.GetValueOrDefault(s.Key),Needed=s.Value,
            Status=p.Skills.GetValueOrDefault(s.Key)>=s.Value?"READY":"MISSING",Color=p.Skills.GetValueOrDefault(s.Key)>=s.Value?"#74D6C9":"#D6ACFF"}).OrderBy(s=>s.Status=="MISSING"?0:1).ThenBy(s=>s.Name).ToList();
        string key=CostKey(p,row.Recipe,runs);
        var costs=_service.State.CostSettings.GetValueOrDefault(key)??new IndustryCostSettings{TaxPercent=IndustryProfitability.Tax(p),BrokerPercent=IndustryProfitability.Broker(p),CopyCost=plan.Blueprint.StartsWith("BPO")?0:null};
        if(_costKey!=key) {
            _costKey=key;CopyCost.Text=costs.CopyCost?.ToString()??"";JobCost.Text=costs.JobCost?.ToString()??"";
            SaleTax.Text=costs.TaxPercent.ToString("0.###");BrokerFee.Text=costs.BrokerPercent.ToString("0.###");InstantSale.IsChecked=costs.InstantSale;
        }
        ProfitRows.ItemsSource=IndustryProfitability.Calculate(plan,runs,_service.State.Quotes,costs);
        ProfitHint.Text=$"{runs:N0} runs | Defaults: Accounting {p.Skills.GetValueOrDefault(16622)}, Broker Relations {p.Skills.GetValueOrDefault(3446)}; neutral NPC standings. Override rates for your selling character/station. Save applies the fields above.";
    }
    private void Calculate_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} p||Recipes.SelectedItem is not IndustryPlan row)return;
        bool Amount(string value,out double? amount) {amount=null;if(string.IsNullOrWhiteSpace(value))return true;if(!double.TryParse(value,out var n)||!double.IsFinite(n)||n<0)return false;amount=n;return true;}
        if(!Amount(CopyCost.Text,out var copy)||!Amount(JobCost.Text,out var job)||!Amount(SaleTax.Text,out var tax)||!Amount(BrokerFee.Text,out var broker)||tax==null||broker==null||tax+broker>=100) {ProfitHint.Text="Enter non-negative costs and fee percentages totalling less than 100%. Blank job costs stay pending.";return;}
        int runs=int.TryParse(Runs.Text,out var n)?Math.Clamp(n,1,10000):1;
        _service.State.CostSettings[CostKey(p,row.Recipe,runs)]=new(){CopyCost=copy,JobCost=job,TaxPercent=tax.Value,BrokerPercent=broker.Value,InstantSale=InstantSale.IsChecked==true};
        _service.Save();ShowPlan();
    }
    private async void Quote_Click(object sender,RoutedEventArgs e)
    {
        if(_quoting||Recipes.SelectedItem is not IndustryPlan p)return;_quoting=true;
        try {StatusText.Text="Loading selected recipe's Jita quotes through the shared queue...";await _service.QuoteAsync(p.Recipe,_life.Token);ShowPlan();StatusText.Text="Quotes updated (hourly cache).";}
        catch(Exception ex){StatusText.Text="Quote refresh deferred: "+ex.GetType().Name;}finally{_quoting=false;}
    }
    private async void Link_Click(object sender,RoutedEventArgs e)
    {
        try {var linked=await _sso.LoadPilotsAsync();var p=linked.FirstOrDefault(p=>p.CharacterId==Selected?.Id);await _sso.AddCharacterAsync(_life.Token,(p?.Scopes??Array.Empty<string>()).Concat(IndustryService.Scopes),p?.CharacterId);_service.Due();await _service.RefreshAsync(_life.Token);}
        catch(Exception ex){StatusText.Text=ex.Message;}
    }
    private async void Refresh_Click(object sender,RoutedEventArgs e)=>await _service.RefreshAsync(_life.Token);
    private void Alerts_Click(object sender,RoutedEventArgs e){_service.State.Alerts=Alerts.IsChecked==true;_service.Save();}
}
