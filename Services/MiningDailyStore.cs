using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace EveMultiPreview.Services;

/// <summary>
/// Append-only persistent mining-day history. A "mining day" rolls at 04:00 local
/// time, so a session that begins at 06:00 and runs until 03:00 next morning stays
/// together. EVE logs are only consumed live by LogMonitor, so app restarts do not
/// replay old pulls into this file.
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
        timestampUtc.ToLocalTime().AddHours(-DayCutoffHour).ToString("yyyy-MM-dd");

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

            return ores.ToDictionary(kv => kv.Key, kv => kv.Value.Units,
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<string> GetCharacters()
    {
        lock (_gate)
        {
            EnsureDayLocked(GetDayKey(DateTime.UtcNow));
            return _byCharacter.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
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
                catch
                {
                    // Ignore a single damaged line and retain the rest of the day.
                }
            }
        }
        catch
        {
            // A locked/damaged history file should not stop live tracking.
        }
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
        if (ev.IsCritical) totals.Crits++;
        _lastOre[ev.Character] = ev.Ore;
    }

    private sealed class DailyOreTotals
    {
        public double Units { get; set; }
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

public readonly record struct MiningDailyCritSummary(int Crits, int Cycles)
{
    public double Percent => Cycles > 0 ? Crits * 100.0 / Cycles : 0;
    public override string ToString() => $"{Crits}/{Cycles} ({Percent:F1}%)";
}
