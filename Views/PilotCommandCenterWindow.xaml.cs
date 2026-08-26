using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfMessageBox = System.Windows.MessageBox;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

public partial class PilotCommandCenterWindow : Window
{
    private readonly EveSsoService _sso = new();
    private readonly EveSkillCatalogService _skillCatalog = new();
    private CancellationTokenSource? _loadCts;
    private bool _loaded;
    private string _skillFilter = "all";
    private List<SkillRowViewModel> _allSkillRows = new();
    private List<QueueRowViewModel> _queueRows = new();
    private EveTrainingProfile _trainingProfile = new();
    private long _inventoryLoadedForCharacterId;
    private EveInventorySnapshot? _currentInventory;

    public ObservableCollection<PilotCardViewModel> Pilots { get; } = new();
    public ObservableCollection<SkillGroupViewModel> SkillGroups { get; } = new();

    public PilotCommandCenterWindow()
    {
        InitializeComponent();
        DataContext = this;

        Loaded += async (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            await LoadPilotsAsync();
        };

        Closed += (_, _) => _loadCts?.Cancel();
    }

    private async Task LoadPilotsAsync()
    {
        SetStatus("Loading connected pilots...");
        var profiles = await _sso.LoadPilotsAsync();

        Pilots.Clear();
        foreach (var profile in profiles)
            Pilots.Add(new PilotCardViewModel(profile));

        if (Pilots.Count == 0)
        {
            ClearDetails();
            SetStatus("No pilots connected yet. Use Add Character.");
            return;
        }

        PilotList.SelectedIndex = 0;

        using var gate = new SemaphoreSlim(3);
        var tasks = Pilots.Select(async card =>
        {
            await gate.WaitAsync();
            try
            {
                var summary =
                    await _sso.GetSummaryAsync(card.Profile);

                await Dispatcher.InvokeAsync(
                    () => card.Apply(summary));
            }
            catch (Exception ex)
            {
                await Dispatcher.InvokeAsync(
                    () => card.TrainingText =
                        "Refresh failed: " + ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        SetStatus($"{Pilots.Count} connected pilot(s)");
    }

    private async void AddCharacter_Click(
        object sender, RoutedEventArgs e)
    {
        try
        {
            IsEnabled = false;
            SetStatus("Opening EVE SSO in your browser...");

            EvePilotProfile profile =
                await _sso.AddCharacterAsync();

            PilotCardViewModel? existing =
                Pilots.FirstOrDefault(
                    p => p.Profile.CharacterId ==
                         profile.CharacterId);

            if (existing == null)
            {
                existing =
                    new PilotCardViewModel(profile);
                Pilots.Add(existing);
            }
            else
            {
                existing.Profile = profile;
            }

            EvePilotSummary summary =
                await _sso.GetSummaryAsync(profile);
            existing.Apply(summary);
            PilotList.SelectedItem = existing;
            SetStatus($"{profile.CharacterName} connected");
        }
        catch (OperationCanceledException)
        {
            SetStatus("EVE SSO timed out or was cancelled");
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(
                ex.Message,
                "EVE Command Center - SSO",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            SetStatus("SSO failed");
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private async void PilotList_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PilotList.SelectedItem is PilotCardViewModel card)
        {
            _inventoryLoadedForCharacterId = 0;

            await LoadSelectedPilotAsync(card);

            if (ShipAssetsTab.IsSelected)
                await LoadInventoryAsync(
                    card,
                    force: true);
        }
    }

    private async void Refresh_Click(
        object sender, RoutedEventArgs e)
    {
        if (PilotList.SelectedItem is PilotCardViewModel card)
            await LoadSelectedPilotAsync(
                card, forceCardRefresh: true);
    }

    private async Task LoadSelectedPilotAsync(
        PilotCardViewModel card,
        bool forceCardRefresh = false)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();

        try
        {
            SetStatus($"Refreshing {card.CharacterName}...");

            EvePilotDashboard data =
                await _sso.GetDashboardAsync(
                    card.Profile, _loadCts.Token);

            if (forceCardRefresh ||
                card.WalletText == "Loading...")
                card.Apply(data.Summary);

            WalletText.Text =
                EveSsoService.FormatIsk(
                    data.Summary.WalletBalance);
            WalletTabBalanceText.Text = WalletText.Text;
            TotalSpText.Text =
                $"{data.Summary.TotalSp:N0} SP";

            CurrentSkillText.Text =
                string.IsNullOrWhiteSpace(
                    data.Summary.CurrentSkillRemaining)
                    ? data.Summary.CurrentSkill
                    : $"{data.Summary.CurrentSkill}  •  " +
                      data.Summary.CurrentSkillRemaining;

            SkillProgress.Value =
                data.Summary.CurrentProgressPercent;
            QueueEndsText.Text =
                data.Summary.QueueEndsIn;

            ApplyWalletData(data);

            _trainingProfile = data.TrainingProfile;

            AttributeItems.ItemsSource =
                data.TrainingProfile.Attributes;

            RemapSummaryText.Text =
                $"Bonus remaps: {data.TrainingProfile.BonusRemaps} | " +
                data.TrainingProfile.StandardRemapText;

            ShowAllImplantsToggle.IsChecked = false;
            RefreshImplantItems();
            card.Apply(data.Summary);

            IReadOnlyList<EveSkillCatalogEntry> catalog =
                await LoadSkillBrowserAsync(
                    data.TrainedSkills,
                    _loadCts.Token);

            BuildQueueRows(
                data.SkillQueue,
                catalog,
                data.TrainingProfile);

            SetStatus(
                $"{card.CharacterName} • updated " +
                DateTime.Now.ToString("HH:mm:ss"));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus("Refresh failed");
            WpfMessageBox.Show(
                ex.Message,
                "EVE Command Center - Pilot Data",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void Disconnect_Click(
        object sender, RoutedEventArgs e)
    {
        if (PilotList.SelectedItem
            is not PilotCardViewModel card)
            return;

        MessageBoxResult answer =
            WpfMessageBox.Show(
                $"Disconnect {card.CharacterName}?\n\n" +
                "The stored EVE refresh token will be " +
                "removed from Windows Credential Manager.",
                "EVE Command Center",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        await _sso.RemoveCharacterAsync(
            card.Profile.CharacterId);
        Pilots.Remove(card);

        if (Pilots.Count > 0)
            PilotList.SelectedIndex = 0;
        else
            ClearDetails();

        SetStatus($"{card.CharacterName} disconnected");
    }

    private void ClearDetails()
    {
        WalletText.Text = "-";
        WalletTabBalanceText.Text = "-";
        TotalSpText.Text = "-";
        CurrentSkillText.Text = "-";
        QueueEndsText.Text = "-";
        SkillProgress.Value = 0;
        SkillQueueGrid.ItemsSource = null;
        WalletJournalGrid.ItemsSource = null;
        WalletTransactionsGrid.ItemsSource = null;
        WalletPlexGrid.ItemsSource = null;
        WalletMarketSummaryText.Text = "";
        WalletJournalCountText.Text = "";
        WalletTransactionCountText.Text = "";
        WalletPlexCountText.Text = "";
        WalletTodayIncomeText.Text = "-";
        WalletTodaySpentText.Text = "-";
        WalletTodayNetText.Text = "-";
        WalletWeekIncomeText.Text = "-";
        WalletWeekSpentText.Text = "-";
        WalletWeekNetText.Text = "-";
        PlexBoughtText.Text = "-";
        PlexSoldText.Text = "-";
        PlexAverageBuyText.Text = "-";
        PlexAverageSellText.Text = "-";
        PlexNetText.Text = "-";
        AttributeItems.ItemsSource = null;
        ImplantItems.ItemsSource = null;
        RemapSummaryText.Text = "-";
        ImplantAccessText.Text = "";
        ShowAllImplantsToggle.IsChecked = false;
        _trainingProfile = new();
        _queueRows.Clear();

        _inventoryLoadedForCharacterId = 0;
        _currentInventory = null;
        CurrentShipDetailText.Text = "-";
        CurrentShipItemText.Text = "";
        ShipAssetsStatusText.Text =
            "Open this tab to sync ship and asset data.";
        CurrentEquipmentCountText.Text = "";
        FittingCountText.Text = "";
        AssetCountText.Text = "";
        CurrentShipModulesGrid.ItemsSource = null;
        SavedFittingsGrid.ItemsSource = null;
        PersonalAssetsGrid.ItemsSource = null;

        _allSkillRows.Clear();
        SkillGroups.Clear();
        SkillCategoryFilter.ItemsSource = null;
        SkillSearchBox.Text = "";
        SkillTotalsText.Text = "-";
        SkillsCatalogStatusText.Text = "";
    }

    private void ApplyWalletData(
        EvePilotDashboard data)
    {
        EveWalletOverview overview =
            data.WalletOverview;

        WalletJournalGrid.ItemsSource =
            data.WalletJournal;
        WalletTransactionsGrid.ItemsSource =
            data.WalletTransactions;
        WalletPlexGrid.ItemsSource =
            data.PlexTransactions;

        WalletJournalCountText.Text =
            $"{overview.JournalCount:N0} recent journal entries";

        WalletTransactionCountText.Text =
            $"{overview.TransactionCount:N0} recent market transactions";

        WalletPlexCountText.Text =
            overview.PlexTransactionCount > 0
                ? $"{overview.PlexTransactionCount:N0} PLEX market transactions"
                : "No PLEX market transactions in the current ESI transaction history";

        WalletTodayIncomeText.Text =
            EveSsoService.FormatIsk(
                overview.TodayIncome);

        WalletTodaySpentText.Text =
            EveSsoService.FormatIsk(
                overview.TodaySpent);

        SetSignedMoney(
            WalletTodayNetText,
            overview.TodayNet);

        WalletWeekIncomeText.Text =
            EveSsoService.FormatIsk(
                overview.WeekIncome);

        WalletWeekSpentText.Text =
            EveSsoService.FormatIsk(
                overview.WeekSpent);

        SetSignedMoney(
            WalletWeekNetText,
            overview.WeekNet);

        WalletMarketSummaryText.Text =
            "MARKET  BUY " +
            EveSsoService.FormatIsk(
                overview.MarketBought) +
            "  |  SELL " +
            EveSsoService.FormatIsk(
                overview.MarketSold) +
            "  |  NET " +
            EveSsoService.FormatIskSigned(
                overview.MarketNet);

        PlexBoughtText.Text =
            overview.PlexBought.ToString("N0");

        PlexSoldText.Text =
            overview.PlexSold.ToString("N0");

        PlexAverageBuyText.Text =
            overview.PlexAverageBuy > 0
                ? EveSsoService.FormatIsk(
                    overview.PlexAverageBuy)
                : "-";

        PlexAverageSellText.Text =
            overview.PlexAverageSell > 0
                ? EveSsoService.FormatIsk(
                    overview.PlexAverageSell)
                : "-";

        SetSignedMoney(
            PlexNetText,
            overview.PlexNetIsk);
    }

    private static void SetSignedMoney(
        System.Windows.Controls.TextBlock target,
        decimal value)
    {
        target.Text =
            EveSsoService.FormatIskSigned(value);

        string color =
            value > 0
                ? "#58D3B4"
                : value < 0
                    ? "#E87979"
                    : "#9DB5AF";

        target.Foreground =
            (Brush)new BrushConverter()
                .ConvertFromString(color)!;
    }

    private async Task<IReadOnlyList<EveSkillCatalogEntry>> LoadSkillBrowserAsync(
        IReadOnlyList<EveSkillEntry> trainedSkills,
        CancellationToken cancellationToken)
    {
        SkillsCatalogStatusText.Text =
            "Preparing skill catalogue...";

        var progress = new Progress<string>(
            message => SkillsCatalogStatusText.Text = message);

        IReadOnlyList<EveSkillCatalogEntry> catalog =
            await _skillCatalog.GetCatalogAsync(
                progress,
                cancellationToken);

        var trainedById =
            trainedSkills.ToDictionary(
                skill => skill.SkillId);

        _allSkillRows = catalog
            .Select(entry =>
            {
                trainedById.TryGetValue(
                    entry.SkillId,
                    out EveSkillEntry? trained);

                return new SkillRowViewModel(
                    entry,
                    trained,
                    _trainingProfile,
                    AttributeAlignmentToggle.IsChecked == true);
            })
            .OrderBy(
                row => row.GroupName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(
                row => row.Name,
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        string? previousCategory =
            SkillCategoryFilter.SelectedItem as string;

        string[] categories =
            new[] { "All categories" }
            .Concat(
                _allSkillRows
                    .Select(row => row.GroupName)
                    .Distinct(
                        StringComparer.OrdinalIgnoreCase)
                    .OrderBy(
                        name => name,
                        StringComparer.OrdinalIgnoreCase))
            .ToArray();

        SkillCategoryFilter.ItemsSource = categories;

        if (!string.IsNullOrWhiteSpace(previousCategory) &&
            categories.Contains(
                previousCategory,
                StringComparer.OrdinalIgnoreCase))
        {
            SkillCategoryFilter.SelectedItem =
                previousCategory;
        }
        else
        {
            SkillCategoryFilter.SelectedIndex = 0;
        }

        UpdateSkillFilterCounts();
        ApplySkillFilters();

        SkillsCatalogStatusText.Text =
            $"{catalog.Count:N0} published skills";

        return catalog;
    }

    private void RefreshImplantItems()
    {
        if (!_trainingProfile.ImplantDataAvailable)
        {
            ImplantItems.ItemsSource = null;
            ShowAllImplantsToggle.Visibility =
                Visibility.Collapsed;
            ImplantAccessText.Text =
                "IMPLANTS LOCKED | Add Character again and select this pilot to grant implant access.";
            return;
        }

        int total =
            _trainingProfile.Implants.Count;

        int trainingCount =
            _trainingProfile.Implants.Count(
                implant => implant.IsTrainingRelevant);

        bool showAll =
            ShowAllImplantsToggle.IsChecked == true;

        ImplantItems.ItemsSource =
            showAll
                ? _trainingProfile.Implants
                : _trainingProfile.Implants
                    .Where(
                        implant => implant.IsTrainingRelevant)
                    .ToArray();

        int hidden =
            Math.Max(
                0,
                total - trainingCount);

        ImplantAccessText.Text =
            trainingCount > 0
                ? $"TRAINING IMPLANTS | {trainingCount} active" +
                  (hidden > 0
                      ? $" | {hidden} other hidden"
                      : "")
                : "TRAINING IMPLANTS | none";

        ShowAllImplantsToggle.Visibility =
            hidden > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        ShowAllImplantsToggle.Content =
            showAll
                ? "TRAINING ONLY"
                : $"SHOW ALL ({total})";
    }

    private void ShowAllImplantsToggle_Click(
        object sender,
        RoutedEventArgs e)
    {
        RefreshImplantItems();
    }

    private async void PilotTabs_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        // SelectionChanged is a routed event. DataGrids, ComboBoxes and other
        // selectors inside a tab can bubble their own SelectionChanged through
        // the parent TabControl, so only respond to the TabControl itself.
        if (sender is not System.Windows.Controls.TabControl tabs ||
            !ReferenceEquals(e.OriginalSource, tabs) ||
            !ShipAssetsTab.IsSelected)
            return;

        if (PilotList.SelectedItem
            is not PilotCardViewModel card)
            return;

        await LoadInventoryAsync(
            card,
            force: false);
    }

    private async void RefreshShipAssets_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (PilotList.SelectedItem
            is not PilotCardViewModel card)
            return;

        await LoadInventoryAsync(
            card,
            force: true);
    }

    private async Task LoadInventoryAsync(
        PilotCardViewModel card,
        bool force)
    {
        if (!force &&
            _inventoryLoadedForCharacterId ==
            card.CharacterId)
            return;

        try
        {
            ShipAssetsStatusText.Text =
                $"Syncing {card.CharacterName} ship, assets and fittings...";

            CancellationToken token =
                _loadCts?.Token ??
                CancellationToken.None;

            EveInventorySnapshot inventory =
                await _sso.GetInventoryAsync(
                    card.Profile,
                    token);

            if (PilotList.SelectedItem
                    is not PilotCardViewModel selected ||
                selected.CharacterId !=
                card.CharacterId)
                return;

            _inventoryLoadedForCharacterId =
                card.CharacterId;
            _currentInventory = inventory;

            CurrentShipDetailText.Text =
                string.IsNullOrWhiteSpace(
                    inventory.CurrentShip.DisplayName)
                    ? "-"
                    : inventory.CurrentShip.DisplayName;

            CurrentShipItemText.Text =
                inventory.CurrentShip.DetailText;

            ShipAssetsStatusText.Text =
                inventory.AccessMessage;

            CurrentEquipmentCountText.Text =
                $"{inventory.CurrentShipModules.Count:N0} fitted/bay item(s)";

            FittingCountText.Text =
                inventory.FittingsAvailable
                    ? $"{inventory.Fittings.Count:N0} fitting(s)"
                    : "permission required";

            AssetCountText.Text =
                inventory.AssetsAvailable
                    ? $"{inventory.Assets.Count:N0} asset row(s)"
                    : "permission required";

            CurrentShipModulesGrid.ItemsSource =
                inventory.CurrentShipModules;
            SavedFittingsGrid.ItemsSource =
                inventory.Fittings;
            PersonalAssetsGrid.ItemsSource =
                inventory.Assets;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShipAssetsStatusText.Text =
                "Ship/assets sync failed: " +
                ex.Message;
        }
    }

    private void ViewCurrentFit_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_currentInventory == null)
        {
            WpfMessageBox.Show(
                "Open or refresh Ship & Assets first so the current fit can be loaded.",
                "EVE Command Center - Fit Viewer",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        OpenFitWindow(
            "CURRENT FIT",
            _currentInventory.CurrentShip.DisplayName,
            _currentInventory.CurrentShipModules,
            "");
    }

    private void ViewSavedFit_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.DataContext is not EveFittingView fitting)
            return;

        OpenFitWindow(
            fitting.Name,
            fitting.Ship,
            fitting.Modules,
            fitting.Description);
    }

    private void OpenFitWindow(
        string fitName,
        string shipName,
        IReadOnlyList<EveShipModuleView> modules,
        string description)
    {
        var window =
            new PilotFitWindow(
                fitName,
                shipName,
                modules,
                description)
            {
                Owner = this
            };

        window.Show();
    }

    private void AttributeAlignmentToggle_Click(
        object sender,
        RoutedEventArgs e)
    {
        bool enabled =
            AttributeAlignmentToggle.IsChecked == true;

        foreach (SkillRowViewModel row in _allSkillRows)
            row.HighlightOffMap = enabled;

        foreach (QueueRowViewModel row in _queueRows)
            row.HighlightOffMap = enabled;

        ApplySkillFilters();

        SkillQueueGrid.ItemsSource = null;
        SkillQueueGrid.ItemsSource = _queueRows;
    }

    private void BuildQueueRows(
        IReadOnlyList<EveSkillQueueView> queue,
        IReadOnlyList<EveSkillCatalogEntry> catalog,
        EveTrainingProfile trainingProfile)
    {
        var catalogById =
            catalog.ToDictionary(
                entry => entry.SkillId);

        bool highlight =
            AttributeAlignmentToggle.IsChecked == true;

        _queueRows = queue
            .Select(entry =>
            {
                catalogById.TryGetValue(
                    entry.SkillId,
                    out EveSkillCatalogEntry? skill);

                return new QueueRowViewModel(
                    entry,
                    skill,
                    trainingProfile,
                    highlight);
            })
            .ToList();

        SkillQueueGrid.ItemsSource = _queueRows;
    }

    private static AttributePresentation GetAttributePresentation(
        int dogmaAttributeId)
    {
        return dogmaAttributeId switch
        {
            164 => new(
                "Charisma",
                "CHA",
                "",
                "#FF8FA6"),
            165 => new(
                "Intelligence",
                "INT",
                "",
                "#64C7FF"),
            166 => new(
                "Memory",
                "MEM",
                "",
                "#9FD67A"),
            167 => new(
                "Perception",
                "PER",
                "",
                "#E7B85A"),
            168 => new(
                "Willpower",
                "WIL",
                "",
                "#D693FF"),
            _ => new(
                "Unknown",
                "?",
                "",
                "#78958E")
        };
    }
    private static AlignmentPresentation GetAlignment(
        int primaryAttributeId,
        int secondaryAttributeId,
        EveTrainingProfile profile)
    {
        int primary =
            profile.GetTotal(primaryAttributeId);
        int secondary =
            profile.GetTotal(secondaryAttributeId);

        double rate =
            primary + secondary / 2.0;

        double best =
            profile.BestCurrentTrainingRate;

        double alignment =
            best <= 0
                ? 1.0
                : Math.Clamp(rate / best, 0, 1);

        bool offMap =
            best > 0 &&
            alignment < 0.90;

        return new AlignmentPresentation(
            rate,
            alignment,
            offMap);
    }

    private readonly record struct AttributePresentation(
        string Name,
        string ShortName,
        string Symbol,
        string Accent);

    private readonly record struct AlignmentPresentation(
        double Rate,
        double Alignment,
        bool IsOffMap);

    private void SkillSearchBox_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_allSkillRows.Count > 0)
            ApplySkillFilters();
    }

    private void SkillCategoryFilter_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_allSkillRows.Count > 0)
            ApplySkillFilters();
    }

    private void SkillFilter_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not string filter)
            return;

        _skillFilter = filter;
        UpdateSkillFilterVisuals();
        ApplySkillFilters();
    }

