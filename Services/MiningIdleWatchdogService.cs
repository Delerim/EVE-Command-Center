using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace EveMultiPreview.Services;

public sealed class MiningDashboardPreferences
{
    public int PreferencesVersion { get; set; } = 9;

    public bool JitaEnabled { get; set; } = true;
    public bool AmarrEnabled { get; set; } = true;
    public string MarketPriceMode { get; set; } = "sell";

    public double CorpBuybackPercent { get; set; } = 90.0;
    public string CorpBuybackMarket { get; set; } = "Jita";
    public string CorpBuybackPriceMode { get; set; } = "sell";

    public bool IdleWatchdogEnabled { get; set; } = true;
    public int IdleSeconds { get; set; } = 90;
    public bool IdleSoundEnabled { get; set; } = true;

    public bool YieldDropEnabled { get; set; } = true;
    public int YieldDropPercent { get; set; } = 35;
    public int YieldDropHoldSeconds { get; set; } = 30;

    // Per-character watchdog mute. Useful for Orcas/mining drones where a very
    // long drone travel time can legitimately exceed the normal no-pull timer.
    public List<string> AlarmMutedCharacters { get; set; } = new();

    // Manual source of truth for live Orca shield command bursts.
    // Values: OFF, HARM, EXT, BOTH.
    public Dictionary<string, string> OrcaShieldBoostModes { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    // Persistent left-to-right miner wall order.
    // Characters not yet present in this list append automatically.
    public List<string> FleetTileOrder { get; set; } = new();

    public bool UseFleetTileWall { get; set; } = true;
    public bool AutoShowFleetOverview { get; set; } = true;

    // Legacy preference retained for compatibility. V1.11 auto-fits by default.
    public bool AutoSizeFleetOverview { get; set; } = true;

    // False = one fixed horizontal row that exactly fits the miner tiles.
    // True = user may manually resize the wall; cards still never wrap vertically.
    public bool AllowFleetOverviewResize { get; set; } = false;

    // Filters the market and sell-timing tables only. Mining totals/history remain complete.
    // Default is the high-sec set used by this mining fleet.
    public string MarketOreFilter { get; set; } = "myhs";
    public bool FleetOverviewTopmost { get; set; } = true;
    public double? FleetOverviewX { get; set; }
    public double? FleetOverviewY { get; set; }
    public double FleetOverviewWidth { get; set; } = 1780;
    public double FleetOverviewHeight { get; set; } = 165;

    public int DashboardOpacityPercent { get; set; } = 96;
    public int FleetOverviewOpacityPercent { get; set; } = 94;
}

public static class MiningDashboardPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string FilePath
    {
        get
        {
            var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                         ?? AppDomain.CurrentDomain.BaseDirectory;

            string newPath =
                Path.Combine(
                    exeDir,
                    "EVE Command Center Mining.json");

            string legacyPath =
                Path.Combine(
                    exeDir,
                    "EVE MultiPreview Mining.json");

            if (!File.Exists(newPath) &&
                File.Exists(legacyPath))
            {
                try
                {
                    File.Move(
                        legacyPath,
                        newPath);
                }
                catch
                {
                    // Safe fallback: preserve the legacy file if a rename is
                    // blocked, but copy it to the new branded filename so this
                    // and all future runs use the new path.
                    try
                    {
                        File.Copy(
                            legacyPath,
                            newPath,
                            overwrite: false);
                    }
                    catch
                    {
                    }
                }
            }

            return newPath;
        }
    }

    public static MiningDashboardPreferences Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new MiningDashboardPreferences();

            string json = File.ReadAllText(FilePath);
            var prefs = JsonSerializer.Deserialize<MiningDashboardPreferences>(json, JsonOptions)
                        ?? new MiningDashboardPreferences();

            int storedVersion = json.Contains("\"PreferencesVersion\"", StringComparison.Ordinal)
                ? prefs.PreferencesVersion
                : 1;

            bool changed = false;

