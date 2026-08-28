using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EveMultiPreview.Models;
using EveMultiPreview.Services;
using Microsoft.Win32;

namespace EveMultiPreview.Views;

public partial class MoonReportWindow : Window
{
    private readonly EveSsoService _sso = new();
    private readonly MoonReportService _service;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<EvePilotProfile> _pilots =
        Array.Empty<EvePilotProfile>();
    private MoonReportSnapshot _snapshot = new();
    private string _filter = "ALL";
    private bool _busy;

    public MoonReportWindow()
    {
        InitializeComponent();
        _service = new MoonReportService(_sso);
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
            {
                SetStatus(
                    "No ESI characters are connected. Use RECONNECT / ADD.",
                    error: true);
            }
            else if (PilotCombo.SelectedItem is EvePilotProfile selected &&
                     !MoonReportService.HasRequiredScopes(selected))
            {
                SetStatus(
                    "This toon needs reconnecting once to approve the two moon " +
                    "report scopes.",
                    error: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _service.Dispose();
    }

    private async Task ReloadPilotsAsync(long preferCharacterId = 0)
    {
        _pilots = await _sso.LoadPilotsAsync();
        PilotCombo.ItemsSource = _pilots;

        long selectedId = preferCharacterId > 0
            ? preferCharacterId
            : _service.SelectedCharacterId;
        PilotCombo.SelectedItem = _pilots.FirstOrDefault(
            pilot => pilot.CharacterId == selectedId) ??
            _pilots.FirstOrDefault();
    }

    private async void UsePilot_Click(object sender, RoutedEventArgs e)
    {
        if (PilotCombo.SelectedItem is not EvePilotProfile pilot)
        {
            SetStatus("Choose a corporation-data toon first.", error: true);
            return;
        }

        await _service.SelectPilotAsync(pilot.CharacterId);
        if (!MoonReportService.HasRequiredScopes(pilot))
        {
            SetStatus(
                $"{pilot.CharacterName} needs RECONNECT / ADD once so EVE can " +
                "approve the moon scopes.",
                error: true);
            return;
        }

        await RefreshSelectedPilotAsync();
    }

    private async void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
            return;

        SetBusy(true);
        try
        {
            SetStatus(
                "Complete EVE authorization in the browser. Existing characters " +
                "are updated in place.");
            EvePilotProfile pilot = await _sso.AddCharacterAsync(
                _lifetime.Token);
            await ReloadPilotsAsync(pilot.CharacterId);
            await _service.SelectPilotAsync(pilot.CharacterId);
            SetStatus($"{pilot.CharacterName} connected. Refreshing moon data...");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
            return;
        }
        finally
        {
            SetBusy(false);
        }

        await RefreshSelectedPilotAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshSelectedPilotAsync();
    }

    private async Task RefreshSelectedPilotAsync()
    {
        if (_busy)
            return;
        if (PilotCombo.SelectedItem is not EvePilotProfile pilot)
        {
            SetStatus("Choose a corporation-data toon first.", error: true);
            return;
        }

        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => SetStatus(message));
            MoonReportSnapshot snapshot = await _service.RefreshAsync(
                pilot,
                progress,
                _lifetime.Token);
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplySnapshot(MoonReportSnapshot snapshot)
    {
        _snapshot = snapshot;
        ScheduledText.Text = snapshot.ScheduledCount.ToString("N0");
        ReadyText.Text = snapshot.ReadyCount.ToString("N0");
        ActiveText.Text = snapshot.ActiveFieldCount.ToString("N0");
        DespawnText.Text = snapshot.TargetDespawnCount.ToString("N0");
        ZeoLostText.Text = MoonReportService.FormatM3(snapshot.ZeolitesLostM3);
        BitumensLostText.Text = MoonReportService.FormatM3(
            snapshot.BitumensLostM3);
        AuditGrid.ItemsSource = snapshot.Audit;
        UpdatedText.Text = snapshot.GeneratedUtc == default
            ? "Not refreshed"
            : "Updated " + snapshot.GeneratedUtc.ToLocalTime()
                .ToString("dd MMM yyyy HH:mm:ss");
        ApplyFilters();
    }

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button)
            _filter = button.Tag?.ToString() ?? "ALL";
        ApplyFilters();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (MoonItems == null || SearchBox == null)
            return;

        string search = SearchBox.Text.Trim();
        IEnumerable<MoonCardView> cards = _snapshot.Cards;

