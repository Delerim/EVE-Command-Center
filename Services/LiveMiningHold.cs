using System.Globalization;
using System.Text.RegularExpressions;

namespace EveCommandCenter.Services;

internal sealed record HoldLogEvent(string Character, DateTime Utc, string Kind, string Ore = "", long Units = 0);

// Session-only log estimate. Never seeds from an ESI response whose contents may predate its fetch.
internal sealed class LiveMiningHold
{
    private sealed class State
    {
        internal long Ship;
        internal DateTime Started;
        internal string Anchor = "Session estimate";
        internal bool Full;
        internal readonly List<HoldLogEvent> Events = new();
        internal bool Overflow;
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, State> _pilots = new(StringComparer.OrdinalIgnoreCase);
    internal void Configure(string pilot, long ship, DateTime now)
    {
        if (ship <= 0) return;
        lock (_gate)
            if (!_pilots.TryGetValue(pilot, out var state) || state.Ship != ship)
                _pilots[pilot] = new() { Ship = ship, Started = now };
    }
    internal void Empty(string pilot, DateTime now)
    {
        lock (_gate)
            if (_pilots.TryGetValue(pilot, out var state))
            {
                state.Started = now; state.Full = false; state.Anchor = "Emptied";
                state.Events.Clear(); state.Overflow = false;
            }
    }
    internal void Record(HoldLogEvent item, DateTime received)
    {
        lock (_gate)
        {
            if (!_pilots.TryGetValue(item.Character, out var state) ||
                item.Utc < state.Started || item.Utc < received.AddSeconds(-30) || item.Utc > received.AddSeconds(5)) return;
            if (item.Kind == "full")
            {
                state.Full = true; state.Anchor = "Full notification"; state.Started = item.Utc;
                state.Events.Clear(); state.Overflow = false; return;
            }
            if (item.Kind is not ("mined" or "compress") || item.Units <= 0 || string.IsNullOrWhiteSpace(item.Ore)) return;
            if (state.Events.Count >= 10000) { state.Overflow = true; return; }
            state.Events.Add(item);
        }
    }
    internal MiningHoldSnapshots.Row Present(string pilot, double capacity, Func<string, (double Raw, double Compressed)> volumes)
    {
        lock (_gate)
        {
            if (!_pilots.TryGetValue(pilot, out var state) || !double.IsFinite(capacity) || capacity <= 0)
                return new("Mining", "Capacity unavailable", 0, "#829C9F", "Refresh the pilot's ship and skills.");
            double used = state.Full ? capacity : 0; bool missing = state.Overflow;
            foreach (var item in state.Events)
            {
                var v = volumes(item.Ore);
                bool valid = double.IsFinite(v.Raw) && v.Raw > 0;
                if (item.Kind == "compress")
                    valid &= double.IsFinite(v.Compressed) && v.Compressed > 0 && v.Compressed < v.Raw;
                if (!valid) { missing = true; continue; }
                double delta = item.Units * (item.Kind == "compress" ? v.Compressed - v.Raw : v.Raw);
                if (!double.IsFinite(delta)) { missing = true; continue; }
                used = Math.Clamp(used + delta, 0, capacity);
            }
            double fraction = used / capacity;
            return new("Mining", missing ? "Waiting for volumes" : $"~{used:N0} / {capacity:N0} m\u00b3",
                missing ? 0 : fraction * 100,
                missing ? "#829C9F" : fraction >= .85 ? "#E88991" : fraction >= .5 ? "#E0BA68" : "#65D5B6",
                $"Live log estimate. {state.Anchor} since {state.Started.ToLocalTime():HH:mm:ss}.\n" +
                "Mining adds volume; compression reduces it; a full notification anchors it to capacity. " +
                "Unlogged transfers and unloading can cause drift. Use Reset beside the hold after unloading. " +
                "Starting contents are unknown until an empty/full anchor. Changing ships starts a new estimate.");
        }
    }

    internal static HoldLogEvent? Parse(string line, string character)
    {
        if (!line.Contains("(notify)", StringComparison.OrdinalIgnoreCase)) return null;
        var stamp = Regex.Match(line, @"^\s*\[\s*(?<time>\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2})\s*\]");
        if (!DateTime.TryParseExact(stamp.Groups["time"].Value, "yyyy.MM.dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)) return null;
        string text = Regex.Replace(line, "<[^>]*>", "");
        if (Regex.IsMatch(text, @"(?:Ship's cargo|Your mining|Your cargo) hold is full", RegexOptions.IgnoreCase))
            return new(character, utc, "full");
        var match = Regex.Match(text, @"Successfully compressed (?<ore>.+?) into (?<units>[\d, ]+) Compressed (?<output>.+?)\.\s*$");
        if (!match.Success || !match.Groups["ore"].Value.Equals(match.Groups["output"].Value, StringComparison.OrdinalIgnoreCase)) return null;
        if (!long.TryParse(Regex.Replace(match.Groups["units"].Value, @"[^\d]", ""), out long units) || units <= 0) return null;
        return new(character, utc, "compress", match.Groups["ore"].Value, units);
    }
}
