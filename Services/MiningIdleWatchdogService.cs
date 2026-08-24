using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace EveMultiPreview.Services;

/// <summary>
/// Small sidecar settings file for the mining dashboard.  Kept separate from the
/// legacy AHK-compatible EVE MultiPreview.json so new mining-only options can be
/// added without risking the user's existing MultiPreview configuration.
/// </summary>
public sealed class MiningDashboardPreferences
{
    public bool JitaEnabled { get; set; } = true;
    public bool AmarrEnabled { get; set; } = true;
    public string MarketPriceMode { get; set; } = "sell";

    public double CorpBuybackPercent { get; set; } = 90.0;
    public string CorpBuybackMarket { get; set; } = "Jita";
    public string CorpBuybackPriceMode { get; set; } = "sell";

    public bool IdleWatchdogEnabled { get; set; } = false;
    public int IdleSeconds { get; set; } = 90;
    public bool IdleSoundEnabled { get; set; } = true;
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

            return JsonSerializer.Deserialize<MiningDashboardPreferences>(
                       File.ReadAllText(FilePath), JsonOptions)
                   ?? new MiningDashboardPreferences();
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
            // Dashboard preferences are non-critical.  Never take down MultiPreview
            // because a sidecar file could not be written.
        }
    }
}

public enum MiningIdleKind
{
    Waiting,
    Mining,
    Late,
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
        MiningIdleKind.Idle => "IDLE ⚠",
        _ => "WAITING"
    };
}

/// <summary>
/// Watches StatTracker's monotonically increasing mining-cycle count.  It does
/// not need another EVE parser: a changed cycle count means the existing parser
/// just received a mining pull for that character.
/// </summary>
public sealed class MiningIdleWatchdogService : IDisposable
{
    private readonly StatTrackerService _tracker;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, int> _lastCycleCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastActivityUtc =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _alerted =
        new(StringComparer.OrdinalIgnoreCase);

    public MiningDashboardPreferences Preferences { get; }
    public event Action<string>? IdleDetected;

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

        var kind =
            age >= idleAfter ? MiningIdleKind.Idle :
            age >= idleAfter * 0.70 ? MiningIdleKind.Late :
            MiningIdleKind.Mining;

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
            return;
        }

        if (count != previous)
        {
            _lastCycleCounts[character] = count;
            _lastActivityUtc[character] = now;
            _alerted.Remove(character);
            return;
        }

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

        if (_alerted.Add(character))
            IdleDetected?.Invoke(character);
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}

