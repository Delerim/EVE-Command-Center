using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

internal static partial class Program
{
    private static void CheckCloudBackupLayout()
    {
        var backupPath = Path.Combine(Directory.GetCurrentDirectory(), "Views", "CloudBackupWindow.xaml");
        var document = XDocument.Load(backupPath);
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var root = document.Root!;
        var shell = root.Elements(wpf + "Grid").Single();
        var scroller = shell.Elements(wpf + "ScrollViewer").Single();
        var footer = shell.Elements(wpf + "Border").Single();
        var actions = footer.Descendants(wpf + "WrapPanel").Single();
        string? Name(XElement element) => (string?)element.Attribute(x + "Name");
        Check((string?)scroller.Attribute("Grid.Row") == "0" &&
              Name(scroller) == "BackupSetupScroller" &&
              scroller.Descendants(wpf + "PasswordBox").Count() == 2,
              "Backup setup scrolls when the window is vertically constrained");
        Check((string?)footer.Attribute("Grid.Row") == "1" &&
              footer.Descendants(wpf + "ComboBox").Any(e => Name(e) == "RestorePointsBox"),
              "Restore point selection stays outside the setup scroller");
        var buttons = actions.Elements(wpf + "Button").Select(Name).ToArray();
        Check(buttons.Contains("BackupButton") && buttons.Contains("RestoreButton") &&
              buttons.Contains("ExportEncryptedButton") && buttons.Contains("RestoreLocalButton"),
              "Backup, restore and export controls remain in the fixed wrapping footer");
        Check(shell.Elements(wpf + "DockPanel").Single().Attribute("Grid.Row")?.Value == "2",
              "Backup progress and status stay visible beneath the action footer");
        string nav = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Views", "SettingsWindow.xaml"));
        Check(nav.Contains("x:Name=\"NavBackup\" Content=\" ☁ Backup\"", StringComparison.Ordinal),
              "Backup sidebar icon uses compact alignment with neighbouring settings");
    }
}
