using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using Microsoft.Win32;

namespace EveCommandCenter.Views;

public partial class ContractsWindow
{
    private BuybackAnalyticsSnapshot? _analyticsSnapshot;
    private bool _analyticsLoading;

    private string AnalyticsPeriodName => (AnalyticsPeriod?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Month";

    private (DateTime Start, DateTime End) AnalyticsRange()
    {
        DateTime date = AnalyticsDate?.SelectedDate ?? DateTime.UtcNow.Date;
        return BuybackAnalytics.Range(date, AnalyticsPeriodName, AnalyticsFrom?.SelectedDate, AnalyticsTo?.SelectedDate);
    }

    private void AnalyticsChanged(object sender, RoutedEventArgs e) => RenderAnalytics();
    private void AnalyticsSearch_Changed(object sender, TextChangedEventArgs e) => FilterAnalytics();
    private void AnalyticsToday_Click(object sender, RoutedEventArgs e) => AnalyticsDate.SelectedDate = DateTime.UtcNow.Date;
    private void AnalyticsPrevious_Click(object sender, RoutedEventArgs e) => MoveAnalytics(-1);
    private void AnalyticsNext_Click(object sender, RoutedEventArgs e) => MoveAnalytics(1);

    private void MoveAnalytics(int delta)
    {
        if (AnalyticsPeriodName == "Custom") return;
        DateTime date = AnalyticsDate.SelectedDate ?? DateTime.UtcNow.Date;
        AnalyticsDate.SelectedDate = AnalyticsPeriodName switch
        {
            "Week" => date.AddDays(7 * delta),
            "Year" => date.AddYears(delta),
            _ => date.AddMonths(delta)
        };
    }

    private void RenderAnalytics()
    {
        if (_closed || AnalyticsDate == null || AnalyticsPeriod == null || AnalyticsBars == null) return;
        var (start, end) = AnalyticsRange();
        if (AnalyticsCustomDates != null)
            AnalyticsCustomDates.Visibility = AnalyticsPeriodName == "Custom" ? Visibility.Visible : Visibility.Collapsed;
        var state = Service.State;
        var snapshots = state.CachedContractItems ?? new Dictionary<string, List<ContractItem>>();
        _analyticsSnapshot = BuybackAnalytics.Build(state.History, state.CorporationId, start, end, snapshots, AnalyticsPeriodName, state.Rows);
        var result = _analyticsSnapshot;
        AnalyticsDatesText.Text = $"{start:dd MMM yyyy} – {end.AddDays(-1):dd MMM yyyy} (UTC)  •  {result.PeriodContracts:N0} contracts issued in period";
        AnalyticsPaid.Text = result.ValueText;
        AnalyticsChange.Text = result.ChangeText;
        AnalyticsCount.Text = result.Count.ToString("N0", CultureInfo.InvariantCulture);
        AnalyticsCountChange.Text = $"{result.PreviousCount:N0} in previous comparable period";
        AnalyticsVolume.Text = result.VolumeText;
        AnalyticsCoverage.Text = result.CoverageText;
        AnalyticsPending.Text = result.Outstanding.ToString("N0", CultureInfo.InvariantCulture);
        AnalyticsPendingDetail.Text = $"Current ESI snapshot • {result.ExpiredOrCancelled:N0} declined/expired in selected issued period";
        AnalyticsBars.ItemsSource = result.TimeBuckets;
        AnalyticsSuppliers.ItemsSource = result.Suppliers;
        AnalyticsOres.ItemsSource = result.Contents;
        AnalyticsIndexStatus.Text = result.Count == 0
            ? "No qualifying accepted buybacks in this period. Older periods may not be covered by ESI history."
            : result.IndexedContracts == result.Count
                ? "All qualifying buyback contents indexed for this period."
                : $"{result.Count - result.IndexedContracts:N0} qualifying contract contents not indexed. Indexing is optional and may be restricted by ESI.";
        AnalyticsLoadContents.IsEnabled = !_analyticsLoading && result.IndexedContracts < result.Count;
        FilterAnalytics();
    }

    private void FilterAnalytics()
    {
        if (AnalyticsGrid == null || _analyticsSnapshot == null) return;
        string query = AnalyticsSearch?.Text?.Trim() ?? "";
        AnalyticsGrid.ItemsSource = _analyticsSnapshot.AcceptedContracts
            .Where(r => query.Length == 0 ||
                ($"{r.Issuer} {r.Contract.Id} {r.Contract.Title} {r.Location}")
                .Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private void AnalyticsRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (AnalyticsGrid?.SelectedItem is ContractRow row) OpenContents(row);
    }

    private async void AnalyticsIndex_Click(object sender, RoutedEventArgs e)
    {
        if (_analyticsLoading || _analyticsSnapshot == null) return;
        var candidates = _analyticsSnapshot.AcceptedContracts
            .Where(r => !Service.State.CachedContractItems.ContainsKey(BuybackAnalytics.Key(r)))
            .Take(15).ToArray();
        if (candidates.Length == 0) return;
        var pilot = _pilots.FirstOrDefault(p => p.CharacterId == Service.State.CharacterId);
        if (pilot == null || !ContractService.CanRead(pilot))
        {
            AnalyticsIndexStatus.Text = "Select and authorize your corporation contract reader before indexing contents.";
            return;
        }
        _analyticsLoading = true;
        AnalyticsLoadContents.IsEnabled = false;
        int loaded = 0, failed = 0;
        try
        {
            foreach (ContractRow contract in candidates)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                AnalyticsIndexStatus.Text = $"Indexing contents {loaded + failed + 1} of {candidates.Length}...";
                try
                {
                    await Service.ItemsAsync(contract, pilot, _lifetime.Token);
                    loaded++;
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception) { failed++; } // Old contracts may no longer expose items through ESI.
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _analyticsLoading = false;
            if (!_closed)
            {
                RenderAnalytics();
                AnalyticsIndexStatus.Text = $"Indexed {loaded} contracts; {failed} unavailable or failed. Contents are cached locally, not uploaded.";
            }
        }
    }

    private void AnalyticsExport_Click(object sender, RoutedEventArgs e)
    {
        if (_analyticsSnapshot == null) return;
        var s = _analyticsSnapshot;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export buyback analytics",
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"eve-buyback-{s.StartUtc:yyyyMMdd}-{s.EndExclusiveUtc.AddDays(-1):yyyyMMdd}.csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        static string Csv(string? value)
        {
            string text = value ?? "";
            if (text.Length > 0 && "=+-@".Contains(text[0])) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        var lines = new List<string>
        {
            "Metric,Value",
            $"Period Start (UTC),{s.StartUtc:yyyy-MM-dd}",
            $"Period End Inclusive (UTC),{s.EndExclusiveUtc.AddDays(-1):yyyy-MM-dd}",
            $"Accepted Buyback Price (ISK),{s.ValueIsk.ToString(CultureInfo.InvariantCulture)}",
            $"Qualifying Buyback Contracts,{s.Count}",
            $"Indexed Contract Contents,{s.IndexedContracts}",
            $"Known Item Volume m3,{s.KnownVolumeM3.ToString(CultureInfo.InvariantCulture)}",
            "",
            "Accepted UTC,Contract ID,Seller,Status,Contract Price ISK,Location,Contents Indexed"
        };
        foreach (var row in s.AcceptedContracts)
            lines.Add(string.Join(",", Csv(row.Contract.Accepted?.UtcDateTime.ToString("yyyy-MM-dd HH:mm")),
                row.Contract.Id.ToString(CultureInfo.InvariantCulture), Csv(row.Issuer), Csv(row.Contract.Status),
                (row.Contract.Price ?? 0).ToString(CultureInfo.InvariantCulture), Csv(row.Location),
                Service.State.CachedContractItems.ContainsKey(BuybackAnalytics.Key(row)) ? "Yes" : "No"));
        lines.Add(""); lines.Add("Indexed content,Volume m3");
        foreach (var row in s.Contents)
            lines.Add($"{Csv(row.Label)},{row.CubicMetres.ToString(CultureInfo.InvariantCulture)}");
        lines.Add(""); lines.Add("Note,Accepted price is not wallet-confirmed payment; profit is not calculated; unindexed contracts are excluded from volume totals.");
        File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(true));
        if (!_closed) AnalyticsIndexStatus.Text = "Report CSV saved locally: " + dialog.FileName;
    }
}