    private void ExpandAllSkills_Click(
        object sender,
        RoutedEventArgs e)
    {
        foreach (SkillGroupViewModel group in SkillGroups)
            group.IsExpanded = true;
    }

    private void CollapseAllSkills_Click(
        object sender,
        RoutedEventArgs e)
    {
        foreach (SkillGroupViewModel group in SkillGroups)
            group.IsExpanded = false;
    }

    private void UpdateSkillFilterCounts()
    {
        SkillFilterAll.Content =
            $"All ({_allSkillRows.Count:N0})";

        SkillFilterTrained.Content =
            $"Trained ({_allSkillRows.Count(s => s.Level > 0):N0})";

        SkillFilterV.Content =
            $"Level V ({_allSkillRows.Count(s => s.Level == 5):N0})";

        SkillFilterIV.Content =
            $"Level IV ({_allSkillRows.Count(s => s.Level == 4):N0})";

        SkillFilterIII.Content =
            $"Level III ({_allSkillRows.Count(s => s.Level == 3):N0})";

        SkillFilterII.Content =
            $"Level II ({_allSkillRows.Count(s => s.Level == 2):N0})";

        SkillFilterI.Content =
            $"Level I ({_allSkillRows.Count(s => s.Level == 1):N0})";

        SkillFilterUntrained.Content =
            $"Untrained ({_allSkillRows.Count(s => s.Level == 0):N0})";

        long currentSp =
            _allSkillRows.Sum(s => s.CurrentSp);
        long maxSp =
            _allSkillRows.Sum(s => s.MaxSp);

        double completion =
            maxSp <= 0
                ? 0
                : currentSp * 100.0 / maxSp;

        SkillTotalsText.Text =
            $"{_allSkillRows.Count(s => s.Level > 0):N0}/" +
            $"{_allSkillRows.Count:N0} trained  •  " +
            $"{FormatSkillPoints(currentSp)} / " +
            $"{FormatSkillPoints(maxSp)}  •  " +
            $"{completion:0.0}%";

        UpdateSkillFilterVisuals();
    }

