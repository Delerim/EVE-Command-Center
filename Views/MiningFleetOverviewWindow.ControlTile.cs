using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using EveCommandCenter.Services;
using Panel = System.Windows.Controls.Panel;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace EveCommandCenter.Views;

public partial class MiningFleetOverviewWindow
{
    private TextBlock? _deckMining, _deckStatus;
    private readonly System.Collections.Generic.List<Button> _allAlarmButtons = new();
    private void UpdateCompactDeck(double value)
    {
        if (_deckMining != null) {
            _deckMining.Text = "TODAY " + (value >= 1000000000d ? (value/1000000000d).ToString("N2")+"B" : value>=1000000d ? (value/1000000d).ToString("N2")+"M" : value.ToString("N0")) + " ISK";
            _deckMining.ToolTip = FleetTodayProfit.Text;
        }
        if (_deckStatus != null) _deckStatus.Text = $"EVE {System.DateTime.UtcNow:HH:mm} | {_clientSource().Count()} clients";
        foreach(var button in _allAlarmButtons) {
            var characters = AlarmControlCharacters();
            bool muted = characters.Length > 0 && characters.All(_watchdog.IsCharacterAlarmMuted);
            button.Content = button.Tag?.ToString()=="icon" ? (muted ? "\uE7ED" : "\uEA8F") : (muted ? "ALL ALARMS ON" : "ALL ALARMS OFF");
            button.ToolTip = muted ? "Turn every client alarm switch on." : "Turn every client alarm switch off. Orca drone indicators stay unchanged.";
            button.Foreground = muted ? Brushes.Gold : Brushes.WhiteSmoke;
        }
    }
    private string[] AlarmControlCharacters() => _clientSource()
        .Select(c => c.CharacterName).Where(c => !string.IsNullOrWhiteSpace(c) &&
            (!_pilotIntel.TryGetValue(c,out var intel) || !intel.IsOrca)).Distinct(System.StringComparer.OrdinalIgnoreCase).ToArray();
    private void ToggleAllAlarms(object sender, RoutedEventArgs e)
    {
        var characters = AlarmControlCharacters();
        if (characters.Length == 0) return;
        bool turnOff = !characters.All(_watchdog.IsCharacterAlarmMuted);
        _watchdog.SetCharacterAlarmsMuted(characters, turnOff);
        RefreshCards();
    }

