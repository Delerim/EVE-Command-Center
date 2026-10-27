using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

public sealed class SkillImportCharacter
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("id")] public long? Id { get; set; }
}
public sealed class SkillImportStep
{
    [JsonPropertyName("position")] public int Position { get; set; }
    [JsonPropertyName("skill")] public string Skill { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    public string EveText => $"{Skill} {SkillPlanning.Roman(Level)}";
}
public sealed class SkillImportAttributes
{
    [JsonPropertyName("intelligence")] public int Intelligence { get; set; }
    [JsonPropertyName("memory")] public int Memory { get; set; }
    [JsonPropertyName("perception")] public int Perception { get; set; }
    [JsonPropertyName("willpower")] public int Willpower { get; set; }
    [JsonPropertyName("charisma")] public int Charisma { get; set; }
    public int Get(int id) => id switch { 165 => Intelligence, 166 => Memory, 167 => Perception, 168 => Willpower, 164 => Charisma, _ => 0 };
    public override string ToString() => $"INT {Intelligence} / MEM {Memory} / PER {Perception} / WIL {Willpower} / CHA {Charisma}";
}
public sealed class SkillImportTrigger
{
    [JsonPropertyName("afterCompleted")] public string? AfterCompleted { get; set; }
    [JsonPropertyName("beforeStarts")] public string? BeforeStarts { get; set; }
    [JsonPropertyName("plannedQueuePosition")] public int PlannedQueuePosition { get; set; }
}
public sealed class SkillImportRemap
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("phaseNumber")] public int PhaseNumber { get; set; }
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("targetBaseAttributes")] public SkillImportAttributes? TargetBaseAttributes { get; set; }
    [JsonPropertyName("trigger")] public SkillImportTrigger Trigger { get; set; } = new();
    [JsonPropertyName("estimatedDate")] public string? EstimatedDate { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
}
public sealed class SkillImportDocument
{
    [JsonPropertyName("schema")] public string Schema { get; set; } = "";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("character")] public SkillImportCharacter Character { get; set; } = new();
    [JsonPropertyName("queue")] public List<SkillImportStep> Queue { get; set; } = new();
    [JsonPropertyName("followOnQueue")] public List<SkillImportStep> FollowOnQueue { get; set; } = new();
    [JsonPropertyName("remaps")] public List<SkillImportRemap> Remaps { get; set; } = new();
    [JsonPropertyName("skillbooksNeeded")] public List<string> SkillbooksNeeded { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
}
public sealed class StoredSkillImport
{
    public long CharacterId { get; set; }
    public DateTimeOffset ImportedUtc { get; set; }
    public SkillImportDocument Document { get; set; } = new();
    public HashSet<string> Acknowledged { get; set; } = new(StringComparer.Ordinal);
}
public sealed class SkillRemapAssessment
{
    public SkillImportRemap Remap { get; init; } = new();
    public bool Due { get; init; }
    public bool Acknowledged { get; init; }
    public string State { get; init; } = "WAITING";
    public string Detail { get; init; } = "";
}

