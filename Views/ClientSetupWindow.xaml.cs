using System.Windows;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

public partial class ClientSetupWindow : Window
{
    private readonly BackgroundOperations _operations = BackgroundOperations.Current;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<EvePilotProfile> _pilots = Array.Empty<EvePilotProfile>();
    private bool _busy;
    private sealed record LinkChoice(EvePilotProfile Profile)
    {
        public string Label => Profile.CharacterName + (EveAuthorizationScopes.Complete(Profile) ? " | All feature permissions saved" : " | One-time upgrade needed");
    }
    public ClientSetupWindow(bool firstRun = false)
    {
        InitializeComponent();
        EsiDebug.IsChecked = EsiDiagnostics.Enabled;
        var progress = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        progress.Tick += (_, _) => { if (_busy) StatusText.Text = "Working (permission checks allow 45 seconds per reader). ESI: " + EsiDiagnostics.Status; };
        progress.Start();
        Closed += (_, _) => progress.Stop();
        MaxHeight = SystemParameters.WorkArea.Height;
        Height = Math.Min(Height, MaxHeight);
        GeneralButton.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) => { Busy(true); try { await ReloadAsync(); await ValidateAsync(); } catch (Exception ex) { StatusText.Text = ex.Message; } finally { Busy(false); } };
        Closed += (_, _) => _lifetime.Cancel();
    }
    private async Task ReloadAsync()
    {
        long? selectedUpgrade = (UpgradePilot.SelectedItem as LinkChoice)?.Profile.CharacterId;
        _pilots = await _operations.Sso.LoadPilotsAsync();
        var choices = _pilots.Select(p => new LinkChoice(p)).ToList();
        UpgradePilot.ItemsSource = choices;
        UpgradePilot.SelectedItem = choices.FirstOrDefault(p => p.Profile.CharacterId == selectedUpgrade)
            ?? choices.FirstOrDefault(p => !EveAuthorizationScopes.Complete(p.Profile)) ?? choices.FirstOrDefault();
        UpgradeButton.IsEnabled = choices.Count > 0;
        PilotText.Text = _pilots.Count == 0 ? "No characters linked yet." : string.Join(" | ", _pilots.Select(p => p.CharacterName));
        MoonPilot.ItemsSource = ContractPilot.ItemsSource = _pilots;
        MoonPilot.SelectedItem = _pilots.FirstOrDefault(p => p.CharacterId == _operations.Access.State.MoonCharacterId);
        ContractPilot.SelectedItem = _pilots.FirstOrDefault(p => p.CharacterId == _operations.Access.State.ContractCharacterId);
    }
    private void Busy(bool value) { _busy = value; LinkPanel.IsEnabled = ContinueButton.IsEnabled = !value; }
    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Busy(true);
        try
        {
            StatusText.Text = "Choose the correct character on the official EVE authorization page.";
            var role = (sender as System.Windows.Controls.Button)?.Tag?.ToString();
            var selected = role == "upgrade" ? (UpgradePilot.SelectedItem as LinkChoice)?.Profile : null;
            if (role == "upgrade" && selected == null) { StatusText.Text = "Select a linked toon to upgrade."; return; }
            await _operations.Sso.AddCharacterAsync(_lifetime.Token, characterId: selected?.CharacterId);
            await ReloadAsync();
            await ValidateAsync();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { Busy(false); }
    }
    private async Task ValidateAsync()
    {
        StatusText.Text = "Checking live EVE corporation permissions...";
        _operations.Access.State.MoonCharacterId = (MoonPilot.SelectedItem as EvePilotProfile)?.CharacterId ?? 0;
        _operations.Access.State.ContractCharacterId = (ContractPilot.SelectedItem as EvePilotProfile)?.CharacterId ?? 0;
        _operations.Access.Save();
        await _operations.Access.ValidateAsync(_pilots, _lifetime.Token);
        if (_operations.Access.CanReadMoons) await _operations.Moons.SelectPilotAsync(_operations.Access.State.MoonCharacterId);
        if (_operations.Access.CanReadContracts)
        {
            if (_operations.Contracts.State.CharacterId != _operations.Access.State.ContractCharacterId)
            {
                _operations.Contracts.State.Rows.Clear();
                _operations.Contracts.State.CorporationName = "";
                _operations.Contracts.State.CorporationId = 0;
                _operations.Contracts.State.LastRefreshUtc = null;
            }
            _operations.Contracts.State.CharacterId = _operations.Access.State.ContractCharacterId;
            _operations.Contracts.Save();
        }
        _operations.ScheduleRefresh();
        MoonStatus.Text = _operations.Access.MoonStatus;
        ContractStatus.Text = _operations.Access.ContractStatus;
        StatusText.Text = "Access checked. Unavailable corporation views stay hidden; all personal tools remain available.";
    }
    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Busy(true);
        try { await ValidateAsync(); } catch (Exception ex) { StatusText.Text = ex.Message; } finally { Busy(false); }
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as System.Windows.Controls.Button)?.Tag?.ToString() == "moon") MoonPilot.SelectedItem = null;
        else ContractPilot.SelectedItem = null;
        Verify_Click(sender, e);
    }
    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_pilots.Count == 0) { StatusText.Text = "Link your first pilot to finish setup. Corporation links are optional."; return; }
        Busy(true);
        try { await ValidateAsync(); _operations.Access.State.SetupCompleted = true; _operations.Access.Save(); DialogResult = true; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { Busy(false); }
    }
    private void EsiDebug_Click(object sender, RoutedEventArgs e) => EsiDiagnostics.Enabled = EsiDebug.IsChecked == true;
    private void OpenEsiLogs_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(EsiDiagnostics.DirectoryPath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(EsiDiagnostics.DirectoryPath) { UseShellExecute = true });
    }
    private void General_Click(object sender, RoutedEventArgs e) => (System.Windows.Application.Current as App)?.ShowGeneralSettings();
}
