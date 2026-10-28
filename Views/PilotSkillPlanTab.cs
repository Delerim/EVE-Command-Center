using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using System.Windows.Media;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;
using ColorConverter = System.Windows.Media.ColorConverter;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridRow = System.Windows.Controls.DataGridRow;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace EveCommandCenter.Views;

/// <summary>
/// Embedded, read-only ESI skill-plan comparison. The imported training order remains immutable;
/// only the cached live ESI snapshot and reconciliation presentation change during refresh.
/// </summary>
public sealed class PilotSkillPlanTab : UserControl
{
    private EvePilotProfile? _pilot;
    private EvePilotDashboard? _snapshot;
    private StoredSkillImport? _plan;
    private SkillPlanReconciliation.Result? _comparison;
    private bool _freshEsi;
    private readonly TextBlock _heading = Label("Select a pilot", 20, "#65DDC1", true);
    private readonly TextBlock _subheading = Label("Import a saved training plan to compare with EVE.", 12, "#91B6B4");
    private readonly TextBlock _alignment = Label("Attributes pending", 12, "#93B9AF");
    private readonly TextBlock _status = Label("", 11, "#9EC5C4");
    private readonly TextBlock _trained = Label("-", 19, "#68DAB3", true);
    private readonly TextBlock _queued = Label("-", 19, "#73C8E1", true);
    private readonly TextBlock _missing = Label("-", 19, "#E5C17A", true);
    private readonly TextBlock _extras = Label("-", 19, "#9DB5C2", true);
    private readonly TextBlock _overview = Label("", 12, "#D8F0E9");
    private readonly TextBox _search = new() { Height = 29, MinWidth = 130, MaxWidth = 260, Margin = new Thickness(0, 0, 9, 0) };
    private readonly ComboBox _filter = new() { Width = 158, Height = 29, Margin = new Thickness(0, 0, 9, 0) };
    private readonly DataGrid _grid = new();
    private readonly ListBox _alerts = new();
    private readonly ListBox _remaps = new();
    private readonly Button _ack = MakeButton("ACKNOWLEDGE REMAP");
    private IReadOnlyList<SkillPlanReconciliation.Row> _allRows = Array.Empty<SkillPlanReconciliation.Row>();
    private readonly Brush _surface = Ink("#101F24");
    public event Action? RefreshRequested;

