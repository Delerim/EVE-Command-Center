namespace EveCommandCenter.Models;
public sealed record MarketJournalRecord(long OwnerId, DateTimeOffset Observed, EveWalletJournalEntry Entry);
