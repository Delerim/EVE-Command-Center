using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public static class FinancialDashboard
{
    public sealed record Summary(string Orders, string OrderCoverage, string Wallet, string WalletCoverage, string Trades);
    public static Summary Build(MarketOrdersService market, PilotSyncSettings settings, IReadOnlyList<EvePilotProfile> pilots, DateTimeOffset now)
    {
        var ordersPilots = pilots.Where(p => settings.Get(p.CharacterId).ShowMarket).ToArray();
        var walletPilots = pilots.Where(p => settings.Get(p.CharacterId).ShowWallet).ToArray();
        var ids = walletPilots.Select(p => p.CharacterId).ToHashSet();
        var snapshots = market.State.Characters.Where(c => ordersPilots.Any(p => p.CharacterId == c.CharacterId) && c.Updated != default).ToArray();
        var terminal = market.History.Select(h => (h.OwnerId, h.Order.OrderId)).ToHashSet();
        var orders = snapshots.SelectMany(c => c.Orders.Where(o => !o.Corporation && !terminal.Contains((c.CharacterId, o.OrderId)))).ToArray();
        string orderText = ordersPilots.Length == 0 ? "No market pilots shown" : snapshots.Length == 0 ? "Orders not loaded" : $"{orders.Count(o => o.Buy):N0} buy / {orders.Count(o => !o.Buy):N0} sell orders\nBuy commitment {orders.Where(o => o.Buy).Sum(o => o.RemainingValue):N2} ISK\nSell listings {orders.Where(o => !o.Buy).Sum(o => o.RemainingValue):N2} ISK";
        string orderCoverage = $"{snapshots.Length}/{ordersPilots.Length} pilots have saved orders; {snapshots.Count(c => c.Error.Length > 0 || c.NextRefresh <= now)} need refresh/review. {ordersPilots.Count(p => !p.Scopes.Contains(MarketOrdersService.Scope))} missing permission. Commitments are not spending.";
        var balances = market.State.Balances.Where(b => ids.Contains(b.Key) && b.Value.Amount.HasValue).ToArray();
        var journal = market.Journal.Where(j => ids.Contains(j.OwnerId)).Select(j => j.Entry).ToArray();
        string Flow(int days)
        {
            if (journal.Length == 0) return "No entries imported";
            var rows = journal.Where(j => j.Date >= now.AddDays(-days) && j.Date <= now).ToArray();
            if (rows.Any(j => !j.Amount.HasValue)) return "Incomplete (missing amounts)";
            return rows.Sum(j => j.Amount!.Value).ToString("N2") + " ISK";
        }
        string walletText = walletPilots.Length == 0 ? "No wallet pilots shown" : $"Saved balances: {(balances.Length == 0 ? "Not loaded" : balances.Sum(b => b.Value.Amount!.Value).ToString("N2") + " ISK")}\n7-day cash flow: {Flow(7)}\n30-day cash flow: {Flow(30)}";
        string coverage = $"Balances {balances.Length}/{walletPilots.Length} pilots; {balances.Count(b => b.Value.Error.Length > 0 || b.Value.NextRefresh <= now)} need refresh/review. {walletPilots.Count(p => !p.Scopes.Contains(MarketOrdersService.WalletScope))} missing permission. Imported cash flow may be incomplete; it is not profit.";
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var trades = market.Transactions.Where(t => ids.Contains(t.OwnerId) && t.Transaction.Date >= today && t.Transaction.Date <= now).ToArray();
        string tradeText = market.Transactions.Any(t => ids.Contains(t.OwnerId)) ? $"Today (UTC): purchases {trades.Where(t => t.Transaction.Buy).Sum(t => t.Transaction.Total):N2} ISK / sales {trades.Where(t => !t.Transaction.Buy).Sum(t => t.Transaction.Total):N2} ISK" : "Today's trades: no imported history";
        return new(orderText, orderCoverage, walletText, coverage, tradeText);
    }
}
