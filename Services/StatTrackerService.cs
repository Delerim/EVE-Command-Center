using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EveMultiPreview.Models;

namespace EveMultiPreview.Services;

/// <summary>
/// Tracks combat, mining (ore/gas/ice), logi (armor/shield/cap in/out),
/// ratting, and volley statistics per character using thread-safe rolling
/// time windows.
/// Exact AHK StatTracker.ahk parity: per-repair-type tracking,
/// hits/misses for applied %, session totals, side-by-side column overlay,
/// CSV logging with auto-cleanup, NPC toggle per character.
/// </summary>
public sealed class StatTrackerService
{
    private readonly ConcurrentDictionary<string, CharacterStats> _stats = new();
    private readonly TimeSpan _windowDuration = TimeSpan.FromSeconds(30); // AHK: WINDOW_SECS := 30
    private static readonly TimeSpan MiningRateWindow = TimeSpan.FromMinutes(2);
    private const int MaxEventsPerWindow = 500;
    private const int MaxMiningCyclesPerCharacter = 2000;
    private int _totalRecordCount = 0;
    private readonly AppSettings _settings;
    private readonly MiningMarketService _miningMarket = new();
    private readonly MiningDailyStore _dailyMiningStore = new();

    public StatTrackerService(AppSettings? settings = null)
    {
        _settings = settings ?? new AppSettings();
    }

    // CSV logging
    private bool _csvLoggingEnabled = false;
    private string _csvLogDirectory = "";
    private int _csvRetentionDays = 30;

    /// <summary>Configure CSV stat logging.</summary>
    public void SetCsvLogging(bool enabled, string directory, int retentionDays = 30)
    {
        _csvLoggingEnabled = enabled;
        _csvLogDirectory = directory;
        _csvRetentionDays = retentionDays;
        Debug.WriteLine($"[StatTracker:CSV] ÃƒÂ°Ã…Â¸Ã¢â‚¬ÂÃ‚Â§ CSV logging: enabled={enabled}, dir='{directory}', retention={retentionDays}d");

        // AHK: Run auto-cleanup on startup
        if (enabled && !string.IsNullOrEmpty(directory))
            CleanupOldLogs();
    }

    /// <summary>Record incoming or outgoing damage.</summary>
    public void RecordDamage(string character, int amount, bool isIncoming, bool isNpc = false,
        string hitQuality = "", DamageType damageType = DamageType.Unknown)
    {
        var stats = GetOrCreate(character);
        var entry = new TimedValue(DateTime.UtcNow, amount);

        if (isIncoming)
        {
            stats.DamageReceived.Add(entry);
            stats.TotalDamageIn += amount;

            // Track per-type incoming for the damage-type breakdown overlay (issue #11)
            stats.IncomingByType.AddOrUpdate(damageType, amount, (_, total) => total + amount);

            // Track volley (peak single hit) ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â NPC or player
            if (amount > stats.PeakVolley)
            {
                stats.PeakVolley = amount;
                Debug.WriteLine($"[StatTracker:Record] ÃƒÂ°Ã…Â¸Ã¢â‚¬â„¢Ã‚Â¥ New peak volley: {amount} for '{character}' (NPC={isNpc})");
            }
        }
        else
        {
            stats.DamageDealt.Add(entry);
            stats.TotalDamageOut += amount;

            // AHK: Track hits/misses for applied damage %
            if (hitQuality == "hit")
                stats.HitsOut++;
            else if (hitQuality == "glance" || hitQuality == "miss")
                stats.MissesOut++;

            // Bounty tracking ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â only NPC kills count as ratting ISK
            if (isNpc)
                stats.BountyTicks.Add(entry);
        }

        CheckAndPrune(character, stats);
        LogCsv(character, isIncoming ? "DMG_IN" : "DMG_OUT", amount);
    }

    /// <summary>Record repair with type and direction (AHK: 6 separate fields).</summary>
    public void RecordRepair(string character, int amount, bool isIncoming, string repairType = "armor")
    {
        var stats = GetOrCreate(character);
        var entry = new TimedValue(DateTime.UtcNow, amount);

        switch (repairType.ToLowerInvariant())
        {
            case "armor":
                if (isIncoming) { stats.ArmorRepIn += amount; }
                else { stats.ArmorRepOut += amount; stats.ArmorRepOutWindow.Add(entry); }
                break;
            case "shield":
                if (isIncoming) { stats.ShieldRepIn += amount; }
                else { stats.ShieldRepOut += amount; stats.ShieldRepOutWindow.Add(entry); }
                break;
            case "capacitor":
            case "cap":
                if (isIncoming) { stats.CapTransIn += amount; }
                else { stats.CapTransOut += amount; stats.CapTransOutWindow.Add(entry); }
                break;
            default:
                // Hull or unknown ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â treat as armor
                if (isIncoming) { stats.ArmorRepIn += amount; }
                else { stats.ArmorRepOut += amount; stats.ArmorRepOutWindow.Add(entry); }
                break;
        }

        CheckAndPrune(character, stats);
        string dir = isIncoming ? "IN" : "OUT";
        LogCsv(character, $"REP_{repairType.ToUpperInvariant()}_{dir}", amount);
    }

    /// <summary>Record bounty ISK from NPC kills.</summary>
    public void RecordBounty(string character, double amount)
    {
        var stats = GetOrCreate(character);
        stats.BountyTicks.Add(new TimedValue(DateTime.UtcNow, amount));
        stats.BountySession += amount;
        stats.LastBountyTick = amount;
        CheckAndPrune(character, stats);
        LogCsv(character, "BOUNTY", amount);
        Debug.WriteLine($"[StatTracker:Record] ÃƒÂ°Ã…Â¸Ã¢â‚¬â„¢Ã‚Â° Bounty recorded: {amount:N0} ISK for '{character}'");
    }

