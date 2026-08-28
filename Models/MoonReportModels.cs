using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Models;

public sealed class MoonReportState
{
    public long SelectedCharacterId { get; set; }
    public DateTimeOffset? LastRefreshUtc { get; set; }
    public Dictionary<long, MoonProfile> Profiles { get; set; } = new();
    public Dictionary<string, MoonPullRecord> Pulls { get; set; } = new();
    public Dictionary<string, long> LedgerTotals { get; set; } = new();
    public HashSet<long> BaselinedObservers { get; set; } = new();
    public Dictionary<int, string> TypeNames { get; set; } = new();
    public Dictionary<int, double> TypeVolumes { get; set; } = new();
    public Dictionary<int, string> SystemNames { get; set; } = new();
}

public sealed class MoonProfile
{
    public long MoonId { get; set; }
    public long StructureId { get; set; }
    public string MoonName { get; set; } = "";
    public string StructureName { get; set; } = "";
    public int SystemId { get; set; }
    public string SystemName { get; set; } = "";
    public bool ProfileConfigured { get; set; }
    public double ZeolitesPercent { get; set; }
    public double BitumensPercent { get; set; }
    public double FieldLifetimeHours { get; set; } = 48;
    public double WastePercent { get; set; } = 7;
}

public sealed class MoonPullRecord
{
    public string Id { get; set; } = "";
    public long StructureId { get; set; }
    public long MoonId { get; set; }
    public string MoonName { get; set; } = "";
    public string StructureName { get; set; } = "";
    public int SystemId { get; set; }
    public string SystemName { get; set; } = "";
    public DateTimeOffset ExtractionStartUtc { get; set; }
    public DateTimeOffset ChunkArrivalUtc { get; set; }
    public DateTimeOffset NaturalDecayUtc { get; set; }
    public DateTimeOffset? FracturedUtc { get; set; }
    public DateTimeOffset? EstimatedFieldExpiryUtc { get; set; }
    public DateTimeOffset? ExpiredUtc { get; set; }
    public bool OutcomeUnobserved { get; set; }
    public bool SeenInLatestExtractionList { get; set; }
    public Dictionary<string, double> MinedM3ByOre { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MoonReportSnapshot
{
    public DateTimeOffset GeneratedUtc { get; init; }
    public IReadOnlyList<MoonCardView> Cards { get; init; } =
        Array.Empty<MoonCardView>();
    public IReadOnlyList<MoonAuditView> Audit { get; init; } =
        Array.Empty<MoonAuditView>();
    public int ScheduledCount { get; init; }
    public int ReadyCount { get; init; }
    public int ActiveFieldCount { get; init; }
    public int TargetDespawnCount { get; init; }
    public double ZeolitesLostM3 { get; init; }
    public double BitumensLostM3 { get; init; }
}

public sealed class MoonCardView
{
    public string PullId { get; init; } = "";
    public long MoonId { get; init; }
    public long StructureId { get; init; }
    public string MoonName { get; init; } = "";
    public string StructureName { get; init; } = "";
    public string SystemName { get; init; } = "";
    public string Status { get; init; } = "";
    public string StatusBrush { get; init; } = "#78909C";
    public string ScheduleLabel { get; init; } = "";
    public string ScheduleValue { get; init; } = "";
    public string PullLength { get; init; } = "";
    public string LastFracture { get; init; } = "Never observed";
    public string FieldExpiry { get; init; } = "-";
    public string ZeolitesMined { get; init; } = "0 m3";
    public string ZeolitesRemaining { get; init; } = "Profile needed";
    public string BitumensMined { get; init; } = "0 m3";
    public string BitumensRemaining { get; init; } = "Profile needed";
    public double ZeolitesRemainingM3 { get; init; }
    public double BitumensRemainingM3 { get; init; }
    public bool HasTargetProfile { get; init; }
    public bool HasTargetLeftover { get; init; }
    public string MoonImageUri { get; init; } =
        "https://images.evetech.net/types/46031/render?size=128";
    public MoonProfile Profile { get; init; } = new();
}

public sealed class MoonAuditView
{
    public string MoonName { get; init; } = "";
    public string StructureName { get; init; } = "";
    public string SystemName { get; init; } = "";
    public string Fractured { get; init; } = "";
    public string Expired { get; init; } = "";
    public string ZeolitesMined { get; init; } = "";
    public string ZeolitesLeft { get; init; } = "";
    public string BitumensMined { get; init; } = "";
    public string BitumensLeft { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string OutcomeBrush { get; init; } = "#78909C";
}

public sealed class EsiCharacterPublic
{
    [JsonPropertyName("corporation_id")]
    public long CorporationId { get; set; }
}

public sealed class EsiMoonExtraction
{
    [JsonPropertyName("structure_id")]
    public long StructureId { get; set; }

    [JsonPropertyName("moon_id")]
    public long MoonId { get; set; }

    [JsonPropertyName("extraction_start_time")]
    public DateTimeOffset ExtractionStartTime { get; set; }

    [JsonPropertyName("chunk_arrival_time")]
    public DateTimeOffset ChunkArrivalTime { get; set; }

    [JsonPropertyName("natural_decay_time")]
    public DateTimeOffset NaturalDecayTime { get; set; }
}

public sealed class EsiCorporationStructure
{
    [JsonPropertyName("structure_id")]
    public long StructureId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("system_id")]
    public int SystemId { get; set; }

    [JsonPropertyName("services")]
    public List<EsiStructureService> Services { get; set; } = new();
}

public sealed class EsiStructureService
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";
}

public sealed class EsiMiningObserver
{
    [JsonPropertyName("observer_id")]
    public long ObserverId { get; set; }

    [JsonPropertyName("last_updated")]
    public DateTime LastUpdated { get; set; }

    [JsonPropertyName("observer_type")]
    public string ObserverType { get; set; } = "";
}

public sealed class EsiMiningLedgerEntry
{
    [JsonPropertyName("character_id")]
    public long CharacterId { get; set; }

    [JsonPropertyName("recorded_corporation_id")]
    public long RecordedCorporationId { get; set; }

    [JsonPropertyName("type_id")]
    public int TypeId { get; set; }

    [JsonPropertyName("quantity")]
    public long Quantity { get; set; }

    [JsonPropertyName("last_updated")]
    public DateTime LastUpdated { get; set; }
}

public sealed class EsiMoonPublic
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("system_id")]
    public int SystemId { get; set; }
}

public sealed class EsiUniverseName
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class EsiTypePublic
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("volume")]
    public double Volume { get; set; }
}
