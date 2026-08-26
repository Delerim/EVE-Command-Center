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

            ShieldHpText.Text =
                FormatCompact(
                    defense.ShieldHp) +
                " HP";

            ArmorHpText.Text =
                FormatCompact(
                    defense.ArmorHp) +
                " HP";

            HullHpText.Text =
                FormatCompact(
                    defense.StructureHp) +
                " HP";

            EhpNoteText.Text =
                "Uniform-damage EHP estimate from the live hull, fit and character skills. Active fitted hardeners are assumed on.";
        }
        else
        {
            EhpValueText.Text = "--";
            ShieldHpText.Text = "--";
            ArmorHpText.Text = "--";
            HullHpText.Text = "--";

            EhpNoteText.Text =
                "Defense stats are available for the character's current live fit. Saved-fit full Dogma simulation is still approximate.";
        }

        EveShipModuleView[] high =
            rows.Where(
                    row =>
                        row.Slot.StartsWith(
                            "High",
                            StringComparison.OrdinalIgnoreCase))
                .ToArray();

        EveShipModuleView[] mid =
            rows.Where(
                    row =>
                        row.Slot.StartsWith(
                            "Mid",
                            StringComparison.OrdinalIgnoreCase))
                .ToArray();

        EveShipModuleView[] low =
            rows.Where(
                    row =>
                        row.Slot.StartsWith(
                            "Low",
                            StringComparison.OrdinalIgnoreCase))
                .ToArray();

        EveShipModuleView[] rigs =
            rows.Where(
                    row =>
                        row.Slot.StartsWith(
                            "Rig",
                            StringComparison.OrdinalIgnoreCase))
                .ToArray();

        EveShipModuleView[] drones =
            rows.Where(
                    row =>
                        row.Slot.Contains(
                            "Drone",
                            StringComparison.OrdinalIgnoreCase) ||
                        row.Slot.Contains(
                            "Fighter",
                            StringComparison.OrdinalIgnoreCase))
                .ToArray();

        EveShipModuleView[] other =
            rows.Except(
                    high
                        .Concat(mid)
                        .Concat(low)
                        .Concat(rigs)
                        .Concat(drones))
                .ToArray();

        BindRing(
            HighRingItems,
            high);

        BindRing(
            MidRingItems,
            mid);

        BindRing(
            LowRingItems,
            low);

        BindRing(
            RigRingItems,
            rigs);

        BindRing(
            DroneRingItems,
            drones);

        DroneTray.Visibility =
            drones.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        BindDetail(
            HighDetailSection,
            HighDetailItems,
            high);

        BindDetail(
            MidDetailSection,
            MidDetailItems,
            mid);

        BindDetail(
            LowDetailSection,
            LowDetailItems,
            low);

        BindDetail(
            RigDetailSection,
            RigDetailItems,
            rigs);

        BindDetail(
            DroneDetailSection,
            DroneDetailItems,
            drones);

        BindDetail(
            OtherDetailSection,
            OtherDetailItems,
            other);
    }

    private static void BindRing(
        System.Windows.Controls.ItemsControl target,
        IReadOnlyList<EveShipModuleView> rows)
    {
        target.ItemsSource =
            rows;
    }

    private static void BindDetail(
        FrameworkElement section,
        System.Windows.Controls.ItemsControl target,
        IReadOnlyList<EveShipModuleView> rows)
    {
        section.Visibility =
            rows.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;

        target.ItemsSource =
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
