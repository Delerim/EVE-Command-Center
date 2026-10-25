using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EveCommandCenter.Services;
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using Border = System.Windows.Controls.Border;
using DockPanel = System.Windows.Controls.DockPanel;
using Orientation = System.Windows.Controls.Orientation;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace EveCommandCenter.Views;

public static class AbyssalWatchToast
{
    private static Window? _current;
    private static SolidColorBrush Ink(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static readonly SolidColorBrush Teal = Ink(113, 228, 196);
    private static readonly SolidColorBrush Muted = Ink(151, 188, 190);
    private static readonly SolidColorBrush Amber = Ink(248, 205, 117);

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "mutamarket.com" ||
            !uri.AbsolutePath.StartsWith("/modules/", StringComparison.Ordinal)) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private static Button Action(string title, bool primary)
    {
        return new Button {
            Content = title, Height = 30, Padding = new Thickness(11, 5, 11, 5),
            FontSize = 11, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 7, 0),
            Background = primary ? Ink(30, 116, 99) : Ink(19, 54, 59),
            BorderBrush = primary ? Teal : Ink(65, 112, 110),
            Foreground = Brushes.WhiteSmoke, BorderThickness = new Thickness(1)
        };
    }

    private static TextBlock Stat(string label, string value, SolidColorBrush color)
    {
        return new TextBlock {
            Text = label + "  " + value, Foreground = color, FontSize = 12,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static void Show(IReadOnlyList<AbyssalWatchListing> listings, bool preview = false)
    {
        if (listings.Count == 0) return;
        var best = listings.OrderByDescending(x => x.EffectiveSpeed).First();
        // One non-blocking toast at a time. A batch reports its count, not one
        // separate popup per contract, and never opens another full-sized window.
        if (_current != null) _current.Close();
        var win = new Window {
            Title = preview ? "Abyssal Hunter - preview" : "Abyssal Hunter - new listing",
            Width = 500, Height = 255,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            Topmost = true, ShowActivated = false, ShowInTaskbar = false,
            AllowsTransparency = true, Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        _current = win;
        win.Closed += (_, _) => { if (ReferenceEquals(_current, win)) _current = null; };
        win.Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - win.Width - 16);
        win.Top = Math.Max(SystemParameters.WorkArea.Top, SystemParameters.WorkArea.Bottom - win.Height - 16);

        var shell = new Border {
            Background = Ink(9, 29, 34), BorderBrush = Teal,
            BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(17, 14, 17, 13)
        };
        win.Content = shell;
        var content = new StackPanel(); shell.Child = content;
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        content.Children.Add(heading);
        var close = Action("×", false);
        close.Height = 25; close.Width = 29; close.Margin = new Thickness(7, 0, 0, 0);
        close.Click += (_, _) => win.Close();
        DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        var title = new StackPanel(); heading.Children.Add(title);
        title.Children.Add(new TextBlock {
            Text = preview ? "ALERT PREVIEW" : listings.Count == 1 ? "NEW ABYSSAL OPPORTUNITY" : $"{listings.Count} ABYSSAL OPPORTUNITIES",
            Foreground = Amber, FontWeight = FontWeights.Bold, FontSize = 11
        });
        title.Children.Add(new TextBlock {
            Text = best.Source, Foreground = Brushes.WhiteSmoke,
            FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0)
        });
        content.Children.Add(new TextBlock {
            Text = best.MutaplasmidName, Foreground = Muted, FontSize = 11,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var metrics = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        content.Children.Add(metrics);
        metrics.Children.Add(Stat("YIELD", best.MiningAmountText + " m³", Teal));
        metrics.Children.Add(Stat("EFF.", best.EffectiveSpeedText + " m³/s", Teal));
        metrics.Children.Add(Stat("CPU", best.CpuDeltaText, best.CpuPenalty <= 2 ? Teal : Amber));
        metrics.Children.Add(Stat("PG", best.PowergridDeltaText, best.PowergridPenalty <= 2 ? Teal : Amber));
        var priceBand = new Border {
            Background = Ink(18, 49, 53), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8), Margin = new Thickness(0, 0, 0, 11)
        };
        content.Children.Add(priceBand);
        priceBand.Child = new TextBlock {
            Text = $"ASK  {best.PriceText}     ESTIMATE  {best.EstimateText}     GAP  {best.PriceAdvantageText}",
            Foreground = Amber, FontSize = 12, FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(actions);
        var open = Action("OPEN MODULE", true);
        open.Click += (_, _) => { OpenUrl(best.Url); win.Close(); };
        var review = Action("VIEW IN HUNTER", false);
        review.Click += (_, _) => {
            var market = Application.Current.Windows.OfType<MarketOrdersWindow>().FirstOrDefault();
            if (market == null)
            {
                (Application.Current as App)?.OpenCommandCenterModule("market");
                market = Application.Current.Windows.OfType<MarketOrdersWindow>().FirstOrDefault();
            }
            market?.OpenAbyssalTab();
            market?.Activate();
            win.Close();
        };
        var dismiss = Action("DISMISS", false);
        dismiss.Click += (_, _) => win.Close();
        actions.Children.Add(open); actions.Children.Add(review); actions.Children.Add(dismiss);
        win.Show();
    }
}
