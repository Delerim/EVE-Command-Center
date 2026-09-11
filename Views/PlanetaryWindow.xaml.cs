using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;
public partial class PlanetaryWindow : Window
{
    private readonly PlanetaryService _service = BackgroundOperations.Current.Planetary;
    private readonly EveSsoService _sso = BackgroundOperations.Current.Sso;
    private readonly CancellationTokenSource _life = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private IReadOnlyList<EvePilotProfile> _pilots = System.Array.Empty<EvePilotProfile>();
    private PiAnalysis _analysis = new();
    private PiColony? _selected;
    public PlanetaryWindow()
    {
        InitializeComponent();
        _service.Changed += Update;
        _timer.Tick += (_, _) => Update();
        Loaded += async (_, _) => { await LoadPilots(); Update(); _timer.Start(); await _service.RefreshAsync(_life.Token); };
        Closed += (_, _) => { _timer.Stop(); _service.Changed -= Update; _life.Cancel(); };
    }
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
        Colonies.ItemsSource = _analysis.Colonies; Production.ItemsSource = _analysis.Production;
        StockGrid.ItemsSource = _analysis.Stock; Refills.ItemsSource = _analysis.Refills;
        Container.ItemsSource = _service.State.Containers;
        Container.SelectedItem = _service.State.Containers.FirstOrDefault(c => c.Id == _service.State.ContainerId);
        StockPilot.SelectedItem ??= _pilots.FirstOrDefault(p => p.CharacterId == _service.State.StockCharacterId);
        Summary.Text = $"{_analysis.Colonies.Count} colonies | {_analysis.Colonies.Count(c => c.Color == "#FFD166")} need attention";
        StockStatus.Text = _service.State.ContainerId == 0 ? "Pick a stockpile character, click Use Selection to load containers, then choose a container and apply it." : $"Only selected-container contents (including nested containers). Asset snapshot: {_service.State.StockFetched.ToLocalTime():dd MMM HH:mm}. Other station cargo is excluded.";
        if (_service.State.StockError.Length > 0) StockStatus.Text += " " + _service.State.StockError;
        Links.Text = string.Join(Environment.NewLine + Environment.NewLine, _pilots.Select(p => p.CharacterName + ": " + _service.State.PilotStatus.GetValueOrDefault(p.CharacterId, "Waiting")));
        StatusText.Text = _service.Status + (_service.Busy ? " | ESI: " + EsiDiagnostics.Status : "");
        ShowPins();
    }
    private void ShowPins()
    {
        Pins.ItemsSource = _analysis.Pins.Where(p => p.Colony?.CharacterId == _selected?.CharacterId && p.Colony?.PlanetId == _selected?.PlanetId).ToArray();
        if (_selected != null) DetailTitle.Text = _selected.Character + " | " + _selected.Planet + " | ESI colony updated " + _selected.LastUpdate.ToLocalTime().ToString("dd MMM HH:mm");
    }
    private void Colony_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (Colonies.SelectedItem is not PiRow row) return;
        _selected = row.Colony; ShowPins(); Tabs.SelectedIndex = 1;
    }
    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        if (Pilot.SelectedItem is not EvePilotProfile pilot) return;
        try
        {
            StatusText.Text = "Authorize " + pilot.CharacterName + " on the official EVE page. Select the same character.";
            await _sso.AddCharacterAsync(_life.Token, pilot.Scopes.Append(PlanetaryService.Scope));
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
