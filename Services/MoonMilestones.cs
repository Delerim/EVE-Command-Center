using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed record MoonMilestone(string Structure, string Title, string Message);

public static class MoonMilestones
{
    public static IReadOnlyList<MoonMilestone> Observe(MoonReportSnapshot snapshot, long reader,
        Dictionary<string, DateTimeOffset> seen, DateTimeOffset now)
    {
        var result = new List<MoonMilestone>();
        if (snapshot.LastRefreshUtc == null) return result;
        string prefix = "events:" + reader + ":";
        bool baseline = !seen.ContainsKey(prefix + "baseline");
        foreach (var card in snapshot.CalendarCards)
        {
            if (card.Status == "READY") Record("ready", card, "MOON READY TO FRACTURE", card.ScheduleValue);
            if (card.Status == "FIELD ACTIVE")
            {
                Record("fracture", card, "MOON FIELD DETECTED", card.Evidence + " | " + card.ScheduleValue);
                if (card.IsJackpot) Record("jackpot", card, "GLISTENING MOON DETECTED",
                    "Glistening ore confirmed in the mining ledger. " + card.RemainingSummary);
            }
        }
        seen[prefix + "baseline"] = now;
        return result;
        void Record(string kind, MoonCardView card, string title, string message)
        {
            string key = prefix + kind + ":" + card.PullId;
            if (!seen.TryAdd(key, now)) return;
            if (!baseline) result.Add(new(card.StructureName, title, message));
        }
    }
}
