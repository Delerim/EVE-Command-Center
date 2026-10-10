namespace EveCommandCenter.Models;

[Flags]
public enum IndustryCharacterRole
{
    None = 0, Manufacturing = 1, Buying = 2, Selling = 4,
    Jobs = 8, Market = 16, Wallet = 32, Dashboard = 64
}

public sealed class IndustryWorkspace
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public long? DefaultLeadId { get; set; }
    public long? DefaultBuyerId { get; set; }
    public long? DefaultSellerId { get; set; }
    public Dictionary<long, IndustryCharacterRole> Roles { get; set; } = new();
    public List<IndustryProject> Projects { get; set; } = new();
    public List<IndustryReservation> Reservations { get; set; } = new();
    public List<IndustryPlanningEvent> Events { get; set; } = new();
}

public sealed class IndustryProject
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public long? LeadId { get; set; }
    public long? BuyerId { get; set; }
    public long? SellerId { get; set; }
    public bool Paused { get; set; }
    public bool Archived { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<IndustryProjectNode> Nodes { get; set; } = new();
}

public sealed class IndustryProjectNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ParentId { get; set; }
    public int TypeId { get; set; }
    public string Name { get; set; } = "";
    public long Quantity { get; set; } = 1;
    public long? ExecutorId { get; set; }
    public int? BlueprintTypeId { get; set; }
    public string Strategy { get; set; } = "Make";
    public string Notes { get; set; } = "";
}

public sealed class IndustryReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public Guid NodeId { get; set; }
    public long OwnerId { get; set; }
    public long ItemId { get; set; }
    public int TypeId { get; set; }
    public long LocationId { get; set; }
    public string LocationFlag { get; set; } = "";
    public string Path { get; set; } = "";
    public long Quantity { get; set; }
    public DateTimeOffset SnapshotUtc { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IndustryPlanningEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset TimeUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ProjectId { get; set; }
    public string Description { get; set; } = "";
}

public sealed record IndustryStockSource(long OwnerId, string Owner, long ItemId, int TypeId,
    long LocationId, string LocationFlag, string Path, long Quantity, DateTimeOffset SnapshotUtc,
    bool Verified, string Detail);
