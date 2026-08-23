using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

public partial class MiningDashboardWindow : Window
{
    private readonly StatTrackerService _tracker;
    private readonly AppSettings _settings;
    private readonly Action? _saveRequested;
    private readonly DispatcherTimer _refreshTimer;
    private bool _syncingSettings;

    public MiningDashboardWindow(StatTrackerService tracker, AppSettings settings, Action? saveRequested = null)
    {
        InitializeComponent();
        _tracker = tracker;
        _settings = settings;
        _saveRequested = saveRequested;

        SyncControlsFromSettings();
        HookSettingsControls();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshDashboardAsync();
        _refreshTimer.Start();
        Closed += (_, _) => _refreshTimer.Stop();
        Loaded += async (_, _) => await RefreshDashboardAsync();
    }

    private void SyncControlsFromSettings()
    {
        _syncingSettings = true;
        try
        {
            JitaCheck.IsChecked = _settings.MiningMarketJitaEnabled;
            AmarrCheck.IsChecked = _settings.MiningMarketAmarrEnabled;
            SelectComboTag(MarketPriceModeCombo, _settings.MiningMarketPriceMode);
            BuybackPercentText.Text = _settings.MiningCorpBuybackPercent.ToString("0.##", CultureInfo.InvariantCulture);
            SelectComboTag(BuybackMarketCombo, _settings.MiningCorpBuybackMarket);
            SelectComboTag(BuybackPriceModeCombo, _settings.MiningCorpBuybackPriceMode);
        }
        finally
        {
            _syncingSettings = false;
        }
    }

