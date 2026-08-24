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

        if (prefs.FleetOverviewX.HasValue && prefs.FleetOverviewY.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = prefs.FleetOverviewX.Value;
            Top = prefs.FleetOverviewY.Value;
        }

        Width = Math.Max(MinWidth, prefs.FleetOverviewWidth);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RefreshCards();
        _timer.Start();

        Loaded += (_, _) => RefreshCards();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _prefs.FleetOverviewX = Left;
            _prefs.FleetOverviewY = Top;
            _prefs.FleetOverviewWidth = Width;
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
            cards.Add(new FleetCard
            {
                Character = character,
                BaseText = s.BaseM3PerSec > 0
                    ? $"{s.BaseM3PerSec.ToString("N1", CultureInfo.CurrentCulture)} m³/s"
                    : "warming…",
                ActualText = s.ActualM3PerSec > 0
                    ? $"actual {s.ActualM3PerSec.ToString("N1", CultureInfo.CurrentCulture)} m³/s"
                    : "actual warming…",
                Status = state.Label
            });
        }

        MinerItems.ItemsSource = cards.OrderBy(x => x.Character, StringComparer.OrdinalIgnoreCase).ToList();
        UpdatedText.Text = $"{cards.Count} miner(s) · {DateTime.Now:HH:mm:ss}";
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
        public string Character { get; init; } = "";
        public string BaseText { get; init; } = "";
        public string ActualText { get; init; } = "";
        public string Status { get; init; } = "";
    }
}
