namespace EveCommandCenter.Services;

// Tracks inferred pull streams, never physical module slots. Ambiguous pulls suppress red states.
internal static class MiningStatusRings
{
    internal sealed record Row(string Label, string Timing, string Accent, double Progress = 0, string Detail = "", string CycleText = "--", string CrystalText = "", string OreText = "")
    {
        public string ClassicText => $"L{(Label.Length == 1 ? Label[0] - 'A' + 1 : 1)}" + (IsStopped ? "" : $" {CycleText}");
        public string StatusSymbol => IsStopped ? "\u25A0" : Timing == "Learning" ? "-" : "\u25B6";
        public string StatusGeometry => IsStopped ? "M0,0 H7 V7 H0 Z" : Timing == "Learning" ? "M0,3 H7 V4 H0 Z" : "M0,0 L7,4 L0,8 Z";
        public string ClassicCrystalText => CrystalText.StartsWith("Type ", StringComparison.Ordinal) ? CrystalText[5..] : CrystalText == "ORE" ? "ORE" : "?";
        public string ClassicCrystalColor => CrystalText.StartsWith("Type ", StringComparison.Ordinal) ? "#64C7FF" : Accent;
        public bool IsStopped => Timing == "Stopped";
        public bool PulseStopped => IsStopped && Accent == "#E88991";
        public string CentreText => IsStopped ? "" : CycleText;
        public string ArcData
        {
            get
            {
                double portion = Math.Clamp(Progress, 0.01, 99.99), angle = portion * Math.PI / 50;
                return FormattableString.Invariant($"M 18,3 A 15,15 0 {(portion > 50 ? 1 : 0)} 1 {18 + 15 * Math.Sin(angle):0.###},{18 - 15 * Math.Cos(angle):0.###}");
            }
        }
    }
    internal sealed record Pull(DateTime Utc, double Units, string Ore, bool Critical);
    private sealed class Track
    {
        internal DateTime Last;
        internal string Ore = "";
        internal double Yield;
        internal double SeedCycle;
        internal List<double> Gaps = new();
        internal double Cycle => Gaps.Count == 0 ? SeedCycle : Gaps.TakeLast(2).Average();
        internal bool Ready => Gaps.Count >= 3 && Gaps.Max() - Gaps.Min() <= Math.Max(3, Cycle * .3);
    }
    // Keep the restart decision outside the already large Build method.
    // After a full hold and a prolonged pause, a new observed full-size pull
    // may have a changed yield. Never infer a restart from time or compression.
    private static bool TryResumeChangedYield(List<Track> tracks, Pull pull)
    {
        Track? candidate = null;
        foreach (Track track in tracks)
        {
            if (!track.Ready || track.Cycle <= 0 ||
                !string.Equals(track.Ore, pull.Ore, StringComparison.OrdinalIgnoreCase))
                continue;

            double previousCycle = track.Cycle;
            double elapsed = (pull.Utc - track.Last).TotalSeconds;
            double yieldDifference = Math.Abs(pull.Units - track.Yield);
            if (elapsed <= Math.Max(75, previousCycle * 3) ||
                pull.Units < track.Yield * .8 ||
                yieldDifference <= Math.Max(1.5, track.Yield * .2))
                continue;

            if (candidate == null || track.Last < candidate.Last)
                candidate = track;
        }
        if (candidate == null) return false;

        double seedCycle = candidate.Cycle;
        candidate.Last = pull.Utc;
        candidate.Yield = pull.Units;
        candidate.Gaps.Clear();
        candidate.SeedCycle = seedCycle;
        return true;
    }

