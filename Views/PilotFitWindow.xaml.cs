using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using EveMultiPreview.Models;

namespace EveMultiPreview.Views;

public partial class PilotFitWindow : Window
{
    public PilotFitWindow(
        string fitName,
        string shipName,
        IReadOnlyList<EveShipModuleView> modules,
        string description)
    {
        InitializeComponent();

        EveShipModuleView[] rows =
            modules
                .ToArray();

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

        ModulesGrid.ItemsSource =
            rows;
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
