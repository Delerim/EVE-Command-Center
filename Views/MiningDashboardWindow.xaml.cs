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
    private readonly DispatcherTimer _historyRefreshTimer;
    private DateTime _historyFrom;
    private DateTime _historyTo;
    private DateTime _profitFrom;
    private DateTime _profitTo;
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
        HookHistoryControls();
        SetHistoryRange("today");
        HookProfitControls();
        SetProfitRange("today");
        Opacity = Math.Clamp(_prefs.DashboardOpacityPercent, 55, 100) / 100.0;

        _historyRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _historyRefreshTimer.Tick += async (_, _) =>
        {
            if (HistoryTab.IsSelected)
                await RefreshHistoryAsync();
            else if (ProfitTab.IsSelected)
                await RefreshProfitAsync();
        };
        _historyRefreshTimer.Start();

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshDashboardAsync();
        _refreshTimer.Start();
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _historyRefreshTimer.Stop();
        };
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
            DashboardOpacityText.Text = Math.Clamp(_prefs.DashboardOpacityPercent, 55, 100).ToString(CultureInfo.InvariantCulture);
            OverviewOpacityText.Text = Math.Clamp(_prefs.FleetOverviewOpacityPercent, 55, 100).ToString(CultureInfo.InvariantCulture);
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
        DashboardOpacityText.LostFocus += (_, _) => SaveSettingsFromControls();
        OverviewOpacityText.LostFocus += (_, _) => SaveSettingsFromControls();

        BuybackPercentText.KeyDown += NumericTextBox_KeyDown;
        IdleSecondsText.KeyDown += NumericTextBox_KeyDown;
        YieldDropPercentText.KeyDown += NumericTextBox_KeyDown;
        YieldDropSecondsText.KeyDown += NumericTextBox_KeyDown;
        DashboardOpacityText.KeyDown += NumericTextBox_KeyDown;
        OverviewOpacityText.KeyDown += NumericTextBox_KeyDown;

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
        int dashboardOpacity = ParseInt(DashboardOpacityText.Text, _prefs.DashboardOpacityPercent, 55, 100);
        int overviewOpacity = ParseInt(OverviewOpacityText.Text, _prefs.FleetOverviewOpacityPercent, 55, 100);

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
        _prefs.DashboardOpacityPercent = dashboardOpacity;
        _prefs.FleetOverviewOpacityPercent = overviewOpacity;
        Opacity = dashboardOpacity / 100.0;

        _syncingSettings = true;
        try
        {
            BuybackPercentText.Text = _settings.MiningCorpBuybackPercent.ToString("0.##", CultureInfo.InvariantCulture);
            IdleSecondsText.Text = _prefs.IdleSeconds.ToString(CultureInfo.InvariantCulture);
            YieldDropPercentText.Text = _prefs.YieldDropPercent.ToString(CultureInfo.InvariantCulture);
            YieldDropSecondsText.Text = _prefs.YieldDropHoldSeconds.ToString(CultureInfo.InvariantCulture);
            DashboardOpacityText.Text = _prefs.DashboardOpacityPercent.ToString(CultureInfo.InvariantCulture);
            OverviewOpacityText.Text = _prefs.FleetOverviewOpacityPercent.ToString(CultureInfo.InvariantCulture);
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
                : "warming...";

            liveRows.Add(new LiveMiningRow
            {
                Character = character,
                Status = idle.Label,
                LastPull = idle.LastActivityUtc.HasValue ? AgeText(idle.AgeSeconds) : "-",
                Ore = string.IsNullOrWhiteSpace(s.CurrentOre) ? "-" : s.CurrentOre,
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
                Ore = string.IsNullOrWhiteSpace(s.CurrentOre) ? "-" : s.CurrentOre,
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
                Status = "-",
                LastPull = "-",
                Ore = "-",
                BaseM3PerSec = totalBase,
                ActualM3PerSecText = totalActual > 0
                    ? totalActual.ToString("N1", CultureInfo.CurrentCulture)
                    : "warming...",
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

        SummaryBaseText.Text = totalBase > 0 ? $"{totalBase:N1} m3/s" : "-";
        SummaryActualText.Text = totalActual > 0 ? $"{totalActual:N1} m3/s" : "warming...";
        SummarySessionM3Text.Text = totalTodayM3 > 0 ? $"{totalTodayM3:N0} m3" : "-";
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
                BestMarket = best.Market ?? "-",
                JitaValueText = _settings.MiningMarketJitaEnabled ? Isk(jitaValue) : "off",
                AmarrValueText = _settings.MiningMarketAmarrEnabled ? Isk(amarrValue) : "off",
                BestValueText = best.Market == null ? "-" : Isk(best.Value)
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
            $"{_tracker.GetMiningDayLabel()} day | ESI {DateTime.Now:HH:mm:ss} | {fleetOre.Count} resource(s) | {watchdogText} | {dropText}";
    }


    private void HookHistoryControls()
    {
        HistoryTodayButton.Click += async (_, _) => { SetHistoryRange("today"); await RefreshHistoryAsync(); };
        HistoryYesterdayButton.Click += async (_, _) => { SetHistoryRange("yesterday"); await RefreshHistoryAsync(); };
        HistoryThisWeekButton.Click += async (_, _) => { SetHistoryRange("thisweek"); await RefreshHistoryAsync(); };
        HistoryLastWeekButton.Click += async (_, _) => { SetHistoryRange("lastweek"); await RefreshHistoryAsync(); };
        HistoryThisMonthButton.Click += async (_, _) => { SetHistoryRange("thismonth"); await RefreshHistoryAsync(); };
        HistoryLastMonthButton.Click += async (_, _) => { SetHistoryRange("lastmonth"); await RefreshHistoryAsync(); };
        History30Button.Click += async (_, _) => { SetHistoryRange("30"); await RefreshHistoryAsync(); };
        History90Button.Click += async (_, _) => { SetHistoryRange("90"); await RefreshHistoryAsync(); };
        HistoryYearButton.Click += async (_, _) => { SetHistoryRange("year"); await RefreshHistoryAsync(); };
        HistoryRefreshButton.Click += async (_, _) => await RefreshHistoryAsync();

        MiningTabs.SelectionChanged += async (_, _) =>
        {
            if (HistoryTab.IsSelected)
                await RefreshHistoryAsync();
            else if (ProfitTab.IsSelected)
                await RefreshProfitAsync();
        };
    }

    private void SetHistoryRange(string preset)
    {
        if (!DateTime.TryParseExact(
                _tracker.GetMiningDayLabel(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var today))
            today = DateTime.Today;

        DateTime from;
        DateTime to;

        switch (preset)
        {
            case "yesterday":
                from = to = today.AddDays(-1);
                break;

            case "thisweek":
                int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
                from = today.AddDays(-daysSinceMonday);
                to = today;
                break;

            case "lastweek":
                int thisWeekOffset = ((int)today.DayOfWeek + 6) % 7;
                DateTime thisWeekStart = today.AddDays(-thisWeekOffset);
                from = thisWeekStart.AddDays(-7);
                to = thisWeekStart.AddDays(-1);
                break;

            case "thismonth":
                from = new DateTime(today.Year, today.Month, 1);
                to = today;
                break;

            case "lastmonth":
                DateTime thisMonth = new DateTime(today.Year, today.Month, 1);
                from = thisMonth.AddMonths(-1);
                to = thisMonth.AddDays(-1);
                break;

            case "30":
                from = today.AddDays(-29);
                to = today;
                break;

            case "90":
                from = today.AddDays(-89);
                to = today;
                break;

            case "year":
                from = today.AddDays(-364);
                to = today;
                break;

            default:
                from = to = today;
                break;
        }

        _historyFrom = from.Date;
        _historyTo = to.Date;
        HistoryRangeText.Text = from == to
            ? from.ToString("dd MMM yyyy", CultureInfo.CurrentCulture)
            : $"{from:dd MMM yyyy} -> {to:dd MMM yyyy}";
    }

    private async System.Threading.Tasks.Task RefreshHistoryAsync()
    {
        if (!HistoryTab.IsSelected)
            return;

        var aggregates = _tracker.GetMiningHistoryRange(_historyFrom, _historyTo);

        var ores = aggregates
            .Select(r => r.Ore)
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ores.Count > 0)
        {
            await System.Threading.Tasks.Task.WhenAll(
                ores.Select(o => _tracker.EnsureMiningQuoteAsync(o)));
        }

        double totalM3 = 0;
        double totalProfit = 0;
        double totalBuyback = 0;
        int totalCrits = 0;
        int totalCycles = 0;
        var rows = new List<HistoryRow>();

        foreach (var r in aggregates
                     .OrderByDescending(r => r.DayKey, StringComparer.Ordinal)
                     .ThenBy(r => r.Character, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(r => r.Ore, StringComparer.OrdinalIgnoreCase))
        {
            double m3 = 0;
            double profit = 0;
            double buyback = 0;

            if (_tracker.TryGetMiningQuote(r.Ore, out var quote) && quote.IsAvailable)
            {
                m3 = r.Units * quote.UnitVolumeM3;

                double jita = r.Units * _tracker.GetMarketUnitPrice(
                    quote, "Jita", _settings.MiningMarketPriceMode);
                double amarr = r.Units * _tracker.GetMarketUnitPrice(
                    quote, "Amarr", _settings.MiningMarketPriceMode);

                if (_settings.MiningMarketJitaEnabled) profit = Math.Max(profit, jita);
                if (_settings.MiningMarketAmarrEnabled) profit = Math.Max(profit, amarr);

                double bbUnit = _tracker.GetMarketUnitPrice(
                    quote,
                    _settings.MiningCorpBuybackMarket,
                    _settings.MiningCorpBuybackPriceMode);

                buyback = r.Units
                    * bbUnit
                    * Math.Clamp(_settings.MiningCorpBuybackPercent, 0, 100) / 100.0;
            }

            totalM3 += m3;
            totalProfit += profit;
            totalBuyback += buyback;
            totalCrits += r.Crits;
            totalCycles += r.Cycles;

            double critPct = r.Cycles > 0 ? r.Crits * 100.0 / r.Cycles : 0;

            rows.Add(new HistoryRow
            {
                Day = r.DayKey,
                Character = r.Character,
                Ore = r.Ore,
                UnitsText = r.Units.ToString("N0", CultureInfo.CurrentCulture),
                VolumeText = m3 > 0 ? m3.ToString("N0", CultureInfo.CurrentCulture) : "-",
                CritText = $"{r.Crits}/{r.Cycles} ({critPct:F1}%)",
                ProfitText = Isk(profit),
                BuybackText = Isk(buyback)
            });
        }

        HistoryGrid.ItemsSource = rows;
        HistoryVolumeText.Text = totalM3 > 0 ? $"{totalM3:N0} m3" : "-";
        HistoryProfitText.Text = Isk(totalProfit);
        HistoryBuybackText.Text = Isk(totalBuyback);

        double fleetCritPct = totalCycles > 0 ? totalCrits * 100.0 / totalCycles : 0;
        HistoryCritText.Text = $"{totalCrits}/{totalCycles} ({fleetCritPct:F1}%)";

        var status = _tracker.GetMiningHistoryStatus();
        HistoryBuildText.Text = status.IsRunning
            ? $"{status.Message} | {status.ProgressPercent:F0}%"
            : status.Message;
    }

    private void HookProfitControls()
    {
        ProfitTodayButton.Click += async (_, _) => { SetProfitRange("today"); await RefreshProfitAsync(); };
        ProfitYesterdayButton.Click += async (_, _) => { SetProfitRange("yesterday"); await RefreshProfitAsync(); };
        ProfitThisWeekButton.Click += async (_, _) => { SetProfitRange("thisweek"); await RefreshProfitAsync(); };
        ProfitLastWeekButton.Click += async (_, _) => { SetProfitRange("lastweek"); await RefreshProfitAsync(); };
        ProfitThisMonthButton.Click += async (_, _) => { SetProfitRange("thismonth"); await RefreshProfitAsync(); };
        ProfitLastMonthButton.Click += async (_, _) => { SetProfitRange("lastmonth"); await RefreshProfitAsync(); };
        Profit30Button.Click += async (_, _) => { SetProfitRange("30"); await RefreshProfitAsync(); };
        Profit90Button.Click += async (_, _) => { SetProfitRange("90"); await RefreshProfitAsync(); };
        ProfitYearButton.Click += async (_, _) => { SetProfitRange("year"); await RefreshProfitAsync(); };
        ProfitRefreshButton.Click += async (_, _) => await RefreshProfitAsync();
    }

    private void SetProfitRange(string preset)
    {
        if (!DateTime.TryParseExact(
                _tracker.GetMiningDayLabel(),
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var today))
            today = DateTime.Today;

        DateTime from;
        DateTime to;

        switch (preset)
        {
            case "yesterday":
                from = to = today.AddDays(-1);
                break;
            case "thisweek":
                int daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
                from = today.AddDays(-daysSinceMonday);
                to = today;
                break;
            case "lastweek":
                int offset = ((int)today.DayOfWeek + 6) % 7;
                DateTime thisWeek = today.AddDays(-offset);
                from = thisWeek.AddDays(-7);
                to = thisWeek.AddDays(-1);
                break;
            case "thismonth":
                from = new DateTime(today.Year, today.Month, 1);
                to = today;
                break;
            case "lastmonth":
                DateTime thisMonth = new DateTime(today.Year, today.Month, 1);
                from = thisMonth.AddMonths(-1);
                to = thisMonth.AddDays(-1);
                break;
            case "30":
                from = today.AddDays(-29);
                to = today;
                break;
            case "90":
                from = today.AddDays(-89);
                to = today;
                break;
            case "year":
                from = today.AddDays(-364);
                to = today;
                break;
            default:
                from = to = today;
                break;
        }

        _profitFrom = from.Date;
        _profitTo = to.Date;
        ProfitRangeText.Text = from == to
            ? from.ToString("dd MMM yyyy", CultureInfo.CurrentCulture)
            : $"{from:dd MMM yyyy} -> {to:dd MMM yyyy}";
    }

    private async System.Threading.Tasks.Task RefreshProfitAsync()
    {
        if (!ProfitTab.IsSelected)
            return;

        var aggregates = _tracker.GetMiningHistoryRange(_profitFrom, _profitTo);

        var ores = aggregates
            .Select(r => r.Ore)
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ores.Count > 0)
            await System.Threading.Tasks.Task.WhenAll(ores.Select(o => _tracker.EnsureMiningQuoteAsync(o)));

        double totalUnits = aggregates.Sum(r => r.Units);
        double totalNormal = aggregates.Sum(r => r.NormalUnits);
        double totalCriticalUnits = aggregates.Sum(r => r.CriticalUnits);
        int totalCrits = aggregates.Sum(r => r.Crits);
        int totalCycles = aggregates.Sum(r => r.Cycles);

        double totalProfit = 0;
        double totalBuyback = 0;
        double totalM3 = 0;

        var oreRows = new List<ProfitOreRow>();

        foreach (var group in aggregates
                     .GroupBy(r => r.Ore, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Sum(r => r.Units)))
        {
            string ore = group.Key;
            double units = group.Sum(r => r.Units);
            double normal = group.Sum(r => r.NormalUnits);
            double critical = group.Sum(r => r.CriticalUnits);

            double jitaUnit = 0;
            double amarrUnit = 0;
            double bestValue = 0;
            double bbValue = 0;

            if (_tracker.TryGetMiningQuote(ore, out var quote) && quote.IsAvailable)
            {
                totalM3 += units * quote.UnitVolumeM3;

                jitaUnit = _tracker.GetMarketUnitPrice(quote, "Jita", _settings.MiningMarketPriceMode);
                amarrUnit = _tracker.GetMarketUnitPrice(quote, "Amarr", _settings.MiningMarketPriceMode);

                if (_settings.MiningMarketJitaEnabled)
                    bestValue = Math.Max(bestValue, units * jitaUnit);
                if (_settings.MiningMarketAmarrEnabled)
                    bestValue = Math.Max(bestValue, units * amarrUnit);

                double bbUnit = _tracker.GetMarketUnitPrice(
                    quote,
                    _settings.MiningCorpBuybackMarket,
                    _settings.MiningCorpBuybackPriceMode);

                bbValue = units * bbUnit *
                    Math.Clamp(_settings.MiningCorpBuybackPercent, 0, 100) / 100.0;
            }

            totalProfit += bestValue;
            totalBuyback += bbValue;

            oreRows.Add(new ProfitOreRow
            {
                Ore = ore,
                NormalText = normal.ToString("N0", CultureInfo.CurrentCulture),
                CriticalText = critical > 0
                    ? "+" + critical.ToString("N0", CultureInfo.CurrentCulture)
                    : "0",
                CombinedText = units.ToString("N0", CultureInfo.CurrentCulture),
                PercentText = totalUnits > 0 ? $"{units * 100.0 / totalUnits:F1}%" : "0.0%",
                JitaUnitText = jitaUnit > 0 ? Price(jitaUnit) : "-",
                AmarrUnitText = amarrUnit > 0 ? Price(amarrUnit) : "-",
                BestValueText = Isk(bestValue),
                BuybackText = Isk(bbValue)
            });
        }

        var characterRows = new List<ProfitCharacterRow>();

        foreach (var group in aggregates
                     .GroupBy(r => r.Character, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => g.Sum(r => r.Units)))
        {
            double units = group.Sum(r => r.Units);
            int crits = group.Sum(r => r.Crits);
            int cycles = group.Sum(r => r.Cycles);
            double m3 = 0;
            double profit = 0;
            double buyback = 0;

            foreach (var r in group)
            {
                if (!_tracker.TryGetMiningQuote(r.Ore, out var quote) || !quote.IsAvailable)
                    continue;

                m3 += r.Units * quote.UnitVolumeM3;

                double jita = r.Units * _tracker.GetMarketUnitPrice(
                    quote, "Jita", _settings.MiningMarketPriceMode);
                double amarr = r.Units * _tracker.GetMarketUnitPrice(
                    quote, "Amarr", _settings.MiningMarketPriceMode);

                if (_settings.MiningMarketJitaEnabled) profit += jita;
                if (_settings.MiningMarketAmarrEnabled)
                {
                    // Per ore choose the better enabled market, not Jita+Amarr.
                    double currentBestForOre = Math.Max(
                        _settings.MiningMarketJitaEnabled ? jita : 0,
                        amarr);
                    double jitaContribution = _settings.MiningMarketJitaEnabled ? jita : 0;
                    profit -= jitaContribution;
                    profit += currentBestForOre;
                }

                double bbUnit = _tracker.GetMarketUnitPrice(
                    quote,
                    _settings.MiningCorpBuybackMarket,
                    _settings.MiningCorpBuybackPriceMode);
                buyback += r.Units * bbUnit *
                    Math.Clamp(_settings.MiningCorpBuybackPercent, 0, 100) / 100.0;
            }

            double critPct = cycles > 0 ? crits * 100.0 / cycles : 0;

            characterRows.Add(new ProfitCharacterRow
            {
                Character = group.Key,
                UnitsText = units.ToString("N0", CultureInfo.CurrentCulture),
                VolumeText = m3 > 0 ? m3.ToString("N0", CultureInfo.CurrentCulture) : "-",
                CritText = $"{crits}/{cycles} ({critPct:F1}%)",
                ProfitText = Isk(profit),
                BuybackText = Isk(buyback)
            });
        }

        ProfitOreGrid.ItemsSource = oreRows;
        ProfitCharacterGrid.ItemsSource = characterRows;

        ProfitTotalMinedText.Text = totalUnits > 0 ? totalUnits.ToString("N0", CultureInfo.CurrentCulture) : "-";
        ProfitNormalText.Text = totalNormal > 0 ? totalNormal.ToString("N0", CultureInfo.CurrentCulture) : "-";
        ProfitCriticalUnitsText.Text = totalCriticalUnits > 0
            ? "+" + totalCriticalUnits.ToString("N0", CultureInfo.CurrentCulture)
            : "0";
        ProfitCriticalCountText.Text = totalCrits.ToString("N0", CultureInfo.CurrentCulture);
        ProfitMarketText.Text = Isk(totalProfit);
        ProfitBuybackText.Text = Isk(totalBuyback);

        int miners = aggregates.Select(r => r.Character).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int oreTypes = aggregates.Select(r => r.Ore).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int miningDays = Math.Max(1, aggregates.Select(r => r.DayKey).Distinct(StringComparer.Ordinal).Count());
        double critRate = totalCycles > 0 ? totalCrits * 100.0 / totalCycles : 0;
        double avgDay = totalUnits / miningDays;
        double avgMiner = miners > 0 ? totalUnits / miners : 0;

        ProfitFleetStatsText.Text =
            $"{miners} miners | {oreTypes} ore types | {miningDays} mining day(s) | " +
            $"{totalCycles:N0} mining pulls | crit rate {critRate:F1}% | " +
            $"avg/day {avgDay:N0} units | avg/miner {avgMiner:N0} units | volume {totalM3:N0} m3";

        var status = _tracker.GetMiningHistoryStatus();
        ProfitBuildText.Text = status.IsRunning
            ? $"{status.Message} | {status.ProgressPercent:F0}%"
            : status.Message;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) =>
        Close();
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
        value <= 0 ? "-" : StatTrackerService.FormatNumber(value);

    private static string Price(double value) =>
        value <= 0 ? "-" : value.ToString("N2", CultureInfo.CurrentCulture);

    private static string Number(double value) =>
        value <= 0 ? "-" : value.ToString("N0", CultureInfo.CurrentCulture);

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
            VolumeM3Text = "loading...",
            JitaUnitText = "loading...",
            AmarrUnitText = "loading...",
            BestMarket = "-",
            JitaValueText = "-",
            AmarrValueText = "-",
            BestValueText = "-"
        };
    }


    private sealed class ProfitOreRow
    {
        public string Ore { get; init; } = "";
        public string NormalText { get; init; } = "";
        public string CriticalText { get; init; } = "";
        public string CombinedText { get; init; } = "";
        public string PercentText { get; init; } = "";
        public string JitaUnitText { get; init; } = "";
        public string AmarrUnitText { get; init; } = "";
        public string BestValueText { get; init; } = "";
        public string BuybackText { get; init; } = "";
    }

    private sealed class ProfitCharacterRow
    {
        public string Character { get; init; } = "";
        public string UnitsText { get; init; } = "";
        public string VolumeText { get; init; } = "";
        public string CritText { get; init; } = "";
        public string ProfitText { get; init; } = "";
        public string BuybackText { get; init; } = "";
    }
    private sealed class HistoryRow
    {
        public string Day { get; init; } = "";
        public string Character { get; init; } = "";
        public string Ore { get; init; } = "";
        public string UnitsText { get; init; } = "";
        public string VolumeText { get; init; } = "";
        public string CritText { get; init; } = "";
        public string ProfitText { get; init; } = "";
        public string BuybackText { get; init; } = "";
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
            ReferenceUnitText = "loading...",
            GrossText = "-",
            RateText = $"{pct:0.##}%",
            PayoutText = "-"
        };
    }
}
