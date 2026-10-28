using System;
using System.Collections.Generic;
using System.Linq;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>
/// A plan describes intent; the ESI snapshot describes reality. Never mutate either while
/// comparing. In particular, extra EVE queue rows are NOT an ordering failure.
/// </summary>
public static class SkillPlanReconciliation
{
    public sealed class Row
    {
        public int Position { get; init; }
        public string Skill { get; init; } = "";
        public int Level { get; init; }
        public string LevelText => Skill + " " + SkillPlanning.Roman(Level);
        public string Status { get; init; } = "UNKNOWN";
        public string QueuePosition { get; init; } = "-";
        public string FinishText { get; init; } = "-";
        public string PhaseText { get; init; } = "";
        public bool Training { get; init; }
        public bool Missing { get; init; }
        public bool OutOfOrder { get; init; }
        public bool Trained { get; init; }
        public bool Queued { get; init; }
    }
    public sealed class Alert
    {
        public string Key { get; init; } = "";
        public string Severity { get; init; } = "INFO";
        public string Message { get; init; } = "";
    }
    public sealed class Remap
    {
        public string Id { get; init; } = "";
        public int Phase { get; init; }
        public string Mode { get; init; } = "";
        public string State { get; init; } = "";
        public string Trigger { get; init; } = "";
        public string Projected { get; init; } = "Not in current queue";
        public string Target { get; init; } = "";
        public bool Due { get; init; }
        public bool Acknowledged { get; init; }
    }
    public sealed class Result
    {
        public IReadOnlyList<Row> Rows { get; init; } = Array.Empty<Row>();
        public IReadOnlyList<Alert> Alerts { get; init; } = Array.Empty<Alert>();
        public IReadOnlyList<Remap> Remaps { get; init; } = Array.Empty<Remap>();
        public int Trained { get; init; }
        public int Training { get; init; }
        public int Queued { get; init; }
        public int Missing { get; init; }
        public int Extras { get; init; }
        public int OutOfOrder { get; init; }
        public bool MatchesOrder => OutOfOrder == 0;
        public string Alignment { get; init; } = "";
    }

