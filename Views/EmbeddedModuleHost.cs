using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;

namespace EveCommandCenter.Views;

/// <summary>
/// Hosts mature Window-based tools inside Command Center without ever showing
/// their backing top-level Window. The hidden Window still owns the existing
/// code-behind/lifecycle, while its real visual content lives in the shell.
/// </summary>
internal sealed class EmbeddedModuleHost : IDisposable
{
    private readonly CommandCenterWindow _shell;
    private readonly ContentControl _surface;
    private readonly Dictionary<string, ModuleState> _modules =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _activeKey;
    private bool _disposing;

    internal event Action<string>? ModuleClosed;

    internal EmbeddedModuleHost(
        CommandCenterWindow shell,
        ContentControl surface)
    {
        _shell = shell;
        _surface = surface;

        _surface.SizeChanged +=
            (_, _) =>
                SyncBackingWindowSize();
    }

    internal Window Show(
        string key,
        Func<Window> factory)
    {
        if (!_modules.TryGetValue(
                key,
                out ModuleState? state))
        {
            state =
                Create(
                    key,
                    factory());

            _modules[key] =
                state;
        }

        _activeKey =
            key;

        _surface.Content =
            state.Content;

        EnsureWindowLoaded(
            state);

        SyncBackingWindowSize();

        return state.Window;
    }

    internal bool Contains(
        string key) =>
        _modules.ContainsKey(key);

    internal Window? GetWindow(
        string key) =>
        _modules.TryGetValue(
            key,
            out ModuleState? state)
                ? state.Window
                : null;

    internal string? KeyFor(
        Window window) =>
        _modules
            .FirstOrDefault(pair =>
                ReferenceEquals(
                    pair.Value.Window,
                    window))
            .Key;

    internal bool Activate(
        Window window)
    {
        string? key =
            KeyFor(window);

        if (string.IsNullOrWhiteSpace(key))
            return false;

        _shell.OpenModule(key);
        return true;
    }

    internal void Close(
        string key)
    {
        if (!_modules.TryGetValue(
                key,
                out ModuleState? state))
            return;

        if (ReferenceEquals(
                _surface.Content,
                state.Content))
        {
            _surface.Content =
                null;
        }

        state.Window.Close();
    }

    private ModuleState Create(
        string key,
        Window window)
    {
        window.Owner =
            _shell;

        // The backing Window exists only for code-behind and lifecycle state.
        // Creating its HWND is enough for SourceInitialized/interop consumers;
        // unlike Window.Show(), EnsureHandle never makes it visible.
        window.ShowInTaskbar =
            false;

        window.ShowActivated =
            false;

        window.WindowStartupLocation =
            WindowStartupLocation.Manual;

        window.WindowState =
            WindowState.Normal;

        window.WindowStyle =
            WindowStyle.None;

        window.ResizeMode =
            ResizeMode.NoResize;

        window.Left =
            -30000;

        window.Top =
            -30000;

        window.Opacity =
            0;

        window.Width =
            Math.Max(
                1100,
                _surface.ActualWidth);

        window.Height =
            Math.Max(
                720,
                _surface.ActualHeight);

        IntPtr hwnd =
            new System.Windows.Interop.WindowInteropHelper(
                window)
                .EnsureHandle();

        int exStyle =
            EveCommandCenter.Interop.User32.GetWindowLong(
                hwnd,
                EveCommandCenter.Interop.User32.GWL_EXSTYLE);

        EveCommandCenter.Interop.User32.SetWindowLong(
            hwnd,
            EveCommandCenter.Interop.User32.GWL_EXSTYLE,
            exStyle |
            EveCommandCenter.Interop.User32.WS_EX_TOOLWINDOW |
            EveCommandCenter.Interop.User32.WS_EX_NOACTIVATE);

        EveCommandCenter.Interop.User32.SetWindowPos(
            hwnd,
            EveCommandCenter.Interop.User32.HWND_NOTOPMOST,
            -30000,
            -30000,
            Math.Max(
                1100,
                (int)Math.Ceiling(window.Width)),
            Math.Max(
                720,
                (int)Math.Ceiling(window.Height)),
            EveCommandCenter.Interop.User32.SWP_NOACTIVATE |
            EveCommandCenter.Interop.User32.SWP_NOZORDER);

        EveCommandCenter.Interop.User32.ShowWindow(
            hwnd,
            EveCommandCenter.Interop.User32.SW_HIDE);

        if (window.Content is not
            FrameworkElement content)
        {
            window.Close();

            throw new InvalidOperationException(
                "Embedded module did not expose WPF content.");
        }

        object? inheritedDataContext =
            content.DataContext;

        foreach (DictionaryEntry entry in
                 window.Resources)
        {
            if (!content.Resources.Contains(
                    entry.Key))
            {
                content.Resources[entry.Key] =
                    entry.Value;
            }
        }

        while (window.Resources.MergedDictionaries.Count > 0)
        {
            ResourceDictionary dictionary =
                window.Resources.MergedDictionaries[0];

            window.Resources.MergedDictionaries.RemoveAt(0);

            content.Resources.MergedDictionaries.Add(
                dictionary);
        }

        if (content.ReadLocalValue(
                FrameworkElement.DataContextProperty) ==
            DependencyProperty.UnsetValue &&
            inheritedDataContext != null)
        {
            content.DataContext =
                inheritedDataContext;
        }

        content.SetValue(
            TextElement.FontFamilyProperty,
            window.FontFamily);

        content.SetValue(
            TextElement.FontSizeProperty,
            window.FontSize);

        content.SetValue(
            TextElement.ForegroundProperty,
            window.Foreground);

        if (window.FindName("EmbeddedTitleBar") is
            FrameworkElement embeddedTitleBar)
        {
            embeddedTitleBar.Visibility =
                Visibility.Collapsed;
        }

        foreach (System.Windows.Input.CommandBinding binding in
                 window.CommandBindings
                     .Cast<System.Windows.Input.CommandBinding>()
                     .ToArray())
        {
            window.CommandBindings.Remove(
                binding);

            content.CommandBindings.Add(
                binding);
        }

        foreach (System.Windows.Input.InputBinding binding in
                 window.InputBindings
                     .Cast<System.Windows.Input.InputBinding>()
                     .ToArray())
        {
            window.InputBindings.Remove(
                binding);

            content.InputBindings.Add(
                binding);
        }

        window.Content =
            null;

        content.Width =
            double.NaN;

        content.Height =
            double.NaN;

        content.HorizontalAlignment =
            System.Windows.HorizontalAlignment.Stretch;

        content.VerticalAlignment =
            System.Windows.VerticalAlignment.Stretch;

        content.Margin =
            new Thickness(0);

        var state =
            new ModuleState(
                window,
                content);

        window.Closed +=
            (_, _) =>
            {
                if (_disposing)
                    return;

                _modules.Remove(
                    key);

                if (ReferenceEquals(
                        _surface.Content,
                        content))
                {
                    _surface.Content =
                        null;
                }

                ModuleClosed?.Invoke(
                    key);
            };

        return state;
    }