    // A repeated short/long gap pair is two staggered pulls, not a faster module.
    // Require observable alternation; fitted count alone must never manufacture a second stream.
    private static Dictionary<string, double> StaggeredCadences(Pull[] observations, int fitted)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (fitted != 2) return result;
        foreach (var group in observations.Where(p => !p.Critical).GroupBy(p => p.Ore, StringComparer.OrdinalIgnoreCase))
        {
            var pulls = group.ToArray();
            if (pulls.Length < 10) continue;
            double yield = pulls.Select(p=>p.Units).Order().ElementAt(pulls.Length/2);
            pulls = pulls.Where(p=>Math.Abs(p.Units-yield)<=Math.Max(1.5,yield*.012)).ToArray();
            if (pulls.Length < 10) continue;
            // Keep a recently demonstrated cadence when one stream stops. Requiring the
            // entire rolling window to remain paired would erase precisely that evidence.
            for (int end = pulls.Length; end >= 10; end--)
            {
                var window = pulls.Skip(Math.Max(0,end-16)).Take(Math.Min(16,end)).ToArray();
                var gaps = window.Zip(window.Skip(1), (a,b)=>(b.Utc-a.Utc).TotalSeconds).ToArray();
                var pairs = gaps.Zip(gaps.Skip(1), (a,b)=>(Short:Math.Min(a,b), Long:Math.Max(a,b), Cycle:a+b)).ToArray();
                double cycle = pairs.Select(p=>p.Cycle).Order().ElementAt(pairs.Length/2);
                var stable = pairs.Where(p=>Math.Abs(p.Cycle-cycle)<=Math.Max(1.5,cycle*.06)).ToArray();
                if (cycle >= 6 && stable.Length >= pairs.Length*.8 &&
                    stable.Count(p=>p.Long-p.Short>=1.5 && p.Short<=p.Long*.9)>=Math.Max(4,pairs.Length*.25))
                { result[group.Key]=cycle; break; }
            }
        }
        return result;
    }
    internal static IReadOnlyList<Row> Build(int fitted, bool droneMining, bool muted, IEnumerable<Pull> source, DateTime now)
    {
        if (droneMining || fitted <= 0) return Array.Empty<Row>();
        fitted = Math.Min(fitted, 8);
        var recent = source.Where(p => p.Units > 0 && double.IsFinite(p.Units) && p.Utc <= now).OrderBy(p => p.Utc).TakeLast(100).ToArray();
        // Anchor the learning window to activity, not wall time: stopping both modules
        // must not erase the cadence. The caller scopes observations to the current ship session.
        var lastActivity = recent.LastOrDefault()?.Utc ?? now;
        var observations = recent.Where(p=>p.Utc>=lastActivity.AddMinutes(-10)).ToArray();
        var cadence = StaggeredCadences(observations, fitted);
        var tracks = new List<Track>();
        DateTime uncertainUntil = default;
        var lastNormal = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var pull in observations)
        {
            // EVE writes critical bonus units as a second line for the same completed cycle.
            if (pull.Critical && lastNormal.TryGetValue(pull.Ore, out var normalAt) && (pull.Utc - normalAt).TotalSeconds <= 1) continue;
            if (!pull.Critical) lastNormal[pull.Ore] = pull.Utc;
            var candidates = new List<(Track Track, double Score, double Gap, int Cycles)>();
            foreach (var track in tracks)
            {
                if (!string.Equals(track.Ore, pull.Ore, StringComparison.OrdinalIgnoreCase)) continue;
                double gap = (pull.Utc - track.Last).TotalSeconds;
                if (gap < Math.Max(6, track.Cycle * .65)) continue;
                double error = Math.Abs(pull.Units - track.Yield), tolerance = Math.Max(1.5, track.Yield * .012);
                int cycles = track.Cycle > 0 ? Math.Max(1, (int)Math.Round(gap / track.Cycle)) : 1;
                double deviation = track.Cycle > 0 ? Math.Abs(gap - cycles * track.Cycle) : 0;
                if (pull.Critical)
                {
                    if (!track.Ready || deviation > 2.5 || cycles > 3) continue;
                    candidates.Add((track, deviation / 2.5, gap, cycles));
                }
                else if (track.Cycle == 0 && error <= tolerance)
                    candidates.Add((track, error / tolerance, gap, 1));
                else if (track.Cycle > 0 && deviation <= 2.5 && error <= track.Yield * .2 && cycles <= 3)
                    candidates.Add((track, deviation / 2.5 + error / (3 * tolerance), gap, cycles));
                else if (track.Cycle > 0 && gap > track.Cycle + 8 && error <= tolerance)
                    candidates.Add((track, 2 + error / tolerance, gap, Math.Max(4, cycles)));
            }
            var ordered = candidates.OrderBy(c => c.Score).ToArray();
            if (ordered.Length > 1 && ordered[1].Score - ordered[0].Score < .2 &&
                !(Math.Abs(ordered[0].Track.Yield - ordered[1].Track.Yield) <= 1.5 &&
                  Math.Abs(ordered[0].Track.Cycle - ordered[1].Track.Cycle) <= 1))
            {
                uncertainUntil = pull.Utc.AddSeconds(Math.Max(30, tracks.Max(t => t.Cycle) * 2));
                continue;
            }
            if (ordered.Length > 0)
            {
                var chosen = ordered[0];
                if (chosen.Cycles <= 3)
                {
                    chosen.Track.Gaps.Add(chosen.Gap / chosen.Cycles);
                    if (chosen.Track.Gaps.Count > 4) chosen.Track.Gaps.RemoveAt(0);
                }
                chosen.Track.Last = pull.Utc;
                if (!pull.Critical) chosen.Track.Yield = chosen.Track.Yield * .75 + pull.Units * .25;
            }
            else if (!pull.Critical && tracks.Count < fitted)
                tracks.Add(new() { Last = pull.Utc, Yield = pull.Units, Ore = pull.Ore, SeedCycle = cadence.GetValueOrDefault(pull.Ore) });
            else if (!pull.Critical && !tracks.Any(t => t.Ore == pull.Ore) &&
                tracks.FirstOrDefault(t => (pull.Utc - t.Last).TotalSeconds > Math.Max(30, t.Cycle + 8)) is { } replaced)
            {
                replaced.Ore = pull.Ore; replaced.Last = pull.Utc; replaced.Yield = pull.Units; replaced.Gaps.Clear(); replaced.SeedCycle = cadence.GetValueOrDefault(pull.Ore);
            }
            // If a hold stopped the lasers for several cycles, their restarted
            // full-size pulls may have a new effective yield (boost/crystal/ore).
            // Relearn from the observed pull instead of leaving both lanes
            // permanently stuck on old stopped/learning tracks. Never infer a
            // restart from compression or elapsed time alone.
            else if (!pull.Critical && TryResumeChangedYield(tracks, pull))
            {
                // Successful recovery is based on an actual new laser pull.
            }
            // A short final pull when the hold fills is not a new yield/cadence.
            // Keep established tracks; subsequent full pulls can re-anchor a stopped stream.
            else if (!pull.Critical && tracks.Any(t => t.Ready &&
                string.Equals(t.Ore, pull.Ore, StringComparison.OrdinalIgnoreCase) && pull.Units < t.Yield * .8))
                continue;
            else
                uncertainUntil = pull.Utc.AddSeconds(Math.Max(30, tracks.Count == 0 ? 30 : tracks.Max(t => t.Cycle) * 2));
        }
        // Keep A/B ordered by learned yield when the rolling input window advances.
        tracks = tracks.OrderBy(t => t.Yield).ToList();
        return Enumerable.Range(0, fitted).Select(i =>
        {
            var track = i < tracks.Count ? tracks[i] : null;
            bool ready = track?.Ready == true && now >= uncertainUntil;
            double elapsed = track == null ? 0 : Math.Max(0, (now - track.Last).TotalSeconds);
            bool stopped = ready && elapsed > track!.Cycle + 8;
            string label = ((char)('A' + i)).ToString();
            return new Row(label, !ready ? "Learning" : stopped ? "Stopped" : $"~{Math.Max(0, track!.Cycle - elapsed):0}s",
                muted || !ready ? "#829C9F" : stopped ? "#E88991" : "#65D5B6",
                ready ? Math.Clamp(elapsed / track!.Cycle * 100, 0, 100) : 0,
                "Inferred mining stream " + label + (track == null ? "" : " - " + track.Ore) + ". Timing and yield identify a stream, not a module slot. " +
                (stopped ? "Stopped stream. " : "") +
                (!ready ? "Learning or ambiguous pulls; no stopped claim." : $"Typical cycle {track!.Cycle:0.#}s. Last pull {elapsed:0}s ago.") +
                (muted ? " Alarm muted." : "") + " Equal-yield coincident streams are interchangeable, not physical module identities.",
                ready ? $"{track!.Cycle:0.#}s" : "--", OreText: track?.Ore ?? "");
        }).ToArray();
    }
}
