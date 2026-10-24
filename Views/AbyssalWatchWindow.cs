using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Binding = System.Windows.Data.Binding;
using System.Windows.Media;
using EveCommandCenter.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using TabControl = System.Windows.Controls.TabControl;
using TextBlock = System.Windows.Controls.TextBlock;
using Panel = System.Windows.Controls.Panel;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;

namespace EveCommandCenter.Views;

// Compact market-intelligence workspace. Reuses the background monitor; no
// additional pilot authentication or market-order polling is introduced.
public sealed class AbyssalWatchWindow : Window
{
    private readonly AbyssalWatchService _watch = AbyssalWatchService.Instance;
    private readonly CheckBox _enabled = new() { Content = "Popup alerts" };
    private readonly CheckBox _ore = new() { Content = "ORE Strip Miner" };
    private readonly CheckBox _modulated = new() { Content = "Modulated Strip Miner II" };
    private readonly CheckBox _single = new() { Content = "Single-item contracts" };
    private readonly TextBox _asking = new() { Width = 85 };
    private readonly TextBox _estimated = new() { Width = 85 };
    private readonly TextBox _amount = new() { Width = 85 };
    private readonly TextBox _speed = new() { Width = 85 };
    private readonly TextBox _cpu = new() { Width = 85 };
    private readonly TextBox _pg = new() { Width = 85 };
    private readonly ComboBox _minutes = new() { Width = 75 };
    private readonly TextBox _search = new() { Width = 230, ToolTip = "Search model, mutaplasmid, contract or module ID" };
    private readonly DataGrid _results = new() { AutoGenerateColumns = false, IsReadOnly = true,
        CanUserAddRows = false, CanUserDeleteRows = false, CanUserSortColumns = true,
        EnableRowVirtualization = true, RowHeight = 42, FontSize = 12,
        SelectionMode = DataGridSelectionMode.Single,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
    private readonly TextBlock _status = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { FontSize = 14, FontWeight = FontWeights.Bold };
    private readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TabControl _tabs = new();
    private readonly Border _filterPanel = new();
    private static SolidColorBrush Ink(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
    private static readonly SolidColorBrush Teal = Ink(107, 221, 198);
    private static readonly SolidColorBrush Muted = Ink(144, 181, 187);

    public AbyssalWatchWindow()
    {
        Title = "EVE Command Center | Abyssal Hunter";
        Width = 1520; Height = 900; MinWidth = 920; MinHeight = 610;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ink(6, 20, 24); Foreground = Ink(224, 244, 244);
        var root = new DockPanel { Margin = new Thickness(15, 12, 15, 12) };
        Content = root;
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        header.Children.Add(top);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        var scan = ActionButton("CHECK NOW", buttons, "Refresh the latest public contracts");
        scan.Click += async (_, _) => { if (Save()) await _watch.RefreshAsync(); };
        var previewAlert = ActionButton("PREVIEW ALERT", buttons,
            "Show the actual popup for the selected matching contract without marking it new");
        previewAlert.Click += (_, _) => PreviewAlert();
        var browse = ActionButton("LOAD MORE", buttons, "Scan up to three pages per model for browsing. Background monitoring stays lightweight.");
        browse.Click += async (_, _) => { if (Save()) await _watch.RefreshAsync(includeMorePages: true); };
        var filterButton = ActionButton("FILTERS", buttons, "Show or hide saved hunter thresholds");
        filterButton.Click += (_, _) => _filterPanel.Visibility = _filterPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        var open = ActionButton("OPEN MODULE", buttons, "Open the selected module on MutaMarket");
        open.Click += (_, _) => OpenSelected();
        top.Children.Add(new TextBlock { Text = "ABYSSAL HUNTER", FontSize = 23,
            FontWeight = FontWeights.Bold, Foreground = Teal, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock {
            Text = "ORE + Modulated II  •  Public contracts  •  Performance, fitting and value intelligence",
            Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 0, 0, 11) });

        _filterPanel.Background = Ink(13, 37, 42);
        _filterPanel.BorderBrush = Ink(43, 92, 92);
        _filterPanel.BorderThickness = new Thickness(1);
        _filterPanel.CornerRadius = new CornerRadius(6);
        _filterPanel.Padding = new Thickness(10);
        _filterPanel.Margin = new Thickness(0, 0, 0, 10);
        _filterPanel.Visibility = Visibility.Collapsed;
        header.Children.Add(_filterPanel);
        var filterStack = new StackPanel();
        _filterPanel.Child = filterStack;
        var toggles = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        filterStack.Children.Add(toggles);
        foreach (var box in new[] { _enabled, _ore, _modulated, _single })
        { box.Margin = new Thickness(0, 0, 22, 4); toggles.Children.Add(box); }
        var thresholds = new WrapPanel(); filterStack.Children.Add(thresholds);
        AddFilter(thresholds, "Min asking (M ISK)", _asking);
        AddFilter(thresholds, "Min estimated (M)", _estimated);
        AddFilter(thresholds, "Min yield (m³)", _amount);
        AddFilter(thresholds, "Min effective (m³/s)", _speed);
        AddFilter(thresholds, "Max CPU Δ (tf)", _cpu);
        AddFilter(thresholds, "Max PG Δ (MW)", _pg);
        var settingsBottom = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        filterStack.Children.Add(settingsBottom);
        settingsBottom.Children.Add(new TextBlock { Text = "Scan every (minutes)", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (int n in new[] { 2, 5, 10, 15, 30 }) _minutes.Items.Add(n);
        settingsBottom.Children.Add(_minutes);
        var save = ActionButton("SAVE FILTERS", settingsBottom, "Keep thresholds and polling interval across restarts");
        save.Click += (_, _) => { if (Save()) Refresh(); };

        var infoRow = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };
        header.Children.Add(infoRow);
        var searchArea = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(searchArea, Dock.Right); infoRow.Children.Add(searchArea);
        searchArea.Children.Add(new TextBlock { Text = "Find", VerticalAlignment = VerticalAlignment.Center,
            Foreground = Muted, Margin = new Thickness(0, 0, 7, 0) });
        _search.Padding = new Thickness(7, 4, 7, 4);
        _search.TextChanged += (_, _) => Refresh();
        searchArea.Children.Add(_search);
        _summary.Foreground = Teal;
        infoRow.Children.Add(_summary);
        _status.Foreground = Muted; header.Children.Add(_status);

        _tabs.Margin = new Thickness(0, 8, 0, 7);
        _tabs.Background = Ink(11, 37, 40);
        _tabs.BorderBrush = Ink(45, 84, 86);
        _tabs.Items.Add(HunterTab("MATCHES"));
        _tabs.Items.Add(HunterTab("ORE STRIP MINER"));
        _tabs.Items.Add(HunterTab("MODULATED II"));
        _tabs.Items.Add(HunterTab("BROWSE ALL"));
        _tabs.Items.Add(HunterTab("ALERT HISTORY"));
        _tabs.SelectionChanged += (_, e) => { if (e.Source == _tabs) Refresh(); };
        header.Children.Add(_tabs);

        _results.Background = Ink(9, 29, 33);
        _results.Foreground = Ink(220, 240, 237);
        _results.RowBackground = Ink(9, 30, 35);
        _results.AlternatingRowBackground = Ink(14, 42, 46);
        _results.HorizontalGridLinesBrush = Ink(35, 69, 70);
        _results.VerticalGridLinesBrush = Ink(35, 69, 70);
        _results.BorderBrush = Ink(45, 88, 87);
        _results.ColumnHeaderStyle = new Style(typeof(DataGridColumnHeader));
        _results.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, Ink(18, 55, 59)));
        _results.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, Teal));
        _results.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.SemiBold));
        _results.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.HeightProperty, 38d));
        _results.CellStyle = new Style(typeof(DataGridCell));
        _results.CellStyle.Setters.Add(new Setter(DataGridCell.BackgroundProperty, Brushes.Transparent));
        _results.CellStyle.Setters.Add(new Setter(DataGridCell.ForegroundProperty, Ink(220, 240, 237)));
        _results.CellStyle.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0)));
        _results.RowStyle = new Style(typeof(DataGridRow));
        _results.RowStyle.Setters.Add(new Setter(DataGridRow.BackgroundProperty, Brushes.Transparent));
        _results.RowStyle.Setters.Add(new Setter(DataGridRow.MinHeightProperty, 42d));
        var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(DataGridRow.BackgroundProperty, Ink(26, 85, 79)));
        selected.Setters.Add(new Setter(DataGridRow.ForegroundProperty, Brushes.White));
        _results.RowStyle.Triggers.Add(selected);
        AddColumn("MODULE", "Source", 174);
        AddColumn("MUTAPLASMID", "MutaplasmidName", 195);
        AddColumn("YIELD m³", "MiningAmountText", 92);
        AddColumn("CYCLE s", "CycleText", 92);
        AddColumn("EFF. m³/s", "EffectiveSpeedText", 112);
        AddColumn("CRIT", "CritText", 111);
        AddColumn("CPU Δ", "CpuDeltaText", 84);
        AddColumn("PG Δ", "PowergridDeltaText", 84);
        AddColumn("ASK", "PriceText", 104);
        AddColumn("EST.", "EstimateText", 102);
        AddColumn("VALUE GAP", "PriceAdvantageText", 112);
        AddColumn("EXPIRES", "ExpiryText", 160);
        _results.SelectionChanged += (_, _) => RefreshSelection();
        _results.MouseDoubleClick += (_, _) => OpenSelected();
        root.Children.Add(_results);

        var foot = new Border { Margin = new Thickness(0, 10, 0, 0), Background = Ink(13, 38, 42),
            Padding = new Thickness(11), BorderBrush = Ink(39, 81, 79), BorderThickness = new Thickness(1) };
        DockPanel.SetDock(foot, Dock.Bottom);
        var footRow = new DockPanel(); foot.Child = footRow;
        var footButtons = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(footButtons, Dock.Right); footRow.Children.Add(footButtons);
        ActionButton("VIEW LISTING", footButtons, "Open selected item's public detail page")
            .Click += (_, _) => OpenSelected();
        _detail.Foreground = Muted;
        _detail.Text = "Select a module to inspect its roll, price and contract details.";
        footRow.Children.Add(_detail);
        // Dock the detail before the results table so the table fills remaining space.
        root.Children.Remove(foot); root.Children.Insert(root.Children.IndexOf(_results), foot);

        var opt = _watch.Options;
        _enabled.IsChecked = opt.Enabled;
        _ore.IsChecked = opt.WatchOreStripMiner; _modulated.IsChecked = opt.WatchModulatedStripMiner;
        _single.IsChecked = opt.SingleItemContractsOnly;
        _asking.Text = (opt.MinAskingPrice / 1_000_000).ToString(CultureInfo.InvariantCulture);
        _estimated.Text = (opt.MinEstimatedValue / 1_000_000).ToString(CultureInfo.InvariantCulture);
        _amount.Text = opt.MinMiningAmount.ToString(CultureInfo.InvariantCulture);
        _speed.Text = opt.MinEffectiveSpeed.ToString("0.00", CultureInfo.InvariantCulture);
        _cpu.Text = opt.MaxCpuPenalty.ToString(CultureInfo.InvariantCulture);
        _pg.Text = opt.MaxPowergridPenalty.ToString(CultureInfo.InvariantCulture);
        _minutes.SelectedItem = Math.Clamp(opt.PollMinutes, 2, 30);
        _watch.Updated += Refresh;
        Closed += (_, _) => _watch.Updated -= Refresh;
        Refresh();
    }

    // All tabs share the same listing rows; the family tabs deliberately show
    // *all* observed contracts for their base module, not just alert matches.
    public static IReadOnlyList<AbyssalWatchListing> RowsForTab(int tab,
        IReadOnlyList<AbyssalWatchListing> matches,
        IReadOnlyList<AbyssalWatchListing> all,
        IReadOnlyList<AbyssalWatchListing> history) => tab switch
    {
        1 => all.Where(x => x.Source == "ORE Strip Miner").ToArray(),
        2 => all.Where(x => x.Source == "Modulated Strip Miner II").ToArray(),
        3 => all,
        4 => history,
        _ => matches
    };

    private static TabItem HunterTab(string label) => new()
    {
        Header = label, Background = Ink(17, 48, 51), Foreground = Teal,
        BorderBrush = Ink(43, 100, 91), Margin = new Thickness(0, 0, 7, 0),
        Padding = new Thickness(15, 7, 15, 7), MinHeight = 32,
        FontSize = 11, FontWeight = FontWeights.SemiBold
    };

    private sealed class HunterStatColor : IValueConverter
    {
        private readonly string _column;
        public HunterStatColor(string column) { _column = column; }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var raw = value?.ToString() ?? "";
            bool valid = double.TryParse(raw.TrimEnd('%'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var number);
            if (_column == "PriceText") return Ink(244, 206, 135);
            if (_column == "ExpiryText" && raw == "EXPIRED") return Ink(235, 121, 134);
            if (_column == "EffectiveSpeedText" && valid)
                return number >= 3.30 ? Ink(100, 224, 177) : Ink(235, 187, 113);
            if (_column == "MiningAmountText" && valid)
                return number >= 140 ? Ink(100, 224, 177) : Ink(235, 187, 113);
            if ((_column == "CpuDeltaText" || _column == "PowergridDeltaText") && valid)
                return number <= 0 ? Ink(100, 224, 177) : number <= 2 ? Ink(238, 201, 123) : Ink(240, 123, 140);
            if (_column == "PriceAdvantageText" && valid)
                return number >= 10 ? Ink(100, 224, 177) : number >= 0 ? Ink(238, 201, 123) : Ink(240, 123, 140);
            return Ink(211, 234, 234);
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private void AddColumn(string caption, string path, double width)
    {
        // Bind within a DataTemplate, not via Style.Setter.Value: WPF setters
        // cannot directly hold a Binding object for a data-row property.
        var cell = new FrameworkElementFactory(typeof(TextBlock));
        cell.SetBinding(TextBlock.TextProperty, new Binding(path));
        cell.SetBinding(TextBlock.ForegroundProperty,
            new Binding(path) { Converter = new HunterStatColor(path) });
        cell.SetValue(TextBlock.MarginProperty, new Thickness(8, 0, 8, 0));
        cell.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        cell.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        if (path is "MiningAmountText" or "CycleText" or "EffectiveSpeedText" or
            "CpuDeltaText" or "PowergridDeltaText" or "PriceText" or
            "EstimateText" or "PriceAdvantageText")
            cell.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
        var template = new DataTemplate { VisualTree = cell };
        _results.Columns.Add(new DataGridTemplateColumn
        {
            Header = caption, CellTemplate = template, SortMemberPath = path,
            Width = new DataGridLength(width)
        });
    }

    private void PreviewAlert()
    {
        var item = _results.SelectedItem as AbyssalWatchListing;
        if (item == null || !AbyssalWatchService.Matches(item, _watch.Options))
            item = _watch.Latest.FirstOrDefault();
        if (item == null)
        {
            System.Windows.MessageBox.Show(this,
                "No current listings pass your saved alert filters. Try CHECK NOW or adjust FILTERS.",
                "Abyssal Hunter", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AbyssalWatchToast.Show(new[] { item }, preview: true);
    }

    private static Button ActionButton(string title, Panel parent, string tooltip)
    {
        var button = new Button { Content = title, Height = 27, FontSize = 10,
            FontWeight = FontWeights.SemiBold, Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(3, 0, 3, 0), ToolTip = tooltip,
            Background = Ink(17, 50, 53), Foreground = Ink(203, 238, 235),
            BorderBrush = Ink(54, 113, 105), BorderThickness = new Thickness(1) };
        parent.Children.Add(button); return button;
    }
    private static void AddFilter(Panel parent, string label, TextBox input)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 12, 5) };
        stack.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = Muted, Margin = new Thickness(0, 0, 0, 3) });
        input.Background = Ink(9, 29, 33); input.Foreground = Brushes.WhiteSmoke;
        input.BorderBrush = Ink(52, 99, 93); input.Padding = new Thickness(5, 4, 5, 4);
        stack.Children.Add(input); parent.Children.Add(stack);
    }
    private static bool ReadNumber(TextBox box, out double val) =>
        double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out val) && double.IsFinite(val);
    private bool Save()
    {
        if (!ReadNumber(_asking, out var ask) || ask < 0 ||
            !ReadNumber(_estimated, out var est) || est < 0 ||
            !ReadNumber(_amount, out var amount) || amount < 0 ||
            !ReadNumber(_speed, out var speed) || speed < 0 ||
            !ReadNumber(_cpu, out var cpu) || !ReadNumber(_pg, out var pg))
        {
            System.Windows.MessageBox.Show(this, "Use numeric thresholds (a dot for decimals).", "Abyssal Hunter",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (_ore.IsChecked != true && _modulated.IsChecked != true)
        {
            System.Windows.MessageBox.Show(this, "Select at least one strip-miner family.", "Abyssal Hunter",
                MessageBoxButton.OK, MessageBoxImage.Warning); return false;
        }
        _watch.Save(new AbyssalWatchSettings {
            Enabled = _enabled.IsChecked == true,
            WatchOreStripMiner = _ore.IsChecked == true,
            WatchModulatedStripMiner = _modulated.IsChecked == true,
            SingleItemContractsOnly = _single.IsChecked == true,
            MinAskingPrice = ask * 1_000_000, MinEstimatedValue = est * 1_000_000,
            MinMiningAmount = amount, MinEffectiveSpeed = speed,
            MaxCpuPenalty = cpu, MaxPowergridPenalty = pg,
            PollMinutes = _minutes.SelectedItem is int n ? n : 5
        });
        return true;
    }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        var options = _tabs.SelectedIndex;
        var source = RowsForTab(options, _watch.Latest, _watch.AllListings, _watch.RecentAlerts);
        ((TabItem)_tabs.Items[0]).Header = $"MATCHES  {_watch.Latest.Count}";
        ((TabItem)_tabs.Items[1]).Header = $"ORE STRIP MINER  {_watch.AllListings.Count(x => x.Source == "ORE Strip Miner")}";
        ((TabItem)_tabs.Items[2]).Header = $"MODULATED II  {_watch.AllListings.Count(x => x.Source == "Modulated Strip Miner II")}";
        ((TabItem)_tabs.Items[3]).Header = $"BROWSE ALL  {_watch.AllListings.Count}";
        ((TabItem)_tabs.Items[4]).Header = $"ALERT HISTORY  {_watch.RecentAlerts.Count}";
        string needle = _search.Text.Trim();
        var filtered = source.Where(x => needle.Length == 0 ||
            x.Source.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            x.MutaplasmidName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            x.ContractId.ToString(CultureInfo.InvariantCulture).Contains(needle) ||
            x.ItemId.ToString(CultureInfo.InvariantCulture).Contains(needle))
            .OrderByDescending(x => x.EffectiveSpeed).ToArray();
        var selectedId = (_results.SelectedItem as AbyssalWatchListing)?.Key;
        _results.ItemsSource = filtered;
        if (selectedId != null) _results.SelectedItem = filtered.FirstOrDefault(x => x.Key == selectedId);
        _summary.Text = $"{_watch.Latest.Count} MATCHES   ·   {_watch.AllListings.Count} BROWSED   ·   {_watch.RecentAlerts.Count} ALERTS";
        _status.Text = $"Last check: {_watch.LastChecked?.ToLocalTime().ToString("dd MMM HH:mm") ?? "never"}  |  " +
            (string.IsNullOrWhiteSpace(_watch.LastError) ? "Watching public contracts" : _watch.LastError) +
            "  |  Popups only for NEW matches or 10%+ price drops; first scan is a quiet baseline. Use PREVIEW ALERT to test.";
        RefreshSelection();
    }
    private void RefreshSelection()
    {
        if (_results.SelectedItem is AbyssalWatchListing item)
            _detail.Text = $"{item.Source} · {item.MutaplasmidName}  |  {item.QualityText}  |  " +
                $"Ask {item.PriceText}, estimate {item.EstimateText} (not a guaranteed sale price)  |  " +
                $"{item.ExpiryText}  |  Contract #{item.ContractId}";
        else _detail.Text = "Select a module to inspect roll quality, asking price, estimated value and contract details.";
    }
    private void OpenSelected()
    {
        if (_results.SelectedItem is AbyssalWatchListing item) AbyssalWatchToast.OpenUrl(item.Url);
    }
}
