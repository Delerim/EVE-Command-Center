using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class IndustryWorkspaceView
{
    private IndustryService? _craftPricing;
    private CraftEconomicsSettings _craftSettings=new();
    private CraftPricingState CraftPrices => _craftPricing?.State.CraftPricing??_emptyCraftPrices;
    private readonly CraftPricingState _emptyCraftPrices=new();
    private bool _economicsFormLoading,_craftQuoting,_autoCraftPricing;
    private CancellationTokenSource? _craftPriceCancellation;
    private readonly System.Windows.Threading.DispatcherTimer _craftQuoteAgeTimer=new(){Interval=TimeSpan.FromSeconds(30)};
    private bool _craftTimerAttached;
    private IndustryCraftSuggestions.Suggestion[] _craftMatching=Array.Empty<IndustryCraftSuggestions.Suggestion>();

    private void InitializeCraftEconomics(IndustryService? service)
    {
        _craftPricing=service;
        _craftSettings=JsonSerializer.Deserialize<CraftEconomicsSettings>(JsonSerializer.Serialize(CraftPrices.Settings))!;
        _economicsFormLoading=true;
        CraftHub.SelectedIndex=_craftSettings.Hub=="Amarr"?1:0;
        CraftSystem.Text=_craftSettings.SystemName;CraftFacilityTax.Text=_craftSettings.FacilityTaxPercent?.ToString()??"";
        CraftJobBonus.Text=_craftSettings.JobBonusPercent.ToString();CraftScc.Text=_craftSettings.SccPercent.ToString();
        CraftAlpha.Text=_craftSettings.AlphaPercent.ToString();CraftSalesTax.Text=_craftSettings.TaxPercent.ToString();
        CraftBroker.Text=_craftSettings.BrokerPercent.ToString();CraftExtras.Text=_craftSettings.ExtraPerRun.ToString();
        CraftInstantSale.IsChecked=_craftSettings.InstantSale;
        _economicsFormLoading=false;
        ShowCraftAssumptions();
        if(!_craftSettings.SystemId.HasValue || !_craftSettings.FacilityTaxPercent.HasValue)
            CraftPricingStatus.Text="Set your build system and facility tax under Job costs & selling fees to complete profit estimates.";
        if(!_craftTimerAttached)
        {
            _craftTimerAttached=true;
            _craftQuoteAgeTimer.Tick+=(_,_)=> {if(_initialized && CraftPage.Visibility==Visibility.Visible && !_craftQuoting)FilterCraftSuggestions();};
            Loaded+=(_,_)=>_craftQuoteAgeTimer.Start();
            Unloaded+=(_,_)=> {_craftQuoteAgeTimer.Stop();_craftPriceCancellation?.Cancel();};
        }
        if(IsLoaded)_craftQuoteAgeTimer.Start();
    }
    private void ShowCraftAssumptions()
    {
        string index=_craftSettings.SystemId.HasValue && CraftPrices.ManufacturingIndices.TryGetValue(_craftSettings.SystemId.Value,out var value)
            ? (value*100).ToString("0.###")+"%" : "pending";
        CraftAssumptions.Text=$"Job cost: base ME0 adjusted input value × (system index {index} × job-bonus multiplier + facility tax + SCC + Alpha tax). " +
            "Market defaults assume a neutral NPC seller (7.5% sales tax, 3% broker); enter your actual selling rates. Extras include BPC/hauling costs per run.";
    }
    private static decimal CraftAmount(System.Windows.Controls.TextBox field)
    {
        if(decimal.TryParse(field.Text,NumberStyles.Number,CultureInfo.CurrentCulture,out var value) && value>=0)return value;
        throw new InvalidOperationException("Enter non-negative numeric rates and costs. Facility tax may be blank while setup is pending.");
    }
    private void ReadCraftSettings()
    {
        var settings=new CraftEconomicsSettings {Hub=CraftHub.SelectedIndex==1?"Amarr":"Jita",SystemName=CraftSystem.Text.Trim(),
            FacilityTaxPercent=string.IsNullOrWhiteSpace(CraftFacilityTax.Text)?null:CraftAmount(CraftFacilityTax),
            JobBonusPercent=CraftAmount(CraftJobBonus),SccPercent=CraftAmount(CraftScc),AlphaPercent=CraftAmount(CraftAlpha),
            TaxPercent=CraftAmount(CraftSalesTax),BrokerPercent=CraftAmount(CraftBroker),ExtraPerRun=CraftAmount(CraftExtras),InstantSale=CraftInstantSale.IsChecked==true};
        IndustryCraftEconomics.Validate(settings);
        if(CraftPrices.Systems.TryGetValue(settings.SystemName.ToUpperInvariant(),out var id))settings.SystemId=id;
        _craftSettings=settings;
        if(_craftPricing!=null){CraftPrices.Settings=settings;_craftPricing.Save();}
        ShowCraftAssumptions();
    }
    private async void ApplyCraftEconomics_Click(object sender,RoutedEventArgs e)
    {
        try {ReadCraftSettings();FilterCraftSuggestions();await LoadCraftPricesAsync(false);}
        catch(Exception ex){CraftPricingStatus.Text=ex.Message;}
    }
    private async void CraftHub_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!_initialized || _economicsFormLoading)return;
        try {ReadCraftSettings();FilterCraftSuggestions();if(_autoCraftPricing)await LoadCraftPricesAsync(false);}
        catch(Exception ex){CraftPricingStatus.Text=ex.Message;}
    }
    private void CraftProfitOnly_Click(object sender,RoutedEventArgs e)=>FilterCraftSuggestions();
    private async void PriceCraftRows_Click(object sender,RoutedEventArgs e)=>await LoadCraftPricesAsync(false);
    private async void PriceAllCraftRows_Click(object sender,RoutedEventArgs e)=>await LoadCraftPricesAsync(true);
    private void CancelCraftPrices_Click(object sender,RoutedEventArgs e)=>_craftPriceCancellation?.Cancel();
    private async Task LoadCraftPricesAsync(bool all)
    {
        if(_craftQuoting)return;
        if(_craftPricing==null){CraftPricingStatus.Text="Open this workspace with Industry connected to load public market prices.";return;}
        try {ReadCraftSettings();}
        catch(Exception ex){CraftPricingStatus.Text=ex.Message;return;}
        var selected=CraftGrid.SelectedItems.Cast<IndustryCraftSuggestions.Suggestion>().ToArray();
        var candidates=all?_craftMatching:selected.Length>0?selected:_craftMatching.Take(100).ToArray();
        var types=candidates.SelectMany(s=>s.Recipe.Materials.Keys.Concat(s.Recipe.Products.Keys)).Distinct().ToArray();
        _craftQuoting=true;
        CraftPricesButton.IsEnabled=CraftAllPricesButton.IsEnabled=CraftHub.IsEnabled=false;CraftCancelPrices.IsEnabled=true;
        using var cancel=new CancellationTokenSource();_craftPriceCancellation=cancel;
        try
        {
            var progress=new Progress<string>(message=> {if(_craftQuoting)CraftPricingStatus.Text=message;});
            var result=await _craftPricing.RefreshCraftPricesAsync(types,_craftSettings.Hub,_craftSettings.SystemName,cancel.Token,progress);
            if(CraftPrices.Systems.TryGetValue(_craftSettings.SystemName.ToUpperInvariant(),out long id))_craftSettings.SystemId=id;
            CraftPrices.Settings=_craftSettings;_craftPricing.Save();
            CraftPricingStatus.Text=$"{_craftSettings.Hub}: {result.Updated:N0} quotes updated; {result.Failed:N0} deferred. Cached quotes retain their ESI expiry. " + result.ReferenceError;
            if(!_craftSettings.SystemId.HasValue || !_craftSettings.FacilityTaxPercent.HasValue)
                CraftPricingStatus.Text+=" Set build system + facility tax under Job costs & selling fees to complete profit estimates.";
        }
        catch(OperationCanceledException){CraftPricingStatus.Text="Price refresh cancelled. Completed quotes retained.";}
        catch(Exception ex){CraftPricingStatus.Text="Price refresh deferred: "+ex.Message;}
        finally
        {
            _craftQuoting=false;_craftPriceCancellation=null;
            CraftPricesButton.IsEnabled=CraftAllPricesButton.IsEnabled=CraftHub.IsEnabled=true;CraftCancelPrices.IsEnabled=false;
            ShowCraftAssumptions();FilterCraftSuggestions();
        }
    }
    private long EconomicRuns => long.TryParse(CraftRuns.Text,out var runs) && runs>0 ? runs : 0;
    private string CraftSourceCost(int type,decimal quantity)
    {
        var q=CraftPrices.Quotes.GetValueOrDefault(IndustryCraftEconomics.Key(_craftSettings.Hub,type));
        decimal? price=q!=null && IndustryCraftEconomics.Fresh(q,DateTimeOffset.UtcNow)?q.Sell:null;
        return $"{IndustryCatalog.Name(type)}: {quantity:N0} × {IndustryCraftEconomics.Money(price)} = {IndustryCraftEconomics.Money(quantity*price)}";
    }
    private IndustryCraftSuggestions.Suggestion CostCraftRow(IndustryCraftSuggestions.Suggestion row)=>
        row with {Economics=IndustryCraftEconomics.Calculate(row,EconomicRuns,CraftPrices,_craftSettings,DateTimeOffset.UtcNow)};
    private void SetCraftRows(IndustryCraftSuggestions.Suggestion[] rows)
    {
        var selected=CraftGrid.SelectedItems.Cast<IndustryCraftSuggestions.Suggestion>().Select(s=>(s.Blueprint.OwnerId,s.ItemId)).ToHashSet();
        var sort=CollectionViewSource.GetDefaultView(CraftGrid.ItemsSource)?.SortDescriptions.ToArray();
        _selectingCraftBundle=true;
        try
        {
            CraftGrid.ItemsSource=rows;
            var view=CollectionViewSource.GetDefaultView(rows);
            if(sort!=null && view.CanSort){view.SortDescriptions.Clear();foreach(var item in sort)view.SortDescriptions.Add(item);}
            foreach(var row in rows.Where(s=>selected.Contains((s.Blueprint.OwnerId,s.ItemId))))CraftGrid.SelectedItems.Add(row);
        }
        finally{_selectingCraftBundle=false;}
    }
    private void ShowCraftTotals(IEnumerable<IndustryCraftSuggestions.Suggestion> selected)
    {
        var rows=selected.ToArray();
        var total=rows.Length==0?null:IndustryCraftEconomics.Combine(rows.Select(s=>CostCraftRow(s).Economics));
        CraftMaterialsTotal.Text=IndustryCraftEconomics.Money(total?.Materials);
        CraftJobsTotal.Text=IndustryCraftEconomics.Money(total?.JobCost+total?.Extras);
        CraftSalesTotal.Text=IndustryCraftEconomics.Money(total?.NetSale);
        CraftFeesTotal.Text=IndustryCraftEconomics.Money(total?.Fees);
        CraftProfitTotal.Text=IndustryCraftEconomics.Money(total?.Profit);
        CraftProfitTotal.Foreground=(SolidColorBrush)new BrushConverter().ConvertFromString(total?.Color??"#EDBC73")!;
        CraftProfitTotal.ToolTip=total?.Breakdown;
    }
}
