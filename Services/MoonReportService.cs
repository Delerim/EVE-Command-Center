using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

public sealed class MoonReportService : IDisposable
{
    public const int SchemaVersion = 7;
    public const string MiningScope =
        "esi-industry.read_corporation_mining.v1";
    public const string StructureScope =
        "esi-corporations.read_structures.v1";

    private const string EsiBase = "https://esi.evetech.net/latest";
    private const double PullM3PerHour = 30000.0;
    private const double AlertFloorM3 = 1000.0;
    private const int HistoryRetentionDays = 365;

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
            "EVE-Command-Center-Moon-Report/0.7");
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
        NormalizeState();
        RebuildPullMinedTotals();
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
            progress?.Report("Refreshing moon ore market values...");
            await UpdateMarketPricesAsync(cancellationToken);
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

            progress?.Report("Resolving miner and corporation names...");
            await ResolveLedgerNamesAsync(cancellationToken);
            PruneHistory(DateTimeOffset.UtcNow);
            RebuildPullMinedTotals();

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
            profile.ZeolitesPercent = Clamp(profile.ZeolitesPercent, 0, 100);
            profile.SylvitePercent = Clamp(profile.SylvitePercent, 0, 100);
            profile.BitumensPercent = Clamp(profile.BitumensPercent, 0, 100);
            profile.CoesitePercent = Clamp(profile.CoesitePercent, 0, 100);
            profile.ProfileConfigured = true;
            profile.FieldLifetimeHours =
                Clamp(profile.FieldLifetimeHours, 1, 168);
            profile.WastePercent = Clamp(profile.WastePercent, 0, 100);
            if (profile.MoonId == 0)
            {
                string key = NormalizeMoonName(profile.MoonName);
                if (string.IsNullOrWhiteSpace(key))
                    throw new InvalidOperationException(
                        "This pending moon needs a name before it can be saved.");
                _state.PendingProfilesByMoonName[key] = profile;
            }
            else
            {
                _state.Profiles[profile.MoonId] = profile;
            }
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
                profile.SylvitePercent =
                    Clamp(profile.SylvitePercent, 0, 100);
                profile.BitumensPercent =
                    Clamp(profile.BitumensPercent, 0, 100);
                profile.CoesitePercent =
                    Clamp(profile.CoesitePercent, 0, 100);
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

    public async Task<MoonProfileImportResult> ImportProfilesByNameAsync(
        IEnumerable<MoonProfile> profiles)
    {
        await _gate.WaitAsync();
        try
        {
            int total = 0;
            int matched = 0;
            foreach (MoonProfile imported in profiles)
            {
                string key = NormalizeMoonName(imported.MoonName);
                if (string.IsNullOrWhiteSpace(key)) continue;
                total++;
                MoonProfile? existing = _state.Profiles.Values.FirstOrDefault(
                    profile => NormalizeMoonName(profile.MoonName) == key);
                if (existing != null)
                {
                    ApplyComposition(existing, imported);
                    matched++;
                    _state.PendingProfilesByMoonName.Remove(key);
                }
                else
                {
                    MoonProfile pending = CloneProfile(imported);
                    pending.MoonId = 0;
                    pending.ProfileConfigured = true;
                    _state.PendingProfilesByMoonName[key] = pending;
                }
            }

            await SaveStateAsync();
            return new MoonProfileImportResult
            {
                Total = total,
                Matched = matched,
                Pending = total - matched
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public IReadOnlyList<MoonProfile> ExportProfiles()
    {
        return _state.Profiles.Values
            .Concat(_state.PendingProfilesByMoonName.Values)
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

            string profileKey = NormalizeMoonName(pull.MoonName);
            if (_state.PendingProfilesByMoonName.TryGetValue(
                    profileKey, out MoonProfile? pendingProfile))
            {
                ApplyComposition(profile, pendingProfile);
                _state.PendingProfilesByMoonName.Remove(profileKey);
            }

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
        var dailyAbsolute = new Dictionary<string, double>(
            StringComparer.OrdinalIgnoreCase);

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

            EsiTypePublic type = await GetTypeAsync(
                entry.TypeId, cancellationToken);
            string? family = OreFamily(type.Name);
            if (family == null)
                continue;

            string dailyKey = string.Join(
                ":",
                observerId.ToString(CultureInfo.InvariantCulture),
                entry.LastUpdated.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                family);
            dailyAbsolute.TryGetValue(dailyKey, out double dailyM3);
            dailyAbsolute[dailyKey] = dailyM3 +
                entry.Quantity * Math.Max(0, type.Volume);

            MoonPullRecord? observedPull = FindLedgerPull(
                observerId, entry.LastUpdated);
            _state.LedgerTotals[key] = entry.Quantity;
            double price = _state.TypePrices.TryGetValue(
                entry.TypeId, out double foundPrice) ? foundPrice : 0;
            _state.LedgerHistory[key] = new MoonLedgerRecord
            {
                Key = key,
                PullId = observedPull?.Id ?? "",
                ObserverId = observerId,
                CharacterId = entry.CharacterId,
                RecordedCorporationId = entry.RecordedCorporationId,
                TypeId = entry.TypeId,
                Date = entry.LastUpdated.Date,
                Quantity = entry.Quantity,
                VolumeM3 = entry.Quantity * Math.Max(0, type.Volume),
                EstimatedIsk = entry.Quantity * Math.Max(0, price),
                OreName = type.Name,
                LastSeenUtc = DateTimeOffset.UtcNow
            };
        }

        foreach (KeyValuePair<string, double> item in dailyAbsolute)
            _state.DailyMinedM3[item.Key] = item.Value;

        _state.BaselinedObservers.Add(observerId);
    }

    private void RebuildPullMinedTotals()
    {
        foreach (MoonPullRecord pull in _state.Pulls.Values)
        {
            pull.MinedM3ByOre.Clear();
            pull.JackpotObserved = false;
        }

        foreach (MoonLedgerRecord record in _state.LedgerHistory.Values)
        {
            if (string.IsNullOrWhiteSpace(record.PullId) ||
                !_state.Pulls.TryGetValue(
                    record.PullId, out MoonPullRecord? pull))
                continue;
            pull.MinedM3ByOre.TryGetValue(
                record.OreName, out double existing);
            pull.MinedM3ByOre[record.OreName] = existing + record.VolumeM3;
            if (record.OreName.Contains(
                    "Glistening", StringComparison.OrdinalIgnoreCase))
                pull.JackpotObserved = true;
        }
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
                !p.OutcomeUnobserved)
            .OrderByDescending(p => p.FracturedUtc ?? p.ChunkArrivalUtc)
            .ToArray();

        return candidates.FirstOrDefault(p =>
                   (p.FracturedUtc ?? p.ChunkArrivalUtc) < dayEnd &&
                   (p.EstimatedFieldExpiryUtc ??
                    p.ChunkArrivalUtc.AddHours(
                        ProfileFor(p).FieldLifetimeHours)) >= dayStart) ??
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
        foreach (MoonProfile pending in
                     _state.PendingProfilesByMoonName.Values)
        {
            cards.Add(BuildCard(pending, null, now));
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
        DateTimeOffset calendarCutoff = now.AddDays(-HistoryRetentionDays);
        MoonCardView[] calendarCards = _state.Pulls.Values
            .Where(pull => pull.ChunkArrivalUtc >= calendarCutoff)
            .Select(pull => BuildCard(ProfileFor(pull), pull, now))
            .OrderBy(card => card.ScheduleUtc)
            .ToArray();
        MoonDailyTotalView[] daily = BuildDailyTotals();
        MoonPeriodReportView[] monthly = BuildPeriodReports(daily, true);
        MoonPeriodReportView[] weekly = BuildPeriodReports(daily, false);
        (MoonLedgerMoonView[] ledgerMoons,
            MoonLedgerPullView[] ledgerPulls) = BuildLedgerViews(now);

        MoonPullRecord[] reliableExpired = _state.Pulls.Values
            .Where(p =>
                p.ExpiredUtc.HasValue &&
                !p.OutcomeUnobserved &&
                ProfileFor(p).ProfileConfigured)
            .ToArray();
        double zeoLost = reliableExpired.Sum(p =>
            Remaining(p, ProfileFor(p), "zeolit"));
        double sylviteLost = reliableExpired.Sum(p =>
            Remaining(p, ProfileFor(p), "sylvit"));
        double bitumensLost = reliableExpired.Sum(p =>
            Remaining(p, ProfileFor(p), "bitumen"));
        double coesiteLost = reliableExpired.Sum(p =>
            Remaining(p, ProfileFor(p), "coesite"));

        return new MoonReportSnapshot
        {
            GeneratedUtc = now,
            Cards = orderedCards,
            CalendarCards = calendarCards,
            Audit = auditRows,
            ScheduledCount = orderedCards.Count(c => c.Status == "SCHEDULED"),
            ReadyCount = orderedCards.Count(c => c.Status == "READY"),
            ActiveFieldCount = orderedCards.Count(c => c.Status == "FIELD ACTIVE"),
            TargetDespawnCount =
                auditRows.Count(a => a.Outcome == "ORE LEFT"),
            ZeolitesLostM3 = zeoLost,
            SylviteLostM3 = sylviteLost,
            BitumensLostM3 = bitumensLost,
            CoesiteLostM3 = coesiteLost,
            TotalMinedM3 = daily.Sum(d => d.TotalM3),
            TotalLostM3 = zeoLost + sylviteLost + bitumensLost + coesiteLost,
            ZeolitesMinedM3 = daily.Sum(d => d.ZeolitesM3),
            SylviteMinedM3 = daily.Sum(d => d.SylviteM3),
            BitumensMinedM3 = daily.Sum(d => d.BitumensM3),
            CoesiteMinedM3 = daily.Sum(d => d.CoesiteM3),
            JackpotCount = _state.Pulls.Values.Count(p => p.JackpotObserved),
            DailyTotals = daily,
            MonthlyReports = monthly,
            WeeklyReports = weekly,
            LedgerMoons = ledgerMoons,
            LedgerPulls = ledgerPulls
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
                LastFracture = InferredLastFracture(profile.StructureId),
                HasTargetProfile = HasTargetProfile(profile),
                OreSummary = ProfileOreSummary(profile),
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
        double sylviteMined = Mined(pull, "sylvit");
        double bitumensMined = Mined(pull, "bitumen");
        double coesiteMined = Mined(pull, "coesite");
        double zeoRemaining = Remaining(pull, profile, "zeolit");
        double sylviteRemaining = Remaining(pull, profile, "sylvit");
        double bitumensRemaining = Remaining(pull, profile, "bitumen");
        double coesiteRemaining = Remaining(pull, profile, "coesite");
        double minedTotal = zeoMined + sylviteMined +
            bitumensMined + coesiteMined;
        double remainingTotal = zeoRemaining + sylviteRemaining +
            bitumensRemaining + coesiteRemaining;
        double hours = Math.Max(0,
            (pull.ChunkArrivalUtc - pull.ExtractionStartUtc).TotalHours);
        double profileTotal = profile.ZeolitesPercent +
            profile.SylvitePercent + profile.BitumensPercent +
            profile.CoesitePercent;
        double initialTotal = hours * PullM3PerHour *
            profileTotal / 100.0;
        bool targetProfile = HasTargetProfile(profile);
        bool targetLeft = pull.ExpiredUtc.HasValue &&
            !pull.OutcomeUnobserved && targetProfile &&
            (zeoRemaining >= AlertFloorM3 ||
             sylviteRemaining >= AlertFloorM3 ||
             bitumensRemaining >= AlertFloorM3 ||
             coesiteRemaining >= AlertFloorM3);

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
                : InferredLastFracture(pull.StructureId),
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
            SylviteMined = FormatM3(sylviteMined),
            SylviteRemaining = targetProfile
                ? FormatM3(sylviteRemaining)
                : "Profile needed",
            CoesiteMined = FormatM3(coesiteMined),
            CoesiteRemaining = targetProfile
                ? FormatM3(coesiteRemaining)
                : "Profile needed",
            ZeolitesRemainingM3 = zeoRemaining,
            BitumensRemainingM3 = bitumensRemaining,
            SylviteRemainingM3 = sylviteRemaining,
            CoesiteRemainingM3 = coesiteRemaining,
            InitialTotalM3 = initialTotal,
            MinedTotalM3 = minedTotal,
            RemainingTotalM3 = remainingTotal,
            RemainingPercent = initialTotal > 0
                ? Math.Clamp(remainingTotal / initialTotal * 100.0, 0, 100)
                : 0,
            OreSummary = ProfileOreSummary(profile),
            RemainingSummary = targetProfile
                ? FormatM3(remainingTotal) + " est. left"
                : "Ore profile needed",
            ScheduleUtc = pull.ChunkArrivalUtc,
            IsJackpot = pull.JackpotObserved,
            JackpotLabel = pull.JackpotObserved ? "JACKPOT OBSERVED" : "",
            HasTargetProfile = targetProfile,
            HasTargetLeftover = targetLeft,
            Profile = CloneProfile(profile)
        };
    }

    private (MoonLedgerMoonView[] Moons, MoonLedgerPullView[] Pulls)
        BuildLedgerViews(DateTimeOffset now)
    {
        DateTime cutoff = now.UtcDateTime.Date.AddDays(-HistoryRetentionDays);
        MoonLedgerPullView[] pulls = _state.Pulls.Values
            .Where(pull =>
                pull.ChunkArrivalUtc.UtcDateTime >= cutoff &&
                pull.ChunkArrivalUtc <= now)
            .OrderByDescending(pull => pull.FracturedUtc ?? pull.ChunkArrivalUtc)
            .Select(pull =>
            {
                MoonLedgerRowView[] rows = _state.LedgerHistory.Values
                    .Where(record => record.PullId == pull.Id)
                    .GroupBy(record => new
                    {
                        record.CharacterId,
                        record.RecordedCorporationId
                    })
                    .Select(group =>
                    {
                        double zeo = group.Where(record =>
                            OreFamily(record.OreName) == "zeolites")
                            .Sum(record => record.VolumeM3);
                        double syl = group.Where(record =>
                            OreFamily(record.OreName) == "sylvite")
                            .Sum(record => record.VolumeM3);
                        double bit = group.Where(record =>
                            OreFamily(record.OreName) == "bitumens")
                            .Sum(record => record.VolumeM3);
                        double coe = group.Where(record =>
                            OreFamily(record.OreName) == "coesite")
                            .Sum(record => record.VolumeM3);
                        double volume = group.Sum(record => record.VolumeM3);
                        return new MoonLedgerRowView
                        {
                            CharacterId = group.Key.CharacterId,
                            CorporationName = _state.CorporationNames.TryGetValue(
                                group.Key.RecordedCorporationId, out string? corp)
                                ? corp
                                : "Corp " + group.Key.RecordedCorporationId,
                            CharacterName = _state.CharacterNames.TryGetValue(
                                group.Key.CharacterId, out string? character)
                                ? character
                                : "Character " + group.Key.CharacterId,
                            Quantity = group.Sum(record => record.Quantity),
                            VolumeM3 = volume,
                            EstimatedIsk = group.Sum(record => record.EstimatedIsk),
                            ZeolitesM3 = zeo,
                            SylviteM3 = syl,
                            BitumensM3 = bit,
                            CoesiteM3 = coe,
                            QuantityText = FormatCompact(
                                group.Sum(record => record.Quantity)),
                            VolumeText = FormatM3(volume),
                            IskText = FormatIsk(
                                group.Sum(record => record.EstimatedIsk)),
                            OreBreakdown = OreBreakdown(zeo, syl, bit, coe)
                        };
                    })
                    .OrderByDescending(row => row.VolumeM3)
                    .ToArray();
                DateTimeOffset fracture =
                    pull.FracturedUtc ?? pull.ChunkArrivalUtc;
                return new MoonLedgerPullView
                {
                    PullId = pull.Id,
                    MoonId = pull.MoonId,
                    MoonName = pull.MoonName,
                    StructureName = pull.StructureName,
                    Label = fracture.ToLocalTime().ToString("dd MMM yyyy HH:mm") +
                        (pull.JackpotObserved ? "  ·  ★ JACKPOT" : ""),
                    FractureUtc = fracture,
                    JackpotObserved = pull.JackpotObserved,
                    TotalM3 = rows.Sum(row => row.VolumeM3),
                    TotalIsk = rows.Sum(row => row.EstimatedIsk),
                    Rows = rows
                };
            })
            .ToArray();

        IEnumerable<MoonProfile> pullProfiles = _state.Pulls.Values.Select(
            pull => new MoonProfile
            {
                MoonId = pull.MoonId,
                MoonName = pull.MoonName,
                StructureName = pull.StructureName
            });
        MoonLedgerMoonView[] moons = _state.Profiles.Values
            .Concat(_state.PendingProfilesByMoonName.Values)
            .Concat(pullProfiles)
            .Where(profile => !profile.MoonName.Equals(
                "Moon pending ESI", StringComparison.OrdinalIgnoreCase))
            .GroupBy(profile => NormalizeMoonName(profile.MoonName))
            .Select(group => group.First())
            .Select(profile => new MoonLedgerMoonView
            {
                MoonId = profile.MoonId > 0 ? profile.MoonId : 0,
                MoonName = profile.MoonName,
                StructureName = profile.StructureName,
                Label = profile.MoonName + "  ·  " + profile.StructureName
            })
            .OrderBy(moon => moon.MoonName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return (moons, pulls);
    }

    private MoonAuditView BuildAudit(
        MoonPullRecord pull,
        MoonProfile profile)
    {
        double zeoLeft = Remaining(pull, profile, "zeolit");
        double sylviteLeft = Remaining(pull, profile, "sylvit");
        double bitumensLeft = Remaining(pull, profile, "bitumen");
        double coesiteLeft = Remaining(pull, profile, "coesite");
        bool configured = HasTargetProfile(profile);
        bool reliable = configured && !pull.OutcomeUnobserved;
        bool targetLeft = reliable &&
            (zeoLeft >= AlertFloorM3 ||
             sylviteLeft >= AlertFloorM3 ||
             bitumensLeft >= AlertFloorM3 ||
             coesiteLeft >= AlertFloorM3);

        string outcome = pull.OutcomeUnobserved
            ? "UNOBSERVED"
            : !configured
                ? "PROFILE NEEDED"
                : targetLeft
                    ? "ORE LEFT"
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
            SylviteMined = FormatM3(Mined(pull, "sylvit")),
            SylviteLeft = reliable ? FormatM3(sylviteLeft) : "Unknown",
            CoesiteMined = FormatM3(Mined(pull, "coesite")),
            CoesiteLeft = reliable ? FormatM3(coesiteLeft) : "Unknown",
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
        double percentage = family switch
        {
            "zeolit" => profile.ZeolitesPercent,
            "sylvit" => profile.SylvitePercent,
            "bitumen" => profile.BitumensPercent,
            "coesite" => profile.CoesitePercent,
            _ => 0
        };
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

    private static string? OreFamily(string name)
    {
        if (name.Contains("zeolit", StringComparison.OrdinalIgnoreCase))
            return "zeolites";
        if (name.Contains("sylvit", StringComparison.OrdinalIgnoreCase))
            return "sylvite";
        if (name.Contains("bitumen", StringComparison.OrdinalIgnoreCase))
            return "bitumens";
        if (name.Contains("coesite", StringComparison.OrdinalIgnoreCase))
            return "coesite";
        return null;
    }

    private static bool HasTargetProfile(MoonProfile profile)
    {
        return profile.ProfileConfigured;
    }

    private MoonDailyTotalView[] BuildDailyTotals()
    {
        var totals = new Dictionary<DateTime, double[]>();
        foreach (KeyValuePair<string, double> item in _state.DailyMinedM3)
        {
            string[] parts = item.Key.Split(':');
            if (parts.Length != 3 ||
                !DateTime.TryParseExact(
                    parts[1], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime date))
                continue;

            if (!totals.TryGetValue(date.Date, out double[]? ores))
            {
                ores = new double[4];
                totals[date.Date] = ores;
            }

            switch (parts[2].ToLowerInvariant())
            {
                case "zeolites": ores[0] += item.Value; break;
                case "sylvite": ores[1] += item.Value; break;
                case "bitumens": ores[2] += item.Value; break;
                case "coesite": ores[3] += item.Value; break;
            }
        }

        DateTime[] dates = totals.Keys
            .Concat(_state.Pulls.Values.Select(
                p => p.ChunkArrivalUtc.UtcDateTime.Date))
            .Concat(_state.Pulls.Values
                .Where(p => p.ExpiredUtc.HasValue)
                .Select(p => p.ExpiredUtc!.Value.UtcDateTime.Date))
            .Distinct()
            .OrderBy(date => date)
            .ToArray();

        return dates
            .Select(date =>
            {
                double[] ores = totals.TryGetValue(date, out double[]? found)
                    ? found : new double[4];
                MoonPullRecord[] pulls = _state.Pulls.Values
                    .Where(p => p.ChunkArrivalUtc.UtcDateTime.Date == date)
                    .ToArray();
                MoonPullRecord[] expired = _state.Pulls.Values
                    .Where(p =>
                        p.ExpiredUtc?.UtcDateTime.Date == date &&
                        !p.OutcomeUnobserved &&
                        ProfileFor(p).ProfileConfigured)
                    .ToArray();
                double lost = expired.Sum(p =>
                    Remaining(p, ProfileFor(p), "zeolit") +
                    Remaining(p, ProfileFor(p), "sylvit") +
                    Remaining(p, ProfileFor(p), "bitumen") +
                    Remaining(p, ProfileFor(p), "coesite"));
                return new MoonDailyTotalView
                {
                    Date = date,
                    DateKey = date.ToString("yyyy-MM-dd"),
                    ZeolitesM3 = ores[0],
                    SylviteM3 = ores[1],
                    BitumensM3 = ores[2],
                    CoesiteM3 = ores[3],
                    LostM3 = lost,
                    PullCount = pulls.Length,
                    JackpotCount = pulls.Count(p => p.JackpotObserved)
                };
            })
            .ToArray();
    }

    private MoonPeriodReportView[] BuildPeriodReports(
        IReadOnlyList<MoonDailyTotalView> daily,
        bool monthly)
    {
        DateTime PeriodStart(DateTime date)
        {
            if (monthly)
                return new DateTime(date.Year, date.Month, 1);
            int offset = ((int)date.DayOfWeek + 6) % 7;
            return date.Date.AddDays(-offset);
        }

        var starts = daily
            .Where(d => d.TotalM3 > 0 || d.LostM3 > 0)
            .Select(d => PeriodStart(d.Date))
            .Concat(_state.Pulls.Values
                .Where(p => p.ExpiredUtc.HasValue)
                .Select(p => PeriodStart(p.ExpiredUtc!.Value.UtcDateTime)))
            .Distinct()
            .OrderByDescending(date => date)
            .ToArray();

        var result = new List<MoonPeriodReportView>();
        foreach (DateTime start in starts)
        {
            DateTime end = monthly
                ? start.AddMonths(1)
                : start.AddDays(7);
            MoonDailyTotalView[] rows = daily
                .Where(d => d.Date >= start && d.Date < end)
                .ToArray();
            MoonPullRecord[] pulls = _state.Pulls.Values
                .Where(p =>
                    p.ChunkArrivalUtc.UtcDateTime.Date >= start &&
                    p.ChunkArrivalUtc.UtcDateTime.Date < end &&
                    p.ChunkArrivalUtc <= DateTimeOffset.UtcNow)
                .ToArray();
            MoonPullRecord[] expired = _state.Pulls.Values
                .Where(p =>
                    p.ExpiredUtc.HasValue &&
                    p.ExpiredUtc.Value.UtcDateTime.Date >= start &&
                    p.ExpiredUtc.Value.UtcDateTime.Date < end &&
                    !p.OutcomeUnobserved &&
                    ProfileFor(p).ProfileConfigured)
                .ToArray();

            double lost = expired.Sum(p =>
                Remaining(p, ProfileFor(p), "zeolit") +
                Remaining(p, ProfileFor(p), "sylvit") +
                Remaining(p, ProfileFor(p), "bitumen") +
                Remaining(p, ProfileFor(p), "coesite"));
            double mined = rows.Sum(d => d.TotalM3);
            double efficiency = mined + lost > 0
                ? mined / (mined + lost) * 100.0
                : 0;
            DateTime inclusiveEnd = end.AddDays(-1);
            result.Add(new MoonPeriodReportView
            {
                PeriodKey = monthly
                    ? start.ToString("yyyy-MM")
                    : start.ToString("yyyy-MM-dd"),
                Label = monthly
                    ? start.ToString("MMMM yyyy")
                    : start.ToString("dd MMM") + " - " +
                      inclusiveEnd.ToString("dd MMM yyyy"),
                StartDate = start,
                EndDate = inclusiveEnd,
                MinedM3 = mined,
                LostM3 = lost,
                ZeolitesM3 = rows.Sum(d => d.ZeolitesM3),
                SylviteM3 = rows.Sum(d => d.SylviteM3),
                BitumensM3 = rows.Sum(d => d.BitumensM3),
                CoesiteM3 = rows.Sum(d => d.CoesiteM3),
                PullCount = pulls.Length,
                JackpotCount = pulls.Count(p => p.JackpotObserved),
                MinedText = FormatM3(mined),
                LostText = FormatM3(lost),
                EfficiencyText = efficiency.ToString("0.0") + "%"
            });
        }

        return result.ToArray();
    }

    private string InferredLastFracture(long structureId)
    {
        string prefix = structureId.ToString(CultureInfo.InvariantCulture) + ":";
        DateTime[] dates = _state.DailyMinedM3.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(key => key.Split(':'))
            .Where(parts => parts.Length == 3)
            .Select(parts => DateTime.TryParseExact(
                    parts[1], "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime parsed)
                ? parsed.Date
                : DateTime.MinValue)
            .Where(date => date != DateTime.MinValue)
            .Distinct()
            .OrderBy(date => date)
            .ToArray();
        if (dates.Length == 0)
            return "Not observed yet";

        DateTime start = dates[^1];
        for (int i = dates.Length - 2; i >= 0; i--)
        {
            if ((start - dates[i]).TotalDays > 1)
                break;
            start = dates[i];
        }
        return "≈ " + start.ToString("dd MMM yyyy") +
            " · inferred from ledger";
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

    private async Task UpdateMarketPricesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            List<EsiMarketPrice> prices =
                await GetPublicAsync<List<EsiMarketPrice>>(
                    "/markets/prices/", cancellationToken);
            foreach (EsiMarketPrice price in prices)
            {
                double value = price.AveragePrice ?? price.AdjustedPrice ?? 0;
                if (value > 0)
                    _state.TypePrices[price.TypeId] = value;
            }
        }
        catch (Exception)
        {
            // Prices are optional presentation data. A temporary public market
            // endpoint failure must not prevent schedules and ledgers loading.
        }
    }

    private async Task ResolveLedgerNamesAsync(
        CancellationToken cancellationToken)
    {
        long[] ids = _state.LedgerHistory.Values
            .SelectMany(record => new[]
            {
                record.CharacterId,
                record.RecordedCorporationId
            })
            .Where(id => id > 0 &&
                !_state.CharacterNames.ContainsKey(id) &&
                !_state.CorporationNames.ContainsKey(id))
            .Distinct()
            .ToArray();

        foreach (long[] chunk in ids.Chunk(900))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                List<EsiUniverseNameEntry> names =
                    await PostPublicAsync<List<EsiUniverseNameEntry>>(
                        "/universe/names/", chunk, cancellationToken);
                foreach (EsiUniverseNameEntry item in names)
                {
                    if (item.Category.Equals(
                            "character", StringComparison.OrdinalIgnoreCase))
                        _state.CharacterNames[item.Id] = item.Name;
                    else if (item.Category.Equals(
                                 "corporation", StringComparison.OrdinalIgnoreCase))
                        _state.CorporationNames[item.Id] = item.Name;
                }
            }
            catch (Exception)
            {
                // IDs remain visible as fallbacks and can resolve next refresh.
            }
        }
    }

    private void PruneHistory(DateTimeOffset now)
    {
        DateTime cutoff = now.UtcDateTime.Date.AddDays(-HistoryRetentionDays);
        foreach (string key in _state.LedgerHistory
                     .Where(item => item.Value.Date < cutoff)
                     .Select(item => item.Key)
                     .ToArray())
            _state.LedgerHistory.Remove(key);

        foreach (string key in _state.DailyMinedM3.Keys
                     .Where(key => IsKeyOlderThan(key, cutoff))
                     .ToArray())
            _state.DailyMinedM3.Remove(key);
        foreach (string key in _state.LedgerTotals.Keys
                     .Where(key => IsKeyOlderThan(key, cutoff))
                     .ToArray())
            _state.LedgerTotals.Remove(key);

        foreach (string key in _state.Pulls
                     .Where(item =>
                         item.Value.NaturalDecayUtc.UtcDateTime < cutoff &&
                         !item.Value.SeenInLatestExtractionList)
                     .Select(item => item.Key)
                     .ToArray())
            _state.Pulls.Remove(key);
    }

    private static bool IsKeyOlderThan(string key, DateTime cutoff)
    {
        string[] parts = key.Split(':');
        return parts.Length > 1 && DateTime.TryParseExact(
            parts[1], "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime date) && date < cutoff;
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

    private async Task<T> PostPublicAsync<T>(
        string path,
        object bodyValue,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(bodyValue, _json),
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await _http.PostAsync(
            EsiBase + path, content, cancellationToken);
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

    private void NormalizeState()
    {
        // Keep older V0.4-V0.6 state files forward-compatible. JSON can contain
        // explicit nulls even though the model properties have initializers.
        _state.Profiles ??= new();
        _state.PendingProfilesByMoonName ??=
            new(StringComparer.OrdinalIgnoreCase);
        _state.Pulls ??= new();
        _state.LedgerTotals ??= new();
        _state.LedgerHistory ??= new(StringComparer.OrdinalIgnoreCase);
        _state.DailyMinedM3 ??= new();
        _state.BaselinedObservers ??= new();
        _state.TypeNames ??= new();
        _state.TypeVolumes ??= new();
        _state.SystemNames ??= new();
        _state.CharacterNames ??= new();
        _state.CorporationNames ??= new();
        _state.TypePrices ??= new();
        foreach (MoonPullRecord pull in _state.Pulls.Values)
            pull.MinedM3ByOre ??= new(StringComparer.OrdinalIgnoreCase);
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

    private static string FormatCompact(double value)
    {
        if (value >= 1_000_000_000)
            return $"{value / 1_000_000_000:0.00}B";
        if (value >= 1_000_000)
            return $"{value / 1_000_000:0.00}M";
        if (value >= 1_000)
            return $"{value / 1_000:0.0}K";
        return $"{value:0}";
    }

    private static string FormatIsk(double value)
    {
        if (value >= 1_000_000_000)
            return $"{value / 1_000_000_000:0.00}B ISK";
        if (value >= 1_000_000)
            return $"{value / 1_000_000:0.00}M ISK";
        if (value >= 1_000)
            return $"{value / 1_000:0.0}K ISK";
        return $"{value:0} ISK";
    }

    private static string ProfileOreSummary(MoonProfile profile)
    {
        if (!profile.ProfileConfigured)
            return "ORE PROFILE NEEDED";
        var parts = new List<string>();
        if (profile.ZeolitesPercent > 0)
            parts.Add($"ZEO {profile.ZeolitesPercent:0.#}%");
        if (profile.SylvitePercent > 0)
            parts.Add($"SYL {profile.SylvitePercent:0.#}%");
        if (profile.BitumensPercent > 0)
            parts.Add($"BIT {profile.BitumensPercent:0.#}%");
        if (profile.CoesitePercent > 0)
            parts.Add($"COE {profile.CoesitePercent:0.#}%");
        return parts.Count == 0 ? "NO R4 ORE PROFILE" : string.Join("  ·  ", parts);
    }

    private static string OreBreakdown(
        double zeolites,
        double sylvite,
        double bitumens,
        double coesite)
    {
        var parts = new List<string>();
        if (zeolites > 0) parts.Add("Zeo " + FormatM3(zeolites));
        if (sylvite > 0) parts.Add("Syl " + FormatM3(sylvite));
        if (bitumens > 0) parts.Add("Bit " + FormatM3(bitumens));
        if (coesite > 0) parts.Add("Coe " + FormatM3(coesite));
        return parts.Count == 0 ? "-" : string.Join("  ·  ", parts);
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
            SylvitePercent = source.SylvitePercent,
            BitumensPercent = source.BitumensPercent,
            CoesitePercent = source.CoesitePercent,
            FieldLifetimeHours = source.FieldLifetimeHours,
            WastePercent = source.WastePercent
        };
    }

    private static void ApplyComposition(
        MoonProfile destination,
        MoonProfile source)
    {
        destination.ZeolitesPercent = Clamp(source.ZeolitesPercent, 0, 100);
        destination.SylvitePercent = Clamp(source.SylvitePercent, 0, 100);
        destination.BitumensPercent = Clamp(source.BitumensPercent, 0, 100);
        destination.CoesitePercent = Clamp(source.CoesitePercent, 0, 100);
        destination.FieldLifetimeHours = Clamp(source.FieldLifetimeHours, 1, 168);
        destination.WastePercent = Clamp(source.WastePercent, 0, 100);
        destination.ProfileConfigured = true;
        destination.StructureName = First(
            destination.StructureName, source.StructureName);
        destination.SystemName = First(
            destination.SystemName, source.SystemName);
    }

    private static string NormalizeMoonName(string? value)
    {
        string normalized = Regex.Replace(
            (value ?? "").Trim().ToLowerInvariant(),
            @"\bmoon\s+0+(\d+)\b", "moon $1");
        var result = new StringBuilder(normalized.Length);
        foreach (char character in normalized)
        {
            if (char.IsLetterOrDigit(character))
                result.Append(character);
        }
        return result.ToString();
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