        cards = _filter switch
        {
            "UPCOMING" => cards.Where(c =>
                c.Status == "SCHEDULED" || c.Status == "READY"),
            "ACTIVE" => cards.Where(c => c.Status == "FIELD ACTIVE"),
            "ALERT" => cards.Where(c => c.HasTargetLeftover),
            "ZEO" => cards.Where(c =>
                c.Profile.ZeolitesPercent > 0 ||
                c.ZeolitesRemainingM3 > 0),
            "BITUMENS" => cards.Where(c =>
                c.Profile.BitumensPercent > 0 ||
                c.BitumensRemainingM3 > 0),
            _ => cards
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            cards = cards.Where(card =>
                Contains(card.MoonName, search) ||
                Contains(card.StructureName, search) ||
                Contains(card.SystemName, search));
        }

        MoonItems.ItemsSource = cards.ToArray();
    }

    private async void Profile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not MoonCardView card)
            return;

        MoonProfile? profile = ShowProfileEditor(card.Profile);
        if (profile == null)
            return;

        try
        {
            await _service.SaveProfileAsync(profile);
            ApplySnapshot(_service.GetSnapshot());
            SetStatus($"Saved ore profile for {profile.MoonName}.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, error: true);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Moon Report setup",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            string[] lines = await File.ReadAllLinesAsync(
                dialog.FileName, _lifetime.Token);
            var profiles = new List<MoonProfile>();
            foreach (string line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                List<string> values = ParseCsvLine(line);
                if (values.Count < 8 ||
                    !long.TryParse(values[0], out long moonId))
                    continue;

                profiles.Add(new MoonProfile
                {
                    MoonId = moonId,
                    MoonName = values[1],
                    StructureName = values[2],
                    SystemName = values[3],
                    ProfileConfigured = true,
                    ZeolitesPercent = ParseNumber(values[4], 0),
                    BitumensPercent = ParseNumber(values[5], 0),
                    FieldLifetimeHours = ParseNumber(values[6], 48),
                    WastePercent = ParseNumber(values[7], 7)
                });
            }

            await _service.ImportProfilesAsync(profiles);
            ApplySnapshot(_service.GetSnapshot());
            SetStatus($"Imported {profiles.Count:N0} moon profiles.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not import moon setup: " + ex.Message, error: true);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Moon Report setup",
            FileName = "moon-report-setup.csv",
            DefaultExt = ".csv",
            Filter = "CSV files (*.csv)|*.csv"
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var lines = new List<string>
            {
                "MoonId,MoonName,StructureName,SystemName," +
                "ZeolitesPercent,BitumensPercent,FieldLifetimeHours,WastePercent"
            };
            lines.AddRange(_service.ExportProfiles().Select(profile =>
                string.Join(",",
                    Csv(profile.MoonId.ToString(CultureInfo.InvariantCulture)),
                    Csv(profile.MoonName),
                    Csv(profile.StructureName),
                    Csv(profile.SystemName),
                    Csv(profile.ZeolitesPercent.ToString(
                        "0.####", CultureInfo.InvariantCulture)),
                    Csv(profile.BitumensPercent.ToString(
                        "0.####", CultureInfo.InvariantCulture)),
                    Csv(profile.FieldLifetimeHours.ToString(
                        "0.##", CultureInfo.InvariantCulture)),
                    Csv(profile.WastePercent.ToString(
                        "0.##", CultureInfo.InvariantCulture)))));

            File.WriteAllLines(
                dialog.FileName,
                lines,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            SetStatus(
                $"Exported {_service.ExportProfiles().Count:N0} moon profiles.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not export moon setup: " + ex.Message, error: true);
        }
    }

    private MoonProfile? ShowProfileEditor(MoonProfile source)
    {
        var window = new Window
        {
            Owner = this,
            Title = "Ore profile · " + source.MoonName,
            Width = 455,
            Height = 430,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(7, 24, 27)),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI")
        };

        var root = new Grid { Margin = new Thickness(18) };
        for (int i = 0; i < 7; i++)
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = source.MoonName,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(232, 255, 255)),
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetRow(title, 0);
        root.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = source.StructureName + " · " + source.SystemName,
            Foreground = new SolidColorBrush(Color.FromRgb(130, 171, 174)),
            Margin = new Thickness(0, 0, 0, 14)
        };
        Grid.SetRow(subtitle, 1);
        root.Children.Add(subtitle);

        TextBox zeo = AddEditorRow(
            root, 2, "Zeolites composition %", source.ZeolitesPercent);
        TextBox bitumens = AddEditorRow(
            root, 3, "Bitumens composition %", source.BitumensPercent);
        TextBox lifetime = AddEditorRow(
            root, 4, "Asteroid field lifetime hours", source.FieldLifetimeHours);
        TextBox waste = AddEditorRow(
            root, 5, "Estimated waste %", source.WastePercent);

        var note = new TextBlock
        {
            Text = "Composition comes from your moon scan. ESI does not expose " +
                   "moon ore percentages or exact asteroid leftovers. Default " +
                   "field lifetime is 48 hours; change it for rigged drills.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(117, 155, 158)),
            Margin = new Thickness(0, 12, 0, 10)
        };
        Grid.SetRow(note, 6);
        root.Children.Add(note);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = DialogButton("CANCEL", "#12383D");
        cancel.Click += (_, _) => window.DialogResult = false;
        var save = DialogButton("SAVE PROFILE", "#17656A");
        save.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        Grid.SetRow(buttons, 8);
        root.Children.Add(buttons);

        MoonProfile? result = null;
        save.Click += (_, _) =>
        {
            if (!TryNumber(zeo.Text, out double zeoValue) ||
                !TryNumber(bitumens.Text, out double bitumensValue) ||
                !TryNumber(lifetime.Text, out double lifetimeValue) ||
                !TryNumber(waste.Text, out double wasteValue))
            {
                MessageBox.Show(
                    window,
                    "Enter valid numbers in all four fields.",
                    "Moon Report",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            result = new MoonProfile
            {
                MoonId = source.MoonId,
                StructureId = source.StructureId,
                MoonName = source.MoonName,
                StructureName = source.StructureName,
                SystemId = source.SystemId,
                SystemName = source.SystemName,
                ProfileConfigured = true,
                ZeolitesPercent = zeoValue,
                BitumensPercent = bitumensValue,
                FieldLifetimeHours = lifetimeValue,
                WastePercent = wasteValue
            };
            window.DialogResult = true;
        };

        window.Content = root;
        return window.ShowDialog() == true ? result : null;
    }

    private static TextBox AddEditorRow(
        Grid root,
        int row,
        string label,
        double value)
    {
        var panel = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        var caption = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(178, 213, 214))
        };
        panel.Children.Add(caption);

        var box = new TextBox
        {
            Text = value.ToString("0.####", CultureInfo.InvariantCulture),
            Background = new SolidColorBrush(Color.FromRgb(10, 31, 35)),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(44, 98, 105)),
            Padding = new Thickness(7, 5, 7, 5)
        };
        Grid.SetColumn(box, 1);
        panel.Children.Add(box);
        Grid.SetRow(panel, row);
        root.Children.Add(panel);
        return box;
    }

    private static System.Windows.Controls.Button DialogButton(
        string text,
        string color)
    {
        return new System.Windows.Controls.Button
        {
            Content = text,
            Padding = new Thickness(14, 7, 14, 7),
            Background = (Brush)new BrushConverter().ConvertFromString(color)!,
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(44, 98, 105)),
            FontWeight = FontWeights.SemiBold
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        RefreshButton.IsEnabled = !busy;
        ReconnectButton.IsEnabled = !busy;
        UsePilotButton.IsEnabled = !busy;
        PilotCombo.IsEnabled = !busy;
    }

    private void SetStatus(string message, bool error = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = error
            ? new SolidColorBrush(Color.FromRgb(239, 83, 80))
            : new SolidColorBrush(Color.FromRgb(143, 178, 181));
    }

    private static bool Contains(string value, string search)
    {
        return value?.IndexOf(
            search, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static double ParseNumber(string value, double fallback)
    {
        return TryNumber(value, out double parsed) ? parsed : fallback;
    }

    private static bool TryNumber(string value, out double parsed)
    {
        return double.TryParse(
                   value,
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out parsed) ||
               double.TryParse(
                   value,
                   NumberStyles.Float,
                   CultureInfo.CurrentCulture,
                   out parsed);
    }

    private static string Csv(string value)
    {
        if (value.Contains(',') || value.Contains('"') ||
            value.Contains('\r') || value.Contains('\n'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString());
        return values;
    }
}
