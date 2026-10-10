using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class IndustryWorkspaceView : System.Windows.Controls.UserControl
{
    private IndustryWorkspaceStore _store = null!;
    private Func<IEnumerable<IndustryPilot>> _inventory = null!;
    private IndustryWorkspace _state = new();
    private List<EvePilotProfile> _linked = new();
    private IReadOnlyList<IndustryStockSource> _stock = Array.Empty<IndustryStockSource>();
    private Guid? _projectId, _nodeId;
    private bool _loading;
    private IReadOnlyList<IndustryBlueprintLibrary.Blueprint> _blueprints = Array.Empty<IndustryBlueprintLibrary.Blueprint>();
    private bool _initialized;
    private readonly Dictionary<(long Owner, long Location), string> _displayPaths = new();
    private string DisplayPath(long owner, long location)
    {
        if (!_displayPaths.TryGetValue((owner, location), out var path))
            _displayPaths[(owner, location)] = path = IndustryDisplayNames.Path(_inventory().FirstOrDefault(p => p.Id == owner), location);
        return path;
    }
    private Func<Task<List<EvePilotProfile>>>? _loadLinked;
    private HashSet<long> LinkedIds => _linked.Select(p => p.CharacterId).ToHashSet();
    private IReadOnlyList<IndustryStockSource> ReadStock() => IndustryWorkspaceInventory.Read(_inventory(), DateTimeOffset.UtcNow,
        _linked.Where(p => p.Scopes.Contains("esi-assets.read_assets.v1")).Select(p => p.CharacterId).ToHashSet());
    public IndustryWorkspaceView()
    {
        InitializeComponent();
        Strategy.ItemsSource = new[] { "Make", "Buy", "Use Stock" };
        Loaded += async (_, _) =>
        {
            if (_initialized) return;
            try
            {
                var ops = BackgroundOperations.Current;
                _loadLinked = async () => (await ops.Sso.LoadPilotsAsync()).ToList();
                Initialize(ops.IndustryWorkspace, () => ops.Industry.State.Pilots, await _loadLinked());
            }
            catch (Exception ex) { Message.Text = "Planning unavailable: " + ex.Message; }
        };
    }
    internal void Initialize(IndustryWorkspaceStore store, Func<IEnumerable<IndustryPilot>> inventory, List<EvePilotProfile> linked)
    { _store = store; _inventory = inventory; _linked = linked; _initialized = true; RefreshAll(); }
    private void Action(Action action, string message)
    {
        try { action(); RefreshAll(); Message.Text = message; Message.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(118, 215, 203)); }
        catch (Exception ex) { Message.Text = "Not saved: " + ex.Message; Message.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 174, 159)); }
    }
    private string ProductName(int typeId, string fallback) => _inventory().Select(p => p.TypeNames.GetValueOrDefault(typeId)).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? fallback;
    private string Pilot(long? id) => !id.HasValue ? "Unassigned" : _linked.FirstOrDefault(p => p.CharacterId == id)?.CharacterName ?? $"Unlinked character {id}";
    private List<PilotOption> Options()
    {
        var ids = _linked.Select(p => (long?)p.CharacterId)
            .Concat(new[] { _state.DefaultLeadId, _state.DefaultBuyerId, _state.DefaultSellerId })
            .Concat(_state.Projects.SelectMany(p => new[] { p.LeadId, p.BuyerId, p.SellerId }.Concat(p.Nodes.Select(n => n.ExecutorId))));
        return new[] { new PilotOption(null, "Unassigned") }.Concat(ids.Where(id => id.HasValue).Distinct().Select(id => new PilotOption(id, Pilot(id))).OrderBy(p => p.Name)).ToList();
    }
    private static long? Choice(System.Windows.Controls.ComboBox box) => (box.SelectedItem as PilotOption)?.Id;
    private void RefreshAll()
    {
        _loading = true;
        try
        {
            _displayPaths.Clear();
            _state = _store.Snapshot(); _stock = ReadStock();
            long? blueprintOwner = Choice(BlueprintOwner);
            BlueprintOwner.ItemsSource = _linked.Select(p => new PilotOption(p.CharacterId, p.CharacterName)).ToArray();
            Select(BlueprintOwner, blueprintOwner ?? _state.DefaultLeadId);
            if (BlueprintOwner.SelectedItem == null && BlueprintOwner.Items.Count > 0) BlueprintOwner.SelectedIndex = 0;
            _blueprints = IndustryBlueprintLibrary.Read(_inventory(), _linked.Where(p => p.Scopes.Contains("esi-characters.read_blueprints.v1")).Select(p => p.CharacterId).ToHashSet(), DateTimeOffset.UtcNow);
            Shopping.ItemsSource = IndustryShoppingList.Build(_state, _stock, DateTimeOffset.UtcNow);
            RecoverButton.Visibility = _store.Error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            Message.Text = _store.Error.Length > 0 ? _store.Error : "Draft planning only. Refresh Industry to update source snapshots, then reload here. No purchases, jobs or deliveries are inferred.";
            foreach (var box in new[] { DefaultLead, DefaultBuyer, DefaultSeller, ProjectLead, ProjectBuyer, ProjectSeller, Executor }) box.ItemsSource = Options();
            Select(DefaultLead, _state.DefaultLeadId); Select(DefaultBuyer, _state.DefaultBuyerId); Select(DefaultSeller, _state.DefaultSellerId);
            Roles.ItemsSource = _linked.Select(p => new RoleRow(p.CharacterId, p.CharacterName, _state.Roles.GetValueOrDefault(p.CharacterId))).ToList();
            Projects.ItemsSource = _state.Projects.Where(p => ShowArchived.IsChecked == true || !p.Archived)
                .OrderBy(p => p.CreatedUtc).Select(p => new ProjectRow(p.Id, p.Name, $"{(p.Archived ? "Archived" : p.Paused ? "Paused" : "Draft")} | {p.Nodes.Count} nodes | Lead: {Pilot(p.LeadId)}")).ToArray();
            Projects.SelectedItem = Projects.Items.Cast<ProjectRow>().FirstOrDefault(p => p.Id == _projectId) ?? Projects.Items.Cast<ProjectRow>().FirstOrDefault();
            _projectId = (Projects.SelectedItem as ProjectRow)?.Id;
        }
        finally { _loading = false; }
        RefreshBlueprintLocations();
        ShowProject();
    }
    private static void Select(System.Windows.Controls.ComboBox box, long? id) => box.SelectedItem = box.Items.Cast<PilotOption>().FirstOrDefault(p => p.Id == id);
    private void Project_Selected(object sender, SelectionChangedEventArgs e)
    { if (_loading) return; _projectId = (Projects.SelectedItem as ProjectRow)?.Id; _nodeId = null; ShowProject(); WorkspaceTabs.SelectedItem = ProjectDetailTab; EditorScroll.ScrollToTop(); }
    private void ShowProject()
    {
        var project = _state.Projects.FirstOrDefault(p => p.Id == _projectId);
        ProjectEditor.Visibility = project == null ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceEmpty.Visibility = project == null ? Visibility.Visible : Visibility.Collapsed;
        if (project == null) { Nodes.ItemsSource = null; return; }
        ConfirmDelete.IsChecked = false; DeletePrompt.Visibility = Visibility.Collapsed;
        DeleteSummary.Text = $"Delete {project.Name}? This releases {_state.Reservations.Count(r => r.ProjectId == project.Id)} reservations and removes the local plan. Audit history is retained. EVE jobs and assets are unchanged.";
        ProjectIdentity.Text = project.Name; ProjectIdentity.ToolTip = project.Id.ToString(); ProjectName.Text = project.Name; ProjectNotes.Text = project.Notes;
        Select(ProjectLead, project.LeadId); Select(ProjectBuyer, project.BuyerId); Select(ProjectSeller, project.SellerId);
        Paused.IsChecked = project.Paused; Archived.IsChecked = project.Archived;
        if (!project.Nodes.Any(n => n.Id == _nodeId)) _nodeId = project.Nodes.Single(n => n.ParentId == null).Id;
        var expanded = new HashSet<Guid>();
        void Remember(NodeRow row) { if (row.Expanded) expanded.Add(row.Id); foreach (var child in row.Children) Remember(child); }
        foreach (var row in Nodes.Items.OfType<NodeRow>()) Remember(row);
        var cursor = project.Nodes.FirstOrDefault(n => n.Id == _nodeId);
        while (cursor != null) { expanded.Add(cursor.Id); cursor = project.Nodes.FirstOrDefault(n => n.Id == cursor.ParentId); }
        NodeRow Row(IndustryProjectNode n) => new(n.Id, $"{ProductName(n.TypeId, n.Name)} x {n.Quantity:N0}",
            $"{n.Strategy} | {Pilot(n.ExecutorId)}{(n.ActualJob != null ? " | EVE job linked" : n.PlannedJobId.HasValue ? " | Planned" : "")}",
            project.Nodes.Where(child => child.ParentId == n.Id).Select(Row).ToList(), n.TypeId, n.Strategy, n.CalculationNote, n.Id == _nodeId) { Expanded = n.ParentId == null || expanded.Contains(n.Id) };
        Nodes.ItemsSource = project.Nodes.Where(n => n.ParentId == null).Select(Row).ToList();

        Audit.Text = string.Join(Environment.NewLine, _state.Events.Where(e => e.ProjectId == project.Id || e.ProjectId == null).OrderByDescending(e => e.TimeUtc).Take(100).Select(e => $"{e.TimeUtc:u} {e.Description}"));
        ShowNode();
    }
    private void Node_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    { if (e.NewValue is NodeRow node) { _nodeId = node.Id; ShowNode(); WorkspaceTabs.SelectedItem = ProjectDetailTab; EditorScroll.ScrollToTop(); } }
    private IndustryProjectNode? Node => _state.Projects.FirstOrDefault(p => p.Id == _projectId)?.Nodes.FirstOrDefault(n => n.Id == _nodeId);
    private void ShowNode()
    {
        var node = Node; NodeEditor.Visibility = node == null ? Visibility.Collapsed : Visibility.Visible; if (node == null) return;
        NodeTitle.Text = ProductName(node.TypeId, node.Name);
        NodeIcon.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri($"https://images.evetech.net/types/{node.TypeId}/icon?size=64"));
        NodeSummary.Text = $"{node.Quantity:N0} required  |  {node.Strategy}  |  {Pilot(node.ExecutorId)}"; NodeTitle.ToolTip = node.Id.ToString(); NodeQuantity.Text = node.Quantity.ToString(); NodeNotes.Text = node.Notes;
        Select(Executor, node.ExecutorId); Strategy.SelectedItem = node.Strategy;
        NodeQuantity.IsReadOnly = node.CalculationSource.Length > 0;
        Eligibility.Text = node.CalculationSource.Length == 0
            ? "Manual draft. Generate a material tree or create a plan from the blueprint library."
            : node.CalculationNote + (node.BlueprintItemId.HasValue ? $" | Blueprint {node.BlueprintItemId}, owner {node.BlueprintOwnerId}, location {node.BlueprintLocationId}" : "") + " | Skills/facility eligibility and delivery require verification.";
        if (node.ExecutorId.HasValue && !LinkedIds.Contains(node.ExecutorId.Value)) Eligibility.Text = "Assigned toon is no longer linked; assignment retained for review. " + Eligibility.Text;
        ManualChildControls.Visibility = node.CalculationSource.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlannedJobPanel.Visibility = node.Strategy == "Make" && node.PlannedRuns > 0 ? Visibility.Visible : Visibility.Collapsed;
        var facilities = new[] { new LocationOption(null, "Unassigned location") }.Concat(_inventory().SelectMany(p => p.LocationNames).GroupBy(k => k.Key).Select(g => new LocationOption(g.Key, g.First().Value)).OrderBy(x => x.Name)).ToList();
        if (node.PlannedJobFacilityId is {} savedFacility && !facilities.Any(f => f.Id == savedFacility)) facilities.Add(new(savedFacility, "Saved location (name unavailable)"));
        JobFacilityPicker.ItemsSource = facilities;
        JobFacilityPicker.SelectedItem = JobFacilityPicker.Items.Cast<LocationOption>().FirstOrDefault(x => x.Id == node.PlannedJobFacilityId) ?? JobFacilityPicker.Items[0];
        JobFacility.Text = node.PlannedJobFacilityId?.ToString() ?? "";
        PlannedJobStatus.ToolTip = node.PlannedJobId?.ToString();
        PlannedJobStatus.Text = node.PlannedJobId.HasValue
            ? $"PLANNED | {Pilot(node.ExecutorId)} | {node.PlannedRuns:N0} runs | {node.PlannedOutput:N0} output units"
            : "No planned job assigned. Select an executing toon, then create the plan.";
        RefreshComponentDetails(node);
        StockOwner.ItemsSource = new[] { new PilotOption(null, "All personal stock owners") }.Concat(_stock.Where(s => s.TypeId == node.TypeId).GroupBy(s => s.OwnerId).Select(g => new PilotOption(g.Key, g.First().Owner))).ToArray();
        StockOwner.SelectedIndex = 0; RefreshStockLocations();
        RefreshStock();
        Reservations.ItemsSource = _state.Reservations.Where(r => r.NodeId == node.Id).Select(r => new ReservationRow(r.Id, r.Quantity,
            $"{Pilot(r.OwnerId)} / {DisplayPath(r.OwnerId, r.LocationId)}", IndustryWorkspaceInventory.Review(r, _state.Reservations, _stock, DateTimeOffset.UtcNow))).ToArray();
        decimal reserved = _state.Reservations.Where(r => r.NodeId == node.Id).Sum(r => (decimal)r.Quantity);
        Coverage.Text = $"Required {node.Quantity:N0} | Reserved {reserved:N0} | Unreserved {node.Quantity - reserved:N0}. Orders, purchases, manufactured output, delivery and consumption are not reconciled in this increment.";
    }
    private IReadOnlyList<IndustryJobLink> ReadJobs() => IndustryJobMatching.Read(_inventory(), _linked.Where(p => p.Scopes.Contains("esi-industry.read_character_jobs.v1")).Select(p => p.CharacterId).ToHashSet());
    private void RefreshComponentDetails(IndustryProjectNode node)
    {
        var project = _state.Projects.Single(p => p.Id == _projectId);
        var children = project.Nodes.Where(n => n.ParentId == node.Id).ToArray();
        ComponentInputs.ItemsSource = children.Select(n => new InputRow(n.Id, ProductName(n.TypeId, n.Name), n.TypeId, n.Quantity, n.Strategy,
            _state.Reservations.Where(r => r.NodeId == n.Id).Sum(r => (decimal)r.Quantity),
            _stock.Any(s => s.TypeId == n.TypeId && s.Verified) ? _stock.Where(s => s.TypeId == n.TypeId && s.Verified).Sum(s => Math.Max(0, s.Quantity - _state.Reservations.Where(r => r.OwnerId == s.OwnerId && r.ItemId == s.ItemId).Sum(r => (decimal)r.Quantity))).ToString("N0") : "Unverified")).ToArray();
        InputsHint.Text = children.Length == 0 ? "No expanded manufacturing inputs. Choose stock or procurement for this item." : node.Strategy == "Make" ? "Select an input to inspect its own blueprint, stock and assignment. Free stock is shared across every project." : "These inputs are inactive while this component is sourced through " + node.Strategy + ".";
        ComponentBlueprintOwner.ItemsSource = new[] { new PilotOption(null, "All authorized owners") }.Concat(_linked.Select(p => new PilotOption(p.CharacterId, p.CharacterName))).ToArray();
        ComponentBlueprintOwner.SelectedIndex = 0;
        RefreshComponentBlueprintLocations();
        ComponentBlueprintHint.Text = node.BlueprintItemId.HasValue ? $"Selected instance | {Pilot(node.BlueprintOwnerId)} | ME {node.MaterialEfficiency}%" : "Select an owned instance to apply its ME and runs. Location and availability remain visible per copy.";
        var jobs = ReadJobs().Where(j => j.ProductTypeId == node.TypeId && j.BlueprintTypeId == node.BlueprintTypeId).OrderByDescending(j => j.StartUtc).ToArray();
        ExistingJobs.ItemsSource = jobs.Select(j => new JobRow(j, Pilot(j.InstallerId), FacilityName(j.OwnerId, j.FacilityId))).ToArray();
        ActualJobPanel.Visibility = node.Strategy == "Make" && node.BlueprintTypeId.HasValue ? Visibility.Visible : Visibility.Collapsed;
        ActualJobStatus.Text = node.ActualJob == null ? (jobs.Length == 0 ? "No matching jobs in the current authorized snapshots. Refresh Industry after starting the job in EVE." : "Candidates share this product and blueprint type. Inspect the evidence, then explicitly link your job.") :
            jobs.FirstOrDefault(j => j.JobId == node.ActualJob.JobId) is {} current && IndustryWorkspaceInventory.Recent(current.SnapshotUtc, DateTimeOffset.UtcNow) ? $"Linked | {Pilot(current.InstallerId)} | {IndustryJobMatching.Describe(current)}" : "Link retained | current job observation unavailable or stale. Completion and delivery are unverified.";
        JobEvidence.Text = "Select a row to review the blueprint, installer, facility and timing.";
        MatchJobButton.IsEnabled = false;
        TransferNeeded.IsChecked = node.TransferNeeded; TransferNote.Text = node.TransferNote;
    }
    private string FacilityName(long owner, long id) => _inventory().FirstOrDefault(p => p.Id == owner)?.LocationNames.GetValueOrDefault(id) ?? "Location name unavailable";
    private void Input_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ComponentInputs.SelectedItem is not InputRow input) return;
        _nodeId = input.Id; ShowProject(); EditorScroll.ScrollToTop();
    }
    private void Facility_Changed(object sender, SelectionChangedEventArgs e)
    { if (JobFacility != null) JobFacility.Text = (JobFacilityPicker.SelectedItem as LocationOption)?.Id?.ToString() ?? ""; }
    private void ComponentBlueprintOwner_Changed(object sender, SelectionChangedEventArgs e) => RefreshComponentBlueprintLocations();
    private void RefreshComponentBlueprintLocations()
    {
        if (ComponentBlueprintLocation == null || Node is not {} node) return;
        ComponentBlueprintLocation.ItemsSource = new[] { new LocationOption(null, "All locations / containers") }.Concat(_blueprints.Where(b => b.TypeId == node.BlueprintTypeId && (Choice(ComponentBlueprintOwner) == null || b.OwnerId == Choice(ComponentBlueprintOwner))).GroupBy(b => b.LocationId).Select(g => new LocationOption(g.Key, g.First().Location))).ToArray();
        ComponentBlueprintLocation.SelectedIndex = 0; FilterComponentBlueprints();
    }
    private void ComponentBlueprintLocation_Changed(object sender, SelectionChangedEventArgs e) => FilterComponentBlueprints();
    private void FilterComponentBlueprints()
    {
        if (ComponentBlueprints == null || Node is not {} node) return;
        var location = (ComponentBlueprintLocation.SelectedItem as LocationOption)?.Id;
        ComponentBlueprints.ItemsSource = _blueprints.Where(b => b.TypeId == node.BlueprintTypeId && (Choice(ComponentBlueprintOwner) == null || b.OwnerId == Choice(ComponentBlueprintOwner)) && (location == null || b.LocationId == location)).ToArray();
    }
    private void SelectComponentBlueprint_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        if (_projectId is not {} id || Node is not {} node || ComponentBlueprints.SelectedItem is not IndustryBlueprintLibrary.Blueprint bp)
            throw new InvalidOperationException("Select an owned blueprint instance first.");
        var current = IndustryBlueprintLibrary.Read(_inventory(), _linked.Where(p => p.Scopes.Contains("esi-characters.read_blueprints.v1")).Select(p => p.CharacterId).ToHashSet(), DateTimeOffset.UtcNow).FirstOrDefault(b => b.OwnerId == bp.OwnerId && b.ItemId == bp.ItemId);
        if (current == null || current != bp) throw new InvalidOperationException("Blueprint snapshot changed. Reload before choosing it.");
        _store.SelectBlueprint(id, node.Id, current);
    }, "Blueprint selected; component inputs recalculated. Existing project identity retained.");
    private void Job_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (ExistingJobs.SelectedItem is not JobRow row) { MatchJobButton.IsEnabled = false; return; }
        var j = row.Link;
        JobEvidence.Text = $"{IndustryCatalog.Name(j.BlueprintTypeId)} | instance {j.BlueprintItemId}\nInstaller: {row.Owner} | {row.Facility} (facility {j.FacilityId})\n{j.StartUtc.UtcDateTime:dd MMM HH:mm} to {j.EndUtc.UtcDateTime:dd MMM HH:mm} EVE | {j.Runs:N0} runs | job {j.JobId}\nObserved {j.SnapshotUtc.UtcDateTime:dd MMM HH:mm} EVE. Partial batches and surplus do not change reserved or delivered stock.";
        MatchJobButton.IsEnabled = IndustryWorkspaceInventory.Recent(j.SnapshotUtc, DateTimeOffset.UtcNow);
    }
    private void MatchJob_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        if (_projectId is not {} id || Node is not {} node || ExistingJobs.SelectedItem is not JobRow row) throw new InvalidOperationException("Select an EVE job first.");
        var latest = ReadJobs().FirstOrDefault(j => j.OwnerId == row.Link.OwnerId && j.JobId == row.Link.JobId);
        if (latest == null || latest != row.Link) throw new InvalidOperationException("Job evidence changed. Reload and review it before linking.");
        _store.MatchJob(id, node.Id, latest, LinkedIds, DateTimeOffset.UtcNow);
    }, "EVE job linked explicitly. Stock, consumption and delivery remain separate.");
    private void UnmatchJob_Click(object sender, RoutedEventArgs e) => Action(() => { if (_projectId is {} id && Node is {} node) _store.UnmatchJob(id, node.Id); }, "EVE job unlinked; history retained.");
    private void Transfer_Click(object sender, RoutedEventArgs e) => Action(() => { if (_projectId is {} id && Node is {} node) _store.MarkTransfer(id, node.Id, TransferNeeded.IsChecked == true, TransferNote.Text); }, "Transfer requirement saved; no delivery inferred.");
    private sealed record InputRow(Guid Id, string Name, int TypeId, long Quantity, string Strategy, decimal Reserved, string Free)
    { public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=32"; }
    private sealed record JobRow(IndustryJobLink Link, string Owner, string Facility)
    { public string Status => IndustryJobMatching.Describe(Link); public string End => Link.EndUtc.UtcDateTime.ToString("dd MMM HH:mm"); }
    private void StockOwner_Changed(object sender, SelectionChangedEventArgs e) => RefreshStockLocations();
    private void StockLocation_Changed(object sender, SelectionChangedEventArgs e) { if (_initialized) RefreshStock(); }
    private void RefreshStockLocations()
    {
        if (!_initialized || StockLocation == null || Node is not {} node) return;
        StockLocation.ItemsSource = new[] { new LocationOption(null, "All stations / containers") }.Concat(_stock.Where(s => s.TypeId == node.TypeId && (Choice(StockOwner) == null || s.OwnerId == Choice(StockOwner))).GroupBy(s => s.LocationId).Select(g => new LocationOption(g.Key, DisplayPath(g.First().OwnerId, g.Key)))).ToArray();
        StockLocation.SelectedIndex = 0; RefreshStock();
    }
    private void RefreshStock()
    {
        if (Node is not {} node) return;
        string search = StockSearch.Text.Trim();
        Stock.ItemsSource = _stock.Where(s => s.TypeId == node.TypeId && (Choice(StockOwner) == null || s.OwnerId == Choice(StockOwner)) && ((StockLocation.SelectedItem as LocationOption)?.Id == null || s.LocationId == ((LocationOption)StockLocation.SelectedItem).Id) && (search.Length == 0 || (s.Owner + " " + DisplayPath(s.OwnerId, s.LocationId) + " " + s.Path + " " + s.ItemId).Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new StockRow(s, DisplayPath(s.OwnerId, s.LocationId), Math.Max(0, s.Quantity - _state.Reservations.Where(r => r.OwnerId == s.OwnerId && r.ItemId == s.ItemId).Sum(r => (decimal)r.Quantity)))).ToArray();
    }
    private void StockSearch_Changed(object sender, TextChangedEventArgs e) { if (_initialized) RefreshStock(); }
    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        try { if (_loadLinked != null) _linked = await _loadLinked(); RefreshAll(); }
        catch (Exception ex) { Message.Text = "Could not reload linked characters: " + ex.Message; }
    }
    private void TypeSearch_Changed(object sender, TextChangedEventArgs e)
    { if (TypeResults != null) TypeResults.ItemsSource = IndustryCatalog.FindTypes(TypeSearch.Text); }
    private void UseProduct_Click(object sender, RoutedEventArgs e)
    {
        if (TypeResults.SelectedItem is not KeyValuePair<int, string> selected) return;
        NewType.Text = selected.Key.ToString(); if (string.IsNullOrWhiteSpace(NewName.Text)) NewName.Text = selected.Value;
    }
    private void UseComponent_Click(object sender, RoutedEventArgs e)
    { if (TypeResults.SelectedItem is KeyValuePair<int, string> selected) ChildType.Text = selected.Key.ToString(); }
    private void CopyShopping_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rows = Shopping.Items.Cast<IndustryShoppingList.Row>();
            System.Windows.Clipboard.SetText("Material\tType ID\tRequired\tReserved\tUnreserved\tFree observed\tProjects\tReview" + Environment.NewLine +
                string.Join(Environment.NewLine, rows.Select(r => $"{r.Name}\t{r.TypeId}\t{r.Required}\t{r.Reserved}\t{r.Unreserved}\t{r.FreeStock}\t{r.Projects}\t{r.Review}")));
            Message.Text = "Copied consolidated planned materials. Free stock still needs explicit reservation.";
        }
        catch (Exception ex) { Message.Text = "Could not copy materials: " + ex.Message; }
    }
    private void Recover_Click(object sender, RoutedEventArgs e) => Action(() => _store.RestoreBackup(), "Recovered the last good backup; the unreadable original was preserved. Review recent edits.");
    private static long Quantity(string value) => long.TryParse(value, out long q) && q > 0 ? q : throw new InvalidOperationException("Enter a positive whole quantity.");
    private static int Type(string value) => int.TryParse(value, out int id) && id > 0 ? id : throw new InvalidOperationException("Enter a positive EVE product type ID.");
    private void Defaults_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        Roles.CommitEdit(DataGridEditingUnit.Cell, true); Roles.CommitEdit(DataGridEditingUnit.Row, true);
        _store.SaveDefaults(Choice(DefaultLead), Choice(DefaultBuyer), Choice(DefaultSeller), Roles.Items.Cast<RoleRow>().ToDictionary(r => r.Id, r => r.Flags), LinkedIds);
    }, "Saved defaults and role preferences. Existing project ownership and component assignments were preserved.");
    private void Create_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        int type = Type(NewType.Text);
        if (IndustryCatalog.Recipes.Any(r => r.Blueprint == type && r.Activity == "manufacturing"))
            _projectId = _store.CreateCalculated(IndustryDependencyPlanner.Build(type, Quantity(NewQuantity.Text)), LinkedIds);
        else _projectId = _store.Create(NewName.Text, type, Quantity(NewQuantity.Text), null, LinkedIds);
        WorkspaceTabs.SelectedItem = ProjectDetailTab;
    }, "Project saved. Blueprint types are converted to their manufactured product; manual products can be expanded using GENERATE MATERIAL TREE.");
    public void CreateFromRecipe(IndustryRecipe recipe, int runs)
    {
        if (!_initialized) { Message.Text = "Open Production Queue once to load linked characters, then add the recipe again."; return; }
        Action(() =>
        {
            if (recipe.Activity != "manufacturing" || recipe.Products.Count != 1) throw new InvalidOperationException("Select a single-product manufacturing recipe.");
            _projectId = _store.CreateCalculated(IndustryDependencyPlanner.Build(recipe.Blueprint, checked((long)((decimal)recipe.Products.Single().Value * runs))), LinkedIds);
            WorkspaceTabs.SelectedItem = ProjectDetailTab;
        }, "Material tree generated at ME 0. Use the blueprint library to plan from a specific owned copy.");
    }
    private void RefreshBlueprintLocations()
    {
        if (!_initialized) return;
        long? previous = (BlueprintLocation.SelectedItem as LocationOption)?.Id;
        _loading = true;
        BlueprintLocation.ItemsSource = new[] { new LocationOption(null, "All locations / containers") }.Concat(_blueprints.Where(b => b.OwnerId == Choice(BlueprintOwner)).GroupBy(b => b.LocationId).Select(g => new LocationOption(g.Key, g.First().Location)).OrderBy(x => x.Name)).ToArray();
        BlueprintLocation.SelectedItem = BlueprintLocation.Items.Cast<LocationOption>().FirstOrDefault(x => x.Id == previous) ?? BlueprintLocation.Items[0];
        _loading = false;
        RefreshBlueprints();
    }
    private void RefreshBlueprints()
    {
        if (!_initialized || _loading) return;
        string query = BlueprintSearch.Text.Trim();
        long? location = (BlueprintLocation.SelectedItem as LocationOption)?.Id;
        var owned = _blueprints.Where(b => b.OwnerId == Choice(BlueprintOwner));
        var rows = owned.Where(b => (location == null || b.LocationId == location) &&
            (query.Length == 0 || b.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || b.ItemId.ToString() == query)).ToArray();
        Blueprints.ItemsSource = rows;
        BlueprintSummary.Text = $"{rows.Length:N0} of {owned.Count():N0} blueprint instances | Personal blueprint snapshots | source filters use stable location IDs.";
        if (!owned.Any()) BlueprintSummary.Text += " Refresh Industry with blueprint access for this toon; missing data is not confirmed empty inventory.";
    }
    private void BlueprintOwner_Changed(object sender, SelectionChangedEventArgs e) { if (!_loading) RefreshBlueprintLocations(); }
    private void BlueprintFilter_Changed(object sender, SelectionChangedEventArgs e) => RefreshBlueprints();
    private void BlueprintSearch_Changed(object sender, TextChangedEventArgs e) => RefreshBlueprints();
    private void CreateBlueprint_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        if (Blueprints.SelectedItem is not IndustryBlueprintLibrary.Blueprint selected) throw new InvalidOperationException("Select an owned manufacturing blueprint.");
        var latest = IndustryBlueprintLibrary.Read(_inventory(), _linked.Where(p => p.Scopes.Contains("esi-characters.read_blueprints.v1")).Select(p => p.CharacterId).ToHashSet(), DateTimeOffset.UtcNow)
            .FirstOrDefault(b => b.OwnerId == selected.OwnerId && b.ItemId == selected.ItemId);
        if (latest == null || latest != selected) throw new InvalidOperationException("Blueprint snapshot changed. Reload and select it again.");
        _projectId = _store.CreateCalculated(IndustryDependencyPlanner.Build(selected.TypeId, Quantity(BlueprintQuantity.Text), selected), LinkedIds);
        WorkspaceTabs.SelectedItem = ProjectDetailTab;
    }, "Production plan saved with components and material inputs. Review child blueprint and facility assumptions before manufacturing.");
    private void ExpandDraft_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        if (_projectId is not {} id) return;
        var root = _state.Projects.Single(p => p.Id == id).Nodes.Single(n => n.ParentId == null);
        _store.ExpandDraft(id, IndustryDependencyPlanner.Build(root.TypeId, root.Quantity));
    }, "Draft expanded at ME 0 with product and input quantities. Project identity retained; review blueprint assumptions.");
    private void ShowDelete_Click(object sender, RoutedEventArgs e)
    { DeletePrompt.Visibility = Visibility.Visible; DeletePrompt.IsExpanded = true; }
    private void DeleteProject_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        if (_projectId is not {} id || ConfirmDelete.IsChecked != true) throw new InvalidOperationException("Tick Confirm deletion for the selected project first.");
        _store.DeleteProject(id, releaseReservations: true); _projectId = null; _nodeId = null;
    }, "Project deleted. Audit history retained; no in-game assets or jobs changed.");
    private void PlanJob_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        if (_projectId is not {} id || Node is not {} node || Choice(Executor) is not {} executor)
            throw new InvalidOperationException("Select a component and executing toon first.");
        long? facility = string.IsNullOrWhiteSpace(JobFacility.Text) ? null : Quantity(JobFacility.Text);
        _store.AssignPlannedJob(id, node.Id, executor, facility, LinkedIds);
    }, "Manufacturing job plan assigned. Start the actual job in EVE; this is a local plan only.");
    private void RemovePlanJob_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        if (_projectId is {} id && Node is {} node) _store.RemovePlannedJob(id, node.Id);
    }, "Local job assignment removed; component and audit history retained.");
    private void SaveProject_Click(object sender, RoutedEventArgs e) => Action(() =>
    { if (_projectId is {} id) _store.EditProject(id, ProjectName.Text, Choice(ProjectLead), Choice(ProjectBuyer), Choice(ProjectSeller), Paused.IsChecked == true, Archived.IsChecked == true, ProjectNotes.Text, LinkedIds); }, "Project saved; all child assignments retained.");
    private void SaveNode_Click(object sender, RoutedEventArgs e) => Action(() =>
    { if (_projectId is {} id && Node is {} n) _store.EditNode(id, n.Id, Quantity(NodeQuantity.Text), Choice(Executor), Strategy.SelectedItem?.ToString() ?? "Make", NodeNotes.Text, LinkedIds); }, "Component assignment and planned quantity saved. Eligibility still requires review.");
    private void AddChild_Click(object sender, RoutedEventArgs e) => Action(() =>
    { if (_projectId is {} id && _nodeId is {} node) _nodeId = _store.AddComponent(id, node, Type(ChildType.Text), Quantity(ChildQuantity.Text)); }, "Child component saved under the same parent project.");
    private void Reserve_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        if (_projectId is not {} id || _nodeId is not {} node || Stock.SelectedItem is not StockRow selected) throw new InvalidOperationException("Select a matching stock row.");
        var currentStock = ReadStock();
        var latest = currentStock.FirstOrDefault(s => s.OwnerId == selected.Source.OwnerId && s.ItemId == selected.Source.ItemId);
        if (latest == null || latest.Path != selected.Source.Path || latest.LocationId != selected.Source.LocationId) throw new InvalidOperationException("Stock moved or disappeared. Reload and review the source.");
        _store.Reserve(id, node, latest, Quantity(ReserveQuantity.Text), DateTimeOffset.UtcNow, currentStock);
    }, "Stock reserved across all projects. Transfer and delivery remain unverified.");
    private void Release_Click(object sender, RoutedEventArgs e) => Action(() =>
    { if (Reservations.SelectedItem is not ReservationRow r) throw new InvalidOperationException("Select a reservation to release."); _store.Release(r.Id); }, "Reservation released; audit history retained.");

    private sealed record LocationOption(long? Id, string Name) { public override string ToString() => Name; }
    private sealed record PilotOption(long? Id, string Name) { public override string ToString() => Name; }
    private sealed record ProjectRow(Guid Id, string Name, string Detail);
    private sealed record NodeRow(Guid Id, string Title, string Detail, List<NodeRow> Children, int TypeId, string Strategy, string Calculation, bool Selected)
    {
        public bool Expanded { get; set; }
        public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=32";
        public string Color => Strategy == "Make" ? "#74D6C9" : Strategy == "Buy" ? "#80BFFF" : "#FFD166";
    }
    private sealed record ReservationRow(Guid Id, long Quantity, string Source, string Review);
    private sealed record StockRow(IndustryStockSource Source, string DisplayPath, decimal Available)
    {
        public string Free => Source.Verified ? Available.ToString("N0") : "Unverified";
        public string Owner => Source.Owner;
        public string Path => DisplayPath;
        public long ItemId => Source.ItemId;
        public long Quantity => Source.Quantity;
        public string Detail => Source.Detail + " | " + (Source.SnapshotUtc == default ? "No snapshot" : Source.SnapshotUtc.ToString("u"));
    }
    private sealed class RoleRow
    {
        public long Id { get; }
        public string Name { get; }
        public bool Make { get; set; } public bool Buy { get; set; } public bool Sell { get; set; }
        public bool Jobs { get; set; } public bool Market { get; set; } public bool Wallet { get; set; } public bool Dashboard { get; set; }
        public RoleRow(long id, string name, IndustryCharacterRole roles)
        { Id = id; Name = name; Make = roles.HasFlag(IndustryCharacterRole.Manufacturing); Buy = roles.HasFlag(IndustryCharacterRole.Buying); Sell = roles.HasFlag(IndustryCharacterRole.Selling); Jobs = roles.HasFlag(IndustryCharacterRole.Jobs); Market = roles.HasFlag(IndustryCharacterRole.Market); Wallet = roles.HasFlag(IndustryCharacterRole.Wallet); Dashboard = roles.HasFlag(IndustryCharacterRole.Dashboard); }
        public IndustryCharacterRole Flags => (Make ? IndustryCharacterRole.Manufacturing : 0) | (Buy ? IndustryCharacterRole.Buying : 0) | (Sell ? IndustryCharacterRole.Selling : 0) | (Jobs ? IndustryCharacterRole.Jobs : 0) | (Market ? IndustryCharacterRole.Market : 0) | (Wallet ? IndustryCharacterRole.Wallet : 0) | (Dashboard ? IndustryCharacterRole.Dashboard : 0);
    }
}
