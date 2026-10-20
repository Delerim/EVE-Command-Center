using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushConverter = System.Windows.Media.BrushConverter;

namespace EveCommandCenter.Views;

public partial class CloudBackupWindow : Window
{
    private readonly CloudBackupService _service = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _connected;
    private bool _configured;
    private bool _hasRemoteBackup;

    public CloudBackupWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AutoBackupCheck.IsChecked = _service.Settings.AutoBackupEnabled;
        MinutesBox.Text = _service.Settings.AutoBackupMinutes
            .ToString(CultureInfo.InvariantCulture);
        RecoveryPasswordStatus.Text = _service.HasRecoveryPassword ? "Configured" : "Not set";
        PrivateMoonCsvStatus.Text = string.IsNullOrWhiteSpace(_service.Settings.PrivateMoonCsvPath)
            ? "No external moon CSV selected" : _service.Settings.PrivateMoonCsvPath;
        await RefreshStatusAsync();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _service.Dispose();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose Google Desktop OAuth client JSON",
            Filter = "Google OAuth JSON (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        bool restored = false;
        await RunAsync(async () =>
        {
            SetStatus("Complete Google authorization in your browser.");
            await _service.ConfigureFromGoogleJsonAsync(
                dialog.FileName, _lifetime.Token);
            CloudBackupCoordinator.RefreshSettings();
            SetStatus("Google Drive connected.");
            CloudBackupStatus status = await _service.GetStatusAsync(
                _lifetime.Token);
            if (status.HasRemoteBackup && System.Windows.MessageBox.Show(
                    this,
                    "A cloud backup was found. Restore it now and restart " +
                    "EVE Command Center? Current files will be safety-copied first.",
                    "Restore Existing Backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var progress = new Progress<string>(SetStatus);
                await _service.RestoreNowAsync(progress, _lifetime.Token,
                (RestorePointsBox.SelectedItem as CloudRestorePoint)?.Id);
                restored = true;
            }
        }, refreshAfter: false);
        if (restored)
            _service.RestartApplication();
        else
            await RefreshStatusAsync();
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            SetStatus("Complete Google authorization in your browser.");
            await _service.AuthorizeGoogleAsync(_lifetime.Token);
            CloudBackupCoordinator.RefreshSettings();
            SetStatus("Google Drive reconnected.");
        });
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                this,
                "Disconnect Google Drive on this PC? Existing cloud backup data will not be deleted.",
                "Cloud Backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunAsync(async () =>
        {
            await _service.DisconnectAsync();
            CloudBackupCoordinator.RefreshSettings();
            SetStatus("Google Drive disconnected on this PC.");
        });
    }

    private void SaveRecoveryPassword_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.Equals(RecoveryPasswordBox.Password, ConfirmRecoveryPasswordBox.Password, StringComparison.Ordinal))
                throw new InvalidOperationException("Recovery passwords do not match.");
            if (_service.HasRecoveryPassword && System.Windows.MessageBox.Show(this,
                "Replacing the recovery password does not re-encrypt older snapshots. Keep the previous password if you may need to restore them. Continue?",
                "Change Backup Recovery Password", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _service.SetRecoveryPassword(RecoveryPasswordBox.Password);
            RecoveryPasswordBox.Clear();
            ConfirmRecoveryPasswordBox.Clear();
            RecoveryPasswordStatus.Text = "Configured";
            SetStatus("Recovery password encrypted locally. Keep a copy in your password manager!");
            UpdateButtonStates();
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private async void ChoosePrivateMoonCsv_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog {
            Title = "Select private moon CSV to include in encrypted backups",
            Filter = "CSV files (*.csv)|*.csv" };
        if (picker.ShowDialog(this) != true) return;
        await RunAsync(async () => {
            await _service.SelectPrivateMoonCsvAsync(picker.FileName);
            PrivateMoonCsvStatus.Text = picker.FileName;
            SetStatus("Private moon CSV selected for future encrypted backups.");
        });
    }

    private async void ExportEncrypted_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.SaveFileDialog {
            Title = "Export encrypted EVE Command Center backup",
            FileName = "EVE-Command-Center-" + DateTime.Now.ToString("yyyyMMdd") + ".ecc",
            Filter = "Encrypted Command Center backup (*.ecc)|*.ecc" };
        if (picker.ShowDialog(this) != true) return;
        await RunAsync(async () => {
            await _service.ExportEncryptedSnapshotAsync(picker.FileName, _lifetime.Token);
            SetStatus("Encrypted copy saved to your selected location.");
        });
    }

    private async void RestoreLocal_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog {
            Title = "Restore locally exported encrypted EVE Command Center backup",
            Filter = "Encrypted Command Center backup (*.ecc)|*.ecc" };
        if (picker.ShowDialog(this) != true) return;
        if (System.Windows.MessageBox.Show(this,
            "Restore this encrypted backup and restart? Existing files are copied into the dated RestoreBackups safety directory.",
            "Restore Encrypted Backup", MessageBoxButton.YesNo,
            MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        bool restored = false;
        await RunAsync(async () => {
            await _service.RestoreLocalEncryptedFileAsync(picker.FileName, _lifetime.Token);
            restored = true;
        }, refreshAfter: false);
        if (restored) _service.RestartApplication();
    }

    private async void SaveAutomation_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinutesBox.Text, out int minutes))
        {
            SetStatus("Enter a valid backup interval in minutes.", true);
            return;
        }
        await RunAsync(async () =>
        {
            await _service.SetAutoBackupAsync(
                AutoBackupCheck.IsChecked == true, minutes);
            CloudBackupCoordinator.RefreshSettings();
            MinutesBox.Text = _service.Settings.AutoBackupMinutes
                .ToString(CultureInfo.InvariantCulture);
            SetStatus("Your backup schedule has been saved. Only two cloud copies are kept.");
        });
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        await RunAsync(async () =>
        {
            var progress = new Progress<string>(SetStatus);
            await _service.BackupNowAsync(progress, _lifetime.Token);
        });
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                this,
                "Restore the newest cloud backup and restart EVE Command Center? " +
                "Current files are copied into a dated RestoreBackups safety folder first.",
                "Restore Cloud Backup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        bool restored = false;
        await RunAsync(async () =>
        {
            var progress = new Progress<string>(SetStatus);
            await _service.RestoreNowAsync(progress, _lifetime.Token,
                (RestorePointsBox.SelectedItem as CloudRestorePoint)?.Id);
            restored = true;
        }, refreshAfter: false);
        if (restored) _service.RestartApplication();
    }

    private void HistoryAudit_Click(object sender, RoutedEventArgs e)
    {
        var audit = new CloudHistoryAuditWindow(_service) { Owner = this };
        audit.ShowDialog();
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e) =>
        await RefreshStatusAsync();

    private async Task RunAsync(
        Func<Task> action,
        bool refreshAfter = true)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            await action();
            if (refreshAfter) await RefreshStatusAsync(skipBusy: true);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    private async Task RefreshStatusAsync(bool skipBusy = false)
    {
        if (!skipBusy) SetBusy(true);
        try
        {
            CloudBackupStatus status = await _service.GetStatusAsync(
                _lifetime.Token);
            _connected = status.IsConnected;
            _configured = status.IsConfigured;
            _hasRemoteBackup = status.HasRemoteBackup;
            GoogleStatusText.Text = status.IsConnected
                ? "Connected"
                : status.IsConfigured ? "Reconnect required" : "Setup required";
            GoogleStatusText.Foreground = Brush(
                status.IsConnected ? "#81C784" : "#FFB74D");
            RemoteBackupText.Text = !status.HasRemoteBackup
                ? "No backup found"
                : FormatDate(status.RemoteModifiedUtc) + " · " +
                  FormatBytes(status.RemoteSize);
            LastBackupText.Text = FormatDate(status.LastBackupUtc);
            if (status.IsConnected)
            {
                string? currentId = (RestorePointsBox.SelectedItem as CloudRestorePoint)?.Id;
                var points = await _service.GetRestorePointsAsync(_lifetime.Token);
                RestorePointsBox.ItemsSource = points;
                RestorePointsBox.DisplayMemberPath = nameof(CloudRestorePoint.Display);
                RestorePointsBox.SelectedItem = points.FirstOrDefault(p => p.Id == currentId)
                    ?? points.FirstOrDefault();
            }
            UpdateButtonStates();
        }
        catch (Exception ex)
        {
            SetStatus("Could not refresh cloud status: " + ex.Message, true);
        }
        finally { if (!skipBusy) SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtonStates();
    }

    private void UpdateButtonStates()
    {
        ConnectButton.IsEnabled = !_busy;
        BackupButton.IsEnabled = !_busy && _connected && _service.HasRecoveryPassword;
        RestoreButton.IsEnabled = !_busy && _connected && _hasRemoteBackup;
        ExportEncryptedButton.IsEnabled = !_busy && _service.HasRecoveryPassword;
        RestoreLocalButton.IsEnabled = !_busy && _service.HasRecoveryPassword;
        ReconnectButton.IsEnabled = !_busy && _configured;
        DisconnectButton.IsEnabled = !_busy && _connected;
    }

    private void SetStatus(string message) => SetStatus(message, false);
    private void SetStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = Brush(error ? "#EF5350" : "#8FB2B5");
    }

    private static string FormatDate(DateTimeOffset? value) =>
        value.HasValue
            ? value.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm")
            : "Never";
    private static string FormatBytes(long value) => value >= 1_048_576
        ? (value / 1_048_576.0).ToString("0.0") + " MB"
        : (value / 1024.0).ToString("0.0") + " KB";
    private static WpfBrush Brush(string value) =>
        (WpfBrush)new WpfBrushConverter().ConvertFromString(value)!;
}
