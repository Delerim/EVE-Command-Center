using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using WpfMessageBox = System.Windows.MessageBox;
using EveMultiPreview.Models;
using EveMultiPreview.Services;

namespace EveMultiPreview.Views;

public partial class PilotCommandCenterWindow : Window
{
    private readonly EveSsoService _sso = new();
    private CancellationTokenSource? _loadCts;
    private bool _loaded;

    public ObservableCollection<PilotCardViewModel> Pilots { get; } = new();

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
    }

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
