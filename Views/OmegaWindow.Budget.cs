using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class OmegaWindow
{
    private sealed class BudgetRow
    {
        public string Key {get;set;}=""; public string Name {get;set;}=""; public int Plex {get;set;}
        public string Saved {get;set;}="";public string Gap {get;set;}="";public string Daily {get;set;}="";
    }
    private static string Isk(decimal? value)=>value.HasValue?value.Value.ToString("N0")+" ISK":"Unavailable";
    private void ReloadOffers()
    {
        var selected=Offer.SelectedItem as OmegaOffer;
        Offer.ItemsSource=_service.Budget.Offers.Where(x=>!x.Ends.HasValue||x.Ends>DateTimeOffset.UtcNow).OrderBy(x=>x.PerMonth).ToArray();
        Offer.SelectedItem=selected;if(Offer.SelectedItem==null)Offer.SelectedItem=Offer.Items.Cast<OmegaOffer>().FirstOrDefault(x=>x.Days==30)??Offer.Items.Cast<OmegaOffer>().FirstOrDefault();
    }
    private void Budget_Changed(object sender,SelectionChangedEventArgs e)=>RefreshBudget();
    private void RefreshBudget()
    {
        if(Budgets==null)return;
        var state=_service.Budget;var now=DateTimeOffset.UtcNow;
        QuoteText.Text=(state.PriceTime==default?"No live PLEX quote yet.":$"Jita 4-4 lowest PLEX sell: {state.PlexSell:N0} ISK | {state.PriceTime.ToLocalTime():dd MMM HH:mm}"+(state.PriceTime<now.AddHours(-1)?" | STALE cached estimate":""))+"\n"+_service.MarketStatus;
        News.ItemsSource=state.News;
        NewsStatus.Text="Announcements may describe expired or account-limited promotions. Open the official post and verify in NES/EVE Store before using a price. "+(state.NewsTime==default?"News not fetched yet.":$"Checked {state.NewsTime.ToLocalTime():dd MMM HH:mm}.");
        Deals.ItemsSource=state.Offers.OrderBy(x=>x.Ends<=now).ThenBy(x=>x.PerMonth).Select(x=>new {x.Name,Monthly=x.PerMonth.ToString("N1"),Cost=Isk(state.PlexSell is >0?x.Plex*(decimal)state.PlexSell.Value:null),x.Validity,x.Source,Offer=x}).ToList();
        if(Offer.SelectedItem is not OmegaOffer offer){BudgetTotal.Text="Add or select a package to calculate renewal costs.";Budgets.ItemsSource=null;return;}
        if(offer.Ends<=now){BudgetTotal.Text="Selected offer has expired. Choose another package.";Budgets.ItemsSource=null;return;}
        var key=(Budgets.SelectedItem as BudgetRow)?.Key;
        var accounts=OmegaPlanning.Accounts(_service.Pilots);decimal total=0;int count=0;
        var rows=accounts.Select(p=>{
            var saved=state.Savings.GetValueOrDefault(OmegaPlanning.Key(p))??new();
            var b=OmegaPlanning.Budget(offer,saved,state.PlexSell,p.Expiry,now);if(b.Gap.HasValue){total+=b.Gap.Value;count++;}
            return new BudgetRow{Key=OmegaPlanning.Key(p),Name=p.Account.Length>0?p.Account:p.Name,Plex=b.PlexNeeded,Saved=Isk(saved.Isk),Gap=Isk(b.Gap),Daily=p.Expiry<=now?"Renew / verify now":p.Expiry==null?"Set expiry date":Isk(b.Daily)};
        }).ToArray();
        // Preserve savings being edited when background prices refresh.
        var oldPlex=SavedPlex.Text;var oldIsk=SavedIsk.Text;
        Budgets.ItemsSource=rows;Budgets.SelectedItem=rows.FirstOrDefault(x=>x.Key==key);
        if(key!=null&&Budgets.SelectedItem!=null){SavedPlex.Text=oldPlex;SavedIsk.Text=oldIsk;}else if(rows.Length>0)Budgets.SelectedIndex=0;
        BudgetTotal.Text=$"{offer.Plex:N0} PLEX per account | {offer.PerMonth:N1} PLEX per 30 days | {accounts.Count} accounts | Remaining savings target: "+(count==accounts.Count?Isk(total):"waiting for PLEX price");
    }
    private void Budget_Selected(object sender,SelectionChangedEventArgs e)
    {
        if(Budgets.SelectedItem is not BudgetRow row)return;
        var saved=_service.Budget.Savings.GetValueOrDefault(row.Key)??new();SavedPlex.Text=saved.Plex.ToString();SavedIsk.Text=saved.Isk.ToString("0",CultureInfo.CurrentCulture);
    }
    private void Savings_Click(object sender,RoutedEventArgs e)
    {
        if(Budgets.SelectedItem is not BudgetRow row)return;
        if(!int.TryParse(SavedPlex.Text,NumberStyles.Integer|NumberStyles.AllowThousands,CultureInfo.CurrentCulture,out int plex)||plex<0||!decimal.TryParse(SavedIsk.Text,out var isk)||isk<0){StatusText.Text="Enter non-negative PLEX and ISK savings.";return;}
        _service.Budget.Savings[row.Key]=new(){Plex=plex,Isk=isk};_service.SaveBudget();RefreshBudget();StatusText.Text="Account savings saved. Use separate earmarked funds for each account to avoid counting the same ISK twice.";
    }
    private void Deal_Click(object sender,RoutedEventArgs e)
    {
        if(string.IsNullOrWhiteSpace(DealName.Text)||!int.TryParse(DealDays.Text,out int days)||days<1||days>3650||!int.TryParse(DealPlex.Text,out int plex)||plex<1||plex>1000000){StatusText.Text="Enter an offer name, 1-3650 days and a positive PLEX price.";return;}
        DateTimeOffset? end=DealEnd.SelectedDate is {} d?new DateTimeOffset(DateTime.SpecifyKind(d.AddDays(1),DateTimeKind.Local)):null;
        if(end<=DateTimeOffset.UtcNow){StatusText.Text="Choose an offer expiry in the future.";return;}
        var offer=new OmegaOffer{Name=DealName.Text.Trim(),Days=days,Plex=plex,Ends=end,Manual=true};_service.Budget.Offers.Add(offer);_service.SaveBudget();ReloadOffers();Offer.SelectedItem=offer;RefreshBudget();
    }
    private void OfferSource_Click(object sender,RoutedEventArgs e){if(Deals.SelectedItem?.GetType().GetProperty("Offer")?.GetValue(Deals.SelectedItem) is OmegaOffer offer){if(offer.Manual)StatusText.Text="This is your manually entered NES price.";else OpenOfficial(offer.SourceUrl.Length>0?offer.SourceUrl:"https://www.eveonline.com/news/view/25-off-all-omega-in-nes");}}
    private void RemoveDeal_Click(object sender,RoutedEventArgs e)
    {
        if(Deals.SelectedItem==null)return;
        var offer=Deals.SelectedItem.GetType().GetProperty("Offer")?.GetValue(Deals.SelectedItem) as OmegaOffer;
        if(offer!=null){_service.Budget.Offers.Remove(offer);_service.SaveBudget();ReloadOffers();RefreshBudget();}
    }
    private void Alerts_Click(object sender,RoutedEventArgs e){_service.Budget.Alerts=Alerts.IsChecked==true;_service.SaveBudget();_service.CheckAlerts();StatusText.Text="Grouped reminders at under 30 days, under 7 days and expiry. Command Center must remain running.";}
    private async void Market_Click(object sender,RoutedEventArgs e){try{await _service.RefreshMarketAsync(_life.Token);RefreshBudget();}catch(Exception ex){StatusText.Text=ex.Message;}}
    private void Store_Click(object sender,RoutedEventArgs e)=>OpenOfficial("https://store.eveonline.com/");
    private void News_Click(object sender,RoutedEventArgs e){if(News.SelectedItem is OmegaNews n)OpenOfficial(n.Url);}
    private void OpenOfficial(string url){try{if(Uri.TryCreate(url,UriKind.Absolute,out var u)&&u.Scheme=="https"&&(u.Host=="www.eveonline.com"||u.Host=="store.eveonline.com"))Process.Start(new ProcessStartInfo(url){UseShellExecute=true});}catch(Exception ex){StatusText.Text=ex.Message;}}
}
