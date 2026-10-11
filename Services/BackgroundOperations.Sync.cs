using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public sealed partial class BackgroundOperations
{
    private bool _marketSyncBusy;
    private DateTimeOffset _nextMarketSync;
    public void ScheduleFinancialSync() => _nextMarketSync = default;
    private async Task SyncMarketAndWalletAsync(IReadOnlyList<EvePilotProfile> pilots)
    {
        if (_marketSyncBusy || MarketOrders.Busy || DateTimeOffset.UtcNow < _nextMarketSync || SyncSettings.Error.Length > 0) return;
        _marketSyncBusy = true; _nextMarketSync = DateTimeOffset.UtcNow.AddMinutes(5);
        try
        {
            await FinancialSync.RunAsync(MarketOrders, SyncSettings, pilots, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { EsiDiagnostics.Write("Scheduled market/wallet sync: " + ex.GetType().Name); }
        finally { _marketSyncBusy = false; }
    }
}
