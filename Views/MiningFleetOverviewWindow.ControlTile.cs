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
        var title = new DockPanel { Margin = new Thickness(0,0,0,14), Background = Ink("#10252A"), LastChildFill = true };
        Button TitleButton(string text) => new() { Content = text, Width = 28, Height = 26, Margin = new Thickness(4,0,0,0), Padding = new Thickness(0) };
        var close = TitleButton("X"); close.ToolTip = "Hide control tile";
        close.Click += (_,_) => _controlTile?.Hide();
        DockPanel.SetDock(close, Dock.Right); title.Children.Add(close);
        var minimize = TitleButton("-"); minimize.ToolTip = "Minimize";
        minimize.Click += (_,_) => { if (_controlTile != null) _controlTile.WindowState = WindowState.Minimized; };
        DockPanel.SetDock(minimize, Dock.Right); title.Children.Add(minimize);
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = "COMMAND DECK", Foreground = Ink("#E9FAF7"), FontSize = 16, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = "FLEET CONTROL", Foreground = Ink("#6FB7B5"), FontSize = 9, Margin = new Thickness(0,3,0,0) });
        title.Children.Add(heading);
        title.MouseLeftButtonDown += (_,e) => { if(e.OriginalSource is TextBlock || ReferenceEquals(e.OriginalSource,title)) { try { _controlTile?.DragMove(); } catch(System.InvalidOperationException) {} } };
        body.Children.Add(title);
        Border Section(string caption, UIElement content)
        {
            var inner = new StackPanel();
            inner.Children.Add(new TextBlock { Text = caption, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Ink("#81B9BF"), Margin = new Thickness(0,0,0,8) });
            inner.Children.Add(content);
            return new Border { Background = Ink("#102126"), BorderBrush = Ink("#28464C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(12), Margin = new Thickness(0,0,0,10), Child = inner };
        }
        stats.Margin = new Thickness(0);
        DayText.Margin = new Thickness(0,0,12,6);
        PlexMarketBorder.Margin = new Thickness(0,0,12,6);
        FleetTodayProfit.Margin = new Thickness(0,0,0,6);
        body.Children.Add(Section("MARKET & MINING", stats));
        launch.Content = "CHARACTER OVERVIEW"; launch.FontSize = 10; launch.Height = 34; launch.Margin = new Thickness(0,0,6,0);
        launch.Background = Ink("#205B51"); launch.BorderBrush = Ink("#48AC96");
        var launchRow = new Grid();
        launchRow.ColumnDefinitions.Add(new ColumnDefinition()); launchRow.ColumnDefinitions.Add(new ColumnDefinition());
        launchRow.Children.Add(launch);
        var dashboard = new Button { Content = "COMMAND CENTER", FontSize = 10, Height = 34 };
        dashboard.Click += OpenCommandCenter_Click;
        Grid.SetColumn(dashboard,1); launchRow.Children.Add(dashboard);
        body.Children.Add(Section("LAUNCH", launchRow));
        var tools = new StackPanel();
        foreach(var actions in new Panel[] { FullActions, CompactActions }) {
            foreach(var child in actions.Children.OfType<Button>().ToArray()) {
                if(child.Content?.ToString()=="COMMAND CENTER") { actions.Children.Remove(child); continue; }
                child.Height=28; child.FontSize=10; child.Padding=new Thickness(9,3,9,3); child.Margin=new Thickness(0,0,6,6);
            }
            actions.Margin = new Thickness(0); tools.Children.Add(actions);
        }
        body.Children.Add(Section("DISPLAY & AUDIO", tools));
        UpdatedText.FontSize=10; UpdatedText.Foreground=Ink("#83A2A7"); UpdatedText.Margin=new Thickness(2,0,0,2);
        UpdatedText.TextWrapping=TextWrapping.Wrap; UpdatedText.TextAlignment=TextAlignment.Left;
        body.Children.Add(UpdatedText);
        return new Border { Background = Ink("#0B171C"), BorderBrush = Ink("#3A686C"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(14), Child = body };
    }
}
