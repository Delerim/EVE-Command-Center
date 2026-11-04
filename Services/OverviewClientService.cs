using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using EveCommandCenter.Models;
using EveCommandCenter.Views;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
namespace EveCommandCenter.Services;

// Shared client services for the combined overview. This service owns no preview windows.
public sealed class OverviewClientService : IDisposable
{
    private readonly WindowDiscoveryService _discovery;
    private readonly SettingsService _settings;
    private readonly ConcurrentDictionary<IntPtr,EveWindow> _clients = new();
    private readonly ConcurrentDictionary<string,string> _charSystems = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,(string From,string To,DateTime ExpiresUtc)> _overviewSystemTransitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int,double> _latestFpsByPid = new();
    private readonly ConcurrentDictionary<string,AlertBadgeInfo> _alertBadges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string,AlertFlashInfo> _alertFlashChars = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludedFromCycle = new(StringComparer.OrdinalIgnoreCase);
    private readonly AudioSessionService _audio = new();
    private ProcessMonitorService? _processMonitor;
    private FrozenFrameService? _frozenFrames;
    private WinEventHookService? _winEvents;
    private DispatcherTimer? _focusTimer, _minimizeTimer, _tooltipTimer;
    private Window? _tooltipWindow;
    private QuickSwitchWheel? _quickSwitchWheel;
    private IntPtr _lastActiveEveHwnd, _taskbarCoverTop, _lastAffinityActiveHwnd;
    private bool _disposed, _capture, _hidden, _taskbarCoverApplied, _lastManageAffinityState;
    private DateTime _lastAffinityApplyUtc;
    public bool AllClientsMuted { get; private set; }
    public Func<string,bool>? IsMiningAlarmMuted { get; set; }
    internal event Action<IntPtr>? OverviewForegroundChanged;
    public event Action<bool>? AllVisibilityChanged;
    public event Action? CropsVisibilityToggleRequested;
    public event Action? CycleWrapped;
    public OverviewClientService(WindowDiscoveryService discovery, SettingsService settings)
    {
        _discovery=discovery; _settings=settings;
        foreach(var pair in discovery.Windows) _clients[pair.Key]=pair.Value;
        discovery.WindowFound+=OnFound; discovery.WindowLost+=OnLost; discovery.WindowTitleChanged+=OnTitleChanged;
    }
    private static bool CanTrackClient(EveWindow client) => client.Hwnd != IntPtr.Zero && Interop.User32.IsWindow(client.Hwnd);
    private void OnFound(EveWindow client)
    {
        if(!CanTrackClient(client)) return;
        _clients[client.Hwnd]=client;
        Application.Current?.Dispatcher.BeginInvoke(new Action(()=> {
            if(_disposed) return;
            RestoreClientPosition(client.Hwnd,client.CharacterName);
            SetClientVolume(client.CharacterName,GetClientVolume(client.CharacterName));
        }));
    }
    private void OnLost(EveWindow client) { _clients.TryRemove(client.Hwnd,out _); _frozenFrames?.Forget(client.Hwnd); }
    private void OnTitleChanged(EveWindow client,string previous) => OnFound(client);
    public void SetProcessMonitor(ProcessMonitorService monitor) => _processMonitor=monitor;
    internal FrozenFrame? OverviewFrame(IntPtr hwnd) => _frozenFrames?.GetLastFrame(hwnd);
    internal void SetOverviewCapture(bool capture)
    {
        _capture=capture;
        _frozenFrames?.SetPeriodicCaptureEnabled(capture);
    }
    public void StartFocusTracking(WinEventHookService? events=null)
    {
        _winEvents=events;
        _frozenFrames=new FrozenFrameService {MaximumFrameWidth=480};
        _frozenFrames.Start(()=>_capture?_clients.Keys.ToArray():Array.Empty<IntPtr>(),events);
        _frozenFrames.SetPeriodicCaptureEnabled(_capture);
        _focusTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(250)};
        _focusTimer.Tick+=(_,_)=>UpdateFocus(); _focusTimer.Start();
        if(events!=null) events.ForegroundChanged+=ForegroundChanged;
    }
    private int _activeBorderUiPending;
    private void ForegroundChanged(IntPtr hwnd) {
        if(System.Threading.Interlocked.Exchange(ref _activeBorderUiPending,1)!=0) return;
        var dispatcher=Application.Current?.Dispatcher;
        if(dispatcher==null) {System.Threading.Interlocked.Exchange(ref _activeBorderUiPending,0);return;}
        dispatcher.BeginInvoke(new Action(()=> {try {UpdateFocus();} finally {System.Threading.Interlocked.Exchange(ref _activeBorderUiPending,0);}}));
    }
    private void UpdateFocus()
    {
        if(_disposed) return;
        var foreground=Interop.User32.GetForegroundWindow();
        if(_clients.ContainsKey(foreground) && foreground!=_lastActiveEveHwnd) {
            _lastActiveEveHwnd=foreground;
            var character=_clients[foreground].CharacterName;
            ClearAlertFlash(character); ClearAlertBadge(character);
        }
        OverviewForegroundChanged?.Invoke(foreground);
        ApplyClientTaskbarCover(foreground);
        ManageCpuAffinity(foreground,_settings.Settings);
        if(AllClientsMuted) ApplyAllClientMute();
        else if(_settings.Settings.AutoSoloClientAudio) ApplyAudioSolo(foreground);
        _latestFpsByPid.Clear();
        if(_settings.Settings.ShowRtssFps) foreach(var pair in RtssMemoryReader.GetAllFps()) _latestFpsByPid[pair.Key]=pair.Value;
        foreach(var pair in _alertFlashChars)
            if((DateTime.Now-pair.Value.StartTime).TotalSeconds>(pair.Value.Severity=="critical"?8:pair.Value.Severity=="warning"?6:4)) _alertFlashChars.TryRemove(pair.Key,out _);
    }
    public void ActivateEveWindow(IntPtr hwnd,string? title=null,bool rapidSwitch=false,Action? onActivated=null)
    {
        if(hwnd==IntPtr.Zero) hwnd=GetHwndForCharacter(title ?? "");
        if(hwnd==IntPtr.Zero || !Interop.User32.IsWindow(hwnd) || Interop.User32.IsHungAppWindow(hwnd)) return;
        if(Interop.User32.IsIconic(hwnd)) Interop.User32.ShowWindowAsync(hwnd,_settings.Settings.AlwaysMaximize?Interop.User32.SW_MAXIMIZE:Interop.User32.SW_RESTORE);
        if(!_settings.Settings.TrackClientPositions) ApplyFixedClientPosition(hwnd);
        Interop.User32.ActivateWindow(hwnd);
        if(Interop.User32.GetForegroundWindow()!=hwnd) return;
        _lastActiveEveHwnd=hwnd;
        var name=_clients.GetValueOrDefault(hwnd)?.CharacterName ?? title ?? "";
        ClearAlertFlash(name); ClearAlertBadge(name);
        ApplyClientTaskbarCover(hwnd);
        if(_settings.Settings.AlwaysMaximize && !Interop.User32.IsZoomed(hwnd)) Interop.User32.ShowWindowAsync(hwnd,Interop.User32.SW_MAXIMIZE);
        if(_settings.Settings.MinimizeInactiveClients) {
            _minimizeTimer?.Stop();
            var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(Math.Max(1,_settings.Settings.MinimizeDelay))};
            _minimizeTimer=timer;
            timer.Tick+=(_,_)=> {timer.Stop(); if(Interop.User32.GetForegroundWindow()!=hwnd) return;
                foreach(var other in _clients.Where(p=>p.Key!=hwnd && !_settings.CurrentProfile.DontMinimizeClients.Contains(p.Value.CharacterName)).Select(p=>p.Key)) Interop.User32.ShowWindowAsync(other,Interop.User32.SW_MINIMIZE);};
            timer.Start();
        }
        onActivated?.Invoke(); OverviewForegroundChanged?.Invoke(hwnd);
    }
    public IEnumerable<string> GetActiveCharacterNames() => _clients.Values.Select(c=>c.CharacterName).Where(n=>!string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase);
    public bool HasTrackedClients() => !_clients.IsEmpty;
    public IntPtr GetHwndForCharacter(string name) => _clients.Values.FirstOrDefault(c=>string.Equals(c.CharacterName,name,StringComparison.OrdinalIgnoreCase))?.Hwnd ?? IntPtr.Zero;
    public void UpdateCharacterSystem(string name,string system) {
        if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(system)) return;
        if(_charSystems.TryGetValue(name,out var previous)&&previous!=system) _overviewSystemTransitions[name]=(previous,system,DateTime.UtcNow.AddSeconds(15));
        _charSystems[name]=system;
    }
    public bool IsCharacterAlertMuted(string name) => IsMiningAlarmMuted?.Invoke(name)==true;
    public void SetAlertFlash(string name,string severity="critical",string eventType="attack") {
        if(!IsCharacterAlertMuted(name)) _alertFlashChars[name]=new() {StartTime=DateTime.Now,Severity=severity,EventType=eventType};
    }
    public void ClearAlertFlash(string name) => _alertFlashChars.TryRemove(name,out _);
    public void IncrementAlertBadge(string name,string severity,string alertType="") {
        if(IsCharacterAlertMuted(name) || string.IsNullOrWhiteSpace(name) || !_settings.Settings.ShowAlertBadgeOnThumbnails ||
            (!string.IsNullOrEmpty(alertType) && _settings.Settings.BadgeOnThumbnailAlertTypes.TryGetValue(alertType,out var enabled) && !enabled) ||
            GetHwndForCharacter(name)==Interop.User32.GetForegroundWindow()) return;
        _alertBadges.AddOrUpdate(name,_=>new(){Count=1,TopSeverity=severity},(_,old)=>new(){Count=old.Count+1,TopSeverity=Rank(old.TopSeverity)>Rank(severity)?old.TopSeverity:severity});
    }
    private static int Rank(string severity) => severity=="critical"?3:severity=="warning"?2:1;
    public void ClearAlertBadge(string name) => _alertBadges.TryRemove(name,out _);
    public int GetClientVolume(string name) => _settings.Settings.PerClientAudioVolume.GetValueOrDefault(name,100);
    public void SetClientVolume(string name,int percent) {
        if(string.IsNullOrEmpty(name)) return;
        percent=Math.Clamp(percent,0,100);
        if(percent==100) _settings.Settings.PerClientAudioVolume.Remove(name); else _settings.Settings.PerClientAudioVolume[name]=percent;
        _settings.SaveDelayed();
        Interop.User32.GetWindowThreadProcessId(GetHwndForCharacter(name),out uint pid);
        if(pid!=0) _audio.SetVolume(pid,percent/100f,unmute:!AllClientsMuted);
    }
    public void ReapplySettings() { UpdateFocus(); if(!_settings.Settings.AutoSoloClientAudio&&!AllClientsMuted) UnmuteAllClientAudio(); }
    public void ToggleAllVisibility() { _hidden=!_hidden; AllVisibilityChanged?.Invoke(_hidden); }
    public void ToggleOverviewVisibility() => ToggleAllVisibility();
    public void ToggleCropsVisibility() => CropsVisibilityToggleRequested?.Invoke();
    public void CycleGroup(string groupName,List<string> members,bool forward) {
        var names=members.Where(n=>GetHwndForCharacter(n) is var hwnd && hwnd!=IntPtr.Zero && Interop.User32.IsWindow(hwnd) && !Interop.User32.IsHungAppWindow(hwnd) && !_excludedFromCycle.Contains(n)).ToList();
        if(names.Count==0) return;
        int current=names.FindIndex(n=>GetHwndForCharacter(n)==_lastActiveEveHwnd);
        int next=current<0?(forward?0:names.Count-1):(current+(forward?1:-1)+names.Count)%names.Count;
        if(current>=0&&names.Count>1&&((forward&&next==0)||(!forward&&next==names.Count-1))) CycleWrapped?.Invoke();
        ActivateEveWindow(GetHwndForCharacter(names[next]),names[next],true);
    }
    public void CycleAll(bool forward)
    {
        // Cadence governed upstream by HotkeyService (issues #59/#60) — no
        // CycleDelayMs throttle here, which would block deliberate fast taps.
        bool includeLogins = _settings.Settings.IncludeLoginScreensInCycle;
        var hwnds = _clients
            .OrderBy(kv => kv.Key.ToInt64())
            .Where(kv =>
            {
                if (!Interop.User32.IsWindow(kv.Key) || Interop.User32.IsHungAppWindow(kv.Key)) return false;
                var name = kv.Value.CharacterName;
                if (_excludedFromCycle.Contains(name ?? "")) return false;
                bool isLogin = string.IsNullOrEmpty(name);
                return isLogin ? includeLogins : true;
            })
            .Select(kv => kv.Key)
            .ToList();

        if (hwnds.Count == 0) return;

        var fgHwnd = _lastActiveEveHwnd != IntPtr.Zero && _clients.ContainsKey(_lastActiveEveHwnd)
                     ? _lastActiveEveHwnd
                     : Interop.User32.GetForegroundWindow();

        int currentIdx = hwnds.IndexOf(fgHwnd);
        int nextIdx = forward
            ? (currentIdx + 1) % hwnds.Count
            : (currentIdx - 1 + hwnds.Count) % hwnds.Count;

        bool wrapped = forward
            ? currentIdx > 0 && nextIdx == 0 && hwnds.Count > 1
            : currentIdx >= 0 && currentIdx < hwnds.Count - 1
                && nextIdx == hwnds.Count - 1 && hwnds.Count > 1;
        if (wrapped) CycleWrapped?.Invoke();

        var targetHwnd = hwnds[nextIdx];
        _lastActiveEveHwnd = targetHwnd;

        string targetTitle = _clients.TryGetValue(targetHwnd, out var tThumb) ? tThumb.CharacterName : "";
        ActivateEveWindow(targetHwnd, targetTitle, rapidSwitch: true);
    }


    public void CycleCharSelect(bool forward) {
        var clients=_clients.Values.Where(c=>string.IsNullOrWhiteSpace(c.CharacterName)||c.CharacterName=="EVE").OrderBy(c=>c.Hwnd.ToInt64()).ToArray();
        if(clients.Length==0) return;
        int i=Array.FindIndex(clients,c=>c.Hwnd==_lastActiveEveHwnd);
        ActivateEveWindow(clients[(i+(forward?1:-1)+clients.Length)%clients.Length].Hwnd);
    }
    public bool IsExcludedFromCycle(string name) => _excludedFromCycle.Contains(name);
    public void ToggleCycleExclusion(string name) {if(!_excludedFromCycle.Add(name)) _excludedFromCycle.Remove(name);}
    public void CloseAllEveWindows() {foreach(var hwnd in _clients.Keys) Interop.User32.PostMessage(hwnd,0x0010,IntPtr.Zero,IntPtr.Zero);}
    public void Dispose() {
        if(_disposed) return;
        AllClientsMuted=false; UnmuteAllClientAudio(); if(_lastManageAffinityState) ResetAllEveAffinity();
        if(_taskbarCoverTop!=IntPtr.Zero) SetTaskbarCoverBand(_taskbarCoverTop,false);
        _disposed=true; _focusTimer?.Stop(); _minimizeTimer?.Stop(); _tooltipTimer?.Stop();
        _tooltipWindow?.Close(); _quickSwitchWheel?.ForceClose(); _frozenFrames?.Dispose();
        if(_winEvents!=null) _winEvents.ForegroundChanged-=ForegroundChanged;
        _discovery.WindowFound-=OnFound; _discovery.WindowLost-=OnLost; _discovery.WindowTitleChanged-=OnTitleChanged;
    }
    private HashSet<uint> CollectClientPids(IntPtr hwndToMatch, out uint pidForHwnd)
    {
        var pids = new HashSet<uint>();
        pidForHwnd = 0;
        foreach (var (hwnd, _) in _clients)
        {
            Interop.User32.GetWindowThreadProcessId(hwnd, out uint p);
            if (p == 0) continue;
            pids.Add(p);
            if (hwnd == hwndToMatch) pidForHwnd = p;
        }
        return pids;
    }
    public void ToggleAllClientAudio()
    {
        if (_disposed) return;
        AllClientsMuted = !AllClientsMuted;
        if (AllClientsMuted) ApplyAllClientMute();
        else {
            UnmuteAllClientAudio();
            if (_settings.Settings.AutoSoloClientAudio) ApplyAudioSolo(Interop.User32.GetForegroundWindow());
        }
    }
    private void ApplyAllClientMute()
    {
        if (_disposed) return;
        var pids = CollectClientPids(IntPtr.Zero, out _);
        if (pids.Count > 0) _audio.MuteAll(pids);
    }
    private void ApplyAudioSolo(IntPtr activeHwnd)
    {
        if (AllClientsMuted) { ApplyAllClientMute(); return; }
        var pids = CollectClientPids(activeHwnd, out uint activePid);
        if (activePid != 0 && pids.Count > 0) _audio.ApplySolo(activePid, pids);
    }
    public void UnmuteAllClientAudio()
    {
        if (AllClientsMuted) return;
        var pids = CollectClientPids(IntPtr.Zero, out _);
        if (pids.Count > 0) _audio.UnmuteAll(pids);
    }
    public void SaveClientPositions()
    {
        foreach (var (eveHwnd, thumb) in _clients)
        {
            if (string.IsNullOrEmpty(thumb.CharacterName)) continue;

            try
            {
                Interop.User32.GetWindowRect(eveHwnd, out var rect);
                bool isMaximized = Interop.User32.IsZoomed(eveHwnd);

                var pos = new ClientPosition
                {
                    X = rect.Left,
                    Y = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height,
                    IsMaximized = isMaximized ? 1 : 0
                };

                _settings.CurrentProfile.ClientPositions[thumb.CharacterName] = pos;
            }
            catch { }
        }
        _settings.Save();
    }
    private void RestoreClientPosition(IntPtr hwnd, string characterName)
    {
        if (string.IsNullOrEmpty(characterName)) return;
        if (!_settings.CurrentProfile.ClientPositions.TryGetValue(characterName, out var pos)) return;

        try
        {
            if (pos.IsMaximized == 1)
            {
                Interop.User32.ShowWindowAsync(hwnd, Interop.User32.SW_MAXIMIZE);
            }
            else
            {
                Interop.User32.SetWindowPos(hwnd, IntPtr.Zero, (int)pos.X, (int)pos.Y, (int)pos.Width, (int)pos.Height,
                    Interop.User32.SWP_NOZORDER | Interop.User32.SWP_NOACTIVATE | Interop.User32.SWP_ASYNCWINDOWPOS);
            }
        }
        catch { }
    }
    private void ApplyFixedClientPosition(IntPtr hwnd)
    {
        int mode = _settings.Settings.ClientPositionMode; // 0=Off, 1=Center, 2=Custom
        if (mode == 0) return;
        if (Interop.User32.IsIconic(hwnd) || Interop.User32.IsZoomed(hwnd)) return;
        if (!Interop.User32.GetWindowRect(hwnd, out var rect)) return;

        int w = rect.Width, h = rect.Height;
        if (w <= 0 || h <= 0) return;

        int tx, ty;
        if (mode == 1)
        {
            // Centre within the WORK AREA, not Screen.Bounds (#96). Bounds includes the
            // taskbar strip, so centring in it pushes the window roughly half the taskbar
            // height too low — a windowed client then extends underneath the taskbar,
            // which is topmost and draws over it. WorkingArea excludes the taskbar (on
            // whichever edge it lives), so the client lands fully visible.
            // GetWindowRect + Screen bounds + SetWindowPos are all physical device
            // pixels, so no DPI conversion is needed here.
            // "Cover taskbar" mode (#99) deliberately uses the FULL screen: EVE's fixed
            // window list offers the monitor's exact resolution, and such a window is
            // taller than the work area — centring it there would push it down so the
            // taskbar clips the bottom. Screen bounds centres it flush with the display.
            var scr = System.Windows.Forms.Screen.FromHandle(hwnd);
            var wa = _settings.Settings.ClientCoverTaskbar ? scr.Bounds : scr.WorkingArea;
            tx = wa.Left + (wa.Width - w) / 2;
            ty = wa.Top + (wa.Height - h) / 2;

            // A window taller/wider than the area would still centre to a negative
            // offset and slide under the taskbar; pin it to the area origin instead.
            if (w > wa.Width) tx = wa.Left;
            if (h > wa.Height) ty = wa.Top;
        }
        else
        {
            tx = _settings.Settings.ClientPositionX;
            ty = _settings.Settings.ClientPositionY;
        }

        // Already there? Don't spend a cross-process SetWindowPos on it. This runs on
        // every client switch, and after the first switch the client is normally already
        // in position, so this skips the call entirely most of the time (#100 delay).
        if (rect.Left == tx && rect.Top == ty) return;

        try
        {
            Interop.User32.SetWindowPos(hwnd, IntPtr.Zero, tx, ty, 0, 0,
                Interop.User32.SWP_NOSIZE | Interop.User32.SWP_NOZORDER |
                Interop.User32.SWP_NOACTIVATE | Interop.User32.SWP_ASYNCWINDOWPOS);
        }
        catch { }
    }
    private void ApplyClientTaskbarCover(IntPtr fgHwnd)
    {
        bool enabled = _settings.Settings.ClientCoverTaskbar;
        if (!enabled && !_taskbarCoverApplied) return;   // nothing to do, nothing to undo

        if (!enabled)
        {
            // Option switched off: sweep every client out of the band once, or one
            // would stay stuck over the taskbar with nothing left to put it back.
            foreach (var (eveHwnd, _) in _clients) SetTaskbarCoverBand(eveHwnd, false);
            _taskbarCoverApplied = false;
            _taskbarCoverTop = IntPtr.Zero;
            return;
        }

        if (_taskbarCoverTop == fgHwnd) return;   // already the promoted client

        // Demote the outgoing client BEFORE promoting the new one. Doing this only
        // from the focus poll was the #100 regression: the poll ignores the 500ms
        // after a cycle, so hotkey-switching left the previous client topmost and it
        // stayed visible over the client you just switched to.
        if (_taskbarCoverTop != IntPtr.Zero) SetTaskbarCoverBand(_taskbarCoverTop, false);
        _taskbarCoverTop = IntPtr.Zero;

        // Foreground may be our own app or a non-EVE window — then nothing is promoted
        // and the taskbar is reachable again, which is the intended blur behaviour.
        if (fgHwnd != IntPtr.Zero && _clients.ContainsKey(fgHwnd))
        {
            SetTaskbarCoverBand(fgHwnd, true);
            _taskbarCoverTop = fgHwnd;
        }

        _taskbarCoverApplied = true;

        // Keep thumbnails above the promoted client — but off this path, so switching
        // stays O(1) no matter how many clients are open (#100).
    }
    private static void SetTaskbarCoverBand(IntPtr hwnd, bool topmost)
    {
        try
        {
            Interop.User32.SetWindowPos(hwnd,
                topmost ? Interop.User32.HWND_TOPMOST : Interop.User32.HWND_NOTOPMOST,
                0, 0, 0, 0,
                Interop.User32.SWP_NOMOVE | Interop.User32.SWP_NOSIZE |
                Interop.User32.SWP_NOACTIVATE | Interop.User32.SWP_ASYNCWINDOWPOS);
        }
        catch { }
    }
    public OverviewPreviewTelemetry GetOverviewTelemetry(string characterName)
    {
        string systemName =
            _charSystems.TryGetValue(characterName ?? "", out var system)
                ? system
                : "";

        IntPtr hwnd = GetHwndForCharacter(characterName ?? "");
        int pid = 0;
        if (hwnd != IntPtr.Zero)
        {
            Interop.User32.GetWindowThreadProcessId(hwnd, out uint rawPid);
            pid = (int)rawPid;
        }

        string processText = "";
        if (_settings.Settings.ShowProcessStats &&
            pid > 0 &&
            _processMonitor?.GetStats(pid) is { } stats)
        {
            processText =
                $"CPU {stats.CpuPercent:F0}%  RAM {stats.RamMB}MB" +
                (stats.VramMB > 0 ? $"  VRAM {stats.VramMB}MB" : "");
        }

        string fpsText = "";
        if (_settings.Settings.ShowRtssFps &&
            pid > 0 &&
            _latestFpsByPid.TryGetValue(pid, out double fps) &&
            fps > 0)
        {
            fpsText = $"{fps:F1} fps";
        }

        _alertBadges.TryGetValue(characterName ?? "", out var badge);
        _alertFlashChars.TryGetValue(characterName ?? "", out var flash);

        string severity =
            flash?.Severity ??
            badge?.TopSeverity ??
            "";

        string transitionText = "";
        bool transitionActive = false;

        if (_overviewSystemTransitions.TryGetValue(
                characterName ?? "",
                out var transition))
        {
            if (transition.ExpiresUtc > DateTime.UtcNow)
            {
                transitionActive = true;
                transitionText =
                    $"{transition.From} -> {transition.To}";
            }
            else
            {
                _overviewSystemTransitions.TryRemove(
                    characterName ?? "",
                    out _);
            }
        }

        return new OverviewPreviewTelemetry(
            systemName,
            processText,
            fpsText,
            badge?.Count ?? 0,
            severity,
            flash != null,
            transitionText,
            transitionActive);
    }
    public void ShowTooltipFeedback(string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                // Close any existing tooltip
                _tooltipWindow?.Close();
                _tooltipTimer?.Stop();

                // Get cursor position (physical pixels) and convert to DIPs for WPF Left/Top
                var cursorPos = System.Windows.Forms.Cursor.Position;
                double dpi = Interop.DpiHelper.GetScaleFactorForPoint(cursorPos.X, cursorPos.Y);

                // Create a small borderless tooltip window near the cursor (AHK ToolTip style)
                _tooltipWindow = new System.Windows.Window
                {
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromArgb(230, 40, 40, 40)),
                    Topmost = true,
                    ShowInTaskbar = false,
                    SizeToContent = SizeToContent.WidthAndHeight,
                    Left = Interop.DpiHelper.PhysicalToDip(cursorPos.X, dpi) + 16,
                    Top = Interop.DpiHelper.PhysicalToDip(cursorPos.Y, dpi) + 16,
                    Content = new System.Windows.Controls.TextBlock
                    {
                        Text = message,
                        Foreground = System.Windows.Media.Brushes.White,
                        FontSize = 12,
                        Margin = new Thickness(8, 4, 8, 4)
                    }
                };
                _tooltipWindow.Show();

                // Auto-dismiss after 1500ms (AHK: SetTimer(() => ToolTip(), -1500))
                _tooltipTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                _tooltipTimer.Tick += (_, _) =>
                {
                    _tooltipTimer.Stop();
                    _tooltipWindow?.Close();
                    _tooltipWindow = null;
                };
                _tooltipTimer.Start();
            }
            catch
            {
                Debug.WriteLine($"[Tooltip] {message}");
            }
        });
    }
    public void ShowSuspendTooltip(bool isSuspended)
    {
        ShowTooltipFeedback(isSuspended ? "Hotkeys: SUSPENDED" : "Hotkeys: ACTIVE");
    }
    public void ShowQuickSwitch()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // Toggle: close if already visible
                if (_quickSwitchWheel != null && _quickSwitchWheel.IsVisible)
                {
                    _quickSwitchWheel.ForceClose();
                    return;
                }

                var characters = _clients.Values
                    .Where(t => !string.IsNullOrEmpty(t.CharacterName) && t.CharacterName != "EVE")
                    .Select(t => t.CharacterName)
                    .Distinct()
                    .ToList();

                if (characters.Count == 0) return;

                // Apply saved order — saved chars first, then any new chars at end
                var savedOrder = _settings.Settings.QuickSwitchCardOrder;
                var ordered = savedOrder
                    .Where(n => characters.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                var unsaved = characters
                    .Where(n => !savedOrder.Contains(n, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(n => n)
                    .ToList();
                ordered.AddRange(unsaved);

                // Recreate wheel each time
                if (_quickSwitchWheel != null)
                {
                    try { _quickSwitchWheel.Close(); } catch { }
                }

                _quickSwitchWheel = new Views.QuickSwitchWheel();
                _quickSwitchWheel.CharacterSelected += (charName) =>
                {
                    ActivateEveWindow(IntPtr.Zero, charName);
                };
                _quickSwitchWheel.OrderSaved += (newOrder) =>
                {
                    _settings.Settings.QuickSwitchCardOrder = newOrder;
                    _settings.Save();
                    Debug.WriteLine($"[QuickSwitch] 💾 Card order saved: {string.Join(", ", newOrder)}");
                };

                _quickSwitchWheel.ShowWheel(ordered);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[QuickSwitch] ❌ Error: {ex.Message}");
                Debug.WriteLine(ex.ToString());
                _quickSwitchWheel = null;
            }
        });
    }
    public static Color ParseColor(string hex)
    {
        try
        {
            if (string.IsNullOrEmpty(hex)) return Color.FromRgb(80, 80, 80);

            hex = hex.TrimStart('#');
            // Handle AHK "0x" prefix format
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex[2..];

            if (hex.Length == 6)
            {
                byte r = Convert.ToByte(hex[..2], 16);
                byte g = Convert.ToByte(hex[2..4], 16);
                byte b = Convert.ToByte(hex[4..6], 16);
                return Color.FromRgb(r, g, b);
            }
        }
        catch { }
        return Color.FromRgb(80, 80, 80);
    }
    private void ManageCpuAffinity(IntPtr activeHwnd, AppSettings s)
    {
        // Detect on->off transition and restore every tracked EVE process once.
        if (!s.ManageAffinity)
        {
            if (_lastManageAffinityState)
            {
                ResetAllEveAffinity();
                _lastManageAffinityState = false;
                _lastAffinityActiveHwnd = IntPtr.Zero;
                _lastAffinityApplyUtc = DateTime.MinValue;
            }

            return;
        }

        DateTime now =
            DateTime.UtcNow;

        // Focus changes must be applied immediately, but the safety sweep runs
        // four times per second. Reopening every EVE Process and re-reading its
        // priority/affinity on every sweep is unnecessary long-session churn.
        if (_lastManageAffinityState &&
            activeHwnd == _lastAffinityActiveHwnd &&
            now - _lastAffinityApplyUtc < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastManageAffinityState = true;
        _lastAffinityActiveHwnd = activeHwnd;
        _lastAffinityApplyUtc = now;

        int totalCores =
            Math.Clamp(
                Environment.ProcessorCount,
                1,
                IntPtr.Size * 8);

        long allCoresMask =
            FullAffinityMask(totalCores);

        var activeEves =
            new List<(Process Proc, string CharName, bool IsActive)>();

        foreach (var (hwnd, thumb) in _clients)
        {
            try
            {
                Interop.User32.GetWindowThreadProcessId(thumb.Hwnd, out uint rawPid);
                int pid=(int)rawPid;

                if (pid > 0)
                {
                    Process process =
                        Process.GetProcessById(pid);

                    activeEves.Add(
                        (
                            process,
                            thumb.CharacterName,
                            hwnd == activeHwnd
                        ));
                }
            }
            catch
            {
                // Process might have just exited.
            }
        }

        int backgroundSlot = 0;

        foreach (var eve in activeEves)
        {
            try
            {
                if (eve.IsActive)
                {
                    if (eve.Proc.PriorityClass !=
                        ProcessPriorityClass.Normal)
                    {
                        eve.Proc.PriorityClass =
                            ProcessPriorityClass.Normal;
                    }

                    if ((long)eve.Proc.ProcessorAffinity !=
                        allCoresMask)
                    {
                        eve.Proc.ProcessorAffinity =
                            (IntPtr)allCoresMask;
                    }

                    continue;
                }

                // Idle priority plus one logical CPU is too aggressive for a
                // live EVE client that still has rendering/network work to do.
                // BelowNormal keeps background clients polite without starving
                // them until they are selected again.
                if (eve.Proc.PriorityClass !=
                    ProcessPriorityClass.BelowNormal)
                {
                    eve.Proc.PriorityClass =
                        ProcessPriorityClass.BelowNormal;
                }

                long affinityMask = 0;

                if (s.PerClientCores.TryGetValue(
                        eve.CharName,
                        out int overrideCore) &&
                    overrideCore >= 0 &&
                    overrideCore < totalCores)
                {
                    // Explicit single-core choices remain intentional.
                    affinityMask =
                        1L << overrideCore;
                }
                else if (s.AutoBalanceCores)
                {
                    // Do not guess Intel/AMD P-core/E-core topology from logical
                    // CPU numbering. Rotate background clients through pairs of
                    // logical processors so no live client is pinned to one CPU.
                    affinityMask =
                        BackgroundAffinityMask(
                            totalCores,
                            backgroundSlot++);
                }

                if (affinityMask == 0)
                    affinityMask = allCoresMask;

                if ((long)eve.Proc.ProcessorAffinity !=
                    affinityMask)
                {
                    eve.Proc.ProcessorAffinity =
                        (IntPtr)affinityMask;
                }
            }
            catch
            {
                // Ignore access denied or exited processes.
            }
            finally
            {
                eve.Proc.Dispose();
            }
        }
    }
    private static long FullAffinityMask(int totalCores)
    {
        int bits =
            Math.Clamp(
                totalCores,
                1,
                IntPtr.Size * 8);

        return bits >= 64
            ? -1L
            : (1L << bits) - 1;
    }
    private static long BackgroundAffinityMask(
        int totalCores,
        int slot)
    {
        int bits =
            Math.Clamp(
                totalCores,
                1,
                IntPtr.Size * 8);

        if (bits == 1)
            return 1;

        int start =
            bits / 2;

        int pool =
            Math.Max(
                1,
                bits - start);

        int first =
            start +
            Math.Abs(slot % pool);

        int second =
            start +
            Math.Abs((slot + 1) % pool);

        if (second == first)
            second =
                (first + 1) % bits;

        return
            (1L << first) |
            (1L << second);
    }
    private void ResetAllEveAffinity()
    {
        int totalCores =
            Math.Clamp(
                Environment.ProcessorCount,
                1,
                IntPtr.Size * 8);

        long allCoresMask =
            FullAffinityMask(totalCores);

        foreach (var (_, thumb) in _clients)
        {
            int pid;

            try
            {
                Interop.User32.GetWindowThreadProcessId(thumb.Hwnd, out uint rawPid);
                pid=(int)rawPid;
            }
            catch
            {
                continue;
            }

            if (pid <= 0)
                continue;

            try
            {
                using var process =
                    Process.GetProcessById(pid);

                if (process.PriorityClass !=
                    ProcessPriorityClass.Normal)
                {
                    process.PriorityClass =
                        ProcessPriorityClass.Normal;
                }

                if ((long)process.ProcessorAffinity !=
                    allCoresMask)
                {
                    process.ProcessorAffinity =
                        (IntPtr)allCoresMask;
                }

                Debug.WriteLine(
                    $"[Affinity:Reset] '{thumb.CharacterName}' restored to Normal + all cores");
            }
            catch
            {
                // Process exited or access was denied.
            }
        }
    }
}
public class AlertFlashInfo
{
    public DateTime StartTime { get; set; }
    public string Severity { get; set; } = "critical";
    public string EventType { get; set; } = "attack";
    public bool ShowFlash { get; set; } = true;
    public DateTime LastToggleTime { get; set; }
}

/// <summary>Per-character unread-alert badge state — count of alerts fired
/// since the user last activated this character, plus the strongest severity
/// seen so the rendered badge can pick its colour. Cleared on activation.</summary>
public class AlertBadgeInfo
{
    public int Count { get; set; }
    public string TopSeverity { get; set; } = "info";
}

public readonly record struct OverviewPreviewTelemetry(
    string SystemName,
    string ProcessText,
    string FpsText,
    int AlertCount,
    string AlertSeverity,
    bool AlertActive,
    string SystemTransitionText,
    bool SystemTransitionActive);
