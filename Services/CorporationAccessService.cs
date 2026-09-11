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
    }
    private readonly EveSsoService _sso;
    private readonly HttpClient _http;
    private readonly string _file;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public LinkState State { get; }
    public bool CanReadMoons { get; private set; }
    public bool CanReadContracts { get; private set; }
    public string MoonStatus { get; private set; } = "Not verified — link a holding-corporation character.";
    public string ContractStatus { get; private set; } = "Not verified — link a corporation contract reader.";
    public event Action? Changed;

    public CorporationAccessService(EveSsoService sso, HttpClient? http = null, string? directory = null)
    {
        _sso = sso;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
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
            long moonId = State.MoonCharacterId, contractId = State.ContractCharacterId;
            var moon = await ProbeAsync(pilots.FirstOrDefault(p => p.CharacterId == moonId), true, cancellationToken);
            var contracts = await ProbeAsync(pilots.FirstOrDefault(p => p.CharacterId == contractId), false, cancellationToken);
            (CanReadMoons, MoonStatus) = moonId == State.MoonCharacterId ? moon : (false, "Reader changed; verification pending.");
            (CanReadContracts, ContractStatus) = contractId == State.ContractCharacterId ? contracts : (false, "Reader changed; verification pending.");
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }
    private async Task<(bool, string)> ProbeAsync(EvePilotProfile? pilot, bool moons, CancellationToken ct)
    {
        if (pilot == null) return (false, "Not linked — this view is hidden.");
        if (moons ? !MoonReportService.HasRequiredScopes(pilot) : !ContractService.CanRead(pilot))
            return (false, pilot.CharacterName + ": reconnect to approve the required scopes.");
        try
        {
            var token = await _sso.GetAccessTokenForAsync(pilot, ct);
            return await ProbeEndpointsAsync(_http, token, pilot.CharacterId, moons, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (false, pilot.CharacterName + ": access could not be verified. " + ex.Message); }
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
        return (true, $"Verified: {name} • {corporation.RootElement.GetProperty("name").GetString()}");
    }
}
