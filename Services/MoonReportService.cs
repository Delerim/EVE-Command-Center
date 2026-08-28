using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

public sealed class MoonReportService : IDisposable
{
    public const string MiningScope =
        "esi-industry.read_corporation_mining.v1";
    public const string StructureScope =
        "esi-corporations.read_structures.v1";

    private const string EsiBase = "https://esi.evetech.net/latest";
    private const double PullM3PerHour = 30000.0;
    private const double AlertFloorM3 = 1000.0;

    private readonly EveSsoService _sso;
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _json;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _stateFile;
    private MoonReportState _state;

    public MoonReportService(EveSsoService sso)
    {
        _sso = sso;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "EVE-Command-Center-Moon-Report/0.4");
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Compatibility-Date", "2026-08-25");

        _json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        string root = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "EVE Command Center", "MoonReport");
        Directory.CreateDirectory(root);
        _stateFile = Path.Combine(root, "moon-report.json");
        _state = LoadState();
    }

    public long SelectedCharacterId => _state.SelectedCharacterId;

    public static bool HasRequiredScopes(EvePilotProfile pilot)
    {
        var scopes = pilot.Scopes ?? Array.Empty<string>();
        return scopes.Contains(MiningScope, StringComparer.OrdinalIgnoreCase) &&
               scopes.Contains(StructureScope, StringComparer.OrdinalIgnoreCase);
    }

    public async Task SelectPilotAsync(long characterId)
    {
        await _gate.WaitAsync();
        try
        {
            _state.SelectedCharacterId = characterId;
            await SaveStateAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public MoonReportSnapshot GetSnapshot()
    {
        return BuildSnapshot(DateTimeOffset.UtcNow);
    }

    public async Task<MoonReportSnapshot> RefreshAsync(
        EvePilotProfile pilot,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!HasRequiredScopes(pilot))
        {
            throw new InvalidOperationException(
                $"{pilot.CharacterName} was connected without the moon report " +
                "permissions. Use RECONNECT / ADD and authorize the new scopes.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            progress?.Report("Refreshing the selected ESI token...");
            string token = await _sso.GetAccessTokenForAsync(
                pilot, cancellationToken);

            progress?.Report("Finding the character corporation...");
            EsiCharacterPublic character =
                await GetPublicAsync<EsiCharacterPublic>(
                    $"/characters/{pilot.CharacterId}/",
                    cancellationToken);

            if (character.CorporationId <= 0)
                throw new InvalidOperationException(
                    "ESI did not return a corporation for the selected character.");

            long corporationId = character.CorporationId;

            progress?.Report("Loading moon drill schedules...");
            List<EsiMoonExtraction> extractions;
            try
            {
                extractions = await GetPagedAsync<EsiMoonExtraction>(
                    $"/corporation/{corporationId}/mining/extractions/",
                    token,
                    cancellationToken);
            }
            catch (EsiRequestException ex) when (
                ex.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    "The selected character needs the Station Manager corporation " +
                    "role to read moon extraction schedules.", ex);
            }

            progress?.Report("Loading corporation structures...");
            List<EsiCorporationStructure> structures;
            try
            {
                structures = await GetPagedAsync<EsiCorporationStructure>(
                    $"/corporations/{corporationId}/structures/",
                    token,
                    cancellationToken);
            }
            catch (EsiRequestException ex) when (
                ex.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    "The selected character needs the Station Manager corporation " +
                    "role to read corporation structure names.", ex);
            }

            var structureMap = structures.ToDictionary(
                item => item.StructureId);

            await UpdateExtractionsAsync(
                extractions,
                structureMap,
                progress,
                cancellationToken);

            await UpdateIdleDrillsAsync(
                structures,
                extractions.Select(item => item.StructureId).ToHashSet(),
                cancellationToken);

            progress?.Report("Loading corporation mining observers...");
            List<EsiMiningObserver> observers;
            try
            {
                observers = await GetPagedAsync<EsiMiningObserver>(
                    $"/corporation/{corporationId}/mining/observers/",
                    token,
                    cancellationToken);
            }
            catch (EsiRequestException ex) when (
                ex.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    "The selected character needs the Accountant corporation role " +
                    "to read the corporation mining ledger.", ex);
            }

            int observerIndex = 0;
            foreach (EsiMiningObserver observer in observers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observerIndex++;
                progress?.Report(
                    $"Reading mining ledger {observerIndex:N0} of " +
                    $"{observers.Count:N0}...");

                List<EsiMiningLedgerEntry> ledger =
                    await GetPagedAsync<EsiMiningLedgerEntry>(
                        $"/corporation/{corporationId}/mining/observers/" +
                        $"{observer.ObserverId}/",
                        token,
                        cancellationToken);

                await ApplyLedgerAsync(
                    observer.ObserverId,
                    ledger,
                    cancellationToken);
            }

            EvaluateExpiredFields(DateTimeOffset.UtcNow);
            _state.SelectedCharacterId = pilot.CharacterId;
            _state.LastRefreshUtc = DateTimeOffset.UtcNow;
            await SaveStateAsync();

            progress?.Report(
                $"Moon report updated at " +
                $"{DateTime.Now:HH:mm:ss}.");
            return BuildSnapshot(DateTimeOffset.UtcNow);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveProfileAsync(MoonProfile profile)
    {
        await _gate.WaitAsync();
        try
        {
            if (profile.MoonId == 0)
                throw new InvalidOperationException(
                    "This moon has not been identified by ESI yet.");

            profile.ZeolitesPercent = Clamp(profile.ZeolitesPercent, 0, 100);
            profile.BitumensPercent = Clamp(profile.BitumensPercent, 0, 100);
            profile.ProfileConfigured = true;
            profile.FieldLifetimeHours =
                Clamp(profile.FieldLifetimeHours, 1, 168);
            profile.WastePercent = Clamp(profile.WastePercent, 0, 100);
            _state.Profiles[profile.MoonId] = profile;
            await SaveStateAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ImportProfilesAsync(
        IEnumerable<MoonProfile> profiles)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (MoonProfile profile in profiles.Where(p => p.MoonId != 0))
            {
                profile.ZeolitesPercent =
                    Clamp(profile.ZeolitesPercent, 0, 100);
                profile.BitumensPercent =
                    Clamp(profile.BitumensPercent, 0, 100);
                profile.ProfileConfigured = true;
                profile.FieldLifetimeHours =
                    Clamp(profile.FieldLifetimeHours, 1, 168);
                profile.WastePercent = Clamp(profile.WastePercent, 0, 100);

                if (_state.Profiles.TryGetValue(
                        profile.MoonId, out MoonProfile? existing))
                {
                    profile.StructureId = profile.StructureId > 0
                        ? profile.StructureId : existing.StructureId;
                    profile.MoonName = First(profile.MoonName, existing.MoonName);
                    profile.StructureName = First(
                        profile.StructureName, existing.StructureName);
                    profile.SystemId = profile.SystemId > 0
                        ? profile.SystemId : existing.SystemId;
                    profile.SystemName = First(
                        profile.SystemName, existing.SystemName);
                }

                _state.Profiles[profile.MoonId] = profile;
            }

            await SaveStateAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<MoonProfile> ExportProfiles()
    {
        return _state.Profiles.Values
            .OrderBy(p => p.MoonName, StringComparer.OrdinalIgnoreCase)
            .Select(CloneProfile)
            .ToArray();
    }

    private async Task UpdateExtractionsAsync(
        IReadOnlyList<EsiMoonExtraction> extractions,
        IReadOnlyDictionary<long, EsiCorporationStructure> structures,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (MoonPullRecord pull in _state.Pulls.Values)
            pull.SeenInLatestExtractionList = false;

        int index = 0;
        foreach (EsiMoonExtraction extraction in extractions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            progress?.Report(
                $"Resolving moon {index:N0} of {extractions.Count:N0}...");

            string id = PullId(extraction);
            if (!_state.Pulls.TryGetValue(id, out MoonPullRecord? pull))
            {
                pull = new MoonPullRecord
                {
                    Id = id,
                    StructureId = extraction.StructureId,
                    MoonId = extraction.MoonId,
                    ExtractionStartUtc = extraction.ExtractionStartTime,
                    ChunkArrivalUtc = extraction.ChunkArrivalTime,
                    NaturalDecayUtc = extraction.NaturalDecayTime
                };
                _state.Pulls[id] = pull;
            }

            pull.SeenInLatestExtractionList = true;
            pull.MoonId = extraction.MoonId;
            pull.ExtractionStartUtc = extraction.ExtractionStartTime;
            pull.ChunkArrivalUtc = extraction.ChunkArrivalTime;
            pull.NaturalDecayUtc = extraction.NaturalDecayTime;

            if (structures.TryGetValue(
                    extraction.StructureId,
                    out EsiCorporationStructure? structure))
            {
                pull.StructureName = structure.Name;
                pull.SystemId = structure.SystemId;
            }

            EsiMoonPublic moon = await GetPublicAsync<EsiMoonPublic>(
                $"/universe/moons/{extraction.MoonId}/",
                cancellationToken);
            pull.MoonName = First(moon.Name, $"Moon {extraction.MoonId}");
            if (pull.SystemId <= 0)
                pull.SystemId = moon.SystemId;
            pull.SystemName = await GetSystemNameAsync(
                pull.SystemId, cancellationToken);

            if (!_state.Profiles.TryGetValue(
                    extraction.MoonId, out MoonProfile? profile))
            {
                MoonProfile? placeholder = _state.Profiles.Values
                    .FirstOrDefault(item =>
                        item.StructureId == extraction.StructureId &&
                        item.MoonId < 0);

                profile = placeholder != null
                    ? CloneProfile(placeholder)
                    : new MoonProfile
                    {
                        MoonId = extraction.MoonId
                    };
                if (placeholder != null)
                {
                    _state.Profiles.Remove(placeholder.MoonId);
                    profile.MoonId = extraction.MoonId;
                }
                _state.Profiles[extraction.MoonId] = profile;
            }

            profile.StructureId = pull.StructureId;
            profile.MoonName = pull.MoonName;
            profile.StructureName = pull.StructureName;
            profile.SystemId = pull.SystemId;
            profile.SystemName = pull.SystemName;

            MoonPullRecord? previous = _state.Pulls.Values
                .Where(p =>
                    p.StructureId == pull.StructureId &&
                    p.Id != pull.Id &&
                    p.FracturedUtc == null &&
                    !p.OutcomeUnobserved)
                .OrderByDescending(p => p.ChunkArrivalUtc)
                .FirstOrDefault();

            if (previous != null &&
                extraction.ExtractionStartTime >= previous.ChunkArrivalUtc)
            {
                if (extraction.ExtractionStartTime < previous.NaturalDecayUtc)
                {
                    MarkFractured(
                        previous,
                        extraction.ExtractionStartTime,
                        ProfileFor(previous));
                }
                else
                {
                    previous.OutcomeUnobserved = true;
                    previous.ExpiredUtc = extraction.ExtractionStartTime;
                }
            }
        }

        foreach (MoonPullRecord missing in _state.Pulls.Values.Where(p =>
                     !p.SeenInLatestExtractionList &&
                     p.FracturedUtc == null &&
                     !p.OutcomeUnobserved))
        {
            if (now >= missing.NaturalDecayUtc)
            {
                // The app did not observe the extraction disappear before its
                // natural-decay deadline, so ESI cannot tell us whether pilots
                // fractured it or allowed the chunk to decay.
                missing.OutcomeUnobserved = true;
                missing.ExpiredUtc = now;
            }
            else if (now >= missing.ChunkArrivalUtc)
            {
                MarkFractured(missing, now, ProfileFor(missing));
            }
        }
    }

    private async Task UpdateIdleDrillsAsync(
        IReadOnlyList<EsiCorporationStructure> structures,
        HashSet<long> activeStructureIds,
        CancellationToken cancellationToken)
    {
        foreach (EsiCorporationStructure structure in structures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool hasMoonService = structure.Services.Any(service =>
                service.Name.Contains(
                    "moon", StringComparison.OrdinalIgnoreCase));
            if (!hasMoonService &&
                !activeStructureIds.Contains(structure.StructureId))
                continue;

            MoonProfile? existing = _state.Profiles.Values.FirstOrDefault(
                profile => profile.StructureId == structure.StructureId);
            if (existing != null)
            {
                existing.StructureName = structure.Name;
                existing.SystemId = structure.SystemId;
                existing.SystemName = await GetSystemNameAsync(
                    structure.SystemId, cancellationToken);
                continue;
            }

            long placeholderId = -Math.Abs(structure.StructureId);
            _state.Profiles[placeholderId] = new MoonProfile
            {
                MoonId = placeholderId,
                StructureId = structure.StructureId,
                MoonName = "Moon pending ESI",
                StructureName = structure.Name,
                SystemId = structure.SystemId,
                SystemName = await GetSystemNameAsync(
                    structure.SystemId, cancellationToken)
            };
        }
    }

    private async Task ApplyLedgerAsync(
        long observerId,
        IReadOnlyList<EsiMiningLedgerEntry> ledger,
        CancellationToken cancellationToken)
    {
        bool isBaseline = !_state.BaselinedObservers.Contains(observerId);

        foreach (EsiMiningLedgerEntry entry in ledger)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = string.Join(
                ":",
                observerId.ToString(CultureInfo.InvariantCulture),
                entry.LastUpdated.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                entry.CharacterId.ToString(CultureInfo.InvariantCulture),
                entry.RecordedCorporationId.ToString(CultureInfo.InvariantCulture),
                entry.TypeId.ToString(CultureInfo.InvariantCulture));

            _state.LedgerTotals.TryGetValue(key, out long oldQuantity);
            long delta = Math.Max(0, entry.Quantity - oldQuantity);
            _state.LedgerTotals[key] = entry.Quantity;

            if (isBaseline || delta <= 0)
                continue;

            EsiTypePublic type = await GetTypeAsync(
                entry.TypeId, cancellationToken);
            if (!IsTargetOre(type.Name))
                continue;

            MoonPullRecord? pull = FindLedgerPull(
                observerId,
                entry.LastUpdated);
            if (pull == null)
                continue;

            double minedM3 = delta * Math.Max(0, type.Volume);
            pull.MinedM3ByOre.TryGetValue(type.Name, out double oldM3);
            pull.MinedM3ByOre[type.Name] = oldM3 + minedM3;
        }

        _state.BaselinedObservers.Add(observerId);
    }

    private MoonPullRecord? FindLedgerPull(
        long observerId,
        DateTime ledgerDate)
    {
        DateTimeOffset dayStart = new DateTimeOffset(
            DateTime.SpecifyKind(ledgerDate.Date, DateTimeKind.Utc));
        DateTimeOffset dayEnd = dayStart.AddDays(1);
        MoonPullRecord[] candidates = _state.Pulls.Values
            .Where(p =>
                p.StructureId == observerId &&
                p.FracturedUtc.HasValue &&
                !p.OutcomeUnobserved)
            .OrderByDescending(p => p.FracturedUtc)
            .ToArray();

        return candidates.FirstOrDefault(p =>
                   p.FracturedUtc!.Value < dayEnd &&
                   (!p.EstimatedFieldExpiryUtc.HasValue ||
                    p.EstimatedFieldExpiryUtc.Value >= dayStart)) ??
               candidates.FirstOrDefault(p => !p.ExpiredUtc.HasValue);
    }

    private void EvaluateExpiredFields(DateTimeOffset now)
    {
        foreach (MoonPullRecord pull in _state.Pulls.Values)
        {
            if (pull.FracturedUtc.HasValue &&
                pull.EstimatedFieldExpiryUtc.HasValue &&
                !pull.ExpiredUtc.HasValue &&
                now >= pull.EstimatedFieldExpiryUtc.Value)
            {
                pull.ExpiredUtc = now;
            }
        }
    }

    private void MarkFractured(
        MoonPullRecord pull,
        DateTimeOffset fracturedUtc,
        MoonProfile profile)
    {
        pull.FracturedUtc = fracturedUtc;
        pull.EstimatedFieldExpiryUtc = fracturedUtc.AddHours(
            profile.FieldLifetimeHours > 0
                ? profile.FieldLifetimeHours
                : 48);
    }

    private MoonReportSnapshot BuildSnapshot(DateTimeOffset now)
    {
        EvaluateExpiredFields(now);
        var cards = new List<MoonCardView>();
        var audit = new List<MoonAuditView>();

        foreach (MoonProfile profile in _state.Profiles.Values)
        {
            MoonPullRecord? pull = _state.Pulls.Values
                .Where(p => p.MoonId == profile.MoonId)
                .OrderByDescending(p => p.ExtractionStartUtc)
                .FirstOrDefault();

            cards.Add(BuildCard(profile, pull, now));
        }

        foreach (MoonPullRecord pull in _state.Pulls.Values
                     .Where(p => p.ExpiredUtc.HasValue)
                     .OrderByDescending(p => p.ExpiredUtc))
        {
            audit.Add(BuildAudit(pull, ProfileFor(pull)));
        }

        MoonCardView[] orderedCards = cards
            .OrderBy(c => StatusOrder(c.Status))
            .ThenBy(c => NextSortTime(c.PullId))
            .ThenBy(c => c.MoonName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        MoonAuditView[] auditRows = audit.ToArray();

        return new MoonReportSnapshot
        {
            GeneratedUtc = now,
            Cards = orderedCards,
            Audit = auditRows,
            ScheduledCount = orderedCards.Count(c => c.Status == "SCHEDULED"),
            ReadyCount = orderedCards.Count(c => c.Status == "READY"),
            ActiveFieldCount = orderedCards.Count(c => c.Status == "FIELD ACTIVE"),
            TargetDespawnCount =
                auditRows.Count(a => a.Outcome == "TARGET ORE LEFT"),
            ZeolitesLostM3 = _state.Pulls.Values
                .Where(p =>
                    p.ExpiredUtc.HasValue &&
                    !p.OutcomeUnobserved &&
                    ProfileFor(p).ProfileConfigured)
                .Sum(p => Remaining(p, ProfileFor(p), "zeolit")),
            BitumensLostM3 = _state.Pulls.Values
                .Where(p =>
                    p.ExpiredUtc.HasValue &&
                    !p.OutcomeUnobserved &&
                    ProfileFor(p).ProfileConfigured)
                .Sum(p => Remaining(p, ProfileFor(p), "bitumen"))
        };
    }

    private MoonCardView BuildCard(
        MoonProfile profile,
        MoonPullRecord? pull,
        DateTimeOffset now)
    {
        if (pull == null)
        {
            return new MoonCardView
            {
                MoonId = profile.MoonId,
                StructureId = profile.StructureId,
                MoonName = First(profile.MoonName, $"Moon {profile.MoonId}"),
                StructureName = profile.StructureName,
                SystemName = profile.SystemName,
                Status = "IDLE",
                StatusBrush = "#607D8B",
                ScheduleLabel = "NEXT PULL",
                ScheduleValue = "No active extraction",
                HasTargetProfile = HasTargetProfile(profile),
                Profile = CloneProfile(profile)
            };
        }

        string status;
        string brush;
        string label;
        string value;

        if (pull.SeenInLatestExtractionList && now < pull.ChunkArrivalUtc)
        {
            status = "SCHEDULED";
            brush = "#46C7C7";
            label = "FRACTURES";
            value = DateAndRelative(pull.ChunkArrivalUtc, now);
        }
        else if (pull.SeenInLatestExtractionList)
        {
            status = "READY";
            brush = "#FFB74D";
            label = "READY SINCE";
            value = DateAndRelative(pull.ChunkArrivalUtc, now);
        }
        else if (pull.FracturedUtc.HasValue && !pull.ExpiredUtc.HasValue)
        {
            status = "FIELD ACTIVE";
            brush = "#81C784";
            label = "FIELD EXPIRES";
            value = pull.EstimatedFieldExpiryUtc.HasValue
                ? DateAndRelative(pull.EstimatedFieldExpiryUtc.Value, now)
                : "Unknown";
        }
        else if (pull.OutcomeUnobserved)
        {
            status = "OUTCOME UNKNOWN";
            brush = "#8D6E63";
            label = "LAST OBSERVED";
            value = pull.ExpiredUtc.HasValue
                ? DateAndRelative(pull.ExpiredUtc.Value, now)
                : "Detected";
        }
        else
        {
            status = "IDLE";
            brush = "#607D8B";
            label = "LAST FIELD";
            value = pull.ExpiredUtc.HasValue
                ? DateAndRelative(pull.ExpiredUtc.Value, now)
                : "No active extraction";
        }

        double zeoMined = Mined(pull, "zeolit");
        double bitumensMined = Mined(pull, "bitumen");
        double zeoRemaining = Remaining(pull, profile, "zeolit");
        double bitumensRemaining = Remaining(pull, profile, "bitumen");
        bool targetProfile = HasTargetProfile(profile);
        bool targetLeft = pull.ExpiredUtc.HasValue &&
            !pull.OutcomeUnobserved && targetProfile &&
            (zeoRemaining >= AlertFloorM3 ||
             bitumensRemaining >= AlertFloorM3);

        return new MoonCardView
        {
            PullId = pull.Id,
            MoonId = pull.MoonId,
            StructureId = pull.StructureId,
            MoonName = First(pull.MoonName, profile.MoonName),
            StructureName = First(pull.StructureName, profile.StructureName),
            SystemName = First(pull.SystemName, profile.SystemName),
            Status = targetLeft ? "TARGET LEFT" : status,
            StatusBrush = targetLeft ? "#EF5350" : brush,
            ScheduleLabel = label,
            ScheduleValue = value,
            PullLength = FormatDuration(
                pull.ChunkArrivalUtc - pull.ExtractionStartUtc),
            LastFracture = pull.FracturedUtc.HasValue
                ? DateAndRelative(pull.FracturedUtc.Value, now)
                : "Not observed yet",
            FieldExpiry = pull.EstimatedFieldExpiryUtc.HasValue
                ? DateAndRelative(pull.EstimatedFieldExpiryUtc.Value, now)
                : "-",
            ZeolitesMined = FormatM3(zeoMined),
            ZeolitesRemaining = targetProfile
                ? FormatM3(zeoRemaining)
                : "Profile needed",
            BitumensMined = FormatM3(bitumensMined),
            BitumensRemaining = targetProfile
                ? FormatM3(bitumensRemaining)
                : "Profile needed",
            ZeolitesRemainingM3 = zeoRemaining,
            BitumensRemainingM3 = bitumensRemaining,
            HasTargetProfile = targetProfile,
            HasTargetLeftover = targetLeft,
            Profile = CloneProfile(profile)
        };
    }

    private MoonAuditView BuildAudit(
        MoonPullRecord pull,
        MoonProfile profile)
    {
        double zeoLeft = Remaining(pull, profile, "zeolit");
        double bitumensLeft = Remaining(pull, profile, "bitumen");
        bool configured = HasTargetProfile(profile);
        bool reliable = configured && !pull.OutcomeUnobserved;
        bool targetLeft = reliable &&
            (zeoLeft >= AlertFloorM3 || bitumensLeft >= AlertFloorM3);

        string outcome = pull.OutcomeUnobserved
            ? "UNOBSERVED"
            : !configured
                ? "PROFILE NEEDED"
                : targetLeft
                    ? "TARGET ORE LEFT"
                    : "CLEARED";

        return new MoonAuditView
        {
            MoonName = First(pull.MoonName, profile.MoonName),
            StructureName = First(pull.StructureName, profile.StructureName),
            SystemName = First(pull.SystemName, profile.SystemName),
            Fractured = pull.FracturedUtc?.ToLocalTime()
                .ToString("dd MMM yyyy HH:mm") ?? "-",
            Expired = pull.ExpiredUtc?.ToLocalTime()
                .ToString("dd MMM yyyy HH:mm") ?? "-",
            ZeolitesMined = FormatM3(Mined(pull, "zeolit")),
            ZeolitesLeft = reliable ? FormatM3(zeoLeft) : "Unknown",
            BitumensMined = FormatM3(Mined(pull, "bitumen")),
            BitumensLeft = reliable ? FormatM3(bitumensLeft) : "Unknown",
            Outcome = outcome,
            OutcomeBrush = targetLeft
                ? "#EF5350"
                : outcome == "CLEARED"
                    ? "#81C784"
                    : "#90A4AE"
        };
    }

    private double Remaining(
        MoonPullRecord pull,
        MoonProfile profile,
        string family)
    {
        double percentage = family == "zeolit"
            ? profile.ZeolitesPercent
            : profile.BitumensPercent;
        if (percentage <= 0)
            return 0;

        double hours = Math.Max(
            0,
            (pull.ChunkArrivalUtc - pull.ExtractionStartUtc).TotalHours);
        double estimatedInitial =
            hours * PullM3PerHour * percentage / 100.0;
        double removed = Mined(pull, family) *
            (1.0 + Math.Max(0, profile.WastePercent) / 100.0);
        return Math.Max(0, estimatedInitial - removed);
    }

    private static double Mined(MoonPullRecord pull, string family)
    {
        return pull.MinedM3ByOre
            .Where(pair => pair.Key.Contains(
                family, StringComparison.OrdinalIgnoreCase))
            .Sum(pair => pair.Value);
    }

    private static bool IsTargetOre(string name)
    {
        return name.Contains("zeolit", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("bitumen", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasTargetProfile(MoonProfile profile)
    {
        return profile.ProfileConfigured;
    }

    private MoonProfile ProfileFor(MoonPullRecord pull)
    {
        return _state.Profiles.TryGetValue(
            pull.MoonId, out MoonProfile? profile)
            ? profile
            : new MoonProfile
            {
                MoonId = pull.MoonId,
                StructureId = pull.StructureId,
                MoonName = pull.MoonName,
                StructureName = pull.StructureName,
                SystemId = pull.SystemId,
                SystemName = pull.SystemName
            };
    }

    private async Task<EsiTypePublic> GetTypeAsync(
        int typeId,
        CancellationToken cancellationToken)
    {
        if (_state.TypeNames.TryGetValue(typeId, out string? name) &&
            _state.TypeVolumes.TryGetValue(typeId, out double volume))
        {
            return new EsiTypePublic { Name = name, Volume = volume };
        }

        EsiTypePublic type = await GetPublicAsync<EsiTypePublic>(
            $"/universe/types/{typeId}/", cancellationToken);
        _state.TypeNames[typeId] = type.Name;
        _state.TypeVolumes[typeId] = type.Volume;
        return type;
    }

    private async Task<string> GetSystemNameAsync(
        int systemId,
        CancellationToken cancellationToken)
    {
        if (systemId <= 0)
            return "Unknown system";
        if (_state.SystemNames.TryGetValue(systemId, out string? cached))
            return cached;

        EsiUniverseName system = await GetPublicAsync<EsiUniverseName>(
            $"/universe/systems/{systemId}/", cancellationToken);
        _state.SystemNames[systemId] = system.Name;
        return system.Name;
    }

    private async Task<T> GetPublicAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(
            EsiBase + path, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(
            cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new EsiRequestException(
                response.StatusCode,
                $"ESI {path} failed: {(int)response.StatusCode} " +
                response.ReasonPhrase);
        return JsonSerializer.Deserialize<T>(body, _json)
            ?? throw new InvalidOperationException(
                $"ESI returned an empty response for {path}.");
    }

    private async Task<List<T>> GetPagedAsync<T>(
        string path,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var result = new List<T>();
        int page = 1;
        int pages = 1;

        do
        {
            string separator = path.Contains('?') ? "&" : "?";
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                EsiBase + path + separator + "page=" + page);
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", accessToken);

            using HttpResponseMessage response = await _http.SendAsync(
                request, cancellationToken);
            string body = await response.Content.ReadAsStringAsync(
                cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new EsiRequestException(
                    response.StatusCode,
                    $"ESI {path} failed: {(int)response.StatusCode} " +
                    response.ReasonPhrase + ". " + TrimBody(body));

            List<T> items =
                JsonSerializer.Deserialize<List<T>>(body, _json) ?? new();
            result.AddRange(items);

            if (page == 1 &&
                response.Headers.TryGetValues("X-Pages", out var values) &&
                int.TryParse(values.FirstOrDefault(), out int parsed))
            {
                pages = Math.Max(1, parsed);
            }

            page++;
        }
        while (page <= pages);

        return result;
    }

    private MoonReportState LoadState()
    {
        if (!File.Exists(_stateFile))
            return new MoonReportState();
        try
        {
            string json = File.ReadAllText(_stateFile);
            return JsonSerializer.Deserialize<MoonReportState>(json, _json)
                ?? new MoonReportState();
        }
        catch
        {
            return new MoonReportState();
        }
    }

    private async Task SaveStateAsync()
    {
        string temp = _stateFile + ".tmp";
        string json = JsonSerializer.Serialize(_state, _json);
        await File.WriteAllTextAsync(temp, json);
        File.Move(temp, _stateFile, true);
    }

    private DateTimeOffset NextSortTime(string pullId)
    {
        if (!_state.Pulls.TryGetValue(pullId, out MoonPullRecord? pull))
            return DateTimeOffset.MaxValue;
        return pull.SeenInLatestExtractionList
            ? pull.ChunkArrivalUtc
            : pull.EstimatedFieldExpiryUtc ?? DateTimeOffset.MaxValue;
    }

    private static int StatusOrder(string status) => status switch
    {
        "TARGET LEFT" => 0,
        "READY" => 1,
        "FIELD ACTIVE" => 2,
        "SCHEDULED" => 3,
        _ => 4
    };

    private static string PullId(EsiMoonExtraction extraction)
    {
        return extraction.StructureId.ToString(CultureInfo.InvariantCulture) +
            ":" + extraction.ExtractionStartTime.UtcDateTime.Ticks
                .ToString(CultureInfo.InvariantCulture);
    }

    private static string DateAndRelative(
        DateTimeOffset value,
        DateTimeOffset now)
    {
        TimeSpan delta = value - now;
        string direction = delta >= TimeSpan.Zero ? "in " : "";
        string suffix = delta < TimeSpan.Zero ? " ago" : "";
        return value.ToLocalTime().ToString("dd MMM HH:mm") +
            " · " + direction + FormatDuration(delta.Duration()) + suffix;
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1)
            return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{Math.Max(0, span.Minutes)}m";
    }

    public static string FormatM3(double value)
    {
        if (value >= 1_000_000)
            return $"{value / 1_000_000:0.00}M m3";
        if (value >= 1_000)
            return $"{value / 1_000:0.0}K m3";
        return $"{value:0} m3";
    }

    private static MoonProfile CloneProfile(MoonProfile source)
    {
        return new MoonProfile
        {
            MoonId = source.MoonId,
            StructureId = source.StructureId,
            MoonName = source.MoonName,
            StructureName = source.StructureName,
            SystemId = source.SystemId,
            SystemName = source.SystemName,
            ProfileConfigured = source.ProfileConfigured,
            ZeolitesPercent = source.ZeolitesPercent,
            BitumensPercent = source.BitumensPercent,
            FieldLifetimeHours = source.FieldLifetimeHours,
            WastePercent = source.WastePercent
        };
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Max(min, Math.Min(max, value));
    }

    private static string First(string? primary, string? fallback)
    {
        return !string.IsNullOrWhiteSpace(primary)
            ? primary
            : fallback ?? "";
    }

    private static string TrimBody(string body)
    {
        body = body.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return body.Length <= 180 ? body : body[..180];
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    private sealed class EsiRequestException : Exception
    {
        public EsiRequestException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }

        public HttpStatusCode StatusCode { get; }
    }
}
