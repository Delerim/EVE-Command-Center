using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckSkillPlanReconciliation()
    {
        string source = Path.Combine("Tests", "Fixtures", "SkillPlans", "Ansurek.eccskillplan.json");
        var document = JsonSerializer.Deserialize<SkillImportDocument>(File.ReadAllText(source))!;
        var plan = new StoredSkillImport { CharacterId = 90012, Document = document };
        var names = SkillPlanning.Catalog.Values.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var alreadyTrained = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) {
            ["Planetology"] = 5, ["Drone Sharpshooting"] = 4, ["EM Shield Compensation"] = 1
        };
        var levels = alreadyTrained.Select(k => new EveSkillEntry {
            SkillId = names[k.Key].Id, TrainedSkillLevel = k.Value, ActiveSkillLevel = k.Value
        }).ToArray();
        var notQueued = new HashSet<string> { "Caldari Battlecruiser:2", "Caldari Battlecruiser:3" };
        var queue = new List<EveSkillQueueView>();
        int position = 0;
        var now = new DateTimeOffset(2026, 10, 27, 23, 0, 0, TimeSpan.Zero);
        foreach (var step in document.Queue)
        {
            if (alreadyTrained.GetValueOrDefault(step.Skill) >= step.Level || notQueued.Contains(step.Skill + ":" + step.Level)) continue;
            // Extras from the real ESI snapshot are inserted without disrupting plan order.
            if (step.Position == 59 || step.Position == 107 || step.Position == 109)
            {
                string skill = step.Position == 59 ? "Advanced Planetology" : "Light Missiles";
                int level = step.Position == 59 ? 4 : step.Position == 107 ? 2 : 3;
                queue.Add(new EveSkillQueueView { SkillId = names[skill].Id, Skill = skill, FinishedLevel = level, Position = ++position,
                    StartDate = now.AddDays(position), FinishDate = now.AddDays(position + 1) });
            }
            bool active = step.Skill == "Drone Sharpshooting" && step.Level == 5;
            queue.Add(new EveSkillQueueView { SkillId = names[step.Skill].Id, Skill = step.Skill,
                FinishedLevel = step.Level, Position = ++position,
                StartDate = active ? now.AddHours(-1) : now.AddDays(position),
                FinishDate = active ? now.AddDays(3) : now.AddDays(position + 1) });
        }
        var data = new EvePilotDashboard {
            Summary = new EvePilotSummary { CharacterId = plan.CharacterId, CharacterName = "Example Pilot", UnallocatedSp = 250_000 },
            TrainedSkills = levels, SkillQueue = queue,
            TrainingProfile = new EveTrainingProfile { ImplantDataAvailable = true,
                Attributes = new[] {
                    new EveTrainingAttribute { DogmaAttributeId = 165, Raw = 27, Total = 31, ImplantBonus = 4 },
                    new EveTrainingAttribute { DogmaAttributeId = 166, Raw = 21, Total = 25, ImplantBonus = 4 },
                    new EveTrainingAttribute { DogmaAttributeId = 167, Raw = 17, Total = 21, ImplantBonus = 4 },
                    new EveTrainingAttribute { DogmaAttributeId = 168, Raw = 17, Total = 21, ImplantBonus = 4 },
                    new EveTrainingAttribute { DogmaAttributeId = 164, Raw = 17, Total = 21, ImplantBonus = 4 }
                }
            }
        };
        var reconciled = SkillPlanReconciliation.Compare(plan, data, now);
        Check(reconciled.Trained == 6 && reconciled.Training == 1 && reconciled.Queued == 141 && reconciled.Missing == 2,
            "Ansurek-style snapshot reconciles all 150 plan entries by skill ID and level");
        Check(reconciled.Extras == 3 && reconciled.MatchesOrder,
            "Unplanned EVE queue additions do not wrongly flag planned order");
        Check(reconciled.Rows.Where(x => x.Missing).Select(x => x.LevelText).SequenceEqual(new[] {
            "Caldari Battlecruiser II", "Caldari Battlecruiser III" }), "Only the two end-of-plan levels remain unqueued");
        Check(!reconciled.Alerts.Any(x => x.Severity == "WARNING") && reconciled.Alignment.Contains("ON PLAN"),
            "Correct INT/MEM remap and end-of-queue omissions do not generate false warnings");
        var next = reconciled.Remaps.Single(x => x.Phase == 2);
        Check(next.Trigger.Contains("Thermodynamics IV") && next.Projected.Contains("ESI projection"),
            "Remap ETA is derived from the EVE queue milestone, never the stale plan estimate");
        var altered = queue.ToArray();
        var swapA = altered.First(q => q.Skill == "Drone Navigation" && q.FinishedLevel == 1);
        var swapB = altered.First(q => q.Skill == "Drone Navigation" && q.FinishedLevel == 2);
        var reordered = altered.Select(q => new EveSkillQueueView { SkillId = q.SkillId, FinishedLevel = q.FinishedLevel,
            Position = ReferenceEquals(q, swapA) ? swapB.Position : ReferenceEquals(q, swapB) ? swapA.Position : q.Position,
            StartDate = q.StartDate, FinishDate = q.FinishDate }).ToArray();
        var wrongOrder = new EvePilotDashboard { Summary = data.Summary, TrainedSkills = levels,
            SkillQueue = reordered, TrainingProfile = data.TrainingProfile };
        var conflict = SkillPlanReconciliation.Compare(plan, wrongOrder, now);
        Check(conflict.OutOfOrder > 0 && conflict.Alerts.Any(x => x.Key == "queue-order" && x.Severity == "WARNING"),
            "Swapped planned levels generate a true reconciliation warning");
        // Persistent warning lifecycle: create once, retain while active, close on resolution.
        var alertTime = DateTimeOffset.UtcNow;
        string notificationFolder = Path.Combine(Path.GetTempPath(), "ecc-skill-alert-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new NotificationCenterService(notificationFolder);
            var issue = new CenterNotification { Id = "SKILLS:90012:queue-order", Source = "SKILLS",
                AlertKey = "inversion", Active = true, Title = "Example Pilot | Training plan",
                Detail = "Live queue differs from plan", Updated = alertTime };
            history.SyncSkillPlanAlerts(new[] { issue }, alertTime);
            history.SyncSkillPlanAlerts(new[] { issue }, alertTime.AddMinutes(30));
            Check(history.Items.Count(x => x.Id == issue.Id) == 1 && history.Items.Single().Active,
                "Repeated ESI polling does not spam duplicate reconciliation warnings");
            history.SyncSkillPlanAlerts(Array.Empty<CenterNotification>(), alertTime.AddMinutes(40));
            Check(!history.Items.Single().Active && history.Items.Single().Read,
                "Resolved queue mismatch automatically closes the persistent notification");
        }
        finally { if (Directory.Exists(notificationFolder)) Directory.Delete(notificationFolder, true); }
        bool wrongPilot = false;
        try { SkillPlanReconciliation.Compare(plan, new EvePilotDashboard { Summary = new EvePilotSummary { CharacterId = 222 } }, now); }
        catch (InvalidOperationException) { wrongPilot = true; }
        Check(wrongPilot, "Cross-character ESI data never reconciles against the wrong pilot");
    }
}