    private void HookSettingsControls()
    {
        JitaCheck.Checked += (_, _) => SaveSettingsFromControls();
        JitaCheck.Unchecked += (_, _) => SaveSettingsFromControls();
        AmarrCheck.Checked += (_, _) => SaveSettingsFromControls();
        AmarrCheck.Unchecked += (_, _) => SaveSettingsFromControls();
        MarketPriceModeCombo.SelectionChanged += (_, _) => SaveSettingsFromControls();
        BuybackMarketCombo.SelectionChanged += (_, _) => SaveSettingsFromControls();
        BuybackPriceModeCombo.SelectionChanged += (_, _) => SaveSettingsFromControls();
        BuybackPercentText.LostFocus += (_, _) => SaveSettingsFromControls();
        BuybackPercentText.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SaveSettingsFromControls();
                Keyboard.ClearFocus();
            }
        };
    }

    private void SaveSettingsFromControls()
    {
        if (_syncingSettings) return;

        _settings.MiningMarketJitaEnabled = JitaCheck.IsChecked == true;
        _settings.MiningMarketAmarrEnabled = AmarrCheck.IsChecked == true;
        _settings.MiningMarketPriceMode = GetComboTag(MarketPriceModeCombo, "sell");
        _settings.MiningCorpBuybackMarket = GetComboTag(BuybackMarketCombo, "Jita");
        _settings.MiningCorpBuybackPriceMode = GetComboTag(BuybackPriceModeCombo, "sell");

        if (double.TryParse(BuybackPercentText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) ||
            double.TryParse(BuybackPercentText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out pct))
        {
            _settings.MiningCorpBuybackPercent = Math.Clamp(pct, 0, 100);
            BuybackPercentText.Text = _settings.MiningCorpBuybackPercent.ToString("0.##", CultureInfo.InvariantCulture);
        }

        _saveRequested?.Invoke();
    }

    private async System.Threading.Tasks.Task RefreshDashboardAsync()
    {
        var fleetOre = _tracker.GetFleetMiningSessionUnitsByOre();
        foreach (var ore in fleetOre.Keys)
            _ = _tracker.EnsureMiningQuoteAsync(ore);

        // Yield once so newly completed quote tasks can update the grids on this tick.
        await System.Threading.Tasks.Task.Yield();

        var liveRows = new List<LiveMiningRow>();
        foreach (var character in _tracker.GetTrackedCharacters())
        {
            var s = _tracker.GetSnapshot(character);
            if (s.MiningCycleCount == 0 && string.IsNullOrWhiteSpace(s.CurrentOre))
                continue;

            double critPct = s.MiningCycleCount > 0 ? s.MiningCritCount * 100.0 / s.MiningCycleCount : 0;
            liveRows.Add(new LiveMiningRow
            {
                Character = character,
                Ore = string.IsNullOrWhiteSpace(s.CurrentOre) ? "—" : s.CurrentOre,
                BaseM3PerSec = s.BaseM3PerSec,
                ActualM3PerSec = s.ActualM3PerSec,
                Crits = $"{s.MiningCritCount}/{s.MiningCycleCount} ({critPct:F1}%)",
                SessionM3 = s.SessionM3,
                JitaIskPerHourText = _settings.MiningMarketJitaEnabled ? Isk(s.JitaIskPerHour) : "off",
                AmarrIskPerHourText = _settings.MiningMarketAmarrEnabled ? Isk(s.AmarrIskPerHour) : "off",
                BestIskPerHourText = Isk(s.BestIskPerHour),
                CorpSessionText = Isk(s.SessionBuybackValue)
            });
        }

        if (liveRows.Count > 1)
        {
            liveRows.Add(new LiveMiningRow
            {
                Character = "FLEET",
                Ore = "—",
                BaseM3PerSec = liveRows.Sum(r => r.BaseM3PerSec),
                ActualM3PerSec = liveRows.Sum(r => r.ActualM3PerSec),
                Crits = FleetCritText(),
                SessionM3 = liveRows.Sum(r => r.SessionM3),
                JitaIskPerHourText = _settings.MiningMarketJitaEnabled ? Isk(SumSnapshot(x => x.JitaIskPerHour)) : "off",
                AmarrIskPerHourText = _settings.MiningMarketAmarrEnabled ? Isk(SumSnapshot(x => x.AmarrIskPerHour)) : "off",
                BestIskPerHourText = Isk(SumSnapshot(x => x.BestIskPerHour)),
                CorpSessionText = Isk(SumSnapshot(x => x.SessionBuybackValue))
            });
        }
        LiveGrid.ItemsSource = liveRows;

        var marketRows = new List<MarketOreRow>();
        foreach (var kv in fleetOre.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!_tracker.TryGetMiningQuote(kv.Key, out var quote) || !quote.IsAvailable)
            {
                marketRows.Add(MarketOreRow.Pending(kv.Key, kv.Value));
                continue;
            }

            double jitaUnit = _tracker.GetMarketUnitPrice(quote, "Jita", _settings.MiningMarketPriceMode);
            double amarrUnit = _tracker.GetMarketUnitPrice(quote, "Amarr", _settings.MiningMarketPriceMode);
            double jitaValue = kv.Value * jitaUnit;
            double amarrValue = kv.Value * amarrUnit;
            var enabled = new List<(string Market, double Unit, double Value)>();
            if (_settings.MiningMarketJitaEnabled) enabled.Add(("Jita", jitaUnit, jitaValue));
            if (_settings.MiningMarketAmarrEnabled) enabled.Add(("Amarr", amarrUnit, amarrValue));
            var best = enabled.OrderByDescending(x => x.Value).FirstOrDefault();

            marketRows.Add(new MarketOreRow
            {
                Ore = kv.Key,
                Units = kv.Value,
                VolumeM3Text = Number(kv.Value * quote.UnitVolumeM3),
                JitaUnitText = _settings.MiningMarketJitaEnabled ? Price(jitaUnit) : "off",
                AmarrUnitText = _settings.MiningMarketAmarrEnabled ? Price(amarrUnit) : "off",
                BestMarket = best.Market ?? "—",
                JitaValueText = _settings.MiningMarketJitaEnabled ? Isk(jitaValue) : "off",
                AmarrValueText = _settings.MiningMarketAmarrEnabled ? Isk(amarrValue) : "off",
                BestValueText = best.Market == null ? "—" : Isk(best.Value)
            });
        }
        MarketGrid.ItemsSource = marketRows;

        var buybackRows = new List<BuybackOreRow>();
        double grossTotal = 0;
        double payoutTotal = 0;
        double rate = Math.Clamp(_settings.MiningCorpBuybackPercent, 0, 100) / 100.0;
        foreach (var kv in fleetOre.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!_tracker.TryGetMiningQuote(kv.Key, out var quote) || !quote.IsAvailable)
            {
                buybackRows.Add(BuybackOreRow.Pending(kv.Key, kv.Value, _settings.MiningCorpBuybackPercent));
                continue;
            }

            double refUnit = _tracker.GetMarketUnitPrice(quote, _settings.MiningCorpBuybackMarket, _settings.MiningCorpBuybackPriceMode);
            double gross = kv.Value * refUnit;
            double payout = gross * rate;
            grossTotal += gross;
            payoutTotal += payout;
            buybackRows.Add(new BuybackOreRow
            {
                Ore = kv.Key,
                Units = kv.Value,
                ReferenceUnitText = Price(refUnit),
                GrossText = Isk(gross),
                RateText = $"{_settings.MiningCorpBuybackPercent:0.##}%",
                PayoutText = Isk(payout)
            });
        }
        if (buybackRows.Count > 0)
        {
            buybackRows.Add(new BuybackOreRow
            {
                Ore = "TOTAL",
                Units = fleetOre.Values.Sum(),
                ReferenceUnitText = $"{_settings.MiningCorpBuybackMarket} / {_settings.MiningCorpBuybackPriceMode}",
                GrossText = Isk(grossTotal),
                RateText = $"{_settings.MiningCorpBuybackPercent:0.##}%",
                PayoutText = Isk(payoutTotal)
            });
        }
        BuybackGrid.ItemsSource = buybackRows;

        LastRefreshText.Text = $"Public ESI • {DateTime.Now:HH:mm:ss} • {fleetOre.Count} resource type(s)";
    }

    private string FleetCritText()
    {
        int crit = 0;
        int cycles = 0;
        foreach (var c in _tracker.GetTrackedCharacters())
        {
            var s = _tracker.GetSnapshot(c);
            crit += s.MiningCritCount;
            cycles += s.MiningCycleCount;
        }
        double pct = cycles > 0 ? crit * 100.0 / cycles : 0;
        return $"{crit}/{cycles} ({pct:F1}%)";
    }

    private double SumSnapshot(Func<CharacterStatSnapshot, double> selector)
    {
        double result = 0;
        foreach (var c in _tracker.GetTrackedCharacters())
            result += selector(_tracker.GetSnapshot(c));
        return result;
    }

    private static string Isk(double value) => value <= 0 ? "—" : StatTrackerService.FormatNumber(value);
    private static string Price(double value) => value <= 0 ? "—" : value.ToString("N2", CultureInfo.CurrentCulture);
    private static string Number(double value) => value <= 0 ? "—" : value.ToString("N0", CultureInfo.CurrentCulture);

    private static string GetComboTag(System.Windows.Controls.ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectComboTag(System.Windows.Controls.ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private sealed class LiveMiningRow
    {
        public string Character { get; init; } = "";
        public string Ore { get; init; } = "";
        public double BaseM3PerSec { get; init; }
        public double ActualM3PerSec { get; init; }
        public string Crits { get; init; } = "";
        public double SessionM3 { get; init; }
        public string JitaIskPerHourText { get; init; } = "";
        public string AmarrIskPerHourText { get; init; } = "";
        public string BestIskPerHourText { get; init; } = "";
        public string CorpSessionText { get; init; } = "";
    }

    private sealed class MarketOreRow
    {
        public string Ore { get; init; } = "";
        public double Units { get; init; }
        public string VolumeM3Text { get; init; } = "";
        public string JitaUnitText { get; init; } = "";
        public string AmarrUnitText { get; init; } = "";
        public string BestMarket { get; init; } = "";
        public string JitaValueText { get; init; } = "";
        public string AmarrValueText { get; init; } = "";
        public string BestValueText { get; init; } = "";

        public static MarketOreRow Pending(string ore, double units) => new()
        {
            Ore = ore, Units = units, VolumeM3Text = "loading…", JitaUnitText = "loading…",
            AmarrUnitText = "loading…", BestMarket = "—", JitaValueText = "—", AmarrValueText = "—", BestValueText = "—"
        };
    }

    private sealed class BuybackOreRow
    {
        public string Ore { get; init; } = "";
        public double Units { get; init; }
        public string ReferenceUnitText { get; init; } = "";
        public string GrossText { get; init; } = "";
        public string RateText { get; init; } = "";
        public string PayoutText { get; init; } = "";

        public static BuybackOreRow Pending(string ore, double units, double pct) => new()
        {
            Ore = ore, Units = units, ReferenceUnitText = "loading…", GrossText = "—",
            RateText = $"{pct:0.##}%", PayoutText = "—"
        };
    }
}
