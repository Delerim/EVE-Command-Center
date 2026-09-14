using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class PiEconomicsView : System.Windows.Controls.UserControl
{
    private PiState? _state;
    private bool _loading;
    private bool _quoting;
    private sealed record PlanetEntry(PiColony Colony,string Label,string Rate,string Updated);
    private PiColony? Selected=>(Planets.SelectedItem as PlanetEntry)?.Colony;
    public PiEconomicsView(){InitializeComponent();}
    public void Refresh(PiState state)
    {
        _state=state;string? key=Selected is {} c?PlanetaryEconomics.Key(c):null;
        _loading=true;
        var rows=state.Colonies.OrderBy(c=>c.Character).ThenBy(c=>c.Planet).Select(c=>{
            var s=state.Taxes.GetValueOrDefault(PlanetaryEconomics.Key(c));
            return new PlanetEntry(c,c.Character+" | "+c.Planet,s?.PocoPercent?.ToString("0.###")??"Not set",s==null?"—":s.Updated.ToLocalTime().ToString("dd MMM HH:mm"));
        }).ToArray();
        Planets.ItemsSource=rows;Planets.SelectedItem=rows.FirstOrDefault(r=>PlanetaryEconomics.Key(r.Colony)==key)??rows.FirstOrDefault();_loading=false;
        if(key==null || (Selected is {} selected && PlanetaryEconomics.Key(selected)!=key))LoadSelection();
    }
    private void Planet_Selected(object sender,SelectionChangedEventArgs e){if(!_loading)LoadSelection();}
    private void LoadSelection()
    {
        if(Selected is not {} c||_state==null)return;
        var s=_state.Taxes.GetValueOrDefault(PlanetaryEconomics.Key(c))??new();
        ColonyTitle.Text=c.Character+" | "+c.Planet;
        PocoRate.Text=s.PocoPercent?.ToString()??"";SalesRate.Text=s.SalesTaxPercent.ToString();BrokerRate.Text=s.BrokerPercent.ToString();Other.Text=s.OtherCost.ToString();Immediate.IsChecked=s.SellImmediately;
        Product.ItemsSource=PlanetaryEconomics.Recipes(c).SelectMany(r=>r.Outputs.Keys).Distinct().Select(PlanetaryAnalysis.Type).OrderBy(t=>t.Name).ToArray();Product.SelectedIndex=0;
        Net.Text="Calculate this batch";Breakdown.Text=InputSummary.Text=Status.Text="";
    }
    private bool Read(out PiTaxSettings settings,out double units)
    {
        settings=new();units=0;
        bool Number(string text,out double n)=>double.TryParse(text,out n)&&double.IsFinite(n)&&n>=0;
        if(!Number(PocoRate.Text,out var poco)||poco>100||!Number(SalesRate.Text,out var sales)||!Number(BrokerRate.Text,out var broker)||sales+broker>=100||!Number(Other.Text,out var other)||!Number(Units.Text,out units)||units<1||units>1000000000||units!=Math.Floor(units)) {Status.Text="Enter the total POCO rate (0–100), valid seller fees, non-negative costs and a whole batch quantity (1–1 billion). Unknown rates cannot be treated as zero.";return false;}
        settings=new(){PocoPercent=poco,SalesTaxPercent=sales,BrokerPercent=broker,OtherCost=other,SellImmediately=Immediate.IsChecked==true,Updated=DateTimeOffset.UtcNow};return true;
    }
    private void Calculate_Click(object sender,RoutedEventArgs e)=>Calculate(true);
    private void Collectable_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not {} c||Product.SelectedItem is not PiType product)return;
        var estimate=PlanetaryAnalysis.Build(new PiState{Colonies=new(){c}},DateTimeOffset.UtcNow).FactoryTiers.SelectMany(t=>t.Products).FirstOrDefault(p=>p.TypeId==product.Id)?.Collect??0;
        Units.Text=Math.Floor(estimate).ToString("0");Net.Text="Recalculate with the estimated quantity";Breakdown.Text=InputSummary.Text="";
        Status.Text="Projected collectable quantity; reserved intermediate materials are excluded. Verify in EVE before hauling.";
    }
    private void Calculate(bool save)
    {
        Net.Text="Pending calculation";Breakdown.Text=InputSummary.Text="";
        if(_state==null||Selected is not {} c||Product.SelectedItem is not PiType product||!Read(out var s,out var units))return;
        if(save){_state.Taxes[PlanetaryEconomics.Key(c)]=s;BackgroundOperations.Current.Planetary.Save();}
        try {
            var estimate=PlanetaryEconomics.Calculate(c,product.Id,units,s,_state.Prices);
            Net.Text=estimate.NetText;Net.Foreground=(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(estimate.Color)!;
            Breakdown.Text=estimate.Detail;InputSummary.Text=estimate.Inputs.Count==0?"Local extraction: no purchased inputs/import tax included.":"T1 inputs for this batch (rounded to whole processing cycles):\n"+string.Join("\n",estimate.Inputs.Select(i=>$"{PlanetaryAnalysis.Type(i.Key).Name}: {i.Value:N0}"));
            var dates=estimate.Inputs.Keys.Append(product.Id).Select(t=>_state.Prices.GetValueOrDefault(t)?.Checked).Where(d=>d.HasValue).ToArray();
            Status.Text=$"{units:N0} {product.Name}. "+(save?"Manual rates saved. ":"")+(estimate.Net==null?"Load missing Jita quotes.":"Independent batch estimate; do not sum intermediate and final sales for the same production.")+(dates.Length>0?$" Oldest quote: {dates.Min()!.Value.ToLocalTime():dd MMM HH:mm}":"");
        }catch(InvalidOperationException ex){Net.Text="Cannot estimate this chain";Breakdown.Text=InputSummary.Text="";Status.Text=ex.Message;}
    }
    private async void Quotes_Click(object sender,RoutedEventArgs e)
    {
        if(_quoting||_state==null||Selected is not {} c||Product.SelectedItem is not PiType product||!double.TryParse(Units.Text,out var units)||!double.IsFinite(units)||units<1||units>1000000000)return;
        _quoting=true;QuotesButton.IsEnabled=false;Status.Text="Loading quotes through the shared ESI queue...";
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try {
            foreach(int type in PlanetaryEconomics.Inputs(c,product.Id,units).Keys.Append(product.Id).Distinct()) {
                if(_state.Prices.TryGetValue(type,out var cached)&&cached.Sell.HasValue&&cached.Checked>DateTimeOffset.UtcNow.AddHours(-1))continue;
                var quote=await MiningMarketService.FetchStationPricesAsync(MiningMarketService.TheForgeRegionId,MiningMarketService.Jita44StationId,type,timeout.Token);
                _state.Prices[type]=new(){Buy=quote.BestBuy,Sell=quote.BestSell,Checked=DateTimeOffset.UtcNow};
            }
            BackgroundOperations.Current.Planetary.Save();Calculate(false);
        }catch(Exception ex){Status.Text="Quote/chain check deferred: "+ex.Message;}finally{_quoting=false;QuotesButton.IsEnabled=true;}
    }
}
