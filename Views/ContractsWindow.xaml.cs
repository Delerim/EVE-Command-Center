using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class ContractsWindow : Window
{
    private readonly BackgroundOperations _operations = BackgroundOperations.Current;
    private ContractService Service => _operations.Contracts;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<EvePilotProfile> _pilots = Array.Empty<EvePilotProfile>();
    private bool _closed;
    private int _accountLedgerRenderVersion;
    public ContractsWindow()
    {
        InitializeComponent();
        ReportDate.SelectedDate = DateTime.UtcNow.Date;
        AccountDate.SelectedDate = DateTime.UtcNow.Date;
        RateBox.Text = Service.State.BuyPercent.ToString(CultureInfo.InvariantCulture);
        ToleranceBox.Text = Service.State.TolerancePercent.ToString(CultureInfo.InvariantCulture);
        AlertsCheck.IsChecked = Service.State.NotificationsEnabled;
        Service.Changed += Render;
        Loaded += async (_, _) =>
        {
            try { await LoadPilotsAsync(); Render(); }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        };
        Closed += (_, _) => { _closed = true; Service.Changed -= Render; _lifetime.Cancel(); };
    }
    private async Task LoadPilotsAsync(long preferred = 0)
    {
        _pilots = await _operations.Sso.LoadPilotsAsync();
        if (_closed) return;
        PilotCombo.ItemsSource = _pilots.Where(p => p.CharacterId == _operations.Access.State.ContractCharacterId).ToArray();
        PilotCombo.SelectedItem = _pilots.FirstOrDefault(p => p.CharacterId == (preferred > 0 ? preferred : Service.State.CharacterId)) ?? _pilots.FirstOrDefault();
    }
    private void Render()
    {
        if (_closed) return;
        var state = Service.State;
        CountText.Text = state.Rows.Count.ToString("N0");
        CorpText.Text = state.CorporationName;
        ValueText.Text = (state.Rows.Sum(r => r.Contract.Price ?? 0) / 1_000_000m).ToString("N2") + "M ISK";
        PassedText.Text = state.Rows.Count(r => r.Passed).ToString();
        AttentionText.Text = state.Rows.Count(r => !r.Passed).ToString();
        FreshnessText.Text = state.LastRefreshUtc is { } time ? "Updated " + time.ToLocalTime().ToString("dd MMM HH:mm") + (DateTimeOffset.UtcNow - time > TimeSpan.FromMinutes(45) ? " (stale)" : "") : "Not refreshed";
        RefreshButton.IsEnabled = !Service.IsRefreshing;
        if (Service.IsRefreshing) StatusText.Text = "Refreshing corporation contracts and checking appraisals...";
        else if (Service.LastError != null) StatusText.Text = "Refresh failed; showing last successful data. " + Service.LastError;
        else StatusText.Text = state.Rows.Count == 0 ? "No outstanding contracts in the current snapshot. Checks run every 30 minutes, subject to provider cooldown." : "Double-click for contents. Monitoring remains active with this window closed. Checks do not verify item-by-item appraisal contents.";
        ApplyFilter();
        RenderReport();
        _ = RenderAccountLedgerAsync();
    }
    private void ApplyFilter()
    {
        if (ContractsGrid == null || SearchBox == null || FilterCombo == null) return;
        var text = SearchBox.Text.Trim();
        var rows = Service.State.Rows.Where(r => text.Length == 0 || (r.Issuer + " " + r.Location + " " + r.Contract.Title + " " + r.Contract.Id).Contains(text, StringComparison.OrdinalIgnoreCase));
        if (FilterCombo.SelectedIndex == 1) rows = rows.Where(r => r.Passed);
        if (FilterCombo.SelectedIndex == 2) rows = rows.Where(r => !r.Passed);
        ContractsGrid.ItemsSource = rows.ToArray();
        if (HistoryGrid == null) return;
        var history = Service.State.History.Where(r => r.CorporationId == Service.State.CorporationId).ToArray();
        HistorySummary.Text = $"{history.Length:N0} recorded contracts | {history.Count(r => r.Contract.WasAccepted):N0} accepted | {history.Sum(r => r.Contract.Price ?? 0):N0} ISK total recorded price";
        HistoryGrid.ItemsSource = history.Where(r => text.Length == 0 || (r.Issuer + " " + r.Acceptor + " " + r.Contract.Status + " " + r.Contract.Title + " " + r.Contract.Id).Contains(text, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Contract.Accepted ?? r.Contract.Issued).ToArray();
        AcceptorsGrid.ItemsSource = history.Where(r => r.Contract.WasAccepted).GroupBy(r => r.Contract.AcceptorId).Select(g => new { Name = g.First().Acceptor, Count = g.Count(), Value = g.Sum(r => r.Contract.Price ?? 0), Last = g.Max(r => r.Contract.Accepted) }).OrderByDescending(r => r.Count).ToArray();
    }
    private string Period => (ReportPeriod.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Month";
    private void RenderReport()
    {
        if (ReportDate == null || ReportBars == null || ReportTitle == null) return;
        var date = ReportDate.SelectedDate ?? DateTime.UtcNow.Date;
        var buckets = BuybackReport.Build(Service.State.History, Service.State.CorporationId, date, Period);
        ReportBars.ItemsSource = buckets;
        ReportTitle.Text = $"{Period.ToUpperInvariant()} | {BuybackReport.Start(date, Period):dd MMM yyyy} - {BuybackReport.End(BuybackReport.Start(date, Period), Period).AddDays(-1):dd MMM yyyy}";
        ReportSummary.Text = $"{buckets.Sum(b => b.Count):N0} recorded buybacks | {buckets.Sum(b => b.Value):N2} ISK | tallest bar {buckets.Max(b => b.Value):N0} ISK";
    }
    private string AccountPeriodName =>
        (AccountPeriod.SelectedItem as ComboBoxItem)?.Content?.ToString() ??
        "Month";

    private SettingsService? AccountSettingsService =>
        (System.Windows.Application.Current as App)?
            .OperationsSettings;

    private static bool SameName(
        string? left,
        string? right) =>
        string.Equals(
            left?.Trim(),
            right?.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static bool ContainsName(
        IEnumerable<string> names,
        string candidate) =>
        names.Any(name =>
            SameName(
                name,
                candidate));

    private static void RemoveMinerFromManualGroups(
        AppSettings settings,
        string miner)
    {
        foreach (List<string> members in
                 settings.OperationsMinerGroups.Values)
            members.RemoveAll(name =>
                SameName(
                    name,
                    miner));
    }

    private void RefreshAccountEditor(
        AppSettings settings,
        IEnumerable<string>? periodMiners = null,
        string? preferredGroup = null)
    {
        if (AccountMinerCombo == null ||
            AccountGroupCombo == null)
            return;

        string previousMiner =
            AccountMinerCombo.Text.Trim();

        string? previousGroup =
            preferredGroup ??
            AccountGroupCombo.SelectedItem as string;

        string[] candidates =
            _pilots
                .Select(pilot =>
                    pilot.CharacterName)
                .Concat(
                    periodMiners ??
                    Array.Empty<string>())
                .Concat(
                    Service.State.History
                        .Where(row =>
                            BuybackReport.IsQualifyingAcceptedBuyback(
                                row,
                                Service.State.CorporationId))
                        .Select(row =>
                            row.Issuer))
                .Concat(
                    settings.OperationsMinerGroups.Values
                        .SelectMany(members =>
                            members))
                .Concat(
                    settings.OperationsSoloMiners)
                .Where(name =>
                    !string.IsNullOrWhiteSpace(name))
                .Select(name =>
                    name.Trim())
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    name =>
                        name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        AccountMinerCombo.ItemsSource =
            candidates;

        if (previousMiner.Length > 0)
        {
            AccountMinerCombo.Text =
                previousMiner;
        }
        else if (candidates.Length > 0)
        {
            AccountMinerCombo.SelectedItem =
                candidates[0];
        }

        string[] groups =
            settings.OperationsMinerGroups.Keys
                .Where(name =>
                    !string.IsNullOrWhiteSpace(name))
                .OrderBy(
                    name =>
                        name,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        AccountGroupCombo.ItemsSource =
            groups;

        AccountGroupCombo.IsEnabled =
            groups.Length > 0;

        AccountGroupEmptyText.Visibility =
            groups.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        string? groupToSelect =
            groups.FirstOrDefault(group =>
                SameName(
                    group,
                    previousGroup));

        if (groupToSelect == null &&
            groups.Length > 0)
            groupToSelect =
                groups[0];

        AccountGroupCombo.SelectedItem =
            groupToSelect;

        if (groupToSelect != null &&
            string.IsNullOrWhiteSpace(
                AccountGroupNameBox.Text))
            AccountGroupNameBox.Text =
                groupToSelect;

        UpdateMinerAssignmentCue(
            settings,
            AccountMinerCombo.Text.Trim());

        UpdateAccountEditorButtons();
    }

    private string? ManualGroupForMiner(
        AppSettings settings,
        string miner)
    {
        return settings.OperationsMinerGroups
            .FirstOrDefault(pair =>
                pair.Value.Any(member =>
                    SameName(
                        member,
                        miner)))
            .Key;
    }

    private string? AutoAccountForMiner(
        AppSettings settings,
        string miner)
    {
        EvePilotProfile? pilot =
            _pilots.FirstOrDefault(p =>
                SameName(
                    p.CharacterName,
                    miner));

        if (pilot == null)
            return null;

        string id =
            pilot.CharacterId.ToString(
                CultureInfo.InvariantCulture);

        return settings.AccountCharacterMap
            .FirstOrDefault(pair =>
                pair.Value.Any(characterId =>
                    string.Equals(
                        characterId,
                        id,
                        StringComparison.OrdinalIgnoreCase)))
            .Key;
    }

    private void SetSelectionCue(
        string title,
        string state,
        string background,
        string border,
        string foreground)
    {
        if (AccountSelectionText == null ||
            AccountSelectionStateText == null)
            return;

        AccountSelectionText.Text =
            string.IsNullOrWhiteSpace(title)
                ? "None"
                : title;

        AccountSelectionStateText.Text =
            state;

        AccountSelectionStateBorder.Background =
            AccountBrush(background);

        AccountSelectionStateBorder.BorderBrush =
            AccountBrush(border);

        AccountSelectionStateText.Foreground =
            AccountBrush(foreground);
    }

    private static System.Windows.Media.SolidColorBrush AccountBrush(
        string hex)
    {
        var color =
            (System.Windows.Media.Color)
            System.Windows.Media.ColorConverter.ConvertFromString(hex);

        return new System.Windows.Media.SolidColorBrush(color);
    }

    private void UpdateMinerAssignmentCue(
        AppSettings settings,
        string miner)
    {
        if (string.IsNullOrWhiteSpace(miner))
        {
            SetSelectionCue(
                "None",
                "CHOOSE A MINER",
                "#13282B",
                "#315E56",
                "#8FB2B5");

            return;
        }

        string? manual =
            ManualGroupForMiner(
                settings,
                miner);

        if (!string.IsNullOrWhiteSpace(manual))
        {
            SetSelectionCue(
                miner,
                "MANUAL: " + manual,
                "#15382F",
                "#58D3B4",
                "#74D6C9");

            return;
        }

        if (settings.OperationsSoloMiners.Any(name =>
                SameName(
                    name,
                    miner)))
        {
            SetSelectionCue(
                miner,
                "SOLO",
                "#3A3017",
                "#A88938",
                "#FFD166");

            return;
        }

        string? account =
            AutoAccountForMiner(
                settings,
                miner);

        if (!string.IsNullOrWhiteSpace(account))
        {
            string label =
                settings.AccountLabels.TryGetValue(
                    account,
                    out string? friendly) &&
                !string.IsNullOrWhiteSpace(friendly)
                    ? friendly.Trim()
                    : "EVE ACCOUNT " + account;

            SetSelectionCue(
                miner,
                "AUTO: " + label,
                "#152C3A",
                "#4783A8",
                "#80BFFF");

            return;
        }

        SetSelectionCue(
            miner,
            "UNASSIGNED",
            "#251F20",
            "#5A4A4D",
            "#C5A9AF");
    }

    private void UpdateAccountEditorButtons()
    {
        if (AccountCreateGroupButton == null)
            return;

        bool hasMiner =
            !string.IsNullOrWhiteSpace(
                AccountMinerCombo?.Text);

        bool hasGroup =
            AccountGroupCombo?.SelectedItem is string;

        bool hasName =
            !string.IsNullOrWhiteSpace(
                AccountGroupNameBox?.Text);

        AccountCreateGroupButton.IsEnabled =
            hasName;

        AccountRenameGroupButton.IsEnabled =
            hasGroup &&
            hasName;

        AccountDeleteGroupButton.IsEnabled =
            hasGroup;

        AccountLinkMinerButton.IsEnabled =
            hasMiner &&
            hasGroup;

        AccountSoloMinerButton.IsEnabled =
            hasMiner;

        AccountAutoMinerButton.IsEnabled =
            hasMiner;
    }

    private async Task RenderAccountLedgerAsync()
    {
        if (_closed ||
            AccountDate == null ||
            AccountLedgerGrid == null)
            return;

        int version =
            ++_accountLedgerRenderVersion;

        App? app =
            System.Windows.Application.Current as App;

        StatTrackerService? stats =
            app?.OperationsStats;

        SettingsService? settingsService =
            app?.OperationsSettings;

        AppSettings? settings =
            settingsService?.Settings;

        if (stats == null ||
            settings == null)
        {
            AccountReportTitle.Text =
                "Mining/account correlation is unavailable until the main Command Center services are running.";

            AccountLedgerGrid.ItemsSource =
                Array.Empty<OperationsLedgerRow>();

            return;
        }

        DateTime selected =
            AccountDate.SelectedDate ??
            DateTime.UtcNow.Date;

        DateTime start =
            BuybackReport.Start(
                selected,
                AccountPeriodName);

        DateTime end =
            BuybackReport.End(
                start,
                AccountPeriodName);

        IReadOnlyList<MiningAggregateRow> mining =
            stats.GetMiningHistoryRange(
                start.Date,
                end.AddDays(-1).Date);

        string[] ores =
            mining
                .Select(row =>
                    row.Ore)
                .Where(ore =>
                    !string.IsNullOrWhiteSpace(ore))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();

        try
        {
            await Task.WhenAll(
                ores.Select(ore =>
                    stats.EnsureMiningQuoteAsync(ore)));
        }
        catch
        {
            // Partial market availability is fine.
        }

        if (_closed ||
            version != _accountLedgerRenderVersion)
            return;

        decimal UnitPrice(string ore)
        {
            if (!stats.TryGetMiningQuote(
                    ore,
                    out MiningMarketQuote quote) ||
                !quote.IsAvailable)
                return 0m;

            double market =
                stats.GetMarketUnitPrice(
                    quote,
                    settings.MiningCorpBuybackMarket,
                    settings.MiningCorpBuybackPriceMode);

            double rate =
                Math.Clamp(
                    settings.MiningCorpBuybackPercent,
                    0,
                    100) /
                100.0;

            return (decimal)Math.Max(
                0,
                market * rate);
        }

        IReadOnlyList<ContractRow> qualifyingBuybacks =
            BuybackReport.Qualifying(
                Service.State.History,
                Service.State.CorporationId,
                start,
                end);

        OperationsLedgerSummary summary =
            OperationsLedgerService.Build(
                settings,
                _pilots,
                mining,
                qualifyingBuybacks,
                UnitPrice);

        object? selectedKey =
            (AccountLedgerGrid.SelectedItem as OperationsLedgerRow)?
                .GroupKey;

        AccountLedgerGrid.ItemsSource =
            summary.Rows;

        if (selectedKey is string key)
            AccountLedgerGrid.SelectedItem =
                summary.Rows.FirstOrDefault(row =>
                    string.Equals(
                        row.GroupKey,
                        key,
                        StringComparison.OrdinalIgnoreCase));

        AccountMinedText.Text =
            OperationsLedgerRow.FormatIsk(
                summary.MinedBuybackValue);

        AccountContractedText.Text =
            OperationsLedgerRow.FormatIsk(
                summary.ContractedBackValue);

        AccountGapText.Text =
            OperationsLedgerRow.FormatIsk(
                summary.UncontractedValue);

        AccountGapDetailText.Text =
            summary.OverRecordedValue > 0
                ? OperationsLedgerRow.FormatIsk(
                      summary.OverRecordedValue) +
                  " contracted above locally recorded mining"
                : "Tracked mining not yet represented by qualifying buybacks";

        AccountCountText.Text =
            summary.GroupCount.ToString("N0");

        AccountContractCountText.Text =
            summary.BuybackContracts.ToString("N0") +
            " qualifying buybacks";

        int miners =
            mining
                .Select(row =>
                    row.Character)
                .Concat(
                    qualifyingBuybacks.Select(row =>
                        row.Issuer))
                .Where(name =>
                    !string.IsNullOrWhiteSpace(name))
                .Distinct(
                    StringComparer.OrdinalIgnoreCase)
                .Count();

        AccountReportTitle.Text =
            $"{AccountPeriodName.ToUpperInvariant()} | " +
            $"{start:dd MMM yyyy} - " +
            $"{end.AddDays(-1):dd MMM yyyy} | " +
            $"{miners:N0} miners/sellers";

        RefreshAccountEditor(
            settings,
            mining.Select(row =>
                row.Character));
    }

    private void AccountReport_Changed(
        object sender,
        SelectionChangedEventArgs e)
    {
        _ = RenderAccountLedgerAsync();
    }

    private void AccountPrevious_Click(
        object sender,
        RoutedEventArgs e) =>
        MoveAccountReport(-1);

    private void AccountNext_Click(
        object sender,
        RoutedEventArgs e) =>
        MoveAccountReport(1);

    private void AccountToday_Click(
        object sender,
        RoutedEventArgs e) =>
        AccountDate.SelectedDate =
            DateTime.UtcNow.Date;

    private void MoveAccountReport(
        int direction)
    {
        DateTime date =
            AccountDate.SelectedDate ??
            DateTime.UtcNow.Date;

        AccountDate.SelectedDate =
            AccountPeriodName == "Week"
                ? date.AddDays(
                    direction * 7)
                : AccountPeriodName == "Year"
                    ? date.AddYears(
                        direction)
                    : date.AddMonths(
                        direction);
    }

    private void AccountMinerSelection_Changed(
        object sender,
        SelectionChangedEventArgs e)
    {
        SettingsService? service =
            AccountSettingsService;

        if (service == null)
            return;

        UpdateMinerAssignmentCue(
            service.Settings,
            AccountMinerCombo.Text.Trim());

        UpdateAccountEditorButtons();
    }

    private void AccountMinerFocusLost(
        object sender,
        KeyboardFocusChangedEventArgs e)
    {
        SettingsService? service =
            AccountSettingsService;

        if (service == null)
            return;

        UpdateMinerAssignmentCue(
            service.Settings,
            AccountMinerCombo.Text.Trim());

        UpdateAccountEditorButtons();
    }

    private void AccountGroupSelection_Changed(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (AccountGroupCombo?.SelectedItem is string group &&
            AccountGroupNameBox != null)
            AccountGroupNameBox.Text =
                group;

        UpdateAccountEditorButtons();
    }

    private void AccountGroupName_Changed(
        object sender,
        TextChangedEventArgs e)
    {
        UpdateAccountEditorButtons();
    }

    private void AccountLedgerSelection_Changed(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (AccountLedgerGrid?.SelectedItem is not
            OperationsLedgerRow row)
            return;

        SettingsService? service =
            AccountSettingsService;

        if (service == null)
            return;

        if (row.GroupKind == "MANUAL GROUP")
        {
            string? matching =
                settingsGroup(
                    service.Settings,
                    row.Group);

            if (matching != null)
            {
                AccountGroupCombo.SelectedItem =
                    matching;

                AccountGroupNameBox.Text =
                    matching;
            }
        }

        string[] members =
            row.Characters
                .Split(
                    ',',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);

        if (members.Length == 1)
        {
            AccountMinerCombo.Text =
                members[0];

            UpdateMinerAssignmentCue(
                service.Settings,
                members[0]);

            AccountGroupStatus.Text =
                "Selected " +
                members[0] +
                " from the ledger.";
        }
        else
        {
            AccountMinerCombo.Text =
                "";

            SetSelectionCue(
                row.Group,
                row.GroupKind +
                " | " +
                members.Length +
                " MINERS",
                "#15382F",
                "#58D3B4",
                "#74D6C9");

            AccountGroupStatus.Text =
                "Selected " +
                row.Group +
                ". Choose one member above for an individual action.";
        }

        UpdateAccountEditorButtons();
    }

    private static string? settingsGroup(
        AppSettings settings,
        string group)
    {
        return settings.OperationsMinerGroups.Keys
            .FirstOrDefault(name =>
                SameName(
                    name,
                    group));
    }

    private void AccountCreateGroup_Click(
        object sender,
        RoutedEventArgs e)
    {
        SettingsService? service =
            AccountSettingsService;

        if (service == null)
            return;

        string name =
            AccountGroupNameBox.Text.Trim();

        if (name.Length == 0)
        {
            AccountGroupStatus.Text =
                "Type a new group name first.";
            return;
        }

        AppSettings settings =
            service.Settings;

        string? existing =
            settingsGroup(
                settings,
                name);

        if (existing != null)
        {
            AccountGroupCombo.SelectedItem =
                existing;

            AccountGroupStatus.Text =
                "That group already exists and is now selected.";

            return;
        }

        settings.OperationsMinerGroups[name] =
            new List<string>();

        service.Save();

        RefreshAccountEditor(
            settings,
            preferredGroup: name);

        AccountGroupNameBox.Text =
            "";

        AccountGroupStatus.Text =
            "Created " +
            name +
            ". Now choose a miner and click LINK MINER TO GROUP.";

        _ = RenderAccountLedgerAsync();
    }

    private void AccountRenameGroup_Click(
        object sender,
        RoutedEventArgs e)
    {
        SettingsService? service =
            AccountSettingsService;

        if (service == null)
            return;

        if (AccountGroupCombo.SelectedItem is not
            string selected)
        {
            AccountGroupStatus.Text =
                "Select an existing group first.";
            return;
        }

        string name =
            AccountGroupNameBox.Text.Trim();

        if (name.Length == 0)
        {
            AccountGroupStatus.Text =
                "Type the new group name first.";
            return;
        }

        AppSettings settings =
            service.Settings;

        if (SameName(
                selected,
                name))
        {
            AccountGroupStatus.Text =
                "The selected group already has that name.";
            return;
        }

        if (settingsGroup(
                settings,
                name) != null)
        {
            AccountGroupStatus.Text =
                "A group with that name already exists.";
            return;
        }

        List<string> members =
            settings.OperationsMinerGroups[selected];

        settings.OperationsMinerGroups.Remove(
            selected);

        settings.OperationsMinerGroups[name] =
            members;

        service.Save();

        RefreshAccountEditor(
            settings,
            preferredGroup: name);

        AccountGroupNameBox.Text =
            name;

        AccountGroupStatus.Text =
            "Renamed " +
            selected +
            " to " +
            name +
            ".";

        _ = RenderAccountLedgerAsync();
    }

    private void AccountDeleteGroup_Click(
        object sender,
        RoutedEventArgs e)
    {
        SettingsService? service =
            AccountSettingsService;

        if (service == null)
            return;

        if (AccountGroupCombo.SelectedItem is not
            string selected)
        {
            AccountGroupStatus.Text =
                "Select a group first.";
            return;
        }

        AppSettings settings =
            service.Settings;

        int memberCount =
            settings.OperationsMinerGroups.TryGetValue(
                selected,
                out List<string>? members)
                ? members.Count
                : 0;

        string detail =
            memberCount > 0
                ? $" Its {memberCount} miner(s) will fall back to SOLO/AUTO/UNASSIGNED."
                : "";

        if (System.Windows.MessageBox.Show(
                "Remove reporting group '" +
                selected +
                "'?" +
                detail,
                "Remove reporting group",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) !=
            System.Windows.MessageBoxResult.Yes)
            return;

        settings.OperationsMinerGroups.Remove(
            selected);

        service.Save();

        AccountGroupNameBox.Text =
            "";

        RefreshAccountEditor(settings);

        AccountGroupStatus.Text =
            "Removed " +
            selected +
            ".";

        _ = RenderAccountLedgerAsync();
    }

    private void AccountLinkMiner_Click(
        object sender,
        RoutedEventArgs e)
    {
        SettingsService? service =
            AccountSettingsService;

        if (service == null)
            return;

        string miner =
            AccountMinerCombo.Text.Trim();

        if (miner.Length == 0)
        {
            AccountGroupStatus.Text =
                "Choose or type a miner first.";
            return;
        }

        if (AccountGroupCombo.SelectedItem is not
            string group)
        {
            AccountGroupStatus.Text =
                "Create or select a group first.";
            return;
        }

        AppSettings settings =
            service.Settings;

        RemoveMinerFromManualGroups(
            settings,
            miner);

        settings.OperationsSoloMiners.RemoveAll(name =>
            SameName(
                name,
                miner));

        List<string> members =
            settings.OperationsMinerGroups[group];

        if (!ContainsName(
                members,
                miner))
            members.Add(miner);

        service.Save();

        RefreshAccountEditor(
            settings,
            preferredGroup: group);

        AccountMinerCombo.Text =
            miner;

        UpdateMinerAssignmentCue(
            settings,
            miner);

        AccountGroupStatus.Text =
            miner +
            " linked to " +
            group +
            ".";

        _ = RenderAccountLedgerAsync();
    }

    private void AccountSoloMiner_Click(
        object sender,
        RoutedEventArgs e)
    {
        SettingsService? service =
            AccountSettingsService;

        if (service == null)
            return;

        string miner =
            AccountMinerCombo.Text.Trim();

        if (miner.Length == 0)
        {
            AccountGroupStatus.Text =
                "Choose or type a miner first.";
            return;
        }

        AppSettings settings =
            service.Settings;

        RemoveMinerFromManualGroups(
            settings,
            miner);

        if (!ContainsName(
                settings.OperationsSoloMiners,
                miner))
            settings.OperationsSoloMiners.Add(
                miner);

        service.Save();

        AccountMinerCombo.Text =
            miner;

        UpdateMinerAssignmentCue(
            settings,
            miner);

        UpdateAccountEditorButtons();

        AccountGroupStatus.Text =
            miner +
            " is now explicitly SOLO.";

        _ = RenderAccountLedgerAsync();
    }

    private void AccountUnlinkMiner_Click(
        object sender,
        RoutedEventArgs e)
    {
        SettingsService? service =
            AccountSettingsService;

        if (service == null)
            return;

        string miner =
            AccountMinerCombo.Text.Trim();

        if (miner.Length == 0)
        {
            AccountGroupStatus.Text =
                "Choose or type a miner first.";
            return;
        }

        AppSettings settings =
            service.Settings;

        RemoveMinerFromManualGroups(
            settings,
            miner);

        settings.OperationsSoloMiners.RemoveAll(name =>
            SameName(
                name,
                miner));

        service.Save();

        AccountMinerCombo.Text =
            miner;

        UpdateMinerAssignmentCue(
            settings,
            miner);

        UpdateAccountEditorButtons();

        AccountGroupStatus.Text =
            miner +
            " manual override removed; AUTO account grouping is used when known, otherwise UNASSIGNED.";

        _ = RenderAccountLedgerAsync();
    }
    private void Report_Changed(object sender, SelectionChangedEventArgs e) => RenderReport();
    private void ReportPrevious_Click(object sender, RoutedEventArgs e) => MoveReport(-1);
    private void ReportNext_Click(object sender, RoutedEventArgs e) => MoveReport(1);
    private void ReportToday_Click(object sender, RoutedEventArgs e) => ReportDate.SelectedDate = DateTime.UtcNow.Date;
    private void MoveReport(int direction)
    {
        var date = ReportDate.SelectedDate ?? DateTime.UtcNow.Date;
        ReportDate.SelectedDate = Period == "Week" ? date.AddDays(direction * 7) : Period == "Year" ? date.AddYears(direction) : date.AddMonths(direction);
    }
    private void History_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryGrid.SelectedItem is ContractRow row) OpenContents(row);
    }
    private void Search_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (DateTimeOffset.UtcNow < Service.NextCheckUtc) { StatusText.Text = $"Provider cooldown: next check {Service.NextCheckUtc.ToLocalTime():HH:mm}. Showing saved data."; return; }
        if (PilotCombo.SelectedItem is not EvePilotProfile pilot) { StatusText.Text = "Choose a linked character first."; return; }
        if (!ContractService.CanRead(pilot)) { StatusText.Text = "Use RECONNECT / ADD to grant corporation contract access."; return; }
        try
        {
            await _operations.Access.ValidateAsync(_pilots, _lifetime.Token);
            if (!_operations.Access.CanReadContracts || pilot.CharacterId != _operations.Access.State.ContractCharacterId) return;
            if (Service.State.CharacterId != pilot.CharacterId)
            {
                Service.State.CharacterId = pilot.CharacterId;
                Service.State.Rows.Clear();
                Service.State.CorporationName = "";
                Service.State.LastRefreshUtc = null;
                Service.Save();
            }
            // Refresh belongs to the app, so closing this window does not cancel monitoring.
            await Service.RefreshAsync(pilot, CancellationToken.None);
        }
        catch (Exception ex) { if (!_closed) StatusText.Text = ex.Message; }
    }
    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        ReconnectButton.IsEnabled = false;
        try
        {
            new ClientSetupWindow().ShowDialog();
            await LoadPilotsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed) StatusText.Text = ex.Message; }
        finally { if (!_closed) ReconnectButton.IsEnabled = true; }
    }
    private void SaveRules_Click(object sender, RoutedEventArgs e)
    {
        if (!decimal.TryParse(RateBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var rate) || rate <= 0 || rate > 100 ||
            !decimal.TryParse(ToleranceBox.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var tolerance) || tolerance < 0 || tolerance > 100)
        { StatusText.Text = "Enter a buy percentage above 0 and at most 100, and tolerance from 0 to 100."; return; }
        Service.State.BuyPercent = rate; Service.State.TolerancePercent = tolerance;
        try { Service.Save(); StatusText.Text = "Rules saved. Refresh to re-evaluate contracts."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Alerts_Click(object sender, RoutedEventArgs e)
    {
        Service.State.NotificationsEnabled = AlertsCheck.IsChecked == true;
        try { Service.Save(); } catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Janice_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContractRow row) return;
        if (row.JaniceUrl == null) { StatusText.Text = "This contract has no recognized Janice appraisal link."; return; }
        try { Process.Start(new ProcessStartInfo(row.JaniceUrl) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private async void Game_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ContractRow row) return;
        try
        {
            var pilot = (await _operations.Sso.LoadPilotsAsync()).FirstOrDefault(p => p.CharacterId == (row.ReaderCharacterId > 0 ? row.ReaderCharacterId : Service.State.CharacterId))
                ?? throw new InvalidOperationException("Choose and refresh a corporation-data toon first.");
            await Service.OpenInGameAsync(row.Contract.Id, pilot, _lifetime.Token);
            StatusText.Text = "Contract window requested for " + pilot.CharacterName + ". That character must be logged into EVE.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var row = ItemsControl.ContainerFromElement(ContractsGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        if (row?.Item is ContractRow contract) OpenContents(contract);
    }
    public void OpenContents(ContractRow row) => new ContractContentsWindow(Service, _operations.Sso, row, row.ReaderCharacterId > 0 ? row.ReaderCharacterId : Service.State.CharacterId) { Owner = EmbeddedModuleHost.ResolveOwner(this) }.Show();
}
