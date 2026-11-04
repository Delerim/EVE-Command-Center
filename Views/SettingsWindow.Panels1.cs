using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EveCommandCenter.Models;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using ColorConverter = System.Windows.Media.ColorConverter;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using WinForms = System.Windows.Forms;

namespace EveCommandCenter.Views;

public partial class SettingsWindow
{
    // ═══ COLOR PICKER HELPER ═══
    private string? PickColor(string? initialHex = null)
    {
        var dlg = new WinForms.ColorDialog { FullOpen = true };

        // Load the user's persisted custom-color palette so any colors they've
        // saved show up in the Windows color picker's "Custom colors" panel —
        // both across sessions and across every place we open this dialog
        // (issue #32: custom colors disappeared on restart and weren't shared
        // between different alert pickers). The 16-slot int[] format matches
        // ColorDialog.CustomColors exactly.
        if (S.CustomColorPalette != null && S.CustomColorPalette.Length == 16)
        {
            try { dlg.CustomColors = (int[])S.CustomColorPalette.Clone(); } catch { }
        }

        if (!string.IsNullOrEmpty(initialHex))
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(initialHex.StartsWith("#") ? initialHex : "#" + initialHex.Replace("0x", ""));
                dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
            catch { }
        }
        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
        {
            // Persist whatever palette state the user left the dialog in,
            // including any new "Add to Custom Colors" entries.
            try { S.CustomColorPalette = (int[])dlg.CustomColors.Clone(); SaveDelayed(); } catch { }
            return $"0x{dlg.Color.R:x2}{dlg.Color.G:x2}{dlg.Color.B:x2}";
        }
        return null;
    }

    private void OnPickColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string targetName) return;
        var tb = FindName(targetName) as System.Windows.Controls.TextBox;
        if (tb == null) return;
        var hex = PickColor(tb.Text);
        if (hex != null) tb.Text = hex;
    }

    private static void UpdateColorPreview(System.Windows.Controls.TextBox tb, Border preview)
    {
        try
        {
            var hex = tb.Text.Replace("0x", "#");
            if (!hex.StartsWith("#")) hex = "#" + hex;
            preview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch { preview.Background = Brushes.Gray; }
    }

    // ═══ CAPTURE HOTKEY ═══
    private void OnCaptureHotkey(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string targetName) return;
        var tb = FindName(targetName) as System.Windows.Controls.TextBox;
        if (tb == null) return;
        var old = tb.Text;
        tb.Text = "Press a key or mouse button...";
        tb.IsReadOnly = true;
        tb.Background = Brushes.DarkOrange;
        tb.PreviewKeyDown += CaptureKeyHandler;
        tb.PreviewMouseDown += CaptureMouseHandler;
        tb.LostMouseCapture += OnLostCapture;
        // Suppress Tab navigation so Tab key reaches PreviewKeyDown
        KeyboardNavigation.SetTabNavigation(tb, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(tb, KeyboardNavigationMode.None);
        tb.Focus(); // CRITICAL: TextBox must have focus to receive PreviewKeyDown
        Mouse.Capture(tb); // Route ALL mouse events to tb regardless of cursor position
        // Safety net for #93: if anything ends capture-mode without going through
        // CleanupCapture (window deactivated, alt-tab, panel switch), OnDeactivated
        // calls this so the app can never be left holding the mouse capture — which
        // silently ate every click system-wide until Ctrl+Alt+Del broke it.
        _activeHotkeyCaptureCleanup = () => { CleanupCapture(); if (tb.Text == "Press a key or mouse button...") tb.Text = old; };

        void CleanupCapture()
        {
            _activeHotkeyCaptureCleanup = null;
            tb.PreviewKeyDown -= CaptureKeyHandler;
            tb.PreviewMouseDown -= CaptureMouseHandler;
            tb.LostMouseCapture -= OnLostCapture;
            Mouse.Capture(null);
            tb.IsReadOnly = false;
            tb.Background = (Brush)FindResource("BgPanelBrush");
            // Restore normal Tab navigation
            KeyboardNavigation.SetTabNavigation(tb, KeyboardNavigationMode.Continue);
            KeyboardNavigation.SetDirectionalNavigation(tb, KeyboardNavigationMode.Continue);
        }

        void OnLostCapture(object s2, System.Windows.Input.MouseEventArgs me2)
        {
            // Safety: if capture is lost unexpectedly (e.g. alt-tab), restore state
            CleanupCapture();
            if (tb.Text == "Press a key or mouse button...") tb.Text = old;
        }

        void CaptureKeyHandler(object s, KeyEventArgs ke)
        {
            ke.Handled = true;
            var key = ke.Key == Key.System ? ke.SystemKey : ke.Key;

            // Ignore modifier-only presses
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin)
            {
                return; // wait for a real key
            }

            CleanupCapture();

            if (key == Key.Escape) { tb.Text = old; return; }

            // Build AHK-format string (matches OnHotkeyCaptured)
            string ahkStr = "";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";
            ahkStr += KeyToAhkName(key);

            // Check for conflicts with system hotkeys only
            if (CheckHotkeyConflicts(ahkStr, targetName, systemOnly: true))
                tb.Text = old;
            else
                tb.Text = ahkStr;
        }

        void CaptureMouseHandler(object s, MouseButtonEventArgs me)
        {
            string? buttonName = me.ChangedButton switch
            {
                MouseButton.XButton1 => "XButton1",
                MouseButton.XButton2 => "XButton2",
                MouseButton.Middle => "MButton",
                _ => null
            };

            if (buttonName == null)
            {
                // Left/right click CANCELS capture (#93). These aren't bindable, and
                // previously they were swallowed by the active Mouse.Capture with no
                // exit — so one stray click left the app eating every click on the
                // desktop until Ctrl+Alt+Del. Now any non-bindable click ends capture.
                me.Handled = true;
                CleanupCapture();
                tb.Text = old;
                return;
            }

            me.Handled = true;
            CleanupCapture();

            string ahkStr = "";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";
            ahkStr += buttonName;

            if (CheckHotkeyConflicts(ahkStr, targetName, systemOnly: true))
                tb.Text = old;
            else
                tb.Text = ahkStr;
        }
    }

    // ═══ KNOWN CHARACTERS ═══
    private List<string> GetKnownCharacters()
    {
        var chars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in S.ThumbnailVisibility) chars.Add(kv.Key);
        foreach (var profile in S.Profiles.Values)
        {
            foreach (var k in profile.ThumbnailPositions.Keys) chars.Add(k);
            foreach (var k in profile.Hotkeys.Keys) chars.Add(k);
        }
        foreach (var kv in S.CustomColors) chars.Add(kv.Key);
        foreach (var kv in S.SecondaryThumbnails) chars.Add(kv.Key);
        return chars.OrderBy(c => c).ToList();
    }

    private string? ShowCharacterSearch(string title)
    {
        var known = GetKnownCharacters();
        // Also include active EVE window character names
        if (_overviewClients != null)
        {
            foreach (var name in _overviewClients.GetActiveCharacterNames())
                if (!known.Contains(name, StringComparer.OrdinalIgnoreCase))
                    known.Add(name);
            known.Sort(StringComparer.OrdinalIgnoreCase);
        }
        var dlg = new CharacterPickerDialog(title, known) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.SelectedName : null;
    }

    // ═══ GENERAL ═══
    private void OnGeneralChanged(object s, RoutedEventArgs e) { if (_loading) return; SaveGeneral(); }
    private void OnGeneralChanged(object s, SelectionChangedEventArgs e) { if (_loading) return; SaveGeneral(); }
    private void OnGeneralChanged(object s, TextChangedEventArgs e) { if (_loading) return; SaveGeneral(); }

    private void SaveGeneral()
    {
        S.GlobalHotkeys = CmbHotkeyScope.SelectedIndex == 0;
        S.SuspendHotkey = TxtSuspendHotkey.Text;
        S.HideShowThumbnailsHotkey = TxtHideShowHotkey.Text;
        S.HideShowCropsHotkey = TxtHideCropsHotkey.Text;
        S.ProfileCycleForwardHotkey = TxtProfileCycleForward.Text;
        S.ProfileCycleBackwardHotkey = TxtProfileCycleBackward.Text;
        S.QuickSwitchHotkey = TxtQuickSwitchHotkey.Text;
        S.ShowBroadcastKeyHud = ChkBroadcastHud.IsChecked == true;
        S.AutoSoloClientAudio = ChkAutoSoloAudio.IsChecked == true;
        if (int.TryParse(TxtMinimizeDelay.Text, out int md)) S.MinimizeDelay = md;
        // Clamp cycle delay to a sensible range — too low becomes unresponsive
        // (events outpace processing), too high feels broken.
        if (int.TryParse(TxtCycleDelay.Text, out int cd)) S.CycleDelayMs = Math.Clamp(cd, 25, 2000);
        S.CycleWhileHeld = ChkCycleWhileHeld.IsChecked == true;
        S.AutoLaunchCharacterOverview = ChkAutoLaunchCharacterOverview.IsChecked == true;
        S.MinimizeCommandCenterOnOverviewLaunch = ChkMinimizeCommandCenterOnOverviewLaunch.IsChecked == true;
        // Startup-settings mode: clamp to the 3 known values.
        int startupIdx = CmbStartupSettings.SelectedIndex;
        S.StartupSettings = startupIdx switch
        {
            1 => StartupSettingsMode.Open,
            2 => StartupSettingsMode.OpenMinimized,
            _ => StartupSettingsMode.Off,
        };
        SaveDelayed();
    }

    // ═══ THUMBNAILS ═══
    private void LoadHotkeysList()
    {
        LvHotkeys.Items.Clear();
        var profile = _svc.CurrentProfile;
        foreach (var kv in profile.Hotkeys)
            LvHotkeys.Items.Add(new { Character = kv.Key, Hotkey = kv.Value.Key });
    }

    private void OnHotkeySelected(object s, SelectionChangedEventArgs e) { }

    // ── "+ Add All Active Clients" — shared plumbing ─────────────────
    // Used by Annotations, Individual Character Hotkeys, Per-Character Colors,
    // Groups and Don't Minimize Clients. Each caller supplies only "is it already
    // there?" and "add it", so the dedupe/empty/feedback behaviour stays identical
    // everywhere. Blank names (login screens with no character yet) are skipped.

    private List<string> ActiveCharacterNames() =>
        _overviewClients?.GetActiveCharacterNames()
            .Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new List<string>();

    /// <summary>Add every active client the target doesn't already contain.
    /// Returns how many were added (0 = nothing to do, message already shown).
    /// <paramref name="isGroup"/> only picks the wording of the "nothing to add"
    /// message ("in this group" vs "in this list").</summary>
    private int AddAllActiveClients(string title, Func<string, bool> alreadyHas, Action<string> add, bool isGroup = false)
    {
        var active = ActiveCharacterNames();
        if (active.Count == 0)
        {
            MessageBox.Show(
                EveCommandCenter.Services.LocalizationService.Str("L.Groups.NoActiveClients", "No active clients found."),
                title);
            return 0;
        }

        int added = 0;
        foreach (var name in active)
        {
            if (alreadyHas(name)) continue;
            add(name);
            added++;
        }

        if (added == 0)
            MessageBox.Show(
                isGroup
                    ? EveCommandCenter.Services.LocalizationService.Str("L.Groups.AllAlreadyAdded", "All active clients are already in this group.")
                    : EveCommandCenter.Services.LocalizationService.Str("L.Common.AllAlreadyInList", "All active clients are already in this list."),
                title);
        return added;
    }

    private void OnHotkeyAddAllActive(object s, RoutedEventArgs e)
    {
        // Empty binding per character — the user then assigns keys via Edit.
        var profile = _svc.CurrentProfile;
        int added = AddAllActiveClients(
            EveCommandCenter.Services.LocalizationService.Str("L.Hk.IndividualHeader", "Individual Character Hotkeys"),
            n => profile.Hotkeys.ContainsKey(n),
            n => profile.Hotkeys[n] = new HotkeyBinding { Key = "" });
        if (added > 0) { LoadHotkeysList(); SaveDelayed(); }
    }

    private void OnHotkeyAdd(object s, RoutedEventArgs e)
    {
        var name = ShowCharacterSearch("Add Hotkey");
        if (name == null) return;
        var profile = _svc.CurrentProfile;
        if (!profile.Hotkeys.ContainsKey(name))
            profile.Hotkeys[name] = new HotkeyBinding { Key = "" };
        LoadHotkeysList();
        SaveDelayed();
    }

    private void OnHotkeyEdit(object s, RoutedEventArgs e)
    {
        if (LvHotkeys.SelectedItem == null) { MessageBox.Show("Select a character first."); return; }
        var charName = (string)LvHotkeys.SelectedItem.GetType().GetProperty("Character")!.GetValue(LvHotkeys.SelectedItem)!;
        var current = _svc.CurrentProfile.Hotkeys[charName].Key;

        // Build a proper WPF dialog with text entry + capture button
        var dlg = new Window
        {
            Title = $"Edit Hotkey — {charName}",
            Width = 360, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)FindResource("BgDarkBrush")
        };

        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock
        {
            Text = $"Hotkey for: {charName}",
            Foreground = (Brush)FindResource("AccentBrush"),
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8)
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var tb = new System.Windows.Controls.TextBox
        {
            Width = 200, Text = current,
            Background = (Brush)FindResource("BgPanelBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            Padding = new Thickness(4, 2, 4, 2)
        };
        row.Children.Add(tb);

        var captureBtn = new Button
        {
            Content = "⌨ Capture", Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(6, 0, 0, 0)
        };
        captureBtn.Click += (_, _) =>
        {
            tb.Text = "Press a key or mouse button...";
            tb.Background = System.Windows.Media.Brushes.DarkOrange;
            tb.IsReadOnly = true;
            tb.Focus();
            Mouse.Capture(tb); // Route ALL mouse events to tb regardless of cursor position
            tb.PreviewKeyDown += CaptureKey;
            tb.PreviewMouseDown += CaptureMouseBtn;
            tb.LostMouseCapture += OnLostCap;

            void CleanupCap()
            {
                tb.PreviewKeyDown -= CaptureKey;
                tb.PreviewMouseDown -= CaptureMouseBtn;
                tb.LostMouseCapture -= OnLostCap;
                Mouse.Capture(null);
                tb.IsReadOnly = false;
                tb.Background = (Brush)FindResource("BgPanelBrush");
            }

            void OnLostCap(object cs, System.Windows.Input.MouseEventArgs cme)
            {
                CleanupCap();
                if (tb.Text == "Press a key or mouse button...") tb.Text = current;
            }

            void CaptureKey(object cs, KeyEventArgs cke)
            {
                cke.Handled = true;
                var key = cke.Key == Key.System ? cke.SystemKey : cke.Key;
                if (key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
                    key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin)
                    return; // wait for real key

                CleanupCap();
                string ahkStr = "";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";
                ahkStr += KeyToAhkName(key);
                tb.Text = ahkStr;
            }

            void CaptureMouseBtn(object cs, MouseButtonEventArgs cme)
            {
                string? buttonName = cme.ChangedButton switch
                {
                    MouseButton.XButton1 => "XButton1",
                    MouseButton.XButton2 => "XButton2",
                    MouseButton.Middle => "MButton",
                    _ => null
                };
                if (buttonName == null)
                {
                    // Left/right click cancels capture — see #93. Without this the
                    // click is swallowed by Mouse.Capture and capture never ends.
                    cme.Handled = true;
                    CleanupCap();
                    tb.Text = current;
                    return;
                }

                cme.Handled = true;
                CleanupCap();
                string ahkStr = "";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";
                ahkStr += buttonName;
                tb.Text = ahkStr;
            }
        };
        row.Children.Add(captureBtn);
        sp.Children.Add(row);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var okBtn = new Button { Content = "OK", Padding = new Thickness(16, 4, 16, 4), IsDefault = true };
        var cancelBtn = new Button { Content = "Cancel", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(6, 0, 0, 0), IsCancel = true };
        okBtn.Click += (_, _) => { dlg.DialogResult = true; };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        sp.Children.Add(btnRow);

        dlg.Content = sp;
        if (dlg.ShowDialog() == true)
        {
            string newKey = tb.Text.Trim();
            if (!string.IsNullOrEmpty(newKey) && newKey != "Press a key...")
            {
                // Only check against system hotkeys (allow same key for other characters)
                if (!CheckHotkeyConflicts(newKey, null, systemOnly: true))
                {
                    _svc.CurrentProfile.Hotkeys[charName].Key = newKey;
                    LoadHotkeysList();
                    SaveDelayed();
                }
            }
        }
    }

    private void OnHotkeyDelete(object s, RoutedEventArgs e)
    {
        if (LvHotkeys.SelectedItem == null) return;
        var charName = (string)LvHotkeys.SelectedItem.GetType().GetProperty("Character")!.GetValue(LvHotkeys.SelectedItem)!;
        _svc.CurrentProfile.Hotkeys.Remove(charName);
        LoadHotkeysList();
        SaveDelayed();
    }

    // Hotkey Groups
    private void LoadHotkeyGroups()
    {
        CmbHotkeyGroup.Items.Clear();
        foreach (var kv in S.HotkeyGroups) CmbHotkeyGroup.Items.Add(kv.Key);
    }

    private void OnHotkeyGroupChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || CmbHotkeyGroup.SelectedItem is not string name) return;
        if (S.HotkeyGroups.TryGetValue(name, out var grp))
        {
            // Guard: setting these text fields fires OnHotkeyGroupKeysChanged,
            // which would save partial state (e.g., BackwardsHotkey still empty
            // while ForwardsHotkey is being set). Block events during population.
            _loadingDepth++;
            try
            {
                TxtHotkeyGroupChars.Text = string.Join("\n", grp.Characters);
                TxtGroupFwd.Text = grp.ForwardsHotkey;
                TxtGroupBwd.Text = grp.BackwardsHotkey;
            }
            finally { _loadingDepth--; }
            TxtHotkeyGroupChars.IsEnabled = true;
            TxtGroupFwd.IsEnabled = true;
            TxtGroupBwd.IsEnabled = true;
            RebuildHotkeyGroupPills();
        }
    }

    private void OnHotkeyGroupNew(object s, RoutedEventArgs e)
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("Group name:", "New Hotkey Group");
        if (string.IsNullOrWhiteSpace(name)) return;
        S.HotkeyGroups[name] = new HotkeyGroup();
        LoadHotkeyGroups();
        CmbHotkeyGroup.SelectedItem = name;
        SaveDelayed();
    }

    private void OnHotkeyGroupDelete(object s, RoutedEventArgs e)
    {
        if (CmbHotkeyGroup.SelectedItem is not string name) return;
        S.HotkeyGroups.Remove(name);
        LoadHotkeyGroups();
        TxtHotkeyGroupChars.Text = "";
        TxtGroupFwd.Text = "";
        TxtGroupBwd.Text = "";
        HotkeyGroupPills.Children.Clear();
        SaveDelayed();
    }

    private void OnHotkeyGroupCharsChanged(object s, TextChangedEventArgs e)
    {
        if (_loading || CmbHotkeyGroup.SelectedItem is not string name) return;
        if (S.HotkeyGroups.TryGetValue(name, out var grp))
        {
            grp.Characters = TxtHotkeyGroupChars.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            RebuildHotkeyGroupPills();   // keep pills in step with manual text edits
            SaveDelayed();
        }
    }

    private void OnHotkeyGroupKeysChanged(object s, TextChangedEventArgs e)
    {
        if (_loading || CmbHotkeyGroup.SelectedItem is not string name) return;
        if (S.HotkeyGroups.TryGetValue(name, out var grp))
        {
            grp.ForwardsHotkey = TxtGroupFwd.Text;
            grp.BackwardsHotkey = TxtGroupBwd.Text;
            SaveDelayed();
        }
    }

    private void OnHotkeyGroupAddChar(object s, RoutedEventArgs e)
    {
        if (CmbHotkeyGroup.SelectedItem is not string name) return;
        var charName = ShowCharacterSearch("Add Character to Group");
        if (charName == null) return;
        if (S.HotkeyGroups.TryGetValue(name, out var grp))
        {
            if (!grp.Characters.Contains(charName)) grp.Characters.Add(charName);
            TxtHotkeyGroupChars.Text = string.Join("\n", grp.Characters);
            RebuildHotkeyGroupPills();
            SaveDelayed();
        }
    }

    // ── Cycling group: drag-to-reorder character pills ──────────────
    // The order of HotkeyGroup.Characters IS the cycle order (CycleGroup walks
    // the list in order), so reordering pills reorders the cycle. The text box
    // stays as the underlying editor; pills and text are kept in sync both ways.

    /// <summary>Currently selected cycling group's character list, or null.</summary>
    private List<string>? CurrentHotkeyGroupChars =>
        CmbHotkeyGroup.SelectedItem is string n && S.HotkeyGroups.TryGetValue(n, out var g) ? g.Characters : null;

    private void RebuildHotkeyGroupPills()
    {
        HotkeyGroupPills.Children.Clear();
        var chars = CurrentHotkeyGroupChars;
        if (chars == null) return;

        for (int i = 0; i < chars.Count; i++)
            HotkeyGroupPills.Children.Add(MakeCharacterPill(chars[i], i + 1));
    }

    private Border MakeCharacterPill(string charName, int position)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        // Position number in a filled badge — makes the cycle order pop at a glance.
        row.Children.Add(new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = (Brush)FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = position.ToString(),
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            }
        });
        row.Children.Add(new TextBlock
        {
            Text = charName,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        });
        var remove = new Button
        {
            Content = "✕",
            Style = (Style)FindResource("IconBtn"),
            Margin = new Thickness(5, 0, 0, 0),
            ToolTip = EveCommandCenter.Services.LocalizationService.Str("L.Groups.PillRemoveTip", "Remove from group")
        };
        remove.Click += (_, _) => RemoveCharacterFromHotkeyGroup(charName);
        row.Children.Add(remove);

        var pill = new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = (Brush)FindResource("BgPanelBrush"),
            BorderBrush = (Brush)FindResource("AccentBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 3, 6, 3),
            Margin = new Thickness(0, 2, 4, 2),
            Cursor = System.Windows.Input.Cursors.SizeAll,
            Tag = charName,
            AllowDrop = true,
            Child = row,
            ToolTip = EveCommandCenter.Services.LocalizationService.Str("L.Groups.PillDragTip", "Drag to change cycle order")
        };
        pill.PreviewMouseMove += OnPillMouseMove;
        pill.Drop += OnPillDrop;
        pill.DragOver += OnPillDragOver;
        return pill;
    }

    // Live drag feedback: the pill being moved dims, and a thick accent bar marks
    // the slot it will drop into — so the new order is obvious before releasing.
    private Border? _dropTargetPill;

    private void ClearPillDragVisuals()
    {
        foreach (var child in HotkeyGroupPills.Children)
        {
            if (child is not Border b) continue;
            b.BorderBrush = (Brush)FindResource("AccentBrush");
            b.BorderThickness = new Thickness(1);
            b.Opacity = 1.0;
        }
        _dropTargetPill = null;
    }

    /// <summary>Mark where the drop will land: a fat bar on the leading edge of the
    /// target pill, or on the trailing edge of the last pill when dropping at the end.</summary>
    private void ShowDropIndicator(Border? target, bool atEnd = false)
    {
        if (ReferenceEquals(_dropTargetPill, target) && !atEnd) return;

        foreach (var child in HotkeyGroupPills.Children)
        {
            if (child is not Border b) continue;
            b.BorderBrush = (Brush)FindResource("AccentBrush");
            b.BorderThickness = new Thickness(1);
        }

        if (atEnd)
        {
            if (HotkeyGroupPills.Children.Count > 0 &&
                HotkeyGroupPills.Children[^1] is Border last)
            {
                last.BorderBrush = Brushes.White;
                last.BorderThickness = new Thickness(1, 1, 5, 1);
            }
            _dropTargetPill = null;
            return;
        }

        if (target != null)
        {
            target.BorderBrush = Brushes.White;
            target.BorderThickness = new Thickness(5, 1, 1, 1);
        }
        _dropTargetPill = target;
    }

    private void OnPillMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        if (sender is not Border pill || pill.Tag is not string charName) return;

        // DoDragDrop blocks until the drop completes, so restoring afterwards
        // in a finally guarantees visuals never stay stuck (including on cancel).
        pill.Opacity = 0.4;
        try { System.Windows.DragDrop.DoDragDrop(pill, charName, System.Windows.DragDropEffects.Move); }
        finally { ClearPillDragVisuals(); }
    }

    private void OnPillDragOver(object sender, System.Windows.DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(System.Windows.DataFormats.StringFormat);
        e.Effects = ok ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        if (ok && sender is Border pill) ShowDropIndicator(pill);
        e.Handled = true;
    }

    /// <summary>Hovering empty space in the panel — the character will go to the end.</summary>
    private void OnPillPanelDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Handled) return;
        bool ok = e.Data.GetDataPresent(System.Windows.DataFormats.StringFormat);
        e.Effects = ok ? System.Windows.DragDropEffects.Move : System.Windows.DragDropEffects.None;
        if (ok) ShowDropIndicator(null, atEnd: true);
    }

    /// <summary>Drop onto a pill — insert the dragged character at that pill's slot.</summary>
    private void OnPillDrop(object sender, System.Windows.DragEventArgs e)
    {
        ClearPillDragVisuals();
        if (sender is Border pill && pill.Tag is string targetChar)
            MoveCharacterInHotkeyGroup(e.Data.GetData(System.Windows.DataFormats.StringFormat) as string, targetChar);
        e.Handled = true;
    }

    /// <summary>Drop on empty space in the panel — send the character to the end.</summary>
    private void OnPillPanelDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Handled) return;
        ClearPillDragVisuals();
        MoveCharacterInHotkeyGroup(e.Data.GetData(System.Windows.DataFormats.StringFormat) as string, null);
    }

    private void MoveCharacterInHotkeyGroup(string? dragged, string? targetChar)
    {
        var chars = CurrentHotkeyGroupChars;
        if (chars == null || string.IsNullOrEmpty(dragged)) return;

        int from = chars.IndexOf(dragged);
        if (from < 0) return;
        int to = targetChar != null ? chars.IndexOf(targetChar) : chars.Count - 1;
        if (to < 0 || from == to) return;

        chars.RemoveAt(from);
        chars.Insert(to, dragged);
        SyncHotkeyGroupCharsUi();
    }

    private void RemoveCharacterFromHotkeyGroup(string charName)
    {
        var chars = CurrentHotkeyGroupChars;
        if (chars == null || !chars.Remove(charName)) return;
        SyncHotkeyGroupCharsUi();
    }

    /// <summary>Push the in-memory list back to the text box (without re-triggering
    /// its TextChanged save) and redraw the pills.</summary>
    private void SyncHotkeyGroupCharsUi()
    {
        var chars = CurrentHotkeyGroupChars;
        if (chars == null) return;
        _loadingDepth++;
        try { TxtHotkeyGroupChars.Text = string.Join("\n", chars); }
        finally { _loadingDepth--; }
        RebuildHotkeyGroupPills();
        SaveDelayed();
    }

    private void OnHotkeyGroupAddAllActive(object s, RoutedEventArgs e)
    {
        var chars = CurrentHotkeyGroupChars;
        if (chars == null)
        {
            MessageBox.Show(EveCommandCenter.Services.LocalizationService.Str("L.Groups.SelectGroupFirst", "Select or create a cycling group first."), EveCommandCenter.Services.LocalizationService.Str("L.Groups.CyclingHeader", "Cycling Hotkey Groups"));
            return;
        }

        var active = _overviewClients?.GetActiveCharacterNames().ToList() ?? new List<string>();
        if (active.Count == 0)
        {
            MessageBox.Show(EveCommandCenter.Services.LocalizationService.Str("L.Groups.NoActiveClients", "No active clients found."), EveCommandCenter.Services.LocalizationService.Str("L.Groups.CyclingHeader", "Cycling Hotkey Groups"));
            return;
        }

        int added = 0;
        foreach (var c in active)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;   // skip login screens (no character name yet)
            if (chars.Any(existing => string.Equals(existing, c, StringComparison.OrdinalIgnoreCase))) continue;
            chars.Add(c);
            added++;
        }

        if (added > 0) SyncHotkeyGroupCharsUi();
        else MessageBox.Show(EveCommandCenter.Services.LocalizationService.Str("L.Groups.AllAlreadyAdded", "All active clients are already in this group."), EveCommandCenter.Services.LocalizationService.Str("L.Groups.CyclingHeader", "Cycling Hotkey Groups"));
    }
}
