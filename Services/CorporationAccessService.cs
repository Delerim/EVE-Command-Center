using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>Capabilities come from successful ESI reads, never job titles or scopes alone.</summary>
public sealed class CorporationAccessService
{
    public sealed class LinkState
    {
        public bool SetupCompleted { get; set; }
        public long MoonCharacterId { get; set; }
        public long ContractCharacterId { get; set; }
        public long VerifiedMoonId { get; set; }
        public long VerifiedContractId { get; set; }
        public DateTimeOffset MoonVerifiedUtc { get; set; }
        public DateTimeOffset ContractVerifiedUtc { get; set; }
    }
    private readonly EveSsoService _sso;
    private readonly HttpClient _http;
    private readonly Func<EvePilotProfile, CancellationToken, Task<string>> _token;
    private readonly string _file;
    private readonly Func<Task<IReadOnlyList<EvePilotProfile>>> _profiles;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public LinkState State { get; }
    public bool CanReadMoons { get; private set; }
    public bool CanReadContracts { get; private set; }
    public string MoonStatus { get; private set; } = "Not verified - link a holding-corporation character.";
    public string ContractStatus { get; private set; } = "Not verified - link a corporation contract reader.";
    public event Action? Changed;

    public CorporationAccessService(EveSsoService sso, HttpClient? http = null, string? directory = null, Func<EvePilotProfile, CancellationToken, Task<string>>? token = null, Func<Task<IReadOnlyList<EvePilotProfile>>>? profiles = null)
    {
        _sso = sso;
        _profiles = profiles ?? sso.LoadPilotsAsync;
        _token = token ?? ((pilot, ct) => _sso.GetAccessTokenForAsync(pilot, ct));
        _http = http ?? EsiHttp.CreateClient();
        _file = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center"), "corporation-access.json");
        try { State = JsonSerializer.Deserialize<LinkState>(File.ReadAllText(_file)) ?? new(); }
        catch { State = new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(State));
        File.Move(_file + ".tmp", _file, true);
    }
    public async Task ValidateAsync(IReadOnlyList<EvePilotProfile> pilots, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // UI windows can outlive an SSO reconnect. Read after acquiring the
            // gate so queued validations cannot revoke access using old scopes.
            pilots = await _profiles();
            long moonId = State.MoonCharacterId, contractId = State.ContractCharacterId;
            var moon = await ProbeAsync(pilots.FirstOrDefault(p => p.CharacterId == moonId), true, cancellationToken);
            Apply(moonId, true, moon);
            Changed?.Invoke();
            pilots = await _profiles(); // Moon checks may have awaited a reconnect or provider cooldown.
            var contracts = await ProbeAsync(pilots.FirstOrDefault(p => p.CharacterId == contractId), false, cancellationToken);
            Apply(contractId, false, contracts);
            Save();
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }
    private void Apply(long id, bool moons, (bool? Allowed, string Status) result)
    {
        long selected = moons ? State.MoonCharacterId : State.ContractCharacterId;
        long verified = moons ? State.VerifiedMoonId : State.VerifiedContractId;
        var when = moons ? State.MoonVerifiedUtc : State.ContractVerifiedUtc;
        if (id != selected) result = (false, "Reader changed; verification pending.");
        bool allowed = result.Allowed ?? (id > 0 && id == verified && DateTimeOffset.UtcNow - when < TimeSpan.FromHours(24));
        if (result.Allowed == true) { verified = id; when = DateTimeOffset.UtcNow; }
        if (result.Allowed == false) { verified = 0; when = default; }
        string status = result.Status + (result.Allowed == null && allowed ? " Keeping recently verified access." : "");
        if (moons) { CanReadMoons = allowed; MoonStatus = status; State.VerifiedMoonId = verified; State.MoonVerifiedUtc = when; }
        else { CanReadContracts = allowed; ContractStatus = status; State.VerifiedContractId = verified; State.ContractVerifiedUtc = when; }
    }
    private async Task<(bool?, string)> ProbeAsync(EvePilotProfile? pilot, bool moons, CancellationToken ct)
    {
        if (pilot == null) return (false, "Not linked - this view is hidden.");
        if (moons ? !MoonReportService.HasRequiredScopes(pilot) : !ContractService.CanRead(pilot))
            return (false, pilot.CharacterName + ": reconnect to approve the required scopes.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var token = await _token(pilot, timeout.Token);
            return await ProbeEndpointsAsync(_http, token, pilot.CharacterId, moons, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        { return (false, pilot.CharacterName + ": EVE denied access. Reauthorize this reader and check its corporation roles."); }
        catch (Exception ex) { return (null, pilot.CharacterName + ": verification delayed; will retry automatically. " + ex.Message); }
    }
    public static async Task<(bool Allowed, string Status)> ProbeEndpointsAsync(HttpClient http, string token, long characterId, bool moons, CancellationToken ct = default)
    {
        async Task<JsonDocument> Read(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://esi.evetech.net/latest/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("EVE-Command-Center/2.6.0");
            request.Headers.TryAddWithoutValidation("X-Compatibility-Date", "2026-08-25");
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        using var character = await Read($"characters/{characterId}/");
        var corp = character.RootElement.GetProperty("corporation_id").GetInt64();
        var name = character.RootElement.GetProperty("name").GetString();
        using var data = await Read(moons ? $"corporation/{corp}/mining/extractions/" : $"corporations/{corp}/contracts/");
        if (data.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Unexpected ESI response.");
        if (moons)
        {
            using var structures = await Read($"corporations/{corp}/structures/");
            if (structures.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Unexpected structure response.");
        }
        using var corporation = await Read($"corporations/{corp}/");
        return (true, $"Verified: {name} | {corporation.RootElement.GetProperty("name").GetString()}");
    }
}
