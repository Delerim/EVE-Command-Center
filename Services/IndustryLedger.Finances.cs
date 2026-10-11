using System.Text.Json;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
internal sealed partial class IndustryLedger
{
    internal List<ProjectCashFlow.Entry> ProjectFinancialEntries(Guid project)
    {
        using var c = Open(); using var tx = c.BeginTransaction(deferred: true);
        using var cmd = Command(c, "SELECT a.owner_id,a.json,t.json FROM purchase_allocations a JOIN wallet_transactions t ON t.owner_id=a.owner_id AND t.transaction_id=a.transaction_id UNION ALL SELECT a.owner_id,a.json,t.json FROM sale_allocations a JOIN wallet_transactions t ON t.owner_id=a.owner_id AND t.transaction_id=a.transaction_id");
        using var reader = cmd.ExecuteReader(); var result = new List<ProjectCashFlow.Entry>();
        while (reader.Read())
        {
            var trade = JsonSerializer.Deserialize<MarketTransaction>(reader.GetString(2))!;
            foreach (var split in JsonSerializer.Deserialize<List<PurchaseAllocation>>(reader.GetString(1))!.Where(a => a.Purpose == "Project" && a.ProjectId == project))
                result.Add(new(reader.GetInt64(0), trade, split));
        }
        return result;
    }
}
