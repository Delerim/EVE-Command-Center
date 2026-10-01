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
    private FrameworkElement BuildControlTileContent(Panel oldHeader, Button launch)
    {
        Brush Ink(string color) => (Brush)new BrushConverter().ConvertFromString(color)!;
        var stats = (Panel)PlexMarketBorder.Parent;
        oldHeader.Children.Clear();
        var body = new StackPanel();
        var title = new DockPanel { Margin = new Thickness(0,0,0,8), Background = Ink("#10252A"), LastChildFill = true };
        Button TitleButton(string text) => new() { Content = text, Width = 28, Height = 26, Margin = new Thickness(4,0,0,0), Padding = new Thickness(0) };
        var close = TitleButton("X"); close.ToolTip = "Hide control tile";
        close.Click += (_,_) => _controlTile?.Hide();
        DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        var minimize = TitleButton("-"); minimize.ToolTip = "Minimize";
        minimize.Click += (_,_) => { if (_controlTile != null) _controlTile.WindowState = WindowState.Minimized; };
        DockPanel.SetDock(minimize, Dock.Right); title.Children.Add(minimize);
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "COMMAND DECK", Foreground = Ink("#E9FAF7"), FontSize = 13, FontWeight = FontWeights.SemiBold });
        title.Children.Add(heading);
        title.MouseLeftButtonDown += (_,e) => { if(e.OriginalSource is TextBlock || ReferenceEquals(e.OriginalSource,title)) { try { _controlTile?.DragMove(); } catch(System.InvalidOperationException) {} } };
        body.Children.Add(title);
        Border Section(UIElement content)
        {
            var inner = new StackPanel();
            inner.Children.Add(content);
            return new Border { Background = Ink("#102126"), BorderBrush = Ink("#28464C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0,0,0,6), Child = inner };
        }
        stats.Margin = new Thickness(0);
        DayText.Margin = new Thickness(0,0,8,3);
        PlexMarketBorder.Margin = new Thickness(0,0,8,3);
        FleetTodayProfit.Margin = new Thickness(0,0,0,3);
        body.Children.Add(Section(stats));
        launch.Content = "CHARACTER OVERVIEW"; launch.FontSize = 10; launch.Height = 30; launch.Margin = new Thickness(0,0,6,0);
        launch.Background = Ink("#205B51"); launch.BorderBrush = Ink("#48AC96");
        var launchRow = new Grid();
        launchRow.ColumnDefinitions.Add(new ColumnDefinition()); launchRow.ColumnDefinitions.Add(new ColumnDefinition());
        launchRow.Children.Add(launch);
        var dashboard = new Button { Content = "COMMAND CENTER", FontSize = 10, Height = 30 };
        dashboard.Click += OpenCommandCenter_Click;
        Grid.SetColumn(dashboard,1); launchRow.Children.Add(dashboard);
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
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center" RecognizesAccessKey="True"/>
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
                if(label=="VERTICAL") { child.Content="SWITCH ORIENTATION"; child.ToolTip="Switch character cards between horizontal and vertical layouts"; }
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
        var settings = new Expander {
            Header = "OVERVIEW SETTINGS", IsExpanded = _prefs.ControlTileSettingsExpanded,
            Foreground = Ink("#A4CFCE"), FontSize = 10, FontWeight = FontWeights.SemiBold,
            Content = tools
        };
        settings.Expanded += (_,_) => { _prefs.ControlTileSettingsExpanded = true; MiningDashboardPreferencesStore.Save(_prefs); };
        settings.Collapsed += (_,_) => { _prefs.ControlTileSettingsExpanded = false; MiningDashboardPreferencesStore.Save(_prefs); };
        body.Children.Add(new Border { Background = Ink("#102126"), BorderBrush = Ink("#28464C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(8), Margin = new Thickness(0,0,0,6), Child = settings });
        UpdatedText.FontSize=10; UpdatedText.Foreground=Ink("#83A2A7"); UpdatedText.Margin=new Thickness(2,0,0,2);
        UpdatedText.TextWrapping=TextWrapping.Wrap; UpdatedText.TextAlignment=TextAlignment.Left;
        body.Children.Add(UpdatedText);
        return new Border { Background = Ink("#0B171C"), BorderBrush = Ink("#3A686C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(10), Child = body };
    }
}
