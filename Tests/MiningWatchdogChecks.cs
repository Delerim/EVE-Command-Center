using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckMiningWatchdog()
    {
        var start = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
        var now = start.AddSeconds(89);
        var names = Enumerable.Range(1, 5).Select(i => "Miner " + i).ToArray();
        var pulls = names.ToDictionary(n => n, _ => start);
        CharacterStatSnapshot Snapshot(string name) => new() { MiningCycleCount = 20, LastMiningPullUtc = pulls[name] };
        using var watcher = new MiningIdleWatchdogService(new StatTrackerService(), Snapshot, () => now);
        watcher.Preferences.IdleWatchdogEnabled = true;
        watcher.Preferences.IdleSeconds = 90;
        watcher.Preferences.YieldDropEnabled = false;
        watcher.Preferences.AlarmMutedCharacters.Clear();
        var alerts = new List<string>();
        watcher.IdleDetected += alerts.Add;
        watcher.CheckCharacters(names);
        Check(alerts.Count == 0, "Each miner retains its own no-pull grace period");
        now = start.AddSeconds(91); pulls[names[0]] = start.AddSeconds(90);
        watcher.CheckCharacters(names);
        Check(alerts.Count == 4 && !alerts.Contains(names[0]), "Four stopped miners alert independently while the fifth continues pulling");
        Check(watcher.GetState(names[1]).Kind == MiningIdleKind.Idle, "Stopped toon remains flagged between polling ticks");
        now = start.AddSeconds(181); watcher.CheckCharacters(names); watcher.CheckCharacters(names);
        Check(alerts.Count == 5 && alerts.Distinct().Count() == 5, "All five toons alert once without one toon suppressing another");
        pulls[names[1]] = now; watcher.CheckCharacters(names);
        now = now.AddSeconds(91); watcher.CheckCharacters(names);
        Check(alerts.Count == 6 && alerts.Count(n => n == names[1]) == 2, "A fresh pull rearms only that miner for the next stoppage");
        using var failing = new MiningIdleWatchdogService(new StatTrackerService(), Snapshot, () => now);
        failing.Preferences.IdleWatchdogEnabled = true; failing.Preferences.IdleSeconds = 90;
        failing.Preferences.YieldDropEnabled = false; failing.Preferences.AlarmMutedCharacters.Clear();
        var delivered = new List<string>();
        failing.IdleDetected += name => { if(name == names[0]) throw new InvalidOperationException("Simulated notification failure"); delivered.Add(name); };
        failing.CheckCharacters(names);
        Check(delivered.Count == 4, "One failed notification does not prevent other miners being checked");
        using var muted = new MiningIdleWatchdogService(new StatTrackerService(), Snapshot, () => now);
        muted.Preferences.IdleWatchdogEnabled = true; muted.Preferences.IdleSeconds = 90;
        muted.Preferences.YieldDropEnabled = false; muted.Preferences.AlarmMutedCharacters.Clear(); muted.Preferences.AlarmMutedCharacters.Add(names[0]);
        var mutedAlerts = new List<string>(); muted.IdleDetected += mutedAlerts.Add; muted.CheckCharacters(names);
        Check(mutedAlerts.Count == 4 && !mutedAlerts.Contains(names[0]), "Per-toon alarm mute remains respected");
    }
}
