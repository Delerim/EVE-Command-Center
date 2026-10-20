using System.IO;
using System.Text.Json;
using EveCommandCenter.Models;

namespace EveCommandCenter.Services;

/// <summary>User planning intent only. ESI caches remain owned by IndustryService.</summary>
public sealed class IndustryWorkspaceStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private IndustryLedger Ledger => new(Path.GetDirectoryName(_path)!);
    private IndustryWorkspace _state = new();
    public (List<ProjectCashFlow.Entry> Trades, List<AttributedExpense> Expenses) ProjectFinancialEvidence(Guid project)
        => Ledger.Exists ? Ledger.ProjectFinancialEvidence(project) : (new(), new());
    public List<AttributedExpense> ProjectExpenses(Guid project) => Ledger.Exists ? Ledger.ProjectExpenses(project) : new();
    public List<ProjectCashFlow.Entry> ProjectFinancialEntries(Guid project) => Ledger.Exists ? Ledger.ProjectFinancialEntries(project) : new();
    public string Error { get; private set; } = "";
    public IndustryWorkspaceStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center"), "industry-workspace.json");
        try { if (Ledger.Exists) { _state = Ledger.ReadWorkspace(); Validate(_state); } else if (File.Exists(_path)) _state = Read(_path); else if (File.Exists(_path + ".bak")) throw new InvalidDataException("Planning file missing; a backup is available for recovery."); }
        catch (Exception ex) { Error = "Planning file could not be loaded; changes are blocked and the file is preserved. " + ex.Message; }
    }
    public List<AttributedPurchase> ProjectPurchases(Guid project) => Ledger.Exists ? Ledger.ProjectPurchases(project) : new();
    public List<AttributedSale> ProjectSales(Guid project) => Ledger.Exists ? Ledger.ProjectPurchases(project, true).Select(p => new AttributedSale(p.OwnerId, p.Purchase, p.Allocation)).ToList() : new();
    public IndustryWorkspace Snapshot() { lock (_gate) return Clone(_state); }
    private static IndustryWorkspace Clone(IndustryWorkspace state) => JsonSerializer.Deserialize<IndustryWorkspace>(JsonSerializer.Serialize(state))!;
    private static IndustryWorkspace Read(string path)
    {
        string json = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(json);
        Require(doc.RootElement.TryGetProperty("SchemaVersion", out var version) && version.GetInt32() is 1 or 2 or 3, "Missing or unsupported planning schema; use a compatible application version.");
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
            Require(Ledger.Exists ? Ledger.ReadWorkspace().Revision == _state.Revision : !File.Exists(_path) || Read(_path).Revision == _state.Revision, "Workspace changed in another instance. Reopen the application before editing.");
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
        if (Ledger.Exists) { Ledger.SaveWorkspace(next, _state.Revision); return; }
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
            Require(!Ledger.Exists, "The ledger is authoritative; legacy JSON recovery cannot overwrite it.");
            Require(Error.Length > 0, "Backup recovery is only available after a load failure.");
            using var lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(_path))
            {
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(_path));
                    if (document.RootElement.TryGetProperty("SchemaVersion", out var version))
                        Require(version.GetInt32() <= 3, "This file belongs to a newer schema; do not downgrade it using an old backup.");
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
    public Guid CreateCalculated(List<IndustryProjectNode> nodes, ISet<long> linked)
    {
        Guid id = Guid.NewGuid();
        // Copy caller-owned nodes before they enter persistent state.
        var copy = JsonSerializer.Deserialize<List<IndustryProjectNode>>(JsonSerializer.Serialize(nodes))!;
        Change(s =>
        {
            Linked(s.DefaultLeadId, linked); Linked(s.DefaultBuyerId, linked); Linked(s.DefaultSellerId, linked);
            foreach (var node in copy) Linked(node.ExecutorId, linked);
            s.Projects.Add(new() { Id = id, Name = copy.Single(n => n.ParentId == null).Name,
                LeadId = s.DefaultLeadId, BuyerId = s.DefaultBuyerId, SellerId = s.DefaultSellerId, Nodes = copy });
        }, "Created manufacturing dependency plan with catalog provenance and explicit blueprint assumptions.", id);
        return id;
    }
    public void ExpandDraft(Guid projectId, List<IndustryProjectNode> nodes)
    {
        var copy = JsonSerializer.Deserialize<List<IndustryProjectNode>>(JsonSerializer.Serialize(nodes))!;
        Change(s =>
        {
            var p = Project(s, projectId);
            Require(!p.Archived && p.Nodes.Count == 1 && !s.Reservations.Any(r => r.ProjectId == projectId),
                "Only an unreserved single-node draft can be expanded. Existing component plans are preserved; create a new plan to recalculate.");
            var old = p.Nodes.Single(); var root = copy.Single(n => n.ParentId == null); var generatedId = root.Id;
            root.Id = old.Id; root.ExecutorId = old.ExecutorId; root.Notes = old.Notes;
            foreach (var child in copy.Where(n => n.ParentId == generatedId)) child.ParentId = root.Id;
            p.Nodes = copy;
            if (p.Name == old.Name) p.Name = root.Name;
        }, "Expanded draft into a manufacturing material tree; existing root identity and project ownership retained.", projectId);
    }
    public Guid AddComponent(Guid projectId, Guid parentId, int typeId, long quantity)
    {
        Guid id = Guid.NewGuid();
        Change(s =>
        {
            var p = Project(s, projectId); Require(!p.Archived, "Reopen the project before editing.");
            Require(p.Nodes.Any(n => n.Id == parentId), "Parent component was not found.");
            Require(p.Nodes.Single(n => n.Id == parentId).CalculationSource.Length == 0,
                "Calculated material trees cannot accept manual extra inputs. Create a manual draft for custom requirements.");
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
            Require(!archived || !s.Reservations.Any(r => r.ProjectId == id && r.DeliveredQuantity != r.Quantity), "Confirm delivery or release outstanding reservations before archiving. Delivered stock remains held for this project.");
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
            Require(node.CalculationSource.Length == 0 || node.Quantity == quantity,
                "Calculated quantities are linked to the material tree. Create a new blueprint plan for a different batch quantity.");
            Require(!node.PlannedJobId.HasValue || (strategy == "Make" && executor.HasValue), "Remove the planned job before changing sourcing or clearing its executor.");
            Require(node.ActualJob == null || (strategy == "Make" && executor == node.ActualJob.InstallerId), "Unlink the EVE job before changing its executor or sourcing.");
            node.Quantity = quantity; node.ExecutorId = executor; node.Strategy = strategy; node.Notes = notes;
        }, $"Edited component {nodeId}: planned quantity {quantity}, executor {executor}, strategy {strategy}.", projectId);
    }
    public void AssignPlannedJob(Guid projectId, Guid nodeId, long executor, long? facility, ISet<long> linked)
    {
        Linked(executor, linked);
        Change(s =>
        {
            var p = Project(s, projectId); var node = p.Nodes.Single(n => n.Id == nodeId);
            Require(!p.Archived && !p.Paused, "Resume the project before assigning a job.");
            Require(node.Strategy == "Make" && node.BlueprintTypeId.HasValue && node.PlannedRuns > 0,
                "Select a calculated Make component with a manufacturing recipe.");
            Require(facility == null || facility > 0, "Enter a positive facility ID or leave it blank.");
            Require(node.ActualJob == null || node.ActualJob.InstallerId == executor, "Unlink the EVE job before changing its executor.");
            node.ExecutorId = executor; node.PlannedJobId ??= Guid.NewGuid();
            node.PlannedJobFacilityId = facility; node.PlannedJobAssignedUtc = DateTimeOffset.UtcNow;
        }, $"Assigned local manufacturing plan for component {nodeId} to {executor}; facility {facility}. No EVE job submitted.", projectId);
    }
    public void RemovePlannedJob(Guid projectId, Guid nodeId) => Change(s =>
    {
        var node = Project(s, projectId).Nodes.Single(n => n.Id == nodeId);
        node.PlannedJobId = null; node.PlannedJobFacilityId = null; node.PlannedJobAssignedUtc = null;
    }, $"Removed local job assignment for component {nodeId}; history retained.", projectId);
    public void SelectBlueprint(Guid projectId, Guid nodeId, IndustryBlueprintLibrary.Blueprint blueprint)
    {
        Change(s =>
        {
            var p = Project(s, projectId); var node = p.Nodes.Single(n => n.Id == nodeId);
            Require(!p.Archived && !p.Paused && node.Strategy == "Make", "Select an active Make component.");
            Require(node.BlueprintTypeId == blueprint.TypeId, "Select a blueprint for this component.");
            var affected = new HashSet<Guid> { nodeId };
            bool added; do { added = false; foreach (var n in p.Nodes.Where(n => n.ParentId.HasValue && affected.Contains(n.ParentId.Value))) added |= affected.Add(n.Id); } while (added);
            Require(!s.Reservations.Any(r => affected.Contains(r.NodeId)) && !p.Nodes.Any(n => affected.Contains(n.Id) && (n.ActualJob != null || n.PlannedJobId != null)),
                "Release reservations and job associations for this component's subtree before recalculating. Existing work is preserved.");
            Require(!p.Nodes.Any(n => n.Id != nodeId && affected.Contains(n.Id) && (n.ExecutorId != null || n.Notes.Length > 0 || n.TransferNeeded || n.TransferNote.Length > 0 || n.BlueprintItemId != null || n.Strategy != (n.BlueprintTypeId.HasValue ? "Make" : "Buy"))),
                "This subtree has configured child work. Preserve it by creating a separate batch instead of replacing its inputs.");
            var replacement = IndustryDependencyPlanner.Build(node.TypeId, node.Quantity, blueprint);
            var root = replacement.Single(n => n.ParentId == null); var generatedId = root.Id;
            root.Id = node.Id; root.ParentId = node.ParentId; root.Notes = node.Notes; root.TransferNeeded = node.TransferNeeded; root.TransferNote = node.TransferNote;
            foreach (var child in replacement.Where(n => n.ParentId == generatedId)) child.ParentId = root.Id;
            int index = p.Nodes.IndexOf(node); p.Nodes.RemoveAll(n => affected.Contains(n.Id)); p.Nodes.InsertRange(index, replacement);
        }, $"Selected blueprint {blueprint.ItemId} owned by {blueprint.Owner}; recalculated component inputs at ME {blueprint.ME}%.", projectId);
    }
    public void MatchJob(Guid projectId, Guid nodeId, IndustryJobLink job, ISet<long> linked, DateTimeOffset now)
    {
        Linked(job.OwnerId, linked);
        Change(s =>
        {
            var p = Project(s, projectId); var node = p.Nodes.Single(n => n.Id == nodeId);
            Require(!p.Archived && !p.Paused, "Resume the project before matching jobs.");
            Require(node.Strategy == "Make" && node.TypeId == job.ProductTypeId && node.BlueprintTypeId == job.BlueprintTypeId,
                "The manufacturing job must match this product and blueprint type.");
            Require(IndustryJobMatching.Valid(job) && IndustryWorkspaceInventory.Recent(job.SnapshotUtc, now), "A recent complete job observation is required.");
            Require(node.ExecutorId == null || node.ExecutorId == job.InstallerId, "Choose the job's executing character before matching.");
            Require(node.BlueprintItemId == null || node.BlueprintItemId == job.BlueprintItemId, "This job used a different blueprint instance.");
            Require(!s.Projects.SelectMany(x => x.Nodes).Any(n => n.Id != nodeId && IndustryJobMatching.LinkedJobs(n).Any(j => j.JobId == job.JobId)),
                "This EVE job is already linked to another component. Unlink it there first.");
            var existing = IndustryJobMatching.LinkedJobs(node).FirstOrDefault(j => j.JobId == job.JobId);
            Require(existing == null || (IndustryJobMatching.SameIdentity(existing, job) && job.SnapshotUtc >= existing.SnapshotUtc),
                "This job's identity changed or its observation is older than the saved evidence. Reload and review it.");
            if (node.ActualJob == null || node.ActualJob.JobId == job.JobId) node.ActualJob = job;
            else
            {
                node.AdditionalJobs.RemoveAll(j => j.JobId == job.JobId);
                node.AdditionalJobs.Add(job);
                s.SchemaVersion = 3;
            }
            node.ExecutorId = job.InstallerId;
        }, $"Manually matched EVE job {job.JobId} to component {nodeId}; no stock, consumption or transfer inferred. Evidence: {JsonSerializer.Serialize(job)}", projectId);
    }
    public void UnmatchJob(Guid projectId, Guid nodeId, long? jobId = null) => Change(s =>
    {
        var project = Project(s, projectId);
        Require(!project.Archived, "Reopen the project before unlinking a job.");
        var node = project.Nodes.Single(n => n.Id == nodeId);
        var jobs = IndustryJobMatching.LinkedJobs(node).ToList();
        Require(jobId.HasValue || jobs.Count <= 1, "Select the batch to unlink; other batches will be retained.");
        long? selected = jobId ?? node.ActualJob?.JobId;
        Require(selected == null || jobs.Any(j => j.JobId == selected), "The selected job is not linked to this component.");
        jobs.RemoveAll(j => j.JobId == selected);
        node.ActualJob = jobs.FirstOrDefault();
        node.AdditionalJobs = jobs.Skip(1).ToList();
    }, "Removed selected EVE job association; other batches and previous evidence retained.", projectId);
    public void MarkTransfer(Guid projectId, Guid nodeId, bool needed, string note) => Change(s =>
    {
        var p = Project(s, projectId); Require(!p.Archived, "Reopen the project before editing.");
        var n = p.Nodes.Single(n => n.Id == nodeId); n.TransferNeeded = needed; n.TransferNote = note.Trim();
    }, $"Transfer requirement for component {nodeId}: {needed}. {note}", projectId);
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
    public void ConfirmDelivery(Guid reservationId, IndustryStockSource destination, long quantity,
        string note, DateTimeOffset now, IReadOnlyList<IndustryStockSource> currentStock)
    {
        Guid projectId = Snapshot().Reservations.Single(r => r.Id == reservationId).ProjectId;
        Change(s =>
        {
            var reservation = s.Reservations.Single(r => r.Id == reservationId);
            var project = Project(s, projectId);
            Require(!project.Archived && !project.Paused, "Resume the project before confirming delivery.");
            Require(quantity > 0 && quantity <= reservation.Quantity - reservation.DeliveredQuantity,
                "Choose a quantity no greater than the remaining reserved delivery.");
            Require(!string.IsNullOrWhiteSpace(note), "Describe the destination or handoff you have checked.");
            Require(currentStock.Contains(destination) && destination.Verified &&
                IndustryWorkspaceInventory.Recent(destination.SnapshotUtc, now), "Refresh inventory and select the observed destination stock.");
            Require(destination.TypeId == reservation.TypeId && destination.OwnerId > 0 && destination.ItemId > 0 && destination.LocationId > 0,
                "Destination stock must match the reserved component.");
            bool same = reservation.OwnerId == destination.OwnerId && reservation.ItemId == destination.ItemId &&
                reservation.LocationId == destination.LocationId && reservation.LocationFlag == destination.LocationFlag && reservation.Path == destination.Path;
            Require(same || reservation.DeliveredQuantity == 0 || reservation.OwnerId != destination.OwnerId || reservation.ItemId != destination.ItemId,
                "Undo the earlier confirmation before moving the same confirmed stack again.");
            Require(!s.Reservations.Where(r => r.TypeId == destination.TypeId && r.Id != reservationId).Any(r =>
                !IndustryWorkspaceInventory.Review(r, s.Reservations, currentStock, now).StartsWith("RESERVED", StringComparison.Ordinal)),
                "Review other missing or moved reservations for this material before confirming a handoff.");
            decimal allocated = s.Reservations.Where(r => r.OwnerId == destination.OwnerId && r.ItemId == destination.ItemId && (same || r.Id != reservationId)).Sum(r => (decimal)r.Quantity);
            Require(same ? allocated <= destination.Quantity : quantity <= destination.Quantity - allocated,
                "Destination stock is insufficient or already reserved for another build.");
            if (same)
            {
                reservation.DeliveredQuantity += quantity;
                reservation.DeliveryConfirmedUtc = now;
                reservation.DeliveryNote = note.Trim();
            }
            else
            {
                // Move the reservation atomically: the same physical quantity cannot remain held at both ends.
                reservation.Quantity -= quantity;
                if (reservation.Quantity == 0) s.Reservations.Remove(reservation);
                s.Reservations.Add(new IndustryReservation
                {
                    ProjectId = projectId, NodeId = reservation.NodeId, TypeId = destination.TypeId,
                    OwnerId = destination.OwnerId, ItemId = destination.ItemId, LocationId = destination.LocationId,
                    LocationFlag = destination.LocationFlag, Path = destination.Path, Quantity = quantity,
                    SnapshotUtc = destination.SnapshotUtc, DeliveredQuantity = quantity, DeliveryConfirmedUtc = now,
                    DeliveryNote = note.Trim()
                });
            }
            s.SchemaVersion = Math.Max(s.SchemaVersion, 2); // Older clients must refuse edits rather than discard delivery evidence.
        }, $"Confirmed {quantity:N0} delivered to {destination.Owner}. {note.Trim()} Stock remains reserved; consumption is not inferred.", projectId);
    }
    public void UndoDelivery(Guid reservationId)
    {
        Guid projectId = Snapshot().Reservations.Single(r => r.Id == reservationId).ProjectId;
        Change(s =>
        {
            Require(!Project(s, projectId).Archived, "Reopen the project before correcting delivery.");
            var reservation = s.Reservations.Single(r => r.Id == reservationId);
            Require(reservation.DeliveredQuantity > 0, "Select a reservation with confirmed delivery.");
            reservation.DeliveredQuantity = 0; reservation.DeliveryConfirmedUtc = null; reservation.DeliveryNote = "";
        }, "Delivery confirmation undone. Stock remains reserved at its recorded destination.", projectId);
    }

    public void DeleteProject(Guid projectId, bool releaseReservations = false)
    {
        Change(s =>
        {
            var project = Project(s, projectId);
            Require(releaseReservations || !s.Reservations.Any(r => r.ProjectId == projectId), "Release this project's stock reservations before deleting it.");
            s.Events.Add(new() { ProjectId = projectId, Description = "Deleted project snapshot: " + JsonSerializer.Serialize(project) + "; released reservations: " + JsonSerializer.Serialize(s.Reservations.Where(r => r.ProjectId == projectId).ToArray()) });
            s.Reservations.RemoveAll(r => r.ProjectId == projectId);
            s.Projects.Remove(project);
        }, "Deleted project and its component plan; audit history retained.", projectId);
    }
    public void Release(Guid reservationId)
    {
        Guid projectId = Snapshot().Reservations.Single(r => r.Id == reservationId).ProjectId;
        Change(s => { var r = s.Reservations.Single(r => r.Id == reservationId); Require(r.DeliveredQuantity == 0, "Undo delivery confirmation before releasing this stock."); s.Reservations.Remove(r); }, "Released reservation " + reservationId + "; history retained.", projectId);
    }
    private static IndustryProject Project(IndustryWorkspace state, Guid id) => state.Projects.Single(p => p.Id == id);
    internal static void Validate(IndustryWorkspace state)
    {
        Require(state.SchemaVersion is 1 or 2 or 3, "Unsupported planning schema; use a compatible application version.");
        Require(state.Revision >= 0, "Invalid planning revision.");
        Require(state.Projects.Select(p => p.Id).Distinct().Count() == state.Projects.Count, "Duplicate project IDs.");
        var allNodes = state.Projects.SelectMany(p => p.Nodes).ToArray();
        Require(allNodes.Select(n => n.Id).Distinct().Count() == allNodes.Length, "Duplicate node IDs.");
        Require(allNodes.All(n => n.AdditionalJobs != null && (n.AdditionalJobs.Count == 0 || (state.SchemaVersion >= 3 && n.ActualJob != null))), "Invalid manufacturing batch collection.");
        var actualJobs = allNodes.SelectMany(n => IndustryJobMatching.LinkedJobs(n).Select(j => (Node: n, Job: j))).ToArray();
        Require(actualJobs.Select(x => x.Job.JobId).Distinct().Count() == actualJobs.Length, "Duplicate EVE job links.");
        Require(actualJobs.All(x => IndustryJobMatching.Valid(x.Job) && x.Node.TypeId == x.Job.ProductTypeId &&
            x.Node.BlueprintTypeId == x.Job.BlueprintTypeId && x.Node.ExecutorId == x.Job.InstallerId && x.Node.Strategy == "Make"), "Invalid EVE job association.");
        var plannedJobs = allNodes.Where(n => n.PlannedJobId.HasValue).ToArray();
        Require(plannedJobs.Select(n => n.PlannedJobId).Distinct().Count() == plannedJobs.Length,
            "Duplicate planned job IDs.");
        Require(plannedJobs.All(n => n.PlannedJobId != Guid.Empty && n.ExecutorId > 0 && n.PlannedRuns > 0 && n.BlueprintTypeId > 0 && n.Strategy == "Make"),
            "Planned jobs need a Make recipe, positive runs and an assigned executor.");
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
        foreach (var group in state.Projects.Where(p => !p.Archived).SelectMany(p => p.Nodes)
            .Where(n => n.BlueprintItemId.HasValue && n.BlueprintRunsAvailable.HasValue)
            .GroupBy(n => (n.BlueprintOwnerId, n.BlueprintItemId)))
            Require(group.Sum(n => (decimal)(n.PlannedRuns ?? 0)) <= group.Min(n => n.BlueprintRunsAvailable!.Value),
                "This blueprint copy's runs are already planned in another project. Archive or delete the earlier plan before reusing those runs.");
        Require(state.Reservations.Select(r => r.Id).Distinct().Count() == state.Reservations.Count, "Duplicate reservation IDs.");
        foreach (var r in state.Reservations)
        {
            Require(r.DeliveredQuantity >= 0 && r.DeliveredQuantity <= r.Quantity && (r.DeliveredQuantity == 0 || (state.SchemaVersion >= 2 && r.DeliveryConfirmedUtc.HasValue && !string.IsNullOrWhiteSpace(r.DeliveryNote))), "Invalid delivery confirmation.");
            Require(r.Quantity > 0 && r.OwnerId > 0 && r.ItemId > 0 && state.Projects.Any(p => p.Id == r.ProjectId && p.Nodes.Any(n => n.Id == r.NodeId && n.TypeId == r.TypeId)), "Invalid reservation reference.");
        }
    }
}
