using System.Text.Json;
using System.Text.Json.Nodes;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using EveCommandCenter.Views;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

internal static partial class Program
{
    private static void CheckRetiredPreviewSettings(string? render = null)
    {
        var settings = new AppSettings { GlobalCycleForwardHotkey = "^F12", AutoLaunchCharacterOverview = true };
        settings.Profiles["Default"].PerClientAudioVolume["Pilot"] = 42;
        var old = JsonNode.Parse(JsonSerializer.Serialize(AhkConfigRoot.FromAppSettings(settings)))!;
        var globals = old["global_Settings"]!;
        globals["StaticThumbnails"] = 1;
        globals["SuspendThumbnailsWhenBackground"] = 1;
        globals["StatWindowPositions"] = new JsonObject { ["Pilot"] = new JsonObject { ["X"] = 100 } };
        globals["ThumbnailStartLocation"] = new JsonObject { ["Width"] = 200 };
        old["_Profiles"]!["Default"]!["Secondary Thumbnails"] = new JsonObject {
            ["Pilot"] = new JsonObject { ["enabled"] = 1, ["x"] = 500 } };
        var migrated = JsonSerializer.Deserialize<AhkConfigRoot>(old.ToJsonString())!.ToAppSettings();
        Check(migrated.GlobalCycleForwardHotkey == "^F12" && migrated.AutoLaunchCharacterOverview &&
            migrated.Profiles["Default"].PerClientAudioVolume["Pilot"] == 42,
            "Legacy preview fields load without losing current hotkeys, startup preferences or audio");
        var saved = JsonSerializer.Serialize(AhkConfigRoot.FromAppSettings(migrated));
        Check(new[] { "Secondary Thumbnails", "StaticThumbnails", "SuspendThumbnailsWhenBackground", "StatWindowPositions", "ThumbnailStartLocation" }
            .All(name => !saved.Contains('"' + name + '"')),
            "Saving migrated settings drops retired standalone preview and stat-window configuration");
        var prefs = JsonSerializer.Deserialize<MiningDashboardPreferences>(
            "{\"ControlTileSettingsExpanded\":true,\"ControlTileWidth\":260,\"CompactControlTileApplied\":true,\"ControlTileX\":123,\"ControlTileY\":456,\"ShowMiningHolds\":false}")!;
        var savedPrefs = JsonSerializer.Serialize(prefs);
        Check(prefs.ControlTileX == 123 && prefs.ControlTileY == 456 && !prefs.ShowMiningHolds &&
            !savedPrefs.Contains("ControlTileWidth") && !savedPrefs.Contains("ControlTileSettingsExpanded"),
            "Former compact deck settings retire while current bar position and mining choices survive");

        string config = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ecc-setup-check-" + Guid.NewGuid().ToString("N") + ".json");
        using var service = new SettingsService(config);
        if (render != null) System.Windows.Application.Current.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("/EVE Command Center;component/Resources/Strings.en.xaml", UriKind.Relative) });
        var wizard = new SetupWizard(service);
        try
        {
            var next = typeof(SetupWizard).GetMethod("OnNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
            next.Invoke(wizard, new object[] { wizard, new RoutedEventArgs() });
            Check(((FrameworkElement)wizard.FindName("Step3")).Visibility == Visibility.Visible,
                "First-run setup proceeds straight from welcome to usable hotkeys");
            ((TextBox)wizard.FindName("TxtWizChar1")).Text = "Pilot";
            ((TextBox)wizard.FindName("TxtWizKey1")).Text = "^F10";
            next.Invoke(wizard, new object[] { wizard, new RoutedEventArgs() });
            next.Invoke(wizard, new object[] { wizard, new RoutedEventArgs() });
            Check(((FrameworkElement)wizard.FindName("Step5")).Visibility == Visibility.Visible &&
                ((Button)wizard.FindName("BtnNext")).Content?.ToString() == "Done" &&
                ((TextBlock)wizard.FindName("TxtSummary")).Text.Contains("^F10"),
                "Four-step setup reaches a summary of the current hotkeys");
            if (render != null) Render(wizard, render);
            next.Invoke(wizard, new object[] { wizard, new RoutedEventArgs() });
            Check(service.Settings.SetupCompleted && service.CurrentProfile.Hotkeys["Pilot"].Key == "^F10",
                "Finishing the simplified setup persists its hotkeys and completion flag");
        }
        finally
        {
            wizard.Close();
            if (System.IO.File.Exists(config)) System.IO.File.Delete(config);
            if (System.IO.File.Exists(config + ".bak")) System.IO.File.Delete(config + ".bak");
        }
    }
}
