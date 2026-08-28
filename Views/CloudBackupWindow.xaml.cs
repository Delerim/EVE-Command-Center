using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushConverter = System.Windows.Media.BrushConverter;

namespace EveMultiPreview.Views;

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
                await _service.RestoreNowAsync(progress, _lifetime.Token);
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
            SetStatus("Automatic backup settings saved.");
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
            await _service.RestoreNowAsync(progress, _lifetime.Token);
            restored = true;
        }, refreshAfter: false);
        if (restored) _service.RestartApplication();
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
        BackupButton.IsEnabled = !_busy && _connected;
        RestoreButton.IsEnabled = !_busy && _connected && _hasRemoteBackup;
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