    public static Result Compare(StoredSkillImport plan, EvePilotDashboard data, DateTimeOffset now)
    {
        if (plan.CharacterId != data.Summary.CharacterId)
            throw new InvalidOperationException("ESI snapshot belongs to a different pilot. Comparison cancelled.");
        var named = SkillPlanning.Catalog.Values.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var trained = data.TrainedSkills.ToDictionary(x => x.SkillId, x => x.TrainedSkillLevel);
        var inEve = data.SkillQueue.GroupBy(x => (x.SkillId, x.FinishedLevel))
            .ToDictionary(g => g.Key, g => g.OrderBy(q => q.Position).First());
        var planned = new HashSet<(int, int)>();
        var rows = new List<Row>();
        var alerts = new List<Alert>();
        int lastLivePosition = int.MinValue;
        int done = 0, training = 0, queued = 0, missing = 0, outOfOrder = 0;
        var phases = plan.Document.Remaps.OrderBy(x => x.Trigger.PlannedQueuePosition).ToArray();
        foreach (var step in plan.Document.Queue.OrderBy(x => x.Position))
        {
            if (!named.TryGetValue(step.Skill, out var skill))
                throw new InvalidOperationException("Unknown planned skill: " + step.Skill);
            var key = (skill.Id, step.Level);
            planned.Add(key);
            bool complete = trained.GetValueOrDefault(skill.Id) >= step.Level;
            bool exists = inEve.TryGetValue(key, out var live);
            bool active = exists && live != null && live.StartDate <= now && live.FinishDate > now;
            bool backwards = !complete && exists && live!.Position < lastLivePosition;
            if (!complete && exists) lastLivePosition = Math.Max(lastLivePosition, live!.Position);
            if (complete) done++;
            else if (backwards) outOfOrder++;
            else if (active) training++;
            else if (exists) queued++;
            else missing++;
            string status = complete ? "TRAINED" : backwards ? "ORDER CONFLICT" : active ? "TRAINING" : exists ? "IN EVE QUEUE" : "NOT QUEUED";
            var phase = phases.LastOrDefault(x => x.Trigger.PlannedQueuePosition <= step.Position);
            string phaseName = phase == null ? "No phase" : $"PHASE {phase.PhaseNumber} · {phase.Mode}";
            rows.Add(new Row {
                Position = step.Position, Skill = step.Skill, Level = step.Level,
                Status = status, QueuePosition = !complete && exists ? live!.Position.ToString() : "-",
                FinishText = !complete && exists && live!.FinishDate.HasValue ? live.FinishDate.Value.ToLocalTime().ToString("dd MMM yy HH:mm") : "-",
                PhaseText = phaseName, Training = active && !complete, Trained = complete,
                Queued = !complete && exists, Missing = !complete && !exists, OutOfOrder = backwards
            });
        }
        int extras = inEve.Keys.Count(key => !planned.Contains(key) && trained.GetValueOrDefault(key.SkillId) < key.FinishedLevel);
        if (outOfOrder > 0)
            alerts.Add(new Alert { Key = "queue-order", Severity = "WARNING", Message = $"{outOfOrder} planned levels appear out of order in EVE. Review before the next remap." });
        if (missing > 0)
        {
            // End-of-queue omissions are informative. An imminent hole requires action.
            int firstMissing = rows.Where(x => x.Missing).Min(x => x.Position);
            int firstOutstanding = rows.FirstOrDefault(x => !x.Trained)?.Position ?? (rows.Count + 1);
            bool urgent = firstMissing <= firstOutstanding + 5 || data.SkillQueue.Count < 5;
            alerts.Add(new Alert { Key = "missing-queue", Severity = urgent ? "WARNING" : "INFO",
                Message = $"{missing} planned levels aren't in EVE (first: #{firstMissing}). " +
                    (urgent ? "Queue needs attention soon." : "No alarm: these are later in the plan.") });
        }
        if (extras > 0)
            alerts.Add(new Alert { Key = "extra-queue", Severity = "INFO", Message = $"{extras} extra EVE queue entries aren't in this plan. Extra training is allowed when it doesn't reorder planned levels." });
        string alignment = SkillPlanImportService.BaseAlignment(plan, data);
        if (alignment.Contains("ATTRIBUTE MISMATCH", StringComparison.Ordinal))
            alerts.Add(new Alert { Key = "wrong-remap", Severity = "WARNING", Message = "Current base attributes differ from the expected training phase. Check implants and remap before changing anything." });
        var assessed = SkillPlanImportService.Assess(plan, data).ToDictionary(x => x.Remap.Id);
        var remaps = plan.Document.Remaps.OrderBy(x => x.PhaseNumber).Select(m => {
            assessed.TryGetValue(m.Id, out var assessment);
            DateTimeOffset? finish = null;
            if (!string.IsNullOrWhiteSpace(m.Trigger.AfterCompleted))
            {
                // Identify the completion anchor by skill ID and level rather than queue position.
                string marker = m.Trigger.AfterCompleted!;
                for (int level = 5; level >= 1; level--)
                {
                    string suffix = " " + SkillPlanning.Roman(level);
                    if (!marker.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (named.TryGetValue(marker[..^suffix.Length], out var anchor))
                        finish = data.SkillQueue.FirstOrDefault(q => q.SkillId == anchor.Id && q.FinishedLevel == level)?.FinishDate;
                    break;
                }
            }
            if (assessment?.Due == true && assessment.Acknowledged == false)
                alerts.Add(new Alert { Key = "remap:" + m.Id, Severity = "REMINDER",
                    Message = "Remap checkpoint: " + assessment.Detail });
            return new Remap { Id = m.Id, Phase = m.PhaseNumber, Mode = m.Mode,
                State = assessment?.State ?? "WAITING", Trigger = m.Trigger.AfterCompleted == null ? "Initial attributes" :
                    "After " + m.Trigger.AfterCompleted + " / before " + (m.Trigger.BeforeStarts ?? "next phase"),
                Projected = finish.HasValue ? finish.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm") + " (ESI projection)" :
                    (m.Trigger.AfterCompleted == null ? "Current / initial phase" : "Milestone not in EVE queue"),
                Target = m.TargetBaseAttributes?.ToString() ?? "Retain attributes",
                Due = assessment?.Due ?? false, Acknowledged = assessment?.Acknowledged ?? false };
        }).ToArray();
        return new Result { Rows = rows, Alerts = alerts, Remaps = remaps,
            Trained = done, Training = training, Queued = queued, Missing = missing, Extras = extras,
            OutOfOrder = outOfOrder, Alignment = alignment };
    }
}
