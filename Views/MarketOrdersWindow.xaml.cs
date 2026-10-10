using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class MarketOrdersWindow : Window
{
    private readonly CancellationTokenSource _life = new();
    private MarketOrdersService? _service;
    private IReadOnlyList<EvePilotProfile> _pilots = Array.Empty<EvePilotProfile>();
    private List<CharacterChoice> _choices = new();
    public MarketOrdersWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_service != null) return;
            try
            {
                var ops = BackgroundOperations.Current;
                Initialize(ops.MarketOrders, (await ops.Sso.LoadPilotsAsync()).ToArray());
            }
            catch (Exception ex) { Status.Text = "Market unavailable: " + ex.Message; }
        };
        Closed += (_, _) => _life.Cancel();
    }
    internal void Initialize(MarketOrdersService service, IReadOnlyList<EvePilotProfile> pilots)
    {
        _service = service; _pilots = pilots;
        _choices = pilots.Select(p => new CharacterChoice { Id = p.CharacterId, Name = p.CharacterName, Selected = service.State.SelectedCharacters.Contains(p.CharacterId) }).ToList();
        Characters.ItemsSource = _choices; RenderOrders();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null || _service.Busy) return;
        RefreshButton.IsEnabled = false;
        try
        {
            _pilots = (await BackgroundOperations.Current.Sso.LoadPilotsAsync()).ToArray();
            var selectedIds = _choices.Where(c => c.Selected).Select(c => c.Id).ToHashSet();
            _choices = _pilots.Select(p => new CharacterChoice { Id = p.CharacterId, Name = p.CharacterName, Selected = selectedIds.Contains(p.CharacterId) }).ToList();
            Characters.ItemsSource = _choices;
            _service.Select(selectedIds);
            Status.Text = "Refreshing selected characters through the shared ESI queue...";
            await _service.RefreshAsync(_pilots, _life.Token);
            if (!_life.IsCancellationRequested) RenderOrders();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_life.IsCancellationRequested) Status.Text = "Market refresh unavailable: " + ex.Message; }
        finally { if (!_life.IsCancellationRequested) RefreshButton.IsEnabled = true; }
    }
    private void Side_Changed(object sender, SelectionChangedEventArgs e) { if (e.Source == SideTabs) RenderOrders(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) => RenderOrders();
    private void RenderOrders()
    {
        if (_service == null) return;
        bool buy = (SideTabs.SelectedItem as TabItem)?.Tag?.ToString() != "sell";
        var snapshots = _service.State.Characters.Where(c => _service.State.SelectedCharacters.Contains(c.CharacterId)).ToArray();
        string query = Search.Text.Trim();
        string Fresh(MarketCharacterSnapshot c) => c.Updated == default ? "Not refreshed" :
            $"{c.Updated:dd MMM HH:mm} UTC" + (c.Error.Length > 0 || c.Updated < DateTimeOffset.UtcNow.AddMinutes(-20) ? " / REVIEW" : " / cached");
        var all = snapshots.SelectMany(c => c.Orders.Where(o => !o.Corporation).Select(o => new OrderRow(o, c.Name, Fresh(c)))).ToArray();
        Orders.ItemsSource = all.Where(r => r.Order.Buy == buy && (query.Length == 0 || (r.Order.Name + " " + r.Owner + " " + r.Order.OrderId).Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        bool incomplete = snapshots.Length != _service.State.SelectedCharacters.Count || snapshots.Any(c => c.Error.Length > 0 || c.Updated == default || c.Updated < DateTimeOffset.UtcNow.AddMinutes(-20));
        Totals.Text = _service.State.SelectedCharacters.Count == 0 ? "Choose characters above, then save selection / refresh." :
            $"{(incomplete ? "PARTIAL / STALE CACHE" : "CACHED OPEN ORDERS")} | Buy {all.Count(r => r.Order.Buy):N0}: {all.Where(r => r.Order.Buy).Sum(r => r.Order.RemainingValue):N2} ISK | Sell {all.Count(r => !r.Order.Buy):N0}: {all.Where(r => !r.Order.Buy).Sum(r => r.Order.RemainingValue):N2} ISK";
        Status.Text = _service.Error.Length > 0 ? _service.Error : string.Join("\n", snapshots.Select(c =>
            $"{c.Name}: {Fresh(c)} | {(c.Error.Length > 0 ? c.Error : $"Next refresh after {c.NextRefresh:HH:mm} UTC")}"));
    }
    private sealed class CharacterChoice { public long Id { get; set; } public string Name { get; set; } = ""; public bool Selected { get; set; } }
    private sealed record OrderRow(MarketOrder Order, string Owner, string Freshness);
}
