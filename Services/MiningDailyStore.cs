using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EveMultiPreview.Services;

/// <summary>
/// Current mining-day store.
///
/// Only the active mining day keeps individual lightweight events in JSONL so
/// live restarts are safe. Completed days are compacted by MiningHistoryService
/// into history-v1.json and their JSONL files are deleted.
/// </summary>
public sealed class MiningDailyStore
{
    public const int DayCutoffHour = 4;

    private readonly object _gate = new();
    private readonly string _directory;
    private string _loadedDay = "";
    private readonly Dictionary<string, Dictionary<string, DailyOreTotals>> _byCharacter =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastOre =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<MiningDailyEvent> _events = new();

    public MiningDailyStore()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? AppDomain.CurrentDomain.BaseDirectory;
        _directory = Path.Combine(exeDir, "MiningData");
    }

    public string CurrentDayKey
    {
        get
        {
            lock (_gate)
            {
                var key = GetDayKey(DateTime.UtcNow);
                EnsureDayLocked(key);
                return key;
            }
        }
    }

    public static string GetDayKey(DateTime timestampUtc) =>
        timestampUtc.ToLocalTime().AddHours(-DayCutoffHour)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateTime GetCurrentDayStartUtc()
    {
        var localNow = DateTime.Now;
        var miningDate = localNow.Hour < DayCutoffHour
            ? localNow.Date.AddDays(-1)
            : localNow.Date;

        var localStart = DateTime.SpecifyKind(
            miningDate.AddHours(DayCutoffHour),
            DateTimeKind.Local);

        return localStart.ToUniversalTime();
    }

    /// <summary>
    /// Replace the current day's partial custom ledger with the source-of-truth
    /// events parsed from EVE's raw gamelogs.
    /// </summary>
    public void ReplaceCurrentDay(IEnumerable<MiningEvent> events)
    {
        string day = GetDayKey(DateTime.UtcNow);

        var accepted = events
            .Where(e =>
                e.MineType == "ore" &&
                e.Amount > 0 &&
                !string.IsNullOrWhiteSpace(e.CharacterName) &&
                !string.IsNullOrWhiteSpace(e.OreType) &&
                GetDayKey(e.Timestamp) == day)
            .OrderBy(e => e.Timestamp)
            .ToList();

        lock (_gate)
        {
            _loadedDay = day;
            _byCharacter.Clear();
            _lastOre.Clear();
            _events.Clear();

            foreach (var e in accepted)
            {
                ApplyLocked(new MiningDailyEvent
                {
                    TimestampUtc = e.Timestamp,
                    Character = e.CharacterName.Trim(),
                    Ore = e.OreType.Trim(),
                    Units = e.Amount,
                    IsCritical = e.IsCritical
                });
            }

            try
            {
                Directory.CreateDirectory(_directory);
                string path = Path.Combine(_directory, $"{day}.jsonl");
                string temp = path + ".rebuild";

                using (var writer = new StreamWriter(temp, append: false))
                {
                    foreach (var e in accepted)
                    {
                        writer.WriteLine(JsonSerializer.Serialize(new MiningDailyEvent
                        {
                            TimestampUtc = e.Timestamp,
                            Character = e.CharacterName.Trim(),
                            Ore = e.OreType.Trim(),
                            Units = e.Amount,
                            IsCritical = e.IsCritical
                        }));
                    }
                }

                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                // The raw EVE logs can rebuild this again next launch.
            }
        }
    }

    public void Record(DateTime timestampUtc, string character, string ore, int units, bool isCritical)
    {
        if (string.IsNullOrWhiteSpace(character) || string.IsNullOrWhiteSpace(ore) || units <= 0)
            return;

        var ev = new MiningDailyEvent
        {
            TimestampUtc = timestampUtc,
            Character = character.Trim(),
            Ore = ore.Trim(),
            Units = units,
            IsCritical = isCritical
        };

        lock (_gate)
        {
            string day = GetDayKey(timestampUtc);
            EnsureDayLocked(day);
            ApplyLocked(ev);

            try
            {
                Directory.CreateDirectory(_directory);
                string path = Path.Combine(_directory, $"{day}.jsonl");
                File.AppendAllText(path, JsonSerializer.Serialize(ev) + Environment.NewLine);
            }
            catch
            {
                // History persistence must never interrupt live MultiPreview.
            }
        }
    }

    public Dictionary<string, double> GetFleetUnitsByOre()
    {
        lock (_gate)
        {
            EnsureDayLocked(GetDayKey(DateTime.UtcNow));
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var ores in _byCharacter.Values)
            {
                foreach (var (ore, totals) in ores)
                {
                    result.TryGetValue(ore, out double old);
                    result[ore] = old + totals.Units;
                }
            }
            return result;
        }
    }

    public Dictionary<string, double> GetCharacterUnitsByOre(string character)
    {
        lock (_gate)
        {
            EnsureDayLocked(GetDayKey(DateTime.UtcNow));
            if (!_byCharacter.TryGetValue(character, out var ores))
                return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            return ores.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Units,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<MiningAggregateRow> GetAggregateRows()
    {
        lock (_gate)
        {
            string day = GetDayKey(DateTime.UtcNow);
            EnsureDayLocked(day);

            var result = new List<MiningAggregateRow>();
            foreach (var (character, ores) in _byCharacter)
            {
                foreach (var (ore, totals) in ores)
                {
                    result.Add(new MiningAggregateRow
                    {
                        DayKey = day,
                        Character = character,
                        Ore = ore,
                        Units = totals.Units,
                        NormalUnits = totals.NormalUnits,
                        CriticalUnits = totals.CriticalUnits,
                        Crits = totals.Crits,
                        Cycles = totals.Cycles
                    });
                }
            }

            return result;
        }
    }

    public IReadOnlyList<string> GetCharacters()
    {
        lock (_gate)
        {
            EnsureDayLocked(GetDayKey(DateTime.UtcNow));
            return _byCharacter.Keys
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public string GetLastOre(string character)
    {
        lock (_gate)
        {
            EnsureDayLocked(GetDayKey(DateTime.UtcNow));
            return _lastOre.TryGetValue(character, out var ore) ? ore : "";
        }
    }

    public MiningDailyCritSummary GetCritSummary(string? character = null)
    {
        lock (_gate)
        {
            EnsureDayLocked(GetDayKey(DateTime.UtcNow));

            int crits = 0;
            int cycles = 0;

            if (!string.IsNullOrWhiteSpace(character))
            {
                if (_byCharacter.TryGetValue(character, out var one))
                {
                    foreach (var totals in one.Values)
                    {
                        crits += totals.Crits;
                        cycles += totals.Cycles;
                    }
                }

                return new MiningDailyCritSummary(crits, cycles);
            }

            foreach (var ores in _byCharacter.Values)
            {
                foreach (var totals in ores.Values)
                {
                    crits += totals.Crits;
                    cycles += totals.Cycles;
                }
            }

            return new MiningDailyCritSummary(crits, cycles);
        }
    }

    private void EnsureDayLocked(string day)
    {
        if (_loadedDay == day) return;

        _loadedDay = day;
        _byCharacter.Clear();
        _lastOre.Clear();
        _events.Clear();

        string path = Path.Combine(_directory, $"{day}.jsonl");
        if (!File.Exists(path)) return;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var ev = JsonSerializer.Deserialize<MiningDailyEvent>(line);
                    if (ev == null || GetDayKey(ev.TimestampUtc) != day) continue;
                    ApplyLocked(ev);
                }
                catch { }
            }
        }
        catch { }
    }

    private void ApplyLocked(MiningDailyEvent ev)
    {
        if (!_byCharacter.TryGetValue(ev.Character, out var ores))
        {
            ores = new Dictionary<string, DailyOreTotals>(StringComparer.OrdinalIgnoreCase);
            _byCharacter[ev.Character] = ores;
        }

        if (!ores.TryGetValue(ev.Ore, out var totals))
        {
            totals = new DailyOreTotals();
            ores[ev.Ore] = totals;
        }

        totals.Units += ev.Units;
        totals.Cycles++;
        if (ev.IsCritical)
        {
            totals.Crits++;
            totals.CriticalUnits += ev.Units;
        }
        else
        {
            totals.NormalUnits += ev.Units;
        }
        _lastOre[ev.Character] = ev.Ore;
        _events.Add(ev);
    }

    public MiningActivitySummary GetActivitySummary(string character)
    {
        lock (_gate)
        {
            EnsureDayLocked(GetDayKey(DateTime.UtcNow));

            var times = _events
                .Where(e => string.Equals(
                    e.Character,
                    character,
                    StringComparison.OrdinalIgnoreCase))
                .Select(e => e.TimestampUtc)
                .OrderBy(t => t)
                .ToList();

            if (times.Count == 0)
                return new MiningActivitySummary();

            if (times.Count == 1)
            {
                return new MiningActivitySummary
                {
                    Pulls = 1,
                    FirstPullUtc = times[0],
                    LastPullUtc = times[0],
                    ContinuityPercent = 100
                };
            }

            const double breakThresholdSeconds = 180;
            double activeSeconds = 0;
            double breakSeconds = 0;
            int breaks = 0;

            for (int i = 1; i < times.Count; i++)
            {
                double gap = Math.Max(
                    0,
                    (times[i] - times[i - 1]).TotalSeconds);

                if (gap > breakThresholdSeconds)
                {
                    breaks++;
                    breakSeconds += gap;
                }
                else
                {
                    activeSeconds += gap;
                }
            }

            double spanSeconds = Math.Max(
                1,
                (times[^1] - times[0]).TotalSeconds);

            return new MiningActivitySummary
            {
                Pulls = times.Count,
                FirstPullUtc = times[0],
                LastPullUtc = times[^1],
                ActiveSeconds = activeSeconds,
                BreakSeconds = breakSeconds,
                Breaks = breaks,
                ContinuityPercent = Math.Clamp(
                    activeSeconds * 100.0 / spanSeconds,
                    0,
                    100)
            };
        }
    }

    private sealed class DailyOreTotals
    {
        public double Units { get; set; }
        public double NormalUnits { get; set; }
        public double CriticalUnits { get; set; }
        public int Crits { get; set; }
        public int Cycles { get; set; }
    }

    private sealed class MiningDailyEvent
    {
        public DateTime TimestampUtc { get; set; }
        public string Character { get; set; } = "";
        public string Ore { get; set; } = "";
        public int Units { get; set; }
        public bool IsCritical { get; set; }
    }
}

public sealed class MiningAggregateRow
{
    public string DayKey { get; set; } = "";
    public string Character { get; set; } = "";
    public string Ore { get; set; } = "";
    public double Units { get; set; }
    public double NormalUnits { get; set; }
    public double CriticalUnits { get; set; }
    public int Crits { get; set; }
    public int Cycles { get; set; }
}

public readonly record struct MiningDailyCritSummary(int Crits, int Cycles)
{
    public double Percent => Cycles > 0 ? Crits * 100.0 / Cycles : 0;
    public override string ToString() => $"{Crits}/{Cycles} ({Percent:F1}%)";
}

public sealed class MiningActivitySummary
{
    public int Pulls { get; init; }
    public DateTime? FirstPullUtc { get; init; }
    public DateTime? LastPullUtc { get; init; }
    public double ActiveSeconds { get; init; }
    public double BreakSeconds { get; init; }
    public int Breaks { get; init; }
    public double ContinuityPercent { get; init; }
}
