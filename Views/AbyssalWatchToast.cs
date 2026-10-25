using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EveCommandCenter.Services;
// WPF controls are explicit because UseWindowsForms is enabled in the project.
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace EveCommandCenter.Views;

public static class AbyssalWatchToast
{
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "mutamarket.com" ||
            !uri.AbsolutePath.StartsWith("/modules/", StringComparison.Ordinal)) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static void Show(IReadOnlyList<AbyssalWatchListing> listings, bool preview = false)
    {
        if (listings.Count == 0) return;
        var best = listings.OrderByDescending(x => x.EffectiveSpeed).First();
        var win = new Window {
            Title = preview ? "Abyssal Market Watch - Alert Preview" : "Abyssal Market Watch", Width = 405, Height = 183,
            WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
            Topmost = true, ShowActivated = false, ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(10, 30, 34)),
            Foreground = Brushes.WhiteSmoke, WindowStartupLocation = WindowStartupLocation.Manual
        };
        win.Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - win.Width - 22);
        win.Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - win.Height - 25);
        var box = new StackPanel { Margin = new Thickness(15) };
        win.Content = box;
        box.Children.Add(new TextBlock { Text = preview ? "ALERT PREVIEW - EXISTING MATCH" : listings.Count == 1 ? "NEW ABYSSAL STRIP MINER MATCH" : $"{listings.Count} NEW ABYSSAL STRIP MINER MATCHES", FontWeight = FontWeights.Bold, Foreground = Brushes.Gold });
        box.Children.Add(new TextBlock { Text = $"{best.Source}  |  {best.PriceText}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 3) });
        box.Children.Add(new TextBlock { Text = best.QualityText, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        box.Children.Add(actions);
        var open = new Button { Content = "OPEN ON MUTAMARKET", Padding = new Thickness(8, 4, 8, 4) };
        open.Click += (_, _) => { OpenUrl(best.Url); win.Close(); };
        var review = new Button { Content = "VIEW ALL", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(8, 0, 0, 0) };
        review.Click += (_, _) => {
            var market = Application.Current.Windows.OfType<MarketOrdersWindow>().FirstOrDefault();
            if (market == null)
            {
                (Application.Current as App)?.OpenCommandCenterModule("market");
                market = Application.Current.Windows.OfType<MarketOrdersWindow>().FirstOrDefault();
            }
            market?.OpenAbyssalTab();
            win.Close();
        };
        var dismiss = new Button { Content = "DISMISS", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(8, 0, 0, 0) };
        dismiss.Click += (_, _) => win.Close();
        actions.Children.Add(open); actions.Children.Add(review); actions.Children.Add(dismiss);
        win.Show();
    }
}
