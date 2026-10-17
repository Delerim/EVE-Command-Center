using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Media;
using EveCommandCenter.Services;

namespace EveCommandCenter.Views;

/// <summary>Per-channel, opt-in Discord webhook routing. URLs never appear in a list or log.</summary>
public sealed class DiscordWebhookSettingsWindow : Window
{
    private readonly DiscordWebhookService _service = DiscordWebhookService.Current;
    private readonly ListBox _destinations = new();
    private readonly TextBox _name = new();
    private readonly TextBox _channel = new();
    private readonly PasswordBox _webhook = new();
    private readonly CheckBox _enabled = new() { Content = "Enable sending to this destination", IsChecked = true };
    private readonly Dictionary<string, CheckBox> _types = new(StringComparer.Ordinal);
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray };
    private string? _editingId;

    private static readonly Brush ForegroundColor = new SolidColorBrush(Color.FromRgb(232, 248, 247));
    private static readonly Brush PanelColor = new SolidColorBrush(Color.FromRgb(12, 37, 41));

    public DiscordWebhookSettingsWindow()
    {
        Title = "EVE Command Center | Discord Webhooks";
        Width = 850;
        Height = 735;
        MinWidth = 650;
        MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(7, 24, 27));
        Foreground = ForegroundColor;

        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Content = root;

        var left = new DockPanel();
        Grid.SetColumn(left, 0);
        root.Children.Add(left);
        var leftHeader = new TextBlock { Text = "DESTINATIONS", FontWeight = FontWeights.Bold, Foreground = ForegroundColor, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(leftHeader, Dock.Top);
        left.Children.Add(leftHeader);
        var newButton = Button("+ New webhook", (_, _) => NewDestination());
        newButton.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(newButton, Dock.Bottom);
        left.Children.Add(newButton);
        _destinations.Background = PanelColor;
        _destinations.Foreground = ForegroundColor;
        _destinations.BorderBrush = Brushes.SlateGray;
        _destinations.SelectionChanged += (_, _) => LoadSelected();
        left.Children.Add(_destinations);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 2);
        root.Children.Add(scroll);
        var panel = new StackPanel { Background = PanelColor, Margin = new Thickness(0, 0, 4, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "DISCORD NOTIFICATIONS", FontWeight = FontWeights.Bold, FontSize = 18,
            Foreground = new SolidColorBrush(Color.FromRgb(85, 215, 210)), Margin = new Thickness(15, 15, 15, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = "One webhook per destination/channel. Discord determines the actual channel from the webhook URL. " +
                   "All alerts are optional. Moon fracture alerts are never sent.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(15, 0, 15, 16)
        });
        var editor = new StackPanel { Margin = new Thickness(15, 0, 15, 16) };
        panel.Children.Add(editor);
        scroll.Content = panel;
        AddLabel(editor, "Destination name");
        ConfigureText(_name);
        editor.Children.Add(_name);
        AddLabel(editor, "Channel label (for your reference)");
        ConfigureText(_channel);
        _channel.Text = "#eve-alerts";
        editor.Children.Add(_channel);
        AddLabel(editor, "Discord webhook URL");
        _webhook.Background = new SolidColorBrush(Color.FromRgb(8, 29, 34));
        _webhook.Foreground = ForegroundColor;
        _webhook.BorderBrush = Brushes.SlateGray;
        _webhook.Padding = new Thickness(7);
        editor.Children.Add(_webhook);
        editor.Children.Add(new TextBlock
        {
            Text = "On an existing destination, leave this empty to keep the saved URL. " +
                   "The URL is encrypted for your Windows account and is never exported to GitHub.",
            Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 0, 12)
        });
        _enabled.Foreground = ForegroundColor;
        editor.Children.Add(_enabled);
        AddLabel(editor, "Send these alerts");
        foreach (var (key, label) in DiscordWebhookService.AlertChoices)
        {
            var cb = new CheckBox { Content = label, Foreground = ForegroundColor, Margin = new Thickness(0, 4, 0, 4) };
            _types.Add(key, cb);
            editor.Children.Add(cb);
        }
        editor.Children.Add(new TextBlock
        {
            Text = "Moon not set only fires when the structure has no scheduled extraction. " +
                   "It re-arms when that condition is fixed. Messages are deduplicated across restarts.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightGray,
            Margin = new Thickness(0, 12, 0, 12)
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        var save = Button("Save destination", (_, _) => Save());
        var test = Button("Send test", async (_, _) => await TestAsync());
        var delete = Button("Remove", (_, _) => Remove());
        foreach (var button in new[] { save, test, delete })
        {
            button.Margin = new Thickness(0, 0, 8, 8);
            buttons.Children.Add(button);
        }
        editor.Children.Add(buttons);
        editor.Children.Add(_status);
        RefreshList();
        NewDestination();
    }

    private static Button Button(string title, RoutedEventHandler click)
    {
        var b = new Button
        {
            Content = title, Padding = new Thickness(10, 7, 10, 7),
            Foreground = ForegroundColor,
            Background = new SolidColorBrush(Color.FromRgb(18, 56, 61)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(57, 121, 108)),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        b.Click += click;
        return b;
    }
    private static void AddLabel(Panel parent, string text) => parent.Children.Add(new TextBlock
    {
        Text = text, Foreground = ForegroundColor, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 9, 0, 5)
    });
    private static void ConfigureText(TextBox box)
    {
        box.Background = new SolidColorBrush(Color.FromRgb(8, 29, 34));
        box.Foreground = ForegroundColor;
        box.BorderBrush = Brushes.SlateGray;
        box.Padding = new Thickness(7);
    }
    private void RefreshList()
    {
        _destinations.ItemsSource = _service.Destinations().Select(x => new DestinationItem(x)).ToList();
    }
    private void NewDestination()
    {
        _destinations.SelectedItem = null;
        _editingId = null;
        _name.Text = "EVE alerts";
        _channel.Text = "#eve-alerts";
        _webhook.Clear();
        _enabled.IsChecked = true;
        foreach (var item in _types) item.Value.IsChecked = item.Key == DiscordWebhookService.MoonNotSet;
        _status.Text = "Create a webhook in Discord > Server Settings > Integrations > Webhooks.";
    }
    private void LoadSelected()
    {
        if (_destinations.SelectedItem is not DestinationItem item) return;
        var row = item.Value;
        _editingId = row.Id;
        _name.Text = row.Name;
        _channel.Text = row.Channel;
        _webhook.Clear();
        _enabled.IsChecked = row.Enabled;
        foreach (var (key, cb) in _types) cb.IsChecked = row.AlertTypes.Contains(key);
        _status.Text = "Saved webhook URL is protected. Leave the URL field empty unless replacing it.";
    }
    private void Save()
    {
        try
        {
            _service.SaveDestination(_editingId ?? "", _name.Text, _channel.Text,
                _webhook.Password, _enabled.IsChecked == true,
                _types.Where(x => x.Value.IsChecked == true).Select(x => x.Key));
            _webhook.Clear();
            RefreshList();
            // Returning to the list prevents accidentally storing secrets in the input box.
            NewDestination();
            _status.Text = "Saved. New alerts matching the selected rules will be posted automatically.";
        }
        catch (Exception ex) { _status.Text = "Not saved: " + ex.Message; }
    }
    private async System.Threading.Tasks.Task TestAsync()
    {
        if (_editingId == null)
        {
            _status.Text = "Save the destination, select it in the list, then test.";
            return;
        }
        _status.Text = "Sending a test message...";
        try { _status.Text = await _service.TestAsync(_editingId); }
        catch (Exception ex) { _status.Text = "Test failed: " + ex.Message; }
    }
    private void Remove()
    {
        if (_editingId == null) return;
        if (MessageBox.Show("Remove this Discord destination?", "Discord webhooks",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _service.RemoveDestination(_editingId);
        RefreshList();
        NewDestination();
        _status.Text = "Removed destination.";
    }
    private sealed class DestinationItem
    {
        public DiscordWebhookDestination Value { get; }
        public DestinationItem(DiscordWebhookDestination value) => Value = value;
        public override string ToString() => Value.Name + (Value.Channel.Length > 0 ? "  " + Value.Channel : "") +
            (Value.Enabled ? "" : "  [off]");
    }
}