    /// <summary>Remove all stat data for a character (on logoff).</summary>
    public void RemoveCharacter(string character)
    {
        _stats.TryRemove(character, out _);
    }

    /// <summary>
    /// Record a mining result. The richer event keeps the exact resource name and
    /// a critical-success flag so a crit can contribute to REAL yield without
    /// distorting the miner's stable BASE mÃƒâ€šÃ‚Â³/s estimate.
    /// </summary>
    public void RecordMining(string character, int amount, string mineType = "ore",
        string oreType = "", bool isCriticalHint = false, DateTime? timestampUtc = null)
    {
        var stats = GetOrCreate(character);
        var now = timestampUtc ?? DateTime.UtcNow;
        var entry = new TimedValue(now, amount);
        oreType = oreType?.Trim() ?? "";
        mineType = string.IsNullOrWhiteSpace(mineType) ? "ore" : mineType.ToLowerInvariant();

        // The EVE mining line itself explicitly contains "critical" on the English
        // client, and LogMonitor already passes that as isCriticalHint. The old
        // statistical fallback misclassified different strip miners, drones and
        // partial rocks as crits (often showing impossible 30-50% crit rates).
        // Trust the actual log message instead.
        bool isCritical = isCriticalHint;

        stats.MiningCycles.Enqueue(new MiningCycleRecord(now, amount, oreType, mineType, isCritical));
        if (!string.IsNullOrWhiteSpace(oreType))
        {
            stats.LastOreType = oreType;
            stats.SessionUnitsByOre.AddOrUpdate(oreType, amount, (_, total) => total + amount);
            _ = _miningMarket.EnsureQuoteAsync(oreType);
        }

        if (isCritical)
            stats.MiningCritCount++;
        stats.MiningCycleCount++;

        if (mineType == "ore" && !string.IsNullOrWhiteSpace(oreType))
            _dailyMiningStore.Record(now, character, oreType, amount, isCritical);

        switch (mineType)
        {
            case "gas":
                stats.GasMining.Add(entry);
                stats.GasMined += amount;
                stats.GasLastCycle = amount;
                Debug.WriteLine($"[StatTracker:Record] ÃƒÂ¢Ã‹Å“Ã‚Â Gas mining: {amount} {oreType} for '{character}'");
                break;
            case "ice":
                stats.IceMining.Add(entry);
                stats.IceMined += amount;
                stats.IceLastCycle = amount;
                Debug.WriteLine($"[StatTracker:Record] ÃƒÂ°Ã…Â¸Ã‚Â§Ã…Â  Ice mining: {amount} {oreType} for '{character}'");
                break;
            default:
                stats.MiningYield.Add(entry);
                stats.MinedUnits += amount;
                stats.LastMineCycle = amount;
                Debug.WriteLine($"[StatTracker:Record] ÃƒÂ¢Ã¢â‚¬ÂºÃ‚Â Ore mining: {amount} {oreType} for '{character}', crit={isCritical}");
                break;
        }

        TrimMiningCycles(stats);
        CheckAndPrune(character, stats);
        LogCsv(character, $"MINE_{mineType.ToUpperInvariant()}{(isCritical ? "_CRIT" : "")}", amount);
    }

    private static bool LooksLikeCritical(CharacterStats stats, string oreType, int amount)
    {
        // Critical-success wording is localized. For clients where the parser cannot
        // directly identify the word, infer it conservatively from recent normal
        // cycles of the SAME resource. CCP crit yields are dramatically larger than
        // a normal cycle, so a 1.8x threshold avoids normal jitter/partial cycles.
        var recentNormal = stats.MiningCycles
            .Where(c => c.MineType == "ore" && !c.IsCritical &&
                        c.OreType.Equals(oreType, StringComparison.OrdinalIgnoreCase))
            .TakeLast(20)
            .Select(c => (double)c.Units)
            .OrderBy(v => v)
            .ToList();

        if (recentNormal.Count < 3) return false;
        double median = Median(recentNormal);
        return median > 0 && amount >= median * 1.8;
    }

