using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using EveCommandCenter.Interop;
using EveCommandCenter.Models;
using EveCommandCenter.Services;
using WinForms = System.Windows.Forms;

namespace EveCommandCenter.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _svc;
    private readonly OverviewClientService? _overviewClients;
    private readonly CropManager? _cropManager;
    private AppSettings S => _svc.Settings;
    private int _loadingDepth;
    private bool _loading => _loadingDepth > 0;
    private string _activePanel = "General";
    private bool _hasUnappliedChanges = false;

    /// <summary>Set while a hotkey-capture is holding the WPF mouse capture; cleared
    /// by its cleanup. Invoked from OnDeactivated as a last-resort release so the app
    /// can never keep the capture after losing focus and swallow every click (#93).</summary>
    private Action? _activeHotkeyCaptureCleanup;

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        var cleanup = _activeHotkeyCaptureCleanup;
        _activeHotkeyCaptureCleanup = null;
        cleanup?.Invoke();
    }

    public event Action? SettingsApplied;

    // Click-to-capture hotkey state
    private TextBox? _captureTarget;
    private bool _isCapturing;

    // Live clock timer
    private DispatcherTimer? _clockTimer;

    // Auto-apply timer — debounces 1s after last change, then reapplies settings live
    private readonly DispatcherTimer _autoApplyTimer;
    private bool _opacityOnlyChange;

    // Dark title bar
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int sz);

    public SettingsWindow(SettingsService svc, OverviewClientService? overviewClients = null, CropManager? cropManager = null)
    {
        _svc = svc;
        _overviewClients = overviewClients;
        _cropManager = cropManager;

        // Recovery net (issue #81): opening Settings always restores crops, so an
        // accidental Hide-Crops keypress can't leave them stuck hidden — the user's
        // natural "go to settings to fix it" reflex now just works, no toggle needed.
        _cropManager?.ShowCrops();

        // If something external (tray menu, profile-cycle hotkey, …) switches
        // the active profile while this window is open, the open UI is now bound
        // to the wrong profile — any edit the user makes would leak into the
        // newly-active profile based on stale on-screen values. Re-bind the UI
        // whenever LastUsedProfile actually changes.
        _svc.ProfileSwitched += OnSvcProfileSwitched;

        _autoApplyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _autoApplyTimer.Tick += (_, _) =>
        {
            _autoApplyTimer.Stop();
            _svc.Save();
            if (_opacityOnlyChange)
            {
                _overviewClients?.ReapplySettings();
                _opacityOnlyChange = false;
            }
            else
            {
                _overviewClients?.ReapplySettings();
            }
            SettingsApplied?.Invoke();
            _hasUnappliedChanges = false;
        };

        // Outer guard — must survive until WPF's Loaded event fires.
        // WPF defers events (Checked, SelectionChanged, TextChanged) that
        // fire AFTER the constructor returns but BEFORE the Loaded event.
        // Only the Loaded event guarantees all deferred work is complete.
        _loadingDepth++;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            // Release the constructor's loading guard — all deferred events are done
            _loadingDepth--;

            // Wire up Thumb drag events so scale is applied only on mouse-up,
            // preventing the feedback loop where scaling moves the slider thumb.
            var thumb = FindSliderThumb(SliderUiScale);
            if (thumb != null)
            {
                thumb.DragStarted += OnUiScaleThumbDragStarted;
                thumb.DragCompleted += OnUiScaleThumbDragCompleted;
            }
            // Also handle click-to-position on the track (DragCompleted doesn't fire for those)
            SliderUiScale.PreviewMouseLeftButtonUp += OnUiScaleMouseUp;
        };
        SizeChanged += OnWindowSizeChanged;
        Closing += OnWindowClosing;
        SourceInitialized += OnSourceInitializedRestoreSize;

        // Restore saved window size — but only if it actually exposes the
        // help sidebar. The wiki panel appears at ActualWidth >= 900 (see
        // OnWindowSizeChanged). Anything narrower means the user opens
        // settings without the contextual help, which is the regression
        // reported as "tiny settings window with no help sidebar." If the
        // saved size is below the threshold, compute a screen-aware default
        // that always shows the sidebar and fits the user's work area.
        const int MinReasonableWidth = 900;   // wiki sidebar threshold
        const int MinReasonableHeight = 700;  // enough for 16 nav buttons + header

        var workArea = System.Windows.SystemParameters.WorkArea;
        if (S.SettingsWindowWidth >= MinReasonableWidth)
        {
            Width = Math.Min(S.SettingsWindowWidth, workArea.Width);
        }
        else
        {
            // Screen-aware default. Floor at 1100 (sidebar + comfortable
            // panel area); cap at 1400 so 4K users don't get a giant
            // settings window; otherwise 65% of work area.
            Width = Math.Min(1400, Math.Max(1100, workArea.Width * 0.65));
        }
        if (S.SettingsWindowHeight >= MinReasonableHeight)
        {
            Height = Math.Min(S.SettingsWindowHeight, workArea.Height);
        }
        else
        {
            // Floor at 850, cap at 1100, otherwise 70% of work area.
            Height = Math.Min(1100, Math.Max(850, workArea.Height * 0.70));
        }
        System.Diagnostics.Debug.WriteLine($"[Settings:Ctor] restore size → saved {S.SettingsWindowWidth}x{S.SettingsWindowHeight}, work area {workArea.Width:F0}x{workArea.Height:F0}, applied Width={Width}, Height={Height}");

        // Live clock timer (Local + EVE/UTC)
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClocks();
        _clockTimer.Start();

        LoadSettings();
        ShowPanel("General");
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Prompt if there are changes that haven't been applied
        if (_hasUnappliedChanges)
        {
            var result = MessageBox.Show(
                "You have unapplied changes.\n\nDo you want to apply them before closing?",
                "Unsaved Changes",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                _svc.Save();
                _overviewClients?.ReapplySettings();
            }
            else if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            // No = close without applying
        }

        // Save window size — but if we're closing while Maximized, use the
        // RestoreBounds (the size before maximization) rather than the
        // current Width/Height. Otherwise we'd persist the screen size and
        // re-maximize forever, never letting the user's chosen smaller size
        // come back through.
        if (!EmbeddedModuleHost.IsEmbedded(this))
        {
            if (WindowState == WindowState.Maximized && RestoreBounds != Rect.Empty)
            {
                S.SettingsWindowWidth = (int)RestoreBounds.Width;
                S.SettingsWindowHeight = (int)RestoreBounds.Height;
            }
            else
            {
                S.SettingsWindowWidth = (int)Width;
                S.SettingsWindowHeight = (int)Height;
            }
        }
        _svc.Save();
        _clockTimer?.Stop();
        _svc.ProfileSwitched -= OnSvcProfileSwitched;
    }

    private void UpdateClocks()
    {
        var now = DateTime.Now;
        var utc = DateTime.UtcNow;
        Title = $"EVE Command Center — Settings | {now:HH:mm:ss} Local | {utc:HH:mm:ss} ET";
        TxtClockLocal.Text = now.ToString("hh:mm:ss tt");
        TxtClockEve.Text = utc.ToString("HH:mm:ss");
    }

    // ══════════════════════════════════════════════════════════════
    //  NAVIGATION
    // ══════════════════════════════════════════════════════════════
    private readonly string[] _panels = { "General","Hotkeys","Groups","Alerts","Backup","Sounds","Client","Performance","FPSLimiter","EVEManager","About","Debug","Crop" };

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
            ShowPanel(tag);
    }

    private void OnConfigureDiscord(object sender, RoutedEventArgs e)
    {
        var dialog = new DiscordWebhookSettingsWindow();
        if (IsVisible) dialog.Owner = this;
        dialog.ShowDialog();
    }

    private void OnOpenCloudBackup(object sender, RoutedEventArgs e)
    {
        // Reuse the original Google Drive backup and recovery UI. Do not create
        // a second OAuth client, backup store, or independent restore flow.
        var dialog = new CloudBackupWindow();
        if (IsVisible) dialog.Owner = this;
        dialog.ShowDialog();
    }

    public void OpenAlertsSection() => ShowPanel("Alerts");

    private void ShowPanel(string name)
    {
        _activePanel = name;
        foreach (var p in _panels)
        {
            var el = FindName("Panel" + p) as UIElement;
            if (el != null) el.Visibility = p == name ? Visibility.Visible : Visibility.Collapsed;
        }
        // Highlight active sidebar button
        foreach (var child in SidebarPanel.Children)
        {
            if (child is Button btn)
                btn.Background = (btn.Tag as string) == name
                    ? new SolidColorBrush(Color.FromRgb(28, 81, 75))
                    : Brushes.Transparent;
        }
        if (name == "Alerts") BuildAlertRows();
        if (name == "Sounds") { BuildSoundRows(); BuildClientVolumeRows(); }
        if (name == "Hotkeys") BuildHotkeyAudit();
        if (name == "FPSLimiter") DetectRtss();
        if (name == "EVEManager") LoadEveManagerPanel();
        if (name == "Performance") LoadPerformanceSettings();
        if (name == "Crop") LoadCropPanel();

        UpdateWikiContent();
    }

    // ══════════════════════════════════════════════════════════════
    //  LOAD / SAVE
    // ══════════════════════════════════════════════════════════════
    // Language switch (issue #86): persist the choice and swap the live string
    // overlay. Open windows update immediately via DynamicResource; text set from
    // code-behind refreshes when its window is reopened.
    private void OnLanguageChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (CmbLanguage.SelectedItem is not System.Windows.Controls.ComboBoxItem it) return;
        var code = (it.Tag as string) ?? "en";
        S.Language = code;
        LocalizationService.SetLanguage(code);
        UpdateWikiContent(); // refresh the Help sidebar in the new language immediately
        SaveDelayed();
    }

    private void LoadSettings()
    {
        _loadingDepth++;
        try
        {
            // Profiles
            CmbProfiles.Items.Clear();
            foreach (var p in _svc.GetProfileNames()) CmbProfiles.Items.Add(p);
            CmbProfiles.SelectedItem = S.LastUsedProfile;

            // General
            // Language selector (issue #86) — populate once, then select the active language.
            if (CmbLanguage.Items.Count == 0)
                foreach (var (code, name) in LocalizationService.Languages)
                    CmbLanguage.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = name, Tag = code });
            foreach (System.Windows.Controls.ComboBoxItem it in CmbLanguage.Items)
                if ((it.Tag as string) == LocalizationService.CurrentLanguage) { CmbLanguage.SelectedItem = it; break; }

            CmbHotkeyScope.SelectedIndex = S.GlobalHotkeys ? 0 : 1;
            TxtSuspendHotkey.Text = S.SuspendHotkey;
            TxtHideShowHotkey.Text = S.HideShowThumbnailsHotkey;
            TxtHideCropsHotkey.Text = S.HideShowCropsHotkey;
            TxtProfileCycleForward.Text = S.ProfileCycleForwardHotkey;
            TxtProfileCycleBackward.Text = S.ProfileCycleBackwardHotkey;
            TxtQuickSwitchHotkey.Text = S.QuickSwitchHotkey;
            ChkBroadcastHud.IsChecked = S.ShowBroadcastKeyHud;
            ChkAutoSoloAudio.IsChecked = S.AutoSoloClientAudio;
            TxtMinimizeDelay.Text = S.MinimizeDelay.ToString();
            TxtCycleDelay.Text = S.CycleDelayMs.ToString();
            ChkCycleWhileHeld.IsChecked = S.CycleWhileHeld;
            CmbStartupSettings.SelectedIndex = (int)S.StartupSettings;
            ChkAutoLaunchCharacterOverview.IsChecked = S.AutoLaunchCharacterOverview;
            ChkMinimizeCommandCenterOnOverviewLaunch.IsChecked = S.MinimizeCommandCenterOnOverviewLaunch;

            // UI Scale
            SliderUiScale.Value = S.SettingsUiFontSize;
            TxtUiScaleValue.Text = $"{S.SettingsUiFontSize}pt";
            ApplyUiScale(S.SettingsUiFontSize);

            // Alerts
            ChkChatLogMon.IsChecked = S.EnableChatLogMonitoring;
            TxtChatLogDir.Text = S.ChatLogDirectory;
            ChkGameLogMon.IsChecked = S.EnableGameLogMonitoring;
            TxtGameLogDir.Text = S.GameLogDirectory;
            ChkUnderFire.IsChecked = S.EnableUnderFireIndicator;
            TxtUnderFireTimeout.Text = S.UnderFireTimeoutSeconds.ToString();

            ChkPveMode.IsChecked = S.PveMode;
            int alertOpacity = Math.Clamp(S.AlertOpacityPercent, 10, 100);
            SliderAlertOpacity.Value = alertOpacity;
            TxtAlertOpacityValue.Text = $"{alertOpacity}%";
            TxtAlertBorderThickness.Text = S.AlertBorderThickness.ToString();
            ChkAlertBadgeOnThumbnails.IsChecked = S.ShowAlertBadgeOnThumbnails;
            ChkAlertHub.IsChecked = S.AlertHubEnabled;
            TxtToastDuration.Text = S.AlertToastDuration.ToString();
            ChkAlertHubAutoHide.IsChecked = S.AlertHubAutoHide;
            TxtAlertHubAutoHideSeconds.Text = S.AlertHubAutoHideSeconds.ToString();
            ChkSuppressToastActive.IsChecked = S.SuppressAlertHubToastForActiveClient;
            SetNotLoggedInDDL(S.NotLoggedInIndicator);
            TxtNotLoggedInColor.Text = S.NotLoggedInColor;
            UpdateColorPreview(TxtNotLoggedInColor, PreviewNotLoggedIn);

            // Sounds
            ChkEnableSounds.IsChecked = S.EnableAlertSounds;
            TxtMasterVolume.Text = S.AlertSoundVolume.ToString();

            // Client
            ChkCharSelectCycle.IsChecked = S.CharSelectCyclingEnabled;
            TxtCharSelectFwd.Text = S.CharSelectForwardHotkey;
            TxtCharSelectBwd.Text = S.CharSelectBackwardHotkey;
            ChkMinimizeInactive.IsChecked = S.MinimizeInactiveClients;
            ChkAlwaysMaximize.IsChecked = S.AlwaysMaximize;
            ChkTrackClientPositions.IsChecked = S.TrackClientPositions;
            ChkClientCoverTaskbar.IsChecked = S.ClientCoverTaskbar;
            CmbClientPosition.SelectedIndex = Math.Clamp(S.ClientPositionMode, 0, 2);
            TxtClientPositionX.Text = S.ClientPositionX.ToString();
            TxtClientPositionY.Text = S.ClientPositionY.ToString();
            UpdateClientPositionXYVisibility();
            LoadDontMinimizeList();

            // FPS
            ChkShowFps.IsChecked = S.ShowRtssFps;
            SelectFpsLimit(S.RtssFpsLimit);

            // Hotkeys
            LoadHotkeysList();
            LoadHotkeyGroups();

            // Debug
            if (ChkDebugInjection != null) ChkDebugInjection.IsChecked = S.EnableDebugLogging_Injection;
            if (ChkDebugCycling != null) ChkDebugCycling.IsChecked = S.EnableDebugLogging_Cycling;
            if (ChkDebugWindowHooks != null) ChkDebugWindowHooks.IsChecked = S.EnableDebugLogging_WindowHooks;
            if (ChkDebugDwm != null) ChkDebugDwm.IsChecked = S.EnableDebugLogging_DWM;
            if (ChkDebugAlerts != null) ChkDebugAlerts.IsChecked = S.EnableDebugLogging_Alerts;

            // Color blind button state
            UpdateColorBlindButton();
        }
        finally { _loadingDepth--; }
    }

    private void SaveDelayed()
    {
        if (!_loading)
        {
            _hasUnappliedChanges = true;
            _svc.SaveDelayed();

            // Reset the 1s auto-apply timer — after 1s of no further changes, apply live
            _autoApplyTimer.Stop();
            _autoApplyTimer.Start();
            // Clear opacity-only flag — OnOpacityChanged re-sets it AFTER this call
            _opacityOnlyChange = false;
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  PROFILES
    // ══════════════════════════════════════════════════════════════
    private void OnProfileChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading || CmbProfiles.SelectedItem is not string name) return;
        if (name == _svc.Settings.LastUsedProfile) return;
        // SwitchProfile raises ProfileSwitched, which OnSvcProfileSwitched
        // handles — UI reload + thumbnail/crop reapply happens there so the
        // same path runs no matter who triggers the switch.
        _svc.SwitchProfile(name);
    }

    /// <summary>Re-bind the UI whenever the active profile changes from any
    /// source (this window's dropdown, the tray menu, or the cycle hotkey).
    /// Without this, stale on-screen values from the previous profile would
    /// be written back into the newly-active profile on the user's next edit.</summary>
    private void OnSvcProfileSwitched(string name)
    {
        Dispatcher.Invoke(() =>
        {
            if (CmbProfiles.SelectedItem as string != name)
            {
                _loadingDepth++;
                try { CmbProfiles.SelectedItem = name; }
                finally { _loadingDepth--; }
            }
            LoadSettings();
            // Applying the profile (thumbnails + crops + hotkeys) is App's ProfileSwitched
            // handler now (#101) — it runs whether or not this window is open. This one
            // only re-binds the UI, so the two don't apply the same switch twice.
            SettingsApplied?.Invoke();
        });
    }

    private void OnCreateProfile(object s, RoutedEventArgs e)
    {
        var name = Microsoft.VisualBasic.Interaction.InputBox("Profile name:", "New Profile");
        if (string.IsNullOrWhiteSpace(name)) return;
        _svc.CreateProfile(name);
        LoadSettings();
    }

    private void OnDeleteProfile(object s, RoutedEventArgs e)
    {
        if (CmbProfiles.SelectedItem is not string name) return;
        if (MessageBox.Show($"Delete profile '{name}'?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _svc.DeleteProfile(name);
        LoadSettings();
    }

    private void OnApplySettings(object s, RoutedEventArgs e)
    {
        _svc.Save();
        _overviewClients?.ReapplySettings();
        _cropManager?.Refresh();
        SettingsApplied?.Invoke();
        _hasUnappliedChanges = false;
    }

    // ══════════════════════════════════════════════════════════════
    //  COLOR BLIND MODE
    // ══════════════════════════════════════════════════════════════

    // Normal defaults
    private static readonly Dictionary<string, string> NormalSeverityColors = new()
    {
        ["critical"] = "#FF0000", ["warning"] = "#FFA500", ["info"] = "#4A9EFF"
    };

    // Wong's universally safe color-blind palette
    private static readonly Dictionary<string, string> CbSeverityColors = new()
    {
        ["critical"] = "#D55E00",  // vermillion — distinct for all CB types
        ["warning"] = "#F0E442",   // yellow — visible to all
        ["info"] = "#0072B2"       // blue — safe for all
    };

    private const string NormalFlashColor = "0xff0000";
    private const string CbFlashColor = "0xCC79A7";           // reddish-pink — CB-safe
    private const string NormalHighlightColor = "#E36A0D";
    private const string CbHighlightColor = "#56B4E9";        // sky blue — CB-safe
    private const string NormalStatOverlayText = "#00FF88";
    private const string CbStatOverlayText = "#56B4E9";       // sky blue — green is red-green CB problem
    private const string NormalThumbnailText = "#FAC57A";
    private const string CbThumbnailText = "#FFFFFF";          // white — universally readable
    private const string NormalNotLoggedIn = "#555555";
    private const string CbNotLoggedIn = "#888888";            // brighter gray — better contrast for low vision



    private void OnToggleColorBlind(object sender, RoutedEventArgs e)
    {
        S.ColorBlindMode = !S.ColorBlindMode;
        ApplyColorBlindPalette(S.ColorBlindMode);
        UpdateColorBlindButton();
        SaveDelayed();
        LoadSettings();           // refresh bound controls
        ShowPanel(_activePanel);  // rebuild dynamic panels (alert swatches, severity rows, etc.)
    }

    private void ApplyColorBlindPalette(bool cbMode)
    {
        // Severity tier colors
        var sevPalette = cbMode ? CbSeverityColors : NormalSeverityColors;
        foreach (var kv in sevPalette)
            S.SeverityColors[kv.Key] = kv.Value;

        // Alert flash + active client highlight
        S.AlertFlashColor = cbMode ? CbFlashColor : NormalFlashColor;
        S.ClientHighlightColor = cbMode ? CbHighlightColor : NormalHighlightColor;

        // Stat overlay text (green → sky blue)
        S.StatOverlayTextColor = cbMode ? CbStatOverlayText : NormalStatOverlayText;

        // Thumbnail text overlay
        S.ThumbnailTextColor = cbMode ? CbThumbnailText : NormalThumbnailText;

        // Not-logged-in indicator
        S.NotLoggedInColor = cbMode ? CbNotLoggedIn : NormalNotLoggedIn;

        // Clear per-alert color overrides so they pick up the new severity defaults
        if (cbMode)
            S.AlertColors.Clear();
    }

    private void UpdateColorBlindButton()
    {
        if (BtnColorBlind == null) return;
        if (S.ColorBlindMode)
        {
            BtnColorBlind.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#0072B2")!);
            BtnColorBlind.Foreground = Brushes.White;
            BtnColorBlind.Content = "👁 CB: ON";
        }
        else
        {
            BtnColorBlind.Background = System.Windows.Media.Brushes.Transparent;
            BtnColorBlind.Foreground = FindResource("TextPrimaryBrush") as Brush ?? Brushes.White;
            BtnColorBlind.Content = "👁 CB Mode";
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  WIKI / HELP SIDEBAR
    // ══════════════════════════════════════════════════════════════

    private void OnSourceInitializedRestoreSize(object? sender, EventArgs e)
    {
        // Re-apply saved size after the HWND exists. Covers WPF timing edge cases
        // where Width/Height set in the constructor don't fully commit when the
        // window is Show()'n from a deferred dispatcher timer (auto-open path).
        // Same sanity floor as the constructor — only re-apply saved size if it
        // exposes the help sidebar; otherwise leave whatever the constructor
        // computed (screen-aware default that always shows the sidebar).
        const int MinReasonableWidth = 900;
        const int MinReasonableHeight = 700;
        if (S.SettingsWindowWidth >= MinReasonableWidth && Math.Abs(Width - S.SettingsWindowWidth) > 1)
            Width = S.SettingsWindowWidth;
        if (S.SettingsWindowHeight >= MinReasonableHeight && Math.Abs(Height - S.SettingsWindowHeight) > 1)
            Height = S.SettingsWindowHeight;
        System.Diagnostics.Debug.WriteLine($"[Settings:SourceInit] size → Width={Width}, Height={Height}");
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Show wiki panel when window is wide enough (matches AHK behavior)
        if (ActualWidth >= 900)
        {
            WikiColumn.Width = new GridLength(260);
            WikiPanel.Visibility = Visibility.Visible;
            WikiSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            WikiColumn.Width = new GridLength(0);
            WikiPanel.Visibility = Visibility.Collapsed;
            WikiSplitter.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateWikiContent()
    {
        if (WikiContent == null) return;
        WikiContent.Text = GetWikiContent(_activePanel);
    }

    // ══════════════════════════════════════════════════════════════
    //  DEBUG
    // ══════════════════════════════════════════════════════════════
    private void OnDebugChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.EnableDebugLogging_Injection = ChkDebugInjection?.IsChecked ?? false;
        S.EnableDebugLogging_Cycling = ChkDebugCycling?.IsChecked ?? false;
        S.EnableDebugLogging_WindowHooks = ChkDebugWindowHooks?.IsChecked ?? false;
        S.EnableDebugLogging_DWM = ChkDebugDwm?.IsChecked ?? false;
        S.EnableDebugLogging_Alerts = ChkDebugAlerts?.IsChecked ?? false;
        SaveDelayed();
    }

    private void OnOpenLogsFolder(object sender, RoutedEventArgs e)
    {
        EveCommandCenter.Services.DiagnosticsService.OpenLogsFolder();
    }

    // ══════════════════════════════════════════════════════════════
    //  UI SCALE
    // ══════════════════════════════════════════════════════════════

    private bool _uiScaleDragging;
    private DispatcherTimer? _uiScaleClickTimer;

    private void OnUiScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        int size = (int)SliderUiScale.Value;
        TxtUiScaleValue.Text = $"{size}pt";
        // Don't apply scale here — wait for DragCompleted / click timer to avoid feedback loop
    }

    private void OnUiScaleThumbDragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        _uiScaleDragging = true;
    }

    private void OnUiScaleThumbDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _uiScaleDragging = false;
        ApplyUiScaleFromSlider();
    }

    private void OnUiScaleMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Fires on click-to-position (track click) where DragCompleted doesn't fire.
        // Debounce with a short timer so rapid clicks defer like a drag does.
        if (_uiScaleDragging) return;

        _uiScaleClickTimer?.Stop();
        _uiScaleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _uiScaleClickTimer.Tick += (_, _) =>
        {
            _uiScaleClickTimer.Stop();
            ApplyUiScaleFromSlider();
        };
        _uiScaleClickTimer.Start();
    }

    private void ApplyUiScaleFromSlider()
    {
        int size = (int)SliderUiScale.Value;
        S.SettingsUiFontSize = size;
        ApplyUiScale(size);
        SaveDelayed();
    }

    private void ApplyUiScale(int fontSize)
    {
        double scale = fontSize / 12.0;
        var content = this.Content as FrameworkElement;
        if (content != null)
            content.LayoutTransform = new ScaleTransform(scale, scale);
    }

    private static System.Windows.Controls.Primitives.Thumb? FindSliderThumb(Slider slider)
    {
        slider.ApplyTemplate();
        var track = slider.Template.FindName("PART_Track", slider) as System.Windows.Controls.Primitives.Track;
        return track?.Thumb;
    }

    /// <summary>Contextual Help-sidebar text for a panel. Resolves a translated
    /// block from the active-language resource dictionary (key L.Wiki.&lt;panel&gt;)
    /// when present, otherwise falls back to the English text below (issue #86).</summary>
    private static string GetWikiContent(string panelName)
    {
        // Retired preview instructions in older translation packs must not be shown.
        return GetWikiContentEnglish(panelName);
    }

    private static string GetWikiContentEnglish(string panelName) => panelName switch
    {
        "General" => "Overview and client controls: hotkey scope, startup behaviour, audio solo and cycling. Choose Mining or Preview in the overview toolbar. Separate preview windows are no longer used.",
        "Hotkeys" => "Assign a key to an EVE client. Shared keys cycle between their assigned clients. Suspending hotkeys does not stop data collection.",
        "Groups" => "Create named cycling groups and drag their character pills to set the switching order.",
        "Alerts" => "Choose which events produce alerts, sounds and overview badges. Mining alarms can also be muted per pilot on the overview.",
        "Sounds" => "Set alert sound volume and per-client EVE audio volume. Auto-solo follows the active EVE client.",
        "Client" => "Configure client positioning, minimise behaviour and character-selection cycling. These settings affect EVE windows, not overview card size.",
        "Crop" => "Create cropped views of a client's visible area. Crops remain independent of the combined overview and keep their own visibility control.",
        "Performance" => "Configure CPU affinity and process monitoring. Live versus snapshot rendering is selected in the overview toolbar.",
        "FPSLimiter" => "Configure RTSS integration and FPS display for your EVE clients.",
        "Backup" => "Back up or restore Command Center data using the existing backup service and your chosen schedule.",
        _ => "Changes apply to the selected settings profile. The combined overview retains its own layout, display modes and mining controls."
    };

    // ══════════════════════════════════════════════════════════════
    //  CLICK-TO-CAPTURE HOTKEY INFRASTRUCTURE
    // ══════════════════════════════════════════════════════════════

    private void StartHotkeyCapture(TextBox target)
    {
        if (_isCapturing)
        {
            StopHotkeyCapture();
            return;
        }
        _captureTarget = target;
        _isCapturing = true;
        target.Text = "Press a key or mouse button...";
        target.Background = new SolidColorBrush(Color.FromRgb(80, 40, 20));
        // Suppress Tab navigation so Tab key reaches PreviewKeyDown
        KeyboardNavigation.SetTabNavigation(target, KeyboardNavigationMode.None);
        KeyboardNavigation.SetDirectionalNavigation(target, KeyboardNavigationMode.None);
        target.Focus();
        target.PreviewKeyDown += OnHotkeyCaptured;
        target.PreviewMouseDown += OnMouseCaptured;

        // Install a temporary low-level keyboard hook to catch exotic keys
        // that WPF's PreviewKeyDown can't represent (e.g., hardware-mapped
        // keystrokes from SteelSeries, Razer, Corsair software)
        InstallCaptureHook();

        Debug.WriteLine($"[Settings:Capture] ⌨ Hotkey capture started for {target.Name}");
    }

    private void StopHotkeyCapture()
    {
        RemoveCaptureHook();

        if (_captureTarget != null)
        {
            _captureTarget.PreviewKeyDown -= OnHotkeyCaptured;
            _captureTarget.PreviewMouseDown -= OnMouseCaptured;
            _captureTarget.Background = (Brush)FindResource("BgPanelBrush");
            // Restore normal Tab navigation
            KeyboardNavigation.SetTabNavigation(_captureTarget, KeyboardNavigationMode.Continue);
            KeyboardNavigation.SetDirectionalNavigation(_captureTarget, KeyboardNavigationMode.Continue);
        }
        _captureTarget = null;
        _isCapturing = false;
    }

    // ── Low-Level Keyboard Hook for Exotic Key Capture ──────────────
    // WPF's PreviewKeyDown can't intercept keys that don't map to the
    // System.Windows.Input.Key enum (e.g., F13-F24 from SteelSeries/Razer
    // hardware remapping software). This temporary hook catches the raw
    // Win32 VK code and converts it to an AHK-format string.

    private IntPtr _captureHookHandle = IntPtr.Zero;
    private Interop.User32.LowLevelKeyboardProc? _captureHookProc;
    private bool _captureHookFired = false; // prevent double-fire with PreviewKeyDown

    private void InstallCaptureHook()
    {
        if (_captureHookHandle != IntPtr.Zero) return;
        _captureHookFired = false;
        _captureHookProc = CaptureHookCallback;
        _captureHookHandle = Interop.User32.SetWindowsHookEx(
            Interop.User32.WH_KEYBOARD_LL,
            _captureHookProc,
            Interop.User32.GetModuleHandle(null),
            0);
        Debug.WriteLine($"[Settings:Capture] 🪝 Low-level capture hook installed: {_captureHookHandle != IntPtr.Zero}");
    }

    private void RemoveCaptureHook()
    {
        if (_captureHookHandle != IntPtr.Zero)
        {
            Interop.User32.UnhookWindowsHookEx(_captureHookHandle);
            _captureHookHandle = IntPtr.Zero;
            Debug.WriteLine("[Settings:Capture] 🛑 Low-level capture hook removed");
        }
        _captureHookFired = false;
    }

    private IntPtr CaptureHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isCapturing && !_captureHookFired)
        {
            int msg = wParam.ToInt32();
            if (msg == Interop.User32.WM_KEYDOWN || msg == Interop.User32.WM_SYSKEYDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<Interop.User32.KBDLLHOOKSTRUCT>(lParam);
                uint vk = hookStruct.vkCode;

                // Ignore modifier keys themselves
                if (vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0x5B or 0x5C
                    or 0x10 or 0x11 or 0x12)
                {
                    return Interop.User32.CallNextHookEx(_captureHookHandle, nCode, wParam, lParam);
                }

                // Escape cancels capture
                if (vk == 0x1B)
                {
                    _captureHookFired = true;
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (_captureTarget != null) _captureTarget.Text = "";
                        StopHotkeyCapture();
                    });
                    return Interop.User32.CallNextHookEx(_captureHookHandle, nCode, wParam, lParam);
                }

                // Try WPF conversion first — if it succeeds, let PreviewKeyDown handle it
                var wpfKey = KeyInterop.KeyFromVirtualKey((int)vk);
                if (wpfKey != Key.None && wpfKey != Key.DeadCharProcessed)
                {
                    // WPF can handle this key — let PreviewKeyDown fire normally
                    return Interop.User32.CallNextHookEx(_captureHookHandle, nCode, wParam, lParam);
                }

                // WPF CAN'T handle this key — raw VK capture path
                _captureHookFired = true;
                string ahkName = VkToAhkName(vk);

                // Build modifier string from current keyboard state
                string modStr = "";
                if (Interop.User32.IsKeyDown(0x11)) modStr += "^";  // Ctrl
                if (Interop.User32.IsKeyDown(0x12)) modStr += "!";  // Alt
                if (Interop.User32.IsKeyDown(0x10)) modStr += "+";  // Shift
                if (Interop.User32.IsKeyDown(0x5B) || Interop.User32.IsKeyDown(0x5C)) modStr += "#"; // Win

                string fullAhk = modStr + ahkName;

                Dispatcher.BeginInvoke(() =>
                {
                    if (_captureTarget != null)
                    {
                        if (CheckHotkeyConflicts(fullAhk, _captureTarget.Name))
                            Debug.WriteLine($"[Settings:Capture] ⛔ Blocked raw VK: {fullAhk} (conflict)");
                        else
                        {
                            _captureTarget.Text = fullAhk;
                            Debug.WriteLine($"[Settings:Capture] ✅ Raw VK captured: {fullAhk} (VK=0x{vk:X2})");
                        }
                    }
                    StopHotkeyCapture();
                });

                // Block the key from reaching any app during capture
                return (IntPtr)1;
            }
        }
        return Interop.User32.CallNextHookEx(_captureHookHandle, nCode, wParam, lParam);
    }

    /// <summary>Convert a raw Win32 VK code to an AHK key name. Used for exotic hardware keys.</summary>
    internal static string VkToAhkName(uint vk)
    {
        // F1-F24
        if (vk >= 0x70 && vk <= 0x87)
            return $"F{vk - 0x70 + 1}";

        // Standard named keys
        return vk switch
        {
            0x20 => "Space", 0x0D => "Enter", 0x09 => "Tab", 0x08 => "Backspace",
            0x2E => "Delete", 0x2D => "Insert", 0x24 => "Home", 0x23 => "End",
            0x21 => "PgUp", 0x22 => "PgDn",
            0x26 => "Up", 0x28 => "Down", 0x25 => "Left", 0x27 => "Right",
            0x13 => "Pause", 0x91 => "ScrollLock", 0x14 => "CapsLock", 0x90 => "NumLock",
            0x2C => "PrintScreen", 0x1B => "Escape",
            // Numpad
            0x60 => "Numpad0", 0x61 => "Numpad1", 0x62 => "Numpad2",
            0x63 => "Numpad3", 0x64 => "Numpad4", 0x65 => "Numpad5",
            0x66 => "Numpad6", 0x67 => "Numpad7", 0x68 => "Numpad8",
            0x69 => "Numpad9",
            0x6A => "NumpadMult", 0x6B => "NumpadAdd", 0x6D => "NumpadSub",
            0x6F => "NumpadDiv", 0x6E => "NumpadDot",
            // Mouse (rarely appears here but just in case)
            0x05 => "XButton1", 0x06 => "XButton2", 0x04 => "MButton",
            // Letters/digits: VK_A-VK_Z (0x41-0x5A), VK_0-VK_9 (0x30-0x39)
            _ when vk >= 0x41 && vk <= 0x5A => ((char)vk).ToString().ToLowerInvariant(),
            _ when vk >= 0x30 && vk <= 0x39 => ((char)vk).ToString(),
            // Truly unknown: emit a recognizable VK hex code
            _ => $"vk{vk:X2}"
        };
    }

    private void OnHotkeyCaptured(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true; // Prevent WPF from processing the key

        // Ignore modifier-only presses
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
            return;

        // Escape cancels capture
        if (key == Key.Escape)
        {
            if (_captureTarget != null)
                _captureTarget.Text = "";
            StopHotkeyCapture();
            return;
        }

        // Build AHK-format string
        string ahkStr = "";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";

        ahkStr += KeyToAhkName(key);

        if (_captureTarget != null)
        {
            // Check for conflicts before assigning
            if (CheckHotkeyConflicts(ahkStr, _captureTarget.Name))
            {
                Debug.WriteLine($"[Settings:Capture] ⛔ Blocked: {ahkStr} (conflict)");
            }
            else
            {
                _captureTarget.Text = ahkStr;
                Debug.WriteLine($"[Settings:Capture] ✅ Captured: {ahkStr}");
            }
        }

        StopHotkeyCapture();
    }

    private void OnMouseCaptured(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        string? buttonName = e.ChangedButton switch
        {
            MouseButton.XButton1 => "XButton1",
            MouseButton.XButton2 => "XButton2",
            MouseButton.Middle => "MButton",
            _ => null
        };

        if (buttonName == null) return; // ignore left/right clicks

        e.Handled = true;

        string ahkStr = "";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) ahkStr += "^";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) ahkStr += "!";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) ahkStr += "+";
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) ahkStr += "#";
        ahkStr += buttonName;

        if (_captureTarget != null)
        {
            if (CheckHotkeyConflicts(ahkStr, _captureTarget.Name))
                Debug.WriteLine($"[Settings:Capture] ⛔ Blocked mouse: {ahkStr} (conflict)");
            else
            {
                _captureTarget.Text = ahkStr;
                Debug.WriteLine($"[Settings:Capture] ✅ Captured mouse: {ahkStr}");
            }
        }

        StopHotkeyCapture();
    }

    internal static string KeyToAhkName(Key key)
    {
        return key switch
        {
            Key.F1 => "F1", Key.F2 => "F2", Key.F3 => "F3", Key.F4 => "F4",
            Key.F5 => "F5", Key.F6 => "F6", Key.F7 => "F7", Key.F8 => "F8",
            Key.F9 => "F9", Key.F10 => "F10", Key.F11 => "F11", Key.F12 => "F12",
            Key.F13 => "F13", Key.F14 => "F14", Key.F15 => "F15", Key.F16 => "F16",
            Key.F17 => "F17", Key.F18 => "F18", Key.F19 => "F19", Key.F20 => "F20",
            Key.F21 => "F21", Key.F22 => "F22", Key.F23 => "F23", Key.F24 => "F24",
            Key.Escape => "Escape",
            Key.Space => "Space", Key.Return => "Enter", Key.Tab => "Tab",
            Key.Back => "Backspace", Key.Delete => "Delete", Key.Insert => "Insert",
            Key.Home => "Home", Key.End => "End", Key.PageUp => "PgUp", Key.PageDown => "PgDn",
            Key.Up => "Up", Key.Down => "Down", Key.Left => "Left", Key.Right => "Right",
            Key.Pause => "Pause", Key.Scroll => "ScrollLock",
            Key.CapsLock => "CapsLock", Key.NumLock => "NumLock",
            Key.PrintScreen => "PrintScreen",
            Key.NumPad0 => "Numpad0", Key.NumPad1 => "Numpad1", Key.NumPad2 => "Numpad2",
            Key.NumPad3 => "Numpad3", Key.NumPad4 => "Numpad4", Key.NumPad5 => "Numpad5",
            Key.NumPad6 => "Numpad6", Key.NumPad7 => "Numpad7", Key.NumPad8 => "Numpad8",
            Key.NumPad9 => "Numpad9", Key.Multiply => "NumpadMult", Key.Add => "NumpadAdd",
            Key.Subtract => "NumpadSub", Key.Divide => "NumpadDiv", Key.Decimal => "NumpadDot",
            // Top-row digits: WPF stringifies these as "D0".."D9" which the hotkey
            // parser can't resolve. Emit the bare digit so ParseVirtualKey maps to
            // VK_0..VK_9 directly.
            Key.D0 => "0", Key.D1 => "1", Key.D2 => "2", Key.D3 => "3", Key.D4 => "4",
            Key.D5 => "5", Key.D6 => "6", Key.D7 => "7", Key.D8 => "8", Key.D9 => "9",
            Key.OemTilde => "`", Key.OemMinus => "-", Key.OemPlus => "=",
            Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\", Key.OemSemicolon => ";", Key.OemQuotes => "'",
            Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/",
            // Distinct ISO <>| / extra-backslash key — emit a token the parser
            // resolves to VK_OEM_102 rather than the "oembackslash" fallback (#67).
            Key.OemBackslash => "OEM102",
            _ => key.ToString().ToLowerInvariant()
        };
    }

    /// <summary>Check for hotkey conflicts. When systemOnly=true, only checks system hotkeys (not other character bindings).</summary>
    private bool CheckHotkeyConflicts(string ahkStr, string? excludeField, bool systemOnly = false)
    {
        if (string.IsNullOrWhiteSpace(ahkStr)) return false;

        // System hotkeys — always checked
        var systemFields = new Dictionary<string, string>
        {
            ["TxtSuspendHotkey"] = TxtSuspendHotkey.Text,
            ["TxtHideShowHotkey"] = TxtHideShowHotkey.Text,
            ["TxtHideCropsHotkey"] = TxtHideCropsHotkey.Text,
            ["TxtProfileCycleForward"] = TxtProfileCycleForward.Text,
            ["TxtProfileCycleBackward"] = TxtProfileCycleBackward.Text,
            ["TxtCharSelectFwd"] = TxtCharSelectFwd.Text,
            ["TxtCharSelectBwd"] = TxtCharSelectBwd.Text,
        };

        foreach (var (fieldName, value) in systemFields)
        {
            if (fieldName == excludeField) continue;
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (string.Equals(value, ahkStr, StringComparison.OrdinalIgnoreCase))
            {
                string displayName = fieldName.Replace("Txt", "").Replace("Hotkey", "");
                MessageBox.Show($"⚠ Hotkey conflict!\n\n'{ahkStr}' is already assigned to {displayName}.",
                    "Hotkey Conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
                return true;
            }
        }

        // For system hotkey fields, also check character hotkeys and group hotkeys
        if (!systemOnly)
        {
            foreach (var (charName, binding) in _svc.CurrentProfile.Hotkeys)
            {
                if (string.Equals(binding.Key, ahkStr, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show($"⚠ Hotkey conflict!\n\n'{ahkStr}' is already assigned to character '{charName}'.",
                        "Hotkey Conflict", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                }
            }
        }

        return false;
    }

    // Stores the previous hotkey text so we can revert on conflict
    private readonly Dictionary<string, string> _hotkeyPriorText = new();

    /// <summary>GotFocus handler — stores the current value before edits begin.</summary>
    private void OnHotkeyFieldGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && !string.IsNullOrEmpty(tb.Name))
            _hotkeyPriorText[tb.Name] = tb.Text;
    }

    /// <summary>LostFocus handler for system hotkey TextBoxes — validates manual text entry.</summary>
    private void OnHotkeyFieldLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not System.Windows.Controls.TextBox tb) return;
        string text = tb.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        if (CheckHotkeyConflicts(text, tb.Name))
        {
            string prior = _hotkeyPriorText.GetValueOrDefault(tb.Name, "");
            tb.Text = prior;
            SaveGeneral();
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  PERFORMANCE
    // ══════════════════════════════════════════════════════════════

    private void LoadPerformanceSettings()
    {
        _loadingDepth++;
        try
        {
            ChkManageAffinity.IsChecked = S.ManageAffinity;
            ChkAutoBalanceCores.IsChecked = S.AutoBalanceCores;

            // Build core options list: "Auto (App Decides)", "Core 0", "Core 1", ...
            var cores = new List<string> { "Auto (App Decides)" };
            for (int i = 0; i < Environment.ProcessorCount; i++)
                cores.Add($"Core {i}");

            var rows = new List<PerClientCoreRow>();
            var chars = new List<string>();
            if (_overviewClients != null)
            {
                chars = _overviewClients.GetActiveCharacterNames()
                    .Where(n => !string.IsNullOrEmpty(n))
                    .OrderBy(n => n)
                    .ToList();
            }

            foreach (var ch in chars)
            {
                int coreId = S.PerClientCores.GetValueOrDefault(ch, -1);
                string selectedCore = coreId >= 0 && coreId < Environment.ProcessorCount
                    ? $"Core {coreId}"
                    : "Auto (App Decides)";

                rows.Add(new PerClientCoreRow
                {
                    Character = ch,
                    AvailableCores = cores,
                    SelectedCore = selectedCore
                });
            }

            LvPerClientCores.ItemsSource = rows;
        }
        finally { _loadingDepth--; }
    }

    private void OnPerformanceChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.ManageAffinity = ChkManageAffinity.IsChecked == true;
        S.AutoBalanceCores = ChkAutoBalanceCores.IsChecked == true;
        SaveDelayed();
    }

    private void OnPerClientCoreChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not System.Windows.Controls.ComboBox cb || cb.DataContext is not PerClientCoreRow row) return;

        int coreId = -1; // -1 means Auto
        if (row.SelectedCore.StartsWith("Core "))
        {
            if (int.TryParse(row.SelectedCore.Substring(5), out int c))
                coreId = c;
        }

        if (coreId == -1)
            S.PerClientCores.Remove(row.Character);
        else
            S.PerClientCores[row.Character] = coreId;

        SaveDelayed();
    }

    // ══════════════════════════════════════════════════════════════
    //  CROP
    // ══════════════════════════════════════════════════════════════

    private string? _selectedCropCharacter;

    private void LoadCropPanel()
    {
        _loadingDepth++;
        try
        {
            ChkCropEnabled.IsChecked = S.CropEnabled;

            // Merge live characters + any character already referenced in saved crops
            var all = GetAllCropCharacterNames();

            CmbCropCharacter.ItemsSource = all;
            if (_selectedCropCharacter != null && all.Contains(_selectedCropCharacter, StringComparer.OrdinalIgnoreCase))
                CmbCropCharacter.SelectedItem = _selectedCropCharacter;
            else if (all.Count > 0)
                CmbCropCharacter.SelectedIndex = 0;

            _selectedCropCharacter = CmbCropCharacter.SelectedItem as string;
            RebuildCropList();
        }
        finally { _loadingDepth--; }
    }

    private void OnRefreshCropCharacters(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        LoadCropPanel();
    }

    private void OnCropEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.CropEnabled = ChkCropEnabled.IsChecked == true;
        _svc.Save();
        _cropManager?.Refresh();
    }

    private void OnCropCharacterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _selectedCropCharacter = CmbCropCharacter.SelectedItem as string;
        RebuildCropList();
    }

    private void OnAddCrop(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var ch = _selectedCropCharacter;
        if (string.IsNullOrWhiteSpace(ch))
        {
            MessageBox.Show("Pick a character first.", "Add Crop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!S.Crops.TryGetValue(ch, out var list))
        {
            list = new List<CropDefinition>();
            S.Crops[ch] = list;
        }

        var def = new CropDefinition
        {
            Name = $"Crop {list.Count + 1}",
            PopupX = 100 + (list.Count * 30),
            PopupY = 100 + (list.Count * 30)
        };
        list.Add(def);

        _svc.Save();
        RebuildCropList();
        _cropManager?.Refresh();
    }

    private void RebuildCropList()
    {
        CropListPanel.Children.Clear();
        var ch = _selectedCropCharacter;
        if (string.IsNullOrWhiteSpace(ch) || !S.Crops.TryGetValue(ch, out var list) || list.Count == 0)
        {
            CropEmptyHint.Visibility = Visibility.Visible;
            return;
        }
        CropEmptyHint.Visibility = Visibility.Collapsed;

        foreach (var def in list)
            CropListPanel.Children.Add(BuildCropRow(ch, def));
    }

    private UIElement BuildCropRow(string characterName, CropDefinition def)
    {
        var card = new Border
        {
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(8)
        };
        var root = new StackPanel();

        // Header row: name, label toggle, delete
        var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new System.Windows.Controls.Label { Content = "Name:", Width = 60 });
        var txtName = new TextBox { Width = 180, Text = def.Name };
        txtName.TextChanged += (_, _) =>
        {
            if (_loading) return;
            def.Name = txtName.Text ?? "";
            SaveAndApplyCrop(characterName, def);
        };
        header.Children.Add(txtName);

        var chkLabel = new System.Windows.Controls.CheckBox
        {
            Content = "Show label",
            IsChecked = def.ShowLabel,
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        chkLabel.Checked += (_, _) => { if (_loading) return; def.ShowLabel = true; SaveAndApplyCrop(characterName, def); };
        chkLabel.Unchecked += (_, _) => { if (_loading) return; def.ShowLabel = false; SaveAndApplyCrop(characterName, def); };
        header.Children.Add(chkLabel);

        var btnPick = new Button
        {
            Content = "📐 Pick Area",
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            Background = (Brush)FindResource("AccentBrush"),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            ToolTip = "Drag over the live thumbnail to pick the region of the EVE client to crop."
        };
        btnPick.Click += (_, _) => PickCropArea(characterName, def);
        header.Children.Add(btnPick);

        var btnCopy = new Button
        {
            Content = "📋 Copy to…",
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            Background = (Brush)FindResource("BgPanelBrush"),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            ToolTip = "Copy this crop (region + size + opacity + click-through) to another character — or all of them. The popup lands at the same spot; drag it where you want it."
        };
        btnCopy.Click += (_, _) => ShowCopyCropMenu(btnCopy, characterName, def);
        header.Children.Add(btnCopy);

        var btnDel = new Button
        {
            Content = "🗑 Delete",
            Margin = new Thickness(12, 0, 0, 0),
            Padding = new Thickness(8, 2, 8, 2),
            Background = Brushes.DarkRed,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        btnDel.Click += (_, _) => DeleteCrop(characterName, def);
        header.Children.Add(btnDel);
        root.Children.Add(header);

        // Source rect row
        root.Children.Add(BuildRectRow(
            "Source (on EVE client):", def.SourceX, def.SourceY, def.SourceWidth, def.SourceHeight,
            (x, y, w, h) =>
            {
                def.SourceX = x; def.SourceY = y;
                def.SourceWidth = Math.Max(1, w); def.SourceHeight = Math.Max(1, h);
                SaveAndApplyCrop(characterName, def);
            }));

        // Popup rect row
        root.Children.Add(BuildRectRow(
            "Popup (on screen):", def.PopupX, def.PopupY, def.PopupWidth, def.PopupHeight,
            (x, y, w, h) =>
            {
                def.PopupX = x; def.PopupY = y;
                def.PopupWidth = Math.Max(40, w); def.PopupHeight = Math.Max(30, h);
                SaveAndApplyCrop(characterName, def);
            }));

        // Opacity + click-through row (#62)
        var optRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        optRow.Children.Add(new System.Windows.Controls.Label { Content = "Opacity:", Width = 60, VerticalAlignment = VerticalAlignment.Center });
        int initialOpacity = Math.Clamp(def.Opacity, 10, 100);
        var sldOpacity = new System.Windows.Controls.Slider
        {
            Minimum = 10, Maximum = 100, Width = 160, Value = initialOpacity,
            VerticalAlignment = VerticalAlignment.Center,
            IsSnapToTickEnabled = true, TickFrequency = 5
        };
        var txtOpacity = new TextBlock { Text = $"{initialOpacity}%", Width = 42, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        sldOpacity.ValueChanged += (_, _) =>
        {
            int v = (int)sldOpacity.Value;
            txtOpacity.Text = $"{v}%";
            if (_loading) return;
            def.Opacity = v;
            SaveAndApplyCrop(characterName, def);
        };
        optRow.Children.Add(sldOpacity);
        optRow.Children.Add(txtOpacity);

        var chkClickThrough = new System.Windows.Controls.CheckBox
        {
            Content = "Click-through",
            IsChecked = def.ClickThrough,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Let mouse clicks pass through this crop to whatever is behind it. Disables dragging/resizing the popup until turned off."
        };
        chkClickThrough.Checked += (_, _) => { if (_loading) return; def.ClickThrough = true; SaveAndApplyCrop(characterName, def); };
        chkClickThrough.Unchecked += (_, _) => { if (_loading) return; def.ClickThrough = false; SaveAndApplyCrop(characterName, def); };
        optRow.Children.Add(chkClickThrough);
        root.Children.Add(optRow);

        root.Children.Add(new TextBlock
        {
            Text = "Tip: you can also right-click drag the popup to move it, and right-click + left-click drag to resize.",
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            FontSize = 10,
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        card.Child = root;
        return card;
    }

    private UIElement BuildRectRow(string label, int x, int y, int w, int h, Action<int, int, int, int> onChange)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new System.Windows.Controls.Label { Content = label, Width = 170 });

        var tX = new TextBox { Width = 60, Text = x.ToString() };
        var tY = new TextBox { Width = 60, Text = y.ToString() };
        var tW = new TextBox { Width = 60, Text = w.ToString() };
        var tH = new TextBox { Width = 60, Text = h.ToString() };

        void Push()
        {
            if (_loading) return;
            int.TryParse(tX.Text, out int nx);
            int.TryParse(tY.Text, out int ny);
            int.TryParse(tW.Text, out int nw);
            int.TryParse(tH.Text, out int nh);
            onChange(nx, ny, nw, nh);
        }
        tX.LostFocus += (_, _) => Push();
        tY.LostFocus += (_, _) => Push();
        tW.LostFocus += (_, _) => Push();
        tH.LostFocus += (_, _) => Push();

        row.Children.Add(new System.Windows.Controls.Label { Content = "X" }); row.Children.Add(tX);
        row.Children.Add(new System.Windows.Controls.Label { Content = "Y", Margin = new Thickness(6, 0, 0, 0) }); row.Children.Add(tY);
        row.Children.Add(new System.Windows.Controls.Label { Content = "W", Margin = new Thickness(6, 0, 0, 0) }); row.Children.Add(tW);
        row.Children.Add(new System.Windows.Controls.Label { Content = "H", Margin = new Thickness(6, 0, 0, 0) }); row.Children.Add(tH);
        return row;
    }

    private void DeleteCrop(string characterName, CropDefinition def)
    {
        if (!S.Crops.TryGetValue(characterName, out var list)) return;
        if (MessageBox.Show($"Delete crop '{def.Name}'?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        list.RemoveAll(c => string.Equals(c.Id, def.Id, StringComparison.Ordinal));
        if (list.Count == 0) S.Crops.Remove(characterName);
        _svc.Save();
        RebuildCropList();
        _cropManager?.Refresh();
    }

    private void SaveAndApplyCrop(string characterName, CropDefinition def)
    {
        _svc.Save();
        _cropManager?.ApplyDefinitionEdits(characterName, def.Id);
    }

    /// <summary>Live characters + any character already referenced in saved crops,
    /// de-duped and sorted. Shared by the crop character picker and the per-crop
    /// "Copy to…" menu (issue #82).</summary>
    private List<string> GetAllCropCharacterNames()
    {
        var live = _overviewClients?.GetActiveCharacterNames() ?? Enumerable.Empty<string>();
        return live.Concat(S.Crops.Keys)
                   .Where(n => !string.IsNullOrWhiteSpace(n))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                   .ToList();
    }

    /// <summary>Pop the per-crop "Copy to…" menu: "All other characters" plus each
    /// individual target (issue #82 — copying crops across characters by hand was
    /// tedious).</summary>
    private void ShowCopyCropMenu(System.Windows.Controls.Button anchor, string sourceCharacter, CropDefinition def)
    {
        var targets = GetAllCropCharacterNames()
            .Where(n => !string.Equals(n, sourceCharacter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = anchor };
        if (targets.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "(no other characters)", IsEnabled = false });
        }
        else
        {
            var allItem = new System.Windows.Controls.MenuItem { Header = $"📋 All other characters ({targets.Count})" };
            allItem.Click += (_, _) => CopyCropToTargets(def, targets);
            menu.Items.Add(allItem);
            menu.Items.Add(new System.Windows.Controls.Separator());
            foreach (var t in targets)
            {
                var target = t; // capture per iteration
                var item = new System.Windows.Controls.MenuItem { Header = target };
                item.Click += (_, _) => CopyCropToTargets(def, new List<string> { target });
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    private void CopyCropToTargets(CropDefinition source, IReadOnlyCollection<string> targets)
    {
        foreach (var target in targets)
        {
            if (!S.Crops.TryGetValue(target, out var list))
            {
                list = new List<CropDefinition>();
                S.Crops[target] = list;
            }
            list.Add(CloneCrop(source));
        }

        _svc.Save();
        _cropManager?.Refresh();   // live target characters get the new crop immediately
        RebuildCropList();         // reflect it if the user is viewing a target character

        _overviewClients?.ShowTooltipFeedback(targets.Count == 1
            ? $"Copied crop '{source.Name}' → {targets.First()}"
            : $"Copied crop '{source.Name}' → {targets.Count} characters");
    }

    /// <summary>Deep-copy a crop definition with a fresh unique id. Popup position is
    /// copied verbatim (issue #82 design choice) — the source region is the tedious
    /// part to replicate; the user drags the popup where they want it per character.</summary>
    private static CropDefinition CloneCrop(CropDefinition s) => new CropDefinition
    {
        // Id intentionally left to its fresh generated default so it's a distinct crop.
        Name = s.Name,
        SourceX = s.SourceX, SourceY = s.SourceY, SourceWidth = s.SourceWidth, SourceHeight = s.SourceHeight,
        PopupX = s.PopupX, PopupY = s.PopupY, PopupWidth = s.PopupWidth, PopupHeight = s.PopupHeight,
        ShowLabel = s.ShowLabel, Opacity = s.Opacity, ClickThrough = s.ClickThrough,
    };

    private void PickCropArea(string characterName, CropDefinition def)
    {
        if (_overviewClients == null)
        {
            MessageBox.Show("Thumbnail manager not available.", "Pick Area",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hwnd = _overviewClients.GetHwndForCharacter(characterName);
        if (hwnd == IntPtr.Zero)
        {
            MessageBox.Show(
                $"No live EVE client for '{characterName}'. Launch the EVE client first.",
                "Pick Area", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var (cx, cy, cW, cH) = CropPickerOverlay.QueryClientScreenRect(hwnd);
        if (cW <= 0 || cH <= 0)
        {
            MessageBox.Show("Could not read EVE client area.", "Pick Area",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Hide the Settings window so it doesn't steal focus back over the EVE client.
        // Restored when the picker closes (both success and cancel paths).
        var wasVisible = this.IsVisible;
        this.Hide();

        // Bring the EVE client to the foreground so the user sees exactly what they're cropping.
        // The topmost overlay will sit on top and intercept all mouse input within the client area.
        User32.SetForegroundWindow(hwnd);

        var overlay = new CropPickerOverlay(cx, cy, cW, cH, cW, cH);
        overlay.Completed += result =>
        {
            if (result is { } r)
            {
                def.SourceX = r.X;
                def.SourceY = r.Y;
                def.SourceWidth = r.W;
                def.SourceHeight = r.H;

                // #56: size the popup to match the picked region so it isn't
                // stretched/distorted. Source dims are physical client pixels;
                // popup dims are DIPs, so convert using the EVE client's monitor
                // scale to give the popup the same on-screen size as the region.
                double scale = DpiHelper.GetScaleFactorForPoint(cx, cy);
                def.PopupWidth = Math.Max(40, (int)Math.Round(r.W / scale));
                def.PopupHeight = Math.Max(30, (int)Math.Round(r.H / scale));

                _svc.Save();
                _cropManager?.ApplyDefinitionEdits(characterName, def.Id);
                RebuildCropList();
            }

            if (wasVisible)
            {
                if (!EmbeddedModuleHost.TryActivateShell(this))
                {
                    this.Show();
                    this.Activate();
                }
            }
        };
        overlay.Show();
    }

}

/// <summary>View model row for the Performance Core assignment grid.</summary>

public class PerClientCoreRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Character { get; set; } = "";
    public List<string> AvailableCores { get; set; } = new();

    private string _selectedCore = "";
    public string SelectedCore
    {
        get => _selectedCore;
        set { _selectedCore = value; OnPropertyChanged(nameof(SelectedCore)); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>
/// View model row for the per-character stat grid. Mirrors the character's
/// <see cref="CharacterStatSettings.ForcedOn"/> / <see cref="CharacterStatSettings.ForcedOff"/>
/// bit sets; each bit is tri-state (force-on, force-off, inherit).
/// Editing happens via <see cref="CharacterStatEditorWindow"/>; this row is read-only display.
/// </summary>
public class StatCharacterRow : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";

    private EveCommandCenter.Models.StatMetrics _forcedOn;
    public EveCommandCenter.Models.StatMetrics ForcedOn
    {
        get => _forcedOn;
        set { _forcedOn = value; OnPropertyChanged(nameof(ForcedOn)); OnPropertyChanged(nameof(Summary)); }
    }

    private EveCommandCenter.Models.StatMetrics _forcedOff;
    public EveCommandCenter.Models.StatMetrics ForcedOff
    {
        get => _forcedOff;
        set { _forcedOff = value; OnPropertyChanged(nameof(ForcedOff)); OnPropertyChanged(nameof(Summary)); }
    }

    /// <summary>One-line badge describing this character's overrides,
    /// e.g. "3 ON, 1 OFF (16 inherit)".</summary>
    public string Summary
    {
        get
        {
            int on = System.Numerics.BitOperations.PopCount((uint)_forcedOn);
            int off = System.Numerics.BitOperations.PopCount((uint)_forcedOff);
            if (on == 0 && off == 0) return "All inherit global";
            int total = System.Numerics.BitOperations.PopCount(
                (uint)EveCommandCenter.Models.StatMetrics.AllMetrics) + 1; // +1 for IncludeNpc
            int inherit = total - on - off;
            var parts = new System.Collections.Generic.List<string>();
            if (on > 0)  parts.Add($"{on} ON");
            if (off > 0) parts.Add($"{off} OFF");
            parts.Add($"{inherit} inherit");
            return string.Join(", ", parts);
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>View model row for EVE Manager profile lists (source + target).</summary>
public class ProfileItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public int CharCount { get; set; }

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>View model row for EVE Manager char copy lists (source + target characters).</summary>
public class CharItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string CharName { get; set; } = "";

    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set { _isChecked = value; OnPropertyChanged(nameof(IsChecked)); } }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}


