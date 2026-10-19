using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using EveCommandCenter.Services;
using EveCommandCenter.Views;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static void CheckCombinedPreviewSettingsLifecycle()
    {
        using var source = new Forms.Form
        {
            Text = "Synthetic preview source",
            Width = 280,
            Height = 180,
            StartPosition = Forms.FormStartPosition.Manual,
            Left = 250,
            Top = 170
        };
        source.Show();
        using var settings = new SettingsService(Path.Combine(Path.GetTempPath(),
            "ecc-combined-preview-" + Guid.NewGuid().ToString("N") + ".json"));
        settings.Settings.StatOverlayEnabled = true;
        using var manager = new ThumbnailManager(new WindowDiscoveryService(), settings);
        var preview = new ThumbnailWindow();
        preview.Initialize(source.Handle, "Synthetic miner", 180, 120, 40, 80);
        _ = preview.Handle;
        var text = (TextOverlayWindow)typeof(ThumbnailWindow)
            .GetField("_textOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(preview)!;
        var primaries = (ConcurrentDictionary<IntPtr, ThumbnailWindow>)typeof(ThumbnailManager)
            .GetField("_thumbnails", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        primaries[source.Handle] = preview;
        var stats = new StatOverlayWindow();
        stats.Initialize("Synthetic miner", "Stats", 40, 230);
        var statWindows = (ConcurrentDictionary<string, StatOverlayWindow>)typeof(ThumbnailManager)
            .GetField("_statWindows", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        statWindows["Synthetic miner"] = stats;

        preview.ShowWithOverlay();
        stats.Show();
        Check(preview.IsVisible && text.IsVisible && stats.IsVisible,
            "Synthetic separate preview starts with native, text and stat windows visible");
        manager.SetOverviewCombined(true, false);
        Check(!preview.IsVisible && !text.IsVisible && !stats.IsVisible,
            "Combined mode hides all three independently owned preview windows");

        // Simulate a stale WPF overlay despite the WinForms body being hidden.
        text.Show();
        Check(!preview.IsVisible && text.IsVisible,
            "Synthetic fixture reproduces orphaned text layer seen on EVE clients");
        manager.SetSettingsOpen(true);
        Check(!preview.IsVisible && !text.IsVisible && !stats.IsVisible,
            "Opening embedded Settings removes an orphaned text layer");
        manager.ReapplySettings();
        Check(!preview.IsVisible && !text.IsVisible && !stats.IsVisible,
            "Applying Settings cannot resurrect separate previews under Combined mode");
        manager.SetSettingsOpen(false);
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Check(!preview.IsVisible && !text.IsVisible && !stats.IsVisible,
            "Moving focus to Backup or returning from Settings keeps Combined mode isolated");

        manager.SetOverviewCombined(false, false);
        Check(preview.IsVisible && text.IsVisible && stats.IsVisible,
            "Switching to Separate restores native, text and stat windows");
        manager.SetOverviewCombined(true, false);
        Check(!preview.IsVisible && !text.IsVisible && !stats.IsVisible,
            "Switching back to Combined hides legacy windows without leaving labels behind");
    }
}
