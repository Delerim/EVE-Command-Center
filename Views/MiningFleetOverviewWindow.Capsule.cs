using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EveCommandCenter.Services;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using TextBlock = System.Windows.Controls.TextBlock;
using WrapPanel = System.Windows.Controls.WrapPanel;
using Grid = System.Windows.Controls.Grid;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;
using Orientation = System.Windows.Controls.Orientation;

namespace EveCommandCenter.Views;

public partial class MiningFleetOverviewWindow
{
    private WrapPanel? _controlCapsule;
    private TextBlock? _capsulePlex;
    private TextBlock? _capsuleToday;
    private TextBlock? _capsuleMode;
    private Border? _capsuleContract;
    private Button? _capsuleAlarm;
    private Button? _capsuleMute;

    private static TextBlock CapsuleLabel(string text, string color = "#C7E9E5") => new()
    {
        Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
        Foreground = (Brush)new BrushConverter().ConvertFromString(color)!,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 5, 0)
    };

    private static Button CapsuleButton(string text, string hint) => new()
    {
        Content = text, FontSize = 9, Padding = new Thickness(6, 2, 6, 2),
        Margin = new Thickness(2, 0, 2, 0), Height = 22,
        VerticalAlignment = VerticalAlignment.Center, ToolTip = hint
    };

    private void InstallControlCapsule(Window toolbar)
    {
        if (toolbar.Content is not Border shell) return;
        // The real toolbar still owns all bindings and events. It is simply
        // hidden when the compact capsule is shown, never recreated.
        shell.Child = null;
        var host = new Grid();
        host.Children.Add(OverviewHeader);
        _controlCapsule = new WrapPanel { Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center, ItemHeight = 25,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        _controlCapsule.Children.Add(CapsuleLabel("EVE CONTROL", "#ECF8F5"));
        _controlCapsule.Children.Add(CapsuleLabel("LIVE", "#68E1BE"));
        _capsulePlex = CapsuleLabel("PLEX --", "#8EC9E8");
        _controlCapsule.Children.Add(_capsulePlex);
        _capsuleToday = CapsuleLabel("TODAY AVG --", "#68DAB8");
        _controlCapsule.Children.Add(_capsuleToday);
        _capsuleContract = new Border { Width = 15, Height = 15, CornerRadius = new CornerRadius(8),
            Background = Brushes.Gold, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed, ToolTip = "Contracts awaiting acceptance",
            Child = new TextBlock { Text = "!", FontSize = 10, FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black, TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center } };
        _controlCapsule.Children.Add(_capsuleContract);

        _capsuleAlarm = CapsuleButton("ALARMS OFF", "Mute or re-enable all mining alarms");
        _capsuleAlarm.Click += ToggleAllAlarms;
        _controlCapsule.Children.Add(_capsuleAlarm);
        _capsuleMute = CapsuleButton("MUTE EVE", "Mute or unmute EVE clients");
        _capsuleMute.Click += MuteAllClients_Click;
        _controlCapsule.Children.Add(_capsuleMute);
        var mode = CapsuleButton("MODE", "Choose the preview mode");
        var choices = new ContextMenu();
        foreach (var option in new[] { ("Characters", "CHARACTERS"), ("Mining", "MINING"),
                     ("PvE", "PVE"), ("PvP", "PVP") })
        {
            var item = new MenuItem { Header = option.Item1, Tag = option.Item2 };
            item.Click += OverviewMode_Click;
            choices.Items.Add(item);
        }
        mode.ContextMenu = choices;
        mode.Click += (_, _) => { choices.PlacementTarget = mode; choices.IsOpen = true; };
        _capsuleMode = CapsuleLabel("MINING");
        _controlCapsule.Children.Add(mode);
        _controlCapsule.Children.Add(_capsuleMode);
        var more = CapsuleButton("MORE >>", "Expand the complete EVE Command Center controls");
        more.Click += (_, _) => SetControlCapsuleExpanded(true, true);
        _controlCapsule.Children.Add(more);
        host.Children.Add(_controlCapsule);
        shell.Child = host;

        // Both legacy action rows retain their handlers and settings menus.
        foreach (var actions in new[] { FullActions, CompactActions })
        {
            var compact = CapsuleButton("COMPACT", "Return to the narrow control capsule");
            compact.Click += (_, _) => SetControlCapsuleExpanded(false, true);
            actions.Children.Add(compact);
        }
        SetControlCapsuleExpanded(_prefs.ControlCapsuleExpanded, false);
        RefreshControlCapsule();
    }

    private void SetControlCapsuleExpanded(bool expanded, bool save)
    {
        _prefs.ControlCapsuleExpanded = expanded;
        RefreshControlCapsuleVisibility();
        if (save) MiningDashboardPreferencesStore.Save(_prefs);
    }

    private void RefreshControlCapsuleVisibility()
    {
        if (_controlTile == null || _controlCapsule == null) return;
        bool expanded = _prefs.ControlCapsuleExpanded;
        OverviewHeader.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _controlCapsule.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        _controlTile.Width = expanded
            ? Math.Min(1850, Math.Max(820, SystemParameters.WorkArea.Width - 52))
            : Math.Min(800, Math.Max(520, SystemParameters.WorkArea.Width - 26));
        RefreshControlCapsule();
    }

    private void RefreshControlCapsule()
    {
        if (_controlCapsule == null) return;
        if (_capsulePlex != null) _capsulePlex.Text = "PLEX " + PlexBuyText.Text;
        if (_capsuleToday != null) _capsuleToday.Text = FleetTodayProfit.Text.Replace(" ISK", "");
        if (_capsuleContract != null)
        {
            _capsuleContract.Visibility = PendingContractsBadge.Visibility;
            _capsuleContract.ToolTip = PendingContractsBadge.ToolTip;
        }
        if (_capsuleAlarm != null) {
            _capsuleAlarm.Content = CompactAllAlarmsButton.Content;
            _capsuleAlarm.Foreground = CompactAllAlarmsButton.Foreground;
        }
        if (_capsuleMute != null) _capsuleMute.Content = CompactMuteButton.Content;
        if (_capsuleMode != null) _capsuleMode.Text = OverviewMode;
    }
}
