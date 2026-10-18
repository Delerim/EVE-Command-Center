using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class MoonReportWindow
{
    private MoonIntelligenceReport? _intelligence;
    private string IntelligencePeriodName => (IntelligencePeriod?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Week";

    private void IntelligenceChanged(object sender, SelectionChangedEventArgs e) => RenderIntelligence();

    private void RenderIntelligence()
    {
        if (_closed || IntelligencePeriod == null || IntelligenceHeadline == null || _service == null) return;
        _intelligence = _service.GetIntelligenceReport(IntelligencePeriodName, DateTimeOffset.UtcNow, _snapshot);
        var r = _intelligence;
        IntelligenceHeadline.Text = r.Title;
        IntelligenceFreshness.Text = r.Freshness;
        IntelligenceStructures.Text = r.Structures.ToString("N0") + " structures / " + r.DrillCount.ToString("N0") + " drills";
        IntelligenceFractures.Text = r.Fractures.ToString("N0");
        IntelligenceFields.Text = $"{r.ActiveFields:N0} verified / {r.InferredFields:N0} inferred";
        IntelligenceUnscheduled.Text = r.NotSet.ToString("N0");
        IntelligenceFuel.Text = r.FuelText;
        IntelligenceAttention.Text = $"{r.FuelLow:N0} under 80 days | {r.FuelUnknown:N0} unknown";
        IntelligenceDespawn.Text = $"{r.LostFields:N0} expired / {r.LossesUnquantified:N0} unknown losses";
        IntelligenceOres.ItemsSource = r.Ores;
        IntelligenceTrend.ItemsSource = r.Trend;
        IntelligenceCoverage.Text = r.Coverage + ". Estimates use saved ESI and ledger; unobserved ore is not counted as verified.";
        if (IntelligencePreview.Text.Length == 0 || !IntelligencePreview.IsKeyboardFocusWithin)
            IntelligencePreview.Text = r.DiscordBody();
    }

    private void IntelligencePreview_Click(object sender, RoutedEventArgs e)
    {
        RenderIntelligence();
        if (_intelligence != null) IntelligencePreview.Text = _intelligence.DiscordBody();
    }
    private void IntelligenceRoutes_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new DiscordWebhookSettingsWindow();
        var owner = EmbeddedModuleHost.ResolveOwner(this);
        if (owner != null && owner != dialog) dialog.Owner = owner;
        dialog.ShowDialog();
        IntelligenceSendStatus.Text = "Choose any number of destinations and tick 'Manual moon operations report' for each.";
    }
    private void IntelligenceSend_Click(object sender, RoutedEventArgs e)
    {
        RenderIntelligence();
        if (_intelligence == null || _intelligence.DataUpdatedUtc == null ||
            DateTimeOffset.UtcNow - _intelligence.DataUpdatedUtc > TimeSpan.FromMinutes(75))
        {
            IntelligenceSendStatus.Text = "Refresh the corporation ESI data before sending a report.";
            return;
        }
        if (!DiscordWebhookService.Current.HasRoutes(DiscordWebhookService.MoonReportManual))
        {
            IntelligenceSendStatus.Text = "No manual report destination enabled. Configure Discord webhooks first.";
            return;
        }
        if (System.Windows.MessageBox.Show(EmbeddedModuleHost.ResolveOwner(this),
                "Queue this report to all enabled Discord destinations subscribed to manual Moon Operations reports?",
                "Send moon operations report", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        // New ID for every user-confirmed manual delivery. The publisher routes independently per destination.
        DiscordWebhookService.Current.Publish(DiscordWebhookService.MoonReportManual,
            "Moon operations | " + _intelligence.Period, _intelligence.DiscordBody(),
            "manual:" + Guid.NewGuid().ToString("N"));
        IntelligenceSendStatus.Text = "Report queued for subscribed Discord destinations. Check Discord for delivery.";
    }
    private void IntelligenceSaveSchedule_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!int.TryParse(IntelligenceHourUtc.Text, out var hour) || hour is < 0 or > 23)
                throw new ArgumentException("Enter a UTC hour between 0 and 23.");
            MoonIntelligenceSchedule.Current.Save(IntelligenceDaily.IsChecked == true,
                IntelligenceWeekly.IsChecked == true, IntelligenceMonthly.IsChecked == true, hour);
            IntelligenceSendStatus.Text = "Report schedule saved locally. It runs only while EVE Command Center is open and ESI data is fresh. Configure corresponding destination subscriptions in Discord webhooks.";
        }
        catch (Exception ex) { IntelligenceSendStatus.Text = "Schedule not saved: " + ex.Message; }
    }
    private void LoadIntelligenceSchedule()
    {
        var settings = MoonIntelligenceSchedule.Current.Options();
        IntelligenceDaily.IsChecked = settings.Daily;
        IntelligenceWeekly.IsChecked = settings.Weekly;
        IntelligenceMonthly.IsChecked = settings.Monthly;
        IntelligenceHourUtc.Text = settings.SendHourUtc.ToString();
    }
}