    private FrameworkElement BuildControlTileContent(Panel oldHeader, Button launch)
    {
        Brush Ink(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
        var stats = (Panel)PlexMarketBorder.Parent;
        oldHeader.Children.Clear();
        var body = new StackPanel();
        var title = new DockPanel { Margin = new Thickness(0,0,0,4), Background = Ink("#10252A"), LastChildFill = true };
        Button TitleButton(string text) => new() { Content = text, Width = 22, Height = 20, Margin = new Thickness(4,0,0,0), Padding = new Thickness(0) };
        var close = TitleButton("X"); close.ToolTip = "Hide control tile";
        close.Click += (_,_) => _controlTile?.Hide();
        DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        var minimize = TitleButton("-"); minimize.ToolTip = "Minimize";
        minimize.Click += (_,_) => { if (_controlTile != null) _controlTile.WindowState = WindowState.Minimized; };
        DockPanel.SetDock(minimize, Dock.Right); title.Children.Add(minimize);
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "COMMAND DECK", Foreground = Ink("#E9FAF7"), FontSize = 11, FontWeight = FontWeights.SemiBold });
        title.Children.Add(heading);
        title.MouseLeftButtonDown += (_,e) => { if(e.OriginalSource is TextBlock || ReferenceEquals(e.OriginalSource,title)) { try { _controlTile?.DragMove(); } catch(System.InvalidOperationException) {} } };
        body.Children.Add(title);
        Border Section(UIElement content)
        {
            var inner = new StackPanel();
            inner.Children.Add(content);
            return new Border { Background = Ink("#102126"), BorderBrush = Ink("#28464C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(5), Margin = new Thickness(0,0,0,4), Child = inner };
        }
        var compactStats = new StackPanel();
        var prices = new TextBlock { FontSize=10, Foreground=Ink("#C5DEDF"), Margin=new Thickness(0,0,0,3) };
        prices.Inlines.Add(new System.Windows.Documents.Run("PLEX  "));
        var buy = new System.Windows.Documents.Run(); buy.SetBinding(System.Windows.Documents.Run.TextProperty,new System.Windows.Data.Binding("Text") { Source=PlexBuyText });
        prices.Inlines.Add(buy); prices.Inlines.Add(new System.Windows.Documents.Run(" / "));
        var sell = new System.Windows.Documents.Run(); sell.SetBinding(System.Windows.Documents.Run.TextProperty,new System.Windows.Data.Binding("Text") { Source=PlexSellText });
        prices.Inlines.Add(sell); prices.ToolTip="PLEX buy / sell prices";
        compactStats.Children.Add(prices);
        _deckMining = new TextBlock { FontSize=11, Foreground=Ink("#67D7B8"), FontWeight=FontWeights.SemiBold };
        compactStats.Children.Add(_deckMining);
        body.Children.Add(Section(compactStats));
        Button IconButton(string glyph,string tooltip) => new() { Content=glyph, FontFamily=new FontFamily("Segoe MDL2 Assets"), FontSize=16, Height=28, Margin=new Thickness(0,0,4,0), Padding=new Thickness(2), ToolTip=tooltip };
        launch.Content="\uE716"; launch.FontFamily=new FontFamily("Segoe MDL2 Assets"); launch.FontSize=16; launch.Height=28; launch.Margin=new Thickness(0,0,4,0); launch.Padding=new Thickness(2); launch.ToolTip="Open Character Overview";
        var launchRow = new System.Windows.Controls.Primitives.UniformGrid { Columns=3 };
        launchRow.Children.Add(launch);
        var dashboard = IconButton("\uE80F","Open Command Center"); dashboard.Click+=OpenCommandCenter_Click; launchRow.Children.Add(dashboard);
        var alarms = IconButton("\uEA8F","Mute all client alarms"); alarms.Tag="icon"; alarms.Click+=ToggleAllAlarms; _allAlarmButtons.Add(alarms); launchRow.Children.Add(alarms);
        body.Children.Add(Section(launchRow));
        var tools = new StackPanel();
        var controlStyle = (Style)System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="Button">
                <Setter Property="Background" Value="#183239"/><Setter Property="Foreground" Value="#DFEFEE"/>
                <Setter Property="BorderBrush" Value="#34555D"/><Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Cursor" Value="Hand"/><Setter Property="FontSize" Value="10"/>
                <Setter Property="Height" Value="32"/><Setter Property="Padding" Value="9,4"/>
                <Setter Property="Margin" Value="0,0,6,5"/>
                <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                    <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="5" Padding="{TemplateBinding Padding}">
                        <TextBlock Text="{TemplateBinding Tag}" FontFamily="Segoe MDL2 Assets" FontSize="16" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                </ControlTemplate></Setter.Value></Setter>
                <Style.Triggers>
                    <Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="#24494D"/><Setter Property="BorderBrush" Value="#62BCAE"/></Trigger>
                    <Trigger Property="IsPressed" Value="True"><Setter Property="Background" Value="#286458"/></Trigger>
                    <Trigger Property="IsKeyboardFocused" Value="True"><Setter Property="BorderBrush" Value="#78D9C1"/></Trigger>
                    <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45"/></Trigger>
                </Style.Triggers>
            </Style>
            """);
        foreach(var actions in new Panel[] { FullActions, CompactActions }) {
            var groups = new System.Collections.Generic.Dictionary<string,System.Windows.Controls.Primitives.UniformGrid>();
            var groupPanel = new StackPanel();
            groupPanel.SetBinding(UIElement.VisibilityProperty, new System.Windows.Data.Binding("Visibility") { Source = actions });
            foreach(var child in actions.Children.OfType<Button>().ToArray()) {
                actions.Children.Remove(child);
                string label = child.Content?.ToString() ?? "";
                if(label=="COMMAND CENTER") continue;
                string category = child == HorizontalFitButton || child == CompactFitButton || label=="VERTICAL" ? "LAYOUT" :
                    child == ModeButton || child == LivePreviewButton ? "CARD VIEW" :
                    child == FullMuteButton || child == CompactMuteButton || label=="TOOLS" ? "AUDIO & TOOLS" : "MORE OPTIONS";
                if(!groups.TryGetValue(category,out var row)) {
                    row = new System.Windows.Controls.Primitives.UniformGrid { Columns=2 };
                    groups.Add(category,row);
                }
                child.ClearValue(FrameworkElement.HeightProperty); child.ClearValue(FrameworkElement.MarginProperty);
                child.ClearValue(System.Windows.Controls.Control.FontSizeProperty); child.ClearValue(System.Windows.Controls.Control.PaddingProperty);
                child.Style=controlStyle;
                child.Tag = child == HorizontalFitButton || child == CompactFitButton ? "\uE740" :
                    label=="VERTICAL" ? "\uE7AD" : child==ModeButton ? "\uE8A9" : child==LivePreviewButton ? "\uE714" :
                    child==FullMuteButton || child==CompactMuteButton ? "\uE767" : label=="TOOLS" ? "\uE713" : "\uE8A7";
                if(child.ToolTip==null) child.SetBinding(FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding("Content") { Source=child });

                if(label=="VERTICAL") { child.Content="ORIENTATION"; child.ToolTip="Switch character cards between horizontal and vertical layouts"; }
                if(label=="TOOLS") child.Content="MORE TOOLS";
                if(label=="SEPARATE PREVIEWS") { child.Content="SEPARATE WINDOWS"; child.ToolTip="Replace combined cards with individual preview windows"; }
                row.Children.Add(child);
            }
            // Stable grouping regardless of the original toolbar button order.
            foreach (string category in new[] { "LAYOUT", "CARD VIEW", "AUDIO & TOOLS", "MORE OPTIONS" }) {
                if (!groups.TryGetValue(category,out var row)) continue;
                groupPanel.Children.Add(new TextBlock { Text=category, FontSize=9, Foreground=Ink("#799DA5"), Margin=new Thickness(0,6,0,5) });
                groupPanel.Children.Add(row);
            }
            tools.Children.Add(groupPanel);
        }
        var alarmToggle = new Button { Height=28, FontSize=9, Margin=new Thickness(0,5,0,5) };
        alarmToggle.Click+=ToggleAllAlarms; _allAlarmButtons.Add(alarmToggle); tools.Children.Add(alarmToggle);
        var settings = new Expander {
            Header = "OVERVIEW SETTINGS", IsExpanded = _prefs.ControlTileSettingsExpanded,
            Foreground = Ink("#A4CFCE"), FontSize = 10, FontWeight = FontWeights.SemiBold,
            Content = tools
        };
        settings.Expanded += (_,_) => { _prefs.ControlTileSettingsExpanded = true; MiningDashboardPreferencesStore.Save(_prefs); };
        settings.Collapsed += (_,_) => { _prefs.ControlTileSettingsExpanded = false; MiningDashboardPreferencesStore.Save(_prefs); };
        body.Children.Add(new Border { Background = Ink("#102126"), BorderBrush = Ink("#28464C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(5), Margin = new Thickness(0,0,0,4), Child = settings });
        _deckStatus = new TextBlock { FontSize=9, Foreground=Ink("#83A2A7"), Margin=new Thickness(1,0,0,0) };
        _deckStatus.SetBinding(FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding("Text") { Source=UpdatedText });
        body.Children.Add(_deckStatus);
        return new Border { Background = Ink("#0B171C"), BorderBrush = Ink("#3A686C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(8), Child = body };
    }
}
