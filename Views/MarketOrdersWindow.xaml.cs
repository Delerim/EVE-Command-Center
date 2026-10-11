using System.Globalization;
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
    private bool _updating;
    private CancellationTokenSource? _refresh;
    private readonly System.Windows.Threading.DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    public MarketOrdersWindow()
    {
        InitializeComponent();
        _progressTimer.Tick += (_, _) => ShowProgress();
        WorkspaceLayout.AttachMarket(this);
        TableSorting.Attach(Orders); TableSorting.Attach(TransactionsGrid); TableSorting.Attach(JournalGrid); TableSorting.Attach(AllocationSplits);
        Loaded += async (_, _) =>
        {
            if (_service != null) return;
            try
            {
                var ops = BackgroundOperations.Current;
                Initialize(ops.MarketOrders, (await ops.Sso.LoadPilotsAsync()).ToArray());
            }
            catch (Exception ex) { Status.Text = "Market unavailable: " + MarketOrdersService.Failure(ex); }
        };
        Closed += (_, _) => { _life.Cancel(); _progressTimer.Stop(); };
    }
    internal void Initialize(MarketOrdersService service, IReadOnlyList<EvePilotProfile> pilots)
    {
        _service = service; _pilots = pilots; RebuildPilots(); RenderOrders();
    }
    private long _activePilot;
    private void RebuildPilots()
    {
        if (_service == null) return;
        _updating = true;
        if (!_pilots.Any(p => p.CharacterId == _activePilot))
            _activePilot = _pilots.FirstOrDefault(p => _service.State.SelectedCharacters.Contains(p.CharacterId))?.CharacterId ?? _pilots.FirstOrDefault()?.CharacterId ?? 0;
        if (_activePilot > 0 && !_service.State.SelectedCharacters.SetEquals(new[] { _activePilot }) && !_service.Busy && _service.Error.Length == 0)
            _service.Select(new[] { _activePilot });
        _choices = _pilots.Select(p => new CharacterChoice
        {
            Id = p.CharacterId, Name = p.CharacterName, Selected = p.CharacterId == _activePilot,
            Access = (p.Scopes.Contains(MarketOrdersService.Scope), p.Scopes.Contains(MarketOrdersService.WalletScope)) switch
            { (true, true) => "Orders + wallet", (true, false) => "Orders only", (false, true) => "Wallet only", _ => "Reconnect for access" }
        }).ToList();
        Characters.ItemsSource = _choices; _updating = false;
    }
    private void Pilot_Click(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: CharacterChoice pilot }) SelectPilot(pilot.Id); }
    internal void SelectPilot(long id)
    {
        if (_service == null || _refresh != null || !_pilots.Any(p => p.CharacterId == id)) return;
        try { _service.Select(new[] { id }); _activePilot = id; RebuildPilots(); RenderOrders(); }
        catch (Exception ex) { RebuildPilots(); Status.Text = MarketOrdersService.Failure(ex); }
    }
    private HashSet<long> VisiblePilots() => _activePilot > 0 ? new() { _activePilot } : new();
    private DateTimeOffset Since => Period.SelectedIndex switch
    { 1 => new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero), 2 => DateTimeOffset.UtcNow.AddDays(-7), 3 => DateTimeOffset.UtcNow.AddDays(-30), _ => DateTimeOffset.MinValue };
    private string PilotName(long id) => _pilots.FirstOrDefault(p => p.CharacterId == id)?.CharacterName ?? _service?.State.Characters.FirstOrDefault(c => c.CharacterId == id)?.Name ?? "Unlinked pilot";
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshMarket(MarketTabs.SelectedIndex switch { 1 or 3 => 3, 4 or 5 => 4, 6 => 5, 7 => 6, 8 => 4, _ => 0 });
    private void CancelRefresh_Click(object sender, RoutedEventArgs e) => _refresh?.Cancel();
    private void ShowProgress()
    {
        if (_refresh == null) return;
        Status.Text = _refresh.IsCancellationRequested ? "Cancelling refresh; saved data is retained..." : _service?.Busy == true ? _service.Progress : "Preparing refresh...";
        HistoryStatus.Text = "Refresh in progress. EVE rate limits may add waiting time.";
    }
    internal async Task RefreshMarket(int mode, IReadOnlyList<EvePilotProfile>? linkedPilots = null)
    {
        if (_service == null || _service.Busy || _refresh != null) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(_life.Token);
        _refresh = request; string? failure = null;
        RefreshButton.IsEnabled = Characters.IsEnabled = false;
        CancelRefresh.Visibility = Visibility.Visible; _progressTimer.Start();
        try
        {
            ShowProgress();
            _pilots = linkedPilots ?? (await BackgroundOperations.Current.Sso.LoadPilotsAsync()).ToArray();
            RebuildPilots();
            if (mode == 6)
            {
                await _service.RefreshBalanceAsync(_pilots, request.Token);
                await _service.RefreshJournalAsync(_pilots, request.Token);
                await _service.RefreshTransactionsAsync(_pilots, request.Token);
            }
            else if (mode == 3) await _service.RefreshHistoryAsync(_pilots, request.Token);
            else if (mode == 4) await _service.RefreshTransactionsAsync(_pilots, request.Token);
            else if (mode == 5) await _service.RefreshJournalAsync(_pilots, request.Token);
            else await _service.RefreshAsync(_pilots, request.Token);
            _service.ReloadAllocations();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failure = MarketOrdersService.Failure(ex); }
        finally
        {
            _progressTimer.Stop(); _refresh = null;
            RefreshButton.IsEnabled = Characters.IsEnabled = true;
            CancelRefresh.Visibility = Visibility.Collapsed; RenderOrders();
            if (failure != null) Status.Text = failure;
            else if (request.IsCancellationRequested) Status.Text = "Refresh cancelled. Saved data is retained; you can refresh again.";
        }
    }
    private void MarketTab_Changed(object sender, SelectionChangedEventArgs e) { if (e.Source == MarketTabs) RenderOrders(); }
    private void Filter_Changed(object sender, SelectionChangedEventArgs e) => RenderOrders();
    private void Search_Changed(object sender, TextChangedEventArgs e) => RenderOrders();
    private void Order_Selected(object sender, SelectionChangedEventArgs e)
    { if (OrderDetail != null) OrderDetail.Text = (Orders.SelectedItem as OrderRow)?.Detail ?? "Select an order to inspect its last observation."; }
    private void Transaction_Selected(object sender, SelectionChangedEventArgs e)
    { if (OrderDetail != null) OrderDetail.Text = (TransactionsGrid.SelectedItem as TransactionRow)?.Detail ?? "Select a purchase or sale for details."; ShowPurchaseAllocation(); }
    private void Journal_Selected(object sender, SelectionChangedEventArgs e)
    { if (OrderDetail != null) OrderDetail.Text = (JournalGrid.SelectedItem as JournalRow)?.Detail ?? "Select wallet activity to inspect its related trade."; }
    private void RenderOrders()
    {
        if (_service == null || _updating) return;
        int tab = MarketTabs.SelectedIndex; OrderSide.Visibility = tab == 9 ? Visibility.Visible : Visibility.Collapsed; var ids = VisiblePilots(); string query = Search.Text.Trim();
        PilotSummary.Text = _activePilot > 0 ? PilotName(_activePilot) : "Choose a pilot";
        RenderBalance();
        WalletOverviewPanel.Visibility = tab == 7 ? Visibility.Visible : Visibility.Collapsed;
        TradeSide.Visibility = tab == 8 ? Visibility.Visible : Visibility.Collapsed;
        AllocationFilters.Visibility = tab is 4 or 5 ? Visibility.Visible : Visibility.Collapsed;
        AllocatedColumn.Visibility = UnallocatedColumn.Visibility = DestinationsColumn.Visibility = tab is 4 or 5 ? Visibility.Visible : Visibility.Collapsed;
        if (tab is not (4 or 5)) HidePurchaseAllocation();
        DestinationsColumn.Header = tab == 5 ? "Sold for" : "Bought for";
        ((ComboBoxItem)AllocationFilter.Items[0]).Content = tab == 5 ? "All sales" : "All purchases";
        Orders.Visibility = tab is 4 or 5 or 6 or 7 or 8 ? Visibility.Collapsed : Visibility.Visible;
        TransactionsGrid.Visibility = tab is 4 or 5 or 8 ? Visibility.Visible : Visibility.Collapsed;
        JournalGrid.Visibility = tab == 6 ? Visibility.Visible : Visibility.Collapsed;
        PeriodControls.Visibility = tab is 4 or 5 or 6 or 8 ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Content = tab switch { 1 or 3 => "Refresh history", 4 or 5 or 8 => "Refresh transactions", 6 => "Refresh wallet activity", 7 => "Refresh wallet", _ => "Refresh open orders" };
        if (tab is 4 or 5) RenderTransactions(tab == 4, query, ids);
        else if (tab == 6) RenderJournal(query, ids);
        else if (tab == 7) RenderWalletOverview();
        else if (tab == 8) RenderTransactions(null, query, ids);
        else
        {
            bool buy = tab is 0 or 1; bool historyTab = tab is 1 or 3; bool missingTab = tab == 9;
            var snapshots = _service.State.Characters.Where(c => ids.Contains(c.CharacterId)).ToArray();
            string Fresh(MarketCharacterSnapshot c) => c.Updated == default ? "Not refreshed" : $"{c.Updated:dd MMM HH:mm} UTC" + (c.Error.Length > 0 || c.NextRefresh <= DateTimeOffset.UtcNow ? " / review" : " / cached");
            var history = _service.History.Where(h => ids.Contains(h.OwnerId)).ToArray();
            var terminal = history.Select(h => (h.OwnerId, h.Order.OrderId)).ToHashSet();
            var confirmed = history.Select(h => new OrderRow(h.OwnerId, h.Order, PilotName(h.OwnerId), "Confirmed history", h.Order.State == "cancelled" ? "Cancelled" : "Expired", h.Observed, null));
            var open = snapshots.SelectMany(c => c.Orders.Where(o => !o.Corporation && !terminal.Contains((c.CharacterId, o.OrderId))).Select(o => new OrderRow(c.CharacterId, o, c.Name, Fresh(c), "Open (last snapshot)", c.Updated, null)));
            var missing = snapshots.SelectMany(c => c.Unobserved.Where(o => !terminal.Contains((c.CharacterId, o.Order.OrderId))).Select(o => new OrderRow(c.CharacterId, o.Order, c.Name, Fresh(c), "Not observed", o.LastSeen, o.MissingSince)));
            var shown = (historyTab ? confirmed : missingTab ? missing : open).Where(r => (missingTab || r.Order.Buy == buy) && (query.Length == 0 || (r.Order.Name + " " + r.Owner + " " + r.Order.Location).Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
            var selection = Orders.SelectedItem as OrderRow; Orders.ItemsSource = shown;
            if (selection != null) Orders.SelectedItem = shown.FirstOrDefault(r => r.Order.OrderId == selection.Order.OrderId && r.OwnerId == selection.OwnerId);
            Totals.Text = historyTab ? $"{shown.Length:N0} confirmed {(buy ? "buy" : "sell")} history records | {shown.Count(r => r.State == "Cancelled"):N0} cancelled | {shown.Count(r => r.State == "Expired"):N0} expired"
                : missingTab ? $"{shown.Length:N0} orders needing review | Final outcome not confirmed"
                : $"{shown.Length:N0} open {(buy ? "buy" : "sell")} orders | Remaining value {shown.Sum(r => r.Order.RemainingValue):N2} ISK | Last saved observations";
            HistoryStatus.Text = historyTab ? _service.HistoryStatus : "";
            Status.Text = _service.Error.Length > 0 ? _service.Error : historyTab ? "" : string.Join("\n", snapshots.Select(c => $"{c.Name}: {Fresh(c)}" + (c.Error.Length > 0 ? " | " + MarketOrdersService.DisplayError(c.Error) : "")));
            ObservationSummary.Text = historyTab ? "Confirmed cancellations and expiries. Actual purchases and sales have their own tabs."
                : missingTab ? "Absent from a successful refresh. Disappearance does not confirm a fill, cancellation or sale."
                : "Remaining order value is a commitment, not confirmed spending or revenue.";
            OrderDetail.Text = (Orders.SelectedItem as OrderRow)?.Detail ?? "Select an order to inspect its last observation.";
        }
        int count = tab == 6 ? JournalGrid.Items.Count : tab is 4 or 5 or 8 ? TransactionsGrid.Items.Count : Orders.Items.Count;
        EmptyState.Visibility = count == 0 && tab != 7 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = ids.Count == 0 ? "Choose a pilot to begin" : "No records in this view";
        EmptyHint.Text = ids.Count == 0 ? "Select a pilot card above. Previously imported records stay saved." : "Try another pilot, search or period, or refresh this tab. A failed refresh does not confirm zero activity.";
        var focused = _pilots.FirstOrDefault(p => p.CharacterId == _activePilot);
        string neededScope = tab is 4 or 5 or 6 or 7 or 8 ? MarketOrdersService.WalletScope : MarketOrdersService.Scope;
        if (focused != null && !focused.Scopes.Contains(neededScope)) Status.Text = "This pilot needs " + (neededScope == MarketOrdersService.WalletScope ? "wallet" : "market order") + " access. Reconnect the pilot to grant it; saved history is retained.";
        ShowProgress();
    }
    private void RenderTransactions(bool? buy, string query, HashSet<long> ids)
    {
        var rows = _service!.Transactions.Where(r => ids.Contains(r.OwnerId) && r.Transaction.Date >= Since && (buy == null ? r.Transaction.TypeId == 44992 : r.Transaction.Buy == buy))
            .Select(r => new TransactionRow(r.OwnerId, r.Transaction, PilotName(r.OwnerId), r.Observed,
                _service.Journal.Any(j => j.OwnerId == r.OwnerId && j.Entry.Id == r.Transaction.JournalRefId),
                (r.Transaction.Buy ? _service.PurchaseAllocations : _service.SaleAllocations).FirstOrDefault(a => a.OwnerId == r.OwnerId && a.TransactionId == r.Transaction.Id)?.Splits ?? new()))
            .Where(r => query.Length == 0 || (r.Transaction.Name + " " + r.Owner + " " + r.Transaction.Location + " " + r.Destinations).Contains(query, StringComparison.OrdinalIgnoreCase)).Where(r => buy == null || AllocationFilter.SelectedIndex == 0 || (AllocationFilter.SelectedIndex == 1 ? r.Unallocated > 0 : r.Unallocated == 0)).OrderByDescending(r => r.Transaction.Date).ToArray();
        var selection = TransactionsGrid.SelectedItem as TransactionRow; TransactionsGrid.ItemsSource = rows;
        if (selection != null) TransactionsGrid.SelectedItem = rows.FirstOrDefault(r => r.OwnerId == selection.OwnerId && r.Transaction.Id == selection.Transaction.Id);
        Totals.Text = $"{rows.Length:N0} {(buy == null ? "PLEX trades" : buy == true ? "purchases" : "sales")} | Gross total {rows.Sum(r => r.Transaction.Total):N2} ISK | Trade value, not profit";
        HistoryStatus.Text = _service.TransactionStatus; Status.Text = _service.Error;
        ObservationSummary.Text = $"{rows.Length:N0} {(buy == null ? "PLEX trades" : buy == true ? "purchases" : "sales")} shown | Pilot, search and period applied. Select a trade to assign its project. Fees and physical stock are separate.";
        if (buy == null)
        {
            decimal bought = rows.Where(r => r.Transaction.Buy).Sum(r => r.Transaction.Total), sold = rows.Where(r => !r.Transaction.Buy).Sum(r => r.Transaction.Total);
            long buyQty = rows.Where(r => r.Transaction.Buy).Sum(r => r.Transaction.Quantity), sellQty = rows.Where(r => !r.Transaction.Buy).Sum(r => r.Transaction.Quantity);
            Totals.Text = $"PLEX bought {buyQty:N0} | Sold {sellQty:N0} | Spent {bought:N2} ISK | Received {sold:N2} ISK";
            ObservationSummary.Text = $"Average buy {(buyQty > 0 ? (bought / buyQty).ToString("N2") : "N/A")} ISK | Average sell {(sellQty > 0 ? (sold / sellQty).ToString("N2") : "N/A")} ISK | Imported personal trades only; not PLEX holdings or profit.";
        }
        OrderDetail.Text = (TransactionsGrid.SelectedItem as TransactionRow)?.Detail ?? "Select a purchase or sale for details.";
    }
    private void RenderJournal(string query, HashSet<long> ids)
    {
        var trades = _service!.Transactions.ToLookup(t => (t.OwnerId, t.Transaction.JournalRefId));
        var rows = _service.Journal.Where(j => ids.Contains(j.OwnerId) && j.Entry.Date >= Since)
            .Select(j => new JournalRow(j.OwnerId, j.Entry, PilotName(j.OwnerId), j.Observed, trades[(j.OwnerId, j.Entry.Id)].Select(t => t.Transaction).ToArray()))
            .Where(j => query.Length == 0 || (j.Activity + " " + j.Owner + " " + j.Related + " " + j.Note).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var selected = JournalGrid.SelectedItem as JournalRow; JournalGrid.ItemsSource = rows;
        if (selected != null) JournalGrid.SelectedItem = rows.FirstOrDefault(r => r.OwnerId == selected.OwnerId && r.Entry.Id == selected.Entry.Id);
        decimal incoming = rows.Where(r => r.Entry.Amount > 0).Sum(r => r.Entry.Amount ?? 0), outgoing = rows.Where(r => r.Entry.Amount < 0).Sum(r => -(r.Entry.Amount ?? 0));
        Totals.Text = $"Money in {incoming:N2} ISK | Money out {outgoing:N2} ISK | Net change {incoming - outgoing:N2} ISK";
        HistoryStatus.Text = _service.JournalStatus; Status.Text = _service.Error;
        ObservationSummary.Text = $"{rows.Length:N0} wallet entries | Cash flow from the shown entries only; includes transfers, fees and other activity. Not project profit. {rows.Count(r => r.Entry.Amount == null):N0} entries without an amount are excluded from totals.";
        OrderDetail.Text = (JournalGrid.SelectedItem as JournalRow)?.Detail ?? "Select wallet activity to inspect its related trade. Transaction totals are not added again.";
    }
    private sealed class CharacterChoice
    {
        public long Id { get; set; } public string Name { get; set; } = ""; public bool Selected { get; set; } public string Access { get; set; } = "";
        public string Portrait => $"https://images.evetech.net/characters/{Id}/portrait?size=64";
    }
    private sealed record PilotChoice(long Id, string Name) { public override string ToString() => Name; }
    private sealed record TransactionRow(long OwnerId, MarketTransaction Transaction, string Owner, DateTimeOffset Observed, bool JournalMatched, List<PurchaseAllocation> Splits)
    {
        public long Allocated => Splits.Sum(a => a.Quantity);
        public long Unallocated => Math.Max(0, Transaction.Quantity - Allocated);
        public string Destinations => Splits.Count == 0 ? "Unallocated" : string.Join(" / ", Splits.Select(a => a.ProjectName).Distinct());
        public string AllocationColor => Unallocated == 0 ? "#76D7CB" : "#EDAD83";
        public string Color => Transaction.Buy ? "#EDAD83" : "#76D7CB";
        public string Detail => $"{Transaction.Side}: {Transaction.Quantity:N0} {Transaction.Name}, {Transaction.Total:N2} ISK at {Transaction.Location}. Confirmed by the wallet feed. Imported {Observed:dd MMM HH:mm} UTC. " + (JournalMatched ? "Related wallet entry found. " : "Related wallet entry not imported yet. ") + $"{(Transaction.Buy ? "Bought for" : "Sold for")}: {Destinations}. {Unallocated:N0} units unallocated.";
    }
    private sealed record JournalRow(long OwnerId, EveWalletJournalEntry Entry, string Owner, DateTimeOffset Observed, MarketTransaction[] Trades)
    {
        public string Activity => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Entry.RefType.Replace('_', ' '));
        public string Amount => Entry.Amount?.ToString("N2") ?? "Unknown";
        public string Balance => Entry.Balance?.ToString("N2") ?? "Unknown";
        public string Note => string.IsNullOrWhiteSpace(Entry.Reason) ? Entry.Description ?? "" : Entry.Reason;
        public string Color => Entry.Amount > 0 ? "#76D7CB" : Entry.Amount < 0 ? "#EDAD83" : "#A5BBC1";
        public string Related => Trades.Length == 0 ? "No linked trade imported" : string.Join(" / ", Trades.Select(t => t.Name).Distinct());
        public string? Icon => Trades.FirstOrDefault()?.Icon;
        public string Detail => $"{Activity} for {Owner}, imported {Observed:dd MMM HH:mm} UTC. {Note} " + (Trades.Length == 0 ? "No purchase or sale linked by EVE's journal reference. Fees are not assigned to a project automatically." : "Linked by EVE's journal reference: " + string.Join("; ", Trades.Select(t => $"{t.Side}: {t.Quantity:N0} {t.Name} at {t.Location}")) + ". The wallet amount is counted once; trade values are not added again.");
    }
    private sealed record OrderRow(long OwnerId, MarketOrder Order, string Owner, string Freshness, string State, DateTimeOffset LastSeen, DateTimeOffset? MissingSince)
    {
        public string Color => Order is HistoricalMarketOrder ? "#A5BBC1" : MissingSince.HasValue ? "#E3BC70" : Freshness.Contains("review") ? "#9DB6BD" : "#76D7CB";
        public string SeenLabel => LastSeen.ToString("dd MMM HH:mm 'UTC'");
        public string Detail => Order is HistoricalMarketOrder ? $"{State}, confirmed by EVE order history on {SeenLabel}. This is not a wallet transaction and does not establish spending or profit." : MissingSince.HasValue ? $"Last returned {SeenLabel}; absent from a successful refresh on {MissingSince:dd MMM HH:mm} UTC. Final state needs confirmed history; no spending is inferred." : $"Last returned {SeenLabel}. Quantities reflect this snapshot, not confirmed transactions.";
    }
}
