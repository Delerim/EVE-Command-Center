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
        try { action(); RefreshAll(); Message.Text = message; }
        catch (Exception ex) { Message.Text = "Not saved: " + ex.Message; }
    }
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
    { if (_loading) return; _projectId = (Projects.SelectedItem as ProjectRow)?.Id; _nodeId = null; ShowProject(); }
    private void ShowProject()
    {
        var project = _state.Projects.FirstOrDefault(p => p.Id == _projectId);
        ProjectEditor.Visibility = project == null ? Visibility.Collapsed : Visibility.Visible;
        if (project == null) return;
        ConfirmDelete.IsChecked = false;
        ProjectIdentity.Text = project.Name; ProjectIdentity.ToolTip = project.Id.ToString(); ProjectName.Text = project.Name; ProjectNotes.Text = project.Notes;
        Select(ProjectLead, project.LeadId); Select(ProjectBuyer, project.BuyerId); Select(ProjectSeller, project.SellerId);
        Paused.IsChecked = project.Paused; Archived.IsChecked = project.Archived;
        NodeRow Row(IndustryProjectNode n) => new(n.Id, $"{n.Name} [type {n.TypeId}] x {n.Quantity:N0}",
            $"{n.Strategy} | Executor: {Pilot(n.ExecutorId)} | Reserved {_state.Reservations.Where(r => r.NodeId == n.Id).Sum(r => (decimal)r.Quantity):N0}",
            project.Nodes.Where(child => child.ParentId == n.Id).Select(Row).ToList(), n.TypeId, n.Strategy, n.CalculationNote);
        Nodes.ItemsSource = project.Nodes.Where(n => n.ParentId == null).Select(Row).ToList();
        if (!project.Nodes.Any(n => n.Id == _nodeId)) _nodeId = project.Nodes.Single(n => n.ParentId == null).Id;
        Audit.Text = string.Join(Environment.NewLine, _state.Events.Where(e => e.ProjectId == project.Id || e.ProjectId == null).OrderByDescending(e => e.TimeUtc).Take(100).Select(e => $"{e.TimeUtc:u} {e.Description}"));
        ShowNode();
    }
    private void Node_Selected(object sender, RoutedPropertyChangedEventArgs<object> e)
    { if (e.NewValue is NodeRow node) { _nodeId = node.Id; ShowNode(); } }
    private IndustryProjectNode? Node => _state.Projects.FirstOrDefault(p => p.Id == _projectId)?.Nodes.FirstOrDefault(n => n.Id == _nodeId);
    private void ShowNode()
    {
        var node = Node; NodeEditor.Visibility = node == null ? Visibility.Collapsed : Visibility.Visible; if (node == null) return;
        NodeTitle.Text = $"EDIT: {node.Name}"; NodeTitle.ToolTip = node.Id.ToString(); NodeQuantity.Text = node.Quantity.ToString(); NodeNotes.Text = node.Notes;
        Select(Executor, node.ExecutorId); Strategy.SelectedItem = node.Strategy;
        NodeQuantity.IsReadOnly = node.CalculationSource.Length > 0;
        Eligibility.Text = node.CalculationSource.Length == 0
            ? "Manual draft. Generate a material tree or create a plan from the blueprint library."
            : node.CalculationNote + (node.BlueprintItemId.HasValue ? $" | Blueprint {node.BlueprintItemId}, owner {node.BlueprintOwnerId}, location {node.BlueprintLocationId}" : "") + " | Skills/facility eligibility and delivery require verification.";
        if (node.ExecutorId.HasValue && !LinkedIds.Contains(node.ExecutorId.Value)) Eligibility.Text = "Assigned toon is no longer linked; assignment retained for review. " + Eligibility.Text;
        RefreshStock();
        Reservations.ItemsSource = _state.Reservations.Where(r => r.NodeId == node.Id).Select(r => new ReservationRow(r.Id, r.Quantity,
            $"{Pilot(r.OwnerId)} / {r.Path} / stack {r.ItemId}", IndustryWorkspaceInventory.Review(r, _state.Reservations, _stock, DateTimeOffset.UtcNow))).ToArray();
        decimal reserved = _state.Reservations.Where(r => r.NodeId == node.Id).Sum(r => (decimal)r.Quantity);
        Coverage.Text = $"Required {node.Quantity:N0} | Reserved {reserved:N0} | Unreserved {node.Quantity - reserved:N0}. Orders, purchases, manufactured output, delivery and consumption are not reconciled in this increment.";
    }
    private void RefreshStock()
    {
        if (Node is not {} node) return;
        string search = StockSearch.Text.Trim();
        Stock.ItemsSource = _stock.Where(s => s.TypeId == node.TypeId && (search.Length == 0 || (s.Owner + " " + s.Path + " " + s.ItemId).Contains(search, StringComparison.OrdinalIgnoreCase)))
            .Select(s => new StockRow(s, Math.Max(0, s.Quantity - _state.Reservations.Where(r => r.OwnerId == s.OwnerId && r.ItemId == s.ItemId).Sum(r => (decimal)r.Quantity)))).ToArray();
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
        string? previous = BlueprintLocation.SelectedItem as string;
        _loading = true;
        BlueprintLocation.ItemsSource = new[] { "All locations / containers" }.Concat(_blueprints.Where(b => b.OwnerId == Choice(BlueprintOwner)).Select(b => b.Location).Distinct().OrderBy(s => s)).ToArray();
        BlueprintLocation.SelectedItem = BlueprintLocation.Items.Contains(previous) ? previous : "All locations / containers";
        _loading = false;
        RefreshBlueprints();
    }
    private void RefreshBlueprints()
    {
        if (!_initialized || _loading) return;
        string query = BlueprintSearch.Text.Trim();
        string? location = BlueprintLocation.SelectedItem as string;
        var owned = _blueprints.Where(b => b.OwnerId == Choice(BlueprintOwner));
        var rows = owned.Where(b => (location == null || location == "All locations / containers" || b.Location == location) &&
            (query.Length == 0 || b.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || b.ItemId.ToString() == query)).ToArray();
        Blueprints.ItemsSource = rows;
        BlueprintSummary.Text = $"{rows.Length:N0} of {owned.Count():N0} blueprint instances | Personal cached blueprints; container/station IDs identify sources where names are unavailable.";
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
    private void DeleteProject_Click(object sender, RoutedEventArgs e) => Action(() =>
    {
        if (_projectId is not {} id || ConfirmDelete.IsChecked != true) throw new InvalidOperationException("Tick Confirm deletion for the selected project first.");
        _store.DeleteProject(id); _projectId = null; _nodeId = null;
    }, "Project deleted. Audit history retained; no in-game assets or jobs changed.");
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

    private sealed record PilotOption(long? Id, string Name) { public override string ToString() => Name; }
    private sealed record ProjectRow(Guid Id, string Name, string Detail);
    private sealed record NodeRow(Guid Id, string Title, string Detail, List<NodeRow> Children, int TypeId, string Strategy, string Calculation)
    {
        public string Icon => $"https://images.evetech.net/types/{TypeId}/icon?size=32";
        public string Color => Strategy == "Make" ? "#74D6C9" : Strategy == "Buy" ? "#80BFFF" : "#FFD166";
    }
    private sealed record ReservationRow(Guid Id, long Quantity, string Source, string Review);
    private sealed record StockRow(IndustryStockSource Source, decimal Available)
    {
        public string Free => Source.Verified ? Available.ToString("N0") : "Unverified";
        public string Owner => Source.Owner;
        public string Path => Source.Path;
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
