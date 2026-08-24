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
    private readonly MiningIdleWatchdogService? _watchdog;
    private readonly MiningDashboardPreferences _prefs;
    private readonly Action? _saveRequested;
    private readonly Action? _toggleOverviewRequested;
    private readonly DispatcherTimer _refreshTimer;
    private bool _syncingSettings;

    public MiningDashboardWindow(
        StatTrackerService tracker,
        AppSettings settings,
        MiningIdleWatchdogService? watchdog = null,
        Action? saveRequested = null,
        Action? toggleOverviewRequested = null)
    {
        InitializeComponent();
        _tracker = tracker;
        _settings = settings;
        _watchdog = watchdog;
        _prefs = watchdog?.Preferences ?? MiningDashboardPreferencesStore.Load();
        _saveRequested = saveRequested;
        _toggleOverviewRequested = toggleOverviewRequested;

        ApplyPreferencesToRuntimeSettings();
        SyncControlsFromSettings();
        HookSettingsControls();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshDashboardAsync();
        _refreshTimer.Start();
        Closed += (_, _) => _refreshTimer.Stop();
        Loaded += async (_, _) => await RefreshDashboardAsync();
    }

    private void ApplyPreferencesToRuntimeSettings()
    {
        _settings.MiningMarketJitaEnabled = _prefs.JitaEnabled;
        _settings.MiningMarketAmarrEnabled = _prefs.AmarrEnabled;
        _settings.MiningMarketPriceMode = _prefs.MarketPriceMode;
        _settings.MiningCorpBuybackPercent = _prefs.CorpBuybackPercent;
        _settings.MiningCorpBuybackMarket = _prefs.CorpBuybackMarket;
        _settings.MiningCorpBuybackPriceMode = _prefs.CorpBuybackPriceMode;
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

            IdleWatchdogCheck.IsChecked = _prefs.IdleWatchdogEnabled;
            IdleSecondsText.Text = Math.Clamp(_prefs.IdleSeconds, 15, 3600).ToString(CultureInfo.InvariantCulture);
            IdleSoundCheck.IsChecked = _prefs.IdleSoundEnabled;

            YieldDropCheck.IsChecked = _prefs.YieldDropEnabled;
            YieldDropPercentText.Text = Math.Clamp(_prefs.YieldDropPercent, 10, 80).ToString(CultureInfo.InvariantCulture);
            YieldDropSecondsText.Text = Math.Clamp(_prefs.YieldDropHoldSeconds, 10, 300).ToString(CultureInfo.InvariantCulture);

            AutoOverviewCheck.IsChecked = _prefs.AutoShowFleetOverview;
            TileWallCheck.IsChecked = _prefs.UseFleetTileWall;
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

        IdleWatchdogCheck.Checked += (_, _) => SaveSettingsFromControls();
        IdleWatchdogCheck.Unchecked += (_, _) => SaveSettingsFromControls();
        IdleSoundCheck.Checked += (_, _) => SaveSettingsFromControls();
        IdleSoundCheck.Unchecked += (_, _) => SaveSettingsFromControls();

        YieldDropCheck.Checked += (_, _) => SaveSettingsFromControls();
        YieldDropCheck.Unchecked += (_, _) => SaveSettingsFromControls();
        AutoOverviewCheck.Checked += (_, _) => SaveSettingsFromControls();
        AutoOverviewCheck.Unchecked += (_, _) => SaveSettingsFromControls();
        TileWallCheck.Checked += (_, _) => SaveSettingsFromControls();
        TileWallCheck.Unchecked += (_, _) => SaveSettingsFromControls();

        BuybackPercentText.LostFocus += (_, _) => SaveSettingsFromControls();
        IdleSecondsText.LostFocus += (_, _) => SaveSettingsFromControls();
        YieldDropPercentText.LostFocus += (_, _) => SaveSettingsFromControls();
        YieldDropSecondsText.LostFocus += (_, _) => SaveSettingsFromControls();

        BuybackPercentText.KeyDown += NumericTextBox_KeyDown;
        IdleSecondsText.KeyDown += NumericTextBox_KeyDown;
        YieldDropPercentText.KeyDown += NumericTextBox_KeyDown;
        YieldDropSecondsText.KeyDown += NumericTextBox_KeyDown;

        ToggleOverviewButton.Click += (_, _) => _toggleOverviewRequested?.Invoke();
    }

    private void NumericTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveSettingsFromControls();
        Keyboard.ClearFocus();
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
        }

        int idleSeconds = ParseInt(IdleSecondsText.Text, _prefs.IdleSeconds, 15, 3600);
        int dropPercent = ParseInt(YieldDropPercentText.Text, _prefs.YieldDropPercent, 10, 80);
        int dropSeconds = ParseInt(YieldDropSecondsText.Text, _prefs.YieldDropHoldSeconds, 10, 300);

        _prefs.JitaEnabled = _settings.MiningMarketJitaEnabled;
        _prefs.AmarrEnabled = _settings.MiningMarketAmarrEnabled;
        _prefs.MarketPriceMode = _settings.MiningMarketPriceMode;
        _prefs.CorpBuybackPercent = _settings.MiningCorpBuybackPercent;
        _prefs.CorpBuybackMarket = _settings.MiningCorpBuybackMarket;
        _prefs.CorpBuybackPriceMode = _settings.MiningCorpBuybackPriceMode;

        _prefs.IdleWatchdogEnabled = IdleWatchdogCheck.IsChecked == true;
        _prefs.IdleSeconds = idleSeconds;
        _prefs.IdleSoundEnabled = IdleSoundCheck.IsChecked == true;
        _prefs.YieldDropEnabled = YieldDropCheck.IsChecked == true;
        _prefs.YieldDropPercent = dropPercent;
        _prefs.YieldDropHoldSeconds = dropSeconds;
        _prefs.AutoShowFleetOverview = AutoOverviewCheck.IsChecked == true;
        _prefs.UseFleetTileWall = TileWallCheck.IsChecked == true;

        _syncingSettings = true;
        try
        {
            BuybackPercentText.Text = _settings.MiningCorpBuybackPercent.ToString("0.##", CultureInfo.InvariantCulture);
            IdleSecondsText.Text = _prefs.IdleSeconds.ToString(CultureInfo.InvariantCulture);
            YieldDropPercentText.Text = _prefs.YieldDropPercent.ToString(CultureInfo.InvariantCulture);
            YieldDropSecondsText.Text = _prefs.YieldDropHoldSeconds.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncingSettings = false;
        }

        if (_watchdog != null)
            _watchdog.SavePreferences();
        else
            MiningDashboardPreferencesStore.Save(_prefs);

        _saveRequested?.Invoke();
    }

    private static int ParseInt(string text, int fallback, int min, int max)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ||
            int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value))
            return Math.Clamp(value, min, max);
        return Math.Clamp(fallback, min, max);
    }

    private async System.Threading.Tasks.Task RefreshDashboardAsync()
    {
        var fleetOre = _tracker.GetFleetMiningSessionUnitsByOre();
        foreach (var ore in fleetOre.Keys)
            _ = _tracker.EnsureMiningQuoteAsync(ore);

        await System.Threading.Tasks.Task.Yield();

        var liveRows = new List<LiveMiningRow>();
        var overviewRows = new List<OverviewCharacterRow>();

        double totalBase = 0;
        double totalActual = 0;
        double totalTodayM3 = 0;
        double totalBestToday = 0;
        double totalCorpToday = 0;

        foreach (var character in _tracker.GetMiningDashboardCharacters())
        {
            var s = _tracker.GetSnapshot(character);
            bool hasToday = s.SessionM3 > 0;
            if (s.MiningCycleCount == 0 && string.IsNullOrWhiteSpace(s.CurrentOre) && !hasToday)
                continue;

            var idle = _watchdog?.GetState(character)
                       ?? new MiningIdleState(
                           s.MiningCycleCount > 0 ? MiningIdleKind.Mining : MiningIdleKind.Waiting,
                           null, 0, s.MiningCycleCount);

            var dailyCrit = _tracker.GetTodayMiningCritSummary(character);

            bool actualReady = s.MiningCycleCount >= 6 && s.ActualM3PerSec > 0;
            string actualText = actualReady
                ? s.ActualM3PerSec.ToString("N1", CultureInfo.CurrentCulture)
                : "warmingâ€¦";

            liveRows.Add(new LiveMiningRow
            {
                Character = character,
                Status = idle.Label,
                LastPull = idle.LastActivityUtc.HasValue ? AgeText(idle.AgeSeconds) : "â€”",
                Ore = string.IsNullOrWhiteSpace(s.CurrentOre) ? "â€”" : s.CurrentOre,
                BaseM3PerSec = s.BaseM3PerSec,
                ActualM3PerSecText = actualText,
                ActualM3PerSecValue = actualReady ? s.ActualM3PerSec : 0,
                Crits = dailyCrit.ToString(),
                SessionM3 = s.SessionM3,
                JitaIskPerHourText = _settings.MiningMarketJitaEnabled ? Isk(s.JitaIskPerHour) : "off",
                AmarrIskPerHourText = _settings.MiningMarketAmarrEnabled ? Isk(s.AmarrIskPerHour) : "off",
                BestIskPerHourText = Isk(s.BestIskPerHour),
                CorpSessionText = Isk(s.SessionBuybackValue)
            });

            overviewRows.Add(new OverviewCharacterRow
            {
                Character = character,
                Status = idle.Label,
                Ore = string.IsNullOrWhiteSpace(s.CurrentOre) ? "â€”" : s.CurrentOre,
                SessionM3Text = Number(s.SessionM3),
                JitaValueText = _settings.MiningMarketJitaEnabled ? Isk(s.SessionJitaValue) : "off",
                AmarrValueText = _settings.MiningMarketAmarrEnabled ? Isk(s.SessionAmarrValue) : "off",
                BestValueText = Isk(s.SessionBestValue),
                CorpValueText = Isk(s.SessionBuybackValue)
            });

            totalBase += s.BaseM3PerSec;
            if (actualReady) totalActual += s.ActualM3PerSec;
            totalTodayM3 += s.SessionM3;
            totalBestToday += s.SessionBestValue;
            totalCorpToday += s.SessionBuybackValue;
        }

        if (liveRows.Count > 1)
        {
            liveRows.Add(new LiveMiningRow
            {
                Character = "FLEET",
                Status = "â€”",
                LastPull = "â€”",
                Ore = "â€”",
                BaseM3PerSec = totalBase,
                ActualM3PerSecText = totalActual > 0
                    ? totalActual.ToString("N1", CultureInfo.CurrentCulture)
                    : "warmingâ€¦",
                ActualM3PerSecValue = totalActual,
                Crits = FleetCritText(),
                SessionM3 = totalTodayM3,
                JitaIskPerHourText = _settings.MiningMarketJitaEnabled ? Isk(SumSnapshot(x => x.JitaIskPerHour)) : "off",
                AmarrIskPerHourText = _settings.MiningMarketAmarrEnabled ? Isk(SumSnapshot(x => x.AmarrIskPerHour)) : "off",
                BestIskPerHourText = Isk(SumSnapshot(x => x.BestIskPerHour)),
                CorpSessionText = Isk(totalCorpToday)
            });
        }

        LiveGrid.ItemsSource = liveRows;
        OverviewCharacterGrid.ItemsSource = overviewRows;

        SummaryBaseText.Text = totalBase > 0 ? $"{totalBase:N1} mÂ³/s" : "â€”";
        SummaryActualText.Text = totalActual > 0 ? $"{totalActual:N1} mÂ³/s" : "warmingâ€¦";
        SummarySessionM3Text.Text = totalTodayM3 > 0 ? $"{totalTodayM3:N0} mÂ³" : "â€”";
        SummaryBestValueText.Text = Isk(totalBestToday);
        SummaryBuybackText.Text = Isk(totalCorpToday);
        SummaryCritText.Text = FleetCritText();

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
                BestMarket = best.Market ?? "â€”",
                JitaValueText = _settings.MiningMarketJitaEnabled ? Isk(jitaValue) : "off",
                AmarrValueText = _settings.MiningMarketAmarrEnabled ? Isk(amarrValue) : "off",
                BestValueText = best.Market == null ? "â€”" : Isk(best.Value)
            });
        }

        MarketGrid.ItemsSource = marketRows;
        OverviewOreGrid.ItemsSource = marketRows;

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

            double refUnit = _tracker.GetMarketUnitPrice(
                quote,
                _settings.MiningCorpBuybackMarket,
                _settings.MiningCorpBuybackPriceMode);

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

        string watchdogText = _prefs.IdleWatchdogEnabled
            ? $"no-pull {_prefs.IdleSeconds}s"
            : "no-pull off";
        string dropText = _prefs.YieldDropEnabled
            ? $"drop {_prefs.YieldDropPercent}%/{_prefs.YieldDropHoldSeconds}s"
            : "drop off";

        LastRefreshText.Text =
            $"{_tracker.GetMiningDayLabel()} day Â· ESI {DateTime.Now:HH:mm:ss} Â· {fleetOre.Count} resource(s) Â· {watchdogText} Â· {dropText}";
    }

    private string FleetCritText() => _tracker.GetTodayMiningCritSummary().ToString();

    private double SumSnapshot(Func<CharacterStatSnapshot, double> selector)
    {
        double result = 0;
        foreach (var c in _tracker.GetMiningDashboardCharacters())
            result += selector(_tracker.GetSnapshot(c));
        return result;
    }

    private static string AgeText(double seconds)
    {
        if (seconds < 60) return $"{Math.Round(seconds):0}s";
        if (seconds < 3600) return $"{Math.Floor(seconds / 60):0}m {Math.Round(seconds % 60):0}s";
        return $"{Math.Floor(seconds / 3600):0}h {Math.Floor((seconds % 3600) / 60):0}m";
    }

    private static string Isk(double value) =>
        value <= 0 ? "â€”" : StatTrackerService.FormatNumber(value);

    private static string Price(double value) =>
        value <= 0 ? "â€”" : value.ToString("N2", CultureInfo.CurrentCulture);

    private static string Number(double value) =>
        value <= 0 ? "â€”" : value.ToString("N0", CultureInfo.CurrentCulture);

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

        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private sealed class LiveMiningRow
    {
        public string Character { get; init; } = "";
        public string Status { get; init; } = "";
        public string LastPull { get; init; } = "";
        public string Ore { get; init; } = "";
        public double BaseM3PerSec { get; init; }
        public string ActualM3PerSecText { get; init; } = "";
        public double ActualM3PerSecValue { get; init; }
        public string Crits { get; init; } = "";
        public double SessionM3 { get; init; }
        public string JitaIskPerHourText { get; init; } = "";
        public string AmarrIskPerHourText { get; init; } = "";
        public string BestIskPerHourText { get; init; } = "";
        public string CorpSessionText { get; init; } = "";
    }

    private sealed class OverviewCharacterRow
    {
        public string Character { get; init; } = "";
        public string Status { get; init; } = "";
        public string Ore { get; init; } = "";
        public string SessionM3Text { get; init; } = "";
        public string JitaValueText { get; init; } = "";
        public string AmarrValueText { get; init; } = "";
        public string BestValueText { get; init; } = "";
        public string CorpValueText { get; init; } = "";
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
            Ore = ore,
            Units = units,
            VolumeM3Text = "loadingâ€¦",
            JitaUnitText = "loadingâ€¦",
            AmarrUnitText = "loadingâ€¦",
            BestMarket = "â€”",
            JitaValueText = "â€”",
            AmarrValueText = "â€”",
            BestValueText = "â€”"
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
            Ore = ore,
            Units = units,
            ReferenceUnitText = "loadingâ€¦",
            GrossText = "â€”",
            RateText = $"{pct:0.##}%",
            PayoutText = "â€”"
        };
    }
}