            if (storedVersion < 2)
            {
                prefs.IdleWatchdogEnabled = true;
                prefs.YieldDropEnabled = true;
                changed = true;
            }

            if (storedVersion < 3)
            {
                prefs.UseFleetTileWall = true;
                prefs.AutoShowFleetOverview = true;
                changed = true;
            }

            if (storedVersion < 4)
            {
                // V1.8: compact banner defaults and mild transparency.
                prefs.FleetOverviewWidth = Math.Max(prefs.FleetOverviewWidth, 1780);
                prefs.FleetOverviewHeight = 165;
                prefs.DashboardOpacityPercent = 96;
                prefs.FleetOverviewOpacityPercent = 94;
                changed = true;
            }

            if (storedVersion < 5)
            {
                prefs.AlarmMutedCharacters ??= new List<string>();
                changed = true;
            }

            if (storedVersion < 6)
            {
                prefs.AutoSizeFleetOverview = true;
                prefs.MarketOreFilter = "myhs";
                changed = true;
            }

            if (storedVersion < 7)
            {
                prefs.AllowFleetOverviewResize = false;
                changed = true;
            }

            if (storedVersion < 8)
            {
                prefs.OrcaShieldBoostModes =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                changed = true;
            }

            if (storedVersion < 9)
            {
                prefs.FleetTileOrder =
                    new List<string>();
                changed = true;
            }

            prefs.AlarmMutedCharacters ??= new List<string>();
            prefs.OrcaShieldBoostModes ??=
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);
            prefs.FleetTileOrder ??=
                new List<string>();
            if (string.IsNullOrWhiteSpace(prefs.MarketOreFilter))
                prefs.MarketOreFilter = "myhs";

            prefs.PreferencesVersion = 9;
            prefs.DashboardOpacityPercent = Math.Clamp(prefs.DashboardOpacityPercent, 55, 100);
            prefs.FleetOverviewOpacityPercent = Math.Clamp(prefs.FleetOverviewOpacityPercent, 55, 100);

            if (changed)
                Save(prefs);

            return prefs;
        }
        catch
        {
            return new MiningDashboardPreferences();
        }
    }

    public static void Save(MiningDashboardPreferences prefs)
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(prefs, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // Mining preferences must never prevent MultiPreview from running.
        }
    }
}

public enum MiningIdleKind
{
    Waiting,
    Mining,
    Late,
    Degraded,
    Idle,
    Muted
}

public readonly record struct MiningIdleState(
    MiningIdleKind Kind,
    DateTime? LastActivityUtc,
    double AgeSeconds,
    int CycleCount)
{
    public string Label => Kind switch
    {
        MiningIdleKind.Mining => "MINING",
        MiningIdleKind.Late => "LATE",
        MiningIdleKind.Degraded => "DEGRADED",
        MiningIdleKind.Idle => "IDLE",
        MiningIdleKind.Muted => "MUTED",
        _ => "WAITING"
    };
}

public sealed class MiningIdleWatchdogService : IDisposable
{
    private readonly StatTrackerService _tracker;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _shipTimer;
    private readonly EveSsoService _pilotSso = new();

    private readonly HashSet<string> _automaticAlarmSuppression =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _shipRefreshBusy;

    private readonly Dictionary<string, int> _lastCycleCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastActivityUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _idleAlerted =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, double> _learnedBase =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _stableSamples =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _dropSince =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dropAlerted =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _degraded =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastOre =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, double> _relearnCandidate =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _relearnSince =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _relearnSamples =
        new(StringComparer.OrdinalIgnoreCase);

    private const int StableSamplesRequired = 8;
    private const int RelearnSamplesRequired = 6;
    private const double StableBand = 0.15;
    private const double RelearnBand = 0.18;

    public MiningDashboardPreferences Preferences { get; }
    public event Action<string>? IdleDetected;
    public event Action<string, double, double>? YieldDropDetected;

    public MiningIdleWatchdogService(StatTrackerService tracker)
    {
        _tracker = tracker;
        Preferences = MiningDashboardPreferencesStore.Load();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Tick();

        _shipTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _shipTimer.Tick +=
            async (_, _) =>
                await RefreshAutomaticSuppressionAsync();
    }

