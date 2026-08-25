using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

public partial class MiningFleetOverviewWindow : Window
{
    private readonly StatTrackerService _tracker;
    private readonly MiningIdleWatchdogService _watchdog;
    private readonly MiningDashboardPreferences _prefs;
    private readonly DispatcherTimer _timer;

    public MiningFleetOverviewWindow(
        StatTrackerService tracker,
        MiningIdleWatchdogService watchdog,
        MiningDashboardPreferences prefs)
    {
        InitializeComponent();
        _tracker = tracker;
        _watchdog = watchdog;
        _prefs = prefs;

        Topmost = prefs.FleetOverviewTopmost;
        Opacity = Math.Clamp(prefs.FleetOverviewOpacityPercent, 55, 100) / 100.0;

        if (prefs.FleetOverviewX.HasValue && prefs.FleetOverviewY.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = prefs.FleetOverviewX.Value;
            Top = prefs.FleetOverviewY.Value;
        }

        Width = Math.Max(MinWidth, prefs.FleetOverviewWidth);
        Height = Math.Max(MinHeight, prefs.FleetOverviewHeight);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshCards();
        _timer.Start();

        Loaded += (_, _) => RefreshCards();
        SizeChanged += (_, _) => RefreshCards();

        Closed += (_, _) =>
        {
            _timer.Stop();
            _prefs.FleetOverviewX = Left;
            _prefs.FleetOverviewY = Top;
            _prefs.FleetOverviewWidth = Width;
            _prefs.FleetOverviewHeight = Height;
            MiningDashboardPreferencesStore.Save(_prefs);
        };
    }

    private void RefreshCards()
    {
        var cards = new List<FleetCard>();

        foreach (var character in _tracker.GetTrackedCharacters())
        {
            var s = _tracker.GetSnapshot(character);
            if (s.MiningCycleCount <= 0 && string.IsNullOrWhiteSpace(s.CurrentOre))
                continue;

            var state = _watchdog.GetState(character);
            var crit = _tracker.GetTodayMiningCritSummary(character);

            cards.Add(new FleetCard
            {
                Character = character,
                Ore = string.IsNullOrWhiteSpace(s.CurrentOre) ? "-" : s.CurrentOre,
                BaseText = s.BaseM3PerSec > 0
                    ? $"{s.BaseM3PerSec.ToString("N1", CultureInfo.CurrentCulture)} m3/s"
                    : "warming...",
                ActualText = s.ActualM3PerSec > 0
                    ? $"{s.ActualM3PerSec.ToString("N1", CultureInfo.CurrentCulture)} m3/s"
                    : "warming...",
                CritText = crit.Cycles > 0 ? crit.ToString() : "-",
                ValueText = s.SessionBestValue > 0
                    ? StatTrackerService.FormatNumber(s.SessionBestValue)
                    : "-",
                BuybackText = s.SessionBuybackValue > 0
                    ? StatTrackerService.FormatNumber(s.SessionBuybackValue)
                    : "-",
                Status = state.Label,
                StatusText = state.Kind switch
                {
                    MiningIdleKind.Mining => "Stable",
                    MiningIdleKind.Late => $"Late - {AgeText(state.AgeSeconds)} since pull",
                    MiningIdleKind.Degraded => "Yield drop detected - relearning baseline",
                    MiningIdleKind.Idle => $"Idle - {AgeText(state.AgeSeconds)} since pull",
                    _ => "Warming up"
                }
            });
        }

        var ordered = cards
            .OrderBy(x => x.Character, StringComparer.OrdinalIgnoreCase)
            .ToList();

        double available = Math.Max(500, ActualWidth - 28);
        int count = Math.Max(1, ordered.Count);
        double ideal = (available - Math.Max(0, count - 1) * 6) / count;
        double cardWidth = Math.Clamp(Math.Floor(ideal), 150, 205);

        foreach (var card in ordered)
            card.CardWidth = cardWidth;

        MinerItems.ItemsSource = ordered;

        DayText.Text = $"DAY {_tracker.GetMiningDayLabel()}";
        UpdatedText.Text = $"{cards.Count} miners | {DateTime.Now:HH:mm:ss}";
    }

    private static string AgeText(double seconds)
    {
        if (seconds < 60) return $"{Math.Round(seconds):0}s";
        if (seconds < 3600) return $"{Math.Floor(seconds / 60):0}m {Math.Round(seconds % 60):0}s";
        return $"{Math.Floor(seconds / 3600):0}h {Math.Floor((seconds % 3600) / 60):0}m";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class FleetCard
    {
        public double CardWidth { get; set; } = 170;
        public string Character { get; init; } = "";
        public string Ore { get; init; } = "";
        public string BaseText { get; init; } = "";
        public string ActualText { get; init; } = "";
        public string CritText { get; init; } = "";
        public string ValueText { get; init; } = "";
        public string BuybackText { get; init; } = "";
        public string Status { get; init; } = "";
        public string StatusText { get; init; } = "";
    }
}
