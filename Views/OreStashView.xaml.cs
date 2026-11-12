using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;

namespace EveCommandCenter.Views;

public partial class OreStashView : UserControl
{
    private OreStashService? _service;
    public OreStashView()
    {
        InitializeComponent(); TableSorting.Attach(OreRows);
        Loaded += async (_, _) =>
        {
            if (_service == null) await AttachAsync(BackgroundOperations.Current.OreStash);
        };
        Unloaded += (_, _) => { if (_service != null) _service.Changed -= RenderState; _service = null; };
    }
    internal async Task AttachAsync(OreStashService service)
    {
        if (_service != null) _service.Changed -= RenderState;
        _service = service; _service.Changed += RenderState;
        var s = service.State.Settings;
        CorpPercent.Text = s.CorpPercent.ToString(CultureInfo.CurrentCulture);
        SellGap.Text = s.SellGapPercent.ToString(CultureInfo.CurrentCulture); AvoidGap.Text = s.AvoidGapPercent.ToString(CultureInfo.CurrentCulture);
        MinimumAlert.Text = (s.MinimumAlertIsk / 1000000).ToString(CultureInfo.CurrentCulture);
        Monitoring.IsChecked = s.CharacterId == 0 || s.Enabled; Alerts.IsChecked = s.Notifications;
        try
        {
            var pilots = await service.PilotsAsync();
            if (_service != service) return;
            MainPilot.ItemsSource = pilots;
            MainPilot.SelectedItem = pilots.FirstOrDefault(x => x.CharacterId == s.CharacterId);
            RenderState();
            if (pilots.Count == 0) StatusText.Text = "No linked characters. Link your main in Setup, then reopen this tab.";
        }
        catch (Exception ex) { StatusText.Text = "Could not load linked characters: " + ex.Message; }
    }
    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_service == null) return;
        try
        {
            if (MainPilot.SelectedItem is not EvePilotProfile pilot) throw new ArgumentException("Choose your main character first.");
            decimal Read(TextBox box) => decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var n)
                ? n : throw new ArgumentException("Enter valid percentages and alert value.");
            _service.Configure(new(pilot.CharacterId, Monitoring.IsChecked == true, Alerts.IsChecked == true,
                Read(CorpPercent), Read(SellGap), Read(AvoidGap), Read(MinimumAlert) * 1000000));
            await _service.RefreshAsync(force: true);
        }
        catch (Exception ex) { StatusText.Text = "CHECK SETTINGS: " + ex.Message; }
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    { if (_service != null) await _service.RefreshAsync(force: true); }
    private void RenderState()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(RenderState); return; }
        if (_service == null) return;
        var state = _service.State;
        StatusText.Text = _service.Status;
        var rows = _service.LastScanSucceeded ? state.Rows : state.Rows.Select(x => x with
            { Advice = "ASSESS", Reason = "Cached stash is unverified. Scan before deciding.", DataVerified = false }).ToList();
        OreRows.ItemsSource = rows;
        int priced = rows.Count(x => x.CorpPayout.HasValue);
        TotalsText.Text = rows.Count == 0 ? "No ore positions available for the selected main character." :
            $"{rows.Sum(x => x.Quantity):N0} cached ore units | Estimated corp payout: {rows.Sum(x => x.CorpPayout ?? 0):N0} ISK" +
            (priced < rows.Count ? $" (partial: {priced}/{rows.Count} priced)" : "") +
            $" | {rows.Count(x => x.Advice == "SELL")} ore types at a reasonable selling point. " +
            (!_service.LastScanSucceeded ? "Unverified saved snapshot." : "");
        DatesText.Text = state.AssetsObserved.HasValue
            ? $"Assets dated {state.AssetsObserved.Value.ToLocalTime():dd MMM HH:mm}; provider cache expires {state.AssetsExpire?.ToLocalTime():HH:mm}. Last scan {state.Checked?.ToLocalTime():dd MMM HH:mm}. Cached inventory can lag ship and hangar changes."
            : "No inventory snapshot yet. Asset scope is required; in-space cargo coverage also uses the current ship scope.";
        ApplyButton.IsEnabled = !_service.Busy; RefreshButton.IsEnabled = !_service.Busy && state.Settings.CharacterId > 0 && state.Settings.Enabled;
    }
}
