using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using WpfBorder = System.Windows.Controls.Border;
using WpfButton = System.Windows.Controls.Button;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfGrid = System.Windows.Controls.Grid;
using WpfImage = System.Windows.Controls.Image;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfWrapPanel = System.Windows.Controls.WrapPanel;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfCursors = System.Windows.Input.Cursors;
using WpfBitmapImage = System.Windows.Media.Imaging.BitmapImage;
using WpfDispatcherTimer = System.Windows.Threading.DispatcherTimer;

namespace EveMultiPreview.Views;

public partial class MoonReportWindow : Window
{
    private readonly EveSsoService _sso = new();
    private readonly MoonReportService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<EvePilotProfile> _pilots = Array.Empty<EvePilotProfile>();
    private MoonReportSnapshot _snapshot = new();
    private string _filter = "ALL";
    private string _calendarMode = "MONTH";
    private DateTime _calendarDate = DateTime.Today;
    private DateTime? _expandedMonthDate;
    private bool _busy;
    private bool _loadingReports;
    private bool _loadingLedger;
    private readonly WpfDispatcherTimer _refreshTimer;

    public MoonReportWindow()
    {
        InitializeComponent();
        _service = new MoonReportService(_sso);
        _refreshTimer = new WpfDispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(61)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await ReloadPilotsAsync();
            ApplySnapshot(_service.GetSnapshot());
            if (_pilots.Count == 0)
                SetStatus("No ESI characters are connected. Use RECONNECT / ADD.", true);
            else if (PilotCombo.SelectedItem is EvePilotProfile selected &&
                     !MoonReportService.HasRequiredScopes(selected))
                SetStatus("This toon needs reconnecting once to approve the moon report scopes.", true);
            _refreshTimer.Start();
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _service.Dispose();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_busy || !IsVisible ||
            PilotCombo.SelectedItem is not EvePilotProfile pilot ||
            !MoonReportService.HasRequiredScopes(pilot))
            return;
        SetStatus("Hourly ESI moon and ledger refresh started...");
        await RefreshSelectedPilotAsync();
    }

    private async Task ReloadPilotsAsync(long preferred = 0)
    {
        _pilots = await _sso.LoadPilotsAsync();
        PilotCombo.ItemsSource = _pilots;
        long id = preferred > 0 ? preferred : _service.SelectedCharacterId;
        PilotCombo.SelectedItem = _pilots.FirstOrDefault(p => p.CharacterId == id)
            ?? _pilots.FirstOrDefault();
    }

    private async void UsePilot_Click(object sender, RoutedEventArgs e)
    {
        if (PilotCombo.SelectedItem is not EvePilotProfile pilot)
        {
            SetStatus("Choose a corporation-data toon first.", true);
            return;
        }
        await _service.SelectPilotAsync(pilot.CharacterId);
        if (!MoonReportService.HasRequiredScopes(pilot))
        {
            SetStatus($"{pilot.CharacterName} needs RECONNECT / ADD once to approve the moon scopes.", true);
            return;
        }
        await RefreshSelectedPilotAsync();
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            SetStatus("Complete EVE authorization in the browser.");
            EvePilotProfile pilot = await _sso.AddCharacterAsync(_lifetime.Token);
            await ReloadPilotsAsync(pilot.CharacterId);
            await _service.SelectPilotAsync(pilot.CharacterId);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        catch (Exception ex) { SetStatus(ex.Message, true); return; }
        finally { SetBusy(false); }
        await RefreshSelectedPilotAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) =>
        await RefreshSelectedPilotAsync();

    private async Task RefreshSelectedPilotAsync()
    {
        if (_busy) return;
        if (PilotCombo.SelectedItem is not EvePilotProfile pilot)
        {
            SetStatus("Choose a corporation-data toon first.", true);
            return;
        }
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(SetStatus);
            ApplySnapshot(await _service.RefreshAsync(pilot, progress, _lifetime.Token));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    private void ApplySnapshot(MoonReportSnapshot snapshot)
    {
        _snapshot = snapshot;
        ScheduledText.Text = snapshot.ScheduledCount.ToString("N0");
        ActiveText.Text = (snapshot.ReadyCount + snapshot.ActiveFieldCount).ToString("N0");
        MinedText.Text = MoonReportService.FormatM3(snapshot.TotalMinedM3);
        LostText.Text = MoonReportService.FormatM3(snapshot.TotalLostM3);
        JackpotText.Text = snapshot.JackpotCount.ToString("N0");
        DespawnText.Text = snapshot.TargetDespawnCount.ToString("N0");
        AuditGrid.ItemsSource = snapshot.Audit;
        UpdatedText.Text = snapshot.GeneratedUtc == default ? "Not refreshed" :
            "Updated " + snapshot.GeneratedUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss");
        ApplyFilters();
        RenderCalendar();
        LoadLedger();
        LoadReporting();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton button) _filter = button.Tag?.ToString() ?? "ALL";
        ApplyFilters();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilters();

    private void ApplyFilters()
    {
        if (MoonItems == null || SearchBox == null) return;
        IEnumerable<MoonCardView> cards = _snapshot.Cards;
        cards = _filter switch
        {
            "UPCOMING" => cards.Where(c => c.Status is "SCHEDULED" or "READY"),
            "ACTIVE" => cards.Where(c => c.Status == "FIELD ACTIVE"),
            "ALERT" => cards.Where(c => c.HasTargetLeftover),
            "JACKPOT" => cards.Where(c => c.IsJackpot),
            _ => cards
        };
        string search = SearchBox.Text.Trim();
        if (search.Length > 0)
            cards = cards.Where(c => Contains(c.MoonName, search) ||
                Contains(c.StructureName, search) || Contains(c.SystemName, search));
        MoonItems.ItemsSource = cards.ToArray();
    }

    private void CalendarMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton button) _calendarMode = button.Tag?.ToString() ?? "MONTH";
        _expandedMonthDate = null;
        RenderCalendar();
    }

    private void PreviousPeriod_Click(object sender, RoutedEventArgs e)
    {
        _calendarDate = _calendarMode == "MONTH" ? _calendarDate.AddMonths(-1) :
            _calendarMode == "WEEK" ? _calendarDate.AddDays(-7) : _calendarDate.AddDays(-1);
        RenderCalendar();
    }

    private void NextPeriod_Click(object sender, RoutedEventArgs e)
    {
        _calendarDate = _calendarMode == "MONTH" ? _calendarDate.AddMonths(1) :
            _calendarMode == "WEEK" ? _calendarDate.AddDays(7) : _calendarDate.AddDays(1);
        RenderCalendar();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        _calendarDate = DateTime.Today;
        RenderCalendar();
    }

    private void RenderCalendar()
    {
        if (CalendarHost == null) return;
        UpdateCalendarModeButtons();
        CalendarHost.Children.Clear();
        if (_calendarMode == "MONTH") RenderMonth();
        else if (_calendarMode == "WEEK") RenderWeek();
        else RenderDay();
    }

    private void UpdateCalendarModeButtons()
    {
        if (MonthModeButton == null) return;
        foreach (WpfButton button in new[]
                 { MonthModeButton, WeekModeButton, DayModeButton })
        {
            bool selected = string.Equals(
                button.Tag?.ToString(), _calendarMode,
                StringComparison.OrdinalIgnoreCase);
            button.Background = Brush(selected ? "#17656A" : "#12383D");
            button.BorderBrush = Brush(selected ? "#55D7D2" : "#2C6269");
            button.Foreground = Brush(selected ? "#FFFFFF" : "#DDF6F4");
        }
    }

    private void RenderMonth()
    {
        DateTime first = new(_calendarDate.Year, _calendarDate.Month, 1);
        DateTime start = StartOfWeek(first);
        CalendarTitle.Text = first.ToString("MMMM yyyy").ToUpperInvariant();
        var grid = new WpfGrid();
        for (int i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        for (int i = 0; i < 7; i++) grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = i == 0 ? GridLength.Auto : new GridLength(152) });
        string[] headers = { "MON", "TUE", "WED", "THU", "FRI", "SAT", "SUN" };
        for (int i = 0; i < 7; i++)
        {
            WpfTextBlock header = Text(headers[i], 12, "#7EA8AB", true);
            header.Margin = new Thickness(7, 3, 7, 7);
            WpfGrid.SetColumn(header, i); grid.Children.Add(header);
        }
        for (int index = 0; index < 42; index++)
        {
            DateTime date = start.AddDays(index);
            WpfBorder tile = BuildDayTile(date, date.Month == first.Month);
            WpfGrid.SetRow(tile, index / 7 + 1); WpfGrid.SetColumn(tile, index % 7);
            grid.Children.Add(tile);
        }
        CalendarHost.Children.Add(grid);
        if (_expandedMonthDate.HasValue)
        {
            var detailBorder = new WpfBorder
            {
                Child = BuildDayPanel(_expandedMonthDate.Value, true),
                Margin = new Thickness(3, 14, 3, 2),
                Padding = new Thickness(14),
                Background = Brush("#0A2226"),
                BorderBrush = Brush("#3B7378"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8)
            };
            CalendarHost.Children.Add(detailBorder);
        }
    }

    private WpfBorder BuildDayTile(DateTime date, bool inMonth)
    {
        MoonDailyTotalView? total = _snapshot.DailyTotals.FirstOrDefault(d => d.Date.Date == date.Date);
        MoonCardView[] cards = CardsForDate(date);
        var panel = new WpfStackPanel { Margin = new Thickness(8) };
        var heading = new WpfGrid();
        heading.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        heading.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(Text(date.Day.ToString(), 16, inMonth ? "#E8FFFF" : "#57777A", true));
        WpfTextBlock badge = Text(cards.Length > 0 ? cards.Length + " moon" + (cards.Length == 1 ? "" : "s") : "", 10, "#55D7D2", true);
        WpfGrid.SetColumn(badge, 1); heading.Children.Add(badge); panel.Children.Add(heading);
        if ((total?.TotalM3 ?? 0) > 0) panel.Children.Add(Text("Mined  " + MoonReportService.FormatM3(total!.TotalM3), 11, "#74D6C9"));
        if ((total?.LostM3 ?? 0) > 0) panel.Children.Add(Text("Despawn  " + MoonReportService.FormatM3(total!.LostM3), 11, "#EF7770", true));
        foreach (MoonCardView card in cards.Take(3))
            panel.Children.Add(BuildMiniMoonRow(card));
        if (cards.Length > 3) panel.Children.Add(Text("+ " + (cards.Length - 3) + " more", 10, "#789EA1"));
        var border = new WpfBorder
        {
            Child = panel, Margin = new Thickness(3), CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1), BorderBrush = Brush("#245058"),
            Background = Brush(_expandedMonthDate?.Date == date.Date ? "#164148" : inMonth ? "#0C2529" : "#091D20"),
            Cursor = WpfCursors.Hand
        };
        border.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                _calendarDate = date;
                _calendarMode = "DAY";
                _expandedMonthDate = null;
            }
            else
            {
                _expandedMonthDate = date;
            }
            RenderCalendar();
        };
        return border;
    }

    private void RenderWeek()
    {
        DateTime start = StartOfWeek(_calendarDate);
        CalendarTitle.Text = start.ToString("dd MMM") + " - " + start.AddDays(6).ToString("dd MMM yyyy");
        var grid = new WpfGrid();
        for (int i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        for (int i = 0; i < 7; i++)
        {
            WpfStackPanel day = BuildDayPanel(start.AddDays(i), false);
            var shell = new WpfBorder
            {
                Child = day,
                Margin = new Thickness(3),
                Padding = new Thickness(8),
                MinHeight = 560,
                Background = Brush(start.AddDays(i).Date == DateTime.Today
                    ? "#102F34" : "#091F23"),
                BorderBrush = Brush(start.AddDays(i).Date == DateTime.Today
                    ? "#55D7D2" : "#245058"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7)
            };
            WpfGrid.SetColumn(shell, i); grid.Children.Add(shell);
        }
        CalendarHost.Children.Add(grid);
    }

    private void RenderDay()
    {
        CalendarTitle.Text = _calendarDate.ToString("dddd, dd MMMM yyyy").ToUpperInvariant();
        CalendarHost.Children.Add(BuildDayPanel(_calendarDate, true));
    }

    private WpfStackPanel BuildDayPanel(DateTime date, bool detailed)
    {
        MoonDailyTotalView? total = _snapshot.DailyTotals.FirstOrDefault(d => d.Date.Date == date.Date);
        MoonCardView[] cards = CardsForDate(date);
        var root = new WpfStackPanel();
        root.Children.Add(Text(date.ToString("ddd dd MMM").ToUpperInvariant(), detailed ? 20 : 15, "#E8FFFF", true));
        root.Children.Add(Text($"{cards.Length} moon(s) · mined {MoonReportService.FormatM3(total?.TotalM3 ?? 0)} · despawn {MoonReportService.FormatM3(total?.LostM3 ?? 0)}", detailed ? 12 : 10, "#7EA8AB"));
        if (detailed && total != null)
            root.Children.Add(Text($"Zeo {MoonReportService.FormatM3(total.ZeolitesM3)}  ·  Syl {MoonReportService.FormatM3(total.SylviteM3)}  ·  Bit {MoonReportService.FormatM3(total.BitumensM3)}  ·  Coe {MoonReportService.FormatM3(total.CoesiteM3)}", 12, "#A8CFD0"));
        if (detailed)
        {
            var cardsHost = new WpfWrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            foreach (MoonCardView card in cards)
                cardsHost.Children.Add(BuildCalendarCard(card, true));
            root.Children.Add(cardsHost);
        }
        else
        {
            foreach (MoonCardView card in cards)
                root.Children.Add(BuildCalendarCard(card, false));
        }
        if (cards.Length == 0) root.Children.Add(Text("No scheduled fractures for this day.", 11, "#567B7F"));
        if (_calendarMode == "MONTH")
        {
            var open = new WpfButton { Content = "OPEN FULL DAY", Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            open.Click += (_, _) => { _calendarDate = date; _calendarMode = "DAY"; _expandedMonthDate = null; RenderCalendar(); };
            root.Children.Add(open);
        }
        return root;
    }

    private WpfBorder BuildCalendarCard(MoonCardView card, bool detailed)
    {
        double iconSize = detailed ? 74 : 44;
        var layout = new WpfGrid();
        layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(iconSize + 12) });
        layout.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        WpfGrid icon = BuildMoonStructureIcon(card, iconSize);
        layout.Children.Add(icon);

        var body = new WpfStackPanel();
        WpfGrid.SetColumn(body, 1);
        body.Children.Add(Text((card.IsJackpot ? "★ " : "") + card.MoonName, detailed ? 16 : 12, card.IsJackpot ? "#FFD166" : "#E8FFFF", true));
        body.Children.Add(Text(card.StructureName, detailed ? 11 : 9, "#8FB2B5"));
        body.Children.Add(Text(card.ScheduleValue, detailed ? 11 : 9, "#55D7D2", true));
        var status = new WpfBorder
        {
            Child = Text(card.Status, 9, "#071416", true),
            Background = Brush(card.StatusBrush),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        body.Children.Add(status);
        layout.Children.Add(body);

        var root = new WpfStackPanel();
        root.Children.Add(layout);
        WpfTextBlock profile = Text(card.OreSummary, detailed ? 11 : 9, "#A8CFD0", true);
        profile.Margin = new Thickness(0, 7, 0, 0);
        profile.TextWrapping = TextWrapping.Wrap;
        root.Children.Add(profile);
        root.Children.Add(Text(card.RemainingSummary, detailed ? 11 : 9,
            card.HasTargetLeftover ? "#FF8A80" : "#74D6C9", true));

        if (card.HasTargetProfile && card.InitialTotalM3 > 0)
        {
            var progress = new WpfBorder
            {
                Height = 6,
                Width = detailed ? 250 : 130,
                Margin = new Thickness(0, 5, 0, 1),
                Background = Brush("#173A3F"),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };
            progress.Child = new WpfBorder
            {
                Width = (detailed ? 2.5 : 1.3) * card.RemainingPercent,
                Background = Brush(card.RemainingPercent > 20 ? "#55D7D2" : "#EF7770"),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left
            };
            root.Children.Add(progress);
        }
        if (detailed)
        {
            foreach (MoonOreRowView ore in card.OreRows)
            {
                WpfTextBlock row = Text(
                    ore.Name + "  ·  mined " + ore.Mined +
                    "  ·  est. left " + ore.Remaining,
                    11,
                    ore.Color);
                row.Margin = new Thickness(0, 4, 0, 0);
                root.Children.Add(row);
            }
            root.Children.Add(Text("Last fracture: " + card.LastFracture, 10, "#658C8F"));
            var edit = new WpfButton { Content = "ORE PROFILE", DataContext = card, Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 5, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
            edit.Click += Profile_Click; root.Children.Add(edit);
        }
        return new WpfBorder { Width = detailed ? 420 : double.NaN, MinHeight = detailed ? 260 : 0, Child = root, Background = Brush(card.IsJackpot ? "#332F18" : "#0D292D"), BorderBrush = Brush(card.IsJackpot ? "#D1A72D" : "#28545A"), BorderThickness = new Thickness(card.IsJackpot ? 2 : 1), CornerRadius = new CornerRadius(7), Padding = new Thickness(10), Margin = new Thickness(detailed ? 5 : 0, 6, detailed ? 5 : 0, 0) };
    }

    private WpfGrid BuildMiniMoonRow(MoonCardView card)
    {
        var row = new WpfGrid { Margin = new Thickness(0, 3, 0, 0) };
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(23) });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition());
        row.Children.Add(BuildMoonStructureIcon(card, 19));
        WpfTextBlock name = Text((card.IsJackpot ? "★ " : "") + card.MoonName,
            10, card.IsJackpot ? "#FFD166" : "#B6D4D5", card.IsJackpot);
        name.VerticalAlignment = VerticalAlignment.Center;
        WpfGrid.SetColumn(name, 1); row.Children.Add(name);
        return row;
    }

    private static WpfGrid BuildMoonStructureIcon(MoonCardView card, double size)
    {
        var icon = new WpfGrid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };
        var moon = new WpfImage
        {
            Source = new WpfBitmapImage(new Uri(card.MoonImageUri)),
            Width = size,
            Height = size,
            Stretch = System.Windows.Media.Stretch.UniformToFill,
            Clip = new System.Windows.Media.EllipseGeometry(
                new Point(size / 2, size / 2), size / 2, size / 2)
        };
        icon.Children.Add(moon);
        if (size >= 40)
        {
            double stationSize = size * 0.42;
            var stationShell = new WpfBorder
            {
                Width = stationSize,
                Height = stationSize,
                Background = Brush("#071416"),
                BorderBrush = Brush(card.IsJackpot ? "#FFD166" : "#55D7D2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(stationSize / 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            stationShell.Child = new WpfImage
            {
                Source = new WpfBitmapImage(new Uri(card.StructureImageUri)),
                Stretch = System.Windows.Media.Stretch.Uniform
            };
            icon.Children.Add(stationShell);
        }
        return icon;
    }

    private MoonCardView[] CardsForDate(DateTime date) => _snapshot.CalendarCards
        .Where(c => c.ScheduleUtc?.ToLocalTime().Date == date.Date)
        .OrderBy(c => c.ScheduleUtc).ToArray();

    private void LoadLedger()
    {
        if (LedgerMoonCombo == null || LedgerPullCombo == null ||
            LedgerGrid == null) return;

        string selectedName =
            (LedgerMoonCombo.SelectedItem as MoonLedgerMoonView)?.MoonName ?? "";
        _loadingLedger = true;
        LedgerMoonCombo.ItemsSource = _snapshot.LedgerMoons;
        MoonLedgerMoonView? selected = _snapshot.LedgerMoons.FirstOrDefault(
            moon => moon.MoonName.Equals(
                selectedName, StringComparison.OrdinalIgnoreCase));
        selected ??= _snapshot.LedgerMoons.FirstOrDefault(
            moon => _snapshot.LedgerPulls.Any(pull => LedgerMoonMatches(moon, pull)));
        selected ??= _snapshot.LedgerMoons.FirstOrDefault();
        LedgerMoonCombo.SelectedItem = selected;
        _loadingLedger = false;
        LoadLedgerPulls();
    }

    private void LoadLedgerPulls()
    {
        if (LedgerMoonCombo.SelectedItem is not MoonLedgerMoonView moon)
        {
            ShowLedgerPull(null);
            return;
        }

        string selectedPullId =
            (LedgerPullCombo.SelectedItem as MoonLedgerPullView)?.PullId ?? "";
        MoonLedgerPullView[] pulls = _snapshot.LedgerPulls
            .Where(pull => LedgerMoonMatches(moon, pull))
            .OrderByDescending(pull => pull.FractureUtc)
            .ToArray();
        _loadingLedger = true;
        LedgerPullCombo.ItemsSource = pulls;
        LedgerPullCombo.SelectedItem = pulls.FirstOrDefault(
            pull => pull.PullId == selectedPullId) ?? pulls.FirstOrDefault();
        _loadingLedger = false;
        ShowLedgerPull(LedgerPullCombo.SelectedItem as MoonLedgerPullView);
    }

    private static bool LedgerMoonMatches(
        MoonLedgerMoonView moon,
        MoonLedgerPullView pull)
    {
        if (moon.MoonId > 0 && pull.MoonId > 0)
            return moon.MoonId == pull.MoonId;
        return moon.MoonName.Equals(
            pull.MoonName, StringComparison.OrdinalIgnoreCase);
    }

    private void ShowLedgerPull(MoonLedgerPullView? pull)
    {
        if (LedgerGrid == null) return;
        LedgerGrid.ItemsSource = pull?.Rows ?? Array.Empty<MoonLedgerRowView>();
        LedgerMinerCountText.Text = (pull?.Rows.Count ?? 0).ToString("N0");
        LedgerTotalM3Text.Text = MoonReportService.FormatM3(pull?.TotalM3 ?? 0);
        LedgerTotalIskText.Text = CompactIsk(pull?.TotalIsk ?? 0);
        LedgerStatusText.Text = pull == null
            ? "NO LEDGER DATA"
            : pull.JackpotObserved ? "★ JACKPOT OBSERVED" : "STANDARD PULL";
        LedgerStatusText.Foreground = Brush(
            pull?.JackpotObserved == true ? "#FFD166" : "#8FB2B5");
    }

    private void LedgerMoon_Changed(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loadingLedger) LoadLedgerPulls();
    }

    private void LedgerPull_Changed(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_loadingLedger)
            ShowLedgerPull(LedgerPullCombo.SelectedItem as MoonLedgerPullView);
    }

    private void LoadReporting()
    {
        if (ReportKindCombo == null || PeriodGrid == null ||
            CompareACombo == null || CompareBCombo == null) return;
        _loadingReports = true;
        IReadOnlyList<MoonPeriodReportView> reports = IsMonthlyReport()
            ? _snapshot.MonthlyReports : _snapshot.WeeklyReports;
        PeriodGrid.ItemsSource = reports;
        CompareACombo.ItemsSource = reports; CompareBCombo.ItemsSource = reports;
        CompareACombo.SelectedIndex = reports.Count > 0 ? 0 : -1;
        CompareBCombo.SelectedIndex = reports.Count > 1 ? 1 : reports.Count > 0 ? 0 : -1;
        _loadingReports = false;
        UpdateComparison();
    }

    private void ReportSelection_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loadingReports || CompareMinedText == null) return;
        if (ReferenceEquals(sender, ReportKindCombo)) LoadReporting(); else UpdateComparison();
    }

    private bool IsMonthlyReport() =>
        (ReportKindCombo.SelectedItem as WpfComboBoxItem)?.Content?.ToString() != "WEEKS";

    private void UpdateComparison()
    {
        if (CompareACombo.SelectedItem is not MoonPeriodReportView a || CompareBCombo.SelectedItem is not MoonPeriodReportView b)
        {
            CompareMinedText.Text = CompareLostText.Text = CompareEfficiencyText.Text = CompareWinnerText.Text = "-";
            return;
        }
        CompareMinedText.Text = SignedM3(a.MinedM3 - b.MinedM3) + " (A vs B)";
        CompareLostText.Text = SignedM3(a.LostM3 - b.LostM3) + " (A vs B)";
        CompareEfficiencyText.Text = a.EfficiencyPercent.ToString("0.0") + "% vs " + b.EfficiencyPercent.ToString("0.0") + "%";
        CompareWinnerText.Text = a.MinedM3 == b.MinedM3 ? "Tie" : (a.MinedM3 > b.MinedM3 ? a.Label : b.Label);
    }

    private async void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not MoonCardView card) return;
        MoonProfile? profile = ShowProfileEditor(card.Profile);
        if (profile == null) return;
        try { await _service.SaveProfileAsync(profile); ApplySnapshot(_service.GetSnapshot()); SetStatus("Saved ore profile for " + profile.MoonName + "."); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import Moon Report setup", Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            string[] lines = await File.ReadAllLinesAsync(dialog.FileName, _lifetime.Token);
            var profiles = new List<MoonProfile>();
            foreach (string line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                List<string> v = ParseCsvLine(line);
                if (v.Count < 8 || !long.TryParse(v[0], out long moonId)) continue;
                bool modern = v.Count >= 10;
                profiles.Add(new MoonProfile { MoonId = moonId, MoonName = v[1], StructureName = v[2], SystemName = v[3], ProfileConfigured = true,
                    ZeolitesPercent = ParseNumber(v[4], 0), SylvitePercent = modern ? ParseNumber(v[5], 0) : 0,
                    BitumensPercent = ParseNumber(v[modern ? 6 : 5], 0), CoesitePercent = modern ? ParseNumber(v[7], 0) : 0,
                    FieldLifetimeHours = ParseNumber(v[modern ? 8 : 6], 48), WastePercent = ParseNumber(v[modern ? 9 : 7], 7) });
            }
            await _service.ImportProfilesAsync(profiles); ApplySnapshot(_service.GetSnapshot()); SetStatus($"Imported {profiles.Count:N0} moon profiles.");
        }
        catch (Exception ex) { SetStatus("Could not import moon setup: " + ex.Message, true); }
    }

    private async void ImportLseAudit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            MoonProfileImportResult result =
                await _service.ImportProfilesByNameAsync(
                    LseMoonAuditService.GetProfiles());
            ApplySnapshot(_service.GetSnapshot());
            SetStatus(
                $"Loaded {result.Total:N0} LSHI moon profiles from " +
                $"{LseMoonAuditService.SourceLabel}. " +
                $"Matched {result.Matched:N0}; {result.Pending:N0} will " +
                "attach automatically when ESI reveals those moon IDs.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not load the LSE moon audit: " + ex.Message, true);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export Moon Report setup", FileName = "moon-report-setup.csv", DefaultExt = ".csv", Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var lines = new List<string> { "MoonId,MoonName,StructureName,SystemName,ZeolitesPercent,SylvitePercent,BitumensPercent,CoesitePercent,FieldLifetimeHours,WastePercent" };
            lines.AddRange(_service.ExportProfiles().Select(p => string.Join(",", Csv(p.MoonId.ToString(CultureInfo.InvariantCulture)), Csv(p.MoonName), Csv(p.StructureName), Csv(p.SystemName), Number(p.ZeolitesPercent), Number(p.SylvitePercent), Number(p.BitumensPercent), Number(p.CoesitePercent), Number(p.FieldLifetimeHours), Number(p.WastePercent))));
            File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(false));
            SetStatus($"Exported {_service.ExportProfiles().Count:N0} moon profiles.");
        }
        catch (Exception ex) { SetStatus("Could not export moon setup: " + ex.Message, true); }
    }

    private MoonProfile? ShowProfileEditor(MoonProfile source)
    {
        var window = new Window { Owner = this, Title = "Ore profile · " + source.MoonName, Width = 500, Height = 555, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brush("#07181B"), Foreground = WpfBrushes.White, FontFamily = new System.Windows.Media.FontFamily("Segoe UI") };
        var root = new WpfGrid { Margin = new Thickness(18) };
        for (int i = 0; i < 11; i++) root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition()); root.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = GridLength.Auto });
        WpfTextBlock title = Text(source.MoonName, 20, "#E8FFFF", true); WpfGrid.SetRow(title, 0); root.Children.Add(title);
        WpfTextBlock sub = Text(source.StructureName + " · " + source.SystemName, 12, "#82ABAE"); sub.Margin = new Thickness(0, 0, 0, 12); WpfGrid.SetRow(sub, 1); root.Children.Add(sub);
        WpfTextBox zeo = AddEditorRow(root, 2, "Zeolites composition %", source.ZeolitesPercent);
        WpfTextBox syl = AddEditorRow(root, 3, "Sylvite composition %", source.SylvitePercent);
        WpfTextBox bit = AddEditorRow(root, 4, "Bitumens composition %", source.BitumensPercent);
        WpfTextBox coe = AddEditorRow(root, 5, "Coesite composition %", source.CoesitePercent);
        WpfTextBox life = AddEditorRow(root, 6, "Asteroid field lifetime hours", source.FieldLifetimeHours);
        WpfTextBox waste = AddEditorRow(root, 7, "Estimated waste %", source.WastePercent);
        WpfTextBlock note = Text("Composition comes from your moon scan or CSV. ESI does not expose a moon's ore percentages. Jackpot status is observed from Glistening ore in the mining ledger.", 11, "#759B9E"); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 12, 0, 10); WpfGrid.SetRow(note, 8); root.Children.Add(note);
        var buttons = new WpfStackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancel = DialogButton("CANCEL", "#12383D"); var save = DialogButton("SAVE PROFILE", "#17656A"); save.Margin = new Thickness(8, 0, 0, 0); buttons.Children.Add(cancel); buttons.Children.Add(save); WpfGrid.SetRow(buttons, 12); root.Children.Add(buttons);
        MoonProfile? result = null; cancel.Click += (_, _) => window.DialogResult = false;
        save.Click += (_, _) =>
        {
            WpfTextBox[] boxes = { zeo, syl, bit, coe, life, waste };
            double[] values = new double[6];
            bool valid = true;
            for (int i = 0; i < boxes.Length; i++)
                valid &= TryNumber(boxes[i].Text, out values[i]);
            if (!valid)
            {
                System.Windows.MessageBox.Show(window, "Enter valid numbers in all six fields.", "Moon Report", MessageBoxButton.OK, MessageBoxImage.Warning); return;
            }
            result = new MoonProfile { MoonId = source.MoonId, StructureId = source.StructureId, MoonName = source.MoonName, StructureName = source.StructureName, SystemId = source.SystemId, SystemName = source.SystemName, ProfileConfigured = true, ZeolitesPercent = values[0], SylvitePercent = values[1], BitumensPercent = values[2], CoesitePercent = values[3], FieldLifetimeHours = values[4], WastePercent = values[5] };
            window.DialogResult = true;
        };
        window.Content = root; return window.ShowDialog() == true ? result : null;
    }

    private static WpfTextBox AddEditorRow(WpfGrid root, int row, string label, double value)
    {
        var panel = new WpfGrid { Margin = new Thickness(0, 4, 0, 4) }; panel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition()); panel.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(120) });
        panel.Children.Add(Text(label, 12, "#B2D5D6")); var box = new WpfTextBox { Text = Number(value) }; WpfGrid.SetColumn(box, 1); panel.Children.Add(box); WpfGrid.SetRow(panel, row); root.Children.Add(panel); return box;
    }

    private static WpfButton DialogButton(string text, string color) => new() { Content = text, Padding = new Thickness(14, 7, 14, 7), Background = Brush(color), Foreground = WpfBrushes.White, BorderBrush = Brush("#2C6269"), FontWeight = FontWeights.SemiBold };
    private static WpfTextBlock Text(string value, double size, string color, bool bold = false) => new() { Text = value, FontSize = size, Foreground = Brush(color), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
    private static WpfBrush Brush(string color) => (WpfBrush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
    private static DateTime StartOfWeek(DateTime date) => date.Date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    private static string SignedM3(double value) => (value >= 0 ? "+" : "-") + MoonReportService.FormatM3(Math.Abs(value));
    private static string CompactIsk(double value) => value >= 1_000_000_000 ? $"{value / 1_000_000_000:0.00}B ISK" : value >= 1_000_000 ? $"{value / 1_000_000:0.00}M ISK" : value >= 1_000 ? $"{value / 1_000:0.0}K ISK" : $"{value:0} ISK";
    private static bool Contains(string value, string search) => value?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);
    private static double ParseNumber(string value, double fallback) => TryNumber(value, out double parsed) ? parsed : fallback;
    private static bool TryNumber(string value, out double parsed) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) || double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed);
    private static string Csv(string value) => value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>(); var current = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++) { char ch = line[i]; if (ch == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; } else quoted = !quoted; } else if (ch == ',' && !quoted) { values.Add(current.ToString()); current.Clear(); } else current.Append(ch); }
        values.Add(current.ToString()); return values;
    }
    private void SetBusy(bool value) { _busy = value; RefreshButton.IsEnabled = ReconnectButton.IsEnabled = UsePilotButton.IsEnabled = PilotCombo.IsEnabled = !value; }
    private void SetStatus(string message) => SetStatus(message, false);
    private void SetStatus(string message, bool error) { StatusText.Text = message; StatusText.Foreground = Brush(error ? "#EF5350" : "#8FB2B5"); }
}
