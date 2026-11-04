using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using EveCommandCenter.Services;
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
    private readonly System.Collections.Generic.List<Button> _allAlarmButtons = new();
    private void UpdateCompactDeck(double value)
    {
        foreach (var button in _allAlarmButtons.Concat(new[] { CompactAllAlarmsButton })) {
            var characters = AlarmControlCharacters();
            bool muted = characters.Length > 0 && characters.All(_watchdog.IsCharacterAlarmMuted);
            button.Content = button.Tag?.ToString()=="icon" ? (muted ? "\uE7ED" : "\uEA8F") : (muted ? "ALL ALARMS ON" : "ALL ALARMS OFF");
            button.ToolTip = muted ? "Turn every client alarm switch on." : "Turn every client alarm switch off. Orca drone indicators stay unchanged.";
            button.Foreground = muted ? Brushes.Gold : Brushes.WhiteSmoke;
        }
    }
    private string[] AlarmControlCharacters() => _clientSource()
        .Select(c => c.CharacterName).Where(c => !string.IsNullOrWhiteSpace(c) &&
            (!_pilotIntel.TryGetValue(c,out var intel) || !intel.IsOrca)).Distinct(System.StringComparer.OrdinalIgnoreCase).ToArray();
    private void ToggleAllAlarms(object sender, RoutedEventArgs e)
    {
        var characters = AlarmControlCharacters();
        if (characters.Length == 0) return;
        bool turnOff = !characters.All(_watchdog.IsCharacterAlarmMuted);
        _watchdog.SetCharacterAlarmsMuted(characters, turnOff);
        RefreshCards();
    }

}
