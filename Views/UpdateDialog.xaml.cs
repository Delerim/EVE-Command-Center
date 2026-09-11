using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

/// <summary>Release overview shared by startup and manual update checks.</summary>
public partial class UpdateDialog : Window
{
    private readonly UpdateService _updateService;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _downloading;

    public UpdateDialog(UpdateService updateService)
    {
        InitializeComponent();
        _updateService = updateService;
        TxtCurrentVersion.Text = "v" + updateService.CurrentVersion;
        TxtLatestVersion.Text = updateService.LatestVersion is { } latest ? "v" + latest : "Unknown";
        BtnUpdateNow.IsEnabled = updateService.UpdateAvailable;
        BtnReleaseNotes.IsEnabled = !string.IsNullOrWhiteSpace(updateService.ReleasePageUrl);
        NotesViewer.Document = BuildNotes(updateService.ReleaseNotes);
        Closed += (_, _) => { _lifetime.Cancel(); _lifetime.Dispose(); };
    }

    private static FlowDocument BuildNotes(string? notes)
    {
        var document = new FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 13,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDD, 0xF6, 0xF4)),
            PagePadding = new Thickness(0),
        };
        foreach (var raw in (string.IsNullOrWhiteSpace(notes) ? "No release notes were provided for this version." : notes).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            bool heading = line.StartsWith('#');
            line = heading ? line.TrimStart('#', ' ') : Regex.Replace(line, @"^[-*]\s+", "\u2022  ");
            var paragraph = new Paragraph { Margin = new Thickness(0, heading ? 10 : 0, 0, 8) };
            if (heading) { paragraph.FontSize = 16; paragraph.FontWeight = FontWeights.SemiBold; }
            // Support headings, bullets, inline code and bold without interpreting HTML or executing links.
            foreach (var part in Regex.Split(line, @"(\*\*.*?\*\*|`[^`]+`)") )
            {
                if (part.StartsWith("**") && part.EndsWith("**") && part.Length >= 4)
                    paragraph.Inlines.Add(new Bold(new Run(part[2..^2])));
                else if (part.StartsWith('`') && part.EndsWith('`') && part.Length >= 2)
                    paragraph.Inlines.Add(new Run(part[1..^1]) { FontFamily = new System.Windows.Media.FontFamily("Consolas") });
                else paragraph.Inlines.Add(new Run(part));
            }
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    private void OnReleaseNotes(object sender, RoutedEventArgs e)
    {
        if (_updateService.ReleasePageUrl is not { } url) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { ShowStatus("Could not open the release page: " + ex.Message); }
    }

    private void OnLater(object sender, RoutedEventArgs e) => Close();

    private void ShowStatus(string message)
    {
        TxtStatus.Text = message;
        TxtStatus.Visibility = Visibility.Visible;
    }

    private async void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        if (_downloading) return;
        var token = _lifetime.Token;
        _downloading = true;
        BtnUpdateNow.IsEnabled = false;
        BtnUpdateNow.Content = "DOWNLOADING...";
        TxtStatus.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        DownloadProgress.IsIndeterminate = true;
        TxtProgressStatus.Text = "Preparing download... You can still skip and continue.";
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (token.IsCancellationRequested) return;
                DownloadProgress.IsIndeterminate = false;
                DownloadProgress.Value = value * 100;
                TxtProgressStatus.Text = $"Downloading update - {value:P0}";
            });
            var path = await _updateService.DownloadUpdateAsync(progress, token);
            token.ThrowIfCancellationRequested();
            BtnLater.IsEnabled = false;
            ShowStatus("Download complete. Restarting with the updated version...");
            _updateService.ApplyUpdate(path);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            _downloading = false;
            BtnUpdateNow.IsEnabled = true;
            BtnUpdateNow.Content = "RETRY UPDATE";
            BtnLater.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
            ShowStatus("Update failed. You can retry or skip and continue. " + ex.Message);
            Debug.WriteLine($"[Update] Download/apply failed: {ex}");
        }
    }
}
