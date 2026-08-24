using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace EveMultiPreview.Services;

public sealed class MiningDashboardPreferences
{
    public int PreferencesVersion { get; set; } = 3;

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

    // V1.6 defaults to the single tiled fleet wall the user asked for.
    public bool UseFleetTileWall { get; set; } = true;
    public bool AutoShowFleetOverview { get; set; } = true;
    public bool FleetOverviewTopmost { get; set; } = true;
    public double? FleetOverviewX { get; set; }
    public double? FleetOverviewY { get; set; }
    public double FleetOverviewWidth { get; set; } = 1500;
    public double FleetOverviewHeight { get; set; } = 390;
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
            return Path.Combine(exeDir, "EVE MultiPreview Mining.json");
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

            // V1.4 -> V1.5 migration.
            if (storedVersion < 2)
            {
                prefs.IdleWatchdogEnabled = true;
                prefs.YieldDropEnabled = true;
                changed = true;
            }

            // V1.5 -> V1.6 migration. Move the mining-only overlays into one
            // resizable tiled window and auto-show it on startup.
            if (storedVersion < 3)
            {
                prefs.UseFleetTileWall = true;
                prefs.AutoShowFleetOverview = true;
                changed = true;
            }

            prefs.PreferencesVersion = 3;
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
            // Dashboard preferences are non-critical.
        }
    }
}

public enum MiningIdleKind
{
    Waiting,
    Mining,
    Late,
    Degraded,
    Idle
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
        MiningIdleKind.Idle => "IDLE ⚠",
        _ => "WAITING"
    };
}

/// <summary>
/// Watches both complete inactivity and sustained yield drops.
///
/// V1.6 deliberately requires a stable learned BASE before the yield-drop alarm
/// is armed. This prevents mining drones, warm-up, boost changes, partial rocks,
/// or a temporary BASE estimator wobble from immediately flashing a client.
/// </summary>
public sealed class MiningIdleWatchdogService : IDisposable
{
    private readonly StatTrackerService _tracker;
    private readonly DispatcherTimer _timer;

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

    private const int StableSamplesRequired = 20;
    private const double StableBand = 0.12;

    public MiningDashboardPreferences Preferences { get; }
    public event Action<string>? IdleDetected;
    public event Action<string, double, double>? YieldDropDetected;

    public MiningIdleWatchdogService(StatTrackerService tracker)
    {
        _tracker = tracker;
        Preferences = MiningDashboardPreferencesStore.Load();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        if (!_timer.IsEnabled)
            _timer.Start();
    }

    public void SavePreferences() =>
        MiningDashboardPreferencesStore.Save(Preferences);

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

        if (age >= idleAfter)
            return new MiningIdleState(MiningIdleKind.Idle, last, age, snap.MiningCycleCount);

        if (_degraded.Contains(character))
            return new MiningIdleState(MiningIdleKind.Degraded, last, age, snap.MiningCycleCount);

        var kind = age >= idleAfter * 0.70
            ? MiningIdleKind.Late
            : MiningIdleKind.Mining;

        return new MiningIdleState(kind, last, age, snap.MiningCycleCount);
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

        if (!_lastCycleCounts.TryGetValue(character, out int previous))
        {
            _lastCycleCounts[character] = count;
            _lastActivityUtc[character] = now;
        }
        else if (count != previous)
        {
            _lastCycleCounts[character] = count;
            _lastActivityUtc[character] = now;
            _idleAlerted.Remove(character);
        }

        ObserveYield(character, snap, now, fireAlert);

        if (!Preferences.IdleWatchdogEnabled || !fireAlert)
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
    }

    private void ObserveYield(string character, CharacterStatSnapshot snap, DateTime now, bool fireAlert)
    {
        if (!Preferences.YieldDropEnabled || snap.MiningCycleCount < 10 || snap.BaseM3PerSec <= 0)
            return;

        string ore = snap.CurrentOre ?? "";
        if (_lastOre.TryGetValue(character, out var previousOre) &&
            !string.Equals(previousOre, ore, StringComparison.OrdinalIgnoreCase))
        {
            ResetYieldLearning(character);
        }
        _lastOre[character] = ore;

        double current = snap.BaseM3PerSec;
        if (!_learnedBase.TryGetValue(character, out double learned) || learned <= 0)
        {
            _learnedBase[character] = current;
            _stableSamples[character] = 0;
            return;
        }

        double relative = Math.Abs(current - learned) / Math.Max(1.0, learned);

        // Stable readings slowly refine the learned normal BASE. The alarm is not
        // armed until we have seen ~20 seconds of this stable state.
        if (relative <= StableBand)
        {
            _learnedBase[character] = learned * 0.95 + current * 0.05;
            _stableSamples[character] = Math.Min(
                StableSamplesRequired,
                _stableSamples.GetValueOrDefault(character) + 1);

            _dropSince.Remove(character);
            _dropAlerted.Remove(character);
            _degraded.Remove(character);
            return;
        }

        // A sudden HIGH reading is not a stopped miner. It can be a boost/fit
        // change, drones landing together, warm-up, or a transient estimator jump.
        // Do not promote that spike into the normal baseline; just re-arm learning.
        if (current > learned)
        {
            _stableSamples[character] = 0;
            _dropSince.Remove(character);
            _dropAlerted.Remove(character);
            _degraded.Remove(character);
            return;
        }

        // Never alarm from a baseline that was not proven stable first.
        if (_stableSamples.GetValueOrDefault(character) < StableSamplesRequired)
        {
            _learnedBase[character] = learned * 0.97 + current * 0.03;
            return;
        }

        double dropFraction = Math.Clamp(Preferences.YieldDropPercent, 10, 80) / 100.0;
        double threshold = learned * (1.0 - dropFraction);

        if (current >= threshold)
        {
            _dropSince.Remove(character);
            _dropAlerted.Remove(character);
            _degraded.Remove(character);
            return;
        }

        if (!_dropSince.TryGetValue(character, out var since))
        {
            _dropSince[character] = now;
            return;
        }

        int hold = Math.Clamp(Preferences.YieldDropHoldSeconds, 10, 300);
        if ((now - since).TotalSeconds < hold)
            return;

        _degraded.Add(character);

        if (fireAlert && _dropAlerted.Add(character))
            YieldDropDetected?.Invoke(character, current, learned);
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
