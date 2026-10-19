using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows.Data;
using System.Windows.Media;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

/// <summary>Read-only backup coverage and isolated archive verification.</summary>
public sealed class CloudHistoryAuditWindow : Window
{
    private readonly CloudBackupService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DataGrid _grid;
    private readonly TextBlock _summary;
    private readonly TextBox _warnings;
    private readonly TextBlock _status;
    private readonly Button _scan;
    private readonly Button _verify;
    private bool _busy;

    public CloudHistoryAuditWindow(CloudBackupService service)
    {
        _service = service;
        Title = "EVE Command Center · Historical Data & Recovery Audit";
        Width = 940; Height = 690; MinWidth = 770; MinHeight = 570;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#07181B"); Foreground = Brush("#EAF7F7");
        FontFamily = new System.Windows.Media.FontFamily("Segoe UI");

        var shell = new Grid { Margin = new Thickness(20) };
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(135) });
        shell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = shell;

        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        title.Children.Add(new TextBlock { Text = "HISTORICAL DATA & RECOVERY AUDIT", FontSize = 23, FontWeight = FontWeights.Bold });
        title.Children.Add(new TextBlock {
            Text = "Read-only inventory of files selected for backup. Dates come from saved record fields only; no historical activity is inferred.",
            Foreground = Brush("#8FB2B5"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0)
        });
        Grid.SetRow(title, 0); shell.Children.Add(title);

        _summary = new TextBlock { Text = "Not scanned", FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("#55D7D2"), Margin = new Thickness(0, 0, 0, 12) };
        Grid.SetRow(_summary, 1); shell.Children.Add(_summary);

        _grid = new DataGrid {
            AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            Background = Brush("#0C2529"), Foreground = Brush("#EAF7F7"),
            RowBackground = Brush("#0C2529"), AlternatingRowBackground = Brush("#12383D"),
            BorderBrush = Brush("#245058"), GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            Margin = new Thickness(0, 0, 0, 10)
        };
        _grid.Columns.Add(Column("Historical category", "Name", new DataGridLength(2, DataGridLengthUnitType.Star)));
        _grid.Columns.Add(Column("Files", "Files", new DataGridLength(70)));
        _grid.Columns.Add(Column("Size", "Size", new DataGridLength(95)));
        _grid.Columns.Add(Column("Earliest record", "Earliest", new DataGridLength(125)));
        _grid.Columns.Add(Column("Latest record", "Latest", new DataGridLength(125)));
        _grid.Columns.Add(Column("Dated files", "FilesWithRecordDates", new DataGridLength(95)));
        Grid.SetRow(_grid, 2); shell.Children.Add(_grid);

        _warnings = new TextBox { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush("#0C2529"), Foreground = Brush("#C6DEE0"), BorderBrush = Brush("#245058"),
            Padding = new Thickness(9), FontSize = 12,
            Text = "Warnings and scope limitations will appear here." };
        Grid.SetRow(_warnings, 3); shell.Children.Add(_warnings);

        var actions = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        var buttons = new WrapPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        _scan = MakeButton("SCAN SAVED HISTORY");
        _verify = MakeButton("VERIFY LOCAL ENCRYPTED BACKUP");
        _scan.Click += async (_, _) => await ScanAsync();
        _verify.Click += async (_, _) => await VerifyAsync();
        buttons.Children.Add(_scan); buttons.Children.Add(_verify);
        DockPanel.SetDock(buttons, Dock.Left); actions.Children.Add(buttons);
        _status = new TextBlock { Text = "Ready", Foreground = Brush("#8FB2B5"),
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(12, 0, 0, 0) };
        actions.Children.Add(_status);
        Grid.SetRow(actions, 4); shell.Children.Add(actions);
        Loaded += async (_, _) => await ScanAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private static DataGridTextColumn Column(string title, string path, DataGridLength width) => new()
    { Header = title, Binding = new System.Windows.Data.Binding(path), Width = width };
    private static SolidColorBrush Brush(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    private static Button MakeButton(string text) => new()
    {
        Content = text, Background = Brush("#12383D"), Foreground = Brush("#EAF7F7"),
        BorderBrush = Brush("#39796C"), FontWeight = FontWeights.SemiBold,
        Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private async Task ScanAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            HistoryAuditReport report = await _service.AuditSavedHistoryAsync(_lifetime.Token);
            if (_lifetime.IsCancellationRequested) return;
            _grid.ItemsSource = report.Categories;
            _summary.Text = $"{report.Files:N0} included files · {report.Size} · " +
                $"{report.ExcludedGameLogFiles:N0} external EVE game-log files ({CloudHistoryAuditService.SizeText(report.ExcludedGameLogBytes)}) outside automatic backup";
            _warnings.Text = string.Join(Environment.NewLine, report.Warnings.Select(w => "• " + w));
            _status.Text = $"Local scan: {report.CheckedLiveSqliteDatabases:N0} SQLite databases passed quick_check. No files changed.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _status.Text = "Audit incomplete: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private async Task VerifyAsync()
    {
        if (_busy) return;
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Verify an existing encrypted Command Center backup without restoring it",
            Filter = "Encrypted Command Center backup (*.ecc)|*.ecc"
        };
        if (picker.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            _status.Text = "Verifying contents in an isolated temporary directory...";
            CloudArchiveVerification result = await _service.VerifyLocalEncryptedCopyAsync(picker.FileName, _lifetime.Token);
            _status.Text = result.Summary;
            _warnings.Text = "Archive checks passed: manifest sizes, SHA-256 hashes and SQLite integrity where applicable. " +
                "Files were materialized in an isolated temporary directory, not restored into the app. " +
                "A full disaster-recovery test on another PC is still required.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _status.Text = "Archive NOT verified: " + ex.Message; }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _scan.IsEnabled = !busy;
        _verify.IsEnabled = !busy;
        if (busy) _status.Text = "Working...";
    }
}
