using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace EveMultiPreview.Services;

/// <summary>
/// Compact one-year mining-history index.
///
/// Raw EVE gamelogs are the source of truth. We scan them, then retain only
/// day + character + ore aggregates. No individual historical mining events
/// are kept, so a full year remains tiny compared with an event database.
///
/// The current mining day is rebuilt synchronously at startup by
/// ScanCurrentDay(). Completed older days are indexed in the background.
/// </summary>
public sealed class MiningHistoryService : IDisposable
{
    public const int MaxHistoryDays = 365;

    private readonly object _gate = new();
    private readonly string _archivePath;
    private MiningHistoryArchive _archive;
    private MiningHistoryBuildStatus _status = new();
    private CancellationTokenSource? _cts;
    private Task? _buildTask;

    private static readonly Regex TimestampRegex = new(
        @"^\[\s*(?<ts>\d{4}\.\d{2}\.\d{2}\s+\d{2}:\d{2}:\d{2})\s*\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PrefixRegex = new(
        @"^\[\s*\d{4}\.\d{2}\.\d{2}\s+\d{2}:\d{2}:\d{2}\s*\]\s*\(mining\)\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public MiningHistoryService()
    {
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath)
                     ?? AppDomain.CurrentDomain.BaseDirectory;
        var dataDir = Path.Combine(exeDir, "MiningData");
        _archivePath = Path.Combine(dataDir, "history-v1.json");
        _archive = LoadArchive();
    }

    public MiningHistoryBuildStatus GetStatus()
    {
        lock (_gate)
            return _status with { };
    }

    public void SetUnavailableStatus(string message)
    {
        lock (_gate)
        {
            _status = new MiningHistoryBuildStatus
            {
                IsRunning = false,
                Message = message
            };
        }
    }

