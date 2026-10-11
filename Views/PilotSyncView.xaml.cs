using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
namespace EveCommandCenter.Views;
public partial class PilotSyncView : System.Windows.Controls.UserControl
{
    private PilotSyncSettings? _settings;
    private MarketOrdersService? _market;
    private Action? _schedule;
    private sealed record Row(EvePilotProfile Pilot, PilotSyncPreference Preference)
    { public string Name => Pilot.CharacterName; public string Portrait => $"https://images.evetech.net/characters/{Pilot.CharacterId}/portrait?size=64"; }
    public PilotSyncView() => InitializeComponent();
    internal void Initialize(PilotSyncSettings settings, IReadOnlyList<EvePilotProfile> pilots, MarketOrdersService market, Action? schedule = null)
    {
        _settings = settings; _market = market; _schedule = schedule;
        settings.EnsurePilots(pilots, market.State.SelectedCharacters);
        SyncRows.ItemsSource = VisibilityRows.ItemsSource = pilots.Select(p => new Row(p, settings.Get(p.CharacterId))).ToArray();
        SyncMessage.Text = settings.Error.Length > 0 ? settings.Error : "Wallet defaults to all linked pilots. Choose market pilots independently, then save.";
        SyncRows.SelectedIndex = 0;
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null) return;
        try
        {
            SyncRows.CommitEdit(); VisibilityRows.CommitEdit();
            _settings.Save(SyncRows.Items.Cast<Row>().Select(r => r.Preference)); _schedule?.Invoke();
            SyncMessage.Text = "Saved. Automatic sync will check enabled feeds on the next background cycle; dashboard choices apply immediately.";
            ShowStatus();
        }
        catch (Exception ex) { SyncMessage.Text = "Not saved: " + ex.Message; }
    }
    private void Row_Selected(object sender, SelectionChangedEventArgs e) => ShowStatus();
    private void Status_Click(object sender, RoutedEventArgs e) => ShowStatus();
    private void ShowStatus()
    {
        if (_settings == null || SyncDetail == null || SyncRows.SelectedItem is not Row row) return;
        var p = _settings.Get(row.Pilot.CharacterId);
        string Feed(string name, bool enabled, bool permission) => name + ": " + (!enabled ? "Automatic sync off" : !permission ? "Permission missing - reconnect this pilot" : "Enabled");
        var orders = _market?.State.Characters.FirstOrDefault(c => c.CharacterId == row.Pilot.CharacterId);
        var balance = _market?.State.Balances.GetValueOrDefault(row.Pilot.CharacterId);
        SyncDetail.Text = row.Name + "\n" + Feed("Wallet", p.Wallet, row.Pilot.Scopes.Contains(MarketOrdersService.WalletScope)) + "\n" + Feed("Market orders", p.Market, row.Pilot.Scopes.Contains(MarketOrdersService.Scope)) + "\n" + Feed("Industry", p.Industry, IndustryService.Scopes.All(row.Pilot.Scopes.Contains)) +
            "\n" + Feed("PI", p.Planetary, row.Pilot.Scopes.Contains(PlanetaryService.Scope)) + "\n" + Feed("Clones", p.Clones, row.Pilot.Scopes.Contains(OmegaService.Scope)) + "\nPilot information: " + (p.Pilot ? "Enabled; each feed uses its granted permissions" : "Automatic sync off") +
            $"\nSaved balance: {(balance?.Amount != null ? balance.Updated.ToString("dd MMM HH:mm 'UTC'") : "Not loaded")} | Saved orders: {(orders?.Updated > DateTimeOffset.MinValue ? orders.Updated.ToString("dd MMM HH:mm 'UTC'") : "Not loaded")}" +
            "\n" + _settings.Activity.GetValueOrDefault(row.Pilot.CharacterId, "Waiting for the next sync cycle. Saved history is retained.");
    }
}