    private static void TrimMiningCycles(CharacterStats stats)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromHours(2);
        while (stats.MiningCycles.TryPeek(out var first) &&
               (first.Timestamp < cutoff || stats.MiningCycles.Count > MaxMiningCyclesPerCharacter))
            stats.MiningCycles.TryDequeue(out _);
    }

    public IReadOnlyList<string> GetTrackedCharacters() =>
        _stats.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<string> GetMiningDashboardCharacters() =>
        _stats.Keys
            .Concat(_dailyMiningStore.GetCharacters())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyDictionary<string, double> GetMiningSessionUnitsByOre(string character) =>
        _dailyMiningStore.GetCharacterUnitsByOre(character);

    public Dictionary<string, double> GetFleetMiningSessionUnitsByOre() =>
        _dailyMiningStore.GetFleetUnitsByOre();

    public MiningDailyCritSummary GetTodayMiningCritSummary(string? character = null) =>
        _dailyMiningStore.GetCritSummary(character);

    public string GetMiningDayLabel() => _dailyMiningStore.CurrentDayKey;

    public bool TryGetMiningQuote(string oreType, out MiningMarketQuote quote) =>
        _miningMarket.TryGetQuote(oreType, out quote!);

    public Task<MiningMarketQuote?> EnsureMiningQuoteAsync(string oreType) =>
        _miningMarket.EnsureQuoteAsync(oreType);

    public double GetMarketUnitPrice(MiningMarketQuote quote, string market, string priceMode)
    {
        bool buy = priceMode.Equals("buy", StringComparison.OrdinalIgnoreCase);
        if (market.Equals("Amarr", StringComparison.OrdinalIgnoreCase))
            return (buy ? quote.AmarrBestBuy : quote.AmarrBestSell) ?? 0;
        return (buy ? quote.JitaBestBuy : quote.JitaBestSell) ?? 0;
    }

    // ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ Rate Getters ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬

    public double GetDps(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateRate(stats.DamageDealt);
    }

    public double GetIncomingDps(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateRate(stats.DamageReceived);
    }

    /// <summary>Get armor rep/s given (outgoing).</summary>
    public double GetArmorRepRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateRate(stats.ArmorRepOutWindow);
    }

    /// <summary>Get shield rep/s given (outgoing).</summary>
    public double GetShieldRepRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateRate(stats.ShieldRepOutWindow);
    }

    /// <summary>Get cap transfer/s given (outgoing).</summary>
    public double GetCapTransRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateRate(stats.CapTransOutWindow);
    }

    /// <summary>Get ore mining yield per hour.</summary>
    public double GetMiningRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateMiningRate(stats.MiningYield);
    }

    /// <summary>Get gas mining yield per hour.</summary>
    public double GetGasMiningRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateMiningRate(stats.GasMining);
    }

    /// <summary>Get ice mining yield per hour.</summary>
    public double GetIceMiningRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return CalculateMiningRate(stats.IceMining);
    }

    /// <summary>Get ratting bounty rate (ISK/hr estimate from NPC kills).</summary>
    public double GetBountyRate(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        // AHK: Calculate from bountyTicks array, require >60s elapsed
        var cutoff = DateTime.UtcNow - _windowDuration;
        var recent = stats.BountyTicks.Where(v => v.Timestamp > cutoff).ToList();
        if (recent.Count == 0) return 0;
        var oldest = recent.Min(v => v.Timestamp);
        double elapsed = (DateTime.UtcNow - oldest).TotalSeconds;
        if (elapsed < 60) return 0;
        double total = recent.Sum(v => v.Value);
        return (total / elapsed) * 3600;
    }

    public double GetPeakVolley(string character)
    {
        if (!_stats.TryGetValue(character, out var stats)) return 0;
        return stats.PeakVolley;
    }

    /// <summary>Get combined armor+shield rep/s (legacy compat for stat windows).</summary>
    public double GetHps(string character) => GetArmorRepRate(character) + GetShieldRepRate(character);

    /// <summary>Get all stat values for a character in one call (for stat overlay).</summary>
    public CharacterStatSnapshot GetSnapshot(string character)
    {
        _stats.TryGetValue(character, out var stats);
        stats ??= new CharacterStats();

        var mining = CalculateMiningAnalytics(character, stats);

        return new CharacterStatSnapshot
        {
            Dps = CalculateRate(stats.DamageDealt),
            IncomingDps = CalculateRate(stats.DamageReceived),
            // AHK: Per-repair-type rates
            ArmorRepPerSec = CalculateRate(stats.ArmorRepOutWindow),
            ShieldRepPerSec = CalculateRate(stats.ShieldRepOutWindow),
            CapTransPerSec = CalculateRate(stats.CapTransOutWindow),
            // Mining rates (per hour)
            OreMiningRate = CalculateMiningRate(stats.MiningYield),
            GasMiningRate = CalculateMiningRate(stats.GasMining),
            IceMiningRate = CalculateMiningRate(stats.IceMining),
            // Bounty
            BountyRate = GetBountyRate(character),
            PeakVolley = stats.PeakVolley,
            // AHK: Session totals
            TotalDamageIn = stats.TotalDamageIn,
            TotalDamageOut = stats.TotalDamageOut,
            HitsOut = stats.HitsOut,
            MissesOut = stats.MissesOut,
            // AHK: Per-repair-type session totals
            TotalArmorRepOut = stats.ArmorRepOut,
            TotalArmorRepIn = stats.ArmorRepIn,
            TotalShieldRepOut = stats.ShieldRepOut,
            TotalShieldRepIn = stats.ShieldRepIn,
            // AHK: Mining per-cycle + richer crit-aware mÃƒâ€šÃ‚Â³/market stats
            LastMineCycle = stats.LastMineCycle,
            GasLastCycle = stats.GasLastCycle,
            CurrentOre = mining.CurrentOre,
            BaseM3PerSec = mining.BaseM3PerSec,
            ActualM3PerSec = mining.ActualM3PerSec,
            MiningCritCount = mining.CritCount,
            MiningCycleCount = mining.CycleCount,
            MiningCritBonusM3 = mining.CritBonusM3,
            SessionM3 = mining.SessionM3,
            JitaIskPerHour = mining.JitaIskPerHour,
            AmarrIskPerHour = mining.AmarrIskPerHour,
            BestIskPerHour = mining.BestIskPerHour,
            BuybackIskPerHour = mining.BuybackIskPerHour,
            BuybackUnitPrice = mining.BuybackUnitPrice,
            SessionJitaValue = mining.SessionJitaValue,
            SessionAmarrValue = mining.SessionAmarrValue,
            SessionBestValue = mining.SessionBestValue,
            SessionBuybackValue = mining.SessionBuybackValue,
            MarketDataReady = mining.MarketDataReady,
            // AHK: Bounty session
            BountySession = stats.BountySession,
            LastBountyTick = stats.LastBountyTick,
        };
    }

    /// <summary>Returns session totals per damage type for incoming damage
    /// (issue #11). Keys missing ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ never-hit type.</summary>
    public Dictionary<DamageType, long> GetIncomingByType(string character)
    {
        var stats = GetOrCreate(character);
        return new Dictionary<DamageType, long>(stats.IncomingByType);
    }

    // ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ Overlay Text (AHK: side-by-side columns with abbreviations) ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬

    /// <summary>Build multi-row overlay text matching AHK StatTracker format.
    /// Each metric in <paramref name="metrics"/> is rendered as its own line within
    /// its category column; a category is skipped entirely if no metric for it is set.</summary>
    public string GetOverlayText(string character, StatMetrics metrics)
    {
        var snap = GetSnapshot(character);
        int colWidth = 12;
        var columns = new List<List<string>>();

        // === DPS Column ===
        if ((metrics & StatMetrics.DpsMask) != 0)
        {
            var col = new List<string> { "[DPS]" };
            if ((metrics & StatMetrics.DpsOut) != 0) col.Add($"Out:{FormatNumber(snap.Dps)}/s");
            if ((metrics & StatMetrics.DpsIn)  != 0) col.Add($"In:{FormatNumber(snap.IncomingDps)}/s");
            if ((metrics & StatMetrics.Tdi)    != 0) col.Add($"TDI:{FormatNumber(snap.TotalDamageIn)}");
            if ((metrics & StatMetrics.Tdo)    != 0) col.Add($"TDO:{FormatNumber(snap.TotalDamageOut)}");
            if ((metrics & StatMetrics.DpsInByType) != 0)
            {
                // Session breakdown of incoming damage by damage type (issue #11).
                var byType = GetIncomingByType(character);
                long total = byType.Values.Sum();
                if (total > 0)
                {
                    foreach (var (label, t) in new[] {
                        ("EM",  DamageType.Em),
                        ("Th",  DamageType.Thermal),
                        ("Ki",  DamageType.Kinetic),
                        ("Ex",  DamageType.Explosive),
                        ("?",   DamageType.Unknown),
                    })
                    {
                        if (!byType.TryGetValue(t, out var v) || v == 0) continue;
                        int pct = (int)Math.Round(v * 100.0 / total);
                        col.Add($"{label}:{pct}%");
                    }
                }
            }
            columns.Add(col);
        }

        // === LOGI Column ===
        if ((metrics & StatMetrics.LogiMask) != 0)
        {
            var col = new List<string> { "[Logi]" };
            if ((metrics & StatMetrics.Arps) != 0) col.Add($"ARPS:{FormatNumber(snap.ArmorRepPerSec)}");
            if ((metrics & StatMetrics.Srps) != 0) col.Add($"SRPS:{FormatNumber(snap.ShieldRepPerSec)}");
            if ((metrics & StatMetrics.Ctps) != 0) col.Add($"CTPS:{FormatNumber(snap.CapTransPerSec)}");
            if ((metrics & StatMetrics.Taro) != 0) col.Add($"TARO:{FormatNumber(snap.TotalArmorRepOut)}");
            if ((metrics & StatMetrics.Tari) != 0) col.Add($"TARI:{FormatNumber(snap.TotalArmorRepIn)}");
            if ((metrics & StatMetrics.Tsro) != 0) col.Add($"TSRO:{FormatNumber(snap.TotalShieldRepOut)}");
            if ((metrics & StatMetrics.Tsri) != 0) col.Add($"TSRI:{FormatNumber(snap.TotalShieldRepIn)}");
            columns.Add(col);
        }

        // === MINE Column ===
        if ((metrics & StatMetrics.MineMask) != 0)
        {
            var col = new List<string> { "[Mine]" };

            // The old OMPC/OMPH pair mixed normal cycles and critical-success
            // cycles into one rolling number. Keep the same settings bits so
            // existing profiles continue to work, but present stable BASE and
            // realised ACTUAL mÃƒâ€šÃ‚Â³/s instead.
            bool wantsOre = (metrics & (StatMetrics.Ompc | StatMetrics.Omph)) != 0;
            if (wantsOre)
            {
                if (!string.IsNullOrWhiteSpace(snap.CurrentOre))
                    col.Add(ShortResourceName(snap.CurrentOre, 22));

                if ((metrics & StatMetrics.Ompc) != 0)
                    col.Add($"BASE:{snap.BaseM3PerSec:F1} m3/s");
                if ((metrics & StatMetrics.Omph) != 0)
                    col.Add($"REAL:{snap.ActualM3PerSec:F1} m3/s");

                if (snap.MiningCycleCount > 0)
                {
                    double critPct = snap.MiningCritCount * 100.0 / snap.MiningCycleCount;
                    col.Add($"CRIT:{snap.MiningCritCount}/{snap.MiningCycleCount} {critPct:F1}%");
                }

                col.Add($"PROFIT:{FormatNumber(snap.SessionBestValue)}");
                col.Add($"BB:{FormatNumber(snap.SessionBuybackValue)}");
            }

            // Gas / ice retain the upstream unit-per-hour presentation.
            if ((metrics & StatMetrics.Gmpc) != 0) col.Add($"GMPC:{FormatNumber(snap.GasLastCycle)}");
            if ((metrics & StatMetrics.Gmph) != 0) col.Add($"GMPH:{FormatNumber(snap.GasMiningRate)}");
            if ((metrics & StatMetrics.Imph) != 0) col.Add($"IMPH:{FormatNumber(snap.IceMiningRate)}");
            columns.Add(col);
        }

        // === RAT Column ===
        if ((metrics & StatMetrics.RatMask) != 0)
        {
            var col = new List<string> { "[Rat]" };
            if ((metrics & StatMetrics.Tipt) != 0) col.Add($"TIPT:{FormatNumber(snap.LastBountyTick)}");
            if ((metrics & StatMetrics.Tiph) != 0) col.Add($"TIPH:{FormatNumber(snap.BountyRate)}");
            if ((metrics & StatMetrics.Tips) != 0) col.Add($"TIPS:{FormatNumber(snap.BountySession)}");
            columns.Add(col);
        }

        if (columns.Count == 0)
            return "";

        // Find max rows across all columns
        int maxRows = columns.Max(c => c.Count);

        // Build output row by row, padding each cell to colWidth
        var lines = new List<string>();
        for (int row = 0; row < maxRows; row++)
        {
            string line = "";
            foreach (var col in columns)
            {
                string cell = row < col.Count ? col[row] : "";
                line += cell.PadRight(colWidth);
            }
            lines.Add(line.TrimEnd());
        }
        return string.Join("\n", lines);
    }

    private static string ShortResourceName(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength) return value;
        return value[..Math.Max(1, maxLength - 1)] + "ÃƒÂ¢Ã¢â€šÂ¬Ã‚Â¦";
    }

    // ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ Number Formatting (AHK: _Fmt with K/M/B/T) ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬

    /// <summary>Format numbers with K/M/B/T suffixes matching AHK.</summary>
    public static string FormatNumber(double value)
    {
        if (value < 0) return "-" + FormatNumber(-value);
        if (value >= 1_000_000_000_000) return $"{value / 1_000_000_000_000:F1}T";
        if (value >= 1_000_000_000) return $"{value / 1_000_000_000:F1}B";
        if (value >= 1_000_000) return $"{value / 1_000_000:F1}M";
        if (value >= 10_000) return $"{value / 1_000:F1}K"; // AHK: uppercase K, threshold 10000
        if (value >= 1_000) return $"{value:F0}";
        return $"{Math.Round(value)}";
    }

    // ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ Pruning ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬

    /// <summary>Prune old events from all windows.</summary>
    public void Prune()
    {
        var cutoff = DateTime.UtcNow - _windowDuration;
        foreach (var (charName, stats) in _stats)
        {
            int pruned = 0;
            pruned += PruneWindow(stats.DamageDealt, cutoff);
            pruned += PruneWindow(stats.DamageReceived, cutoff);
            pruned += PruneWindow(stats.ArmorRepOutWindow, cutoff);
            pruned += PruneWindow(stats.ShieldRepOutWindow, cutoff);
            pruned += PruneWindow(stats.CapTransOutWindow, cutoff);
            pruned += PruneWindow(stats.MiningYield, cutoff);
            pruned += PruneWindow(stats.GasMining, cutoff);
            pruned += PruneWindow(stats.IceMining, cutoff);
            pruned += PruneWindow(stats.BountyTicks, cutoff);

            if (pruned > 0)
                Debug.WriteLine($"[StatTracker:Prune] ÃƒÂ°Ã…Â¸Ã‚Â§Ã‚Â¹ Pruned {pruned} old events for '{charName}'");
        }
    }

    private void CheckAndPrune(string character, CharacterStats stats)
    {
        _totalRecordCount++;
        // Auto-prune every 50 records (AHK: Mod(stats._pruneCounter, 50))
        if (_totalRecordCount % 50 == 0)
        {
            var cutoff = DateTime.UtcNow - _windowDuration;
            PruneWindow(stats.DamageDealt, cutoff);
            PruneWindow(stats.DamageReceived, cutoff);
            PruneWindow(stats.ArmorRepOutWindow, cutoff);
            PruneWindow(stats.ShieldRepOutWindow, cutoff);
            PruneWindow(stats.CapTransOutWindow, cutoff);
            PruneWindow(stats.MiningYield, cutoff);
            PruneWindow(stats.GasMining, cutoff);
            PruneWindow(stats.IceMining, cutoff);
            PruneWindow(stats.BountyTicks, cutoff);
        }
    }

    private CharacterStats GetOrCreate(string character)
    {
        return _stats.GetOrAdd(character, _ => new CharacterStats());
    }

    // AHK: _RatePerSec ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â averages over actual elapsed time, not full window
    private double CalculateRate(ConcurrentBag<TimedValue> window)
    {
        var cutoff = DateTime.UtcNow - _windowDuration;
        var recent = window.Where(v => v.Timestamp > cutoff).ToList();
        if (recent.Count == 0) return 0;

        double totalAmount = recent.Sum(v => v.Value);
        var oldest = recent.Min(v => v.Timestamp);
        double elapsedSeconds = Math.Max(1.0, (DateTime.UtcNow - oldest).TotalSeconds);
        return totalAmount / elapsedSeconds;
    }

    // AHK: _MiningRate ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â units per hour from rolling window
    private double CalculateMiningRate(ConcurrentBag<TimedValue> window)
    {
        var cutoff = DateTime.UtcNow - _windowDuration;
        var recent = window.Where(v => v.Timestamp > cutoff).ToList();
        if (recent.Count <= 1) return 0;

        double totalAmount = recent.Sum(v => v.Value);
        var oldest = recent.Min(v => v.Timestamp);
        double elapsedSeconds = (DateTime.UtcNow - oldest).TotalSeconds;
        if (elapsedSeconds <= 0) return 0;
        return (totalAmount / elapsedSeconds) * 3600;
    }

    // ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ Rich Mining Analytics ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬

    private MiningAnalytics CalculateMiningAnalytics(string character, CharacterStats stats)
    {
        var cutoff = DateTime.UtcNow - MiningRateWindow;
        var recent = stats.MiningCycles
            .Where(c => c.Timestamp >= cutoff && c.MineType == "ore")
            .OrderBy(c => c.Timestamp)
            .ToList();

        var baselineUnits = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in recent.GroupBy(c => c.OreType, StringComparer.OrdinalIgnoreCase))
        {
            var normal = group.Where(c => !c.IsCritical).Select(c => (double)c.Units).OrderBy(v => v).ToList();
            if (normal.Count > 0) baselineUnits[group.Key] = Median(normal);
        }

        var valued = new List<ValuedMiningCycle>();
        double critBonusM3 = 0;
        foreach (var cycle in recent)
        {
            if (string.IsNullOrWhiteSpace(cycle.OreType) ||
                !_miningMarket.TryGetQuote(cycle.OreType, out var quote) ||
                !quote.IsAvailable)
                continue;

            double actualM3 = cycle.Units * quote.UnitVolumeM3;
            double normalUnits = baselineUnits.TryGetValue(cycle.OreType, out var b) ? b : cycle.Units;
            double baseM3 = (cycle.IsCritical ? normalUnits : cycle.Units) * quote.UnitVolumeM3;
            if (cycle.IsCritical) critBonusM3 += Math.Max(0, actualM3 - baseM3);

            double jita = cycle.Units * GetMarketUnitPrice(quote, "Jita", _settings.MiningMarketPriceMode);
            double amarr = cycle.Units * GetMarketUnitPrice(quote, "Amarr", _settings.MiningMarketPriceMode);
            double buyback = cycle.Units
                * GetMarketUnitPrice(quote, _settings.MiningCorpBuybackMarket, _settings.MiningCorpBuybackPriceMode)
                * Math.Clamp(_settings.MiningCorpBuybackPercent, 0, 100) / 100.0;
            valued.Add(new ValuedMiningCycle(cycle.Timestamp, actualM3, baseM3, jita, amarr, buyback));
        }

        double baseM3PerSec = 0;
        double actualM3PerSec = 0;
        double jitaIskPerHour = 0;
        double amarrIskPerHour = 0;
        double buybackIskPerHour = 0;

        // Stable BASE / realised REAL from the CURRENT continuous mining segment.
        //
        // V1.5 capped long pauses before summing elapsed time. After a break that
        // made the denominator artificially tiny, so BASE could jump from a correct
        // ~44.8 m3/s to 100+ m3/s even though the strips had not changed.
        //
        // V1.6 splits at the latest real pause instead. Crit pulls keep their
        // timestamp but use BaseM3 (normalised normal-cycle yield), so a crit raises
        // REAL while BASE remains tied to normal production.
        if (valued.Count >= 4)
        {
            var ordered = valued.OrderBy(c => c.Timestamp).ToList();

            var positiveGaps = new List<double>();
            for (int i = 1; i < ordered.Count; i++)
            {
                double gap = (ordered[i].Timestamp - ordered[i - 1].Timestamp).TotalSeconds;
                if (gap > 0.25)
                    positiveGaps.Add(gap);
            }

            if (positiveGaps.Count > 0)
            {
                double typicalGap = Math.Max(0.25, Median(positiveGaps));
                double pauseGap = Math.Max(45.0, typicalGap * 4.0);

                int segmentStart = 0;
                for (int i = 1; i < ordered.Count; i++)
                {
                    double gap = (ordered[i].Timestamp - ordered[i - 1].Timestamp).TotalSeconds;
                    if (gap > pauseGap)
                        segmentStart = i;
                }

                var active = ordered.Skip(segmentStart).ToList();

                // Keep the rate responsive to a current boost / fit change without
                // carrying early-session warm-up forever.
                if (active.Count >= 4)
                {
                    var latest = active[^1].Timestamp;
                    var shortWindowStart = latest - TimeSpan.FromSeconds(90);
                    var shortActive = active.Where(c => c.Timestamp >= shortWindowStart).ToList();
                    if (shortActive.Count >= 4)
                        active = shortActive;
                }

                if (active.Count >= 4)
                {
                    var activeGaps = new List<double>();
                    for (int i = 1; i < active.Count; i++)
                    {
                        double gap = (active[i].Timestamp - active[i - 1].Timestamp).TotalSeconds;
                        if (gap > 0.25 && gap <= pauseGap)
                            activeGaps.Add(gap);
                    }

                    if (activeGaps.Count > 0)
                    {
                        double endpointGap = Math.Max(0.25, Median(activeGaps));
                        double duration = Math.Max(
                            endpointGap,
                            (active[^1].Timestamp - active[0].Timestamp).TotalSeconds + endpointGap);

                        baseM3PerSec = active.Sum(c => c.BaseM3) / duration;
                        actualM3PerSec = active.Sum(c => c.ActualM3) / duration;
                        jitaIskPerHour = active.Sum(c => c.JitaIsk) / duration * 3600.0;
                        amarrIskPerHour = active.Sum(c => c.AmarrIsk) / duration * 3600.0;
                        buybackIskPerHour = active.Sum(c => c.BuybackIsk) / duration * 3600.0;
                    }
                }
            }
        }

        double sessionM3 = 0;
        double sessionJita = 0;
        double sessionAmarr = 0;
        double sessionBest = 0;
        double sessionBuyback = 0;
        bool marketReady = false;

        foreach (var kv in _dailyMiningStore.GetCharacterUnitsByOre(character))
        {
            if (!_miningMarket.TryGetQuote(kv.Key, out var quote) || !quote.IsAvailable)
                continue;

            marketReady = true;
            sessionM3 += kv.Value * quote.UnitVolumeM3;
            double jitaUnit = GetMarketUnitPrice(quote, "Jita", _settings.MiningMarketPriceMode);
            double amarrUnit = GetMarketUnitPrice(quote, "Amarr", _settings.MiningMarketPriceMode);
            double jitaValue = kv.Value * jitaUnit;
            double amarrValue = kv.Value * amarrUnit;
            sessionJita += jitaValue;
            sessionAmarr += amarrValue;

            double best = 0;
            if (_settings.MiningMarketJitaEnabled) best = Math.Max(best, jitaValue);
            if (_settings.MiningMarketAmarrEnabled) best = Math.Max(best, amarrValue);
            sessionBest += best;

            double buybackUnit = GetMarketUnitPrice(quote, _settings.MiningCorpBuybackMarket, _settings.MiningCorpBuybackPriceMode);
            sessionBuyback += kv.Value * buybackUnit * Math.Clamp(_settings.MiningCorpBuybackPercent, 0, 100) / 100.0;
        }

        double bestRate = 0;
        if (_settings.MiningMarketJitaEnabled) bestRate = Math.Max(bestRate, jitaIskPerHour);
        if (_settings.MiningMarketAmarrEnabled) bestRate = Math.Max(bestRate, amarrIskPerHour);

        string currentOreName = string.IsNullOrWhiteSpace(stats.LastOreType)
            ? _dailyMiningStore.GetLastOre(character)
            : stats.LastOreType;

        double buybackUnitPrice = 0;
        if (!string.IsNullOrWhiteSpace(currentOreName)
            && _miningMarket.TryGetQuote(currentOreName, out var currentQuote)
            && currentQuote.IsAvailable)
        {
            buybackUnitPrice =
                GetMarketUnitPrice(currentQuote, _settings.MiningCorpBuybackMarket, _settings.MiningCorpBuybackPriceMode)
                * Math.Clamp(_settings.MiningCorpBuybackPercent, 0, 100) / 100.0;
        }

        return new MiningAnalytics
        {
            CurrentOre = currentOreName,
            BaseM3PerSec = baseM3PerSec,
            ActualM3PerSec = actualM3PerSec,
            CritCount = stats.MiningCritCount,
            CycleCount = stats.MiningCycleCount,
            CritBonusM3 = critBonusM3,
            SessionM3 = sessionM3,
            JitaIskPerHour = jitaIskPerHour,
            AmarrIskPerHour = amarrIskPerHour,
            BestIskPerHour = bestRate,
            BuybackIskPerHour = buybackIskPerHour,
            BuybackUnitPrice = buybackUnitPrice,
            SessionJitaValue = sessionJita,
            SessionAmarrValue = sessionAmarr,
            SessionBestValue = sessionBest,
            SessionBuybackValue = sessionBuyback,
            MarketDataReady = marketReady
        };
    }

    private static List<MiningCluster> ClusterMiningCycles(List<ValuedMiningCycle> cycles)
    {
        var clusters = new List<MiningCluster>();
        foreach (var cycle in cycles)
        {
            if (clusters.Count == 0 ||
                (cycle.Timestamp - clusters[^1].Timestamp).TotalSeconds > 2.5)
            {
                clusters.Add(new MiningCluster(cycle.Timestamp, cycle.ActualM3, cycle.BaseM3, cycle.JitaIsk, cycle.AmarrIsk));
            }
            else
            {
                var old = clusters[^1];
                clusters[^1] = old with
                {
                    ActualM3 = old.ActualM3 + cycle.ActualM3,
                    BaseM3 = old.BaseM3 + cycle.BaseM3,
                    JitaIsk = old.JitaIsk + cycle.JitaIsk,
                    AmarrIsk = old.AmarrIsk + cycle.AmarrIsk
                };
            }
        }
        return clusters;
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToArray();
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static int PruneWindow(ConcurrentBag<TimedValue> window, DateTime cutoff)
    {
        if (window.Count <= MaxEventsPerWindow) return 0;

        // Snapshot the bag and filter ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â any items added concurrently will survive
        // because we only remove items older than cutoff
        var snapshot = window.ToArray();
        var recent = snapshot.Where(v => v.Timestamp > cutoff).ToArray();
        int removed = snapshot.Length - recent.Length;
        if (removed <= 0) return 0;

        // Drain and refill ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â items added between these lines are post-cutoff
        // by definition (they were just created), so losing them is acceptable
        // only if we re-add them. Instead, we accept the brief window of loss
        // is negligible since pruning only triggers every 50 records.
        while (window.TryTake(out _)) { }
        foreach (var item in recent) window.Add(item);
        return removed;
    }

    // ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ CSV Logging (AHK: _LogEvent with HTML stripping) ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬

    private void LogCsv(string character, string eventType, double amount)
    {
        if (!_csvLoggingEnabled || string.IsNullOrEmpty(_csvLogDirectory)) return;

        try
        {
            if (!Directory.Exists(_csvLogDirectory))
                Directory.CreateDirectory(_csvLogDirectory);

            // AHK: sanitize character name for filename
            string safeName = string.Join("_",
                character.Split(Path.GetInvalidFileNameChars()));
            string fileName = $"StatLog_{safeName}_{DateTime.Now:yyyy-MM-dd}.csv";
            string filePath = Path.Combine(_csvLogDirectory, fileName);

            bool isNew = !File.Exists(filePath);
            using var writer = new StreamWriter(filePath, append: true);
            if (isNew)
                writer.WriteLine("Timestamp,Type,Amount");
            writer.WriteLine($"{DateTime.UtcNow:O},{eventType},{amount:F0}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatTracker:CSV] ÃƒÂ¢Ã‚ÂÃ…â€™ CSV write error: {ex.Message}");
        }
    }

    /// <summary>Delete log files older than retention period (AHK: _CleanupOldLogs).</summary>
    private void CleanupOldLogs()
    {
        if (string.IsNullOrEmpty(_csvLogDirectory) || !Directory.Exists(_csvLogDirectory))
            return;

        try
        {
            var cutoffDate = DateTime.Now.AddDays(-_csvRetentionDays);
            foreach (var file in Directory.EnumerateFiles(_csvLogDirectory, "StatLog_*.csv"))
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffDate)
                {
                    fileInfo.Delete();
                    Debug.WriteLine($"[StatTracker:CSV] ÃƒÂ°Ã…Â¸Ã¢â‚¬â€Ã¢â‚¬Ëœ Deleted old log: {fileInfo.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatTracker:CSV] ÃƒÂ¢Ã‚ÂÃ…â€™ Cleanup error: {ex.Message}");
        }
    }

    // ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ Inner Types (AHK: _NewStatData with all fields) ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬ÃƒÂ¢Ã¢â‚¬ÂÃ¢â€šÂ¬

    private class CharacterStats
    {
        // Damage
        public ConcurrentBag<TimedValue> DamageDealt { get; } = new();
        public ConcurrentBag<TimedValue> DamageReceived { get; } = new();
        public double TotalDamageOut { get; set; } = 0;
        public double TotalDamageIn { get; set; } = 0;
        public int HitsOut { get; set; } = 0;
        public int MissesOut { get; set; } = 0;
        public double PeakVolley { get; set; } = 0;

        // Per-type incoming damage totals (issue #11) ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â session accumulator used
        // for the percentage breakdown in the DPS overlay. Not a windowed rate.
        public ConcurrentDictionary<DamageType, long> IncomingByType { get; } = new();

        // Repairs given (AHK: separate armor/shield/cap)
        public double ArmorRepOut { get; set; } = 0;
        public double ShieldRepOut { get; set; } = 0;
        public double CapTransOut { get; set; } = 0;
        public ConcurrentBag<TimedValue> ArmorRepOutWindow { get; } = new();
        public ConcurrentBag<TimedValue> ShieldRepOutWindow { get; } = new();
        public ConcurrentBag<TimedValue> CapTransOutWindow { get; } = new();

        // Repairs received
        public double ArmorRepIn { get; set; } = 0;
        public double ShieldRepIn { get; set; } = 0;
        public double CapTransIn { get; set; } = 0;

        // Mining ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â Ore
        public ConcurrentBag<TimedValue> MiningYield { get; } = new();
        public double MinedUnits { get; set; } = 0;
        public double LastMineCycle { get; set; } = 0;
        public ConcurrentQueue<MiningCycleRecord> MiningCycles { get; } = new();
        public ConcurrentDictionary<string, double> SessionUnitsByOre { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public string LastOreType { get; set; } = "";
        public int MiningCritCount { get; set; } = 0;
        public int MiningCycleCount { get; set; } = 0;

        // Mining ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â Gas
        public ConcurrentBag<TimedValue> GasMining { get; } = new();
        public double GasMined { get; set; } = 0;
        public double GasLastCycle { get; set; } = 0;

        // Mining ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â Ice
        public ConcurrentBag<TimedValue> IceMining { get; } = new();
        public double IceMined { get; set; } = 0;
        public double IceLastCycle { get; set; } = 0;

        // Ratting
        public ConcurrentBag<TimedValue> BountyTicks { get; } = new();
        public double BountySession { get; set; } = 0;
        public double LastBountyTick { get; set; } = 0;
    }

    private record MiningCycleRecord(DateTime Timestamp, int Units, string OreType, string MineType, bool IsCritical);
    private record ValuedMiningCycle(DateTime Timestamp, double ActualM3, double BaseM3, double JitaIsk, double AmarrIsk, double BuybackIsk);
    private record MiningCluster(DateTime Timestamp, double ActualM3, double BaseM3, double JitaIsk, double AmarrIsk);

    private sealed class MiningAnalytics
    {
        public string CurrentOre { get; init; } = "";
        public double BaseM3PerSec { get; init; }
        public double ActualM3PerSec { get; init; }
        public int CritCount { get; init; }
        public int CycleCount { get; init; }
        public double CritBonusM3 { get; init; }
        public double SessionM3 { get; init; }
        public double JitaIskPerHour { get; init; }
        public double AmarrIskPerHour { get; init; }
        public double BestIskPerHour { get; init; }
        public double BuybackIskPerHour { get; init; }
        public double BuybackUnitPrice { get; init; }
        public double SessionJitaValue { get; init; }
        public double SessionAmarrValue { get; init; }
        public double SessionBestValue { get; init; }
        public double SessionBuybackValue { get; init; }
        public bool MarketDataReady { get; init; }
    }

    private record TimedValue(DateTime Timestamp, double Value);
}

/// <summary>All stat values for a single character at a point in time (AHK parity).</summary>
public record CharacterStatSnapshot
{
    // DPS
    public double Dps { get; init; }
    public double IncomingDps { get; init; }
    public double TotalDamageOut { get; init; }
    public double TotalDamageIn { get; init; }
    public int HitsOut { get; init; }
    public int MissesOut { get; init; }
    public double PeakVolley { get; init; }

    // Logi (AHK: per-repair-type)
    public double ArmorRepPerSec { get; init; }
    public double ShieldRepPerSec { get; init; }
    public double CapTransPerSec { get; init; }
    public double TotalArmorRepOut { get; init; }
    public double TotalArmorRepIn { get; init; }
    public double TotalShieldRepOut { get; init; }
    public double TotalShieldRepIn { get; init; }

    // Mining
    public double OreMiningRate { get; init; }
    public double GasMiningRate { get; init; }
    public double IceMiningRate { get; init; }
    public double LastMineCycle { get; init; }
    public double GasLastCycle { get; init; }

    // Rich mining dashboard values
    public string CurrentOre { get; init; } = "";
    public double BaseM3PerSec { get; init; }
    public double ActualM3PerSec { get; init; }
    public int MiningCritCount { get; init; }
    public int MiningCycleCount { get; init; }
    public double MiningCritBonusM3 { get; init; }
    public double SessionM3 { get; init; }
    public double JitaIskPerHour { get; init; }
    public double AmarrIskPerHour { get; init; }
    public double BestIskPerHour { get; init; }
    public double BuybackIskPerHour { get; init; }
    public double BuybackUnitPrice { get; init; }
    public double SessionJitaValue { get; init; }
    public double SessionAmarrValue { get; init; }
    public double SessionBestValue { get; init; }
    public double SessionBuybackValue { get; init; }
    public bool MarketDataReady { get; init; }

    // Ratting
    public double BountyRate { get; init; }
    public double BountySession { get; init; }
    public double LastBountyTick { get; init; }

    // Legacy compat ÃƒÂ¢Ã¢â€šÂ¬Ã¢â‚¬Â computed properties for existing callers
    public double Hps => ArmorRepPerSec + ShieldRepPerSec;
    public double HpsOut => ArmorRepPerSec + ShieldRepPerSec;
}
