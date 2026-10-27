using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

internal static partial class Program
{
    private static void CheckSkillPlanImport()
    {
        var example = new EvePilotProfile { CharacterId = 1234567, CharacterName = "Test Pilot" };
        var template = JsonSerializer.Deserialize<SkillImportDocument>(SkillPlanImportService.CreateTemplate(example))!;
        SkillPlanImportService.Validate(template, example);
        Check(template.Queue.Count == 2 && template.Queue[1].EveText == "Navigation V", "Import template parses and preserves exact order");
        bool wrongPilot = false;
        try { SkillPlanImportService.Validate(template, new EvePilotProfile { CharacterId = 1234568, CharacterName = "Wrong Pilot" }); }
        catch (InvalidDataException) { wrongPilot = true; }
        Check(wrongPilot, "Wrong pilot import is rejected before writing");
        var nav = SkillPlanning.Catalog.Values.Single(x => x.Name == "Navigation");
        template.Remaps.Add(new SkillImportRemap {
            Id = "phase-2", PhaseNumber = 2, Mode = "IM",
            TargetBaseAttributes = new SkillImportAttributes { Intelligence = 27, Memory = 21, Perception = 17, Willpower = 17, Charisma = 17 },
            Trigger = new SkillImportTrigger { AfterCompleted = "Navigation IV", BeforeStarts = "Navigation V", PlannedQueuePosition = 2 }
        });
        SkillPlanImportService.Validate(template, example);
        var plan = new StoredSkillImport { CharacterId = example.CharacterId, Document = template };
        var data = new EvePilotDashboard { Summary = new EvePilotSummary { CharacterId = example.CharacterId, CharacterName = example.CharacterName, TotalSp = 10_000_000, UnallocatedSp = 250_000 },
            TrainedSkills = new[] { new EveSkillEntry { SkillId = nav.Id, TrainedSkillLevel = 4, ActiveSkillLevel = 4, SkillpointsInSkill = 45255 } },
            SkillQueue = new[] { new EveSkillQueueView { SkillId = nav.Id, Skill = "Navigation", FinishedLevel = 5, Position = 1 } } };
        var alerts = SkillPlanImportService.Assess(plan, data);
        Check(alerts.Count == 2 && alerts[1].Due && alerts[1].State == "REMAP DUE", "Completed anchor produces a persistent remap checkpoint");
        plan.Acknowledged.Add(SkillPlanImportService.RemapKey(template, template.Remaps[1]));
        Check(!SkillPlanImportService.Assess(plan, data)[1].Due, "Acknowledging a remap prevents duplicate alerts");
        Check(SkillPlanImportService.EveQueueText(plan, data, false) == "Navigation V", "EVE export omits already trained levels");
        Check(SkillPlanImportService.EveQueueText(plan, data, true).Length == 0, "EVE export can omit entries already in live queue");
        var snapshot = SkillPlanImportService.ExportSnapshot(data);
        Check(snapshot.Contains("\"unallocatedSp\": 250000") && snapshot.Contains("\"finishedLevel\": 5", StringComparison.OrdinalIgnoreCase), "Export includes unallocated SP, trained levels and queue");
        string csv = SkillPlanImportService.ExportSnapshotCsv(data);
        Check(csv.Contains("250000") && csv.Contains("Navigation") && csv.Contains(";" ) == false, "Combined CSV carries trained and queued skill with unallocated SP");
        bool duplicate = false;
        template.Queue[1].Level = 4;
        try { SkillPlanImportService.Validate(template, example); }
        catch (InvalidDataException) { duplicate = true; }
        Check(duplicate, "Repeated queue levels are blocked");
        Check(typeof(EveSkillsResponse).GetProperty("UnallocatedSp") != null, "ESI model retains unallocated skill points");
        // Validate the actual eight-pilot pack used by the project if present in the patch.
        string root = Path.Combine("Tests", "Fixtures", "SkillPlans");
        if (!Directory.Exists(root)) throw new InvalidDataException("Bundled eight-pilot import fixtures are missing.");
        var samples = Directory.GetFiles(root, "*.eccskillplan.json");
        Check(samples.Length == 8, "All eight corrected Oct 16 pilot plans are bundled");
        int total = 0, phases = 0;
        foreach (var file in samples)
        {
            var doc = JsonSerializer.Deserialize<SkillImportDocument>(File.ReadAllText(file))!;
            SkillPlanImportService.Validate(doc, new EvePilotProfile { CharacterName = doc.Character.Name, CharacterId = 10001 });
            Check(doc.Queue.Count == 150, doc.Character.Name + " retains 150 ordered levels");
            total += doc.Queue.Count; phases += doc.Remaps.Count;
        }
        Check(total == 1200 && phases == 17, "Imported fleet retains 1200 levels and 17 remap milestones");
        // Real ZIP import path: the previous user-facing eight-pilot pack uses Plans/<Pilot>.eccskillplan.json.
        string archivePath = Path.Combine(Path.GetTempPath(), "ecc-plan-import-test-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                foreach (string fixture in samples)
                {
                    var entry = archive.CreateEntry("Plans/" + Path.GetFileName(fixture));
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write(File.ReadAllText(fixture));
                }
            }
            var one = SkillPlanImportService.ReadFile(archivePath, new EvePilotProfile { CharacterName = "Mundifari", CharacterId = 10001 });
            Check(one.Character.Name == "Mundifari" && one.Queue.Count == 150 && one.Remaps.Count == 2,
                "Eight-pilot ZIP imports exactly the selected authenticated pilot");
            bool rejected = false;
            try { SkillPlanImportService.ReadFile(archivePath, new EvePilotProfile { CharacterName = "Unknown Alt", CharacterId = 10001 }); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "ZIP with no matching pilot is safely rejected");
        }
        finally { if (File.Exists(archivePath)) File.Delete(archivePath); }
    }
}
