using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace EveMultiPreview.Services;

public sealed class MiningDashboardPreferences
{
    public int PreferencesVersion { get; set; } = 2;

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

    public bool AutoShowFleetOverview { get; set; } = false;
    public bool FleetOverviewTopmost { get; set; } = true;
    public double? FleetOverviewX { get; set; }
    public double? FleetOverviewY { get; set; }
    public double FleetOverviewWidth { get; set; } = 1500;
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

            // V1.4 shipped the watchdog disabled by default. On the first V1.5 load,
            // migrate our own old sidecar once so the mining alarms the user asked for
            // are actually armed. After this, the user's choice is preserved.
            if (!json.Contains("\"PreferencesVersion\"", StringComparison.Ordinal))
            {
                prefs.PreferencesVersion = 2;
                prefs.IdleWatchdogEnabled = true;
                prefs.YieldDropEnabled = true;
                Save(prefs);
            }

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
/// Watches both complete inactivity and sustained yield drops. The latter catches
/// the common "one of two strip miners stopped" case where pulls are still arriving,
/// so a simple no-pull timer would never fire.
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
    private readonly Dictionary<string, DateTime> _dropSince =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dropAlerted =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _degraded =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastOre =
        new(StringComparer.OrdinalIgnoreCase);

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

    private void ObserveYield(string character, CharacterStatSnapshot snap, DateTime now, bool fireAlert)
    {
        if (!Preferences.YieldDropEnabled || snap.MiningCycleCount < 8 || snap.BaseM3PerSec <= 0)
            return;

        string ore = snap.CurrentOre ?? "";
        if (_lastOre.TryGetValue(character, out var previousOre) &&
            !string.Equals(previousOre, ore, StringComparison.OrdinalIgnoreCase))
        {
            _learnedBase.Remove(character);
            _dropSince.Remove(character);
            _dropAlerted.Remove(character);
            _degraded.Remove(character);
        }
        _lastOre[character] = ore;

        double current = snap.BaseM3PerSec;
        if (!_learnedBase.TryGetValue(character, out double learned) || learned <= 0)
        {
            learned = current;
            _learnedBase[character] = current;
            return;
        }

        // Smoothly learn normal changes, but never "learn" a large sustained
        // drop (such as losing one of two strip miners) as the new normal.
        if (current >= learned * 0.85)
        {
            _learnedBase[character] = learned * 0.90 + current * 0.10;

            _dropSince.Remove(character);
            _dropAlerted.Remove(character);
            _degraded.Remove(character);
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
