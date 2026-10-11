using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EveCommandCenter.Models;
namespace EveCommandCenter.Services;
public sealed partial class MarketOrdersService
{
    public async Task RefreshBalanceAsync(IReadOnlyList<EvePilotProfile> linked, CancellationToken ct)
    {
        if (Busy || Error.Length > 0) return;
        Busy = true;
        try
        {
            foreach (long id in State.SelectedCharacters.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var pilot = linked.FirstOrDefault(p => p.CharacterId == id);
                var next = Copy(); next.Balances ??= new();
                if (!next.Balances.TryGetValue(id, out var balance)) next.Balances[id] = balance = new();
                if (pilot == null || !pilot.Scopes.Contains(WalletScope))
                { balance.Error = "Wallet permission required"; Persist(next); continue; }
                if (balance.NextRefresh > DateTimeOffset.UtcNow && balance.Error.Length == 0) continue;
                try
                {
                    Progress = $"{pilot.CharacterName}: refreshing wallet balance...";
                    var token = await _token(pilot, ct);
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"https://esi.evetech.net/characters/{id}/wallet");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.TryAddWithoutValidation("X-Compatibility-Date", "2026-08-25");
                    using var response = await _http.SendAsync(request, ct); response.EnsureSuccessStatusCode();
                    decimal amount = JsonSerializer.Deserialize<decimal>(await response.Content.ReadAsStringAsync(ct));
                    ct.ThrowIfCancellationRequested();
                    balance.Amount = amount; balance.Updated = DateTimeOffset.UtcNow;
                    balance.NextRefresh = RefreshDue(response, balance.Updated, TimeSpan.FromMinutes(2)); balance.Error = "";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { balance.Error = Failure(ex); }
                Persist(next);
            }
        }
        finally { Busy = false; }
    }
}