    private static SolidColorBrush Ink(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private static TextBlock Label(string value, double size, string color, bool bold = false) => new() {
        Text = value, Foreground = Ink(color), FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
    };
    private static Button MakeButton(string title) => new() {
        Content = title, Padding = new Thickness(11, 6, 11, 6), Height = 30,
        Margin = new Thickness(0, 0, 7, 6), FontSize = 11, FontWeight = FontWeights.SemiBold,
        Foreground = Ink("#EAF7F4"), Background = Ink("#173C37"), BorderBrush = Ink("#2B655B"),
        BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand
    };
    private static Border Panel(UIElement content) => new() {
        Child = content, Background = Ink("#101F24"), BorderBrush = Ink("#29423E"),
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(12)
    };
    private static DataGridTextColumn Column(string name, string path, DataGridLength width) => new() {
        Header = name, Binding = new Binding(path), Width = width, IsReadOnly = true
    };

    public PilotSkillPlanTab()
    {
        Background = Ink("#080F13");
        var body = new Grid { Margin = new Thickness(9) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = body;
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        body.Children.Add(top); Grid.SetRow(top, 0);
        top.Children.Add(_heading);
        _subheading.Margin = new Thickness(0, 4, 0, 9); top.Children.Add(_subheading);
        var actions = new WrapPanel(); top.Children.Add(actions);
        var import = MakeButton("IMPORT PLAN / ZIP"); actions.Children.Add(import); import.Click += ImportClick;
        var refresh = MakeButton("RECONCILE WITH ESI"); actions.Children.Add(refresh);
        refresh.Click += (_, _) => { _status.Text = "Refreshing the selected pilot from ESI..."; RefreshRequested?.Invoke(); };
        var copy = MakeButton("COPY NOT ALREADY QUEUED"); actions.Children.Add(copy); copy.Click += (_, _) => CopyRemaining();
        var txt = MakeButton("EXPORT EVE .TXT"); actions.Children.Add(txt); txt.Click += (_, _) => ExportText();
        var json = MakeButton("EXPORT FULL SKILLS .JSON"); actions.Children.Add(json); json.Click += (_, _) => ExportSnapshot(false);
        var csv = MakeButton("EXPORT .CSV"); actions.Children.Add(csv); csv.Click += (_, _) => ExportSnapshot(true);
        var template = MakeButton("SAVE PLAN TEMPLATE"); actions.Children.Add(template); template.Click += (_, _) => SaveTemplate();

        var summaryPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(summaryPanel, 1); body.Children.Add(summaryPanel);
        var cards = new WrapPanel(); summaryPanel.Children.Add(cards);
        cards.Children.Add(Card("TRAINED", _trained));
        cards.Children.Add(Card("TRAINING / IN QUEUE", _queued));
        cards.Children.Add(Card("NOT QUEUED", _missing));
        cards.Children.Add(Card("EXTRA EVE LEVELS", _extras));
        _overview.Margin = new Thickness(0, 7, 0, 4); summaryPanel.Children.Add(_overview);
        _alignment.Margin = new Thickness(0, 0, 0, 4); summaryPanel.Children.Add(_alignment);

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2.75, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star), MinWidth = 315 });
        Grid.SetRow(content, 2); body.Children.Add(content);
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0); content.Children.Add(left);
        var controls = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(controls, 0); left.Children.Add(controls);
        var filterPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(filterPanel, Dock.Right); controls.Children.Add(filterPanel);
        _search.Text = ""; _search.ToolTip = "Search plan skill levels"; filterPanel.Children.Add(_search);
        _filter.ItemsSource = new[] { "All levels", "Trained", "Training", "In EVE queue", "Not queued", "Order conflicts" };
        _filter.SelectedIndex = 0; filterPanel.Children.Add(_filter);
        controls.Children.Add(Label("PLANNED TRAINING ORDER", 12, "#6FDBC4", true));
        _search.TextChanged += (_, _) => FilterRows(); _filter.SelectionChanged += (_, _) => FilterRows();
        _grid.AutoGenerateColumns = false;
        _grid.IsReadOnly = true; _grid.CanUserAddRows = false; _grid.CanUserDeleteRows = false;
        _grid.EnableRowVirtualization = true; _grid.EnableColumnVirtualization = true;
        _grid.HeadersVisibility = DataGridHeadersVisibility.Column;
        _grid.RowHeight = 30; _grid.ColumnHeaderHeight = 32;
        _grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _grid.HorizontalGridLinesBrush = Ink("#25413F");
        _grid.Background = _surface; _grid.RowBackground = Ink("#102126");
        _grid.AlternatingRowBackground = Ink("#14282C");
        _grid.Foreground = Ink("#E6F4F0"); _grid.BorderBrush = Ink("#31524B");
        _grid.BorderThickness = new Thickness(1);
        _grid.SelectionMode = DataGridSelectionMode.Single; _grid.SelectionUnit = DataGridSelectionUnit.FullRow;
        _grid.Columns.Add(Column("#", "Position", new DataGridLength(48)));
        _grid.Columns.Add(Column("SKILL / LEVEL", "LevelText", new DataGridLength(2.4, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(Column("ESI STATUS", "Status", new DataGridLength(1.25, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(Column("EVE #", "QueuePosition", new DataGridLength(70)));
        _grid.Columns.Add(Column("EVE FINISH", "FinishText", new DataGridLength(145)));
        _grid.Columns.Add(Column("REMAP PHASE", "PhaseText", new DataGridLength(1.15, DataGridLengthUnitType.Star)));
        _grid.LoadingRow += (_, e) => {
            if (e.Row.Item is not SkillPlanReconciliation.Row r) return;
            if (r.OutOfOrder) e.Row.Background = Ink("#513328");
            else if (r.Training) e.Row.Background = Ink("#154945");
            else if (r.Missing) e.Row.Background = Ink("#352D23");
            else if (r.Trained) e.Row.Background = Ink("#15382F");
        };
        Grid.SetRow(_grid, 1); left.Children.Add(_grid);

        var right = new Grid();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.15, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 2); content.Children.Add(right);
        var remapContainer = new DockPanel(); Grid.SetRow(remapContainer, 0); right.Children.Add(Panel(remapContainer));
        var heading = Label("REMAP CHECKPOINTS", 13, "#65DDC1", true); heading.Margin = new Thickness(0, 0, 0, 7);
        DockPanel.SetDock(heading, Dock.Top); remapContainer.Children.Add(heading);
        _ack.IsEnabled = false; _ack.Margin = new Thickness(0, 7, 0, 0);
        DockPanel.SetDock(_ack, Dock.Bottom); remapContainer.Children.Add(_ack); _ack.Click += AcknowledgeClick;
        _remaps.SelectionChanged += (_, _) => UpdateAcknowledge();
        _remaps.Background = _surface; _remaps.Foreground = Brushes.WhiteSmoke;
        _remaps.BorderThickness = new Thickness(0); _remaps.FontSize = 12;
        remapContainer.Children.Add(_remaps);
        var alertContainer = new DockPanel(); Grid.SetRow(alertContainer, 2); right.Children.Add(Panel(alertContainer));
        var alertHeading = Label("RECONCILIATION / ALERTS", 13, "#65DDC1", true);
        alertHeading.Margin = new Thickness(0, 0, 0, 7);
        DockPanel.SetDock(alertHeading, Dock.Top); alertContainer.Children.Add(alertHeading);
        _alerts.Background = _surface; _alerts.Foreground = Brushes.WhiteSmoke;
        _alerts.BorderThickness = new Thickness(0); _alerts.FontSize = 12;
        alertContainer.Children.Add(_alerts);
        _status.Margin = new Thickness(0, 7, 0, 0); _status.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_status, 3); body.Children.Add(_status);
        Render();
    }

    private static Border Card(string title, TextBlock value)
    {
        var stack = new StackPanel();
        stack.Children.Add(Label(title, 10, "#8EB3AB", true));
        value.Margin = new Thickness(0, 5, 0, 0); stack.Children.Add(value);
        return new Border { Child = stack, Width = 183, Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 8, 0), Background = Ink("#12272A"),
            BorderBrush = Ink("#31524B"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9) };
    }

    public void Clear()
    {
        _pilot = null; _snapshot = null; _plan = null; _freshEsi = false;
        Render();
    }
    public void SetPilot(EvePilotProfile profile)
    {
        if (_pilot?.CharacterId == profile.CharacterId) { _pilot = profile; return; }
        _pilot = profile; _snapshot = null; _freshEsi = false; _comparison = null;
        try { _plan = SkillPlanImportService.Load(profile.CharacterId); _status.Text = ""; }
        catch (Exception ex) { _plan = null; _status.Text = "Could not load saved plan: " + ex.Message; }
        Render();
    }
    public void UpdateSnapshot(EvePilotProfile profile, EvePilotDashboard data, bool freshEsi)
    {
        if (_pilot?.CharacterId != profile.CharacterId) SetPilot(profile);
        if (data.Summary.CharacterId != profile.CharacterId) return;
        _snapshot = data;
        _freshEsi = freshEsi || _freshEsi;
        Render();
    }
    private void Render()
    {
        _heading.Text = _pilot == null ? "SKILL PLANS" : "SKILL PLANS  |  " + _pilot.CharacterName.ToUpperInvariant();
        _subheading.Text = _plan == null ? "No imported plan for this pilot. Import the eight-pilot ZIP or a single JSON plan." :
            _plan.Document.Name + "  ·  " + (_freshEsi ? "ESI refreshed" : _snapshot == null ? "Waiting for ESI" : "Cached ESI snapshot");
        _comparison = null; _allRows = Array.Empty<SkillPlanReconciliation.Row>();
        _remaps.ItemsSource = null; _alerts.ItemsSource = null;
        _trained.Text = _queued.Text = _missing.Text = _extras.Text = "-";
        _overview.Text = _plan == null ? "Import a plan to start reconciliation." : "Refreshing skills and queue from ESI...";
        _alignment.Text = "Remap alignment unavailable until plan and attributes are loaded.";
        if (_plan == null || _snapshot == null) { FilterRows(); return; }
        try
        {
            var result = SkillPlanReconciliation.Compare(_plan, _snapshot, DateTimeOffset.UtcNow);
            _comparison = result; _allRows = result.Rows;
            _trained.Text = result.Trained.ToString("N0");
            _queued.Text = (result.Queued + result.Training).ToString("N0");
            _missing.Text = result.Missing.ToString("N0"); _extras.Text = result.Extras.ToString("N0");
            _overview.Text = $"{result.Rows.Count - result.Missing:N0}/{result.Rows.Count:N0} planned levels covered  ·  " +
                (result.OutOfOrder == 0 ? "Training order matches EVE" : $"{result.OutOfOrder} ordering conflicts") +
                $"  ·  {_snapshot.Summary.UnallocatedSp:N0} unallocated SP";
            _alignment.Text = result.Alignment;
            _remaps.ItemsSource = result.Remaps.Select(r => new RemapDisplay(r)).ToArray();
            _alerts.ItemsSource = result.Alerts.Count == 0 ? new[] { "ON PLAN · No reconciliation issues" } :
                result.Alerts.Select(a => a.Severity + " · " + a.Message).ToArray();
            FilterRows();
        }
        catch (Exception ex) { _status.Text = "Reconciliation failed; the imported plan and ESI data were not changed: " + ex.Message; }
    }
    private sealed class RemapDisplay
    {
        public SkillPlanReconciliation.Remap Item { get; }
        public RemapDisplay(SkillPlanReconciliation.Remap item) => Item = item;
        public override string ToString() => $"PHASE {Item.Phase}  ·  {Item.Mode}  ·  {Item.State}\n" +
            Item.Trigger + "\n" + Item.Projected + "\n" + Item.Target + "\n";
    }
    private void UpdateAcknowledge() => _ack.IsEnabled = _remaps.SelectedItem is RemapDisplay { Item: { Due: true, Acknowledged: false } };
    private void FilterRows()
    {
        IEnumerable<SkillPlanReconciliation.Row> view = _allRows;
        string search = _search.Text?.Trim() ?? "";
        if (search.Length > 0) view = view.Where(x => x.Skill.Contains(search, StringComparison.OrdinalIgnoreCase));
        view = (_filter.SelectedItem as string) switch {
            "Trained" => view.Where(x => x.Trained),
            "Training" => view.Where(x => x.Training),
            "In EVE queue" => view.Where(x => x.Queued && !x.Training),
            "Not queued" => view.Where(x => x.Missing),
            "Order conflicts" => view.Where(x => x.OutOfOrder),
            _ => view
        };
        _grid.ItemsSource = view.ToArray();
    }
    private void ImportClick(object sender, RoutedEventArgs e)
    {
        if (_pilot == null) return;
        var picker = new OpenFileDialog { Filter = "ECC plans / ZIP|*.eccskillplan.json;*.zip|JSON|*.json|ZIP|*.zip", Title = "Import skill plan for " + _pilot.CharacterName };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var doc = SkillPlanImportService.ReadFile(picker.FileName, _pilot);
            var choice = MessageBox.Show(Window.GetWindow(this),
                $"Import {doc.Queue.Count} levels and {doc.Remaps.Count} remap markers for {_pilot.CharacterName}?\n\n" +
                "This replaces the local plan only. ECC cannot modify the live EVE queue.",
                "Confirm training plan", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (choice != MessageBoxResult.Yes) return;
            _plan = SkillPlanImportService.Import(doc, _pilot);
            Render();
            _status.Text = "Plan imported and reconciled against the latest available ESI snapshot. Use RECONCILE WITH ESI to refresh.";
            if (_snapshot != null && _freshEsi) SkillPlanRemapMonitor.Instance.Observe(_pilot, _snapshot);
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Import rejected", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private bool CanExport(bool planRequired)
    {
        if (_pilot == null || _snapshot == null || !_freshEsi || (planRequired && _plan == null))
        {
            _status.Text = "Import a plan where required and click RECONCILE WITH ESI first; cached exports may be stale.";
            return false;
        }
        return true;
    }
    private void CopyRemaining()
    {
        if (!CanExport(true)) return;
        try {
            string value = SkillPlanImportService.EveQueueText(_plan!, _snapshot, true);
            if (string.IsNullOrWhiteSpace(value)) { _status.Text = "Everything remaining is already queued in EVE."; return; }
            Clipboard.SetText(value);
            _status.Text = $"Copied {value.Split('\n').Length} not-yet-queued levels. Import and verify them manually in EVE.";
        } catch (Exception ex) { _status.Text = "Clipboard error: " + ex.Message; }
    }
    private void ExportText()
    {
        if (!CanExport(true)) return;
        var picker = new SaveFileDialog { Filter = "EVE skill queue|*.txt", FileName = _pilot!.CharacterName + "-not-already-queued.txt" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(picker.FileName, SkillPlanImportService.EveQueueText(_plan!, _snapshot, true), new UTF8Encoding(false)); _status.Text = "EVE-ready missing levels saved."; }
        catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; }
    }
    private void ExportSnapshot(bool csv)
    {
        if (!CanExport(false)) return;
        var picker = new SaveFileDialog { Filter = csv ? "Skill CSV|*.csv" : "Skill JSON|*.json", FileName = _pilot!.CharacterName + (csv ? "-skills-queue.csv" : "-skills-queue.json") };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try {
            File.WriteAllText(picker.FileName, csv ? SkillPlanImportService.ExportSnapshotCsv(_snapshot!) : SkillPlanImportService.ExportSnapshot(_snapshot!), new UTF8Encoding(false));
            _status.Text = "Full trained and queued skills exported.";
        } catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; }
    }
    private void SaveTemplate()
    {
        if (_pilot == null) return;
        var picker = new SaveFileDialog { Filter = "ECC skill-plan template|*.eccskillplan.json", FileName = _pilot.CharacterName + "-template.eccskillplan.json" };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(picker.FileName, SkillPlanImportService.CreateTemplate(_pilot), new UTF8Encoding(false)); _status.Text = "Plan template saved."; }
        catch (Exception ex) { _status.Text = "Template export failed: " + ex.Message; }
    }
    private void AcknowledgeClick(object sender, RoutedEventArgs e)
    {
        if (_pilot == null || _remaps.SelectedItem is not RemapDisplay { Item: { Due: true, Acknowledged: false } } selected) return;
        if (MessageBox.Show(Window.GetWindow(this), "Dismiss the remap reminder?\n\nPlease verify the attributes in EVE first. ECC cannot remap for you.",
                "Acknowledge remap", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try {
            SkillPlanImportService.Acknowledge(_pilot.CharacterId, selected.Item.Id);
            _plan = SkillPlanImportService.Load(_pilot.CharacterId); Render();
            if (_snapshot != null) SkillPlanRemapMonitor.Instance.Observe(_pilot, _snapshot);
            _status.Text = "Remap reminder acknowledged and saved.";
        } catch (Exception ex) { _status.Text = "Could not acknowledge: " + ex.Message; }
    }
}
