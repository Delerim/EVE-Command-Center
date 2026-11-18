using EveCommandCenter.Services;
using System.Globalization;
using System.Text.RegularExpressions;

internal static partial class Program
{
    private static void CheckMiningStreamCadences()
    {
        var start = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        // Early short pulls must not lock later full cycles to a harmonic.
        var pulls = new List<MiningStatusRings.Pull>();
        foreach (int second in new[] {0, 7, 14}) pulls.Add(new(start.AddSeconds(second), 64, "Ore A", false));
        pulls.AddRange(Enumerable.Range(0, 35).Select(n => new MiningStatusRings.Pull(start.AddSeconds(34 + n * 20), 64 + n % 2, "Ore A", false)));
        pulls.AddRange(Enumerable.Range(0, 39).Select(n => new MiningStatusRings.Pull(start.AddSeconds(2 + n * 18), 64, "Ore B", false)));
        bool stable = true;
        for (int second = 150; second < 700; second++)
        {
            var rows = MiningStatusRings.Build(2, false, false, pulls, start.AddSeconds(second));
            stable &= rows.All(r => r.Accent == "#65D5B6") && rows.Single(r => r.OreText == "Ore A").CycleText == "20s";
        }
        Check(stable, "Independent drifting resources preserve demonstrated full cycles at every replayed second");
        // A delayed full pull must update its uniquely identified lane immediately.
        var regular = Enumerable.Range(0, 10).SelectMany(n => new[] {
            new MiningStatusRings.Pull(start.AddSeconds(n * 18), 60, "Ore A", false),
            new MiningStatusRings.Pull(start.AddSeconds(n * 18 + 8), 60, "Ore B", false) }).ToList();
        regular.Add(new(start.AddSeconds(188), 60, "Ore A", false));
        regular.Add(new(start.AddSeconds(188), 60, "Ore B", false));
        Check(MiningStatusRings.Build(2, false, false, regular, start.AddSeconds(192)).All(r => !r.IsStopped),
            "A real delayed pull cannot leave a uniquely identified resource stopped");
        regular.Add(new(start.AddSeconds(206), 4, "Ore A", false));
        regular.Add(new(start.AddSeconds(206), 60, "Ore B", false));
        var partial = MiningStatusRings.Build(2, false, false, regular, start.AddSeconds(210));
        Check(partial.All(r => !r.IsStopped) && partial.Single(r => r.OreText == "Ore A").CycleText != "4s",
            "Partial mining pull updates activity without relearning its small yield as a fast cycle");
        regular.AddRange(Enumerable.Range(0, 6).SelectMany(n => new[] {
            new MiningStatusRings.Pull(start.AddSeconds(224 + n * 18), 60, "Ore A", false),
            new MiningStatusRings.Pull(start.AddSeconds(224 + n * 18), 60, "Ore B", false) }));
        Check(MiningStatusRings.Build(2, false, false, regular, start.AddSeconds(350)).All(r => r.IsStopped),
            "Observed partial activity does not indefinitely hide a genuine stop");
    }

    // Local diagnostic: reads logs only, never links characters or persists telemetry.
    private static void ReplayMiningStreams(string folder, string pattern)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(folder, pattern))
        {
            var pulls = new List<MiningStatusRings.Pull>();
            foreach (var line in System.IO.File.ReadLines(file))
            {
                if (!line.Contains("(mining)")) continue;
                var clean = Regex.Replace(line, "<[^>]+>", "");
                var match = Regex.Match(clean, @"^\[\s*(?<at>[\d.]+ [\d:]+)\s*\].*You mined (?<units>[\d,]+) units of (?<ore>.+)$");
                if (!match.Success) continue;
                pulls.Add(new(DateTime.ParseExact(match.Groups["at"].Value, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
                    double.Parse(match.Groups["units"].Value, CultureInfo.InvariantCulture), match.Groups["ore"].Value.Trim(), false));
            }
            if (pulls.Count < 40) continue;
            int suspect = 0;
            for (int i = 30; i < pulls.Count; i++)
            {
                var latest = pulls.Take(i + 1).TakeLast(100).ToArray();
                var groups = latest.TakeLast(20).GroupBy(p => p.Ore).ToArray();
                // Two distinct resources with regular uninterrupted pulls demonstrate both streams.
                if (groups.Length != 2 || groups.Any(g => g.Count() < 6)) continue;
                if (groups.Any(g => g.Zip(g.Skip(1), (a,b) => (b.Utc-a.Utc).TotalSeconds).Any(gap => gap < 12 || gap > 32))) continue;
                var at = pulls[i].Utc.AddSeconds(1);
                if (groups.Any(g => (at-g.Last().Utc).TotalSeconds > 30)) continue;
                var rows = MiningStatusRings.Build(2, false, false, latest, at);
                if (!rows.Any(r => r.IsStopped)) continue;
                suspect++;
                if (suspect <= 2)
                {
                    Console.WriteLine($"SUSPECT {System.IO.Path.GetFileName(file)} at {at:HH:mm:ss}: {string.Join("; ", rows.Select(r => r.Detail))}");
                    Console.WriteLine(string.Join("|", latest.TakeLast(24).Select(p => $"{p.Utc:HH:mm:ss},{p.Units},{p.Ore}")));
                }
            }
            Console.WriteLine($"REPLAY {System.IO.Path.GetFileName(file)} pulls={pulls.Count} suspect={suspect}");
        }
    }
}
