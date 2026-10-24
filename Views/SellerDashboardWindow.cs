using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using System.Threading;
using EveCommandCenter.Services;
using EveCommandCenter.Models;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBlock = System.Windows.Controls.TextBlock;
using Border = System.Windows.Controls.Border;
using DockPanel = System.Windows.Controls.DockPanel;
using StackPanel = System.Windows.Controls.StackPanel;
using WrapPanel = System.Windows.Controls.WrapPanel;
using DataGrid = System.Windows.Controls.DataGrid;
using DataGridCell = System.Windows.Controls.DataGridCell;
using DataGridRow = System.Windows.Controls.DataGridRow;
using DataGridColumnHeader = System.Windows.Controls.Primitives.DataGridColumnHeader;
using Binding = System.Windows.Data.Binding;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Orientation = System.Windows.Controls.Orientation;

namespace EveCommandCenter.Views;

public sealed class SellerDashboardWindow : Window
{
    private readonly SellerDashboardService _seller;
    private readonly CancellationTokenSource _life=new();
    private readonly DispatcherTimer _refreshTimer=new(){Interval=TimeSpan.FromMinutes(10)};
    private readonly ComboBox _pilots=new(){MinWidth=200,Height=30};
    private readonly ComboBox _filter=new(){Width=145,Height=29};
    private readonly ComboBox _category=new(){Width=170,Height=29};
    private readonly TextBox _search=new(){Width=260,Height=29};
    private readonly DataGrid _grid=new(){AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,
        CanUserDeleteRows=false,CanUserSortColumns=true,EnableRowVirtualization=true,RowHeight=45,
        SelectionMode=DataGridSelectionMode.Single,HeadersVisibility=DataGridHeadersVisibility.Column};
    private readonly TextBlock _status=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _detail=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock _all=new();private readonly TextBlock _ready=new();
    private readonly TextBlock _undercut=new();private readonly TextBlock _active=new();
    private readonly TextBlock _value=new();
    private bool _loadingPilots;
    private bool _closing;
    private static SolidColorBrush Ink(byte r,byte g,byte b)=>new(Color.FromRgb(r,g,b));
    private static readonly SolidColorBrush Teal=Ink(105,222,195),Muted=Ink(147,181,185),Yellow=Ink(253,194,112);
    private sealed record PilotChoice(long Id,string Name,string Detail)
    { public override string ToString()=>Name+"   ·   "+Detail; }

