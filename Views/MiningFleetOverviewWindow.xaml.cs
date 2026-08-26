using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

public partial class MiningFleetOverviewWindow : Window
{
    private readonly StatTrackerService _tracker;
    private readonly MiningIdleWatchdogService _watchdog;
    private readonly MiningDashboardPreferences _prefs;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _pilotIntelTimer;
    private readonly EveSsoService _pilotSso = new();

    private readonly Dictionary<string, EveMiningShipIntel>
        _pilotIntel =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string>
        _portraitUrls =
            new(StringComparer.OrdinalIgnoreCase);

    private bool _pilotIntelRefreshBusy;

    private const double ManualOrcaShieldBoostPercent = 19.7;

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

        ApplyResizeMode();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            // RefreshCards replaces the ItemsSource with fresh card objects.
            // Doing that while the mouse is over a card destroys the tooltip
            // owner before WPF can keep the tooltip open. Pause VISUAL refresh
            // while hovering; the parser/tracker continues recording normally.
            if (!IsMouseOver)
                RefreshCards();
        };
        _timer.Start();

        _pilotIntelTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _pilotIntelTimer.Tick +=
            async (_, _) =>
                await RefreshPilotIntelAsync();
        _pilotIntelTimer.Start();

        Loaded +=
            async (_, _) =>
            {
                RefreshCards();
                await RefreshPilotIntelAsync();
            };

        SizeChanged += (_, _) => RefreshCards();

        Closed += (_, _) =>
        {
            _timer.Stop();
            _pilotIntelTimer.Stop();
            _prefs.FleetOverviewX = Left;
            _prefs.FleetOverviewY = Top;
            if (_prefs.AllowFleetOverviewResize)
            {
                _prefs.FleetOverviewWidth = Width;
                _prefs.FleetOverviewHeight = Height;
            }

            MiningDashboardPreferencesStore.Save(_prefs);
        };
    }

    private async Task RefreshPilotIntelAsync()
    {
        if (_pilotIntelRefreshBusy)
            return;

        _pilotIntelRefreshBusy = true;

        try
        {
            IReadOnlyList<EvePilotProfile> profiles =
                await _pilotSso.LoadPilotsAsync();

            var wanted =
                new HashSet<string>(
                    _tracker.GetMiningDashboardCharacters(),
                    StringComparer.OrdinalIgnoreCase);

            try
            {
                IReadOnlyDictionary<string, long> ids =
                    await _pilotSso.ResolveCharacterIdsAsync(
                        wanted);

                _portraitUrls.Clear();

                foreach (KeyValuePair<string, long> entry in ids)
                {
                    _portraitUrls[entry.Key] =
                        "https://images.evetech.net/characters/" +
                        entry.Value.ToString(
                            CultureInfo.InvariantCulture) +
                        "/portrait?size=64";
                }
            }
            catch
            {
                // Portraits are cosmetic. Never block miner tracking if the
                // public universe name resolver is temporarily unavailable.
            }

            var gate =
                new SemaphoreSlim(2);

            var tasks =
                profiles
                    .Where(
                        profile =>
                            wanted.Contains(
                                profile.CharacterName))
                    .Select(
                        async profile =>
                        {
                            await gate.WaitAsync();

                            try
                            {
                                return await _pilotSso
                                    .GetMiningShipIntelAsync(
                                        profile);
                            }
                            catch
                            {
                                return null;
                            }
                            finally
                            {
                                gate.Release();
                            }
                        })
                    .ToArray();

            EveMiningShipIntel?[] resolved =
                await Task.WhenAll(tasks);

            _pilotIntel.Clear();

            foreach (EveMiningShipIntel? intel
                     in resolved)
            {
                if (intel == null ||
                    string.IsNullOrWhiteSpace(
                        intel.CharacterName))
                    continue;

                _pilotIntel[intel.CharacterName] =
                    intel;
            }

            if (!IsMouseOver)
                RefreshCards();
        }
        finally
        {
            _pilotIntelRefreshBusy = false;
        }
    }

    private void RefreshCards()
    {
        var cards = new List<FleetCard>();

        double fleetShieldExtension = 0;
        double fleetShieldHarmonizing = 0;
        string fleetBoostSource = "";

        foreach (KeyValuePair<string, EveMiningShipIntel> pair
                 in _pilotIntel)
        {
            if (!pair.Value.IsOrca)
                continue;

            string mode =
                GetOrcaBoostMode(
                    pair.Key);

            if (mode is "EXT" or "BOTH")
            {
                fleetShieldExtension =
                    Math.Max(
                        fleetShieldExtension,
                        ManualOrcaShieldBoostPercent);
            }

            if (mode is "HARM" or "BOTH")
            {
                fleetShieldHarmonizing =
                    Math.Max(
                        fleetShieldHarmonizing,
                        ManualOrcaShieldBoostPercent);
            }

            if (mode != "OFF")
            {
                fleetBoostSource =
                    $"{pair.Key}: manual {mode} boost";
            }
        }
        foreach (var character in _tracker.GetMiningDashboardCharacters())
        {
            var s = _tracker.GetSnapshot(character);
            if (s.MiningCycleCount <= 0 &&
                string.IsNullOrWhiteSpace(s.CurrentOre))
                continue;

            _pilotIntel.TryGetValue(
                character,
                out EveMiningShipIntel? shipIntel);

            bool isOrca =
                shipIntel?.IsOrca == true;

            string orcaBoostMode =
                isOrca
                    ? GetOrcaBoostMode(
                        character)
                    : "OFF";

            int fittedLaserCount =
                shipIntel?.MiningLaserCount ?? -1;

            var state = _watchdog.GetState(character);
            var crit = _tracker.GetTodayMiningCritSummary(character);

            ObservedMiningRate droneAverage =
                isOrca
                    ? _tracker.GetObservedMiningAverage(
                        character)
                    : new ObservedMiningRate();

            double displayBaseRate =
                s.BaseM3PerSec > 0
                    ? s.BaseM3PerSec
                    : isOrca &&
                      droneAverage.Ready
                        ? droneAverage.BaseM3PerSec
                        : 0;

            double displayActualRate =
                s.ActualM3PerSec > 0
                    ? s.ActualM3PerSec
                    : isOrca &&
                      droneAverage.Ready
                        ? droneAverage.ActualM3PerSec
                        : 0;

            var laserTiming =
                _tracker.GetMiningLaserTiming(
                    character,
                    shipIntel?.RepresentativeLaserBaseCycleSeconds);
            bool manualAlarmMuted =
                _watchdog.IsCharacterAlarmMuted(
                    character);

            bool automaticSuppression =
                _watchdog
                    .IsCharacterAlarmAutomaticallySuppressed(
                        character);

            bool alarmMuted =
                manualAlarmMuted ||
                automaticSuppression;

            string lastPullAge = laserTiming.LastPullUtc.HasValue
                ? AgeText(Math.Max(
                    0,
                    (DateTime.UtcNow - laserTiming.LastPullUtc.Value).TotalSeconds))
                : "not seen";

            string lastPullClock = laserTiming.LastPullUtc.HasValue
                ? laserTiming.LastPullUtc.Value.ToLocalTime()
                    .ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)
                : "-";

            string laserText;
            string laserToolTip;

            string CrystalForLane(
                int index)
            {
                if (shipIntel == null ||
                    index < 0 ||
                    index >= shipIntel.MiningLasers.Count)
                    return "";

                string crystal =
                    shipIntel.MiningLasers[index]
                        .ShortCrystal;

                return string.IsNullOrWhiteSpace(
                        crystal)
                    ? ""
                    : " " + crystal;
            }

            string fittedLaserDetail =
                shipIntel == null ||
                shipIntel.MiningLasers.Count == 0
                    ? ""
                    : string.Join(
                        Environment.NewLine,
                        shipIntel.MiningLasers.Select(
                            laser =>
                                $"{laser.Slot}: {laser.Name}" +
                                (laser.BaseCycleSeconds > 0
                                    ? $" | module base {laser.BaseCycleSeconds:F2}s" +
                                      (laser.DynamicCycle
                                          ? " (Abyssal dynamic)"
                                          : "")
                                    : "") +
                                (string.IsNullOrWhiteSpace(
                                    laser.ShortCrystal)
                                    ? ""
                                    : $" | crystal {laser.ShortCrystal}")));

            if (isOrca)
            {
                laserText =
                    "DRONE MINING";

                laserToolTip =
                    $"Orca detected from ESI.{Environment.NewLine}" +
                    $"This tile uses drone-mining mode; strip-miner timing is hidden.{Environment.NewLine}" +
                    $"Mining alarm suppression is automatic while this pilot is in the Orca.";
            }
            else if (fittedLaserCount == 0)
            {
                laserText =
                    "NO MINING LASERS";

                laserToolTip =
                    $"Current fit contains no mining laser / strip miner in a high slot.{Environment.NewLine}" +
                    $"No laser cycle is shown.";
            }
            else if (
                laserTiming.Ready &&
                laserTiming.Laser1CycleSeconds.HasValue &&
                laserTiming.Laser2CycleSeconds.HasValue)
            {
                laserText =
                    $"L1 {laserTiming.Laser1CycleSeconds.Value:F1}s{CrystalForLane(0)}   " +
                    $"L2 {laserTiming.Laser2CycleSeconds.Value:F1}s{CrystalForLane(1)}";

                laserToolTip =
                    $"Observed effective mining cycle from EVE pull timestamps, constrained by the fitted module's Dogma duration.{Environment.NewLine}" +
                    $"L1: {laserTiming.Laser1CycleSeconds:F2}s{CrystalForLane(0)}{Environment.NewLine}" +
                    $"L2: {laserTiming.Laser2CycleSeconds:F2}s{CrystalForLane(1)}{Environment.NewLine}" +
                    $"Fitted mining lasers detected: " +
                    (fittedLaserCount >= 0
                        ? fittedLaserCount.ToString(
                            CultureInfo.InvariantCulture)
                        : "unknown - reconnect for assets") +
                    $"{Environment.NewLine}" +
                    $"Last pull: {lastPullAge} ago at {lastPullClock}." +
                    (string.IsNullOrWhiteSpace(fittedLaserDetail)
                        ? ""
                        : $"{Environment.NewLine}{Environment.NewLine}{fittedLaserDetail}");
            }
            else
            {
                string crystalSummary =
                    shipIntel == null
                        ? ""
                        : string.Join(
                            "/",
                            shipIntel.MiningLasers
                                .Select(
                                    laser =>
                                        laser.ShortCrystal)
                                .Where(
                                    crystal =>
                                        !string.IsNullOrWhiteSpace(
                                            crystal)));

                laserText =
                    fittedLaserCount > 0
                        ? $"{fittedLaserCount} LASER(S)" +
                          (string.IsNullOrWhiteSpace(
                              crystalSummary)
                              ? ""
                              : $" {crystalSummary}") +
                          " | learning"
                        : "L1 --   L2 --";

                laserToolTip =
                    $"Cycle timing is warming up.{Environment.NewLine}" +
                    $"Last pull: {lastPullAge} ago at {lastPullClock}.{Environment.NewLine}" +
                    (shipIntel?.AssetsAvailable == true
                        ? $"Fitted mining lasers detected: {fittedLaserCount}."
                        : "Reconnect this pilot for asset access to verify the fitted laser count.");
            }

            string statusText = isOrca
                ? "Drone mining"
                : alarmMuted
                    ? (state.AgeSeconds > 0
                        ? $"Muted - {AgeText(state.AgeSeconds)}"
                        : "Muted")
                    : state.Kind switch
                {
                    MiningIdleKind.Mining => "Stable",
                    MiningIdleKind.Late => $"Late - {AgeText(state.AgeSeconds)}",
                    MiningIdleKind.Degraded => "Yield drop",
                    MiningIdleKind.Idle => $"Idle - {AgeText(state.AgeSeconds)}",
                    _ => "Warming up"
                };

            string statusToolTip = isOrca
                ? $"Orca drone mining detected.{Environment.NewLine}" +
                  $"Mining-drone pull spacing changes with drone travel distance, so the no-pull alarm is automatically suppressed."
                : alarmMuted
                    ? $"Alarm is muted for {character}.{Environment.NewLine}" +
                      $"Last mining pull: {lastPullAge} ago at {lastPullClock}."
                    : state.Kind switch
                {
                    MiningIdleKind.Mining =>
                        $"Mining is stable.{Environment.NewLine}" +
                        $"Last mining pull: {lastPullAge} ago at {lastPullClock}.",
                    MiningIdleKind.Late =>
                        $"Mining pull is late.{Environment.NewLine}" +
                        $"Last mining pull: {lastPullAge} ago at {lastPullClock}.",
                    MiningIdleKind.Degraded =>
                        $"Yield drop detected; baseline is being relearned.{Environment.NewLine}" +
                        $"Last mining pull: {lastPullAge} ago at {lastPullClock}.",
                    MiningIdleKind.Idle =>
                        $"Mining appears idle.{Environment.NewLine}" +
                        $"Last mining pull: {lastPullAge} ago at {lastPullClock}.",
                    _ =>
                        $"Mining baseline is warming up.{Environment.NewLine}" +
                        $"Last mining pull: {lastPullAge} ago at {lastPullClock}."
                };

            _portraitUrls.TryGetValue(
                character,
                out string? portraitUrl);

            cards.Add(new FleetCard
            {
                Character = character,
                PortraitUrl = portraitUrl ?? "",
                ShipText =
                    string.IsNullOrWhiteSpace(
                        shipIntel?.CurrentShip.TypeName)
                        ? "Ship: reconnect/sync"
                        : "Ship: " +
                          shipIntel.CurrentShip.TypeName,
                ShipToolTip =
                    shipIntel == null
                        ? "Connect this character in Pilot Command Center to sync ship and fitting data."
                        : shipIntel.CurrentShip.DisplayName +
                          (shipIntel.AssetsAvailable
                              ? $"{Environment.NewLine}Asset/fitting access available."
                              : $"{Environment.NewLine}Reconnect for asset access to identify fitted mining lasers."),
                Ore = string.IsNullOrWhiteSpace(s.CurrentOre) ? "-" : s.CurrentOre,
                BaseText = displayBaseRate > 0
                    ? $"{displayBaseRate.ToString("N1", CultureInfo.CurrentCulture)} m3/s"
                    : "warming...",
                ActualText = displayActualRate > 0
                    ? $"{displayActualRate.ToString("N1", CultureInfo.CurrentCulture)} m3/s"
                    : "warming...",
                CritText = crit.Cycles > 0 ? crit.ToString() : "-",
                ValueText = s.SessionBestValue > 0
                    ? StatTrackerService.FormatNumber(s.SessionBestValue)
                    : "-",
                BuybackText = s.SessionBuybackValue > 0
                    ? StatTrackerService.FormatNumber(s.SessionBuybackValue)
                    : "-",
                AlarmMuted = alarmMuted,
                // Keep the DRONE badge enabled so WPF does not wash out the
                // intentionally bright blue/cyan style. Orca clicks are
                // ignored by AlarmToggle_Click.
                AlarmEnabled = true,
                AlarmButtonText = isOrca
                    ? "DRONE"
                    : manualAlarmMuted
                        ? "ALARM OFF"
                        : "ALARM ON",
                AlarmToolTip = isOrca
                    ? "DRONE MINING: idle-pull alarm is automatically suppressed. This is an indicator, not an alarm toggle."
                    : manualAlarmMuted
                        ? $"Enable mining alarms for {character}"
                        : $"Mute mining alarms for {character}",
                Status = alarmMuted ? "MUTED" : state.Label,
                StatusText = statusText,
                StatusToolTip = statusToolTip,
                LaserText = laserText,
                LaserToolTip = laserToolTip,
                EhpText =
                    shipIntel?.Defense.Available == true
                        ? shipIntel.Defense
                            .ApplyShieldCommandBoost(
                                fleetShieldExtension,
                                fleetShieldHarmonizing)
                            .EhpText +
                          (
                              fleetShieldExtension > 0 ||
                              fleetShieldHarmonizing > 0
                                  ? "*"
                                  : ""
                          )
                        : "EHP --",
                EhpToolTip =
                    shipIntel?.Defense.Available == true
                        ? shipIntel.Defense
                            .ApplyShieldCommandBoost(
                                fleetShieldExtension,
                                fleetShieldHarmonizing)
                            .ToolTip +
                          (
                              fleetShieldExtension > 0 ||
                              fleetShieldHarmonizing > 0
                                  ? $"{Environment.NewLine}{Environment.NewLine}* Manual Orca shield boost is ON. The fleet estimate assumes that burst is active, in range and affecting this pilot.{Environment.NewLine}" +
                                    $"Extension/HP: {fleetShieldExtension:F1}% | Harmonizing/RES: {fleetShieldHarmonizing:F1}%{Environment.NewLine}" +
                                    fleetBoostSource
                                  : ""
                          )
                        : "Connect this pilot with asset access to calculate fit EHP.",
                IsDroneMining = isOrca,
                BoostMode = orcaBoostMode,
                BoostButtonText =
                    orcaBoostMode switch
                    {
                        "HARM" => "RES 19.7",
                        "EXT" => "HP 19.7",
                        "BOTH" => "BOTH 19.7",
                        _ => "BOOST -"
                    },
                BoostToolTip =
                    isOrca
                        ? "Manual live shield-command state.\n" +
                          "Click cycles: OFF -> RES 19.7 -> HP 19.7 -> BOTH 19.7 -> OFF.\n\n" +
                          "RES = Shield Harmonizing: 19.7% resonance reduction.\n" +
                          "HP = Shield Extension: +19.7% shield capacity.\n" +
                          "This manual state is used for fleet EHP because ESI cannot confirm whether a burst is actually running/in range."
                        : "",
                OreToolTip =
                    $"Current ore: {(string.IsNullOrWhiteSpace(s.CurrentOre) ? "-" : s.CurrentOre)}{Environment.NewLine}" +
                    $"Last mining pull: {lastPullAge} ago at {lastPullClock}.",
                BaseToolTip =
                    $"BASE = recent non-critical mining yield.{Environment.NewLine}" +
                    $"Current BASE: {displayBaseRate:F1} m3/s{Environment.NewLine}" +
                    (isOrca && s.BaseM3PerSec <= 0 && droneAverage.Ready
                        ? $"Using longer observed drone-mining average ({droneAverage.SampleCount} pulls).{Environment.NewLine}"
                        : "") +
                    $"Last mining pull: {lastPullAge} ago at {lastPullClock}.",
                RealToolTip =
                    $"REAL = observed yield including critical pulls.{Environment.NewLine}" +
                    $"Current REAL: {displayActualRate:F1} m3/s{Environment.NewLine}" +
                    (isOrca && s.ActualM3PerSec <= 0 && droneAverage.Ready
                        ? $"Using longer observed drone-mining average ({droneAverage.SampleCount} pulls).{Environment.NewLine}"
                        : "") +
                    $"Last mining pull: {lastPullAge} ago at {lastPullClock}.",
                ProfitToolTip =
                    $"Session market-value estimate: {s.SessionBestValue:N0} ISK.{Environment.NewLine}" +
                    $"Open Mining Command Center for the detailed market breakdown.",
                BuybackToolTip =
                    $"Session buyback-value estimate: {s.SessionBuybackValue:N0} ISK.",
                CritToolTip = crit.Cycles > 0
                    ? $"Critical mining today: {crit}.{Environment.NewLine}" +
                      $"Estimated critical bonus volume: {s.MiningCritBonusM3:N1} m3."
                    : "No mining pulls recorded for today's critical summary yet."
            });
        }

        var ordered = cards
            .OrderBy(x => x.Character, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int minerCount = ordered.Count;

        const double cardWidth = 216;
        const double cardGap = 6;
        const double windowChrome = 44;

        foreach (var card in ordered)
            card.CardWidth = cardWidth;

        ApplyResizeMode();

        if (!_prefs.AllowFleetOverviewResize)
        {
            double desiredWidth = minerCount > 0
                ? windowChrome +
                  minerCount * (cardWidth + cardGap)
                : 620;

            desiredWidth = Math.Max(620, desiredWidth);

            if (Math.Abs(Width - desiredWidth) > 1)
                Width = desiredWidth;

            // Height is intentionally NOT assigned here. In automatic mode WPF
            // measures the larger fonts, labels and alarm button and grows the
            // window just enough to fit them. This prevents future font/layout
            // changes from reintroducing clipping.

            // If the wall grows near the right edge of the Windows virtual desktop,
            // slide it left instead of clipping the new miner tile.
            double virtualLeft = SystemParameters.VirtualScreenLeft;
            double virtualRight =
                SystemParameters.VirtualScreenLeft +
                SystemParameters.VirtualScreenWidth;

            if (Left + Width > virtualRight)
                Left = Math.Max(virtualLeft, virtualRight - Width);
        }

        MinerItems.ItemsSource = ordered;

        DayText.Text = $"DAY {_tracker.GetMiningDayLabel()}";
        UpdatedText.Text = $"{cards.Count} miners | {DateTime.Now:HH:mm:ss}";
    }

    private void ApplyResizeMode()
    {
        if (_prefs.AllowFleetOverviewResize)
        {
            // Manual mode behaves like a normal window again.
            SizeToContent = SizeToContent.Manual;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            MinHeight = 142;
            MaxHeight = double.PositiveInfinity;

            if (MinerScroll != null)
            {
                MinerScroll.HorizontalScrollBarVisibility =
                    System.Windows.Controls.ScrollBarVisibility.Auto;
            }
        }
        else
        {
            // Automatic mode owns the width while WPF owns the height.
            // Any larger font, DPI scale or future extra row automatically
            // increases the wall height instead of clipping the controls.
            ResizeMode = ResizeMode.NoResize;
            MinHeight = 142;
            MaxHeight = double.PositiveInfinity;
            SizeToContent = SizeToContent.Height;

            if (MinerScroll != null)
            {
                MinerScroll.HorizontalScrollBarVisibility =
                    System.Windows.Controls.ScrollBarVisibility.Disabled;
            }
        }
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

    private void AlarmToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not string character ||
            string.IsNullOrWhiteSpace(character))
            return;

        if (_pilotIntel.TryGetValue(
                character,
                out EveMiningShipIntel? intel) &&
            intel.IsOrca)
        {
            // DRONE is a bright indicator only. Orca alarm suppression is
            // automatic and is not changed by clicking the badge.
            return;
        }

        bool currentlyMuted =
            _watchdog.IsCharacterAlarmMuted(character);

        _watchdog.SetCharacterAlarmMuted(
            character,
            !currentlyMuted);

        RefreshCards();
    }

    private string GetOrcaBoostMode(
        string character)
    {
        if (_prefs.OrcaShieldBoostModes.TryGetValue(
                character,
                out string? mode))
        {
            string normalized =
                (mode ?? "")
                    .Trim()
                    .ToUpperInvariant();

            if (normalized is
                "HARM" or
                "EXT" or
                "BOTH")
                return normalized;
        }

        return "OFF";
    }

    private void OrcaBoostToggle_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not string character ||
            string.IsNullOrWhiteSpace(character))
            return;

        if (!_pilotIntel.TryGetValue(
                character,
                out EveMiningShipIntel? intel) ||
            !intel.IsOrca)
            return;

        string next =
            GetOrcaBoostMode(
                character) switch
            {
                "OFF" => "HARM",
                "HARM" => "EXT",
                "EXT" => "BOTH",
                _ => "OFF"
            };

        if (next == "OFF")
        {
            _prefs.OrcaShieldBoostModes.Remove(
                character);
        }
        else
        {
            _prefs.OrcaShieldBoostModes[character] =
                next;
        }

        MiningDashboardPreferencesStore.Save(
            _prefs);

        RefreshCards();
    }
    private void OpenMiningCommandCenter_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is EveMultiPreview.App app)
            app.ShowMiningCommandCenter();
    }

    private void OpenPilotCommandCenter_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is EveMultiPreview.App app)
            app.ShowPilotCommandCenter();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class FleetCard
    {
        public double CardWidth { get; set; } = 170;
        public string Character { get; init; } = "";
        public string PortraitUrl { get; init; } = "";
        public string ShipText { get; init; } = "";
        public string ShipToolTip { get; init; } = "";
        public string Ore { get; init; } = "";
        public string BaseText { get; init; } = "";
        public string ActualText { get; init; } = "";
        public string CritText { get; init; } = "";
        public string ValueText { get; init; } = "";
        public string BuybackText { get; init; } = "";
        public bool AlarmMuted { get; init; }
        public bool AlarmEnabled { get; init; } = true;
        public string AlarmButtonText { get; init; } = "";
        public string AlarmToolTip { get; init; } = "";
        public string Status { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string StatusToolTip { get; init; } = "";
        public string LaserText { get; init; } = "";
        public string LaserToolTip { get; init; } = "";
        public string EhpText { get; init; } = "";
        public string EhpToolTip { get; init; } = "";
        public bool IsDroneMining { get; init; }
        public string BoostMode { get; init; } = "OFF";
        public string BoostButtonText { get; init; } = "";
        public string BoostToolTip { get; init; } = "";
        public string OreToolTip { get; init; } = "";
        public string BaseToolTip { get; init; } = "";
        public string RealToolTip { get; init; } = "";
        public string ProfitToolTip { get; init; } = "";
        public string BuybackToolTip { get; init; } = "";
        public string CritToolTip { get; init; } = "";
    }
}
