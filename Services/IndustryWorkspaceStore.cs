using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>User planning intent only. ESI caches remain owned by IndustryService.</summary>
public sealed class IndustryWorkspaceStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private IndustryWorkspace _state = new();
    public string Error { get; private set; } = "";
    public IndustryWorkspaceStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center"), "industry-workspace.json");
        try { if (File.Exists(_path)) _state = Read(_path); else if (File.Exists(_path + ".bak")) throw new InvalidDataException("Planning file missing; a backup is available for recovery."); }
        catch (Exception ex) { Error = "Planning file could not be loaded; changes are blocked and the file is preserved. " + ex.Message; }
    }
    public IndustryWorkspace Snapshot() { lock (_gate) return Clone(_state); }
    private static IndustryWorkspace Clone(IndustryWorkspace state) => JsonSerializer.Deserialize<IndustryWorkspace>(JsonSerializer.Serialize(state))!;
    private static IndustryWorkspace Read(string path)
    {
        string json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        Require(doc.RootElement.TryGetProperty("SchemaVersion", out var version) && version.GetInt32() == 1, "Missing or unsupported planning schema; use a compatible application version.");
        var state = JsonSerializer.Deserialize<IndustryWorkspace>(json) ?? throw new InvalidDataException("Empty planning document.");
        Validate(state); return state;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private void Change(Action<IndustryWorkspace> change, string description, Guid? projectId = null)
    {
        lock (_gate)
        {
            Require(Error.Length == 0, Error);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Require(!File.Exists(_path) || Read(_path).Revision == _state.Revision, "Workspace changed in another instance. Reopen the application before editing.");
            var next = Clone(_state);
            change(next);
            next.Revision = checked(next.Revision + 1);
            next.Events.Add(new() { ProjectId = projectId, Description = description });
            Validate(next);
            Write(next);
            _state = next;
        }
    }
    private void Write(IndustryWorkspace next, bool keepPrevious = true)
    {
        string temp = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, next); stream.Flush(flushToDisk: true); }
            if (File.Exists(_path)) File.Replace(temp, _path, keepPrevious ? _path + ".bak" : null);
            else File.Move(temp, _path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void RestoreBackup()
    {
        lock (_gate)
        {
            Require(Error.Length > 0, "Backup recovery is only available after a load failure.");
            using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(_path))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(_path));
                    if (document.RootElement.TryGetProperty("SchemaVersion", out var version))
                        Require(version.GetInt32() <= 1, "This file belongs to a newer schema; do not downgrade it using an old backup.");
                }
                catch (JsonException) { }
            }
            var recovered = Read(_path + ".bak");
            // Keep both the corrupt file and the known-good backup for manual inspection.
            if (File.Exists(_path)) File.Copy(_path, _path + ".unreadable-" + DateTime.UtcNow.Ticks);
            Write(recovered, keepPrevious: false); _state = recovered; Error = "";
        }
    }
    private static void Linked(long? id, ISet<long> linked) => Require(!id.HasValue || linked.Contains(id.Value), "Choose a currently linked character or Unassigned.");
    public void SaveDefaults(long? lead, long? buyer, long? seller, Dictionary<long, IndustryCharacterRole> roles, ISet<long> linked)
    {
        Linked(lead, linked); Linked(buyer, linked); Linked(seller, linked);
        foreach (long id in roles.Keys) Linked(id, linked);
        Change(s => { s.DefaultLeadId = lead; s.DefaultBuyerId = buyer; s.DefaultSellerId = seller; s.Roles = new(roles); }, "Updated planning defaults / role preferences (existing projects unchanged).");
    }
    public Guid Create(string name, int typeId, long quantity, int? blueprintTypeId, ISet<long> linked)
    {
        var id = Guid.NewGuid();
        Change(s =>
        {
            Linked(s.DefaultLeadId, linked); Linked(s.DefaultBuyerId, linked); Linked(s.DefaultSellerId, linked);
            s.Projects.Add(new() { Id = id, Name = name.Trim(), LeadId = s.DefaultLeadId, BuyerId = s.DefaultBuyerId, SellerId = s.DefaultSellerId,
                Nodes = new() { new() { TypeId = typeId, Name = IndustryCatalog.Name(typeId), Quantity = quantity, BlueprintTypeId = blueprintTypeId, ExecutorId = s.DefaultLeadId } } });
        }, "Created draft project: " + name, id);
        return id;
    }
    public Guid AddComponent(Guid projectId, Guid parentId, int typeId, long quantity)
    {
        Guid id = Guid.NewGuid();
        Change(s =>
        {
            var p = Project(s, projectId); Require(!p.Archived, "Reopen the project before editing.");
            Require(p.Nodes.Any(n => n.Id == parentId), "Parent component was not found.");
            p.Nodes.Add(new() { Id = id, ParentId = parentId, TypeId = typeId, Name = IndustryCatalog.Name(typeId), Quantity = quantity, Strategy = "Buy" });
        }, $"Added planned component type {typeId}, quantity {quantity} under {parentId}.", projectId);
        return id;
    }
    public void EditProject(Guid id, string name, long? lead, long? buyer, long? seller, bool paused, bool archived, string notes, ISet<long> linked)
    {
        Change(s =>
        {
            var p = Project(s, id);
            if (p.LeadId != lead) Linked(lead, linked);
            if (p.BuyerId != buyer) Linked(buyer, linked);
            if (p.SellerId != seller) Linked(seller, linked);
            Require(!archived || !s.Reservations.Any(r => r.ProjectId == id), "Release reservations before archiving; expenses/history will remain.");
            p.Name = name.Trim(); p.LeadId = lead; p.BuyerId = buyer; p.SellerId = seller; p.Paused = paused; p.Archived = archived; p.Notes = notes;
        }, $"Edited project: {name}; lead {lead}, buyer {buyer}, seller {seller}, paused {paused}, archived {archived}.", id);
    }
    public void EditNode(Guid projectId, Guid nodeId, long quantity, long? executor, string strategy, string notes, ISet<long> linked)
    {
        Change(s =>
        {
            var p = Project(s, projectId); Require(!p.Archived, "Reopen the project before editing.");
            var node = p.Nodes.Single(n => n.Id == nodeId);
            if (node.ExecutorId != executor) Linked(executor, linked);
            node.Quantity = quantity; node.ExecutorId = executor; node.Strategy = strategy; node.Notes = notes;
        }, $"Edited component {nodeId}: planned quantity {quantity}, executor {executor}, strategy {strategy}.", projectId);
    }
    public void Reserve(Guid projectId, Guid nodeId, IndustryStockSource source, long quantity, DateTimeOffset now, IReadOnlyList<IndustryStockSource> currentStock)
    {
        Change(s =>
        {
            var p = Project(s, projectId); Require(!p.Archived && !p.Paused, "Resume the project before reserving stock.");
            var n = p.Nodes.Single(n => n.Id == nodeId);
            Require(source.Verified && IndustryWorkspaceInventory.Recent(source.SnapshotUtc, now), "A recent successful, accessible stock snapshot is required.");
            Require(source.TypeId == n.TypeId && source.OwnerId > 0 && source.ItemId > 0 && source.LocationId > 0, "Stock must match this component and have stable source IDs.");
            Require(currentStock.Contains(source), "Selected source no longer matches the current snapshot.");
            Require(!s.Reservations.Where(r => r.TypeId == source.TypeId).Any(r =>
                !IndustryWorkspaceInventory.Review(r, s.Reservations, currentStock, now).StartsWith("RESERVED", StringComparison.Ordinal)),
                "Existing reservations for this material have moved, missing or insufficient stock. Review/release them before allocating more; stack identity may have changed.");
            decimal allocated = s.Reservations.Where(r => r.OwnerId == source.OwnerId && r.ItemId == source.ItemId).Sum(r => (decimal)r.Quantity);
            Require(quantity > 0 && quantity <= source.Quantity - allocated, "Not enough free stock: this stack is already reserved or the quantity changed.");
            Require(quantity <= n.Quantity - s.Reservations.Where(r => r.NodeId == n.Id).Sum(r => (decimal)r.Quantity), "Reservation exceeds this node's planned quantity.");
            s.Reservations.Add(new() { ProjectId = projectId, NodeId = nodeId, OwnerId = source.OwnerId, ItemId = source.ItemId, TypeId = source.TypeId,
                LocationId = source.LocationId, LocationFlag = source.LocationFlag, Path = source.Path, Quantity = quantity, SnapshotUtc = source.SnapshotUtc });
        }, $"Reserved {quantity} of type {source.TypeId}, owner {source.OwnerId}, item {source.ItemId} at {source.Path}; delivery unverified.", projectId);
    }
    public void Release(Guid reservationId)
    {
        Guid projectId = Snapshot().Reservations.Single(r => r.Id == reservationId).ProjectId;
        Change(s => { var r = s.Reservations.Single(r => r.Id == reservationId); s.Reservations.Remove(r); }, "Released reservation " + reservationId + "; history retained.", projectId);
    }
    private static IndustryProject Project(IndustryWorkspace state, Guid id) => state.Projects.Single(p => p.Id == id);
    internal static void Validate(IndustryWorkspace state)
    {
        Require(state.SchemaVersion == 1, "Unsupported planning schema; use a compatible application version.");
        Require(state.Revision >= 0, "Invalid planning revision.");
        Require(state.Projects.Select(p => p.Id).Distinct().Count() == state.Projects.Count, "Duplicate project IDs.");
        var allNodes = state.Projects.SelectMany(p => p.Nodes).ToArray();
        Require(allNodes.Select(n => n.Id).Distinct().Count() == allNodes.Length, "Duplicate node IDs.");
        foreach (var p in state.Projects)
        {
            Require(p.Id != Guid.Empty && !string.IsNullOrWhiteSpace(p.Name), "Project name is required.");
            Require(p.Nodes.Count(n => n.ParentId == null) == 1, "Each project needs one root node.");
            var nodes = p.Nodes.ToDictionary(n => n.Id);
            foreach (var n in p.Nodes)
            {
                Require(n.Id != Guid.Empty && n.TypeId > 0 && n.Quantity > 0, "Product type and positive whole quantity are required.");
                Require(n.Strategy is "Make" or "Buy" or "Use Stock", "Choose Make, Buy or Use Stock.");
                var seen = new HashSet<Guid> { n.Id }; var current = n;
                while (current.ParentId is {} parent)
                { Require(nodes.ContainsKey(parent) && seen.Add(parent) && seen.Count <= 64, "Invalid or cyclic component tree."); current = nodes[parent]; }
                Require(state.Reservations.Where(r => r.NodeId == n.Id).Sum(r => (decimal)r.Quantity) <= n.Quantity, "Release excess reservations before reducing planned quantity.");
            }
        }
        Require(state.Reservations.Select(r => r.Id).Distinct().Count() == state.Reservations.Count, "Duplicate reservation IDs.");
        foreach (var r in state.Reservations)
            Require(r.Quantity > 0 && r.OwnerId > 0 && r.ItemId > 0 && state.Projects.Any(p => p.Id == r.ProjectId && p.Nodes.Any(n => n.Id == r.NodeId && n.TypeId == r.TypeId)), "Invalid reservation reference.");
    }
}