    public void Start()
    {
        if (!_timer.IsEnabled)
            _timer.Start();

        if (!_shipTimer.IsEnabled)
            _shipTimer.Start();

        _ = RefreshAutomaticSuppressionAsync();
    }

    public void SavePreferences() =>
        MiningDashboardPreferencesStore.Save(Preferences);

    public bool IsCharacterAlarmMuted(string character)
    {
        if (string.IsNullOrWhiteSpace(character))
            return false;

        return Preferences.AlarmMutedCharacters.Exists(
            x => string.Equals(
                x,
                character,
                StringComparison.OrdinalIgnoreCase));
    }

    public bool IsCharacterAlarmAutomaticallySuppressed(
        string character)
    {
        if (string.IsNullOrWhiteSpace(character))
            return false;

        return _automaticAlarmSuppression.Contains(
            character);
    }

    private bool IsCharacterAlarmSuppressed(
        string character) =>
        IsCharacterAlarmMuted(character) ||
        IsCharacterAlarmAutomaticallySuppressed(
            character);

    private async Task RefreshAutomaticSuppressionAsync()
    {
        if (_shipRefreshBusy)
            return;

        _shipRefreshBusy = true;

        try
        {
            var pilots =
                await _pilotSso.LoadPilotsAsync();

            var next =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (var pilot in pilots)
            {
                try
                {
                    var ship =
                        await _pilotSso
                            .GetCurrentShipIdentityAsync(
                                pilot);

                    if (ship.IsOrca)
                        next.Add(
                            pilot.CharacterName);
                }
                catch (Exception)
                {
                    // Ship classification is advisory. Never let an ESI failure
                    // interfere with the ordinary mining watchdog.
                }
            }

            foreach (string character in next)
            {
                if (_automaticAlarmSuppression.Add(
                        character))
                {
                    _idleAlerted.Remove(character);
                    _dropAlerted.Remove(character);
                    _dropSince.Remove(character);
                    _degraded.Remove(character);
                }
            }

            string[] noLongerSuppressed =
                _automaticAlarmSuppression
                    .Where(
                        character =>
                            !next.Contains(character))
                    .ToArray();

            foreach (string character
                     in noLongerSuppressed)
            {
                _automaticAlarmSuppression.Remove(
                    character);

                _idleAlerted.Remove(character);
                _dropAlerted.Remove(character);
                ResetYieldLearning(character);

                var snap =
                    _tracker.GetSnapshot(character);

                if (snap.MiningCycleCount > 0)
                    _lastActivityUtc[character] =
                        DateTime.UtcNow;
            }
        }
        finally
        {
            _shipRefreshBusy = false;
        }
    }

    public void SetCharacterAlarmMuted(string character, bool muted)
    {
        if (string.IsNullOrWhiteSpace(character))
            return;

        if (muted)
        {
            if (!IsCharacterAlarmMuted(character))
                Preferences.AlarmMutedCharacters.Add(character);

            // Stop any watchdog state from carrying on while this miner is muted.
            _idleAlerted.Remove(character);
            _dropAlerted.Remove(character);
            _dropSince.Remove(character);
            _degraded.Remove(character);
        }
        else
        {
            Preferences.AlarmMutedCharacters.RemoveAll(
                x => string.Equals(
                    x,
                    character,
                    StringComparison.OrdinalIgnoreCase));

            // Unmuting starts with a fresh grace period. Do not immediately alarm
            // just because the miner happened to be idle while muted.
            _idleAlerted.Remove(character);
            _dropAlerted.Remove(character);
            ResetYieldLearning(character);

            var snap = _tracker.GetSnapshot(character);
            if (snap.MiningCycleCount > 0)
                _lastActivityUtc[character] = DateTime.UtcNow;
        }

        SavePreferences();
    }

