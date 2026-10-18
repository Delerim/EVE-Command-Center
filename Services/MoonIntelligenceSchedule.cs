using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace EveCommandCenter.Services;

public sealed class MoonIntelligenceScheduleOptions
{
    public bool Daily { get; set; }
    public bool Weekly { get; set; }
    public bool Monthly { get; set; }
    // Explicitly labelled UTC; default options are all OFF.
    public int SendHourUtc { get; set; } = 9;
}

/// <summary>Local-only, opt-in scheduling. No background task runs when the app is closed.</summary>
public sealed class MoonIntelligenceSchedule
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAttempts = new();
    private MoonIntelligenceScheduleOptions _options;
    public static MoonIntelligenceSchedule Current { get; } = new();

    public MoonIntelligenceSchedule(string? directory = null)
    {
        string folder = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center");
        _path = Path.Combine(folder, "moon-report-schedule.json");
        try { _options = JsonSerializer.Deserialize<MoonIntelligenceScheduleOptions>(File.ReadAllText(_path)) ?? new(); }
        catch { _options = new(); }
        _options.SendHourUtc = Math.Clamp(_options.SendHourUtc, 0, 23);
    }

    public MoonIntelligenceScheduleOptions Options()
    {
        lock (_lock) return new MoonIntelligenceScheduleOptions
        { Daily = _options.Daily, Weekly = _options.Weekly, Monthly = _options.Monthly, SendHourUtc = _options.SendHourUtc };
    }
    public void Save(bool daily, bool weekly, bool monthly, int hourUtc)
    {
        if (hourUtc < 0 || hourUtc > 23) throw new ArgumentOutOfRangeException(nameof(hourUtc));
        lock (_lock)
        {
            _options = new MoonIntelligenceScheduleOptions
            { Daily = daily, Weekly = weekly, Monthly = monthly, SendHourUtc = hourUtc };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_options));
            File.Move(temp, _path, true);
        }
    }

    public void Check(MoonReportService moon, DateTimeOffset now)
    {
        MoonIntelligenceScheduleOptions options = Options();
        if ((!options.Daily && !options.Weekly && !options.Monthly) || now.UtcDateTime.Hour < options.SendHourUtc)
            return;
        var discord = DiscordWebhookService.Current;
        var candidates = new List<(string Type, string Period, string Key)>();
        if (options.Daily) candidates.Add((DiscordWebhookService.MoonReportDaily, "Yesterday", "daily:" + now.UtcDateTime.ToString("yyyyMMdd")));
        if (options.Weekly && now.UtcDateTime.DayOfWeek == DayOfWeek.Monday)
            candidates.Add((DiscordWebhookService.MoonReportWeekly, "Last week", "weekly:" + now.UtcDateTime.ToString("yyyyMMdd")));
        if (options.Monthly && now.UtcDateTime.Day == 1)
            candidates.Add((DiscordWebhookService.MoonReportMonthly, "Last month", "monthly:" + now.UtcDateTime.ToString("yyyyMMdd")));
        if (candidates.Count == 0 || moon.SelectedCharacterId <= 0) return;
        // Only do the heavier snapshot/report aggregation for actually due subscriptions.
        candidates = candidates.FindAll(c => discord.HasRoutes(c.Type) &&
            (!_lastAttempts.TryGetValue(moon.SelectedCharacterId + ":" + c.Key, out var last) ||
             now - last >= TimeSpan.FromMinutes(30)));
        if (candidates.Count == 0) return;
        // Never send a stale ESI view as if it were a current operational report.
        var snapshot = moon.GetSnapshot();
        if (snapshot.LastRefreshUtc is not { } updated || now - updated > TimeSpan.FromMinutes(75)) return;
        foreach (var (type, period, key) in candidates)
        {
            var fullKey = moon.SelectedCharacterId + ":" + key;
            lock (_lock)
            {
                _lastAttempts[fullKey] = now;
                foreach (string old in new List<string>(_lastAttempts.Keys))
                    if (now - _lastAttempts[old] > TimeSpan.FromDays(40)) _lastAttempts.Remove(old);
            }
            var report = moon.GetIntelligenceReport(period, now, snapshot);
            discord.Publish(type, "Moon operations | " + report.Period, report.DiscordBody(), fullKey);
        }
    }
}
