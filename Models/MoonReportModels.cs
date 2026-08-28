using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EveMultiPreview.Models;

public sealed class MoonReportState
{
    public long SelectedCharacterId { get; set; }
    public DateTimeOffset? LastRefreshUtc { get; set; }
    public Dictionary<long, MoonProfile> Profiles { get; set; } = new();
    public Dictionary<string, MoonProfile> PendingProfilesByMoonName { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MoonPullRecord> Pulls { get; set; } = new();
    public Dictionary<string, long> LedgerTotals { get; set; } = new();
    public Dictionary<string, MoonLedgerRecord> LedgerHistory { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> DailyMinedM3 { get; set; } = new();
    public HashSet<long> BaselinedObservers { get; set; } = new();
    public Dictionary<int, string> TypeNames { get; set; } = new();
    public Dictionary<int, double> TypeVolumes { get; set; } = new();
    public Dictionary<int, string> SystemNames { get; set; } = new();
    public Dictionary<long, string> CharacterNames { get; set; } = new();
    public Dictionary<long, string> CorporationNames { get; set; } = new();
    public Dictionary<int, double> TypePrices { get; set; } = new();
}

public sealed class MoonProfileImportResult
{
    public int Total { get; init; }
    public int Matched { get; init; }
    public int Pending { get; init; }
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
    public double SylvitePercent { get; set; }
    public double BitumensPercent { get; set; }
    public double CoesitePercent { get; set; }
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
    public bool JackpotObserved { get; set; }
    public bool SeenInLatestExtractionList { get; set; }
    public Dictionary<string, double> MinedM3ByOre { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MoonLedgerRecord
{
    public string Key { get; set; } = "";
    public string PullId { get; set; } = "";
    public long ObserverId { get; set; }
    public long CharacterId { get; set; }
    public long RecordedCorporationId { get; set; }
    public int TypeId { get; set; }
    public DateTime Date { get; set; }
    public long Quantity { get; set; }
    public double VolumeM3 { get; set; }
    public double EstimatedIsk { get; set; }
    public string OreName { get; set; } = "";
    public DateTimeOffset LastSeenUtc { get; set; }
}

public sealed class MoonReportSnapshot
{
    public DateTimeOffset GeneratedUtc { get; init; }
    public IReadOnlyList<MoonCardView> Cards { get; init; } =
        Array.Empty<MoonCardView>();
    public IReadOnlyList<MoonCardView> CalendarCards { get; init; } =
        Array.Empty<MoonCardView>();
    public IReadOnlyList<MoonAuditView> Audit { get; init; } =
        Array.Empty<MoonAuditView>();
    public int ScheduledCount { get; init; }
    public int ReadyCount { get; init; }
    public int ActiveFieldCount { get; init; }
    public int TargetDespawnCount { get; init; }
    public double ZeolitesLostM3 { get; init; }
    public double BitumensLostM3 { get; init; }
    public double SylviteLostM3 { get; init; }
    public double CoesiteLostM3 { get; init; }
    public double TotalMinedM3 { get; init; }
    public double TotalLostM3 { get; init; }
    public double ZeolitesMinedM3 { get; init; }
    public double SylviteMinedM3 { get; init; }
    public double BitumensMinedM3 { get; init; }
    public double CoesiteMinedM3 { get; init; }
    public int JackpotCount { get; init; }
    public IReadOnlyList<MoonDailyTotalView> DailyTotals { get; init; } =
        Array.Empty<MoonDailyTotalView>();
    public IReadOnlyList<MoonPeriodReportView> MonthlyReports { get; init; } =
        Array.Empty<MoonPeriodReportView>();
    public IReadOnlyList<MoonPeriodReportView> WeeklyReports { get; init; } =
        Array.Empty<MoonPeriodReportView>();
    public IReadOnlyList<MoonLedgerMoonView> LedgerMoons { get; init; } =
        Array.Empty<MoonLedgerMoonView>();
    public IReadOnlyList<MoonLedgerPullView> LedgerPulls { get; init; } =
        Array.Empty<MoonLedgerPullView>();
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
    public string PullLabel => "PULL  " + PullLength;
    public string LastFractureLabel => "LAST FRACTURE  " + LastFracture;
    public string FieldExpiry { get; init; } = "-";
    public string ZeolitesMined { get; init; } = "0 m3";
    public string ZeolitesRemaining { get; init; } = "Profile needed";
    public string BitumensMined { get; init; } = "0 m3";
    public string BitumensRemaining { get; init; } = "Profile needed";
    public string SylviteMined { get; init; } = "0 m3";
    public string SylviteRemaining { get; init; } = "Profile needed";
    public string CoesiteMined { get; init; } = "0 m3";
    public string CoesiteRemaining { get; init; } = "Profile needed";
    public double ZeolitesRemainingM3 { get; init; }
    public double BitumensRemainingM3 { get; init; }
    public double SylviteRemainingM3 { get; init; }
    public double CoesiteRemainingM3 { get; init; }
    public double InitialTotalM3 { get; init; }
    public double MinedTotalM3 { get; init; }
    public double RemainingTotalM3 { get; init; }
    public double RemainingPercent { get; init; }
    public string OreSummary { get; init; } = "";
    public string RemainingSummary { get; init; } = "";
    public IReadOnlyList<MoonOreRowView> OreRows { get; init; } =
        Array.Empty<MoonOreRowView>();
    public DateTimeOffset? ScheduleUtc { get; init; }
    public bool IsJackpot { get; init; }
    public string JackpotLabel { get; init; } = "";
    public bool HasTargetProfile { get; init; }
    public bool HasTargetLeftover { get; init; }
    public string MoonImageUri { get; init; } =
        "https://images.evetech.net/types/46031/render?size=128";
    public string StructureImageUri { get; init; } =
        "https://images.evetech.net/types/35832/render?size=64";
    public MoonProfile Profile { get; init; } = new();
}

public sealed class MoonOreRowView
{
    public string Name { get; init; } = "";
    public string Color { get; init; } = "#DDF2F1";
    public string Mined { get; init; } = "0 m3";
    public string Remaining { get; init; } = "0 m3";
}

public sealed class MoonLedgerMoonView
{
    public long MoonId { get; init; }
    public string MoonName { get; init; } = "";
    public string StructureName { get; init; } = "";
    public string Label { get; init; } = "";
}

public sealed class MoonLedgerPullView
{
    public string PullId { get; init; } = "";
    public long MoonId { get; init; }
    public string MoonName { get; init; } = "";
    public string StructureName { get; init; } = "";
    public string Label { get; init; } = "";
    public DateTimeOffset FractureUtc { get; init; }
    public bool JackpotObserved { get; init; }
    public double TotalM3 { get; init; }
    public double TotalIsk { get; init; }
    public IReadOnlyList<MoonLedgerRowView> Rows { get; init; } =
        Array.Empty<MoonLedgerRowView>();
}

public sealed class MoonLedgerRowView
{
    public long CharacterId { get; init; }
    public string CorporationName { get; init; } = "";
    public string CharacterName { get; init; } = "";
    public long Quantity { get; init; }
    public double VolumeM3 { get; init; }
    public double EstimatedIsk { get; init; }
    public double ZeolitesM3 { get; init; }
    public double SylviteM3 { get; init; }
    public double BitumensM3 { get; init; }
    public double CoesiteM3 { get; init; }
    public string QuantityText { get; init; } = "";
    public string VolumeText { get; init; } = "";
    public string IskText { get; init; } = "";
    public string OreBreakdown { get; init; } = "";
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
    public string SylviteMined { get; init; } = "";
    public string SylviteLeft { get; init; } = "";
    public string CoesiteMined { get; init; } = "";
    public string CoesiteLeft { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string OutcomeBrush { get; init; } = "#78909C";
}

public sealed class MoonDailyTotalView
{
    public DateTime Date { get; init; }
    public string DateKey { get; init; } = "";
    public double ZeolitesM3 { get; init; }
    public double SylviteM3 { get; init; }
    public double BitumensM3 { get; init; }
    public double CoesiteM3 { get; init; }
    public double LostM3 { get; init; }
    public int PullCount { get; init; }
    public int JackpotCount { get; init; }
    public double TotalM3 =>
        ZeolitesM3 + SylviteM3 + BitumensM3 + CoesiteM3;
}

public sealed class MoonPeriodReportView
{
    public string PeriodKey { get; init; } = "";
    public string Label { get; init; } = "";
    public DateTime StartDate { get; init; }
    public DateTime EndDate { get; init; }
    public double MinedM3 { get; init; }
    public double LostM3 { get; init; }
    public double ZeolitesM3 { get; init; }
    public double SylviteM3 { get; init; }
    public double BitumensM3 { get; init; }
    public double CoesiteM3 { get; init; }
    public int PullCount { get; init; }
    public int JackpotCount { get; init; }
    public double EfficiencyPercent =>
        MinedM3 + LostM3 > 0
            ? MinedM3 / (MinedM3 + LostM3) * 100.0
            : 0;
    public string MinedText { get; init; } = "";
    public string LostText { get; init; } = "";
    public string EfficiencyText { get; init; } = "";
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

public sealed class EsiUniverseNameEntry
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";
}

public sealed class EsiMarketPrice
{
    [JsonPropertyName("type_id")]
    public int TypeId { get; set; }

    [JsonPropertyName("average_price")]
    public double? AveragePrice { get; set; }

    [JsonPropertyName("adjusted_price")]
    public double? AdjustedPrice { get; set; }
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
