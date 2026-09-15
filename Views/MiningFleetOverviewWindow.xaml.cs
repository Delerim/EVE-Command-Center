using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class MiningFleetOverviewWindow : Window
{
    private readonly BackgroundPilotRefresh _backgroundPilots = BackgroundOperations.Current.Pilots;
    private CloudBackupWindow? _cloudBackupWindow;
    private readonly CloudBackupCoordinator _cloudBackupCoordinator =
        CloudBackupCoordinator.Attach();
    private readonly StatTrackerService _tracker;
    private readonly Func<EveWindow[]> _clientSource;
    private readonly MiningIdleWatchdogService _watchdog;
    private readonly MiningDashboardPreferences _prefs;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _pilotIntelTimer;
    private readonly DispatcherTimer _plexMarketTimer;
    private readonly EveSsoService _pilotSso = new();
    private readonly MiningMarketService _plexMarket = new();

    private readonly Dictionary<string, EveMiningShipIntel>
        _pilotIntel =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string>
        _portraitUrls =
            new(StringComparer.OrdinalIgnoreCase);

    private readonly string _intelCacheFile = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EVE Command Center", "PilotData", "overview-intel-v2.json");
    private bool _pilotIntelRefreshBusy;
    private bool _plexMarketRefreshBusy;

    private bool _tileReorderMode;
    private App? RunningApp => System.Windows.Application.Current as App;
    private readonly Dictionary<IntPtr,(FrozenFrame frame,System.Windows.Media.ImageSource image)> _previewImages=new();
    private System.Windows.Point _tileDragStart;
    private string? _tileDragCharacter;

    private const string FleetTileDragFormat =
        "EVECommandCenter.FleetTileCharacter";

    private const double ManualOrcaShieldBoostPercent = 19.7;
    private const double PlexBuyHighlightThreshold = 4_500_000.0;

    public MiningFleetOverviewWindow(
        StatTrackerService tracker,
        MiningIdleWatchdogService watchdog,
        MiningDashboardPreferences prefs, Func<EveWindow[]>? clientSource = null)
    {
        _clientSource=clientSource??(()=>RunningApp?.OverviewClients??Array.Empty<EveWindow>());
        InitializeComponent();
        BackgroundOperations.Current.Access.Changed += UpdateAccess;
        Closed += (_, _) => BackgroundOperations.Current.Access.Changed -= UpdateAccess;
        UpdateAccess();

        _tracker = tracker;
        RockToggle.Content = tracker.RockTracking.Enabled ? "ROCKS ON" : "ROCKS OFF";
        _watchdog = watchdog;
        _prefs = prefs;
        IsVisibleChanged += (_,_) => ApplyCombinedMode();
        StateChanged += (_,_) => ApplyCombinedMode();
        ApplyCombinedMode();

        Topmost = prefs.FleetOverviewTopmost;
        Opacity = Math.Clamp(prefs.FleetOverviewOpacityPercent, 55, 100) / 100.0;

        if (prefs.FleetOverviewX.HasValue && prefs.FleetOverviewY.HasValue)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = prefs.FleetOverviewX.Value;
            Top = prefs.FleetOverviewY.Value;
        }

        Width = Math.Max(MinWidth, prefs.FleetOverviewWidth);
        Height = Math.Max(MinHeight, prefs.FleetOverviewHeight) + (_tracker.RockTracking.Enabled && !prefs.AllowFleetOverviewResize ? 85 : 0);

        try
        {
            if (System.IO.File.Exists(_intelCacheFile))
                foreach (var intel in System.Text.Json.JsonSerializer.Deserialize<List<EveMiningShipIntel>>(System.IO.File.ReadAllText(_intelCacheFile)) ?? new())
                    if (intel.SyncedUtc > DateTimeOffset.UtcNow.AddDays(-7)) _pilotIntel[intel.CharacterName] = intel;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Overview cache] " + ex.Message); }
        ApplyResizeMode();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
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
        _backgroundPilots.Changed += ApplyBackgroundPilotIntel;

        _plexMarketTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(5)
        };
        _plexMarketTimer.Tick +=
            async (_, _) =>
                await RefreshPlexMarketAsync();
        _plexMarketTimer.Start();

        Loaded +=
            async (_, _) =>
            {
                RefreshCards();

                await Task.WhenAll(
                    RefreshPilotIntelAsync(),
                    RefreshPlexMarketAsync());
            };

        SizeChanged += (_, _) => RefreshCards();

        Closed += (_, _) =>
        {
            _timer.Stop();
            RunningApp?.OverviewThumbnails?.SetOverviewCombined(false);
            _previewImages.Clear();
            _pilotIntelTimer.Stop();
            _backgroundPilots.Changed -= ApplyBackgroundPilotIntel;
            _plexMarketTimer.Stop();
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

    private async Task RefreshPlexMarketAsync()
    {
        if (_plexMarketRefreshBusy)
            return;

        _plexMarketRefreshBusy = true;

        try
        {
            MiningMarketQuote? quote =
                await _plexMarket.EnsureQuoteAsync(
                    "PLEX");

            if (quote == null ||
                (
                    (quote.JitaBestSell ?? 0) <= 0 &&
                    (quote.JitaBestBuy ?? 0) <= 0
                ))
            {
                PlexBuyText.Text = "--";
                PlexSellText.Text = "--";
                PlexBuyText.Foreground =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            234,
                            247,
                            244));
                PlexMarketBorder.Background =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            16,
                            29,
                            32));
                PlexMarketBorder.BorderBrush =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            49,
                            94,
                            103));

                PlexMarketBorder.ToolTip =
                    "Global PLEX market unavailable - no PLEX buy/sell orders were returned." +
                    (
                        string.IsNullOrWhiteSpace(
                            quote?.Error)
                            ? ""
                            : Environment.NewLine +
                              quote.Error
                    );

                return;
            }

            // BUY is what we pay to acquire PLEX: Jita's lowest sell order.
            // SELL is what we receive for an immediate sale: Jita's highest buy.
            double buyPrice =
                quote.JitaBestSell ?? 0;

            double sellPrice =
                quote.JitaBestBuy ?? 0;

            PlexBuyText.Text =
                FormatPlexPrice(
                    buyPrice);

            PlexSellText.Text =
                FormatPlexPrice(
                    sellPrice);

            bool buyZone =
                buyPrice > 0 &&
                buyPrice <=
                    PlexBuyHighlightThreshold;

            if (buyZone)
            {
                PlexMarketBorder.Background =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            19,
                            52,
                            42));

                PlexMarketBorder.BorderBrush =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            72,
                            199,
                            142));

                PlexBuyText.Foreground =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            124,
                            240,
                            181));
            }
            else
            {
                PlexMarketBorder.Background =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            16,
                            29,
                            32));

                PlexMarketBorder.BorderBrush =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            49,
                            94,
                            103));

                PlexBuyText.Foreground =
                    new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(
                            234,
                            247,
                            244));
            }

            string age =
                AgeText(
                    Math.Max(
                        0,
                        (DateTime.UtcNow -
                         quote.FetchedAtUtc)
                        .TotalSeconds));

            PlexMarketBorder.ToolTip =
                $"PLEX - Global PLEX Market{Environment.NewLine}" +
                $"BUY PLEX: {buyPrice:N0} ISK (global lowest sell order){Environment.NewLine}" +
                $"SELL PLEX: {sellPrice:N0} ISK (global highest buy order){Environment.NewLine}" +
                $"Buy-zone highlight: {PlexBuyHighlightThreshold:N0} ISK or lower{Environment.NewLine}" +
                $"Market quote refreshed {age} ago.";
        }
        catch (Exception ex)
        {
            PlexBuyText.Text = "--";
            PlexSellText.Text = "--";

            PlexMarketBorder.ToolTip =
                $"PLEX market refresh failed:{Environment.NewLine}" +
                ex.Message;
        }
        finally
        {
            _plexMarketRefreshBusy = false;
        }
    }

    private static string FormatPlexPrice(
        double price)
    {
        if (price <= 0)
            return "--";

        return
            (price / 1_000_000.0)
            .ToString(
                "0.00",
                CultureInfo.InvariantCulture) +
            "M";
    }
    private void ApplyBackgroundPilotIntel()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(ApplyBackgroundPilotIntel); return; }
        foreach (var intel in _backgroundPilots.Intel.Values)
        {
            if (!_pilotIntel.TryGetValue(intel.CharacterName, out var old) || intel.SyncedUtc > old.SyncedUtc)
                _pilotIntel[intel.CharacterName] = intel;
            _portraitUrls[intel.CharacterName] = $"https://images.evetech.net/characters/{intel.CharacterId}/portrait?size=64";
        }
        if (!IsMouseOver) RefreshCards();
    }
    private async Task RefreshPilotIntelAsync()
    {
        ApplyBackgroundPilotIntel();
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

            // Linked IDs are already known locally; portraits must not wait on ESI.
            foreach (var profile in profiles)
                _portraitUrls[profile.CharacterName] = $"https://images.evetech.net/characters/{profile.CharacterId}/portrait?size=64";
            foreach (var name in _pilotIntel.Keys.Where(n => !profiles.Any(p => p.CharacterName.Equals(n, StringComparison.OrdinalIgnoreCase))).ToArray())
                _pilotIntel.Remove(name);
            RefreshCards();
            var portraitTask = ResolveMissingPortraitsAsync(wanted.Where(n => !_portraitUrls.ContainsKey(n)).ToArray());

            using var gate = new SemaphoreSlim(2);

            var tasks =
                profiles
                    .Where(
                        profile =>
                            wanted.Contains(
                                profile.CharacterName))
                    .OrderByDescending(profile => _prefs.OrcaShieldBoostModes.ContainsKey(profile.CharacterName))
                    .Select(
                        async profile =>
                        {
                            await gate.WaitAsync();

                            try
                            {
                                var intel = await _pilotSso.GetMiningShipIntelAsync(profile, shipIdentified: ship =>
                                {
                                    if (!_pilotIntel.TryGetValue(profile.CharacterName, out var cached) || cached.CurrentShip.ShipItemId != ship.ShipItemId)
                                        _pilotIntel[profile.CharacterName] = new EveMiningShipIntel { CharacterId=profile.CharacterId, CharacterName=profile.CharacterName, CurrentShip=ship };
                                    if (!IsMouseOver) RefreshCards();
                                });
                                if (!_pilotIntel.TryGetValue(intel.CharacterName, out var previous) ||
                                    previous.CurrentShip.ShipItemId != intel.CurrentShip.ShipItemId ||
                                    intel.Defense.Available || !previous.Defense.Available)
                                    _pilotIntel[intel.CharacterName] = intel;
                                if (!IsMouseOver) RefreshCards();
                                return intel;
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

            await Task.WhenAll(tasks);

            // Retain successful data when one pilot fails or ESI is slow.
            try { await System.IO.File.WriteAllTextAsync(_intelCacheFile, System.Text.Json.JsonSerializer.Serialize(_pilotIntel.Values)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Overview cache] " + ex.Message); }
            await portraitTask;

            if (!IsMouseOver)
                RefreshCards();
        }
        finally
        {
            _pilotIntelRefreshBusy = false;
        }
    }

    private async Task ResolveMissingPortraitsAsync(string[] names)
    {
        if (names.Length == 0) return;
        try
        {
            foreach (var pair in await _pilotSso.ResolveCharacterIdsAsync(names))
                _portraitUrls[pair.Key] = $"https://images.evetech.net/characters/{pair.Value}/portrait?size=64";
        }
        catch { /* Cosmetic lookup cannot prevent fitting data from loading. */ }
    }

    private void Notifications_Click(object sender,RoutedEventArgs e)=>BackgroundOperations.Current.OpenNotifications();
    private void RockToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled=!_tracker.RockTracking.Enabled;
        _tracker.RockTracking.Enable(enabled);
        RockToggle.Content=enabled?"ROCKS ON":"ROCKS OFF";
        Height=Math.Max(MinHeight,Height+(enabled?85:-85));
        RefreshCards();
    }
    private void SetRocks_Click(object sender,RoutedEventArgs e)
    {
        if(sender is System.Windows.Controls.Button button && button.DataContext is FleetCard card)
            new RockTrackingWindow(_tracker.RockTracking,card.Character,card.Ore=="-"?"":card.Ore){Owner=this}.Show();
    }

    private string OverviewMode => !_prefs.CombinedCharacterOverview ? "MINING" :
        _prefs.CharacterOverviewCombatMode is "PVE" or "PVP" ? _prefs.CharacterOverviewCombatMode :
        _prefs.CharacterOverviewMiningMode ? "MINING" : "CHARACTERS";

    private FleetCard CombatCard(string character)
    {
        var data=_tracker.Combat.Snapshot(character);
        _pilotIntel.TryGetValue(character,out var intel);_portraitUrls.TryGetValue(character,out var portrait);
        bool pvp=OverviewMode=="PVP";
        string age=data.AgeSeconds.HasValue?$"Last damage {data.AgeSeconds.Value:N0}s ago":"No combat observed";
        string threat=data.ThreatAgeSeconds.HasValue?$"{data.Threat} - {data.ThreatAgeSeconds:N0}s ago":"No scramble message observed";
        return new FleetCard {
            Character=character,PortraitUrl=portrait??"",ShipText="Ship: "+(intel?.CurrentShip.TypeName??"not synced"),
            Status=data.RecentThreat?"IDLE":pvp&&data.InDps>0?"LATE":pvp?"PVP":"PVE",
            StatusToolTip=$"{age}\n{threat}\nColours reflect recent logged events, not live tackle status. Display mode does not change your alert settings.",
            CombatOut=$"{data.OutDps:N1}",CombatIn=$"{data.InDps:N1}",
            CombatSummary=pvp?$"REPS IN {data.RepIn:N0}/s  OUT {data.RepOut:N0}/s":$"BOUNTIES {_tracker.GetBountySession(character):N0} ISK",
            CombatDetail=pvp?$"PEAK HIT {data.PeakIn:N0} | "+(intel?.Defense.Available==true?$"EHP ~{intel.Defense.EhpText}":"EHP unknown"):
                $"LAST WEAPON: {(data.LastWeapon.Length>0?data.LastWeapon:"not observed")}",
            CombatNotice=data.RecentThreat?threat:age,
            CombatHint="Ammo/scripts: unavailable",
            CombatToolTip=$"DPS is actual logged damage over the last 30 seconds, including NPC and player events. Not fitted DPS.\nRepairs are logged remote repairs, not current tank or capacitor.\nBounties are session log totals, not net profit.\nLast weapon: {data.LastWeapon}\n{threat}\nEHP is a cached fitting estimate; refresh it in Pilots. Ammo quantities, loaded scripts and complete active debuffs are not available live here."
        };
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
                        pair.Value.ShieldBoost.ExtensionPercent > 0 ? pair.Value.ShieldBoost.ExtensionPercent : ManualOrcaShieldBoostPercent);
            }

            if (mode is "HARM" or "BOTH")
            {
                fleetShieldHarmonizing =
                    Math.Max(
                        fleetShieldHarmonizing,
                        pair.Value.ShieldBoost.HarmonizingPercent > 0 ? pair.Value.ShieldBoost.HarmonizingPercent : ManualOrcaShieldBoostPercent);
            }

            if (mode != "OFF")
            {
                fleetBoostSource =
                    $"{pair.Key}: manual {mode} enabled | " + (pair.Value.ShieldBoost.Configured ? pair.Value.ShieldBoost.SourceText : "fallback assumed 19.7%; burst fit not resolved");
            }
        }
        var clients=_clientSource();
        foreach (var character in (_prefs.CombinedCharacterOverview ? clients.Select(c=>c.CharacterName).Distinct(StringComparer.OrdinalIgnoreCase) : _tracker.GetMiningDashboardCharacters()))
        {
            if (OverviewMode is "PVE" or "PVP") {cards.Add(CombatCard(character));continue;}
            var s = _tracker.GetSnapshot(character);
            if (!_prefs.CombinedCharacterOverview && s.MiningCycleCount <= 0 &&
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
                    $"Cycle timing is collecting stable samples.{Environment.NewLine}" +
                    $"Last pull: {lastPullAge} ago at {lastPullClock}.{Environment.NewLine}" +
                    (shipIntel?.AssetsAvailable == true
                        ? $"Fitted mining lasers detected: {fittedLaserCount}."
                        : "Reconnect this pilot for asset access to verify the fitted laser count.");
            }

            string laserPrimaryText =
                laserText;

            string laserPrimaryCrystal =
                "";

            string laserSecondaryText =
                "";

            string laserSecondaryCrystal =
                "";

            if (!isOrca &&
                fittedLaserCount != 0 &&
                laserTiming.Ready &&
                laserTiming.Laser1CycleSeconds.HasValue &&
                laserTiming.Laser2CycleSeconds.HasValue)
            {
                laserPrimaryCrystal =
                    CrystalForLane(0)
                        .Trim();

                laserSecondaryCrystal =
                    CrystalForLane(1)
                        .Trim();

                laserPrimaryText =
                    $"L1 {laserTiming.Laser1CycleSeconds.Value:F1}s" +
                    (
                        string.IsNullOrWhiteSpace(
                            laserPrimaryCrystal)
                            ? ""
                            : " "
                    );

                laserSecondaryText =
                    "   " +
                    $"L2 {laserTiming.Laser2CycleSeconds.Value:F1}s" +
                    (
                        string.IsNullOrWhiteSpace(
                            laserSecondaryCrystal)
                            ? ""
                            : " "
                    );
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
                    _ => "Waiting"
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
                        $"No mining pull has been observed yet.{Environment.NewLine}" +
                        $"Current displayed rate is zero until a pull arrives.{Environment.NewLine}" +
                        $"Last mining pull: {lastPullAge} ago at {lastPullClock}."
                };

            _portraitUrls.TryGetValue(
                character,
                out string? portraitUrl);

            var rocks = _tracker.RockTracking.Get(character);
            cards.Add(new FleetCard
            {
                RockVisibility = _tracker.RockTracking.Enabled ? Visibility.Visible : Visibility.Collapsed,
                Rock1Text = "L1 " + rocks[0].Text, Rock2Text = "L2 " + rocks[1].Text,
                Rock1Percent = rocks[0].Percent, Rock2Percent = rocks[1].Percent,
                Rock1Note = rocks[0].Note, Rock2Note = rocks[1].Note,
                Character = character,
                PortraitUrl = portraitUrl ?? "",
                ShipText =
                    string.IsNullOrWhiteSpace(
                        shipIntel?.CurrentShip.TypeName)
                        ? (_pilotIntelRefreshBusy ? "Ship: syncing..." : "Ship: not synced")
                        : "Ship: " +
                          shipIntel.CurrentShip.TypeName,
                ShipToolTip =
                    shipIntel == null
                        ? "Connect this character in Pilot Command Center to sync ship and fitting data."
                        : shipIntel.CurrentShip.DisplayName + (shipIntel.SyncedUtc == default ? "\nFitting data is syncing..." : $"\nLast successful sync: {shipIntel.SyncedUtc.ToLocalTime():dd MMM HH:mm} (cached while refreshing)") +
                          (shipIntel.AssetsAvailable
                              ? $"{Environment.NewLine}Asset/fitting access available."
                              : shipIntel.SyncedUtc == default ? "" : $"{Environment.NewLine}Reconnect for asset access to identify fitted mining lasers."),
                Ore = string.IsNullOrWhiteSpace(s.CurrentOre) ? "-" : s.CurrentOre,
                BaseText =
                    $"{Math.Max(0, displayBaseRate).ToString("N1", CultureInfo.CurrentCulture)} m3/s",
                ActualText =
                    $"{Math.Max(0, displayActualRate).ToString("N1", CultureInfo.CurrentCulture)} m3/s",
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
                LaserPrimaryText = laserPrimaryText,
                LaserPrimaryCrystal = laserPrimaryCrystal,
                LaserSecondaryText = laserSecondaryText,
                LaserSecondaryCrystal = laserSecondaryCrystal,
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
                            .ToolTip + $"\nFit snapshot: {shipIntel.SyncedUtc.ToLocalTime():dd MMM yyyy HH:mm}. Last successful data is retained while refreshing.\n" +
                          (
                              fleetShieldExtension > 0 ||
                              fleetShieldHarmonizing > 0
                                  ? $"{Environment.NewLine}{Environment.NewLine}* Manual Orca shield boost is ON. The fleet estimate assumes that burst is active, in range and affecting this pilot.{Environment.NewLine}" +
                                    $"Extension/HP: {fleetShieldExtension:F1}% | Harmonizing/RES: {fleetShieldHarmonizing:F1}%{Environment.NewLine}" +
                                    fleetBoostSource
                                  : ""
                          )
                        : _pilotIntelRefreshBusy ? "Fitting data is syncing; EHP will appear as this pilot finishes loading." : "Fit EHP unavailable. Check the pilot link and asset access.",
                IsDroneMining = isOrca,
                BoostMode = orcaBoostMode,
                BoostButtonText =
                    orcaBoostMode switch
                    {
                        "HARM" => "RES",
                        "EXT" => "HP",
                        "BOTH" => "BOTH",
                        _ => "BOOST -"
                    },
                BoostToolTip =
                    isOrca
                        ? "Manual live shield-command state.\n" +
                          "Click cycles: OFF -> RES -> HP -> BOTH -> OFF.\n\n" +
                          "RES = Shield Harmonizing resistance boost.\n" +
                          "HP = Shield Extension capacity boost; uses fitted burst and pilot skills when resolved, otherwise assumes 19.7%.\n" +
                          "This manual state is used for fleet EHP because ESI cannot confirm whether a burst is actually running/in range."
                        : "",
                OreToolTip =
                    $"Current ore: {(string.IsNullOrWhiteSpace(s.CurrentOre) ? "-" : s.CurrentOre)}{Environment.NewLine}" +
                    $"Last mining pull: {lastPullAge} ago at {lastPullClock}.",
                BaseToolTip =
                    $"BASE = normal logged mining yield. Separate critical bonus lines add zero to BASE.{Environment.NewLine}" +
                    $"Current BASE: {displayBaseRate:F1} m3/s | measured span: {s.MiningRateSeconds:F1}s{Environment.NewLine}" +
                    "Average: completed intervals around the latest 90 seconds (up to 2 minutes of retained logs). Nearby pulls are grouped; the first boundary yield is excluded. This includes logged mining drones and is not a fitted-module simulation.\n" +
                    (isOrca && s.BaseM3PerSec <= 0 && droneAverage.Ready
                        ? $"Using longer observed drone-mining average ({droneAverage.SampleCount} pulls).{Environment.NewLine}"
                        : "") +
                    $"Last mining pull: {lastPullAge} ago at {lastPullClock}.",
                RealToolTip =
                    $"REAL = observed log yield including critical pulls.{Environment.NewLine}" +
                    $"Current REAL: {displayActualRate:F1} m3/s{Environment.NewLine}" +
                    "Same completed-interval window as BASE; critical yield is included in the numerator. Partial cycles and changed fits can affect the average.\n" +
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

        var savedOrder =
            (_prefs.FleetTileOrder ?? new List<string>())
                .Where(
                    name =>
                        !string.IsNullOrWhiteSpace(name))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        var savedIndex =
            savedOrder
                .Select(
                    (name, index) =>
                        new
                        {
                            Name = name,
                            Index = index
                        })
                .ToDictionary(
                    item => item.Name,
                    item => item.Index,
                    StringComparer.OrdinalIgnoreCase);

        bool hasCustomOrder =
            savedIndex.Count > 0;

        var ordered =
            cards
                .OrderBy(
                    card =>
                        savedIndex.ContainsKey(
                            card.Character)
                            ? 0
                            : 1)
                .ThenBy(
                    card =>
                        savedIndex.TryGetValue(
                            card.Character,
                            out int index)
                            ? index
                            : int.MaxValue)
                .ThenBy(
                    card =>
                        !hasCustomOrder &&
                        card.IsDroneMining
                            ? 0
                            : 1)
                .ThenBy(
                    card => card.Character,
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        int minerCount = ordered.Count;

        double cardWidth = 216;
        const double cardGap = 6;
        const double windowChrome = 44;

        foreach (var card in ordered) {
            card.CardWidth = cardWidth;
            card.CardMinHeight = 174 + (_tracker.RockTracking.Enabled ? 85 : 0);
            card.CanSwitch = _prefs.CombinedCharacterOverview;
            bool compact=OverviewMode=="CHARACTERS";
            card.MiningVisibility=OverviewMode=="MINING"?Visibility.Visible:Visibility.Collapsed;
            card.CombatVisibility=OverviewMode is "PVE" or "PVP"?Visibility.Visible:Visibility.Collapsed;
            card.PreviewVisibility=compact?Visibility.Visible:Visibility.Collapsed;
            if(OverviewMode!="MINING")card.RockVisibility=Visibility.Collapsed;
            var client=clients.FirstOrDefault(c=>string.Equals(c.CharacterName,card.Character,StringComparison.OrdinalIgnoreCase));
            card.SourceHwnd=client?.Hwnd??IntPtr.Zero;
            card.LivePreview=compact&&_prefs.CharacterOverviewLivePreview;
            card.IsActive=_prefs.CombinedCharacterOverview&&client!=null&&client.Hwnd==EveCommandCenter.Interop.User32.GetForegroundWindow();
            if(compact&&client!=null) {card.Preview=PreviewImage(client.Hwnd);card.PreviewNote=EveCommandCenter.Interop.User32.IsIconic(client.Hwnd)?"Minimized - last snapshot":card.LivePreview?"LIVE - click to switch":card.Preview==null?"Waiting for snapshot":"Snapshot - click to switch";}
        }
        foreach(var hwnd in _previewImages.Keys.Where(h=>!clients.Any(c=>c.Hwnd==h)).ToArray())_previewImages.Remove(hwnd);

        ApplyResizeMode();

        DayText.Text = $"DAY {_tracker.GetMiningDayLabel()}";
        UpdatedText.Text =
            _tileReorderMode
                ? (_prefs.CombinedCharacterOverview?"Drag tiles ? Tools ? Reorder characters to finish":"Drag tiles to reorder | click DONE when finished")
                : $"{cards.Count} {(_prefs.CombinedCharacterOverview?"clients":"miners")} | {DateTime.Now:HH:mm:ss}";

        OverviewHeader.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        MinWidth = Math.Max(620, OverviewHeader.DesiredSize.Width + 24);
        if (!_prefs.AllowFleetOverviewResize)
        {
            double desiredWidth = minerCount > 0
                ? windowChrome +
                  minerCount * (cardWidth + cardGap)
                : 620;

            desiredWidth = Math.Max(MinWidth, desiredWidth);
            if(_prefs.CombinedCharacterOverview)desiredWidth=Math.Min(desiredWidth,Math.Max(MinWidth,SystemParameters.WorkArea.Width));

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

        // Preserve the visual tree (tooltips, hover and native thumbnail handles) across ticks.
        if (MinerItems.ItemsSource is List<FleetCard> existing &&
            existing.Select(c=>c.Character).SequenceEqual(ordered.Select(c=>c.Character)))
            for (int i=0;i<existing.Count;i++) existing[i].UpdateFrom(ordered[i]);
        else MinerItems.ItemsSource = ordered;


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
                    _prefs.CombinedCharacterOverview?System.Windows.Controls.ScrollBarVisibility.Auto:System.Windows.Controls.ScrollBarVisibility.Disabled;
            }
        }
    }

    private System.Windows.Media.ImageSource? PreviewImage(IntPtr hwnd)
    {
        var frame=RunningApp?.OverviewThumbnails?.OverviewFrame(hwnd);
        if(frame==null)return null;
        if(_previewImages.TryGetValue(hwnd,out var cached)&&ReferenceEquals(cached.frame,frame))return cached.image;
        using var bitmap=frame.Copy();if(bitmap==null)return cached.image;
        using var stream=new System.IO.MemoryStream();bitmap.Save(stream,System.Drawing.Imaging.ImageFormat.Bmp);stream.Position=0;
        var image=new System.Windows.Media.Imaging.BitmapImage();image.BeginInit();image.CacheOption=System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;image.StreamSource=stream;image.DecodePixelWidth=360;image.EndInit();image.Freeze();
        _previewImages[hwnd]=(frame,image);return image;
    }
    private void ApplyCombinedMode()
    {
        bool combined=_prefs.CombinedCharacterOverview;
        RunningApp?.OverviewThumbnails?.SetOverviewCombined(combined && IsVisible && WindowState!=WindowState.Minimized,OverviewMode=="CHARACTERS"&&!_prefs.CharacterOverviewLivePreview);
        FullActions.Visibility=combined?Visibility.Collapsed:Visibility.Visible;
        CompactActions.Visibility=combined?Visibility.Visible:Visibility.Collapsed;
        LiveBadge.Visibility=DayText.Visibility=PlexMarketBorder.Visibility=combined?Visibility.Collapsed:Visibility.Visible;
        LivePreviewButton.Content=_prefs.CharacterOverviewLivePreview?"PREVIEW: LIVE":"PREVIEW: SNAPSHOT";
        ModeButton.Content="MODE: "+OverviewMode;
    }
    private void Combine_Click(object sender,RoutedEventArgs e)
    {
        _prefs.CombinedCharacterOverview=!_prefs.CombinedCharacterOverview;
        ApplyCombinedMode();MiningDashboardPreferencesStore.Save(_prefs);RefreshCards();
    }
    private void Mode_Click(object sender,RoutedEventArgs e)=>Tools_Click(sender,e);
    private void OverviewMode_Click(object sender,RoutedEventArgs e)
    {
        if(sender is not System.Windows.Controls.MenuItem item||item.Tag is not string mode)return;
        _prefs.CharacterOverviewMiningMode=mode=="MINING";
        _prefs.CharacterOverviewCombatMode=mode is "PVE" or "PVP"?mode:"";
        ApplyCombinedMode();MiningDashboardPreferencesStore.Save(_prefs);RefreshCards();
    }
    private void LivePreview_Click(object sender,RoutedEventArgs e)
    {
        _prefs.CharacterOverviewLivePreview=!_prefs.CharacterOverviewLivePreview;
        ApplyCombinedMode();MiningDashboardPreferencesStore.Save(_prefs);RefreshCards();
    }
    private void Tools_Click(object sender,RoutedEventArgs e)
    {
        if(sender is System.Windows.Controls.Button b&&b.ContextMenu is {} menu){menu.PlacementTarget=b;menu.IsOpen=true;}
    }
    private void Tile_MouseLeftButtonUp(object sender,MouseButtonEventArgs e)
    {
        if(!_prefs.CombinedCharacterOverview||_tileReorderMode||sender is not FrameworkElement element||element.DataContext is not FleetCard card||FindVisualParent<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject)!=null)return;
        RunningApp?.OverviewThumbnails?.ActivateEveWindow(IntPtr.Zero,card.Character);e.Handled=true;
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

    private void OrderMode_Click(
        object sender,
        RoutedEventArgs e)
    {
        _tileReorderMode =
            !_tileReorderMode;

        _tileDragCharacter = null;

        OrderButton.Content =
            _tileReorderMode
                ? "DONE"
                : "ORDER";

        OrderButton.ToolTip =
            _tileReorderMode
                ? "Drag miner tiles left/right into the order you want, then click DONE."
                : "Click to enable tile reordering. Right-click to reset the custom order.";

        RefreshCards();
    }

    private void OrderButton_MouseRightButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        _prefs.FleetTileOrder.Clear();

        MiningDashboardPreferencesStore.Save(
            _prefs);

        _tileDragCharacter = null;

        RefreshCards();

        e.Handled = true;
    }

    private void Tile_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_tileReorderMode ||
            sender is not FrameworkElement element ||
            element.DataContext is not FleetCard card)
        {
            return;
        }

        // Do not hijack buttons inside a card.
        if (FindVisualParent<System.Windows.Controls.Button>(
                e.OriginalSource as DependencyObject) != null)
        {
            _tileDragCharacter = null;
            return;
        }

        _tileDragStart =
            e.GetPosition(this);

        _tileDragCharacter =
            card.Character;
    }

    private void Tile_PreviewMouseMove(
        object sender,
        System.Windows.Input.MouseEventArgs e)
    {
        if (!_tileReorderMode ||
            e.LeftButton != MouseButtonState.Pressed ||
            string.IsNullOrWhiteSpace(
                _tileDragCharacter))
        {
            return;
        }

        System.Windows.Point current =
            e.GetPosition(this);

        if (Math.Abs(
                current.X -
                _tileDragStart.X) <
                SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(
                current.Y -
                _tileDragStart.Y) <
                SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        string character =
            _tileDragCharacter;

        _tileDragCharacter = null;

        var data =
            new System.Windows.DataObject(
                FleetTileDragFormat,
                character);

        System.Windows.DragDrop.DoDragDrop(
            sender as DependencyObject ??
            this,
            data,
            System.Windows.DragDropEffects.Move);
    }

    private void Tile_DragOver(
        object sender,
        System.Windows.DragEventArgs e)
    {
        e.Effects =
            _tileReorderMode &&
            e.Data.GetDataPresent(
                FleetTileDragFormat)
                ? System.Windows.DragDropEffects.Move
                : System.Windows.DragDropEffects.None;

        e.Handled = true;
    }

    private void Tile_Drop(
        object sender,
        System.Windows.DragEventArgs e)
    {
        if (!_tileReorderMode ||
            !e.Data.GetDataPresent(
                FleetTileDragFormat) ||
            sender is not FrameworkElement targetElement ||
            targetElement.DataContext is not FleetCard targetCard)
        {
            return;
        }

        string? sourceCharacter =
            e.Data.GetData(
                FleetTileDragFormat)
                as string;

        if (string.IsNullOrWhiteSpace(
                sourceCharacter) ||
            string.Equals(
                sourceCharacter,
                targetCard.Character,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        bool placeAfter =
            e.GetPosition(
                targetElement).X >
            targetElement.ActualWidth /
            2.0;

        PersistFleetTileMove(
            sourceCharacter,
            targetCard.Character,
            placeAfter);

        e.Handled = true;
    }

    private void PersistFleetTileMove(
        string sourceCharacter,
        string targetCharacter,
        bool placeAfter)
    {
        if (MinerItems.ItemsSource is not
            IEnumerable<FleetCard> visibleCards)
        {
            return;
        }

        var visible =
            visibleCards
                .Select(
                    card => card.Character)
                .Where(
                    name =>
                        !string.IsNullOrWhiteSpace(name))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (!visible.Contains(
                sourceCharacter,
                StringComparer.OrdinalIgnoreCase) ||
            !visible.Contains(
                targetCharacter,
                StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        visible.RemoveAll(
            name =>
                string.Equals(
                    name,
                    sourceCharacter,
                    StringComparison.OrdinalIgnoreCase));

        int targetIndex =
            visible.FindIndex(
                name =>
                    string.Equals(
                        name,
                        targetCharacter,
                        StringComparison.OrdinalIgnoreCase));

        if (targetIndex < 0)
            return;

        int insertIndex =
            placeAfter
                ? targetIndex + 1
                : targetIndex;

        visible.Insert(
            Math.Clamp(
                insertIndex,
                0,
                visible.Count),
            sourceCharacter);

        // Preserve saved pilots that are temporarily absent, after the visible
        // fleet's newly-arranged order.
        var absentSaved =
            (_prefs.FleetTileOrder ?? new List<string>())
                .Where(
                    saved =>
                        !visible.Contains(
                            saved,
                            StringComparer.OrdinalIgnoreCase))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToList();

        _prefs.FleetTileOrder =
            visible
                .Concat(absentSaved)
                .ToList();

        MiningDashboardPreferencesStore.Save(
            _prefs);

        RefreshCards();
    }

    private static T? FindVisualParent<T>(
        DependencyObject? child)
        where T : DependencyObject
    {
        DependencyObject? current =
            child;

        while (current != null)
        {
            if (current is T match)
                return match;

            current =
                System.Windows.Media.VisualTreeHelper
                    .GetParent(current);
        }

        return null;
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
        if (System.Windows.Application.Current is EveCommandCenter.App app)
            app.ShowMiningCommandCenter();
    }

    private void OpenPilotCommandCenter_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is EveCommandCenter.App app)
            app.ShowPilotCommandCenter();
    }

    private void UpdateAccess()
    {
        var access = BackgroundOperations.Current.Access;
        MoonsButton.Visibility = access.CanReadMoons ? Visibility.Visible : Visibility.Collapsed;
        ContractsButton.Visibility = access.CanReadContracts ? Visibility.Visible : Visibility.Collapsed;
        OverviewHeader.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        MinWidth = Math.Max(620, OverviewHeader.DesiredSize.Width + 24);
        if (Width < MinWidth) Width = MinWidth;
    }

    private void OpenPreviewSettings_Click(object sender, RoutedEventArgs e) => (System.Windows.Application.Current as App)?.ShowGeneralSettings();

    private void OpenClientSettings_Click(object sender, RoutedEventArgs e) => new ClientSetupWindow().ShowDialog();

    private void OpenMoonReport_Click(object sender, RoutedEventArgs e) => BackgroundOperations.Current.OpenMoons();
    private void OpenOmega_Click(object sender, RoutedEventArgs e) => BackgroundOperations.Current.OpenOmega();
    private void OpenIndustry_Click(object sender, RoutedEventArgs e) => BackgroundOperations.Current.OpenIndustry();
    private void OpenPlanetary_Click(object sender, RoutedEventArgs e) => BackgroundOperations.Current.OpenPlanetary();
    private void OpenContracts_Click(object sender, RoutedEventArgs e) => BackgroundOperations.Current.OpenContracts();

    private void OpenCloudBackup_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_cloudBackupWindow != null)
        {
            if (_cloudBackupWindow.WindowState == WindowState.Minimized)
                _cloudBackupWindow.WindowState = WindowState.Normal;
            _cloudBackupWindow.Activate();
            return;
        }

        _cloudBackupWindow = new CloudBackupWindow { Owner = this };
        _cloudBackupWindow.Closed += (_, _) => _cloudBackupWindow = null;
        _cloudBackupWindow.Show();
        _cloudBackupWindow.Activate();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private sealed class FleetCard : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private static readonly System.Reflection.PropertyInfo[] Fields=typeof(FleetCard).GetProperties();
        public void UpdateFrom(FleetCard next) {
            foreach(var field in Fields) {
                var value=field.GetValue(next);
                if(Equals(field.GetValue(this),value))continue;
                field.SetValue(this,value);
                PropertyChanged?.Invoke(this,new System.ComponentModel.PropertyChangedEventArgs(field.Name));
            }
        }
        public IntPtr SourceHwnd {get;set;}
        public Visibility CombatVisibility {get;set;}=Visibility.Collapsed;
        public string CombatOut {get;set;}="";
        public string CombatIn {get;set;}="";
        public string CombatSummary {get;set;}="";
        public string CombatDetail {get;set;}="";
        public string CombatNotice {get;set;}="";
        public string CombatHint {get;set;}="";
        public string CombatToolTip {get;set;}="";
        public bool LivePreview {get;set;}
        public bool CanSwitch {get;set;}
        public double CardMinHeight {get;set;}=174;
        public bool IsActive {get;set;}
        public Visibility MiningVisibility { get; set; } = Visibility.Visible;
        public Visibility PreviewVisibility { get; set; } = Visibility.Collapsed;
        public System.Windows.Media.ImageSource? Preview { get; set; }
        public string PreviewNote { get; set; }="";
        public Visibility RockVisibility { get; set; }
        public string Rock1Text { get; set; } = "";
        public string Rock2Text { get; set; } = "";
        public string Rock1Note { get; set; } = "";
        public string Rock2Note { get; set; } = "";
        public double Rock1Percent { get; set; }
        public double Rock2Percent { get; set; }
        public double CardWidth { get; set; } = 170;
        public string Character { get; set; } = "";
        public string PortraitUrl { get; set; } = "";
        public string ShipText { get; set; } = "";
        public string ShipToolTip { get; set; } = "";
        public string Ore { get; set; } = "";
        public string BaseText { get; set; } = "";
        public string ActualText { get; set; } = "";
        public string CritText { get; set; } = "";
        public string ValueText { get; set; } = "";
        public string BuybackText { get; set; } = "";
        public bool AlarmMuted { get; set; }
        public bool AlarmEnabled { get; set; } = true;
        public string AlarmButtonText { get; set; } = "";
        public string AlarmToolTip { get; set; } = "";
        public string Status { get; set; } = "";
        public string StatusText { get; set; } = "";
        public string StatusToolTip { get; set; } = "";
        public string LaserText { get; set; } = "";
        public string LaserPrimaryText { get; set; } = "";
        public string LaserPrimaryCrystal { get; set; } = "";
        public string LaserSecondaryText { get; set; } = "";
        public string LaserSecondaryCrystal { get; set; } = "";
        public string LaserToolTip { get; set; } = "";
        public string EhpText { get; set; } = "";
        public string EhpToolTip { get; set; } = "";
        public bool IsDroneMining { get; set; }
        public string BoostMode { get; set; } = "OFF";
        public string BoostButtonText { get; set; } = "";
        public string BoostToolTip { get; set; } = "";
        public string OreToolTip { get; set; } = "";
        public string BaseToolTip { get; set; } = "";
        public string RealToolTip { get; set; } = "";
        public string ProfitToolTip { get; set; } = "";
        public string BuybackToolTip { get; set; } = "";
        public string CritToolTip { get; set; } = "";
    }
}
