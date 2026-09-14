using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;
public partial class PlanetaryWindow : Window
{
    private void Notifications_Click(object sender,RoutedEventArgs e)=>BackgroundOperations.Current.OpenNotifications();
    private readonly PlanetaryService _service = BackgroundOperations.Current.Planetary;
    private readonly EveSsoService _sso = BackgroundOperations.Current.Sso;
    private readonly CancellationTokenSource _life = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private IReadOnlyList<EvePilotProfile> _pilots = System.Array.Empty<EvePilotProfile>();
    private PiAnalysis _analysis = new();
    private readonly HashSet<string> _expanded = new();
    public PlanetaryWindow()
    {
        InitializeComponent();
        PiAlerts.IsChecked = _service.State.DesktopAlerts;
        CompactExtractors.IsChecked = _service.State.CompactExtractors;
        ApplyExtractorMode();
        _service.Changed += Update;
        _timer.Tick += (_, _) => Update();
        Loaded += async (_, _) => { await LoadPilots(); Update(); _timer.Start(); await _service.RefreshAsync(_life.Token); };
        Closed += (_, _) => { _timer.Stop(); _service.Changed -= Update; _life.Cancel(); };
    }
    private void FitBudget()
    {
        if(BudgetRow==null || RefillLayout.ActualHeight<=0)return;
        BudgetContent.Visibility=ExpandBudget.IsChecked==true?Visibility.Visible:Visibility.Collapsed;
        double wanted=ExpandBudget.IsChecked==true?(_service.State.RefillBudgetHeight??(116+StockBudget.Items.Count*30)):52;
        BudgetRow.Height=new GridLength(Math.Clamp(wanted,52,Math.Max(52,RefillLayout.ActualHeight-150)));
    }
    private void RefillLayout_SizeChanged(object sender,SizeChangedEventArgs e)=>FitBudget();
    private void ExpandBudget_Click(object sender,RoutedEventArgs e)=>FitBudget();
    private void FitBudget_Click(object sender,RoutedEventArgs e)
    {
        _service.State.RefillBudgetHeight=null;ExpandBudget.IsChecked=true;FitBudget();_service.Save();
    }
    private void BudgetSplitter_DragCompleted(object sender,System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if(ExpandBudget.IsChecked==true){_service.State.RefillBudgetHeight=BudgetRow.ActualHeight;_service.Save();}
    }
    private void PiAlerts_Click(object sender,RoutedEventArgs e) { _service.State.DesktopAlerts=PiAlerts.IsChecked==true; _service.Save(); }
    private async Task LoadPilots()
    {
        _pilots = await _sso.LoadPilotsAsync();
        Pilot.ItemsSource = StockPilot.ItemsSource = _pilots;
        Pilot.SelectedIndex = 0;
        StockPilot.SelectedItem = _pilots.FirstOrDefault(p => p.CharacterId == _service.State.StockCharacterId);
    }
    private void Update()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(Update); return; }
        _analysis = PlanetaryAnalysis.Build(_service.State, DateTimeOffset.UtcNow);
        Colonies.ItemsSource = PlanetaryGroups.Build(_analysis, _expanded); Production.ItemsSource = _analysis.Production;
        FactorySummary.ItemsSource = _analysis.FactoryTiers;
        var extractorGroups = PlanetaryExtractors.Build(_analysis, _expanded, DateTimeOffset.UtcNow);
        Extractors.ItemsSource = extractorGroups;
        CompactExtractorGrid.ItemsSource = extractorGroups.SelectMany(g => g.Planets).SelectMany(p => p.Pins).ToList();
        StockBudget.ItemsSource = _analysis.StockBudget;
        FitBudget();
        RefillStockStatus.Text = _service.State.ContainerId==0 ? "Choose a stockpile toon and container on the Stockpile tab." : $"Selected container stock checked {_service.State.StockFetched.ToLocalTime():dd MMM HH:mm}. {_service.State.StockError}";
        StockGrid.ItemsSource = _analysis.Stock; Refills.ItemsSource = PlanetaryGroups.Build(_analysis, _expanded, true);
        var refillGroups = PlanetaryGroups.Build(_analysis, _expanded, true);
        var t1 = _analysis.Refills.Where(r => PlanetaryAnalysis.Tier(r.TypeId) == 1).ToArray();
        RefillSummary.Text = $"T1 REFILLS | {refillGroups.Count} pilots | {refillGroups.Sum(g => g.Planets.Count)} planets | {t1.Sum(r => r.Need):N0} units to haul | {t1.Sum(r => r.Missing):N0} shortfall\nEstimated inputs left; targets assume finished output is collected first. T1 only. Stock allocated once across all refills; verify estimates before hauling.";
        Container.ItemsSource = _service.State.Containers;
        Container.SelectedItem = _service.State.Containers.FirstOrDefault(c => c.Id == _service.State.ContainerId);
        StockPilot.SelectedItem ??= _pilots.FirstOrDefault(p => p.CharacterId == _service.State.StockCharacterId);
        Summary.Text = $"{_analysis.Colonies.Count} colonies | {_analysis.Colonies.Count(c => c.Color == "#FFD166")} need attention | {_analysis.Factories.Count(c => c.Status.StartsWith("COLLECT"))} factory planets collect/refill";
        StockStatus.Text = _service.State.ContainerId == 0 ? "Pick a stockpile character, click Use Selection to load containers, then choose a container and apply it." : $"Only selected-container contents (including nested containers). Asset snapshot: {_service.State.StockFetched.ToLocalTime():dd MMM HH:mm}. Other station cargo is excluded. Values use Jita 4-4 best buy, before fees and order depth; quotes refresh hourly.";
        if (_service.State.StockError.Length > 0) StockStatus.Text += " " + _service.State.StockError;
        Links.Text = string.Join(Environment.NewLine + Environment.NewLine, _pilots.Select(p => p.CharacterName + ": " + _service.State.PilotStatus.GetValueOrDefault(p.CharacterId, "Waiting")));
        StatusText.Text = _service.Status + (_service.Busy ? " | ESI: " + EsiDiagnostics.Status : "");

    }
    private void CompactExtractors_Click(object sender, RoutedEventArgs e)
    {
        _service.State.CompactExtractors = CompactExtractors.IsChecked == true;
        _service.Save();
        ApplyExtractorMode();
    }
    private void ApplyExtractorMode()
    {
        bool compact = CompactExtractors.IsChecked == true;
        CompactExtractorGrid.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        ExtractorScroll.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
    }
    private void ExtractorScroll_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        // Each planet contains a DataGrid with its own ScrollViewer. Route the
        // wheel to the enclosing page before those grids consume it at an edge.
        if (sender is ScrollViewer scroll)
        {
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }
    private void ExtractorExpansion_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander || !ReferenceEquals(e.OriginalSource, expander) || expander.DataContext is not PiGroupView group) return;
        if (expander.IsExpanded) _expanded.Remove("closed:" + group.Key); else _expanded.Add("closed:" + group.Key);
    }
    private void Expansion_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander || !ReferenceEquals(e.OriginalSource, expander)) return;
        var key = expander.DataContext switch { PiGroupView g => g.Key, PiPlanetView p => p.Key, _ => "" };
        if (key.Length == 0) return;
        if (expander.IsExpanded) _expanded.Add(key); else _expanded.Remove(key);
    }
    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        if (Pilot.SelectedItem is not EvePilotProfile pilot) return;
        try
        {
            StatusText.Text = "Authorize " + pilot.CharacterName + " on the official EVE page. Select the same character.";
            await _sso.AddCharacterAsync(_life.Token, pilot.Scopes.Append(PlanetaryService.Scope), pilot.CharacterId);
            await LoadPilots();
            if (!_service.Busy) { _service.State.NextRefresh = default; await _service.RefreshAsync(_life.Token); }
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { await _service.RefreshAsync(_life.Token); Update(); }
    private async void Stock_Click(object sender, RoutedEventArgs e)
    {
        if (StockPilot.SelectedItem is not EvePilotProfile pilot) return;
        if (_service.Busy) { StatusText.Text = "Let the current refresh finish before changing stockpile ownership."; return; }
        bool same = pilot.CharacterId == _service.State.StockCharacterId;
        _service.SelectStock(pilot.CharacterId, same ? (Container.SelectedItem as PiContainer)?.Id ?? 0 : 0);
        if (!same) { StatusText.Text = "Stockpile owner saved; loading its containers through the ESI queue."; await _service.RefreshAsync(_life.Token); }
    }
}