    public MiningIdleState GetState(string character)
    {
        Observe(character, fireAlert: false);

        var snap = _tracker.GetSnapshot(character);
        if (snap.MiningCycleCount <= 0)
            return new MiningIdleState(MiningIdleKind.Waiting, null, 0, 0);

        if (!_lastActivityUtc.TryGetValue(character, out var last))
            return new MiningIdleState(MiningIdleKind.Waiting, null, 0, snap.MiningCycleCount);

        double age = Math.Max(0, (DateTime.UtcNow - last).TotalSeconds);
        int idleAfter = Math.Clamp(Preferences.IdleSeconds, 15, 3600);

        if (IsCharacterAlarmSuppressed(character))
            return new MiningIdleState(
                MiningIdleKind.Muted,
                last,
                age,
                snap.MiningCycleCount);

        if (age >= idleAfter)
            return new MiningIdleState(MiningIdleKind.Idle, last, age, snap.MiningCycleCount);

        if (_degraded.Contains(character))
            return new MiningIdleState(MiningIdleKind.Degraded, last, age, snap.MiningCycleCount);

        return new MiningIdleState(
            age >= idleAfter * 0.70 ? MiningIdleKind.Late : MiningIdleKind.Mining,
            last,
            age,
            snap.MiningCycleCount);
    }

    private void Tick()
    {
        foreach (var character in _tracker.GetTrackedCharacters())
            Observe(character, fireAlert: true);
    }

    private void Observe(string character, bool fireAlert)
    {
        var snap = _tracker.GetSnapshot(character);
        int count = snap.MiningCycleCount;
        if (count <= 0) return;

        var now = DateTime.UtcNow;
        bool newPull = false;

        if (!_lastCycleCounts.TryGetValue(character, out int previous))
        {
            _lastCycleCounts[character] = count;
            _lastActivityUtc[character] = now;
            newPull = true;
        }
        else if (count != previous)
        {
            _lastCycleCounts[character] = count;
            _lastActivityUtc[character] = now;
            _idleAlerted.Remove(character);
            newPull = true;
        }

        bool muted =
            IsCharacterAlarmSuppressed(character);

        ObserveYield(
            character,
            snap,
            now,
            fireAlert && !muted,
            newPull);

        if (muted ||
            !Preferences.IdleWatchdogEnabled ||
            !fireAlert)
            return;

        if (!_lastActivityUtc.TryGetValue(character, out var last))
        {
            _lastActivityUtc[character] = now;
            return;
        }

        int idleAfter = Math.Clamp(Preferences.IdleSeconds, 15, 3600);
        if ((now - last).TotalSeconds < idleAfter)
            return;

        if (_idleAlerted.Add(character))
            IdleDetected?.Invoke(character);
    }

    private void ResetYieldLearning(string character)
    {
        _learnedBase.Remove(character);
        _stableSamples.Remove(character);
        _dropSince.Remove(character);
        _dropAlerted.Remove(character);
        _degraded.Remove(character);
        ClearRelearnCandidate(character);
    }

    private void ClearRelearnCandidate(string character)
    {
        _relearnCandidate.Remove(character);
        _relearnSince.Remove(character);
        _relearnSamples.Remove(character);
    }

    private bool ObserveRelearnCandidate(
        string character,
        double current,
        DateTime now,
        int requiredSeconds)
    {
        if (!_relearnCandidate.TryGetValue(character, out double candidate) || candidate <= 0)
        {
            _relearnCandidate[character] = current;
            _relearnSince[character] = now;
            _relearnSamples[character] = 1;
            return false;
        }

        double deviation = Math.Abs(current - candidate) / Math.Max(1.0, candidate);

        if (deviation > RelearnBand)
        {
            _relearnCandidate[character] = current;
            _relearnSince[character] = now;
            _relearnSamples[character] = 1;
            return false;
        }

        _relearnCandidate[character] = candidate * 0.80 + current * 0.20;
        _relearnSamples[character] = _relearnSamples.GetValueOrDefault(character) + 1;

        if (!_relearnSince.TryGetValue(character, out var since))
            _relearnSince[character] = since = now;

        if (_relearnSamples.GetValueOrDefault(character) < RelearnSamplesRequired)
            return false;

        return (now - since).TotalSeconds >= requiredSeconds;
    }

