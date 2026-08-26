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
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
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
}

public sealed class EveSkillQueueView
{
    public int Position { get; init; }
    public string Skill { get; init; } = "";
    public string Level { get; init; } = "";
    public string Starts { get; init; } = "";
    public string Finishes { get; init; } = "";
    public string Remaining { get; init; } = "";
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
    public IReadOnlyList<EveSkillQueueView> SkillQueue { get; init; } = Array.Empty<EveSkillQueueView>();
    public IReadOnlyList<EveWalletJournalView> WalletJournal { get; init; } = Array.Empty<EveWalletJournalView>();
}
