using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

public sealed class EveSsoService
{
    public const string ClientId = "641eab190a4a4ddcb708981b967eb8b2";
    public const string RedirectUri = "http://localhost:17361/callback/";
    public const int CallbackPort = 17361;

    private const string AuthorizeEndpoint = "https://login.eveonline.com/v2/oauth/authorize";
    private const string TokenEndpoint = "https://login.eveonline.com/v2/oauth/token";
    private const string EsiBase = "https://esi.evetech.net/latest";

    public static readonly string[] InitialScopes =
    {
        "esi-skills.read_skills.v1",
        "esi-skills.read_skillqueue.v1",
        "esi-wallet.read_character_wallet.v1",
        "esi-location.read_location.v1",
        "esi-location.read_ship_type.v1",
        "esi-clones.read_implants.v1",
        "esi-assets.read_assets.v1",
        "esi-fittings.read_fittings.v1"
    };

    private sealed class TokenCache
    {
        public string AccessToken { get; init; } = "";
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private sealed class VerifyResponse
    {
        public long CharacterID { get; set; }
        public string CharacterName { get; set; } = "";
        public string[] Scopes { get; set; } = Array.Empty<string>();
    }

    private sealed class PilotContext
    {
        public string SystemName { get; init; } = "";
        public string ShipName { get; init; } = "";
    }

    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly string _pilotFile;
    private readonly Dictionary<long, TokenCache> _accessTokens = new();
    private readonly Dictionary<int, string> _typeNames = new();
    private readonly Dictionary<int, EveUniverseType> _typeDetails = new();
    private readonly Dictionary<int, string> _systemNames = new();
    private readonly Dictionary<long, AssetCacheEntry> _assetCache = new();

    private sealed class AssetCacheEntry
    {
        public DateTimeOffset ExpiresAt { get; init; }
        public List<EveAssetItem> Items { get; init; } = new();
    }

    private const int CharismaAttributeId = 164;
    private const int IntelligenceAttributeId = 165;
    private const int MemoryAttributeId = 166;
    private const int PerceptionAttributeId = 167;
    private const int WillpowerAttributeId = 168;

    private const int CharismaBonusDogmaId = 175;
    private const int IntelligenceBonusDogmaId = 176;
    private const int MemoryBonusDogmaId = 177;
    private const int PerceptionBonusDogmaId = 178;
    private const int WillpowerBonusDogmaId = 179;

    public EveSsoService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EVE-Command-Center/0.1 (+https://github.com/Delerim/EVE-MultiPreview)");
        // Compatibility dates switch at 11:00 UTC. Pin to a reviewed,
        // already-valid date rather than using the local calendar date:
        // a "today" value before 11:00 UTC is treated by ESI as future
        // and produces HTTP 400.
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Compatibility-Date", "2026-08-25");

        _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EVE Command Center", "PilotData");
        Directory.CreateDirectory(root);
        _pilotFile = Path.Combine(root, "pilots.json");
    }

    public async Task<IReadOnlyList<EvePilotProfile>> LoadPilotsAsync()
    {
        if (!File.Exists(_pilotFile))
            return Array.Empty<EvePilotProfile>();

        try
        {
            string json = await File.ReadAllTextAsync(_pilotFile);
            var pilots = JsonSerializer.Deserialize<List<EvePilotProfile>>(json, _json)
                         ?? new List<EvePilotProfile>();
            return pilots
                .OrderBy(p => p.CharacterName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PilotSSO] Could not load pilots: {ex.Message}");
            return Array.Empty<EvePilotProfile>();
        }
    }

    public async Task<EvePilotProfile> AddCharacterAsync(
        CancellationToken cancellationToken = default)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(
            SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(24));

        using var listener = new TcpListener(IPAddress.Loopback, CallbackPort);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException(
                $"Cannot start the EVE SSO callback on localhost:{CallbackPort}. " +
                "Another program may already be using that port.", ex);
        }