/// <summary>Local, read-only-to-ESI skill-plan import and persistent remap state.</summary>
public static class SkillPlanImportService
{
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "PilotData");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static readonly Dictionary<string, PlanningSkill> NamedSkills = SkillPlanning.Catalog.Values.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
    public static string PlanPath(long characterId) => Path.Combine(DirectoryPath, $"skill-plan-import-{characterId}.json");
    public static StoredSkillImport? Load(long characterId)
    {
        string path = PlanPath(characterId);
        if (!File.Exists(path)) return null;
        var stored = JsonSerializer.Deserialize<StoredSkillImport>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Saved skill plan was empty.");
        if (stored.CharacterId != characterId) throw new InvalidDataException("Saved skill plan belongs to another character.");
        stored.Acknowledged ??= new(StringComparer.Ordinal);
        return stored;
    }
    public static void Save(StoredSkillImport plan)
    {
        Directory.CreateDirectory(DirectoryPath);
        string path = PlanPath(plan.CharacterId);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(plan, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, path, true);
    }
    public static SkillImportDocument ReadFile(string path, EvePilotProfile pilot)
    {
        string json;
        if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var candidates = zip.Entries.Where(e => e.FullName.Replace('\\', '/').StartsWith("Plans/", StringComparison.OrdinalIgnoreCase)
                && e.Name.EndsWith(".eccskillplan.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 0) candidates = zip.Entries.Where(e => e.Name.EndsWith(".eccskillplan.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            var entry = candidates.SingleOrDefault(e => string.Equals(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(e.Name)), pilot.CharacterName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) throw new InvalidDataException($"ZIP has no plan for {pilot.CharacterName}; import a matching pilot only.");
            if (entry.Length > 4_000_000) throw new InvalidDataException("Plan entry exceeds the 4 MB safety limit.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8, true);
            json = reader.ReadToEnd();
        }
        else
        {
            if (new FileInfo(path).Length > 4_000_000) throw new InvalidDataException("Plan exceeds the 4 MB safety limit.");
            json = File.ReadAllText(path);
        }
        var doc = JsonSerializer.Deserialize<SkillImportDocument>(json, JsonOptions) ?? throw new InvalidDataException("File has no skill plan.");
        Validate(doc, pilot);
        return doc;
    }
    public static void Validate(SkillImportDocument doc, EvePilotProfile pilot)
    {
        if (doc.Schema != "ecc.skill-plan.v1" || doc.SchemaVersion != 1) throw new InvalidDataException("Unsupported schema; expected ecc.skill-plan.v1, version 1.");
        if (doc.Character == null || !string.Equals(doc.Character.Name, pilot.CharacterName, StringComparison.OrdinalIgnoreCase)
            || doc.Character.Id.HasValue && doc.Character.Id.Value != pilot.CharacterId)
            throw new InvalidDataException($"This plan belongs to {doc.Character?.Name ?? "an unknown pilot"}, not {pilot.CharacterName}. Nothing was imported.");
        if (doc.Queue == null || doc.Queue.Count == 0 || doc.Queue.Count > 150)
            throw new InvalidDataException("Queue must contain between 1 and 150 ordered skill levels.");
        if (doc.Remaps == null) throw new InvalidDataException("Remaps must be an array (it may be empty).");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (entry, index) in doc.Queue.Select((step, i) => (step, i)))
        {
            if (entry == null || entry.Position != index + 1 || entry.Level is < 1 or > 5 || !NamedSkills.ContainsKey(entry.Skill))
                throw new InvalidDataException($"Invalid queue position, level or unknown skill at row {index + 1}: {entry?.Skill}.");
            if (!seen.Add(entry.Skill.Trim() + ":" + entry.Level)) throw new InvalidDataException($"Duplicate queue level: {entry.EveText}.");
        }
        var remapIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var remap in doc.Remaps)
        {
            if (remap == null || string.IsNullOrWhiteSpace(remap.Id) || !remapIds.Add(remap.Id)) throw new InvalidDataException("Remap IDs must be unique and nonempty.");
            if (!(new[] { "IM", "PW", "CW", "current" }).Contains(remap.Mode, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException($"Unsupported remap mode: {remap.Mode}");
            if (remap.Mode != "current" && remap.TargetBaseAttributes == null) throw new InvalidDataException($"Remap {remap.Id} has no base attributes.");
            int boundary = remap.Trigger?.PlannedQueuePosition ?? 0;
            if (boundary < 1 || boundary > doc.Queue.Count) throw new InvalidDataException($"Remap {remap.Id} has invalid queue boundary {boundary}.");
            if (!string.IsNullOrWhiteSpace(remap.Trigger?.BeforeStarts) && !string.Equals(doc.Queue[boundary-1].EveText, remap.Trigger.BeforeStarts, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Remap {remap.Id}: BEFORE target does not match queue entry {boundary}.");
            if (!string.IsNullOrWhiteSpace(remap.Trigger?.AfterCompleted)
                && !doc.Queue.Take(boundary - 1).Any(x => string.Equals(x.EveText, remap.Trigger.AfterCompleted, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Remap {remap.Id}: AFTER skill is missing before boundary.");
        }
    }
    public static StoredSkillImport Import(SkillImportDocument doc, EvePilotProfile pilot)
    {
        Validate(doc, pilot);
        var previous = Load(pilot.CharacterId);
        var imported = new StoredSkillImport { CharacterId = pilot.CharacterId, Document = doc, ImportedUtc = DateTimeOffset.UtcNow };
        // Never silently carry acknowledgement to a changed training plan.
        if (previous != null && JsonSerializer.Serialize(previous.Document, JsonOptions) == JsonSerializer.Serialize(doc, JsonOptions))
            imported.Acknowledged = previous.Acknowledged;
        Save(imported);
        return imported;
    }
    private static (int Id, int Level)? ParseLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        for (int i = 5; i >= 1; i--)
        {
            string tail = " " + SkillPlanning.Roman(i);
            if (text.EndsWith(tail, StringComparison.OrdinalIgnoreCase) && NamedSkills.TryGetValue(text[..^tail.Length], out var skill)) return (skill.Id, i);
        }
        return null;
    }
    public static string RemapKey(SkillImportDocument doc, SkillImportRemap item) => doc.Name + "::" + item.Id + "::" + item.Trigger.AfterCompleted + "::" + item.Mode;
    public static bool IsCompleted(string? levelName, EvePilotDashboard data)
    {
        var target = ParseLevel(levelName);
        return target.HasValue && data.TrainedSkills.Any(x => x.SkillId == target.Value.Id && x.TrainedSkillLevel >= target.Value.Level);
    }
    public static List<SkillRemapAssessment> Assess(StoredSkillImport plan, EvePilotDashboard data)
    {
        var result = new List<SkillRemapAssessment>();
        foreach (var remap in plan.Document.Remaps.OrderBy(r => r.PhaseNumber))
        {
            bool acknowledged = plan.Acknowledged.Contains(RemapKey(plan.Document, remap));
            bool completed = IsCompleted(remap.Trigger.AfterCompleted, data);
            bool imminent = false;
            var anchor = ParseLevel(remap.Trigger.AfterCompleted);
            if (!completed && anchor.HasValue)
                imminent = data.SkillQueue.Any(x => x.SkillId == anchor.Value.Id && x.FinishedLevel == anchor.Value.Level && x.FinishDate.HasValue
                    && x.FinishDate.Value >= DateTimeOffset.UtcNow && x.FinishDate.Value <= DateTimeOffset.UtcNow.AddHours(12));
            bool due = !acknowledged && (completed || imminent);
            string state = acknowledged ? "ACKNOWLEDGED" : completed ? "REMAP DUE" : imminent ? "DUE WITHIN 12H" : "WAITING";
            if (string.IsNullOrWhiteSpace(remap.Trigger.AfterCompleted))
            {
                due = false;
                state = acknowledged ? "ACKNOWLEDGED" : "INITIAL PHASE";
                // Historical phase-1 remaps must not be flagged if the pilot has already reached a later phase.
                bool laterPhaseReached = plan.Document.Remaps.Any(next => next.PhaseNumber > remap.PhaseNumber
                    && IsCompleted(next.Trigger.AfterCompleted, data));
                if (!acknowledged && !laterPhaseReached && remap.TargetBaseAttributes != null && data.TrainingProfile.ImplantDataAvailable
                    && data.TrainingProfile.Attributes.Count >= 5 && data.TrainingProfile.Attributes.All(a => a.Raw.HasValue))
                {
                    due = data.TrainingProfile.Attributes.Any(a => a.Raw != remap.TargetBaseAttributes.Get(a.DogmaAttributeId));
                    if (due) state = "INITIAL MAP MISMATCH";
                }
            }
            result.Add(new SkillRemapAssessment { Remap = remap, Due = due, Acknowledged = acknowledged, State = state,
                Detail = (remap.Trigger.AfterCompleted == null ? "Initial phase" : "After " + remap.Trigger.AfterCompleted)
                    + " / before " + remap.Trigger.BeforeStarts + " | " + (remap.TargetBaseAttributes?.ToString() ?? "Keep current attributes") });
        }
        return result;
    }
    public static void Acknowledge(long characterId, string remapId)
    {
        var plan = Load(characterId) ?? throw new InvalidOperationException("No plan installed for this pilot.");
        var remap = plan.Document.Remaps.Single(x => x.Id == remapId);
        plan.Acknowledged.Add(RemapKey(plan.Document, remap));
        Save(plan);
    }
    public static string BaseAlignment(StoredSkillImport plan, EvePilotDashboard data)
    {
        var phases = plan.Document.Remaps.OrderBy(x => x.PhaseNumber).ToArray();
        if (phases.Length == 0) return "No remap phase defined";
        int current = 0;
        for (int i = 1; i < phases.Length; i++)
            if (plan.Acknowledged.Contains(RemapKey(plan.Document, phases[i]))) current = i;
        var phase = phases[current];
        if (phase.TargetBaseAttributes == null) return $"Phase {phase.PhaseNumber}: keep current attributes";
        if (!data.TrainingProfile.ImplantDataAvailable || data.TrainingProfile.Attributes.Count < 5 || data.TrainingProfile.Attributes.Any(x => !x.Raw.HasValue))
            return $"Phase {phase.PhaseNumber}: cannot verify base map until active implant data is loaded";
        bool match = data.TrainingProfile.Attributes.All(x => x.Raw == phase.TargetBaseAttributes.Get(x.DogmaAttributeId));
        return $"Phase {phase.PhaseNumber}: " + (match ? "ON PLAN" : "ATTRIBUTE MISMATCH") + " | " + phase.TargetBaseAttributes;
    }
    public static string EveQueueText(StoredSkillImport plan, EvePilotDashboard? data, bool excludeExistingQueue)
    {
        var trained = data?.TrainedSkills.ToDictionary(x => x.SkillId, x => x.TrainedSkillLevel) ?? new Dictionary<int,int>();
        HashSet<string> queued = excludeExistingQueue && data != null
            ? data.SkillQueue.Select(x => x.SkillId + ":" + x.FinishedLevel).ToHashSet()
            : new HashSet<string>();
        return string.Join(Environment.NewLine, plan.Document.Queue.Where(s => NamedSkills.TryGetValue(s.Skill, out var skill)
            && trained.GetValueOrDefault(skill.Id) < s.Level && !queued.Contains(skill.Id + ":" + s.Level)).Select(s => s.EveText));
    }
    public static string ExportSnapshot(EvePilotDashboard data)
    {
        var queueBySkill = data.SkillQueue.GroupBy(x => x.SkillId).ToDictionary(x => x.Key, x => x.OrderBy(z => z.Position).ToArray());
        var trainedBySkill = data.TrainedSkills.ToDictionary(x => x.SkillId);
        var rows = trainedBySkill.Keys.Concat(queueBySkill.Keys).Distinct().OrderBy(x => SkillPlanning.Catalog.TryGetValue(x, out var sk) ? sk.Name : x.ToString())
            .Select(id => new {
                skillId = id, skill = SkillPlanning.Catalog.TryGetValue(id, out var skill) ? skill.Name : $"Skill {id}",
                trainedLevel = trainedBySkill.GetValueOrDefault(id)?.TrainedSkillLevel ?? 0,
                activeLevel = trainedBySkill.GetValueOrDefault(id)?.ActiveSkillLevel ?? 0,
                skillPoints = trainedBySkill.GetValueOrDefault(id)?.SkillpointsInSkill ?? 0,
                queue = queueBySkill.GetValueOrDefault(id)?.Select(q => new {q.Position, q.FinishedLevel, q.StartDate, q.FinishDate, q.TrainingStartSp, q.LevelStartSp, q.LevelEndSp}).ToArray()
            }).ToArray();
        return JsonSerializer.Serialize(new { schema = "ecc.skill-snapshot.v1", exportedAtUtc = DateTimeOffset.UtcNow,
            characterId = data.Summary.CharacterId, characterName = data.Summary.CharacterName,
            totalSp = data.Summary.TotalSp, unallocatedSp = data.Summary.UnallocatedSp,
            effectiveAttributes = data.TrainingProfile.Attributes.Select(a => new {a.Name, a.Total, a.Raw, a.ImplantBonus}).ToArray(),
            skills = rows, queueEntries = data.SkillQueue.Count }, JsonOptions);
    }
    public static string ExportSnapshotCsv(EvePilotDashboard data)
    {
        string Quoted(string text) => "\"" + text.Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "character_id,character_name,skill_id,skill,trained_level,active_level,skillpoints_in_skill,queued_levels,queue_positions,total_sp,unallocated_sp" };
        var trained = data.TrainedSkills.ToDictionary(x => x.SkillId);
        var queued = data.SkillQueue.GroupBy(x => x.SkillId).ToDictionary(x => x.Key, x => x.ToArray());
        foreach (int id in trained.Keys.Concat(queued.Keys).Distinct().OrderBy(x => SkillPlanning.Catalog.TryGetValue(x, out var v) ? v.Name : x.ToString()))
        {
            trained.TryGetValue(id, out var skill);
            queued.TryGetValue(id, out var levels);
            string name = SkillPlanning.Catalog.TryGetValue(id, out var catalog) ? catalog.Name : "Skill " + id;
            lines.Add(string.Join(",", data.Summary.CharacterId, Quoted(data.Summary.CharacterName), id, Quoted(name),
                skill?.TrainedSkillLevel ?? 0, skill?.ActiveSkillLevel ?? 0, skill?.SkillpointsInSkill ?? 0,
                Quoted(string.Join(";", levels?.Select(q => q.FinishedLevel) ?? Enumerable.Empty<int>())),
                Quoted(string.Join(";", levels?.Select(q => q.Position) ?? Enumerable.Empty<int>())),
                data.Summary.TotalSp, data.Summary.UnallocatedSp?.ToString() ?? ""));
        }
        return string.Join(Environment.NewLine, lines);
    }
    public static string CreateTemplate(EvePilotProfile pilot)
    {
        var sample = new SkillImportDocument {
            Schema = "ecc.skill-plan.v1", SchemaVersion = 1, Name = pilot.CharacterName + " | New Training Plan",
            Character = new SkillImportCharacter { Id = pilot.CharacterId, Name = pilot.CharacterName },
            Queue = new() { new() { Position = 1, Skill = "Navigation", Level = 4 }, new() { Position = 2, Skill = "Navigation", Level = 5 } },
            Remaps = new() { new() { Id = "phase-1", PhaseNumber = 1, Mode = "current", Trigger = new() { BeforeStarts = "Navigation IV", PlannedQueuePosition = 1 } } }
        };
        return JsonSerializer.Serialize(sample, JsonOptions);
    }
}
