namespace EveCommandCenter.Services;

public static class MiningRateEstimator
{
    public sealed record Pull(DateTime Time, double Base, double Actual, double Jita = 0, double Amarr = 0, double Buyback = 0);
    public sealed record Rate(double Base, double Actual, double Jita, double Amarr, double Buyback, double Seconds, int Samples);
    public static Rate Calculate(IEnumerable<Pull> source, DateTime now)
    {
        var groups = new List<Pull>();
        foreach (var p in source.OrderBy(p => p.Time))
        {
            if (groups.Count == 0 || (p.Time - groups[^1].Time).TotalSeconds > 2.5) groups.Add(p);
            else { var old = groups[^1]; groups[^1] = old with { Base = old.Base + p.Base, Actual = old.Actual + p.Actual, Jita = old.Jita + p.Jita, Amarr = old.Amarr + p.Amarr, Buyback = old.Buyback + p.Buyback }; }
        }
        Rate empty = new(0, 0, 0, 0, 0, 0, 0);
        if (groups.Count < 3) return empty;
        var gaps = groups.Zip(groups.Skip(1), (a, b) => (b.Time - a.Time).TotalSeconds).OrderBy(g => g).ToArray();
        double typical = gaps[gaps.Length / 2];
        double pause = Math.Max(45, typical * 4);
        int start = 0;
        for (int i = 1; i < groups.Count; i++) if ((groups[i].Time - groups[i - 1].Time).TotalSeconds > pause) start = i;
        var active = groups.Skip(start).ToList();
        if (active.Count < 3 || (now - active[^1].Time).TotalSeconds > pause) return empty;
        var cutoff = active[^1].Time.AddSeconds(-90);
        // Include one boundary sample before the window; its yield belongs to the preceding interval.
        int boundary = active.FindLastIndex(p => p.Time <= cutoff);
        if (boundary > 0) active = active.Skip(boundary).ToList();
        double duration = (active[^1].Time - active[0].Time).TotalSeconds;
        if (duration <= 0) return empty;
        var completed = active.Skip(1).ToArray();
        return new(completed.Sum(p => p.Base) / duration, completed.Sum(p => p.Actual) / duration,
            completed.Sum(p => p.Jita) / duration * 3600, completed.Sum(p => p.Amarr) / duration * 3600,
            completed.Sum(p => p.Buyback) / duration * 3600, duration, completed.Length);
    }
}
