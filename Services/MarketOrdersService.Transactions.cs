using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;

public sealed partial class MarketOrdersService
{
    public const string WalletScope = "esi-wallet.read_character_wallet.v1";
    public List<MarketTransactionRecord> Transactions { get; private set; } = new();
    public Dictionary<long, string> TransactionReports { get; } = new();
    public string TransactionStatus { get; private set; } = "Transactions have not been refreshed.";
    internal static bool SameTransaction(MarketTransaction a, MarketTransaction b) => a.Id == b.Id && a.TypeId == b.TypeId && a.Quantity == b.Quantity && a.UnitPrice == b.UnitPrice && a.Date == b.Date && a.Buy == b.Buy && a.Personal == b.Personal && a.LocationId == b.LocationId && a.ClientId == b.ClientId && a.JournalRefId == b.JournalRefId;
    internal static void ValidateTransactions(List<MarketTransaction> rows)
    {
        if (rows.Select(r => r.Id).Distinct().Count() != rows.Count || rows.Any(r => r.Id <= 0 || r.TypeId <= 0 || r.Quantity <= 0 || r.UnitPrice < 0 || r.LocationId <= 0 || r.ClientId <= 0 || r.JournalRefId <= 0 || r.Date == default))
            throw new InvalidDataException("Invalid transaction response.");
        foreach (var row in rows) _ = row.Total;
    }
    public async Task RefreshTransactionsAsync(IReadOnlyList<EvePilotProfile> linked, CancellationToken ct, IReadOnlyCollection<long>? characterIds = null)
    {
        if (Busy || Error.Length > 0) return;
        Busy = true; Progress = "Preparing transactions..."; var messages = new List<string>();
        void Report(long id, string message) { messages.Add(message); TransactionReports[id] = message; }
        try
        {
            foreach (long id in (characterIds ?? State.SelectedCharacters).ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var pilot = linked.FirstOrDefault(p => p.CharacterId == id);
                if (pilot == null || !pilot.Scopes.Contains(WalletScope))
                { Report(id, (pilot?.CharacterName ?? "Unlinked pilot") + ": wallet permission required; saved transactions retained."); continue; }
                try
                {
                    await Task.Run(() => Ledger.Ensure(), ct);
                    var due = Ledger.NextTransactions(id);
                    if (DateTimeOffset.UtcNow < due) { Report(id, $"{pilot.CharacterName}: transactions cached until {due:HH:mm} UTC."); continue; }
                    var token = await _token(pilot, ct); var rows = new List<MarketTransaction>();
                    long? cursor = null; int pages = 0;
                    var observed = DateTimeOffset.UtcNow; var next = observed;
                    while (true)
                    {
                        if (++pages > 10000) throw new InvalidDataException("Transaction pagination did not finish.");
                        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://esi.evetech.net/characters/{id}/wallet/transactions" + (cursor.HasValue ? $"?from_id={cursor.Value}" : ""));
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        request.Headers.TryAddWithoutValidation("X-Compatibility-Date", "2026-08-25");
                        Progress = $"{pilot.CharacterName}: waiting for transaction page {pages} through the EVE request queue...";
                        using var response = await _http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
                        var page = JsonSerializer.Deserialize<List<MarketTransaction>>(await response.Content.ReadAsStringAsync(ct)) ?? throw new InvalidDataException("Empty transaction response.");
                        ValidateTransactions(page);
                        var pageDue = RefreshDue(response, DateTimeOffset.UtcNow, TimeSpan.FromHours(1)); if (pageDue > next) next = pageDue;
                        if (page.Count == 0) break;
                        if (cursor.HasValue)
                        {
                            if (page.Any(r => r.Id > cursor.Value)) throw new InvalidDataException("Transaction cursor did not advance.");
                            foreach (var boundary in page.Where(r => r.Id == cursor.Value))
                                if (!SameTransaction(rows.Single(r => r.Id == boundary.Id), boundary)) throw new InvalidDataException("Conflicting transaction boundary.");
                            page.RemoveAll(r => r.Id == cursor.Value);
                            if (page.Count == 0) break;
                        }
                        rows.AddRange(page); cursor = page.Min(r => r.Id);
                    }
                    ValidateTransactions(rows);
                    var known = Transactions.Select(r => r.Transaction).ToArray();
                    foreach (var row in rows)
                    { row.ResolvedName = known.FirstOrDefault(r => r.TypeId == row.TypeId)?.ResolvedName; row.ResolvedLocation = known.FirstOrDefault(r => r.LocationId == row.LocationId)?.ResolvedLocation; }
                    if (_resolveNames)
                    {
                        Progress = "Data received. Looking up item and station names...";
                        await ResolveOptionalNames(async metadataCt =>
                        {
                            var names = await _sso.ResolveIndustryTypeNamesAsync(rows.Select(r => r.TypeId), metadataCt);
                            foreach (var row in rows)
                                if (names.GetValueOrDefault(row.TypeId) is {} name && !name.StartsWith("Type ") && name != "Item name unavailable") row.ResolvedName = name;
                            foreach (var location in rows.Select(r => r.LocationId).Distinct())
                                if (await _sso.ResolveDisplayLocationAsync(location, pilot, metadataCt) is {} name)
                                    foreach (var row in rows.Where(r => r.LocationId == location)) row.ResolvedLocation = name;
                        }, ct);
                    }
                    ct.ThrowIfCancellationRequested();
                    await Task.Run(() => Ledger.ImportTransactions(id, rows, observed, next), ct);
                    Report(id, $"{pilot.CharacterName}: {rows.Count(r => r.Personal):N0} personal transactions refreshed at {observed:HH:mm} UTC.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { Report(id, $"{pilot.CharacterName}: {Failure(ex)} Transactions retained."); }
            }
            if (Ledger.Exists) Transactions = await Task.Run(() => Ledger.Transactions(), ct);
        }
        finally { TransactionStatus = string.Join("\n", messages); Busy = false; DataVersion++; }
    }
}
