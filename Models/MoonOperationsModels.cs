namespace EveCommandCenter.Models;

public sealed class MoonCycleSummary
{
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? NextExpected { get; init; }
    public string Anchor { get; init; } = "";
    public int Fractured { get; init; }
    public int Jackpots { get; init; }
    public double MinedM3 { get; init; }
    public double LostM3 { get; init; }
    public int ExpiredWithOre { get; init; }
    public string Description => Start is { } start
        ? $"CURRENT CYCLE | {Anchor} | first fracture {start:dd MMM yyyy HH:mm} UTC" + (NextExpected is { } next ? $" | next anchor due {next:dd MMM HH:mm} UTC" : " | ends at this moon's next fracture")
        : "CURRENT CYCLE | waiting for the first recorded Raren fracture";
}

public sealed class StationFuelRow
{
    public long StructureId { get; init; }
    public string StructureName { get; init; } = "";
    public string SystemName { get; init; } = "";
    public int TypeId { get; init; }
    public string Icon => TypeId > 0 ? $"https://images.evetech.net/types/{TypeId}/icon?size=32" : "";
    public DateTimeOffset? Expires { get; init; }
    public DateTimeOffset Now { get; init; }
    public string Quantity { get; init; } = "Access needed";
    public string Services { get; init; } = "";
    public double? Days => Expires.HasValue ? Math.Max(0, (Expires.Value - Now).TotalDays) : null;
    public bool NeedsFuel => Days < 80;
    public string DaysText => Days is { } days ? $"{days:N1} days" : "Not reported";
    public string ExpiryText => Expires is { } expiry ? expiry.ToString("dd MMM yyyy HH:mm") + " UTC" : "Not reported";
    public string Status => Days is not { } days ? "UNKNOWN" : days <= 0 ? "EMPTY / EXPIRED" : days < 80 ? "REFUEL" : "OK";
    public string Color => Days == null ? "#8FB2B5" : Days <= 0 ? "#EF5350" : NeedsFuel ? "#FFD166" : "#74D6C9";
    public double ReservePercent => Math.Clamp((Days ?? 0) / 80 * 100, 0, 100);
}
