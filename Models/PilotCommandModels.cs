using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Models;

public sealed class EvePilotProfile
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public string[] Scopes { get; set; } = Array.Empty<string>();
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class EveTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }
}

public sealed class EveSkillsResponse
{
    [JsonPropertyName("total_sp")]
    public long TotalSp { get; set; }

    [JsonPropertyName("skills")]
    public List<EveSkillEntry> Skills { get; set; } = new();
}

public sealed class EveSkillEntry
{
    [JsonPropertyName("skill_id")]
    public int SkillId { get; set; }

    [JsonPropertyName("skillpoints_in_skill")]
    public long SkillpointsInSkill { get; set; }

    [JsonPropertyName("trained_skill_level")]
    public int TrainedSkillLevel { get; set; }

    [JsonPropertyName("active_skill_level")]
    public int ActiveSkillLevel { get; set; }
}

public sealed class EveSkillQueueEntry
{
    [JsonPropertyName("skill_id")]
    public int SkillId { get; set; }

    [JsonPropertyName("finished_level")]
    public int FinishedLevel { get; set; }

    [JsonPropertyName("queue_position")]
    public int QueuePosition { get; set; }

    [JsonPropertyName("start_date")]
    public DateTimeOffset? StartDate { get; set; }

    [JsonPropertyName("finish_date")]
    public DateTimeOffset? FinishDate { get; set; }

    [JsonPropertyName("training_start_sp")]
    public long? TrainingStartSp { get; set; }

    [JsonPropertyName("level_start_sp")]
    public long? LevelStartSp { get; set; }

    [JsonPropertyName("level_end_sp")]
    public long? LevelEndSp { get; set; }
}

public sealed class EveWalletJournalEntry
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset Date { get; set; }

    [JsonPropertyName("ref_type")]
    public string RefType { get; set; } = "";

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("balance")]
    public decimal? Balance { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

public sealed class EveUniverseType
{
    [JsonPropertyName("type_id")]
    public int TypeId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("group_id")]
    public int GroupId { get; set; }

    [JsonPropertyName("published")]
    public bool Published { get; set; }

    [JsonPropertyName("dogma_attributes")]
    public List<EveDogmaAttributeValue> DogmaAttributes { get; set; } = new();
}

public sealed class EveDogmaAttributeValue
{
    [JsonPropertyName("attribute_id")]
    public int AttributeId { get; set; }

    [JsonPropertyName("value")]
    public double Value { get; set; }
}

public sealed class EveUniverseCategory
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("groups")]
    public List<int> Groups { get; set; } = new();
}

public sealed class EveUniverseGroup
{
    [JsonPropertyName("group_id")]
    public int GroupId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("published")]
    public bool Published { get; set; }

    [JsonPropertyName("types")]
    public List<int> Types { get; set; } = new();
}

public sealed class EveCharacterLocationResponse
{
    [JsonPropertyName("solar_system_id")]
    public int SolarSystemId { get; set; }
}

public sealed class EveCharacterShipResponse
{
    [JsonPropertyName("ship_type_id")]
    public int ShipTypeId { get; set; }

    [JsonPropertyName("ship_item_id")]
    public long ShipItemId { get; set; }

    [JsonPropertyName("ship_name")]
    public string ShipName { get; set; } = "";
}

public sealed class EveUniverseSystem
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class EveSkillCatalogEntry
{
    public int SkillId { get; set; }
    public string Name { get; set; } = "";
    public int GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public int Rank { get; set; } = 1;
    public long MaxSp { get; set; } = 256000;
    public int PrimaryAttributeId { get; set; }
    public int SecondaryAttributeId { get; set; }
}

public sealed class EveSkillCatalogCache
{
    public int SchemaVersion { get; set; }
    public DateTime GeneratedUtc { get; set; }
    public List<EveSkillCatalogEntry> Entries { get; set; } = new();
}

public sealed class EvePilotSummary
{
    public long CharacterId { get; init; }
    public string CharacterName { get; init; } = "";
    public decimal WalletBalance { get; init; }
    public long TotalSp { get; init; }
    public string CurrentSkill { get; init; } = "Queue empty";
    public string CurrentSkillRemaining { get; init; } = "";
    public string QueueEndsIn { get; init; } = "";
    public double CurrentProgressPercent { get; init; }
    public string CurrentSystem { get; init; } = "";
    public string CurrentShip { get; init; } = "";
}

public sealed class EveSkillQueueView
{
    public int Position { get; init; }
    public int SkillId { get; init; }
    public int FinishedLevel { get; init; }
    public string Skill { get; init; } = "";
    public string Level { get; init; } = "";
    public string Starts { get; init; } = "";
    public string Finishes { get; init; } = "";
    public string Remaining { get; init; } = "";
    public DateTimeOffset? StartDate { get; init; }
    public DateTimeOffset? FinishDate { get; init; }
    public long? TrainingStartSp { get; init; }
    public long? LevelStartSp { get; init; }
    public long? LevelEndSp { get; init; }
}

public sealed class EveWalletJournalView
{
    public string Date { get; init; } = "";
    public string Type { get; init; } = "";
    public string Amount { get; init; } = "";
    public string Balance { get; init; } = "";
    public string Reason { get; init; } = "";
}

public sealed class EvePilotDashboard
{
    public EvePilotSummary Summary { get; init; } = new();
    public EveTrainingProfile TrainingProfile { get; init; } = new();
    public IReadOnlyList<EveSkillEntry> TrainedSkills { get; init; } = Array.Empty<EveSkillEntry>();
    public IReadOnlyList<EveSkillQueueView> SkillQueue { get; init; } = Array.Empty<EveSkillQueueView>();
    public IReadOnlyList<EveWalletJournalView> WalletJournal { get; init; } = Array.Empty<EveWalletJournalView>();
}
