using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using EveMultiPreview.Models;

namespace EveMultiPreview.Views;

public partial class PilotFitWindow : Window
{
    public PilotFitWindow(
        string fitName,
        string shipName,
        int shipTypeId,
        IReadOnlyList<EveShipModuleView> modules,
        string description,
        EveFitDefenseStats defense)
    {
        InitializeComponent();

        EveShipModuleView[] rows =
            modules.ToArray();

        FitNameText.Text =
            string.IsNullOrWhiteSpace(fitName)
                ? "FIT"
                : fitName;

        ShipNameText.Text =
            string.IsNullOrWhiteSpace(shipName)
                ? "-"
                : shipName;

        DescriptionText.Text =
            string.IsNullOrWhiteSpace(description)
                ? ""
                : description;

        ModuleCountText.Text =
            rows.Length.ToString("N0");

        if (shipTypeId > 0)
        {
            try
            {
                ShipRenderImage.Source =
                    new BitmapImage(
                        new Uri(
                            $"https://images.evetech.net/types/{shipTypeId}/render?size=512"));
            }
            catch
            {
            }
        }

        if (defense.Available)
        {
            EhpValueText.Text =
                defense.EhpText
                    .Replace(
                        "EHP ",
                        "");

            BufferText.Text =
                $"Shield {FormatCompact(defense.ShieldHp)} | " +
                $"Armor {FormatCompact(defense.ArmorHp)} | " +
                $"Hull {FormatCompact(defense.StructureHp)}";

            EhpNoteText.Text =
                "Fit/skill omni EHP estimate. Active fitted hardeners assumed; fleet boosts, heat, boosters and most implant effects are not included yet.";
        }
        else
        {
            EhpValueText.Text = "--";
            BufferText.Text =
                "Shield -- | Armor -- | Hull --";
            EhpNoteText.Text =
                "Defense stats are calculated for the character's current live fit. Saved-fit calculation will be added after the full Dogma effect pass.";
        }

        BindSection(
            HighSection,
            HighItems,
            rows.Where(
                row =>
                    row.Slot.StartsWith(
                        "High",
                        StringComparison.OrdinalIgnoreCase)));

        BindSection(
            MidSection,
            MidItems,
            rows.Where(
                row =>
                    row.Slot.StartsWith(
                        "Mid",
                        StringComparison.OrdinalIgnoreCase)));

        BindSection(
            LowSection,
            LowItems,
            rows.Where(
                row =>
                    row.Slot.StartsWith(
                        "Low",
                        StringComparison.OrdinalIgnoreCase)));

        BindSection(
            RigSection,
            RigItems,
            rows.Where(
                row =>
                    row.Slot.StartsWith(
                        "Rig",
                        StringComparison.OrdinalIgnoreCase)));

        BindSection(
            DroneSection,
            DroneItems,
            rows.Where(
                row =>
                    row.Slot.Contains(
                        "Drone",
                        StringComparison.OrdinalIgnoreCase) ||
                    row.Slot.Contains(
                        "Fighter",
                        StringComparison.OrdinalIgnoreCase)));

        BindSection(
            OtherSection,
            OtherItems,
            rows.Where(
                row =>
                    !row.Slot.StartsWith(
                        "High",
                        StringComparison.OrdinalIgnoreCase) &&
                    !row.Slot.StartsWith(
                        "Mid",
                        StringComparison.OrdinalIgnoreCase) &&
                    !row.Slot.StartsWith(
                        "Low",
                        StringComparison.OrdinalIgnoreCase) &&
                    !row.Slot.StartsWith(
                        "Rig",
                        StringComparison.OrdinalIgnoreCase) &&
                    !row.Slot.Contains(
                        "Drone",
                        StringComparison.OrdinalIgnoreCase) &&
                    !row.Slot.Contains(
                        "Fighter",
                        StringComparison.OrdinalIgnoreCase)));
    }

    private static void BindSection(
        FrameworkElement section,
        System.Windows.Controls.ItemsControl items,
        IEnumerable<EveShipModuleView> source)
    {
        EveShipModuleView[] rows =
            source.ToArray();

        section.Visibility =
            rows.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        items.ItemsSource =
            rows;
    }

    private static string FormatCompact(
        double value)
    {
        if (value >= 1000000)
            return $"{value / 1000000.0:0.00}m";

        if (value >= 1000)
            return $"{value / 1000.0:0.#}k";

        return $"{value:0}";
    }

    private void TitleBar_MouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (e.LeftButton !=
            MouseButtonState.Pressed)
            return;

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private void Close_Click(
        object sender,
        RoutedEventArgs e) =>
        Close();
}
