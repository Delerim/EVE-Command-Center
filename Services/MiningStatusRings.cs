namespace EveCommandCenter.Services;

// Tracks inferred pull streams, never physical module slots. Ambiguous pulls suppress red states.
internal static class MiningStatusRings
{
    internal sealed record Row(string Label, string Timing, string Accent, double Progress = 0, string Detail = "", string CycleText = "--", string CrystalText = "", string OreText = "")
    {
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
        internal List<double> Gaps = new();
        internal double Cycle => Gaps.Count == 0 ? 0 : Gaps.TakeLast(2).Average();
        internal bool Ready => Gaps.Count >= 3 && Gaps.Max() - Gaps.Min() <= Math.Max(3, Cycle * .3);
    }
    internal static IReadOnlyList<Row> Build(int fitted, bool droneMining, bool muted, IEnumerable<Pull> source, DateTime now)
    {
        if (droneMining || fitted <= 0) return Array.Empty<Row>();
        fitted = Math.Min(fitted, 8);
        var tracks = new List<Track>();
        DateTime uncertainUntil = default;
        var lastNormal = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var pull in source.Where(p => p.Units > 0 && double.IsFinite(p.Units) && p.Utc <= now && p.Utc >= now.AddMinutes(-10)).OrderBy(p => p.Utc).TakeLast(100))
        {
            // EVE writes critical bonus units as a second line for the same completed cycle.
            if (pull.Critical && lastNormal.TryGetValue(pull.Ore, out var normalAt) && (pull.Utc - normalAt).TotalSeconds <= 1) continue;
            if (!pull.Critical) lastNormal[pull.Ore] = pull.Utc;
            var candidates = new List<(Track Track, double Score, double Gap, int Cycles)>();
            foreach (var track in tracks)
            {
                if (!string.Equals(track.Ore, pull.Ore, StringComparison.OrdinalIgnoreCase)) continue;
                double gap = (pull.Utc - track.Last).TotalSeconds;
                if (gap < Math.Max(6, track.Cycle * .5)) continue;
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
                else if (track.Cycle > 0 && gap > track.Cycle * 3 && error <= tolerance)
                    candidates.Add((track, 2 + error / tolerance, gap, cycles));
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
                tracks.Add(new() { Last = pull.Utc, Yield = pull.Units, Ore = pull.Ore });
            else if (!pull.Critical && !tracks.Any(t => t.Ore == pull.Ore) &&
                tracks.FirstOrDefault(t => (pull.Utc - t.Last).TotalSeconds > Math.Max(30, t.Cycle + 8)) is { } replaced)
            {
                replaced.Ore = pull.Ore; replaced.Last = pull.Utc; replaced.Yield = pull.Units; replaced.Gaps.Clear();
            }
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
                (!ready ? "Learning or ambiguous pulls; no stopped claim." : $"Typical cycle {track!.Cycle:0.#}s. Last pull {elapsed:0}s ago.") +
                (muted ? " Alarm muted." : "") + " Equal-yield coincident streams are interchangeable, not physical module identities.",
                ready ? $"{track!.Cycle:0.#}s" : "--", OreText: track?.Ore ?? "");
        }).ToArray();
    }
}
