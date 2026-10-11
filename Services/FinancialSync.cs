using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public static class FinancialSync
{
    public static async Task RunAsync(MarketOrdersService market, PilotSyncSettings settings, IReadOnlyList<EvePilotProfile> pilots, CancellationToken ct)
    {
        if (market.Busy || settings.Error.Length > 0) return;
            foreach (var pilot in pilots)
            {
                ct.ThrowIfCancellationRequested();
                long[] target = { pilot.CharacterId }; var status = new List<string>();
                if (settings.Enabled(pilot.CharacterId, "wallet"))
                {
                    settings.Activity[pilot.CharacterId] = "Syncing wallet...";
                    await market.RefreshBalanceAsync(pilots, ct, target);
                    await market.RefreshJournalAsync(pilots, ct, target);
                    await market.RefreshTransactionsAsync(pilots, ct, target);
                    var balance = market.State.Balances.GetValueOrDefault(pilot.CharacterId);
                    status.Add(balance?.Error.Length > 0 ? balance.Error : balance?.Amount != null ? $"Balance saved {balance.Updated:dd MMM HH:mm} UTC" : "Balance not loaded");
                    status.Add(market.JournalStatus); status.Add(market.TransactionStatus);
                }
                if (settings.Enabled(pilot.CharacterId, "market"))
                {
                    settings.Activity[pilot.CharacterId] = "Syncing market orders...";
                    await market.RefreshAsync(pilots, ct, target);
                    await market.RefreshHistoryAsync(pilots, ct, target);
                    var orders = market.State.Characters.FirstOrDefault(c => c.CharacterId == pilot.CharacterId);
                    status.Add(orders?.Error.Length > 0 ? orders.Error : orders != null ? $"Orders saved {orders.Updated:dd MMM HH:mm} UTC" : "Orders not loaded");
                    status.Add(market.HistoryStatus);
                }
                if (market.Error.Length > 0) status.Add(market.Error);
                settings.Activity[pilot.CharacterId] = status.Count == 0 ? "Wallet and market automatic sync off" : string.Join("\n", status.Where(s => s.Length > 0));
            }
    }
}
