using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class CommandCenterWindow : Window
{
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _dataTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly bool _live;

    public CommandCenterWindow(bool live = true)
    {
        _live = live;
        InitializeComponent();
        _clockTimer.Tick += (_, _) => UpdateClock();
        _dataTimer.Tick += (_, _) => RefreshDashboard();
        Loaded += (_, _) =>
        {
            UpdateClock();
            if (!_live) return;
            RefreshDashboard();
            _clockTimer.Start();
            _dataTimer.Start();
        };
        Closed += (_, _) => { _clockTimer.Stop(); _dataTimer.Stop(); };
    }

    private static string VersionText
    {
        get
        {
            Version? version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "v?" : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private void UpdateClock()
    {
        VersionClockText.Text = VersionText + " | EVE " + DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) +
            " | LOCAL " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (System.Windows.Application.Current is App app)
        {
            int clients = app.OverviewClients.Length;
            ClientStatusText.Text = clients == 1 ? "1 EVE client detected" : $"{clients:N0} EVE clients detected";
        }
    }

    private void RefreshDashboard()
    {
        try
        {
            UpdateClock();
            if (System.Windows.Application.Current is not App app) return;
            BackgroundOperations ops = BackgroundOperations.Current;
            RefreshMining(app);
            RefreshMoons(ops);
            RefreshIndustry(ops);
            RefreshPi(ops);
            RefreshContracts(app, ops);
            RefreshFreshness(ops);
            RefreshAttention(ops);
        }
        catch (Exception ex)
        {
            SidebarFreshnessText.Text = "Dashboard refresh deferred: " + ex.GetType().Name;
        }
    }

    private void RefreshMining(App app)
    {
        StatTrackerService? stats = app.OperationsStats;
        if (stats == null) { MiningDetailText.Text = "Mining service is still starting."; return; }
        DateTime today = DateTime.Now.Date;
        int offset = ((int)today.DayOfWeek + 6) % 7;
        DateTime weekStart = today.AddDays(-offset);
        DateTime monthStart = new(today.Year, today.Month, 1);
        MiningTodayText.Text = FormatM3(MiningVolume(stats, today, today));
        MiningYesterdayText.Text = FormatM3(MiningVolume(stats, today.AddDays(-1), today.AddDays(-1)));
        MiningWeekText.Text = FormatM3(MiningVolume(stats, weekStart, today));
        MiningMonthText.Text = FormatM3(MiningVolume(stats, monthStart, today));
        MiningDetailText.Text = $"{app.OverviewClients.Length:N0} live EVE client(s) | historical volume uses locally recorded mining and available ore-volume quotes";
    }

    private static double MiningVolume(StatTrackerService stats, DateTime start, DateTime end)
    {
        double total = 0;
        foreach (MiningAggregateRow row in stats.GetMiningHistoryRange(start, end))
        {
            if (string.IsNullOrWhiteSpace(row.Ore)) continue;
            if (!stats.TryGetMiningQuote(row.Ore, out MiningMarketQuote quote) || !quote.IsAvailable || quote.UnitVolumeM3 <= 0) continue;
            total += row.Units * quote.UnitVolumeM3;
        }
        return total;
    }

    private void RefreshMoons(BackgroundOperations ops)
    {
        MoonReportSnapshot snapshot = ops.Moons.GetSnapshot();
        MoonActiveText.Text = snapshot.ActiveFieldCount.ToString("N0");
        MoonReadyText.Text = snapshot.ReadyCount.ToString("N0");
        MoonScheduledText.Text = snapshot.ScheduledCount.ToString("N0");
        int issues = snapshot.Cards.Where(c => !c.HasTargetProfile).Select(c => c.MoonName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int total = snapshot.Cards.Select(c => c.MoonName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int fuel = ops.Moons.OperatingAlerts.Count(a => a.Key.StartsWith("fuel:", StringComparison.OrdinalIgnoreCase));
        MoonProfileIssueText.Text = issues.ToString("N0");
        MoonHealthText.Text = issues == 0 ? $"{total:N0} visible moon profiles complete | {(fuel == 0 ? "fuel status clear" : fuel + " fuel alert(s)")}" : $"{issues:N0} moon profile(s) need composition | {fuel:N0} fuel alert(s)";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        MoonNextItems.ItemsSource = snapshot.Cards.Where(c => c.ScheduleUtc.HasValue && c.ScheduleUtc.Value > now).OrderBy(c => c.ScheduleUtc).Take(6)
            .Select(c => new DashboardLine { Primary = c.MoonName, Secondary = c.ScheduleValue, Tone = c.Status == "READY" ? "#FFD166" : "#74D6C9" }).ToArray();
    }

    private void RefreshIndustry(BackgroundOperations ops)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DashboardPilotLine[] rows = ops.Industry.State.Pilots.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Select(p =>
        {
            IReadOnlyList<IndustryJobView> jobs = IndustryCatalog.Jobs(p, now);
            int active = jobs.Count(j => j.Status == "active");
            int ready = jobs.Count(j => j.Status.Contains("READY", StringComparison.OrdinalIgnoreCase) || j.Status == "ready");
            return new DashboardPilotLine { Name = p.Name, Summary = $"{active:N0} active | {ready:N0} ready", Tone = ready > 0 ? "#FFD166" : "#74D6C9", Ready = ready };
        }).Take(14).ToArray();
        IndustryItems.ItemsSource = rows;
        IndustrySummaryText.Text = rows.Length == 0 ? "No linked industry snapshots yet." : $"{rows.Length:N0} pilot(s) | {rows.Sum(r => r.Ready):N0} job(s) ready";
    }

    private void RefreshPi(BackgroundOperations ops)
    {
        try
        {
            PiAnalysis analysis = PlanetaryAnalysis.Build(ops.Planetary.State, DateTimeOffset.UtcNow);
            DashboardPilotLine[] rows = analysis.Colonies.Where(r => r.Colony != null).GroupBy(r => r.Colony!.Character, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g =>
                {
                    int attention = g.Count(r => r.Color == "#FFD166" || r.Status.Contains("ATTENTION", StringComparison.OrdinalIgnoreCase));
                    int collect = g.Count(r => r.Status.Contains("COLLECT", StringComparison.OrdinalIgnoreCase));
                    string summary = $"{g.Count():N0} planets" + (attention > 0 ? $" | {attention:N0} attention" : "") + (collect > 0 ? $" | {collect:N0} collect" : "");
                    return new DashboardPilotLine { Name = g.Key, Summary = summary, Tone = attention > 0 ? "#FFD166" : collect > 0 ? "#80BFFF" : "#74D6C9" };
                }).Take(14).ToArray();
            PiItems.ItemsSource = rows;
            int totalAttention = analysis.Colonies.Count(r => r.Color == "#FFD166" || r.Status.Contains("ATTENTION", StringComparison.OrdinalIgnoreCase));
            PiSummaryText.Text = rows.Length == 0 ? "No linked PI colony snapshots yet." : $"{ops.Planetary.State.Colonies.Count:N0} colonies | {totalAttention:N0} need attention";
        }
        catch
        {
            PiItems.ItemsSource = Array.Empty<DashboardPilotLine>();
            PiSummaryText.Text = "PI summary is waiting for a valid saved colony snapshot.";
        }
    }

    private void RefreshContracts(App app, BackgroundOperations ops)
    {
        ContractState state = ops.Contracts.State;
        ContractsOutstandingText.Text = state.Rows.Count.ToString("N0");
        DateTime today = DateTime.Now.Date;
        DateTime start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        DateTime end = start.AddDays(7);
        IReadOnlyList<ContractRow> buybacks = state.CorporationId > 0 ? BuybackReport.Qualifying(state.History, state.CorporationId, start, end) : Array.Empty<ContractRow>();
        ContractsBuybackCountText.Text = buybacks.Count.ToString("N0");
        ContractsBuybackValueText.Text = FormatIsk(buybacks.Sum(r => r.Contract.Price ?? 0));
        ContractsGroupText.Text = (app.OperationsSettings?.Settings.OperationsMinerGroups.Count ?? 0).ToString("N0");
        ContractsDetailText.Text = string.IsNullOrWhiteSpace(state.CorporationName) ? "Corporation contract reader is not configured yet." : state.CorporationName + " | qualifying buybacks reuse Buyback Report rules";
    }

    private void RefreshFreshness(BackgroundOperations ops)
    {
        int active = NotificationCenterService.Current.Items.Count(i => i.Active);
        int unread = NotificationCenterService.Current.Items.Count(i => i.Active && !i.Read);
        NotificationText.Text = $"{active:N0} active notification(s) | {unread:N0} unread";
        DateTimeOffset? industry = ops.Industry.State.Pilots.Where(p => p.Updated != default).Select(p => (DateTimeOffset?)p.Updated).OrderByDescending(v => v).FirstOrDefault();
        DateTimeOffset? pi = ops.Planetary.State.Colonies.Where(c => c.Fetched != default).Select(c => (DateTimeOffset?)c.Fetched).OrderByDescending(v => v).FirstOrDefault();
        DateTimeOffset? moon = ops.Moons.GetSnapshot().LastRefreshUtc;
        DateTimeOffset? contracts = ops.Contracts.State.LastRefreshUtc == default ? null : ops.Contracts.State.LastRefreshUtc;
        FreshnessDetailText.Text = "Mining: live local logs" + Environment.NewLine + "Moons: " + Age(moon) + Environment.NewLine + "Industry: " + Age(industry) + Environment.NewLine + "PI: " + Age(pi) + Environment.NewLine + "Contracts: " + Age(contracts);
        SidebarFreshnessText.Text = $"Moons {AgeShort(moon)} | Industry {AgeShort(industry)} | PI {AgeShort(pi)} | Contracts {AgeShort(contracts)}";
    }

    private void RefreshAttention(BackgroundOperations ops)
    {
        var parts = new List<string>();
        MoonReportSnapshot moons = ops.Moons.GetSnapshot();
        int profiles = moons.Cards.Where(c => !c.HasTargetProfile).Select(c => c.MoonName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (profiles > 0) parts.Add($"{profiles:N0} moon profile(s) need composition");
        int ready = ops.Industry.State.Pilots.Sum(p => IndustryCatalog.Jobs(p, DateTimeOffset.UtcNow).Count(j => j.Status.Contains("READY", StringComparison.OrdinalIgnoreCase) || j.Status == "ready"));
        if (ready > 0) parts.Add($"{ready:N0} industry job(s) ready");
        try
        {
            PiAnalysis pi = PlanetaryAnalysis.Build(ops.Planetary.State, DateTimeOffset.UtcNow);
            int piAttention = pi.Colonies.Count(r => r.Color == "#FFD166" || r.Status.Contains("ATTENTION", StringComparison.OrdinalIgnoreCase));
            if (piAttention > 0) parts.Add($"{piAttention:N0} PI colony issue(s)");
        }
        catch { }
        if (ops.Contracts.State.Rows.Count > 0) parts.Add($"{ops.Contracts.State.Rows.Count:N0} outstanding contract(s)");
        int unread = NotificationCenterService.Current.Items.Count(i => i.Active && !i.Read);
        if (unread > 0) parts.Add($"{unread:N0} unread notice(s)");
        AttentionText.Text = parts.Count == 0 ? "No saved operational warnings need attention right now." : string.Join("   |   ", parts);
    }

    private static string FormatM3(double value)
    {
        if (value >= 1_000_000_000) return (value / 1_000_000_000).ToString("0.##", CultureInfo.CurrentCulture) + "b m3";
        if (value >= 1_000_000) return (value / 1_000_000).ToString("0.##", CultureInfo.CurrentCulture) + "m m3";
        if (value >= 1_000) return (value / 1_000).ToString("0.##", CultureInfo.CurrentCulture) + "k m3";
        return value.ToString("N0", CultureInfo.CurrentCulture) + " m3";
    }

    private static string FormatIsk(decimal value)
    {
        if (value >= 1_000_000_000_000m) return (value / 1_000_000_000_000m).ToString("0.##", CultureInfo.CurrentCulture) + "t ISK";
        if (value >= 1_000_000_000m) return (value / 1_000_000_000m).ToString("0.##", CultureInfo.CurrentCulture) + "b ISK";
        if (value >= 1_000_000m) return (value / 1_000_000m).ToString("0.##", CultureInfo.CurrentCulture) + "m ISK";
        return value.ToString("N0", CultureInfo.CurrentCulture) + " ISK";
    }

    private static string Age(DateTimeOffset? value)
    {
        if (!value.HasValue || value.Value == default) return "no saved refresh";
        TimeSpan age = DateTimeOffset.UtcNow - value.Value.ToUniversalTime();
        if (age.TotalMinutes < 2) return "just now";
        if (age.TotalHours < 1) return $"{Math.Max(1, (int)age.TotalMinutes):N0}m ago";
        if (age.TotalDays < 1) return $"{Math.Max(1, (int)age.TotalHours):N0}h ago";
        return $"{Math.Max(1, (int)age.TotalDays):N0}d ago";
    }

    private static string AgeShort(DateTimeOffset? value) => Age(value).Replace(" ago", "", StringComparison.OrdinalIgnoreCase);

    private void LaunchOverview_Click(object sender, RoutedEventArgs e) { if (System.Windows.Application.Current is App app) app.ShowCharacterOverview(); }
    private void Dashboard_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Maximized; Activate(); RefreshDashboard(); }
    private void Mining_Click(object sender, RoutedEventArgs e) => (System.Windows.Application.Current as App)?.ShowMiningCommandCenter();
    private void Pilots_Click(object sender, RoutedEventArgs e) => (System.Windows.Application.Current as App)?.ShowPilotCommandCenter();
    private void Industry_Click(object sender, RoutedEventArgs e) => BackgroundOperations.Current.OpenIndustry();
    private void Planetary_Click(object sender, RoutedEventArgs e) => BackgroundOperations.Current.OpenPlanetary();
    private void Moons_Click(object sender, RoutedEventArgs e) => BackgroundOperations.Current.OpenMoons();
    private void Contracts_Click(object sender, RoutedEventArgs e) => BackgroundOperations.Current.OpenContracts();
    private void Notifications_Click(object sender, RoutedEventArgs e) => BackgroundOperations.Current.OpenNotifications();
    private void Settings_Click(object sender, RoutedEventArgs e) => (System.Windows.Application.Current as App)?.ShowGeneralSettings();

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        BackgroundOperations ops = BackgroundOperations.Current;
        ops.ScheduleRefresh();
        ops.Industry.Due();
        ops.Planetary.State.NextRefresh = default;
        SidebarFreshnessText.Text = "Refresh requested. Background services will update using ESI cache and provider limits.";
        RefreshDashboard();
    }

    private sealed class DashboardLine
    {
        public string Primary { get; init; } = "";
        public string Secondary { get; init; } = "";
        public string Tone { get; init; } = "#74D6C9";
    }

    private sealed class DashboardPilotLine
    {
        public string Name { get; init; } = "";
        public string Summary { get; init; } = "";
        public string Tone { get; init; } = "#74D6C9";
        public int Ready { get; init; }
    }
}