using System.Windows;
using System.Windows.Controls;
using EveCommandCenter.Interop;
using EveCommandCenter.Views;

internal static partial class Program
{
    private static void CheckControlStripRecovery(MiningFleetOverviewWindow overview, Window strip)
    {
        Check(!strip.ShowActivated, "Automatic control strip recovery does not request keyboard focus");
        strip.Hide();
        var foreground = User32.GetForegroundWindow();
        overview.MaintainControlTile();
        Check(strip.IsVisible && User32.GetForegroundWindow() == foreground,
            "A hidden control strip recovers without stealing foreground input");
        strip.WindowState = WindowState.Minimized;
        overview.MaintainControlTile();
        Check(strip.IsVisible && strip.WindowState == WindowState.Normal,
            "An accidentally minimized control strip recovers independently");
        double left = strip.Left, top = strip.Top;
        strip.Left = -50000; strip.Top = -50000;
        overview.MaintainControlTile();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(strip).Handle;
        User32.GetWindowRect(hwnd, out var bounds);
        Check(System.Windows.Forms.Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(
            System.Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom))),
            "Off-screen control bar recovers to a connected monitor");
        strip.Left = left; strip.Top = top;
        overview.SetOverviewHidden(true);
        overview.MaintainControlTile();
        Check(!strip.IsVisible && !overview.IsVisible, "Explicit hide remains respected during recovery");
        overview.ShowControlTile();

        using var source = new System.Windows.Forms.Form { Text = "Synthetic foreground client", TopMost = true };
        source.Show(); source.Activate();
        var cards = (ItemsControl)overview.FindName("MinerItems");
        var card = cards.Items[0];
        var sourceHwnd = card.GetType().GetProperty("SourceHwnd")!;
        sourceHwnd.SetValue(card, source.Handle);
        User32.SetWindowPos(hwnd, User32.HWND_NOTOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        foreground = User32.GetForegroundWindow();
        overview.UpdateActiveClient(source.Handle);
        Check((User32.GetWindowLong(hwnd, User32.GWL_EXSTYLE) & User32.WS_EX_TOPMOST) != 0 &&
            User32.GetForegroundWindow() == foreground,
            "Control strip repairs native topmost state above a foreground client without activating");
        sourceHwnd.SetValue(card, IntPtr.Zero);
    }
}
