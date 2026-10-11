using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public static class ExpenseEvidence
{
    // Explicit debits only. Never infer an expense from a transfer, escrow or trade payment.
    // Ref types verified against https://esi.evetech.net/meta/openapi.json.
    public static string Name(EveWalletJournalEntry entry) => entry.RefType switch
    {
        "brokers_fee" => "Broker fee", "transaction_tax" => "Sales tax",
        "manufacturing" => "Manufacturing charge", "industry_job_tax" => "Industry job tax", _ => "Other wallet activity"
    };
    public static bool Eligible(EveWalletJournalEntry entry) => entry.Amount < 0 && entry.Amount != decimal.MinValue &&
        entry.RefType is "brokers_fee" or "transaction_tax" or "manufacturing" or "industry_job_tax";
    public static decimal Charge(EveWalletJournalEntry entry) => Eligible(entry) ? -entry.Amount!.Value
        : throw new InvalidOperationException("Choose a confirmed broker fee, sales tax or manufacturing charge with a debit amount.");
    public static AttributedExpense[] Filter(IEnumerable<AttributedExpense> entries, DateTimeOffset since, DateTimeOffset until, long? owner)
    {
        if (until < since) throw new ArgumentException("The end of the period must follow its start.");
        return entries.Where(e => e.Entry.Date >= since && e.Entry.Date <= until && (!owner.HasValue || e.OwnerId == owner))
            .OrderByDescending(e => e.Entry.Date).ThenBy(e => e.OwnerId).ThenBy(e => e.Entry.Id).ThenBy(e => e.Allocation.Id).ToArray();
    }
}
