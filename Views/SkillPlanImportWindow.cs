using System;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
// Explicit WPF aliases: ECC enables Windows Forms too, so these names are otherwise ambiguous.
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using System.Windows.Media;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using Microsoft.Win32;

namespace EveCommandCenter.Views;

/// <summary>Per-character external plan import, remap review and read-only ESI export.</summary>
public sealed class SkillPlanImportWindow : Window
{
    private readonly EvePilotProfile _pilot;
    private readonly EveSsoService _sso = new();
    private readonly CancellationTokenSource _lifetime = new();
    private EvePilotDashboard? _snapshot;
    private bool _liveSnapshot;
    private StoredSkillImport? _plan;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
    private readonly TextBlock _alignment = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Brushes.LightGoldenrodYellow };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.LightSkyBlue };
    private readonly ListBox _queue = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly ListBox _remaps = new() { FontFamily = new FontFamily("Consolas"), FontSize = 12 };
    private readonly Button _acknowledge = new() { Content = "ACKNOWLEDGE SELECTED REMAP", Padding = new Thickness(10, 6, 10, 6), IsEnabled = false };

    public SkillPlanImportWindow(EvePilotProfile pilot, EvePilotDashboard? snapshot)
    {
        _pilot = pilot;
        _snapshot = snapshot?.Summary.CharacterId == pilot.CharacterId ? snapshot : null;
        Title = "Skill Plan Command Center | " + pilot.CharacterName;
        Width = 1140; Height = 780; MinWidth = 900; MinHeight = 650;
        Background = new SolidColorBrush(Color.FromRgb(7, 24, 27));
        Foreground = Brushes.Honeydew;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Closed += (_, _) => _lifetime.Cancel();

        var shell = new DockPanel { Margin = new Thickness(17) };
        Content = shell;
        var heading = new TextBlock { Text = "SKILL PLAN COMMAND CENTER  |  " + pilot.CharacterName.ToUpperInvariant(),
            FontSize = 22, FontWeight = FontWeights.Bold, Foreground = Brushes.Turquoise };
        var top = new StackPanel();
        DockPanel.SetDock(top, Dock.Top); shell.Children.Add(top);
        top.Children.Add(heading);
        top.Children.Add(new TextBlock { Text = "External plans remain local. ECC never remaps a character or modifies the EVE queue.", Margin = new Thickness(0, 8, 0, 8), Foreground = Brushes.LightGray });
        var actions = new WrapPanel { Margin = new Thickness(0, 4, 0, 10) };
        top.Children.Add(actions);
        AddButton(actions, "IMPORT JSON / ZIP", ImportClick);
        AddButton(actions, "REFRESH ESI", RefreshClick);
        AddButton(actions, "COPY REMAINING FOR EVE", (_, _) => CopyQueue(false));
        AddButton(actions, "COPY NOT ALREADY QUEUED", (_, _) => CopyQueue(true));
        AddButton(actions, "EXPORT EVE .TXT", ExportEveClick);
        AddButton(actions, "EXPORT FULL SKILLS .JSON", ExportSkillsClick);
        AddButton(actions, "EXPORT FULL SKILLS .CSV", ExportCsvClick);
        AddButton(actions, "SAVE TEMPLATE", SaveTemplateClick);
        _summary.Margin = new Thickness(0, 4, 0, 7); top.Children.Add(_summary);
        _alignment.Margin = new Thickness(0, 0, 0, 9); top.Children.Add(_alignment);
        var lower = new StackPanel();
        DockPanel.SetDock(lower, Dock.Bottom); shell.Children.Add(lower);
        lower.Children.Add(_status);
        var grid = new Grid(); shell.Children.Add(grid);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel(); Grid.SetColumn(left, 0); grid.Children.Add(left);
        var leftHeader = new TextBlock { Text = "IMPORTED SKILL ORDER (never auto-reordered)", Foreground = Brushes.Turquoise,
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(leftHeader, Dock.Top); left.Children.Add(leftHeader);
        _queue.Background = new SolidColorBrush(Color.FromRgb(15, 40, 44)); _queue.Foreground = Brushes.Honeydew;
        left.Children.Add(_queue);
        var right = new DockPanel(); Grid.SetColumn(right, 2); grid.Children.Add(right);
        var remapHeader = new TextBlock { Text = "ATTRIBUTE PHASES / REMAP CHECKPOINTS", Foreground = Brushes.Turquoise,
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(remapHeader, Dock.Top); right.Children.Add(remapHeader);
        var lowerRight = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
        DockPanel.SetDock(lowerRight, Dock.Bottom); right.Children.Add(lowerRight);
        lowerRight.Children.Add(_acknowledge);
        lowerRight.Children.Add(new TextBlock { Text = "Notifications persist until acknowledged. Close/minimise the alert without acknowledging to keep it pending.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 4) });
        _acknowledge.Click += AcknowledgeClick;
        _remaps.SelectionChanged += (_, _) => UpdateAcknowledgement();
        _remaps.Background = new SolidColorBrush(Color.FromRgb(15, 40, 44)); _remaps.Foreground = Brushes.Honeydew;
        right.Children.Add(_remaps);
        try { _plan = SkillPlanImportService.Load(pilot.CharacterId); }
        catch (Exception ex) { _status.Text = "Saved plan could not be read: " + ex.Message; }
        RenderPlan();
    }
    private static void AddButton(WrapPanel parent, string label, RoutedEventHandler callback)
    {
        var button = new Button { Content = label, Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 8, 5),
            Background = new SolidColorBrush(Color.FromRgb(21, 72, 72)), Foreground = Brushes.WhiteSmoke };
        button.Click += callback;
        parent.Children.Add(button);
    }
    private void RenderPlan()
    {
        _queue.ItemsSource = null;
        _remaps.ItemsSource = null;
        _acknowledge.IsEnabled = false;
        if (_plan == null)
        {
            _summary.Text = "No plan imported yet. Click IMPORT JSON / ZIP and select your eight-pilot design pack ZIP or one pilot JSON.";
            _alignment.Text = "Live training information is " + (_snapshot == null ? "not loaded" : "available") + ".";
            return;
        }
        var document = _plan.Document;
        var rows = document.Queue.Select(s => {
            string progress = "";
            if (_snapshot != null && SkillPlanning.Catalog.Values.FirstOrDefault(x => string.Equals(x.Name, s.Skill, StringComparison.OrdinalIgnoreCase)) is { } skill)
            {
                if (_snapshot.TrainedSkills.Any(t => t.SkillId == skill.Id && t.TrainedSkillLevel >= s.Level)) progress = " [TRAINED]";
                else if (_snapshot.SkillQueue.Any(q => q.SkillId == skill.Id && q.FinishedLevel == s.Level)) progress = " [IN EVE QUEUE]";
            }
            return $"{s.Position,3}. {s.EveText}{progress}";
        }).ToArray();
        _queue.ItemsSource = rows;
        var pending = _snapshot == null ? Array.Empty<SkillRemapAssessment>() : SkillPlanImportService.Assess(_plan, _snapshot).ToArray();
        var entries = document.Remaps.OrderBy(r => r.PhaseNumber).Select(r => new RemapEntry(r,
            pending.FirstOrDefault(p => p.Remap.Id == r.Id), _plan.Acknowledged.Contains(SkillPlanImportService.RemapKey(document, r)))).ToArray();
        _remaps.ItemsSource = entries;
        _summary.Text = $"{document.Name}  |  {rows.Length} planned levels  |  {document.Remaps.Count} remap phases  |  " +
            $"{(_snapshot == null ? "No ESI skills loaded" : $"{_snapshot.Summary.UnallocatedSp:N0} unallocated SP" + (_liveSnapshot ? " (live refresh)" : " (cached snapshot)"))}";
        _alignment.Text = _snapshot == null ? "Current map: refresh ESI to compare base attributes with the planned phase."
            : SkillPlanImportService.BaseAlignment(_plan, _snapshot);
        if (document.SkillbooksNeeded.Count > 0) _status.Text = $"Source plan lists {document.SkillbooksNeeded.Count} skillbook(s) to check before applying the queue in EVE.";
        if (string.Equals(_pilot.CharacterName, "Linuleum", StringComparison.OrdinalIgnoreCase) &&
            document.Queue.Any(x => string.Equals(x.Skill, "Upwell Hauler", StringComparison.OrdinalIgnoreCase)))
            _status.Text += "  WARNING: This archived Linuleum plan still contains Upwell Hauler; review against the later removal decision.";
    }
    private sealed class RemapEntry
    {
        public SkillImportRemap Remap { get; }
        public SkillRemapAssessment? Assessment { get; }
        public bool Acknowledged { get; }
        public RemapEntry(SkillImportRemap remap, SkillRemapAssessment? assessment, bool acknowledged)
        { Remap = remap; Assessment = assessment; Acknowledged = acknowledged; }
        public override string ToString() => $"PHASE {Remap.PhaseNumber} | {Remap.Mode} | {Assessment?.State ?? (Acknowledged ? "ACKNOWLEDGED" : "NOT CHECKED")}\n" +
            $"AFTER: {Remap.Trigger.AfterCompleted ?? "Initial phase"}\nBEFORE: {Remap.Trigger.BeforeStarts ?? "-"}\n" +
            $"{Remap.TargetBaseAttributes?.ToString() ?? "Keep current attributes"}\n" +
            $"Estimate: {Remap.EstimatedDate ?? "unknown"} (not an alert trigger)\n";
    }
    private void UpdateAcknowledgement() => _acknowledge.IsEnabled = _remaps.SelectedItem is RemapEntry { Assessment: { Due: true, Acknowledged: false } };
    private void ImportClick(object sender, RoutedEventArgs args)
    {
        var dialog = new OpenFileDialog { Filter = "ECC plan / ZIP|*.eccskillplan.json;*.zip|JSON|*.json|ZIP|*.zip", Title = "Import skill plan for " + _pilot.CharacterName };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var candidate = SkillPlanImportService.ReadFile(dialog.FileName, _pilot);
            var forecast = string.Join("\n", candidate.Remaps.Select(r => $"Phase {r.PhaseNumber}: {r.Mode} after {r.Trigger.AfterCompleted ?? "initial"}"));
            var answer = MessageBox.Show(this, $"Pilot: {_pilot.CharacterName}\n{candidate.Queue.Count} ordered skill levels\n{candidate.Remaps.Count} remap phases\n\n{forecast}\n\n" +
                "Importing replaces this pilot's local plan. ECC never writes to EVE. Continue?", "Review imported training plan", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            _plan = SkillPlanImportService.Import(candidate, _pilot);
            _status.Text = $"Imported {candidate.Queue.Count} levels for {_pilot.CharacterName}. Refresh ESI to reconcile live trained/queued skills.";
            RenderPlan();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Import rejected - no data changed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        _status.Text = "Refreshing the selected pilot's trained skills, complete queue, attributes and implants...";
        try
        {
            var data = await _sso.GetDashboardAsync(_pilot, _lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _snapshot = data; _liveSnapshot = true;
            RenderPlan();
            _status.Text = $"ESI refreshed for {_pilot.CharacterName}. Queue entries: {data.SkillQueue.Count}; unallocated SP: {data.Summary.UnallocatedSp:N0}.";
            SkillPlanRemapMonitor.Instance.Observe(_pilot, data);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "ESI refresh failed; previous data retained: " + ex.Message; }
    }
    private void CopyQueue(bool excludeExisting)
    {
        if (_plan == null) { _status.Text = "Import a plan first."; return; }
        if (_snapshot == null || !_liveSnapshot) { _status.Text = "Use REFRESH ESI first so ECC excludes completed and currently queued skills reliably."; return; }
        try
        {
            string text = SkillPlanImportService.EveQueueText(_plan, _snapshot, excludeExisting);
            if (text.Length == 0) { _status.Text = "Nothing remains for this selection."; return; }
            Clipboard.SetText(text);
            _status.Text = $"Copied {text.Split('\n').Length} ordered levels for EVE. Review the import in EVE; it is not automatically applied.";
        }
        catch (Exception ex) { _status.Text = "Clipboard error: " + ex.Message; }
    }
    private void ExportEveClick(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _snapshot == null || !_liveSnapshot) { _status.Text = "Import a plan and REFRESH ESI before exporting."; return; }
        var dialog = new SaveFileDialog { Filter = "EVE skill list|*.txt", FileName = _pilot.CharacterName + "-remaining-eve.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, SkillPlanImportService.EveQueueText(_plan, _snapshot, false), new UTF8Encoding(false)); _status.Text = "Exported EVE-ready ordered text to " + dialog.FileName; }
        catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; }
    }
    private void ExportSkillsClick(object sender, RoutedEventArgs e)
    {
        if (_snapshot == null || !_liveSnapshot) { _status.Text = "Refresh ESI before exporting a full skills and queue snapshot."; return; }
        var dialog = new SaveFileDialog { Filter = "Combined skill snapshot|*.json", FileName = _pilot.CharacterName + "-skills-and-queue.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, SkillPlanImportService.ExportSnapshot(_snapshot), new UTF8Encoding(false)); _status.Text = "Exported all trained, in-progress and queued levels for " + _pilot.CharacterName; }
        catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; }
    }
    private void ExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (_snapshot == null || !_liveSnapshot) { _status.Text = "Refresh ESI before exporting a full skills CSV."; return; }
        var dialog = new SaveFileDialog { Filter = "Combined skill snapshot CSV|*.csv", FileName = _pilot.CharacterName + "-skills-and-queue.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, SkillPlanImportService.ExportSnapshotCsv(_snapshot), new UTF8Encoding(false)); _status.Text = "Exported all trained, partial and queued skills as CSV."; }
        catch (Exception ex) { _status.Text = "CSV export failed: " + ex.Message; }
    }
    private void SaveTemplateClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "ECC skill plan template|*.eccskillplan.json", FileName = _pilot.CharacterName + "-template.eccskillplan.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, SkillPlanImportService.CreateTemplate(_pilot), new UTF8Encoding(false)); _status.Text = "Created reusable template for " + _pilot.CharacterName; }
        catch (Exception ex) { _status.Text = "Template export failed: " + ex.Message; }
    }
    private void AcknowledgeClick(object sender, RoutedEventArgs e)
    {
        if (_remaps.SelectedItem is not RemapEntry { Assessment: { Due: true, Acknowledged: false } } chosen) return;
        var answer = MessageBox.Show(this, "Acknowledge this reminder for " + _pilot.CharacterName + "?\n\n" +
            "ECC cannot verify that you changed the attributes in EVE. Confirm only after checking the remap yourself.",
            "Acknowledge remap checkpoint", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            SkillPlanImportService.Acknowledge(_pilot.CharacterId, chosen.Remap.Id);
            _plan = SkillPlanImportService.Load(_pilot.CharacterId);
            RenderPlan();
            _status.Text = "Remap reminder acknowledged and persisted; ECC did not modify EVE.";
            SkillPlanRemapMonitor.Instance.CheckCached();
        }
        catch (Exception ex) { _status.Text = "Acknowledgement failed: " + ex.Message; }
    }
}