    public SellerDashboardWindow()
    {
        Title="EVE Command Center | SELLER COMMAND CENTER";
        Width=1590;Height=920;MinWidth=1100;MinHeight=680;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=Ink(6,20,24);Foreground=Ink(230,245,244);
        var ops=BackgroundOperations.Current;
        _seller=new SellerDashboardService(ops.Sso,ops.MarketOrders);
        var root=new DockPanel{Margin=new Thickness(18,14,18,14)};
        Content=root;
        var header=new StackPanel{Margin=new Thickness(0,0,0,12)};
        DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var hero=new DockPanel{Margin=new Thickness(0,0,0,7)};
        header.Children.Add(hero);
        var actions=new StackPanel{Orientation=Orientation.Horizontal};
        DockPanel.SetDock(actions,Dock.Right);hero.Children.Add(actions);
        MakeButton(actions,"REFRESH STOCK + ORDERS",async(_,_)=>await RefreshAsync());
        MakeButton(actions,"LINKED ACCOUNTS",(_,_) =>
            (System.Windows.Application.Current as App)?.OpenCommandCenterModule("setup"));
        hero.Children.Add(new TextBlock{Text="SELLER COMMAND CENTER",FontSize=25,FontWeight=FontWeights.Bold,
            Foreground=Teal,VerticalAlignment=VerticalAlignment.Center});
        header.Children.Add(new TextBlock{Text="Your personal trading character · Hangars, nested containers and live sell orders · Jita 4-4 vs Amarr",
            FontSize=12,Foreground=Muted,Margin=new Thickness(0,0,0,16)});
        var selector=new WrapPanel{Margin=new Thickness(0,0,0,13)};
        header.Children.Add(selector);
        selector.Children.Add(Label("TRADING CHARACTER",new Thickness(0,0,10,0)));
        _pilots.SelectionChanged+=async(_,_)=>
        {
            if(_loadingPilots || _pilots.SelectedItem is not PilotChoice pilot)return;
            _seller.SelectSeller(pilot.Id);await RefreshAsync();
        };
        selector.Children.Add(_pilots);
        selector.Children.Add(Label("Personal assets + market-order permissions required",new Thickness(18,0,0,0),Muted));
        var tiles=new UniformGrid{Columns=5,Margin=new Thickness(0,0,0,14)};
        header.Children.Add(tiles);
        Tile(tiles,"INVENTORY POSITIONS",_all,Teal);
        Tile(tiles,"NOT LISTED",_ready,Yellow);
        Tile(tiles,"UNDERCUT ORDERS",_undercut,Ink(255,132,143));
        Tile(tiles,"ACTIVE ORDERS",_active,Ink(133,205,255));
        Tile(tiles,"BEST HUB STOCK VALUE*",_value,Ink(165,224,191));
        var controls=new DockPanel{Margin=new Thickness(0,0,0,11)};
        header.Children.Add(controls);
        var right=new StackPanel{Orientation=Orientation.Horizontal};
        DockPanel.SetDock(right,Dock.Right);controls.Children.Add(right);
        right.Children.Add(Label("FIND",new Thickness(0,0,9,0)));
        _search.Padding=new Thickness(8,5,8,5);
        StyleInput(_search);_search.TextChanged+=(_,_)=>Render();right.Children.Add(_search);
        right.Children.Add(Label("STATUS",new Thickness(17,0,9,0)));
        foreach(var s in new[]{"All stock","Not listed","Undercut","Listed","Review stale"})_filter.Items.Add(s);
        _filter.SelectedIndex=0;_filter.SelectionChanged+=(_,_)=>Render();StyleInput(_filter);right.Children.Add(_filter);
        right.Children.Add(Label("GROUP",new Thickness(17,0,9,0)));
        _category.Items.Add("All groups");_category.SelectedIndex=0;
        _category.SelectionChanged+=(_,_)=>Render();StyleInput(_category);right.Children.Add(_category);
        controls.Children.Add(new TextBlock{Text="READY TO SELL  /  PRICE INTELLIGENCE",FontWeight=FontWeights.SemiBold,
            Foreground=Teal,FontSize=14,VerticalAlignment=VerticalAlignment.Center});
        _status.Foreground=Muted;_status.FontSize=11;_status.Margin=new Thickness(0,0,0,7);
        header.Children.Add(_status);
        _grid.Background=Ink(9,28,32);_grid.Foreground=Ink(220,239,237);
        _grid.RowBackground=Ink(11,35,39);_grid.AlternatingRowBackground=Ink(16,43,48);
        _grid.HorizontalGridLinesBrush=Ink(26,61,65);_grid.VerticalGridLinesBrush=Ink(26,61,65);
        _grid.BorderBrush=Ink(41,91,84);_grid.BorderThickness=new Thickness(1);
        var head=new Style(typeof(DataGridColumnHeader));
        head.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty,Ink(18,52,57)));
        head.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty,Teal));
        head.Setters.Add(new Setter(DataGridColumnHeader.HeightProperty,37d));
        head.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty,FontWeights.SemiBold));
        _grid.ColumnHeaderStyle=head;
        var cell=new Style(typeof(DataGridCell));
        cell.Setters.Add(new Setter(DataGridCell.BackgroundProperty,Brushes.Transparent));
        cell.Setters.Add(new Setter(DataGridCell.ForegroundProperty,Ink(225,243,242)));
        cell.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty,new Thickness(0)));
        _grid.CellStyle=cell;
        var rows=new Style(typeof(DataGridRow));
        var trigger=new Trigger{Property=DataGridRow.IsSelectedProperty,Value=true};
        trigger.Setters.Add(new Setter(DataGridRow.BackgroundProperty,Ink(23,91,80)));
        rows.Triggers.Add(trigger);_grid.RowStyle=rows;
        var item=new DataGridTemplateColumn{Header="ITEM",Width=new DataGridLength(290),SortMemberPath="Name"};
        var tpl=new DataTemplate();
        var panel=new FrameworkElementFactory(typeof(StackPanel));panel.SetValue(StackPanel.OrientationProperty,Orientation.Horizontal);
        var icon=new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
        icon.SetValue(FrameworkElement.WidthProperty,32d);icon.SetValue(FrameworkElement.HeightProperty,32d);
        icon.SetValue(FrameworkElement.MarginProperty,new Thickness(8,0,10,0));icon.SetBinding(System.Windows.Controls.Image.SourceProperty,new Binding("Icon"));
        panel.AppendChild(icon);
        var name=new FrameworkElementFactory(typeof(TextBlock));name.SetBinding(TextBlock.TextProperty,new Binding("Name"));
        name.SetValue(TextBlock.VerticalAlignmentProperty,VerticalAlignment.Center);
        name.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);
        panel.AppendChild(name);tpl.VisualTree=panel;item.CellTemplate=tpl;_grid.Columns.Add(item);
        Column("GROUP","Category",155);
        Column("STOCK","QtyText",90);
        Column("LISTED","ListedText",85);
        Column("MY ASK","MyPriceText",110);
        Column("JITA 4-4","JitaText",120);
        Column("JITA Δ","JitaShift",105);
        Column("AMARR","AmarrText",120);
        Column("AMARR Δ","AmarrShift",105);
        Column("BETTER HUB","PreferredHub",112);
        Column("EDGE / UNIT","HubEdge",126);
        Column("STATUS","State",126,true);
        Column("LOCATION","Location",230);
        Column("CONTAINER","ContainerPath",240);
        _grid.SelectionChanged+=(_,_)=>Detail();
        root.Children.Add(_grid);
        var foot=new Border{Background=Ink(13,36,40),BorderBrush=Ink(43,91,85),BorderThickness=new Thickness(1),
            Padding=new Thickness(12),Margin=new Thickness(0,10,0,0)};
        DockPanel.SetDock(foot,Dock.Bottom);
        _detail.Text="Select an item to inspect stock, orders, location and hub comparison.";
        _detail.Foreground=Muted;_detail.FontSize=12;foot.Child=_detail;
        root.Children.Remove(foot);root.Children.Insert(root.Children.IndexOf(_grid),foot);
        _seller.Updated+=OnUpdated;
        Loaded+=async(_,_)=>
        {
            await LoadPilots();
            _refreshTimer.Tick+=async(_,_)=>await RefreshAsync();
            _refreshTimer.Start();
        };
        Closed+=(_,_)=>{_closing=true;_refreshTimer.Stop();_life.Cancel();_seller.Updated-=OnUpdated;_seller.Dispose();_life.Dispose();};
        Render();
    }
    private static TextBlock Label(string value,Thickness margin,SolidColorBrush? colour=null)=>new()
    {Text=value,Margin=margin,FontSize=11,Foreground=colour??Muted,VerticalAlignment=VerticalAlignment.Center};
    private static void StyleInput(System.Windows.Controls.Control control)
    {control.Background=Ink(12,34,38);control.Foreground=Brushes.WhiteSmoke;control.BorderBrush=Ink(61,119,108);}
    private static Button MakeButton(StackPanel host,string label,RoutedEventHandler handler)
    {
        var b=new Button{Content=label,Margin=new Thickness(5,0,0,0),Padding=new Thickness(12,5,12,5),Height=30,
            FontSize=11,FontWeight=FontWeights.SemiBold,Background=Ink(22,58,60),Foreground=Brushes.WhiteSmoke,
            BorderBrush=Ink(56,119,108)};
        b.Click+=handler;host.Children.Add(b);return b;
    }
    private static void Tile(UniformGrid parent,string label,TextBlock value,SolidColorBrush colour)
    {
        var border=new Border{CornerRadius=new CornerRadius(7),Margin=new Thickness(0,0,10,0),
            Padding=new Thickness(15,12,15,12),Background=Ink(16,41,45),BorderBrush=Ink(40,86,81),BorderThickness=new Thickness(1)};
        var stack=new StackPanel();border.Child=stack;parent.Children.Add(border);
        stack.Children.Add(new TextBlock{Text=label,Foreground=Muted,FontSize=10,FontWeight=FontWeights.SemiBold});
        value.Text="—";value.FontSize=25;value.FontWeight=FontWeights.SemiBold;value.Foreground=colour;
        value.Margin=new Thickness(0,6,0,0);stack.Children.Add(value);
    }
    private void Column(string header,string property,double width,bool status=false)
    {
        if(!status)
        {
            _grid.Columns.Add(new DataGridTextColumn{Header=header,Binding=new Binding(property),
                Width=new DataGridLength(width),IsReadOnly=true});return;
        }
        var tpl=new DataTemplate();var text=new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty,new Binding(property));
        text.SetBinding(TextBlock.ForegroundProperty,new Binding("StateColor"));
        text.SetValue(TextBlock.FontWeightProperty,FontWeights.Bold);
        text.SetValue(TextBlock.VerticalAlignmentProperty,VerticalAlignment.Center);
        tpl.VisualTree=text;
        _grid.Columns.Add(new DataGridTemplateColumn{Header=header,CellTemplate=tpl,Width=new DataGridLength(width),SortMemberPath=property});
    }
    private async System.Threading.Tasks.Task LoadPilots()
    {
        _loadingPilots=true;
        try
        {
            var pilots=await BackgroundOperations.Current.Sso.LoadPilotsAsync();
            _pilots.ItemsSource=pilots.Select(p=>new PilotChoice(p.CharacterId,p.CharacterName,
                p.Scopes.Contains(SellerDashboardService.AssetsScope)&&p.Scopes.Contains(SellerDashboardService.OrdersScope)
                    ? "Ready" : "Reconnect required")).OrderBy(x=>x.Name).ToArray();
            _pilots.SelectedItem=((IEnumerable<PilotChoice>)_pilots.ItemsSource).FirstOrDefault(p=>p.Id==_seller.SellerCharacterId);
        }
        catch(Exception ex){_status.Text="Cannot load linked traders: "+ex.Message;}
        finally{_loadingPilots=false;}
        if(_seller.SellerCharacterId>0)await RefreshAsync();
    }
    private async System.Threading.Tasks.Task RefreshAsync()
    {
        if(_closing || _seller.Busy || _seller.SellerCharacterId<=0)return;
        await _seller.RefreshAsync(_life.Token);
        Render();
    }
    private void OnUpdated()
    {
        if(_closing)return;
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(new Action(OnUpdated));return;}
        _status.Text=_seller.Status;
        if(!_seller.Busy)Render();
    }
    private void Render()
    {
        if(_closing)return;
        string query=_search.Text.Trim();
        string filter=_filter.SelectedItem as string ?? "All stock";
        string category=_category.SelectedItem as string ?? "All groups";
        var selected=(_grid.SelectedItem as SellerStockRow)?.TypeId;
        var rows=_seller.Rows;
        var cats=rows.Select(x=>x.Category).Distinct().OrderBy(x=>x).ToArray();
        if(_category.Items.Count!=cats.Length+1 || cats.Any(x=>!_category.Items.Contains(x)))
        {
            _category.Items.Clear();_category.Items.Add("All groups");foreach(var cat in cats)_category.Items.Add(cat);
            _category.SelectedItem=_category.Items.Contains(category)?category:"All groups";
        }
        var visible=rows.Where(x=>(category=="All groups"||x.Category==category)&&
            (query.Length==0||(x.Name+" "+x.Category+" "+x.Location+" "+x.ContainerPath).Contains(query,StringComparison.OrdinalIgnoreCase)) &&
            (filter=="All stock"||filter=="Not listed"&&x.State=="NOT LISTED"||
             filter=="Undercut"&&x.State=="UNDERCUT"||filter=="Listed"&&x.State=="LISTED"||
             filter=="Review stale"&&(x.State=="ORDERS STALE"||x.State=="PRICE STALE"))).ToArray();
        _grid.ItemsSource=visible;
        if(selected.HasValue)_grid.SelectedItem=visible.FirstOrDefault(x=>x.TypeId==selected.Value);
        _all.Text=rows.Count.ToString("N0");
        _ready.Text=rows.Count(x=>x.State=="NOT LISTED").ToString("N0");
        _undercut.Text=rows.Count(x=>x.State=="UNDERCUT").ToString("N0");
        _active.Text=rows.Where(x=>x.OpenOrders>0).Select(x=>x.TypeId).Distinct().Count().ToString("N0");
        var estimate=rows.Where(x=>x.Jita.HasValue || x.Amarr.HasValue).Sum(x=>x.Stock*Math.Max(x.Jita??0,x.Amarr??0));
        _value.Text=estimate>=1_000_000_000m?(estimate/1_000_000_000m).ToString("N2")+"B":(estimate/1_000_000m).ToString("N1")+"M";
        _status.Text=_seller.Status+"  ·  *Price-only indicative value; ignores taxes, fees, hauling and unquoted stock.";
        Detail();
    }
    private void Detail()=>_detail.Text=(_grid.SelectedItem as SellerStockRow)?.Detail??
        "Choose an item for stock and order context. Undercut is based on public station prices; results can be cached or incomplete.";
}
