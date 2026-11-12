using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EveCommandCenter.Services;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

namespace EveCommandCenter.Views;

public partial class JaniceSimulationView : UserControl
{
    private JaniceSnapshot? _snapshot;
    private CancellationTokenSource? _load;
    public JaniceSimulationView()
    {
        InitializeComponent();
        TableSorting.Attach(Lines);
        Unloaded += (_, _) =>
        {
            if (_load != null) { _load.Cancel(); if (_snapshot == null) Clear("LOAD CANCELLED"); }
            _load = null; LoadButton.IsEnabled = true;
        };
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        _load?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _load = cancellation;
        _snapshot = null; Clear("LOADING APPRAISAL"); LoadButton.IsEnabled = false;
        try
        {
            var snapshot = await JaniceAppraisalClient.LoadAsync(AppraisalUrl.Text, cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            ShowSnapshot(snapshot);
        }
        catch (OperationCanceledException) { if (_load == cancellation) Clear("LOAD CANCELLED"); }
        catch (Exception ex) { if (_load == cancellation) Clear("UNVERIFIED: " + ex.Message); }
        finally
        {
            if (_load == cancellation) { _load = null; LoadButton.IsEnabled = true; }
        }
    }

    private void Url_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _load?.Cancel(); _load = null; LoadButton.IsEnabled = true;
        _snapshot = null; Clear("LOAD APPRAISAL TO COMPARE");
    }
    private void Inputs_Changed(object sender, RoutedEventArgs e) { if (IsInitialized) Recalculate(); }
    internal void ShowSnapshot(JaniceSnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotText.Text = $"Appraisal {snapshot.Code} | Saved price snapshot: {snapshot.DatasetTime.ToLocalTime():dd MMM yyyy HH:mm zzz} | {snapshot.Items.Count} item lines. Loading does not refresh its prices.";
        Recalculate();
    }

    private void Clear(string message)
    {
        Lines.ItemsSource = null; TotalsText.Text = "";
        DecisionText.Text = message; DecisionText.Foreground = new SolidColorBrush(Color.FromRgb(255, 209, 102));
        if (_snapshot == null) SnapshotText.Text = "Prices come from the saved appraisal. No current profitability decision is available until a complete appraisal is loaded.";
    }

    private void Recalculate()
    {
        if (_snapshot == null) return;
        try
        {
            decimal Read(TextBox box) => decimal.TryParse(box.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var v)
                ? v : throw new ArgumentException("Enter valid percentages and ISK costs.");
            var settings = new JaniceSimulationSettings(Read(PayoutPercent), (JanicePriceSide)PayoutSide.SelectedIndex,
                (JanicePriceSide)ReferenceSide.SelectedIndex, Read(MaximumDiscount), Read(DirectSaleFees), Read(DirectCosts), Read(CorpCosts));
            var result = JaniceProfitability.Calculate(_snapshot, settings);
            Lines.ItemsSource = result.Lines;
            bool older = DateTimeOffset.UtcNow - _snapshot.DatasetTime > TimeSpan.FromMinutes(30) ||
                _snapshot.DatasetTime > DateTimeOffset.UtcNow.AddMinutes(5);
            DecisionText.Text = result.MeetsLimit
                ? (older ? "OLDER SNAPSHOT: within discount limit; refresh appraisal" : "WITHIN DISCOUNT LIMIT: snapshot estimate; check executable prices")
                : $"EXCESS DISCOUNT: {result.Lines.Count(x => !x.MeetsLimit)} of {result.Lines.Count} lines exceed {settings.MaximumDiscountPercent:0.##}%" + (older ? " | OLDER SNAPSHOT" : "");
            DecisionText.Foreground = new SolidColorBrush(result.MeetsLimit && !older
                ? Color.FromRgb(116, 214, 201) : Color.FromRgb(255, 173, 131));
            decimal buy100 = _snapshot.Items.Sum(x => x.Buy), split100 = _snapshot.Items.Sum(x => x.Split), sell100 = _snapshot.Items.Sum(x => x.Sell);
            string Signed(decimal v) => v.ToString("+#,##0.00;-#,##0.00;0.00", CultureInfo.CurrentCulture);
            TotalsText.Text = $"100% Jita: buy {buy100:N2} | split {split100:N2} | sell {sell100:N2} ISK\n" +
                $"Corp net ({settings.PayoutPercent:0.##}% {settings.PayoutSide.ToString().ToLowerInvariant()}): {result.Payout:N2} ISK | Direct net (100% {settings.ReferenceSide.ToString().ToLowerInvariant()}): {result.Reference:N2} ISK | Gap: {Signed(result.Difference)} ISK ({result.DiscountPercent:N2}% discount)\n" +
                $"Buy/sell quote spread: {(1 - buy100 / sell100) * 100:N2}% before the corp discount. " +
                $"Corp payout needs at least {decimal.Ceiling(result.RequiredPayoutPercent * 1000) / 1000:N3}% of Jita {settings.PayoutSide.ToString().ToLowerInvariant()} for every line to stay within the limit. " +
                $"90% scenarios: buy {buy100 * .9m:N2} | split {split100 * .9m:N2} | sell {sell100 * .9m:N2} ISK (before route costs).";

        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            Clear("CHECK INPUTS: " + ex.Message);
        }
    }
}
