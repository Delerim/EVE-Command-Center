using System;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
// Explicit WPF aliases: ECC enables Windows Forms too, so these names are otherwise ambiguous.
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using System.Windows.Media;
using EveCommandCenter.Models;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

/// <summary>Non-expiring reminder; acknowledgement is deliberate and persisted by pilot/remap.</summary>
public sealed class SkillPlanRemapReminderWindow : Window
{
    private readonly ListBox _alerts = new() { Margin = new Thickness(0, 12, 0, 12), FontSize = 13 };
    private readonly Button _ack = new() { Content = "ACKNOWLEDGE SELECTED", Padding = new Thickness(12, 8, 12, 8) };
    private (EvePilotProfile Pilot, SkillRemapAssessment Alert)[] _data = Array.Empty<(EvePilotProfile, SkillRemapAssessment)>();
    private Action<EvePilotProfile, SkillImportRemap>? _onAck;

    public SkillPlanRemapReminderWindow()
    {
        Title = "EVE Command Center | REMAP ACTION REQUIRED";
        Width = 590; Height = 320; MinWidth = 480; MinHeight = 250;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(10, 32, 37));
        Foreground = Brushes.WhiteSmoke;
        ShowInTaskbar = true;
        var shell = new DockPanel { Margin = new Thickness(18) }; Content = shell;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); shell.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "ATTRIBUTE REMAP CHECKPOINT", FontSize = 18,
            FontWeight = FontWeights.Bold, Foreground = Brushes.Orange });
        top.Children.Add(new TextBlock { Text = "This reminder never expires automatically. Verify the in-game remap, then acknowledge it here.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), Foreground = Brushes.LightGray });
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); shell.Children.Add(bottom);
        var controls = new WrapPanel(); bottom.Children.Add(controls);
        controls.Children.Add(_ack);
        var view = new Button { Content = "OPEN PILOT COMMAND CENTER", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 8, 12, 8) };
        controls.Children.Add(view);
        view.Click += (_, _) => (Application.Current as App)?.ShowPilotCommandCenter();
        bottom.Children.Add(new TextBlock { Text = "Closing this window does not acknowledge the reminder; it will reappear on a later refresh.",
            Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        _alerts.Background = new SolidColorBrush(Color.FromRgb(17, 52, 58));
        _alerts.Foreground = Brushes.Honeydew;
        shell.Children.Add(_alerts);
        _alerts.SelectionChanged += (_, _) => _ack.IsEnabled = _alerts.SelectedIndex >= 0;
        _ack.Click += (_, _) => {
            int i = _alerts.SelectedIndex;
            if (i < 0 || i >= _data.Length) return;
            var item = _data[i];
            if (MessageBox.Show(this, "Confirm you have reviewed this remap checkpoint for " + item.Pilot.CharacterName + "?\n" +
                    item.Alert.Detail + "\n\nECC cannot perform an EVE remap.", "Acknowledge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { _onAck?.Invoke(item.Pilot, item.Alert.Remap); }
            catch (Exception ex) { MessageBox.Show(this, "Could not acknowledge remap: " + ex.Message); }
        };
    }
    public void Display(IReadOnlyList<(EvePilotProfile Pilot, SkillRemapAssessment Alert)> alerts,
        Action<EvePilotProfile, SkillImportRemap> onAck)
    {
        _data = alerts.ToArray(); _onAck = onAck;
        _alerts.ItemsSource = _data.Select(x => x.Pilot.CharacterName + " | " + x.Alert.State + " | " + x.Alert.Remap.Mode + "\n" + x.Alert.Detail).ToArray();
        if (_alerts.Items.Count > 0) _alerts.SelectedIndex = 0;
        _ack.IsEnabled = _alerts.Items.Count > 0;
    }
}
