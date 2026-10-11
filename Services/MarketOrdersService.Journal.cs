using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

public sealed partial class MarketOrdersService
{
    public List<MarketJournalRecord> Journal { get; private set; } = new();
    public string JournalStatus { get; private set; } = "Wallet activity has not been refreshed.";
    internal static bool SameJournal(EveWalletJournalEntry a, EveWalletJournalEntry b) =>
        a.Id == b.Id && a.Date == b.Date && a.RefType == b.RefType && a.Amount == b.Amount && a.Balance == b.Balance &&
        a.Description == b.Description && a.Reason == b.Reason && a.ContextId == b.ContextId && a.ContextType == b.ContextType &&
        a.FirstPartyId == b.FirstPartyId && a.SecondPartyId == b.SecondPartyId && a.Tax == b.Tax && a.TaxReceiverId == b.TaxReceiverId;
    internal static void ValidateJournal(List<EveWalletJournalEntry> rows)
    {
        if (rows.Select(r => r.Id).Distinct().Count() != rows.Count || rows.Any(r => r.Id <= 0 || r.Date == default || string.IsNullOrWhiteSpace(r.RefType) || r.Description == null))
            throw new InvalidDataException("Invalid wallet activity response.");
    }
    public async Task RefreshJournalAsync(IReadOnlyList<EvePilotProfile> linked, CancellationToken ct)
    {
        if (Busy || Error.Length > 0) return;
        Busy = true; Progress = "Preparing wallet activity..."; var messages = new List<string>();
        try
        {
            foreach (long id in State.SelectedCharacters.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var pilot = linked.FirstOrDefault(p => p.CharacterId == id);
                if (pilot == null || !pilot.Scopes.Contains(WalletScope))
                { messages.Add((pilot?.CharacterName ?? "Unlinked pilot") + ": wallet permission required; saved entries retained."); continue; }
                try
                {
                    await Task.Run(() => Ledger.Ensure(), ct);
                    var due = Ledger.NextJournal(id);
                    if (DateTimeOffset.UtcNow < due) { messages.Add($"{pilot.CharacterName}: wallet activity cached until {due:HH:mm} UTC."); continue; }
                    var token = await _token(pilot, ct); var rows = new List<EveWalletJournalEntry>();
                    var observed = DateTimeOffset.UtcNow; var next = observed; int totalPages = 1;
                    for (int page = 1; page <= totalPages; page++)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://esi.evetech.net/characters/{id}/wallet/journal?page={page}");
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        request.Headers.TryAddWithoutValidation("X-Compatibility-Date", "2026-08-25");
                        Progress = $"{pilot.CharacterName}: waiting for wallet activity page {page} through the EVE request queue...";
                        using var response = await _http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
                        if (!response.Headers.TryGetValues("X-Pages", out var values) || !int.TryParse(values.Single(), out int count) || count < 1 || count > 10000 || (page > 1 && count != totalPages))
                            throw new InvalidDataException("Wallet page count missing or changed; refresh again.");
                        totalPages = count;
                        var entries = JsonSerializer.Deserialize<List<EveWalletJournalEntry>>(await response.Content.ReadAsStringAsync(ct)) ?? throw new InvalidDataException("Empty wallet response.");
                        ValidateJournal(entries); rows.AddRange(entries);
                        var pageDue = RefreshDue(response, DateTimeOffset.UtcNow, TimeSpan.FromHours(1)); if (pageDue > next) next = pageDue;
                    }
                    ValidateJournal(rows); ct.ThrowIfCancellationRequested();
                    await Task.Run(() => Ledger.ImportJournal(id, rows, observed, next), ct);
                    messages.Add($"{pilot.CharacterName}: {rows.Count:N0} wallet entries refreshed at {observed:HH:mm} UTC.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { messages.Add($"{pilot.CharacterName}: {Failure(ex)} Wallet activity retained."); }
            }
            if (Ledger.Exists) Journal = await Task.Run(() => Ledger.Journal(), ct);
        }
        finally { JournalStatus = string.Join("\n", messages); Busy = false; }
    }
}