    private void AcceptRelearnedBaseline(string character)
    {
        if (!_relearnCandidate.TryGetValue(character, out double candidate) || candidate <= 0)
            return;

        _learnedBase[character] = candidate;
        _stableSamples[character] = StableSamplesRequired;
        _dropSince.Remove(character);
        _dropAlerted.Remove(character);
        _degraded.Remove(character);
        ClearRelearnCandidate(character);
    }

    private void ObserveYield(
        string character,
        CharacterStatSnapshot snap,
        DateTime now,
        bool fireAlert,
        bool newPull)
    {
        if (!Preferences.YieldDropEnabled ||
            snap.MiningCycleCount < 10 ||
            snap.BaseM3PerSec <= 0)
            return;

        string ore = snap.CurrentOre ?? "";
        if (_lastOre.TryGetValue(character, out var previousOre) &&
            !string.Equals(previousOre, ore, StringComparison.OrdinalIgnoreCase))
        {
            ResetYieldLearning(character);
        }
        _lastOre[character] = ore;

        // Only learn from new EVE mining pulls, never the one-second UI timer.
        if (!newPull)
            return;

        double current = snap.BaseM3PerSec;

        if (!_learnedBase.TryGetValue(character, out double learned) || learned <= 0)
        {
            _learnedBase[character] = current;
            _stableSamples[character] = 1;
            ClearRelearnCandidate(character);
            return;
        }

        double relative = Math.Abs(current - learned) / Math.Max(1.0, learned);

        if (relative <= StableBand)
        {
            _learnedBase[character] = learned * 0.90 + current * 0.10;
            _stableSamples[character] = Math.Min(
                StableSamplesRequired,
                _stableSamples.GetValueOrDefault(character) + 1);

            _dropSince.Remove(character);
            _dropAlerted.Remove(character);
            _degraded.Remove(character);
            ClearRelearnCandidate(character);
            return;
        }

        bool baselineArmed =
            _stableSamples.GetValueOrDefault(character) >= StableSamplesRequired;

        if (!baselineArmed)
        {
            _learnedBase[character] = current;
            _stableSamples[character] = 1;
            _dropSince.Remove(character);
            _dropAlerted.Remove(character);
            _degraded.Remove(character);
            ClearRelearnCandidate(character);
            return;
        }

        int hold = Math.Clamp(Preferences.YieldDropHoldSeconds, 10, 300);
        double dropFraction = Math.Clamp(Preferences.YieldDropPercent, 10, 80) / 100.0;
        double dropThreshold = learned * (1.0 - dropFraction);

        // A changed but non-dangerous stable rate may become the new baseline.
        if (current >= dropThreshold)
        {
            _dropSince.Remove(character);
            _dropAlerted.Remove(character);
            _degraded.Remove(character);

            int relearnSeconds = Math.Max(30, hold);
            if (ObserveRelearnCandidate(character, current, now, relearnSeconds))
                AcceptRelearnedBaseline(character);

            return;
        }

        if (!_dropSince.TryGetValue(character, out var dropSince))
        {
            _dropSince[character] = now;
            ClearRelearnCandidate(character);
            return;
        }

        if ((now - dropSince).TotalSeconds < hold)
            return;

        _degraded.Add(character);

        if (fireAlert && _dropAlerted.Add(character))
            YieldDropDetected?.Invoke(character, current, learned);

        // A lower rate is allowed to become the new normal after the warning has
        // fired and consistent mining pulls prove the lower rate is deliberate.
        int lowerRelearnSeconds = Math.Max(60, hold * 2);
        if (ObserveRelearnCandidate(character, current, now, lowerRelearnSeconds))
            AcceptRelearnedBaseline(character);
    }

    public void Dispose()
    {
        _timer.Stop();
        _shipTimer.Stop();
    }
}
