using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
namespace EveCommandCenter.Views;
public partial class MarketOrdersWindow
{
    private void RenderBalance()
    {
        var balance = _service?.State.Balances?.GetValueOrDefault(_activePilot);
        WalletBalanceText.Text = balance?.Amount is {} amount ? $"{amount:N2} ISK" : "Balance not loaded";
        WalletBalanceStatus.Text = balance == null ? "Refresh Wallet overview to load" : balance.Error.Length > 0 ? balance.Error + (balance.Amount != null ? " - previous balance retained" : "")
            : $"Balance at {balance.Updated:dd MMM HH:mm} UTC" + (balance.NextRefresh <= DateTimeOffset.UtcNow ? " - refresh due" : "");
    }
    private void RenderWalletOverview()
    {
        var entries = _service!.Journal.Where(j => j.OwnerId == _activePilot).Select(j => j.Entry).ToArray();
        var now = DateTimeOffset.UtcNow; var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        void Show(DateTimeOffset since, TextBlock incoming, TextBlock outgoing, TextBlock net)
        {
            var period = entries.Where(e => e.Date >= since && e.Date <= now).ToArray();
            if (entries.Length == 0) { incoming.Text = outgoing.Text = net.Text = "No entries loaded"; return; }
            decimal credit = period.Where(e => e.Amount > 0).Sum(e => e.Amount ?? 0), debit = period.Where(e => e.Amount < 0).Sum(e => -(e.Amount ?? 0));
            incoming.Text = $"{credit:N2} ISK"; outgoing.Text = $"{debit:N2} ISK"; net.Text = $"{credit - debit:N2} ISK";
        }
        Show(today, TodayIn, TodayOut, TodayNet); Show(now.AddDays(-7), WeekIn, WeekOut, WeekNet);
        Totals.Text = _activePilot > 0 ? $"{PilotName(_activePilot)} | Wallet overview" : "Choose a pilot";
        WalletOverviewNote.Text = $"Imported wallet activity only. {entries.Count(e => e.Amount == null):N0} entries without an amount are excluded. Refresh Wallet loads balance, wallet activity and trades through their shared caches.";
        HistoryStatus.Text = _service.JournalStatus; Status.Text = _service.Error;
        ObservationSummary.Text = "Today's and seven-day figures cover imported entries; missing or failed history can leave them incomplete.";
        OrderDetail.Text = "Use Wallet activity for individual entries, Purchases / Sales for trades, and PLEX for its trading history.";
    }
}
