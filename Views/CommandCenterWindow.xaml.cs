using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class CommandCenterWindow : Window
{
    private readonly DispatcherTimer _clockTimer =
        new()
        {
            Interval = TimeSpan.FromSeconds(1)
        };

    private readonly DispatcherTimer _dataTimer =
        new()
        {
            Interval = TimeSpan.FromSeconds(5)
        };

    private readonly bool _live;
    private bool _entrancePlayed;
    private readonly EmbeddedModuleHost _moduleHost;
    private readonly List<WorkspaceTabState> _openTabs =
        new();
    private string _activeWorkspace =
        "dashboard";

    public CommandCenterWindow(bool live = true)
    {
        _live = live;
        InitializeComponent();

        _moduleHost =
            new EmbeddedModuleHost(
                this,
                ModuleSurface);

        _moduleHost.ModuleClosed +=
            key =>
            {
                _openTabs.RemoveAll(tab =>
                    string.Equals(
                        tab.Key,
                        key,
                        StringComparison.OrdinalIgnoreCase));

                if (string.Equals(
                        _activeWorkspace,
                        key,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ShowDashboardWorkspace();
                }
                else
                {
                    RefreshWorkspaceTabs();
                }
            };

        _openTabs.Add(
            WorkspaceTabState.For(
                "dashboard",
                "DASHBOARD",
                "\uE80F"));

        RefreshWorkspaceTabs();

        _clockTimer.Tick += (_, _) =>
            UpdateClock();

        _dataTimer.Tick += (_, _) =>
            RefreshDashboard();

        Loaded += (_, _) =>
        {
            UpdateClock();
            PlayEntranceAnimation();

            if (!_live)
                return;

            RefreshDashboard();
            _clockTimer.Start();
            _dataTimer.Start();
        };

        Activated += (_, _) =>
        {
            if (_entrancePlayed)
                PlayReturnAnimation();
        };

        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            _dataTimer.Stop();
            _moduleHost.Dispose();
        };
    }

    private static string VersionText
    {
        get
        {
            Version? version =
                Assembly
                    .GetExecutingAssembly()
                    .GetName()
                    .Version;

            return version == null
                ? "v?"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private void PlayEntranceAnimation()
    {
        if (_entrancePlayed)
            return;

        _entrancePlayed =
            true;

        var ease =
            new CubicEase
            {
                EasingMode =
                    EasingMode.EaseOut
            };

        RootShell.Opacity =
            0;

        RootShell.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                0,
                1,
                TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = ease
            });

        DashboardTransform.X =
            26;

        DashboardTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(
                26,
                0,
                TimeSpan.FromMilliseconds(360))
            {
                EasingFunction = ease
            });

        WelcomePanel.Opacity =
            0;

        WelcomePanel.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                0,
                1,
                TimeSpan.FromMilliseconds(430))
            {
                BeginTime =
                    TimeSpan.FromMilliseconds(90),
                EasingFunction = ease
            });
    }

    private void PlayReturnAnimation()
    {
        DashboardTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(
                10,
                0,
                TimeSpan.FromMilliseconds(180))
            {
                EasingFunction =
                    new CubicEase
                    {
                        EasingMode =
                            EasingMode.EaseOut
                    }
            });
    }

    private void PulseNavigation()
    {
        bool moduleVisible =
            ModuleFrame.Visibility ==
            Visibility.Visible;

        TranslateTransform transform =
            moduleVisible
                ? ModuleTransform
                : DashboardTransform;

        UIElement target =
            moduleVisible
                ? ModuleFrame
                : DashboardContent;

        transform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(
                0,
                9,
                TimeSpan.FromMilliseconds(90))
            {
                AutoReverse =
                    true,
                EasingFunction =
                    new CubicEase
                    {
                        EasingMode =
                            EasingMode.EaseInOut
                    }
            });

        target.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(
                1,
                0.9,
                TimeSpan.FromMilliseconds(90))
            {
                AutoReverse =
                    true
            });
    }

    private void UpdateClock()
    {
        VersionClockText.Text =
            VersionText +
            " | EVE " +
            DateTime.UtcNow.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture) +
            " | LOCAL " +
            DateTime.Now.ToString(
                "HH:mm:ss",
                CultureInfo.InvariantCulture);

        if (System.Windows.Application.Current is App app)
        {
            int clients =
                app.OverviewClients.Length;

            ClientStatusText.Text =
                clients == 1
                    ? "1 EVE client detected"
                    : $"{clients:N0} EVE clients detected";

            WelcomeStatusText.Text =
                clients == 0
                    ? "Command Center online. Standing by for EVE clients."
                    : clients == 1
                        ? "Command Center online. One live EVE client is linked."
                        : $"Command Center online. {clients:N0} live EVE clients are linked.";

            WelcomeTelemetryText.Text =
                DateTime.UtcNow.ToString(
                    "dd MMM yyyy",
                    CultureInfo.InvariantCulture)
                    .ToUpperInvariant() +
                "  //  EVE " +
                DateTime.UtcNow.ToString(
                    "HH:mm",
                    CultureInfo.InvariantCulture) +
                "  //  FLEET TELEMETRY " +
                (clients > 0
                    ? "ACTIVE"
                    : "STANDBY");
        }
    }

    private void RefreshDashboard()
    {
        try
        {
            UpdateClock();

            if (System.Windows.Application.Current is not App app)
                return;

            BackgroundOperations ops =
                BackgroundOperations.Current;

            RefreshMining(app);
            RefreshMoons(ops);
            RefreshIndustry(ops);
            RefreshPi(ops);
            RefreshContracts(app, ops);
            RefreshFreshness(ops);
            RefreshAttention(app, ops);
        }
        catch (Exception ex)
        {
            SidebarFreshnessText.Text =
                "Dashboard refresh deferred: " +
                ex.GetType().Name;
        }
    }

    private void RefreshMining(App app)
    {
        StatTrackerService? stats =
            app.OperationsStats;

        if (stats == null)
        {
            MiningDetailText.Text =
                "Mining service is still starting.";

            return;
        }

        DateTime today =
            DateTime.Now.Date;

        int day =
            ((int)today.DayOfWeek + 6) % 7;

        DateTime weekStart =
            today.AddDays(-day);

        DateTime monthStart =
            new(
                today.Year,
                today.Month,
                1);

        double todayM3 =
            MiningVolume(
                stats,
                today,
                today);

        double yesterdayM3 =
            MiningVolume(
                stats,
                today.AddDays(-1),
                today.AddDays(-1));

        double weekM3 =
            MiningVolume(
                stats,
                weekStart,
                today);

        double monthM3 =
            MiningVolume(
                stats,
                monthStart,
                today);

        MiningTodayText.Text =
            FormatM3(todayM3);

        MiningYesterdayText.Text =
            FormatM3(yesterdayM3);

        MiningWeekText.Text =
            FormatM3(weekM3);

        MiningMonthText.Text =
            FormatM3(monthM3);

        int active =
            app.OverviewClients.Length;

        MiningDetailText.Text =
            $"{active:N0} live EVE client(s) | " +
            "historical m3 uses locally recorded mining and available ore-volume quotes";
    }

    private static double MiningVolume(
        StatTrackerService stats,
        DateTime start,
        DateTime end)
    {
        double total =
            0;

        foreach (MiningAggregateRow row in
                 stats.GetMiningHistoryRange(
                     start,
                     end))
        {
            if (string.IsNullOrWhiteSpace(
                    row.Ore))
                continue;

            if (!stats.TryGetMiningQuote(
                    row.Ore,
                    out MiningMarketQuote quote) ||
                !quote.IsAvailable ||
                quote.UnitVolumeM3 <= 0)
                continue;

            total +=
                row.Units *
                quote.UnitVolumeM3;
        }

        return total;
    }

    private void RefreshMoons(
        BackgroundOperations ops)
    {
        MoonReportSnapshot snapshot =
            ops.Moons.GetSnapshot();

        MoonActiveText.Text =
            snapshot.ActiveFieldCount.ToString(
                "N0");

        MoonReadyText.Text =
            snapshot.ReadyCount.ToString(
                "N0");

        MoonScheduledText.Text =
            snapshot.ScheduledCount.ToString(
                "N0");

        int profileIssues =
            snapshot.Cards
                .Where(card =>
                    !card.HasTargetProfile)
                .Select(card =>
                    card.MoonName)
                .Where(name =>
                    !string.IsNullOrWhiteSpace(name))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Count();

        MoonProfileIssueText.Text =
            profileIssues.ToString(
                "N0");

        int profileTotal =
            snapshot.Cards
                .Select(card =>
                    card.MoonName)
                .Where(name =>
                    !string.IsNullOrWhiteSpace(name))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Count();

        int fuelAlerts =
            ops.Moons.OperatingAlerts.Count(alert =>
                alert.Key.StartsWith(
                    "fuel:",
                    StringComparison.OrdinalIgnoreCase));

        MoonHealthText.Text =
            profileIssues == 0
                ? $"{profileTotal:N0} visible moon profiles complete" +
                  (fuelAlerts > 0
                      ? $" | {fuelAlerts:N0} fuel alert(s)"
                      : " | fuel status clear")
                : $"{profileIssues:N0} moon profile(s) need composition" +
                  (fuelAlerts > 0
                      ? $" | {fuelAlerts:N0} fuel alert(s)"
                      : "");

        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        MoonNextItems.ItemsSource =
            snapshot.Cards
                .Where(card =>
                    card.ScheduleUtc.HasValue &&
                    card.ScheduleUtc.Value >
                    now)
                .OrderBy(card =>
                    card.ScheduleUtc)
                .Take(6)
                .Select(card =>
                    new DashboardLine
                    {
                        Primary =
                            card.MoonName,
                        Secondary =
                            card.ScheduleValue,
                        Tone =
                            card.Status == "READY"
                                ? "#FFD166"
                                : "#74D6C9"
                    })
                .ToArray();
    }

    private void RefreshIndustry(
        BackgroundOperations ops)
    {
        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        DashboardPilotLine[] rows =
            ops.Industry.State.Pilots
                .OrderBy(pilot =>
                    pilot.Name,
                    StringComparer.OrdinalIgnoreCase)
                .Select(pilot =>
                {
                    IReadOnlyList<IndustryJobView> jobs =
                        IndustryCatalog.Jobs(
                            pilot,
                            now);

                    int active =
                        jobs.Count(job =>
                            job.Status == "active");

                    int ready =
                        jobs.Count(job =>
                            job.Status.Contains(
                                "READY",
                                StringComparison.OrdinalIgnoreCase) ||
                            job.Status == "ready");

                    return new DashboardPilotLine
                    {
                        Name =
                            pilot.Name,
                        Summary =
                            $"{active:N0} active | {ready:N0} ready",
                        Tone =
                            ready > 0
                                ? "#FFD166"
                                : "#74D6C9"
                    };
                })
                .Take(14)
                .ToArray();

        IndustryItems.ItemsSource =
            rows;

        int totalReady =
            rows.Sum(row =>
                row.ReadyCount);

        IndustrySummaryText.Text =
            rows.Length == 0
                ? "No linked industry snapshots yet."
                : $"{rows.Length:N0} pilot(s) | " +
                  $"{totalReady:N0} job(s) ready";
    }

    private void RefreshPi(
        BackgroundOperations ops)
    {
        try
        {
            PiAnalysis analysis =
                PlanetaryAnalysis.Build(
                    ops.Planetary.State,
                    DateTimeOffset.UtcNow);

            DashboardPilotLine[] rows =
                analysis.Colonies
                    .Where(row =>
                        row.Colony != null)
                    .GroupBy(
                        row =>
                            row.Colony!.Character,
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group =>
                        group.Key,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group =>
                    {
                        int attention =
                            group.Count(row =>
                                row.Color == "#FFD166" ||
                                row.Status.Contains(
                                    "ATTENTION",
                                    StringComparison.OrdinalIgnoreCase));

                        int collect =
                            group.Count(row =>
                                row.Status.Contains(
                                    "COLLECT",
                                    StringComparison.OrdinalIgnoreCase));

                        string summary =
                            $"{group.Count():N0} planets";

                        if (attention > 0)
                            summary +=
                                $" | {attention:N0} attention";

                        if (collect > 0)
                            summary +=
                                $" | {collect:N0} collect";

                        return new DashboardPilotLine
                        {
                            Name =
                                group.Key,
                            Summary =
                                summary,
                            Tone =
                                attention > 0
                                    ? "#FFD166"
                                    : collect > 0
                                        ? "#80BFFF"
                                        : "#74D6C9"
                        };
                    })
                    .Take(14)
                    .ToArray();

            PiItems.ItemsSource =
                rows;

            int attentionTotal =
                analysis.Colonies.Count(row =>
                    row.Color == "#FFD166" ||
                    row.Status.Contains(
                        "ATTENTION",
                        StringComparison.OrdinalIgnoreCase));

            PiSummaryText.Text =
                rows.Length == 0
                    ? "No linked PI colony snapshots yet."
                    : $"{ops.Planetary.State.Colonies.Count:N0} colonies | " +
                      $"{attentionTotal:N0} need attention";
        }
        catch
        {
            PiItems.ItemsSource =
                Array.Empty<DashboardPilotLine>();

            PiSummaryText.Text =
                "PI summary is waiting for a valid saved colony snapshot.";
        }
    }

    private void RefreshContracts(
        App app,
        BackgroundOperations ops)
    {
        ContractState state =
            ops.Contracts.State;

        ContractsOutstandingText.Text =
            state.Rows.Count.ToString(
                "N0");

        DateTime today =
            DateTime.Now.Date;

        int weekday =
            ((int)today.DayOfWeek + 6) % 7;

        DateTime start =
            today.AddDays(-weekday);

        DateTime end =
            start.AddDays(7);

        IReadOnlyList<ContractRow> buybacks =
            state.CorporationId > 0
                ? BuybackReport.Qualifying(
                    state.History,
                    state.CorporationId,
                    start,
                    end)
                : Array.Empty<ContractRow>();

        decimal value =
            buybacks.Sum(row =>
                row.Contract.Price ??
                0);

        ContractsBuybackCountText.Text =
            buybacks.Count.ToString(
                "N0");

        ContractsBuybackValueText.Text =
            FormatIsk(value);

        int groups =
            app.OperationsSettings?
                .Settings
                .OperationsMinerGroups
                .Count ??
            0;

        ContractsGroupText.Text =
            groups.ToString(
                "N0");

        ContractsDetailText.Text =
            string.IsNullOrWhiteSpace(
                state.CorporationName)
                ? "Corporation contract reader is not configured yet."
                : state.CorporationName +
                  " | completed qualifying buybacks are reused from Buyback Report rules";
    }

    private void RefreshFreshness(
        BackgroundOperations ops)
    {
        int activeNotifications =
            NotificationCenterService.Current.Items.Count(item =>
                item.Active);

        int unread =
            NotificationCenterService.Current.Items.Count(item =>
                item.Active &&
                !item.Read);

        NotificationText.Text =
            $"{activeNotifications:N0} active notification(s) | " +
            $"{unread:N0} unread";

        DateTimeOffset? industry =
            ops.Industry.State.Pilots
                .Where(pilot =>
                    pilot.Updated != default)
                .Select(pilot =>
                    (DateTimeOffset?)pilot.Updated)
                .OrderByDescending(value =>
                    value)
                .FirstOrDefault();

        DateTimeOffset? pi =
            ops.Planetary.State.Colonies
                .Where(colony =>
                    colony.Fetched != default)
                .Select(colony =>
                    (DateTimeOffset?)colony.Fetched)
                .OrderByDescending(value =>
                    value)
                .FirstOrDefault();

        DateTimeOffset? moon =
            ops.Moons
                .GetSnapshot()
                .LastRefreshUtc;

        DateTimeOffset? contracts =
            ops.Contracts.State.LastRefreshUtc ==
            default
                ? null
                : ops.Contracts.State.LastRefreshUtc;

        FreshnessDetailText.Text =
            "Mining: live local logs" +
            Environment.NewLine +
            "Moons: " +
            Age(moon) +
            Environment.NewLine +
            "Industry: " +
            Age(industry) +
            Environment.NewLine +
            "PI: " +
            Age(pi) +
            Environment.NewLine +
            "Contracts: " +
            Age(contracts);

        SidebarFreshnessText.Text =
            $"Moons {AgeShort(moon)} | " +
            $"Industry {AgeShort(industry)} | " +
            $"PI {AgeShort(pi)} | " +
            $"Contracts {AgeShort(contracts)}";
    }

    private void RefreshAttention(
        App app,
        BackgroundOperations ops)
    {
        var parts =
            new List<string>();

        MoonReportSnapshot moons =
            ops.Moons.GetSnapshot();

        int profileIssues =
            moons.Cards
                .Where(card =>
                    !card.HasTargetProfile)
                .Select(card =>
                    card.MoonName)
                .Where(name =>
                    !string.IsNullOrWhiteSpace(name))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Count();

        if (profileIssues > 0)
            parts.Add(
                $"{profileIssues:N0} moon profile(s) need composition");

        int readyIndustry =
            ops.Industry.State.Pilots.Sum(pilot =>
                IndustryCatalog
                    .Jobs(
                        pilot,
                        DateTimeOffset.UtcNow)
                    .Count(job =>
                        job.Status.Contains(
                            "READY",
                            StringComparison.OrdinalIgnoreCase) ||
                        job.Status == "ready"));

        if (readyIndustry > 0)
            parts.Add(
                $"{readyIndustry:N0} industry job(s) ready");

        try
        {
            PiAnalysis pi =
                PlanetaryAnalysis.Build(
                    ops.Planetary.State,
                    DateTimeOffset.UtcNow);

            int piAttention =
                pi.Colonies.Count(row =>
                    row.Color == "#FFD166" ||
                    row.Status.Contains(
                        "ATTENTION",
                        StringComparison.OrdinalIgnoreCase));

            if (piAttention > 0)
                parts.Add(
                    $"{piAttention:N0} PI colony issue(s)");
        }
        catch
        {
        }

        int outstanding =
            ops.Contracts.State.Rows.Count;

        if (outstanding > 0)
            parts.Add(
                $"{outstanding:N0} outstanding contract(s)");

        int unread =
            NotificationCenterService.Current.Items.Count(item =>
                item.Active &&
                !item.Read);

        if (unread > 0)
            parts.Add(
                $"{unread:N0} unread notice(s)");

        AttentionText.Text =
            parts.Count == 0
                ? "No saved operational warnings need attention right now."
                : string.Join(
                    "   |   ",
                    parts);
    }

    private static string FormatM3(
        double value)
    {
        if (value >= 1_000_000_000)
            return
                (value / 1_000_000_000)
                    .ToString(
                        "0.##",
                        CultureInfo.CurrentCulture) +
                "b m3";

        if (value >= 1_000_000)
            return
                (value / 1_000_000)
                    .ToString(
                        "0.##",
                        CultureInfo.CurrentCulture) +
                "m m3";

        if (value >= 1_000)
            return
                (value / 1_000)
                    .ToString(
                        "0.##",
                        CultureInfo.CurrentCulture) +
                "k m3";

        return
            value.ToString(
                "N0",
                CultureInfo.CurrentCulture) +
            " m3";
    }

    private static string FormatIsk(
        decimal value)
    {
        if (value >=
            1_000_000_000_000m)
            return
                (value /
                 1_000_000_000_000m)
                    .ToString(
                        "0.##",
                        CultureInfo.CurrentCulture) +
                "t ISK";

        if (value >=
            1_000_000_000m)
            return
                (value /
                 1_000_000_000m)
                    .ToString(
                        "0.##",
                        CultureInfo.CurrentCulture) +
                "b ISK";

        if (value >=
            1_000_000m)
            return
                (value /
                 1_000_000m)
                    .ToString(
                        "0.##",
                        CultureInfo.CurrentCulture) +
                "m ISK";

        return
            value.ToString(
                "N0",
                CultureInfo.CurrentCulture) +
            " ISK";
    }

    private static string Age(
        DateTimeOffset? value)
    {
        if (!value.HasValue ||
            value.Value ==
            default)
            return "no saved refresh";

        TimeSpan age =
            DateTimeOffset.UtcNow -
            value.Value.ToUniversalTime();

        if (age.TotalMinutes < 2)
            return "just now";

        if (age.TotalHours < 1)
            return
                $"{Math.Max(1, (int)age.TotalMinutes):N0}m ago";

        if (age.TotalDays < 1)
            return
                $"{Math.Max(1, (int)age.TotalHours):N0}h ago";

        return
            $"{Math.Max(1, (int)age.TotalDays):N0}d ago";
    }

    private static string AgeShort(
        DateTimeOffset? value) =>
        Age(value)
            .Replace(
                " ago",
                "",
                StringComparison.OrdinalIgnoreCase);

    internal Window? OpenModule(
        string key)
    {
        key =
            key.Trim()
                .ToLowerInvariant();

        if (key == "dashboard")
        {
            ShowDashboardWorkspace();
            return null;
        }

        BackgroundOperations ops =
            BackgroundOperations.Current;

        if (key == "moons" &&
            !ops.Access.CanReadMoons)
        {
            var setup =
                new ClientSetupWindow
                {
                    Owner = this
                };

            setup.ShowDialog();
            return null;
        }

        if (key == "contracts" &&
            !ops.Access.CanReadContracts)
        {
            var setup =
                new ClientSetupWindow
                {
                    Owner = this
                };

            setup.ShowDialog();
            return null;
        }

        if (System.Windows.Application.Current is not
            App app)
            return null;

        (string title, string subtitle, string icon) =
            WorkspaceMetadata(key);

        Window module =
            _moduleHost.Show(
                key,
                () =>
                    app.CreateCommandCenterModule(
                        key));

        if (!_openTabs.Any(tab =>
                string.Equals(
                    tab.Key,
                    key,
                    StringComparison.OrdinalIgnoreCase)))
        {
            _openTabs.Add(
                WorkspaceTabState.For(
                    key,
                    title,
                    icon));
        }

        _activeWorkspace =
            key;

        DashboardScroll.Visibility =
            Visibility.Collapsed;

        ModuleFrame.Visibility =
            Visibility.Visible;

        PageTitleText.Text =
            title;

        PageSubtitleText.Text =
            subtitle;

        RefreshWorkspaceTabs();
        PulseNavigation();

        return module;
    }

    internal void CloseModule(
        string key)
    {
        _moduleHost.Close(key);
    }

    internal void ActivateEmbeddedModule(
        Window module)
    {
        if (_moduleHost.Activate(module))
        {
            if (WindowState ==
                WindowState.Minimized)
            {
                WindowState =
                    WindowState.Maximized;
            }

            Show();
            Activate();
        }
    }

    private void ShowDashboardWorkspace()
    {
        _activeWorkspace =
            "dashboard";

        ModuleFrame.Visibility =
            Visibility.Collapsed;

        DashboardScroll.Visibility =
            Visibility.Visible;

        PageTitleText.Text =
            "COMMAND OVERVIEW";

        PageSubtitleText.Text =
            "Fleet, industry and corporation operations";

        RefreshWorkspaceTabs();
        PulseNavigation();
        RefreshDashboard();
    }

    private void RefreshWorkspaceTabs()
    {
        WorkspaceTabStrip.ItemsSource =
            _openTabs
                .Select(tab =>
                    tab.WithActive(
                        string.Equals(
                            tab.Key,
                            _activeWorkspace,
                            StringComparison.OrdinalIgnoreCase)))
                .ToArray();
    }

    private void WorkspaceTab_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not
            System.Windows.Controls.Button button ||
            button.Tag is not
            string key)
            return;

        OpenModule(key);
    }

    private static (
        string Title,
        string Subtitle,
        string Icon)
        WorkspaceMetadata(
            string key) =>
        key switch
        {
            "mining" =>
                (
                    "MINING",
                    "Live mining analytics, market value and fleet performance",
                    "\uE9D2"
                ),
            "pilots" =>
                (
                    "PILOTS",
                    "Characters, skills, training, wallet and assets",
                    "\uE716"
                ),
            "industry" =>
                (
                    "INDUSTRY",
                    "Jobs, blueprints, materials and production planning",
                    "\uE7B8"
                ),
            "pi" =>
                (
                    "PLANETARY INDUSTRY",
                    "Colonies, extractors, factories, stock and refill planning",
                    "\uE774"
                ),
            "moons" =>
                (
                    "MOON OPERATIONS",
                    "Schedules, active fields, fuel, profiles and moon reporting",
                    "\uE7C3"
                ),
            "contracts" =>
                (
                    "CONTRACTS / ACCOUNTS",
                    "Corporation contracts, buyback reporting and miner accounts",
                    "\uE8C7"
                ),
            "notifications" =>
                (
                    "NOTIFICATIONS",
                    "Current operational alerts and notification history",
                    "\uEA8F"
                ),
            "settings" =>
                (
                    "SETTINGS",
                    "Profiles, previews, controls, alerts and application preferences",
                    "\uE713"
                ),
            _ =>
                (
                    key.ToUpperInvariant(),
                    "Command Center module",
                    "\uE80F"
                )
        };

    private sealed record WorkspaceTabState(
        string Key,
        string Title,
        string Icon,
        string Background,
        string Border,
        string Foreground)
    {
        internal static WorkspaceTabState For(
            string key,
            string title,
            string icon) =>
            new(
                key,
                title,
                icon,
                "#0B2229",
                "#234752",
                "#9CC4C3");

        internal WorkspaceTabState WithActive(
            bool active) =>
            this with
            {
                Background =
                    active
                        ? "#17483F"
                        : "#0B2229",
                Border =
                    active
                        ? "#58D3B4"
                        : "#234752",
                Foreground =
                    active
                        ? "#F5FFFD"
                        : "#9CC4C3"
            };
    }
    private void LaunchOverview_Click(
        object sender,
        RoutedEventArgs e)
    {
        PulseNavigation();

        if (System.Windows.Application.Current is App app)
            app.ShowCharacterOverview();
    }

    private void Dashboard_Click(
        object sender,
        RoutedEventArgs e)
    {
        ShowDashboardWorkspace();
    }

    private void Mining_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenModule("mining");
    }

    private void Pilots_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenModule("pilots");
    }

    private void Industry_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenModule("industry");
    }

    private void Planetary_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenModule("pi");
    }

    private void Moons_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenModule("moons");
    }

    private void Contracts_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenModule("contracts");
    }

    private void Notifications_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenModule("notifications");
    }

    private void Settings_Click(
        object sender,
        RoutedEventArgs e)
    {
        OpenModule("settings");
    }
    private void Refresh_Click(
        object sender,
        RoutedEventArgs e)
    {
        PulseNavigation();

        BackgroundOperations ops =
            BackgroundOperations.Current;

        ops.ScheduleRefresh();
        ops.Industry.Due();
        ops.Planetary.State.NextRefresh =
            default;

        SidebarFreshnessText.Text =
            "Refresh requested. Background services will update using ESI cache and provider limits.";

        RefreshDashboard();
    }

    private sealed class DashboardLine
    {
        public string Primary { get; init; } =
            "";

        public string Secondary { get; init; } =
            "";

        public string Tone { get; init; } =
            "#74D6C9";
    }

    private sealed class DashboardPilotLine
    {
        public string Name { get; init; } =
            "";

        public string Summary { get; init; } =
            "";

        public string Tone { get; init; } =
            "#74D6C9";

        public int ReadyCount
        {
            get
            {
                string[] parts =
                    Summary.Split(
                        '|',
                        StringSplitOptions.TrimEntries);

                foreach (string part in parts)
                {
                    if (!part.Contains(
                            "ready",
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    string digits =
                        new(
                            part
                                .Where(char.IsDigit)
                                .ToArray());

                    if (int.TryParse(
                            digits,
                            out int ready))
                        return ready;
                }

                return 0;
            }
        }
    }
}