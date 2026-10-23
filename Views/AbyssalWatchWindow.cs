using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EveCommandCenter.Services;
// This app compiles both WPF and Windows Forms. Explicitly bind UI controls to WPF.
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using DataGrid = System.Windows.Controls.DataGrid;
using MessageBox = System.Windows.MessageBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace EveCommandCenter.Views;

public sealed class AbyssalWatchWindow : Window
{
    private readonly AbyssalWatchService _watch = AbyssalWatchService.Instance;
    private readonly CheckBox _enabled = new() { Content = "Enable background popup alerts" };
    private readonly CheckBox _ore = new() { Content = "ORE Strip Miner" };
    private readonly CheckBox _modulated = new() { Content = "Modulated Strip Miner II" };
    private readonly CheckBox _single = new() { Content = "Single abyssal item contracts only (avoid bundle price ambiguity)" };
    private readonly TextBox _asking = new() { Width = 95 };
    private readonly TextBox _estimated = new() { Width = 95 };
    private readonly TextBox _amount = new() { Width = 95 };
    private readonly TextBox _speed = new() { Width = 95 };
    private readonly TextBox _cpu = new() { Width = 95 };
    private readonly TextBox _pg = new() { Width = 95 };
    private readonly ComboBox _minutes = new() { Width = 85 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSeaGreen };
    private readonly DataGrid _results = new() { AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, RowHeight = 31 };

    public AbyssalWatchWindow()
    {
        Title = "EVE Command Center | Abyssal Market Watch";
        Width = 1160; Height = 740; MinWidth = 850; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(8, 25, 28));
        Foreground = Brushes.WhiteSmoke;
        var root = new DockPanel { Margin = new Thickness(16) };
        Content = root;
        var top = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = "ABYSSAL MARKET WATCH", FontWeight = FontWeights.Bold, FontSize = 23, Foreground = Brushes.MediumAquamarine });
        top.Children.Add(new TextBlock { Text = "Both strip-miner models • public MutaMarket listings • no additional EVE permissions", Margin = new Thickness(0, 4, 0, 12) });
        var types = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(types);
        foreach (var cb in new[] { _enabled, _ore, _modulated, _single }) { cb.Margin = new Thickness(0, 0, 18, 4); types.Children.Add(cb); }
        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(filters);
        Filter(filters, "Min asking (M ISK)", _asking);
        Filter(filters, "Min estimate (M; 0=off)", _estimated);
        Filter(filters, "Min yield (m³)", _amount);
        Filter(filters, "Min effective (m³/s)", _speed);
        Filter(filters, "Max CPU delta (tf)", _cpu);
        Filter(filters, "Max PG delta (MW)", _pg);
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        top.Children.Add(actions);
        actions.Children.Add(new TextBlock { Text = "Scan every", VerticalAlignment = VerticalAlignment.Center });
        _minutes.Margin = new Thickness(8, 0, 16, 0);
        foreach (int n in new[] { 2, 5, 10, 15, 30 }) _minutes.Items.Add(n);
        actions.Children.Add(_minutes);
        var save = AddButton("SAVE FILTERS", actions);
        save.Click += (_, _) => Save();
        var refresh = AddButton("CHECK NOW", actions);
        refresh.Click += async (_, _) => { if (Save()) await _watch.RefreshAsync(); };
        var open = AddButton("OPEN SELECTED LISTING", actions);
        open.Click += (_, _) => OpenSelected();
        _status.Margin = new Thickness(0, 0, 0, 8);
        top.Children.Add(_status);
        top.Children.Add(new TextBlock { Text = "New listings are checked against all enabled thresholds. The first scan establishes a baseline and never spams existing contracts. Availability is limited by MutaMarket's own contract feed, which may lag EVE by ~30 minutes.", FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray });
        _results.Background = new SolidColorBrush(Color.FromRgb(13, 37, 42));
        _results.MouseDoubleClick += (_, _) => OpenSelected();
        Column("MODEL", "Source", 230);
        Column("ASK", "PriceText", 125);
        Column("EST. VALUE", "EstimateText", 115);
        Column("YIELD", "MiningAmount", 90);
        Column("EFF. m³/s", "EffectiveSpeed", 110);
        Column("CPU Δ", "CpuPenalty", 85);
        Column("PG Δ", "PowergridPenalty", 85);
        Column("CONTRACT", "ContractId", 140);
        root.Children.Add(_results);
        var opt = _watch.Options;
        _enabled.IsChecked = opt.Enabled; _ore.IsChecked = opt.WatchOreStripMiner;
        _modulated.IsChecked = opt.WatchModulatedStripMiner; _single.IsChecked = opt.SingleItemContractsOnly;
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

    private static void Filter(Panel parent, string title, TextBox entry)
    {
        var box = new StackPanel { Margin = new Thickness(0, 0, 14, 7) };
        box.Children.Add(new TextBlock { Text = title, FontSize = 11, Margin = new Thickness(0, 0, 0, 3) });
        entry.Padding = new Thickness(5, 3, 5, 3);
        box.Children.Add(entry); parent.Children.Add(box);
    }
    private static Button AddButton(string caption, Panel parent)
    {
        var button = new Button { Content = caption, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 7, 0) };
        parent.Children.Add(button); return button;
    }
    private void Column(string caption, string path, double width) =>
        _results.Columns.Add(new DataGridTextColumn { Header = caption, Binding = new System.Windows.Data.Binding(path), Width = new DataGridLength(width) });
    private static bool Read(TextBox box, out double value) =>
        double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private bool Save()
    {
        if (!Read(_asking, out var ask) || ask < 0 || !Read(_estimated, out var estimate) || estimate < 0 ||
            !Read(_amount, out var amount) || amount < 0 || !Read(_speed, out var speed) || speed < 0 ||
            !Read(_cpu, out var cpu) || !Read(_pg, out var pg))
        {
            MessageBox.Show(this, "Enter valid numeric thresholds, using a dot for decimals.", "Abyssal Market Watch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (_ore.IsChecked != true && _modulated.IsChecked != true)
        {
            MessageBox.Show(this, "Select at least one strip-miner family.", "Abyssal Market Watch", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        _watch.Save(new AbyssalWatchSettings {
            Enabled = _enabled.IsChecked == true,
            WatchOreStripMiner = _ore.IsChecked == true,
            WatchModulatedStripMiner = _modulated.IsChecked == true,
            SingleItemContractsOnly = _single.IsChecked == true,
            MinAskingPrice = ask * 1_000_000,
            MinEstimatedValue = estimate * 1_000_000,
            MinMiningAmount = amount, MinEffectiveSpeed = speed,
            MaxCpuPenalty = cpu, MaxPowergridPenalty = pg,
            PollMinutes = _minutes.SelectedItem is int n ? n : 5
        });
        return true;
    }
    private void Refresh()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(Refresh)); return; }
        _results.ItemsSource = _watch.Latest.OrderByDescending(x => x.EffectiveSpeed).ToArray();
        _status.Text = $"Last checked: {_watch.LastChecked?.ToLocalTime().ToString("dd MMM HH:mm:ss") ?? "never"} | Matches: {_watch.Latest.Count} | " +
            (string.IsNullOrEmpty(_watch.LastError) ? "Monitoring" : _watch.LastError);
    }
    private void OpenSelected()
    {
        if (_results.SelectedItem is not AbyssalWatchListing item) return;
        AbyssalWatchToast.OpenUrl(item.Url);
    }
}