        string authUrl =
            AuthorizeEndpoint +
            "?response_type=code" +
            "&client_id=" + Uri.EscapeDataString(ClientId) +
            "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
            "&scope=" + Uri.EscapeDataString(string.Join(" ", InitialScopes)) +
            "&state=" + Uri.EscapeDataString(state) +
            "&code_challenge=" + Uri.EscapeDataString(challenge) +
            "&code_challenge_method=S256";

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        try
        {
            using var timeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));

            using TcpClient client =
                await listener.AcceptTcpClientAsync(timeout.Token);
            using NetworkStream stream = client.GetStream();
            using var reader = new StreamReader(
                stream, Encoding.ASCII, false, 4096, leaveOpen: true);

            string? requestLine = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(requestLine))
                throw new InvalidOperationException("The EVE SSO callback was empty.");

            while (true)
            {
                string? line = await reader.ReadLineAsync(timeout.Token);
                if (string.IsNullOrEmpty(line))
                    break;
            }

            string[] requestParts = requestLine.Split(' ');
            if (requestParts.Length < 2)
                throw new InvalidOperationException("The EVE SSO callback was malformed.");

            var callback =
                new Uri("http://localhost:" + CallbackPort + requestParts[1]);
            var query = ParseQuery(callback.Query);

            if (query.TryGetValue("error", out string? oauthError))
            {
                string detail =
                    query.TryGetValue("error_description", out string? d)
                        ? d : oauthError;
                await WriteBrowserResponseAsync(
                    stream, false, "Authorization cancelled", detail);
                throw new InvalidOperationException(
                    "EVE SSO authorization failed: " + detail);
            }

            if (!query.TryGetValue("state", out string? returnedState) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(state),
                    Encoding.UTF8.GetBytes(returnedState)))
            {
                await WriteBrowserResponseAsync(
                    stream, false, "Authorization rejected",
                    "The OAuth state value did not match.");
                throw new InvalidOperationException(
                    "EVE SSO state validation failed.");
            }

            if (!query.TryGetValue("code", out string? code) ||
                string.IsNullOrWhiteSpace(code))
            {
                await WriteBrowserResponseAsync(
                    stream, false, "Authorization failed",
                    "No authorization code was returned.");
                throw new InvalidOperationException(
                    "EVE SSO did not return an authorization code.");
            }

            EveTokenResponse token =
                await ExchangeAuthorizationCodeAsync(
                    code, verifier, cancellationToken);

            VerifyResponse identity =
                await VerifyIdentityAsync(token.AccessToken, cancellationToken);

            if (identity.CharacterID <= 0 ||
                string.IsNullOrWhiteSpace(identity.CharacterName))
                throw new InvalidOperationException(
                    "EVE SSO returned an invalid character identity.");

            if (string.IsNullOrWhiteSpace(token.RefreshToken))
                throw new InvalidOperationException(
                    "EVE SSO did not return a refresh token.");

            EveCredentialStore.Write(identity.CharacterID, token.RefreshToken);

            _accessTokens[identity.CharacterID] = new TokenCache
            {
                AccessToken = token.AccessToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Max(60, token.ExpiresIn))
            };

            var profile = new EvePilotProfile
            {
                CharacterId = identity.CharacterID,
                CharacterName = identity.CharacterName,
                Scopes = identity.Scopes.Length > 0
                    ? identity.Scopes : InitialScopes,
                AddedUtc = DateTime.UtcNow
            };

            await UpsertPilotAsync(profile);

            await WriteBrowserResponseAsync(
                stream, true, "Character connected",
                $"{profile.CharacterName} is now connected to EVE Command Center. " +
                "You can close this tab.");

            return profile;
        }
        finally
        {
            listener.Stop();
        }
    }

    public async Task RemoveCharacterAsync(long characterId)
    {
        EveCredentialStore.Delete(characterId);
        _accessTokens.Remove(characterId);
        _assetCache.Remove(characterId);

        var pilots = (await LoadPilotsAsync()).ToList();
        pilots.RemoveAll(p => p.CharacterId == characterId);
        await SavePilotsAsync(pilots);
    }

    public async Task<IReadOnlyDictionary<string, long>>
        ResolveCharacterIdsAsync(
            IEnumerable<string> names,
            CancellationToken cancellationToken = default)
    {
        string[] requested =
            names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (requested.Length == 0)
        {
            return new Dictionary<string, long>(
                StringComparer.OrdinalIgnoreCase);
        }

        EveUniverseIdsResponse response =
            await PostPublicAsync<EveUniverseIdsResponse>(
                "/universe/ids/",
                requested,
                cancellationToken);

        return response.Characters
            .Where(
                character =>
                    character.Id > 0 &&
                    !string.IsNullOrWhiteSpace(character.Name))
            .GroupBy(
                character => character.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Id,
                StringComparer.OrdinalIgnoreCase);
    }

    public async Task<EveCurrentShipView> GetCurrentShipIdentityAsync(
        EvePilotProfile pilot,
        CancellationToken cancellationToken = default)
    {
        string token =
            await GetAccessTokenAsync(
                pilot,
                cancellationToken);

        EveCharacterShipResponse ship =
            await GetEsiAsync<EveCharacterShipResponse>(
                $"/characters/{pilot.CharacterId}/ship/",
                token,
                cancellationToken);

        string typeName =
            await GetTypeNameAsync(
                ship.ShipTypeId,
                cancellationToken);

        return new EveCurrentShipView
        {
            TypeName = typeName,
            CustomName = ship.ShipName,
            ShipItemId = ship.ShipItemId,
            ShipTypeId = ship.ShipTypeId
        };
    }

    public async Task<EveMiningShipIntel> GetMiningShipIntelAsync(
        EvePilotProfile pilot,
        CancellationToken cancellationToken = default)
    {
        EveCurrentShipView ship =
            await GetCurrentShipIdentityAsync(
                pilot,
                cancellationToken);

        bool canReadAssets =
            HasScope(
                pilot,
                "esi-assets.read_assets.v1");

        int laserCount = -1;

        if (canReadAssets &&
            ship.ShipItemId > 0)
        {
            string token =
                await GetAccessTokenAsync(
                    pilot,
                    cancellationToken);

            List<EveAssetItem> assets =
                await GetCharacterAssetsCachedAsync(
                    pilot.CharacterId,
                    token,
                    cancellationToken);

            EveAssetItem[] fittedHighs =
                assets
                    .Where(a =>
                        a.LocationId == ship.ShipItemId &&
                        a.LocationFlag.StartsWith(
                            "HighSlot",
                            StringComparison.OrdinalIgnoreCase))
                    .ToArray();

            IReadOnlyDictionary<int, string> names =
                await GetTypeNamesBatchAsync(
                    fittedHighs.Select(a => a.TypeId),
                    cancellationToken);

            laserCount =
                fittedHighs.Count(a =>
                    names.TryGetValue(
                        a.TypeId,
                        out string? name) &&
                    IsMiningLaserName(name));
        }

        return new EveMiningShipIntel
        {
            CharacterId = pilot.CharacterId,
            CharacterName = pilot.CharacterName,
            CurrentShip = ship,
            MiningLaserCount = laserCount,
            AssetsAvailable = canReadAssets
        };
    }

    public async Task<EveInventorySnapshot> GetInventoryAsync(
        EvePilotProfile pilot,
        CancellationToken cancellationToken = default)
    {
        string token =
            await GetAccessTokenAsync(
                pilot,
                cancellationToken);

        EveCurrentShipView ship =
            await GetCurrentShipIdentityAsync(
                pilot,
                cancellationToken);

        bool canReadAssets =
            HasScope(
                pilot,
                "esi-assets.read_assets.v1");

        bool canReadFittings =
            HasScope(
                pilot,
                "esi-fittings.read_fittings.v1");

        List<EveAssetItem> assets =
            canReadAssets
                ? await GetCharacterAssetsCachedAsync(
                    pilot.CharacterId,
                    token,
                    cancellationToken)
                : new List<EveAssetItem>();

        List<EveFitting> fittings =
            canReadFittings
                ? await GetEsiAsync<List<EveFitting>>(
                    $"/characters/{pilot.CharacterId}/fittings/",
                    token,
                    cancellationToken)
                : new List<EveFitting>();

        int[] typeIds =
            assets.Select(a => a.TypeId)
                .Concat(
                    fittings.Select(f => f.ShipTypeId))
                .Concat(
                    fittings.SelectMany(
                        f => f.Items.Select(i => i.TypeId)))
                .Append(ship.ShipTypeId)
                .Where(id => id > 0)
                .Distinct()
                .ToArray();

        IReadOnlyDictionary<int, string> typeNames =
            await GetTypeNamesBatchAsync(
                typeIds,
                cancellationToken);

        string NameFor(int typeId)
        {
            if (typeId <= 0)
                return "-";

            return typeNames.TryGetValue(
                    typeId,
                    out string? resolved)
                ? resolved
                : $"Type {typeId}";
        }

        EveShipModuleView[] currentModules =
            assets
                .Where(a =>
                    ship.ShipItemId > 0 &&
                    a.LocationId == ship.ShipItemId &&
                    IsShipEquipmentFlag(a.LocationFlag))
                .OrderBy(a => SlotSortKey(a.LocationFlag))
                .ThenBy(
                    a => NameFor(a.TypeId),
                    StringComparer.OrdinalIgnoreCase)
                .Select(a =>
                    new EveShipModuleView
                    {
                        Slot = FriendlySlot(a.LocationFlag),
                        Name = NameFor(a.TypeId),
                        Quantity = a.Quantity
                    })
                .ToArray();

        EveAssetView[] assetViews =
            assets
                .OrderBy(
                    a => NameFor(a.TypeId),
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(
                    a => a.LocationFlag,
                    StringComparer.OrdinalIgnoreCase)
                .Select(a =>
                    new EveAssetView
                    {
                        ItemId = a.ItemId,
                        Name = NameFor(a.TypeId),
                        Quantity = a.Quantity.ToString("N0"),
                        Location =
                            ship.ShipItemId > 0 &&
                            a.LocationId == ship.ShipItemId
                                ? "Current ship"
                                : a.LocationType.Equals(
                                    "item",
                                    StringComparison.OrdinalIgnoreCase)
                                    ? $"Inside item {a.LocationId}"
                                    : $"{a.LocationType} {a.LocationId}",
                        Flag = FriendlySlot(a.LocationFlag)
                    })
                .ToArray();

        EveFittingView[] fittingViews =
            fittings
                .OrderBy(
                    f => f.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Select(f =>
                {
                    EveShipModuleView[] modules =
                        f.Items
                            .OrderBy(i => SlotSortKey(i.Flag))
                            .ThenBy(
                                i => NameFor(i.TypeId),
                                StringComparer.OrdinalIgnoreCase)
                            .Select(i =>
                                new EveShipModuleView
                                {
                                    Slot = FriendlySlot(i.Flag),
                                    Name = NameFor(i.TypeId),
                                    Quantity = i.Quantity
                                })
                            .ToArray();

                    string preview =
                        string.Join(
                            ", ",
                            modules
                                .Take(6)
                                .Select(module => module.Name));

                    if (modules.Length > 6)
                        preview += $" +{modules.Length - 6} more";

                    return new EveFittingView
                    {
                        FittingId = f.FittingId,
                        ShipTypeId = f.ShipTypeId,
                        Name = f.Name,
                        Ship = NameFor(f.ShipTypeId),
                        Items = preview,
                        Description = f.Description,
                        Modules = modules
                    };
                })
                .ToArray();

        var missing = new List<string>();

        if (!canReadAssets)
            missing.Add("assets");
        if (!canReadFittings)
            missing.Add("saved fittings");

        string accessMessage =
            missing.Count == 0
                ? $"Synced {assetViews.Length:N0} assets | " +
                  $"{fittingViews.Length:N0} saved fittings"
                : "Reconnect this pilot for " +
                  string.Join(" + ", missing) +
                  " access.";

        return new EveInventorySnapshot
        {
            CurrentShip = ship,
            CurrentShipModules = currentModules,
            Assets = assetViews,
            Fittings = fittingViews,
            AssetsAvailable = canReadAssets,
            FittingsAvailable = canReadFittings,
            AccessMessage = accessMessage
        };
    }

    public async Task<EvePilotSummary> GetSummaryAsync(
        EvePilotProfile pilot,
        CancellationToken cancellationToken = default)
    {
        string token =
            await GetAccessTokenAsync(pilot, cancellationToken);

        Task<EveSkillsResponse> skillsTask =
            GetEsiAsync<EveSkillsResponse>(
                $"/characters/{pilot.CharacterId}/skills/",
                token, cancellationToken);

        Task<List<EveSkillQueueEntry>> queueTask =
            GetEsiAsync<List<EveSkillQueueEntry>>(
                $"/characters/{pilot.CharacterId}/skillqueue/",
                token, cancellationToken);

        Task<decimal> walletTask =
            GetEsiAsync<decimal>(
                $"/characters/{pilot.CharacterId}/wallet/",
                token, cancellationToken);

        Task<PilotContext> contextTask =
            GetPilotContextAsync(
                pilot,
                token,
                cancellationToken);

        await Task.WhenAll(
            skillsTask,
            queueTask,
            walletTask,
            contextTask);

        EveSkillsResponse skills = await skillsTask;
        List<EveSkillQueueEntry> queue =
            (await queueTask).OrderBy(q => q.QueuePosition).ToList();
        decimal wallet = await walletTask;
        PilotContext context = await contextTask;

        EveSkillQueueEntry? current = queue.FirstOrDefault(q =>
            q.FinishDate.HasValue &&
            q.FinishDate.Value > DateTimeOffset.UtcNow);

        string currentSkill = "Queue empty";
        string remaining = "";
        double progress = 0;

        if (current != null)
        {
            string name =
                await GetTypeNameAsync(current.SkillId, cancellationToken);
            currentSkill = $"{name} {Roman(current.FinishedLevel)}";
            remaining = FormatDuration(
                current.FinishDate!.Value - DateTimeOffset.UtcNow);
            progress = ProgressPercent(current);
        }

        DateTimeOffset? queueEnd = queue
            .Where(q => q.FinishDate.HasValue)
            .Select(q => q.FinishDate)
            .LastOrDefault();

        return new EvePilotSummary
        {
            CharacterId = pilot.CharacterId,
            CharacterName = pilot.CharacterName,
            WalletBalance = wallet,
            TotalSp = skills.TotalSp,
            CurrentSkill = currentSkill,
            CurrentSkillRemaining = remaining,
            QueueEndsIn = queueEnd.HasValue
                ? FormatDuration(queueEnd.Value - DateTimeOffset.UtcNow)
                : "Empty",
            CurrentProgressPercent = progress,
            CurrentSystem = context.SystemName,
            CurrentShip = context.ShipName
        };
    }

    public async Task<EvePilotDashboard> GetDashboardAsync(
        EvePilotProfile pilot,
        CancellationToken cancellationToken = default)
    {
        string token =
            await GetAccessTokenAsync(pilot, cancellationToken);

        Task<EveSkillsResponse> skillsTask =
            GetEsiAsync<EveSkillsResponse>(
                $"/characters/{pilot.CharacterId}/skills/",
                token, cancellationToken);

        Task<List<EveSkillQueueEntry>> queueTask =
            GetEsiAsync<List<EveSkillQueueEntry>>(
                $"/characters/{pilot.CharacterId}/skillqueue/",
                token, cancellationToken);

        Task<decimal> walletTask =
            GetEsiAsync<decimal>(
                $"/characters/{pilot.CharacterId}/wallet/",
                token, cancellationToken);

        Task<List<EveWalletJournalEntry>> journalTask =
            GetEsiAsync<List<EveWalletJournalEntry>>(
                $"/characters/{pilot.CharacterId}/wallet/journal/",
                token, cancellationToken);

        Task<PilotContext> contextTask =
            GetPilotContextAsync(
                pilot,
                token,
                cancellationToken);

        Task<EveTrainingProfile> trainingProfileTask =
            GetTrainingProfileAsync(
                pilot,
                token,
                cancellationToken);

        await Task.WhenAll(
            skillsTask,
            queueTask,
            walletTask,
            journalTask,
            contextTask,
            trainingProfileTask);

        List<EveSkillQueueEntry> queue =
            (await queueTask).OrderBy(q => q.QueuePosition).ToList();

        var queueViews = new List<EveSkillQueueView>();
        foreach (EveSkillQueueEntry entry in queue.Take(50))
        {
            string name =
                await GetTypeNameAsync(entry.SkillId, cancellationToken);
            queueViews.Add(new EveSkillQueueView
            {
                Position = entry.QueuePosition + 1,
                SkillId = entry.SkillId,
                FinishedLevel = entry.FinishedLevel,
                Skill = name,
                Level = Roman(entry.FinishedLevel),
                Starts = entry.StartDate?.ToLocalTime()
                    .ToString("dd MMM HH:mm") ?? "-",
                Finishes = entry.FinishDate?.ToLocalTime()
                    .ToString("dd MMM HH:mm") ?? "-",
                Remaining = entry.FinishDate.HasValue
                    ? FormatDuration(
                        entry.FinishDate.Value - DateTimeOffset.UtcNow)
                    : "-",
                StartDate = entry.StartDate,
                FinishDate = entry.FinishDate,
                TrainingStartSp = entry.TrainingStartSp,
                LevelStartSp = entry.LevelStartSp,
                LevelEndSp = entry.LevelEndSp
            });
        }

        EveSkillsResponse skills = await skillsTask;
        decimal wallet = await walletTask;
        PilotContext context = await contextTask;

        EveSkillQueueEntry? current = queue.FirstOrDefault(q =>
            q.FinishDate.HasValue &&
            q.FinishDate.Value > DateTimeOffset.UtcNow);

        string currentSkill = "Queue empty";
        string currentRemaining = "";
        double currentProgress = 0;

        if (current != null)
        {
            string name =
                await GetTypeNameAsync(current.SkillId, cancellationToken);
            currentSkill = $"{name} {Roman(current.FinishedLevel)}";
            currentRemaining = FormatDuration(
                current.FinishDate!.Value - DateTimeOffset.UtcNow);
            currentProgress = ProgressPercent(current);
        }

        DateTimeOffset? queueEnd = queue
            .Where(q => q.FinishDate.HasValue)
            .Select(q => q.FinishDate)
            .LastOrDefault();

        var summary = new EvePilotSummary
        {
            CharacterId = pilot.CharacterId,
            CharacterName = pilot.CharacterName,
            WalletBalance = wallet,
            TotalSp = skills.TotalSp,
            CurrentSkill = currentSkill,
            CurrentSkillRemaining = currentRemaining,
            QueueEndsIn = queueEnd.HasValue
                ? FormatDuration(queueEnd.Value - DateTimeOffset.UtcNow)
                : "Empty",
            CurrentProgressPercent = currentProgress,
            CurrentSystem = context.SystemName,
            CurrentShip = context.ShipName
        };

        EveWalletJournalView[] journal =
            (await journalTask)
            .OrderByDescending(j => j.Date)
            .Take(100)
            .Select(j => new EveWalletJournalView
            {
                Date = j.Date.ToLocalTime().ToString("dd MMM yyyy HH:mm"),
                Type = HumanizeRefType(j.RefType),
                Amount = j.Amount.HasValue
                    ? FormatIskSigned(j.Amount.Value) : "-",
                Balance = j.Balance.HasValue
                    ? FormatIsk(j.Balance.Value) : "-",
                Reason = string.IsNullOrWhiteSpace(j.Reason)
                    ? "" : j.Reason!
            })
            .ToArray();

        return new EvePilotDashboard
        {
            Summary = summary,
            TrainingProfile = await trainingProfileTask,
            TrainedSkills = skills.Skills.ToArray(),
            SkillQueue = queueViews,
            WalletJournal = journal
        };
    }

    private async Task<EveTrainingProfile> GetTrainingProfileAsync(
        EvePilotProfile pilot,
        string accessToken,
        CancellationToken cancellationToken)
    {
        EveCharacterAttributesResponse current =
            await GetEsiAsync<EveCharacterAttributesResponse>(
                $"/characters/{pilot.CharacterId}/attributes/",
                accessToken,
                cancellationToken);

        bool canReadImplants =
            HasScope(
                pilot,
                "esi-clones.read_implants.v1");

        var implantViews = new List<EveImplantView>();

        int charismaBonus = 0;
        int intelligenceBonus = 0;
        int memoryBonus = 0;
        int perceptionBonus = 0;
        int willpowerBonus = 0;

        if (canReadImplants)
        {
            try
            {
                List<int> implantIds =
                    await GetEsiAsync<List<int>>(
                        $"/characters/{pilot.CharacterId}/implants/",
                        accessToken,
                        cancellationToken);

                foreach (int typeId in implantIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    EveUniverseType implant =
                        await GetUniverseTypeAsync(
                            typeId,
                            cancellationToken);

                    int c = GetDogmaInt(
                        implant,
                        CharismaBonusDogmaId);
                    int i = GetDogmaInt(
                        implant,
                        IntelligenceBonusDogmaId);
                    int m = GetDogmaInt(
                        implant,
                        MemoryBonusDogmaId);
                    int p = GetDogmaInt(
                        implant,
                        PerceptionBonusDogmaId);
                    int w = GetDogmaInt(
                        implant,
                        WillpowerBonusDogmaId);

                    charismaBonus += c;
                    intelligenceBonus += i;
                    memoryBonus += m;
                    perceptionBonus += p;
                    willpowerBonus += w;

                    var bonuses = new List<string>();
                    if (c != 0) bonuses.Add($"+{c} CHA");
                    if (i != 0) bonuses.Add($"+{i} INT");
                    if (m != 0) bonuses.Add($"+{m} MEM");
                    if (p != 0) bonuses.Add($"+{p} PER");
                    if (w != 0) bonuses.Add($"+{w} WIL");

                    implantViews.Add(
                        new EveImplantView
                        {
                            TypeId = typeId,
                            Name = string.IsNullOrWhiteSpace(
                                implant.Name)
                                ? $"Implant {typeId}"
                                : implant.Name,
                            BonusText = bonuses.Count > 0
                                ? string.Join("  ", bonuses)
                                : "non-training implant",
                            IsTrainingRelevant = bonuses.Count > 0
                        });
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[PilotTraining] Implant lookup failed: {ex.Message}");
                canReadImplants = false;
                implantViews.Clear();
            }
        }

        EveTrainingAttribute[] attributes =
        {
            MakeTrainingAttribute(
                IntelligenceAttributeId,
                "Intelligence",
                "INT",
                "",
                "#64C7FF",
                current.Intelligence,
                intelligenceBonus,
                canReadImplants),
            MakeTrainingAttribute(
                MemoryAttributeId,
                "Memory",
                "MEM",
                "",
                "#9FD67A",
                current.Memory,
                memoryBonus,
                canReadImplants),
            MakeTrainingAttribute(
                PerceptionAttributeId,
                "Perception",
                "PER",
                "",
                "#E7B85A",
                current.Perception,
                perceptionBonus,
                canReadImplants),
            MakeTrainingAttribute(
                WillpowerAttributeId,
                "Willpower",
                "WIL",
                "",
                "#D693FF",
                current.Willpower,
                willpowerBonus,
                canReadImplants),
            MakeTrainingAttribute(
                CharismaAttributeId,
                "Charisma",
                "CHA",
                "",
                "#FF8FA6",
                current.Charisma,
                charismaBonus,
                canReadImplants)
        };

        string standardRemapText;

        if (!current.AccruedRemapCooldownDate.HasValue ||
            current.AccruedRemapCooldownDate.Value <=
            DateTimeOffset.UtcNow)
        {
            standardRemapText = "Standard remap available";
        }
        else
        {
            standardRemapText =
                "Standard remap " +
                current.AccruedRemapCooldownDate.Value
                    .ToLocalTime()
                    .ToString("dd MMM yyyy");
        }

        return new EveTrainingProfile
        {
            Attributes = attributes,
            Implants = implantViews
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            BonusRemaps = current.BonusRemaps ?? 0,
            StandardRemapText = standardRemapText,
            ImplantDataAvailable = canReadImplants
        };
    }

    private static EveTrainingAttribute MakeTrainingAttribute(
        int dogmaAttributeId,
        string name,
        string shortName,
        string symbol,
        string accent,
        int total,
        int implantBonus,
        bool implantDataAvailable)
    {
        return new EveTrainingAttribute
        {
            DogmaAttributeId = dogmaAttributeId,
            Name = name,
            ShortName = shortName,
            Symbol = symbol,
            Accent = accent,
            Total = total,
            ImplantBonus = implantBonus,
            Raw = implantDataAvailable
                ? Math.Max(0, total - implantBonus)
                : null
        };
    }

    private static int GetDogmaInt(
        EveUniverseType type,
        int dogmaAttributeId)
    {
        EveDogmaAttributeValue? value =
            type.DogmaAttributes.FirstOrDefault(
                a => a.AttributeId == dogmaAttributeId);

        return value == null
            ? 0
            : (int)Math.Round(
                value.Value,
                MidpointRounding.AwayFromZero);
    }

    private static bool HasScope(
        EvePilotProfile pilot,
        string scope) =>
        pilot.Scopes.Any(
            value => string.Equals(
                value,
                scope,
                StringComparison.OrdinalIgnoreCase));

    private async Task<string> GetAccessTokenAsync(
        EvePilotProfile pilot,
        CancellationToken cancellationToken)
    {
        if (_accessTokens.TryGetValue(
                pilot.CharacterId, out TokenCache? cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return cached.AccessToken;

        string? refreshToken =
            EveCredentialStore.Read(pilot.CharacterId);

        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException(
                $"{pilot.CharacterName} is no longer authenticated. " +
                "Remove and add the character again.");

        using var content =
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = ClientId
            });

        using HttpResponseMessage response =
            await _http.PostAsync(
                TokenEndpoint, content, cancellationToken);
        string json =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Could not refresh EVE SSO for {pilot.CharacterName}: " +
                $"{(int)response.StatusCode} {response.ReasonPhrase}");

        EveTokenResponse token =
            JsonSerializer.Deserialize<EveTokenResponse>(json, _json)
            ?? throw new InvalidOperationException(
                "EVE SSO returned an empty refresh response.");

        if (!string.IsNullOrWhiteSpace(token.RefreshToken) &&
            !string.Equals(
                token.RefreshToken, refreshToken,
                StringComparison.Ordinal))
        {
            EveCredentialStore.Write(
                pilot.CharacterId, token.RefreshToken);
        }

        _accessTokens[pilot.CharacterId] = new TokenCache
        {
            AccessToken = token.AccessToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(
                Math.Max(60, token.ExpiresIn))
        };

        return token.AccessToken;
    }

    private async Task<EveTokenResponse> ExchangeAuthorizationCodeAsync(
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        using var content =
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = ClientId,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = RedirectUri
            });

        using HttpResponseMessage response =
            await _http.PostAsync(
                TokenEndpoint, content, cancellationToken);
        string json =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"EVE SSO token exchange failed: " +
                $"{(int)response.StatusCode} {response.ReasonPhrase}");

        return JsonSerializer.Deserialize<EveTokenResponse>(json, _json)
               ?? throw new InvalidOperationException(
                   "EVE SSO returned an empty token response.");
    }

    private Task<VerifyResponse> VerifyIdentityAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        // EVE SSO v2 returns a JWT. Character identity and granted scopes are
        // already carried by the token as `sub`, `name`, and `scp` claims.
        // Reading them directly also avoids the old /verify response shape,
        // where Scopes may be a single JSON string rather than string[].
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(DecodeJwtIdentity(accessToken));
    }

    private static VerifyResponse DecodeJwtIdentity(
        string accessToken)
    {
        string[] parts = accessToken.Split('.');
        if (parts.Length < 2)
            throw new InvalidOperationException(
                "EVE SSO returned a malformed JWT.");

        byte[] payload = Base64UrlDecode(parts[1]);
        using JsonDocument doc = JsonDocument.Parse(payload);
        JsonElement root = doc.RootElement;

        string sub = root.TryGetProperty("sub", out JsonElement subEl)
            ? subEl.GetString() ?? ""
            : "";
        string name = root.TryGetProperty("name", out JsonElement nameEl)
            ? nameEl.GetString() ?? ""
            : "";

        long characterId = 0;
        string[] pieces = sub.Split(':');
        if (pieces.Length > 0)
            long.TryParse(pieces[^1], out characterId);

        var scopes = new List<string>();
        if (root.TryGetProperty("scp", out JsonElement scpEl))
        {
            if (scpEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in scpEl.EnumerateArray())
                {
                    string? value = item.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        scopes.Add(value);
                }
            }
            else if (scpEl.ValueKind == JsonValueKind.String)
            {
                scopes.AddRange((scpEl.GetString() ?? "")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return new VerifyResponse
        {
            CharacterID = characterId,
            CharacterName = name,
            Scopes = scopes.ToArray()
        };
    }

    private async Task<List<EveAssetItem>> GetCharacterAssetsCachedAsync(
        long characterId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (_assetCache.TryGetValue(
                characterId,
                out AssetCacheEntry? cached) &&
            cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return cached.Items;
        }

        List<EveAssetItem> items =
            await GetCharacterAssetsAsync(
                characterId,
                accessToken,
                cancellationToken);

        _assetCache[characterId] =
            new AssetCacheEntry
            {
                ExpiresAt =
                    DateTimeOffset.UtcNow.AddMinutes(3),
                Items = items
            };

        return items;
    }

    private async Task<List<EveAssetItem>> GetCharacterAssetsAsync(
        long characterId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var all = new List<EveAssetItem>();

        int page = 1;
        int pages = 1;

        do
        {
            using var request =
                new HttpRequestMessage(
                    HttpMethod.Get,
                    EsiBase +
                    $"/characters/{characterId}/assets/?page={page}");

            request.Headers.Accept.Add(
                new MediaTypeWithQualityHeaderValue(
                    "application/json"));

            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer",
                    accessToken);

            using HttpResponseMessage response =
                await _http.SendAsync(
                    request,
                    cancellationToken);

            string json =
                await response.Content.ReadAsStringAsync(
                    cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"ESI character assets page {page} failed: " +
                    $"{(int)response.StatusCode} {response.ReasonPhrase}");
            }

            List<EveAssetItem> items =
                JsonSerializer.Deserialize<List<EveAssetItem>>(
                    json,
                    _json)
                ?? new List<EveAssetItem>();

            all.AddRange(items);

            if (page == 1 &&
                response.Headers.TryGetValues(
                    "X-Pages",
                    out IEnumerable<string>? values))
            {
                string? value =
                    values.FirstOrDefault();

                if (!int.TryParse(
                        value,
                        out pages))
                    pages = 1;

                pages = Math.Clamp(
                    pages,
                    1,
                    100);
            }

            page++;
        }
        while (page <= pages);

        return all;
    }

    private async Task<IReadOnlyDictionary<int, string>>
        GetTypeNamesBatchAsync(
            IEnumerable<int> typeIds,
            CancellationToken cancellationToken)
    {
        int[] allIds =
            typeIds
                .Where(id => id > 0)
                .Distinct()
                .ToArray();

        int[] unresolved =
            allIds
                .Where(id => !_typeNames.ContainsKey(id))
                .ToArray();

        for (int offset = 0;
             offset < unresolved.Length;
             offset += 500)
        {
            int[] batch =
                unresolved
                    .Skip(offset)
                    .Take(500)
                    .ToArray();

            if (batch.Length == 0)
                continue;

            try
            {
                List<EveUniverseName> names =
                    await PostPublicAsync<List<EveUniverseName>>(
                        "/universe/names/",
                        batch,
                        cancellationToken);

                foreach (EveUniverseName item in names)
                {
                    if (item.Id <= 0 ||
                        string.IsNullOrWhiteSpace(item.Name))
                        continue;

                    _typeNames[item.Id] =
                        item.Name;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"[PilotInventory] Batch name lookup failed: {ex.Message}");
            }
        }

        return allIds.ToDictionary(
            id => id,
            id => _typeNames.TryGetValue(
                id,
                out string? name)
                ? name
                : $"Type {id}");
    }

    private async Task<T> PostPublicAsync<T>(
        string relativePath,
        object body,
        CancellationToken cancellationToken)
    {
        string bodyJson =
            JsonSerializer.Serialize(
                body,
                _json);

        using var request =
            new HttpRequestMessage(
                HttpMethod.Post,
                EsiBase + relativePath);

        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/json"));

        request.Content =
            new StringContent(
                bodyJson,
                Encoding.UTF8,
                "application/json");

        using HttpResponseMessage response =
            await _http.SendAsync(
                request,
                cancellationToken);

        string json =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ESI {relativePath} failed: " +
                $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return JsonSerializer.Deserialize<T>(
                   json,
                   _json)
               ?? throw new InvalidOperationException(
                   $"ESI returned no data for {relativePath}.");
    }

    private static bool IsMiningLaserName(
        string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Contains(
                   "Strip Miner",
                   StringComparison.OrdinalIgnoreCase) ||
               name.Contains(
                   "Mining Laser",
                   StringComparison.OrdinalIgnoreCase) ||
               name.Contains(
                   "Ice Harvester",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShipEquipmentFlag(
        string flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
            return false;

        return flag.StartsWith(
                   "HighSlot",
                   StringComparison.OrdinalIgnoreCase) ||
               flag.StartsWith(
                   "MedSlot",
                   StringComparison.OrdinalIgnoreCase) ||
               flag.StartsWith(
                   "LowSlot",
                   StringComparison.OrdinalIgnoreCase) ||
               flag.StartsWith(
                   "RigSlot",
                   StringComparison.OrdinalIgnoreCase) ||
               flag.StartsWith(
                   "SubSystemSlot",
                   StringComparison.OrdinalIgnoreCase) ||
               flag.StartsWith(
                   "ServiceSlot",
                   StringComparison.OrdinalIgnoreCase) ||
               flag.Equals(
                   "DroneBay",
                   StringComparison.OrdinalIgnoreCase) ||
               flag.Equals(
                   "FighterBay",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static int SlotSortKey(
        string flag)
    {
        int group =
            flag.StartsWith(
                "HighSlot",
                StringComparison.OrdinalIgnoreCase) ? 100 :
            flag.StartsWith(
                "MedSlot",
                StringComparison.OrdinalIgnoreCase) ? 200 :
            flag.StartsWith(
                "LowSlot",
                StringComparison.OrdinalIgnoreCase) ? 300 :
            flag.StartsWith(
                "RigSlot",
                StringComparison.OrdinalIgnoreCase) ? 400 :
            flag.StartsWith(
                "SubSystemSlot",
                StringComparison.OrdinalIgnoreCase) ? 500 :
            flag.StartsWith(
                "ServiceSlot",
                StringComparison.OrdinalIgnoreCase) ? 600 :
            flag.Equals(
                "DroneBay",
                StringComparison.OrdinalIgnoreCase) ? 700 :
            flag.Equals(
                "FighterBay",
                StringComparison.OrdinalIgnoreCase) ? 800 :
            900;

        int number = 0;

        for (int i = flag.Length - 1;
             i >= 0;
             i--)
        {
            if (!char.IsDigit(flag[i]))
            {
                if (i < flag.Length - 1)
                    int.TryParse(
                        flag[(i + 1)..],
                        out number);

                break;
            }
        }

        return group + number;
    }

    private static string FriendlySlot(
        string flag)
    {
        if (string.IsNullOrWhiteSpace(flag))
            return "-";

        if (flag.Equals(
                "DroneBay",
                StringComparison.OrdinalIgnoreCase))
            return "Drone Bay";

        if (flag.Equals(
                "FighterBay",
                StringComparison.OrdinalIgnoreCase))
            return "Fighter Bay";

        static string WithNumber(
            string value,
            string prefix,
            string label)
        {
            string suffix =
                value[prefix.Length..];

            if (int.TryParse(
                    suffix,
                    out int slot))
                return $"{label} {slot + 1}";

            return label;
        }

        if (flag.StartsWith(
                "HighSlot",
                StringComparison.OrdinalIgnoreCase))
            return WithNumber(
                flag,
                "HighSlot",
                "High");

        if (flag.StartsWith(
                "MedSlot",
                StringComparison.OrdinalIgnoreCase))
            return WithNumber(
                flag,
                "MedSlot",
                "Mid");

        if (flag.StartsWith(
                "LowSlot",
                StringComparison.OrdinalIgnoreCase))
            return WithNumber(
                flag,
                "LowSlot",
                "Low");

        if (flag.StartsWith(
                "RigSlot",
                StringComparison.OrdinalIgnoreCase))
            return WithNumber(
                flag,
                "RigSlot",
                "Rig");

        if (flag.StartsWith(
                "SubSystemSlot",
                StringComparison.OrdinalIgnoreCase))
            return WithNumber(
                flag,
                "SubSystemSlot",
                "Subsystem");

        if (flag.StartsWith(
                "ServiceSlot",
                StringComparison.OrdinalIgnoreCase))
            return WithNumber(
                flag,
                "ServiceSlot",
                "Service");

        return flag;
    }

    private async Task<T> GetEsiAsync<T>(
        string relativePath,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        using var request =
            new HttpRequestMessage(
                HttpMethod.Get, EsiBase + relativePath);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(accessToken))
            request.Headers.Authorization =
                new AuthenticationHeaderValue(
                    "Bearer", accessToken);

        using HttpResponseMessage response =
            await _http.SendAsync(request, cancellationToken);
        string json =
            await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string detail = string.IsNullOrWhiteSpace(json)
                ? ""
                : " - " + (json.Length > 500
                    ? json[..500] + "..."
                    : json);

            throw new InvalidOperationException(
                $"ESI {relativePath} failed: " +
                $"{(int)response.StatusCode} {response.ReasonPhrase}" +
                detail);
        }

        return JsonSerializer.Deserialize<T>(json, _json)
               ?? throw new InvalidOperationException(
                   $"ESI returned no data for {relativePath}.");
    }

    private async Task<PilotContext> GetPilotContextAsync(
        EvePilotProfile pilot,
        string accessToken,
        CancellationToken cancellationToken)
    {
        bool canReadLocation =
            pilot.Scopes.Any(
                scope => string.Equals(
                    scope,
                    "esi-location.read_location.v1",
                    StringComparison.OrdinalIgnoreCase));

        bool canReadShip =
            pilot.Scopes.Any(
                scope => string.Equals(
                    scope,
                    "esi-location.read_ship_type.v1",
                    StringComparison.OrdinalIgnoreCase));

        Task<string> systemTask =
            canReadLocation
                ? GetCurrentSystemAsync(
                    pilot.CharacterId,
                    accessToken,
                    cancellationToken)
                : Task.FromResult("Reconnect for system");

        Task<string> shipTask =
            canReadShip
                ? GetCurrentShipAsync(
                    pilot.CharacterId,
                    accessToken,
                    cancellationToken)
                : Task.FromResult("Reconnect for ship");

        await Task.WhenAll(systemTask, shipTask);

        return new PilotContext
        {
            SystemName = await systemTask,
            ShipName = await shipTask
        };
    }

    private async Task<string> GetCurrentSystemAsync(
        long characterId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            EveCharacterLocationResponse location =
                await GetEsiAsync<EveCharacterLocationResponse>(
                    $"/characters/{characterId}/location/",
                    accessToken,
                    cancellationToken);

            return await GetSystemNameAsync(
                location.SolarSystemId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[PilotContext] System lookup failed: {ex.Message}");
            return "System unavailable";
        }
    }

    private async Task<string> GetCurrentShipAsync(
        long characterId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            EveCharacterShipResponse ship =
                await GetEsiAsync<EveCharacterShipResponse>(
                    $"/characters/{characterId}/ship/",
                    accessToken,
                    cancellationToken);

            return await GetTypeNameAsync(
                ship.ShipTypeId,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[PilotContext] Ship lookup failed: {ex.Message}");
            return "Ship unavailable";
        }
    }

    private async Task<string> GetSystemNameAsync(
        int systemId,
        CancellationToken cancellationToken)
    {
        if (systemId <= 0)
            return "Unknown system";

        if (_systemNames.TryGetValue(
                systemId,
                out string? cached))
            return cached;

        EveUniverseSystem system =
            await GetEsiAsync<EveUniverseSystem>(
                $"/universe/systems/{systemId}/",
                null,
                cancellationToken);

        string name =
            string.IsNullOrWhiteSpace(system.Name)
                ? $"System {systemId}"
                : system.Name;

        _systemNames[systemId] = name;
        return name;
    }

    private async Task<EveUniverseType> GetUniverseTypeAsync(
        int typeId,
        CancellationToken cancellationToken)
    {
        if (_typeDetails.TryGetValue(
                typeId,
                out EveUniverseType? cached))
            return cached;

        EveUniverseType type =
            await GetEsiAsync<EveUniverseType>(
                $"/universe/types/{typeId}/",
                null,
                cancellationToken);

        _typeDetails[typeId] = type;

        string name = string.IsNullOrWhiteSpace(type.Name)
            ? $"Type {typeId}"
            : type.Name;

        _typeNames[typeId] = name;
        return type;
    }

    private async Task<string> GetTypeNameAsync(
        int typeId,
        CancellationToken cancellationToken)
    {
        if (_typeNames.TryGetValue(
                typeId,
                out string? name))
            return name;

        EveUniverseType type =
            await GetUniverseTypeAsync(
                typeId,
                cancellationToken);

        return string.IsNullOrWhiteSpace(type.Name)
            ? $"Type {typeId}"
            : type.Name;
    }

    private async Task UpsertPilotAsync(
        EvePilotProfile pilot)
    {
        var pilots = (await LoadPilotsAsync()).ToList();
        int index =
            pilots.FindIndex(
                p => p.CharacterId == pilot.CharacterId);

        if (index >= 0)
            pilots[index] = pilot;
        else
            pilots.Add(pilot);

        await SavePilotsAsync(pilots);
    }

    private async Task SavePilotsAsync(
        IEnumerable<EvePilotProfile> pilots)
    {
        string json = JsonSerializer.Serialize(
            pilots.OrderBy(
                p => p.CharacterName,
                StringComparer.OrdinalIgnoreCase),
            _json);
        await File.WriteAllTextAsync(_pilotFile, json);
    }

    private static async Task WriteBrowserResponseAsync(
        NetworkStream stream,
        bool success,
        string title,
        string message)
    {
        string accent = success ? "#58D3B4" : "#E26D6D";
        string body =
            "<!doctype html><html><head><meta charset=\"utf-8\">" +
            "<title>EVE Command Center</title></head>" +
            "<body style=\"margin:0;background:#081012;color:#eaf7f4;" +
            "font-family:Segoe UI,Arial,sans-serif;display:flex;" +
            "align-items:center;justify-content:center;height:100vh\">" +
            "<div style=\"background:#101b1e;border:1px solid #28574e;" +
            "border-radius:16px;padding:32px 38px;max-width:560px;" +
            "box-shadow:0 20px 60px #0008\">" +
            $"<div style=\"color:{accent};font-size:13px;font-weight:700;" +
            "letter-spacing:.12em\">EVE COMMAND CENTER</div>" +
            $"<h1 style=\"margin:10px 0 8px;font-size:26px\">" +
            $"{WebUtility.HtmlEncode(title)}</h1>" +
            $"<p style=\"color:#9cb7b0;line-height:1.5\">" +
            $"{WebUtility.HtmlEncode(message)}</p>" +
            "</div></body></html>";

        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
        string headers =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n";

        byte[] headerBytes =
            Encoding.ASCII.GetBytes(headers);

        await stream.WriteAsync(headerBytes);
        await stream.WriteAsync(bodyBytes);
        await stream.FlushAsync();
    }

    private static Dictionary<string, string> ParseQuery(
        string query)
    {
        var result =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (string part in query.TrimStart('?')
                     .Split(
                         '&',
                         StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pair = part.Split('=', 2);
            string key = UrlDecode(pair[0]);
            string value =
                pair.Length > 1 ? UrlDecode(pair[1]) : "";
            result[key] = value;
        }

        return result;
    }

    private static string UrlDecode(string value) =>
        Uri.UnescapeDataString(value.Replace("+", " "));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        string s =
            value.Replace('-', '+').Replace('_', '/');

        s += (s.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => ""
        };

        return Convert.FromBase64String(s);
    }

    private static double ProgressPercent(
        EveSkillQueueEntry entry)
    {
        if (!entry.StartDate.HasValue ||
            !entry.FinishDate.HasValue)
            return 0;

        double total =
            (entry.FinishDate.Value -
             entry.StartDate.Value).TotalSeconds;

        if (total <= 0)
            return 0;

        double done =
            (DateTimeOffset.UtcNow -
             entry.StartDate.Value).TotalSeconds;

        return Math.Clamp(done / total * 100.0, 0, 100);
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span <= TimeSpan.Zero)
            return "Complete";
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{Math.Max(0, span.Minutes)}m";
    }

    private static string Roman(int level) => level switch
    {
        1 => "I",
        2 => "II",
        3 => "III",
        4 => "IV",
        5 => "V",
        _ => level.ToString()
    };

    public static string FormatIsk(decimal value)
    {
        decimal abs = Math.Abs(value);
        if (abs >= 1_000_000_000_000m)
            return $"{value / 1_000_000_000_000m:0.00}T ISK";
        if (abs >= 1_000_000_000m)
            return $"{value / 1_000_000_000m:0.00}B ISK";
        if (abs >= 1_000_000m)
            return $"{value / 1_000_000m:0.00}M ISK";
        if (abs >= 1_000m)
            return $"{value / 1_000m:0.00}K ISK";
        return $"{value:0.00} ISK";
    }

    private static string FormatIskSigned(decimal value) =>
        (value >= 0 ? "+" : "") + FormatIsk(value);

    private static string HumanizeRefType(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return string.Join(
            " ",
            value.Split(
                    '_',
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(
                    p => char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
