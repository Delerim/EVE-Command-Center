using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using EveCommandCenter.Interop;
using EveCommandCenter.Services;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckWindowLayouts()
    {
        var original = new SavedWindowLayout { Left = 2200, Top = 120, Width = 1100, Height = 800, WorkspaceLeft = 1920, WorkspaceTop = 0, Dpi = 96 };
        var same = WindowLayoutService.Fit(original, new System.Drawing.Rectangle(1920, 0, 1920, 1040), 96, 600, 400);
        Check(same.Left == 2200 && same.Top == 120 && same.Width == 1100 && same.Height == 800, "Panel geometry round-trips on a secondary monitor");
        var smaller = WindowLayoutService.Fit(original, new System.Drawing.Rectangle(0, 0, 1000, 700), 96, 600, 400);
        Check(smaller.Left == 0 && smaller.Top == 0 && smaller.Width == 1000 && smaller.Height == 700, "Disconnected or smaller monitor keeps the saved panel reachable");
        var scaled = WindowLayoutService.Fit(original, new System.Drawing.Rectangle(-3840, 0, 3840, 2100), 192, 600, 400);
        Check(scaled.Width == 2200 && scaled.Height == 1600 && scaled.Left == -3280, "Panel geometry adapts to monitor DPI and negative desktop coordinates");
        Check(WindowLayoutService.ShouldRemember(typeof(PlanetaryWindow)) && WindowLayoutService.ShouldRemember(typeof(PilotCommandCenterWindow))
            && WindowLayoutService.ShouldRemember(typeof(SettingsWindow)) && WindowLayoutService.ShouldRemember(typeof(SkillPlannerWindow))
            && !WindowLayoutService.ShouldRemember(typeof(MiningFleetOverviewWindow)) && !WindowLayoutService.ShouldRemember(typeof(OperatingToast)),
            "Panel persistence covers dashboards without overriding overlay placement");
        string directory = Path.Combine(Path.GetTempPath(), "ecc-layout-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "window-layouts.json");
        File.WriteAllText(path, "invalid json");
        using (var corrupt = new WindowLayoutService(directory)) Check(corrupt.Get("panel") == null, "Damaged panel settings fall back to defaults");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, SavedWindowLayout> { ["panel"] = original }));
        using (var reload = new WindowLayoutService(directory)) Check(reload.Get("panel") == original, "Panel settings survive service recreation");
        // Real native placements on isolated test windows; no EVE clients are touched.
        using var service = new WindowLayoutService(directory);
        var window = new Window { Title = "Layout regression check", Width = 620, Height = 440, Left = 100, Top = 100, ShowInTaskbar = false };
        service.Attach(window, "native");
        window.Show();
        window.Left = 140; window.Top = 130; window.Width = 680; window.Height = 460;
        window.UpdateLayout();
        service.Flush();
        var saved = service.Get("native");
        Check(saved != null && saved.Width > 0 && saved.Height > 0, "Native panel movement and resizing record restore bounds");
        window.WindowState = WindowState.Maximized;
        window.WindowState = WindowState.Minimized;
        service.Flush();
        var minimized = service.Get("native");
        Check(minimized?.Maximized == true && minimized.Width == saved!.Width && minimized.Height == saved.Height,
            "Minimising a maximised panel preserves its normal size and maximised preference");
        window.Close();
        using var reopened = new WindowLayoutService(directory);
        var second = new Window { Title = "Different title, same panel", Width = 500, Height = 350, ShowInTaskbar = false };
        reopened.Attach(second, "native");
        second.Show(); second.UpdateLayout();
        Check(second.WindowState == WindowState.Maximized, "Reopening restores maximised state without reopening minimised");
        second.WindowState = WindowState.Normal; second.UpdateLayout();
        var restored = reopened.Get("native");
        Check(restored?.Width == saved!.Width && restored.Height == saved.Height, "Restored normal bounds survive maximisation and restart");
        second.Close();
        WindowLayoutService.Install(reopened);
        var automatic = new LayoutTestWindow { Title = "Automatic layout check", Width = 610, Height = 420, ShowInTaskbar = false };
        automatic.Content = new System.Windows.Controls.Border();
        automatic.Show();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        automatic.Left = 160; automatic.Top = 150; automatic.Width = 640; automatic.UpdateLayout();
        reopened.Flush();
        var automaticSaved = reopened.Get(typeof(LayoutTestWindow).FullName!);
        Check(automaticSaved != null, "Global window-loaded hook automatically remembers new panel types");
        automatic.Close();
        var automaticAgain = new LayoutTestWindow { Title = "New title", Width = 420, Height = 300, ShowInTaskbar = false };
        automaticAgain.Content = new System.Windows.Controls.Border();
        automaticAgain.Show();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        automaticAgain.UpdateLayout();
        var actual = new User32.WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<User32.WINDOWPLACEMENT>() };
        User32.GetWindowPlacement(new WindowInteropHelper(automaticAgain).Handle, ref actual);
        Check(actual.rcNormalPosition.Right - actual.rcNormalPosition.Left == automaticSaved!.Width,
            "Automatically reopened panels use saved size instead of constructor defaults");
        automaticAgain.Close();
    }
}

namespace EveCommandCenter.Views { internal sealed class LayoutTestWindow : System.Windows.Window { } }
