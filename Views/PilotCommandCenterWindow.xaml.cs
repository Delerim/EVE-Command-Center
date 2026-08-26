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
            await LoadSelectedPilotAsync(card);
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
            SkillQueueGrid.ItemsSource =
                data.SkillQueue;
            WalletGrid.ItemsSource =
                data.WalletJournal;

            card.Apply(data.Summary);

            await LoadSkillBrowserAsync(
                data.TrainedSkills,
                _loadCts.Token);

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
        WalletGrid.ItemsSource = null;

        _allSkillRows.Clear();
        SkillGroups.Clear();
        SkillCategoryFilter.ItemsSource = null;
        SkillSearchBox.Text = "";
        SkillTotalsText.Text = "-";
        SkillsCatalogStatusText.Text = "";
    }

    private async Task LoadSkillBrowserAsync(
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
                    trained);
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
    }

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
        if (sender is not Button button ||
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
        Button[] buttons =
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
                Color.FromRgb(28, 90, 79));
        var idleBackground =
            new SolidColorBrush(
                Color.FromRgb(16, 31, 34));

        var activeBorder =
            new SolidColorBrush(
                Color.FromRgb(88, 211, 180));
        var idleBorder =
            new SolidColorBrush(
                Color.FromRgb(39, 71, 64));

        foreach (Button button in buttons)
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
            EveSkillEntry? trained)
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

        public void Apply(EvePilotSummary summary)
        {
            WalletText =
                EveSsoService.FormatIsk(
                    summary.WalletBalance);
            SpText = $"{summary.TotalSp:N0} SP";
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