    private void EnsureWindowLoaded(
        ModuleState state)
    {
        if (state.WindowLoadedRaised ||
            state.WindowLoadedScheduled)
            return;

        if (state.Content.IsLoaded)
        {
            ScheduleWindowLoaded(
                state);

            return;
        }

        RoutedEventHandler? loaded =
            null;

        loaded =
            (_, _) =>
            {
                state.Content.Loaded -=
                    loaded;

                ScheduleWindowLoaded(
                    state);
            };

        state.Content.Loaded +=
            loaded;
    }

    private void ScheduleWindowLoaded(
        ModuleState state)
    {
        if (state.WindowLoadedRaised ||
            state.WindowLoadedScheduled)
            return;

        state.WindowLoadedScheduled =
            true;

        state.Content.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(
                () =>
                {
                    state.WindowLoadedScheduled =
                        false;

                    if (_disposing ||
                        state.WindowLoadedRaised)
                        return;

                    state.WindowLoadedRaised =
                        true;

                    // Existing tools attach their refresh/bootstrap logic to
                    // Window.Loaded. The visual tree is already embedded and
                    // naturally loaded at this point, so raising only the
                    // Window event preserves that logic without ever calling
                    // Window.Show() on the backing HWND.
                    state.Window.RaiseEvent(
                        new RoutedEventArgs(
                            FrameworkElement.LoadedEvent,
                            state.Window));
                }));
    }

    private void SyncBackingWindowSize()
    {
        if (string.IsNullOrWhiteSpace(
                _activeKey) ||
            !_modules.TryGetValue(
                _activeKey,
                out ModuleState? state))
            return;

        double width =
            _surface.ActualWidth;

        double height =
            _surface.ActualHeight;

        if (width > 50)
            state.Window.Width =
                width;

        if (height > 50)
            state.Window.Height =
                height;
    }

    public void Dispose()
    {
        if (_disposing)
            return;

        _disposing =
            true;

        _surface.Content =
            null;

        foreach (ModuleState state in
                 _modules.Values.ToArray())
        {
            try
            {
                state.Window.Close();
            }
            catch
            {
            }
        }

        _modules.Clear();
    }

    internal static Window ResolveOwner(
        Window module) =>
        module.Owner ??
        module;

    internal static bool TryActivateShell(
        Window module)
    {
        if (module.Owner is not
            CommandCenterWindow shell)
            return false;

        shell.ActivateEmbeddedModule(
            module);

        return true;
    }

    private sealed class ModuleState
    {
        internal ModuleState(
            Window window,
            FrameworkElement content)
        {
            Window =
                window;

            Content =
                content;
        }

        internal Window Window { get; }

        internal FrameworkElement Content { get; }

        internal bool WindowLoadedRaised { get; set; }

        internal bool WindowLoadedScheduled { get; set; }
    }
}