    private void UpdateSkillFilterVisuals()
    {
        System.Windows.Controls.Button[] buttons =
        {
            SkillFilterAll,
            SkillFilterTrained,
            SkillFilterV,
            SkillFilterIV,
            SkillFilterIII,
            SkillFilterII,
            SkillFilterI,
            SkillFilterUntrained
        };

        var activeBackground =
            new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(28, 90, 79));
        var idleBackground =
            new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(16, 31, 34));

        var activeBorder =
            new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(88, 211, 180));
        var idleBorder =
            new SolidColorBrush(
                System.Windows.Media.Color.FromRgb(39, 71, 64));

        foreach (System.Windows.Controls.Button button in buttons)
        {
            bool active =
                string.Equals(
                    button.Tag?.ToString(),
                    _skillFilter,
                    StringComparison.OrdinalIgnoreCase);

            button.Background =
                active
                    ? activeBackground
                    : idleBackground;

            button.BorderBrush =
                active
                    ? activeBorder
                    : idleBorder;
        }
    }

    private void ApplySkillFilters()
    {
        IEnumerable<SkillRowViewModel> query =
            _allSkillRows;

        string search =
            SkillSearchBox.Text?.Trim() ?? "";

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(
                skill =>
                    skill.Name.Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase) ||
                    skill.GroupName.Contains(
                        search,
                        StringComparison.OrdinalIgnoreCase));
        }

        string category =
            SkillCategoryFilter.SelectedItem as string
            ?? "All categories";

        if (!string.Equals(
                category,
                "All categories",
                StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(
                skill =>
                    string.Equals(
                        skill.GroupName,
                        category,
                        StringComparison.OrdinalIgnoreCase));
        }

        query = _skillFilter switch
        {
            "trained" =>
                query.Where(skill => skill.Level > 0),
            "5" =>
                query.Where(skill => skill.Level == 5),
            "4" =>
                query.Where(skill => skill.Level == 4),
            "3" =>
                query.Where(skill => skill.Level == 3),
            "2" =>
                query.Where(skill => skill.Level == 2),
            "1" =>
                query.Where(skill => skill.Level == 1),
            "0" =>
                query.Where(skill => skill.Level == 0),
            _ => query
        };

        var expandedState =
            SkillGroups.ToDictionary(
                group => group.Name,
                group => group.IsExpanded,
                StringComparer.OrdinalIgnoreCase);

        SkillGroups.Clear();

        foreach (var group in query
                     .GroupBy(
                         skill => skill.GroupName,
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(
                         group => group.Key,
                         StringComparer.OrdinalIgnoreCase))
        {
            var view =
                new SkillGroupViewModel(
                    group.Key,
                    group.OrderBy(
                            skill => skill.Name,
                            StringComparer.OrdinalIgnoreCase)
                         .ToArray());

            if (expandedState.TryGetValue(
                    view.Name,
                    out bool expanded))
            {
                view.IsExpanded = expanded;
            }

            SkillGroups.Add(view);
        }
    }

    private static string FormatSkillPoints(long value)
    {
        if (value >= 1_000_000_000)
            return $"{value / 1_000_000_000d:0.00}B SP";
        if (value >= 1_000_000)
            return $"{value / 1_000_000d:0.00}M SP";
        if (value >= 1_000)
            return $"{value / 1_000d:0.0}K SP";

        return $"{value:N0} SP";
    }

    private static string RomanLevel(int level) =>
        level switch
        {
            1 => "I",
            2 => "II",
            3 => "III",
            4 => "IV",
            5 => "V",
            _ => level.ToString()
        };

    private void SetStatus(string text) =>
        StatusText.Text = text;

    private void TitleBar_MouseLeftButtonDown(
        object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(
        object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Close_Click(
        object sender, RoutedEventArgs e) =>
        Close();

    public sealed class SkillLevelBlock
    {
        public bool IsFilled { get; init; }
    }

    public sealed class SkillRowViewModel
    {
        private static readonly long[] BaseThresholds =
        {
            0,
            250,
            1415,
            8000,
            45255,
            256000
        };

        public SkillRowViewModel(
            EveSkillCatalogEntry catalog,
            EveSkillEntry? trained,
            EveTrainingProfile trainingProfile,
            bool highlightOffMap)
        {
            SkillId = catalog.SkillId;
            Name = catalog.Name;
            GroupName = catalog.GroupName;
            Rank = Math.Max(1, catalog.Rank);
            Level = Math.Clamp(
                trained?.TrainedSkillLevel ?? 0,
                0,
                5);
            ActiveLevel = Math.Clamp(
                trained?.ActiveSkillLevel ?? Level,
                0,
                5);
            CurrentSp =
                Math.Max(
                    0,
                    trained?.SkillpointsInSkill ?? 0);
            MaxSp = Math.Max(
                catalog.MaxSp,
                BaseThresholds[5] * Rank);

            LevelBlocks =
                Enumerable.Range(1, 5)
                    .Select(
                        level =>
                            new SkillLevelBlock
                            {
                                IsFilled =
                                    level <= Level
                            })
                    .ToArray();

            LevelText =
                Level <= 0
                    ? "Untrained"
                    : $"Level {RomanLevel(Level)}";

            RankText =
                $"Rank {Rank}";

            SpText =
                $"{CurrentSp:N0} / {MaxSp:N0}";

            if (Level >= 5)
            {
                RemainingToNext = 0;
                NextText = "MAX";
                NextForeground = "#58D3B4";
            }
            else
            {
                long nextThreshold =
                    BaseThresholds[Level + 1] * Rank;

                RemainingToNext =
                    Math.Max(
                        0,
                        nextThreshold - CurrentSp);

                NextText =
                    $"To {RomanLevel(Level + 1)}: " +
                    $"{RemainingToNext:N0} SP";

                NextForeground = "#E7B85A";
            }

            PrimaryAttributeId =
                catalog.PrimaryAttributeId;
            SecondaryAttributeId =
                catalog.SecondaryAttributeId;

            AttributePresentation primary =
                GetAttributePresentation(
                    PrimaryAttributeId);
            AttributePresentation secondary =
                GetAttributePresentation(
                    SecondaryAttributeId);

            PrimaryBadge =
                primary.ShortName;
            SecondaryBadge =
                secondary.ShortName;
            PrimaryAccent = primary.Accent;
            SecondaryAccent = secondary.Accent;

            AlignmentPresentation alignment =
                GetAlignment(
                    PrimaryAttributeId,
                    SecondaryAttributeId,
                    trainingProfile);

            TrainingRate = alignment.Rate;
            AlignmentPercent =
                alignment.Alignment * 100.0;
            IsOffMap = alignment.IsOffMap;
            HighlightOffMap = highlightOffMap;
        }

        public int SkillId { get; }
        public string Name { get; }
        public string GroupName { get; }
        public int Rank { get; }
        public int Level { get; }
        public int ActiveLevel { get; }
        public long CurrentSp { get; }
        public long MaxSp { get; }
        public long RemainingToNext { get; }
        public string LevelText { get; }
        public string RankText { get; }
        public string SpText { get; }
        public string NextText { get; }
        public string NextForeground { get; }
        public IReadOnlyList<SkillLevelBlock> LevelBlocks { get; }

        public int PrimaryAttributeId { get; }
        public int SecondaryAttributeId { get; }
        public string PrimaryBadge { get; }
        public string SecondaryBadge { get; }
        public string PrimaryAccent { get; }
        public string SecondaryAccent { get; }
        public double TrainingRate { get; }
        public double AlignmentPercent { get; }
        public bool IsOffMap { get; }

        public bool HighlightOffMap { get; set; }

        public string AlignmentText =>
            $"{TrainingRate:0.0} SP/min | {AlignmentPercent:0}%";
        public string AlignmentForeground =>
            IsOffMap
                ? "#E7B85A"
                : "#58D3B4";

        public string RowBackground =>
            HighlightOffMap && IsOffMap
                ? "#2A2418"
                : "#0D171A";
    }

    public sealed class QueueRowViewModel
    {
        public QueueRowViewModel(
            EveSkillQueueView source,
            EveSkillCatalogEntry? catalog,
            EveTrainingProfile trainingProfile,
            bool highlightOffMap)
        {
            Position = source.Position;
            Skill = source.Skill;
            LevelText = $"-> {source.Level}";            Starts = source.Starts;
            Finishes = source.Finishes;
            Remaining = source.Remaining;

            int primaryId =
                catalog?.PrimaryAttributeId ?? 0;
            int secondaryId =
                catalog?.SecondaryAttributeId ?? 0;

            AttributePresentation primary =
                GetAttributePresentation(
                    primaryId);
            AttributePresentation secondary =
                GetAttributePresentation(
                    secondaryId);

            PrimaryBadge =
                primary.ShortName;
            SecondaryBadge =
                secondary.ShortName;
            PrimaryAccent = primary.Accent;
            SecondaryAccent = secondary.Accent;

            AlignmentPresentation alignment =
                GetAlignment(
                    primaryId,
                    secondaryId,
                    trainingProfile);

            TrainingRate = alignment.Rate;
            AlignmentPercent =
                alignment.Alignment * 100.0;
            IsOffMap = alignment.IsOffMap;
            HighlightOffMap = highlightOffMap;

            DateTimeOffset now =
                DateTimeOffset.UtcNow;

            bool current =
                source.FinishDate.HasValue &&
                source.FinishDate.Value > now &&
                (!source.StartDate.HasValue ||
                 source.StartDate.Value <= now);

            IsCurrent = current;

            if (current &&
                source.StartDate.HasValue &&
                source.FinishDate.HasValue &&
                source.FinishDate.Value >
                source.StartDate.Value)
            {
                double total =
                    (source.FinishDate.Value -
                     source.StartDate.Value)
                    .TotalSeconds;

                double elapsed =
                    (now -
                     source.StartDate.Value)
                    .TotalSeconds;

                ProgressPercent =
                    Math.Clamp(
                        elapsed * 100.0 / total,
                        0,
                        100);
            }
            else
            {
                ProgressPercent = 0;
            }
        }

        public int Position { get; }
        public string Skill { get; }
        public string LevelText { get; }
        public string Starts { get; }
        public string Finishes { get; }
        public string Remaining { get; }
        public string PrimaryBadge { get; }
        public string SecondaryBadge { get; }
        public string PrimaryAccent { get; }
        public string SecondaryAccent { get; }
        public double TrainingRate { get; }
        public double AlignmentPercent { get; }
        public bool IsOffMap { get; }
        public bool IsCurrent { get; }
        public double ProgressPercent { get; }

        public bool HighlightOffMap { get; set; }

        public string AlignmentText =>
            $"{TrainingRate:0.0} SP/min | {AlignmentPercent:0}%";
        public string AlignmentForeground =>
            IsOffMap
                ? "#E7B85A"
                : "#58D3B4";

        public string RowBackground =>
            HighlightOffMap && IsOffMap
                ? "#2A2418"
                : IsCurrent
                    ? "#102522"
                    : "#0D171A";

        public string RowBorderBrush =>
            HighlightOffMap && IsOffMap
                ? "#8C6A2E"
                : IsCurrent
                    ? "#2D7E6C"
                    : "#15282B";

        public string StatusText =>
            IsCurrent
                ? IsOffMap
                    ? "TRAINING | OFF-MAP"
                    : "TRAINING"
                : IsOffMap
                    ? "OFF-MAP"
                    : "QUEUED";
        public string StatusBackground =>
            IsOffMap
                ? "#3A2F16"
                : IsCurrent
                    ? "#153D34"
                    : "#162124";

        public string StatusForeground =>
            IsOffMap
                ? "#F2C96D"
                : IsCurrent
                    ? "#58D3B4"
                    : "#8FA9A3";
    }

    public sealed class SkillGroupViewModel :
        INotifyPropertyChanged
    {
        private bool _isExpanded = true;

        public SkillGroupViewModel(
            string name,
            IReadOnlyList<SkillRowViewModel> skills)
        {
            Name = name;
            Skills = skills;

            TotalCount = skills.Count;
            TrainedCount =
                skills.Count(skill => skill.Level > 0);

            CurrentSp =
                skills.Sum(skill => skill.CurrentSp);
            MaxSp =
                skills.Sum(skill => skill.MaxSp);

            CompletionPercent =
                MaxSp <= 0
                    ? 0
                    : CurrentSp * 100.0 / MaxSp;

            SummaryText =
                $"{TrainedCount:N0}/{TotalCount:N0} trained  •  " +
                $"{FormatSkillPoints(CurrentSp)} / " +
                $"{FormatSkillPoints(MaxSp)}  •  " +
                $"{CompletionPercent:0.0}%";
        }

        public string Name { get; }
        public IReadOnlyList<SkillRowViewModel> Skills { get; }
        public int TotalCount { get; }
        public int TrainedCount { get; }
        public long CurrentSp { get; }
        public long MaxSp { get; }
        public double CompletionPercent { get; }
        public string SummaryText { get; }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                    return;

                _isExpanded = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(
                        nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler?
            PropertyChanged;
    }

    public sealed class PilotCardViewModel :
        INotifyPropertyChanged
    {
        private string _walletText = "Loading...";
        private string _spText = "Loading...";
        private string _trainingText = "Loading...";
        private string _locationText = "System: loading...";
        private string _shipText = "Ship: loading...";
        private string _queueText = "Q ...";

        public PilotCardViewModel(
            EvePilotProfile profile)
        {
            Profile = profile;
        }

        public EvePilotProfile Profile { get; set; }

        public long CharacterId =>
            Profile.CharacterId;

        public string CharacterName =>
            Profile.CharacterName;

        public string PortraitUrl =>
            $"https://images.evetech.net/characters/" +
            $"{CharacterId}/portrait?size=128";

        public string WalletText
        {
            get => _walletText;
            set
            {
                _walletText = value;
                OnPropertyChanged();
            }
        }

        public string SpText
        {
            get => _spText;
            set
            {
                _spText = value;
                OnPropertyChanged();
            }
        }

        public string TrainingText
        {
            get => _trainingText;
            set
            {
                _trainingText = value;
                OnPropertyChanged();
            }
        }

        public string LocationText
        {
            get => _locationText;
            set
            {
                _locationText = value;
                OnPropertyChanged();
            }
        }

        public string ShipText
        {
            get => _shipText;
            set
            {
                _shipText = value;
                OnPropertyChanged();
            }
        }

        public string QueueText
        {
            get => _queueText;
            set
            {
                _queueText = value;
                OnPropertyChanged();
            }
        }

        public void Apply(EvePilotSummary summary)
        {
            WalletText =
                EveSsoService.FormatIsk(
                    summary.WalletBalance);
            SpText = $"{summary.TotalSp:N0} SP";

            LocationText =
                string.IsNullOrWhiteSpace(summary.CurrentSystem)
                    ? "System: -"
                    : "System: " + summary.CurrentSystem;

            ShipText =
                string.IsNullOrWhiteSpace(summary.CurrentShip)
                    ? "Ship: -"
                    : "Ship: " + summary.CurrentShip;

            QueueText =
                string.Equals(
                    summary.QueueEndsIn,
                    "Empty",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Q empty"
                    : "Q " + summary.QueueEndsIn;

            TrainingText =
                summary.CurrentSkill == "Queue empty"
                    ? "Queue empty"
                    : $"{summary.CurrentSkill} • " +
                      summary.CurrentSkillRemaining;
        }

        public event PropertyChangedEventHandler?
            PropertyChanged;

        private void OnPropertyChanged(
            [CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(name));
    }
}