    public IReadOnlyList<string> GetKnownOres()
    {
        lock (_gate)
        {
            return _archive.Rows
                .Select(r => r.Ore)
                .Where(o => !string.IsNullOrWhiteSpace(o))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(o => o, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// Parse the complete CURRENT mining day directly from EVE gamelogs.
    /// This runs before live monitoring so TODAY is correct immediately.
    /// </summary>
    public IReadOnlyList<MiningEvent> ScanCurrentDay(string gameLogPath)
    {
        if (string.IsNullOrWhiteSpace(gameLogPath) || !Directory.Exists(gameLogPath))
            return Array.Empty<MiningEvent>();

        DateTime fromUtc = MiningDailyStore.GetCurrentDayStartUtc();
        DateTime toUtc = DateTime.UtcNow.AddSeconds(5);

        try
        {
            return ReadEvents(gameLogPath, fromUtc, toUtc, progress: null);
        }
        catch
        {
            return Array.Empty<MiningEvent>();
        }
    }

    /// <summary>
    /// Start/resume compact indexing of completed mining days in the background.
    /// First run scans up to one year. Later runs rescan only the last few days so
    /// sessions crossing boundaries are corrected without rebuilding everything.
    /// </summary>
    public void StartBackgroundBuild(string gameLogPath)
    {
        if (string.IsNullOrWhiteSpace(gameLogPath) || !Directory.Exists(gameLogPath))
        {
            SetUnavailableStatus("History unavailable - EVE Gamelogs folder not found");
            return;
        }

        lock (_gate)
        {
            if (_buildTask is { IsCompleted: false })
                return;

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _status = new MiningHistoryBuildStatus
            {
                IsRunning = true,
                Message = "Starting history index..."
            };

            _buildTask = Task.Run(async () =>
            {
                try
                {
                    await BuildBackgroundAsync(gameLogPath, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_gate)
                    {
                        _status = new MiningHistoryBuildStatus
                        {
                            IsRunning = false,
                            Message = "History indexing cancelled"
                        };
                    }
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        _status = new MiningHistoryBuildStatus
                        {
                            IsRunning = false,
                            Message = $"History indexing failed - {ex.Message}"
                        };
                    }
                }
            }, token);
        }
    }

    public IReadOnlyList<MiningAggregateRow> GetRange(DateTime fromMiningDayLocal, DateTime toMiningDayLocal)
    {
        string fromKey = fromMiningDayLocal.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string toKey = toMiningDayLocal.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            return _archive.Rows
                .Where(r =>
                    string.CompareOrdinal(r.DayKey, fromKey) >= 0 &&
                    string.CompareOrdinal(r.DayKey, toKey) <= 0)
                .Select(CloneRow)
                .OrderBy(r => r.DayKey, StringComparer.Ordinal)
                .ThenBy(r => r.Character, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Ore, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private async Task BuildBackgroundAsync(string gameLogPath, CancellationToken token)
    {
        DateTime currentDayStartUtc = MiningDailyStore.GetCurrentDayStartUtc();
        DateTime absoluteFromUtc = currentDayStartUtc.AddDays(-MaxHistoryDays);

        DateTime scanFromUtc;
        lock (_gate)
        {
            // On subsequent launches only revisit the tail. This catches a gamelog
            // session that crossed a mining-day boundary without rescanning a year.
            scanFromUtc = _archive.LastCompletedScanUtc > DateTime.MinValue
                ? _archive.LastCompletedScanUtc.AddDays(-3)
                : absoluteFromUtc;

            if (scanFromUtc < absoluteFromUtc)
                scanFromUtc = absoluteFromUtc;

            _status = new MiningHistoryBuildStatus
            {
                IsRunning = true,
                Message = "Preparing history index..."
            };
        }

        string fromKey = MiningDailyStore.GetDayKey(scanFromUtc);
        string currentKey = MiningDailyStore.GetDayKey(DateTime.UtcNow);

        var files = CandidateFiles(gameLogPath, scanFromUtc)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var events = new List<MiningEvent>();
        int processed = 0;

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                ReadEventsFromFile(file, scanFromUtc, currentDayStartUtc, events);
            }
            catch
            {
                // One damaged/locked old log should not abort the archive.
            }

            processed++;

            lock (_gate)
            {
                _status = new MiningHistoryBuildStatus
                {
                    IsRunning = true,
                    FilesProcessed = processed,
                    FilesTotal = files.Count,
                    ProgressPercent = files.Count == 0 ? 100 : processed * 100.0 / files.Count,
                    Message = $"Indexing mining history... {processed}/{files.Count} log files"
                };
            }

            // Yield regularly so this remains background work on large archives.
            if (processed % 4 == 0)
                await Task.Delay(1, token).ConfigureAwait(false);
        }

        var rebuilt = Aggregate(events)
            // Current day remains owned by MiningDailyStore/live tracking.
            .Where(r => !string.Equals(r.DayKey, currentKey, StringComparison.Ordinal))
            .ToList();

        lock (_gate)
        {
            // Replace every archived day touched by this scan, then append the newly
            // rebuilt compact rows.
            _archive.Rows.RemoveAll(r =>
                string.CompareOrdinal(r.DayKey, fromKey) >= 0 &&
                !string.Equals(r.DayKey, currentKey, StringComparison.Ordinal));

            _archive.Rows.AddRange(rebuilt);

            string oldestKey = MiningDailyStore.GetDayKey(absoluteFromUtc);
            _archive.Rows.RemoveAll(r => string.CompareOrdinal(r.DayKey, oldestKey) < 0);

            _archive.Rows = _archive.Rows
                .GroupBy(
                    r => $"{r.DayKey}\u001f{r.Character}\u001f{r.Ore}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Aggregate(new MiningAggregateRow
                {
                    DayKey = g.First().DayKey,
                    Character = g.First().Character,
                    Ore = g.First().Ore
                }, (acc, row) =>
                {
                    acc.Units += row.Units;
                    acc.NormalUnits += row.NormalUnits;
                    acc.CriticalUnits += row.CriticalUnits;
                    acc.Crits += row.Crits;
                    acc.Cycles += row.Cycles;
                    return acc;
                }))
                .OrderBy(r => r.DayKey, StringComparer.Ordinal)
                .ThenBy(r => r.Character, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Ore, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _archive.LastCompletedScanUtc = currentDayStartUtc;
            SaveArchiveLocked();

            int days = _archive.Rows
                .Select(r => r.DayKey)
                .Distinct(StringComparer.Ordinal)
                .Count();

            _status = new MiningHistoryBuildStatus
            {
                IsRunning = false,
                FilesProcessed = files.Count,
                FilesTotal = files.Count,
                ProgressPercent = 100,
                DaysIndexed = days,
                Message = $"History ready - {days} mining day(s) indexed"
            };
        }

        CleanupOldLiveJsonl(currentKey);
    }

    private static List<string> CandidateFiles(string gameLogPath, DateTime fromUtc)
    {
        try
        {
            // A file can start before the range and continue into it, so filter by
            // LAST write time rather than creation time.
            return Directory.GetFiles(gameLogPath, "*.txt")
                .Where(f =>
                {
                    try { return File.GetLastWriteTimeUtc(f) >= fromUtc; }
                    catch { return false; }
                })
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static IReadOnlyList<MiningEvent> ReadEvents(
        string gameLogPath,
        DateTime fromUtc,
        DateTime toUtc,
        Action<int, int>? progress)
    {
        var files = CandidateFiles(gameLogPath, fromUtc)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var events = new List<MiningEvent>();
        for (int i = 0; i < files.Count; i++)
        {
            try { ReadEventsFromFile(files[i], fromUtc, toUtc, events); }
            catch { }
            progress?.Invoke(i + 1, files.Count);
        }

        return events
            .OrderBy(e => e.Timestamp)
            .ToList();
    }

    private static void ReadEventsFromFile(
        string file,
        DateTime fromUtc,
        DateTime toUtc,
        List<MiningEvent> output)
    {
        string? character = null;
        int headerLines = 0;

        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (true)
        {
            string? raw = reader.ReadLine();
            if (raw == null) break;

            if (character == null && headerLines < 40)
            {
                headerLines++;
                character = TryParseHeaderCharacter(raw.TrimStart()) ?? character;
            }

            if (string.IsNullOrWhiteSpace(character))
                continue;

            if (!raw.Contains("(mining)", StringComparison.Ordinal))
                continue;

            if (TryParseMiningEvent(raw, character, out var ev) &&
                ev.Timestamp >= fromUtc &&
                ev.Timestamp < toUtc &&
                ev.MineType == "ore")
            {
                output.Add(ev);
            }
        }
    }

    private static string? TryParseHeaderCharacter(string trimmed)
    {
        foreach (var key in AlertPatterns.Get("log_header_keys"))
        {
            if (!trimmed.StartsWith(key, StringComparison.Ordinal))
                continue;

            var rest = trimmed.Substring(key.Length).TrimStart();
            if (rest.Length == 0 || (rest[0] != ':' && rest[0] != '\uFF1A'))
                continue;

            string name = rest.Substring(1).Trim();
            if (!string.IsNullOrEmpty(name))
                return name;
        }

        // English safety fallback.
        const string english = "Listener:";
        if (trimmed.StartsWith(english, StringComparison.Ordinal))
        {
            string name = trimmed.Substring(english.Length).Trim();
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }

        return null;
    }

    private static bool TryParseMiningEvent(string rawLine, string character, out MiningEvent miningEvent)
    {
        miningEvent = new MiningEvent();

        if (AlertPatterns.Matches(rawLine, "mining_residue"))
            return false;

        var ts = TimestampRegex.Match(rawLine);
        if (!ts.Success ||
            !DateTime.TryParseExact(
                ts.Groups["ts"].Value,
                "yyyy.MM.dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestampUtc))
            return false;

        // Non-English EVE clients often wrap names with <localized hint="English">.
        // Prefer that stable English hint so market lookup still works.
        string line = Regex.Replace(
            rawLine,
            @"<localized\s+hint=""(?<hint>[^""]+)"">[^<]*",
            "${hint}",
            RegexOptions.CultureInvariant);

        string clean = Regex.Replace(line, @"<[^>]+>", "");
        clean = PrefixRegex.Replace(clean, "");

        Match? yieldMatch = null;
        foreach (var rx in AlertPatterns.Regexes("mining_yield_regex"))
        {
            var m = rx.Match(clean);
            if (m.Success && m.Groups["amount"].Success && m.Groups["ore"].Success)
            {
                yieldMatch = m;
                break;
            }
        }

        if (yieldMatch == null)
            return false;

        string digits = Regex.Replace(yieldMatch.Groups["amount"].Value, @"[^\d]", "");
        if (digits.Length == 0 || !int.TryParse(digits, out int amount) || amount <= 0)
            return false;

        string oreType = yieldMatch.Groups["ore"].Value.Trim();
        if (string.IsNullOrWhiteSpace(oreType))
            return false;

        string mineType = "ore";
        if (oreType.Contains("Fullerite", StringComparison.OrdinalIgnoreCase) ||
            oreType.Contains("Cytoserocin", StringComparison.OrdinalIgnoreCase) ||
            oreType.Contains("Mykoserocin", StringComparison.OrdinalIgnoreCase))
            mineType = "gas";
        else if (
            oreType.Contains("Ice", StringComparison.OrdinalIgnoreCase) ||
            oreType.Contains("Icicle", StringComparison.OrdinalIgnoreCase) ||
            oreType.Contains("Glacial", StringComparison.OrdinalIgnoreCase) ||
            oreType.Contains("Glitter", StringComparison.OrdinalIgnoreCase) ||
            oreType.Contains("Gelidus", StringComparison.OrdinalIgnoreCase) ||
            oreType.Contains("Glare Crust", StringComparison.OrdinalIgnoreCase) ||
            oreType.Contains("Krystallos", StringComparison.OrdinalIgnoreCase) ||
            oreType.Contains("Glaze", StringComparison.OrdinalIgnoreCase))
            mineType = "ice";

        miningEvent = new MiningEvent
        {
            Timestamp = timestampUtc,
            Amount = amount,
            OreType = oreType,
            MineType = mineType,
            IsCritical = clean.Contains("critical", StringComparison.OrdinalIgnoreCase),
            CharacterName = character
        };
        return true;
    }

    private static List<MiningAggregateRow> Aggregate(IEnumerable<MiningEvent> events)
    {
        return events
            .Where(e =>
                e.MineType == "ore" &&
                e.Amount > 0 &&
                !string.IsNullOrWhiteSpace(e.CharacterName) &&
                !string.IsNullOrWhiteSpace(e.OreType))
            .GroupBy(e => new
            {
                Day = MiningDailyStore.GetDayKey(e.Timestamp),
                Character = e.CharacterName.Trim(),
                Ore = e.OreType.Trim()
            })
            .Select(g => new MiningAggregateRow
            {
                DayKey = g.Key.Day,
                Character = g.Key.Character,
                Ore = g.Key.Ore,
                Units = g.Sum(e => (double)e.Amount),
                NormalUnits = g.Where(e => !e.IsCritical).Sum(e => (double)e.Amount),
                CriticalUnits = g.Where(e => e.IsCritical).Sum(e => (double)e.Amount),
                Cycles = g.Count(),
                Crits = g.Count(e => e.IsCritical)
            })
            .ToList();
    }

    private MiningHistoryArchive LoadArchive()
    {
        try
        {
            if (!File.Exists(_archivePath))
                return new MiningHistoryArchive();

            var loaded = JsonSerializer.Deserialize<MiningHistoryArchive>(
                             File.ReadAllText(_archivePath))
                         ?? new MiningHistoryArchive();

            // Archive v1 did not store normal-vs-critical unit totals. Rebuild
            // once from the raw EVE logs so Profit analytics are exact.
            if (loaded.Version < 2)
                return new MiningHistoryArchive { Version = 2 };

            return loaded;
        }
        catch
        {
            return new MiningHistoryArchive();
        }
    }

    private void SaveArchiveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_archivePath)!);
            string tmp = _archivePath + ".tmp";
            File.WriteAllText(
                tmp,
                JsonSerializer.Serialize(
                    _archive,
                    new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, _archivePath, overwrite: true);
        }
        catch
        {
            // History is reconstructable from EVE logs. Never crash MultiPreview.
        }
    }

    private void CleanupOldLiveJsonl(string currentKey)
    {
        try
        {
            string? dir = Path.GetDirectoryName(_archivePath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return;

            foreach (var file in Directory.GetFiles(dir, "*.jsonl"))
            {
                string key = Path.GetFileNameWithoutExtension(file);
                if (!string.Equals(key, currentKey, StringComparison.Ordinal))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    private static MiningAggregateRow CloneRow(MiningAggregateRow r) => new()
    {
        DayKey = r.DayKey,
        Character = r.Character,
        Ore = r.Ore,
        Units = r.Units,
        NormalUnits = r.NormalUnits,
        CriticalUnits = r.CriticalUnits,
        Crits = r.Crits,
        Cycles = r.Cycles
    };

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
    }

    private sealed class MiningHistoryArchive
    {
        public int Version { get; set; } = 2;
        public DateTime LastCompletedScanUtc { get; set; }
        public List<MiningAggregateRow> Rows { get; set; } = new();
    }
}

public record MiningHistoryBuildStatus
{
    public bool IsRunning { get; init; }
    public int FilesProcessed { get; init; }
    public int FilesTotal { get; init; }
    public double ProgressPercent { get; init; }
    public int DaysIndexed { get; init; }
    public string Message { get; init; } = "Waiting for history index...";
}
