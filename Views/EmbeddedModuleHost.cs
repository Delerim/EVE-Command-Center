using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace EveCommandCenter.Views;

/// <summary>
/// Hosts the existing mature WPF tool windows inside Command Center without
/// duplicating or replacing their feature code. The backing Window is created
/// and loaded off-screen so its existing Loaded/Closed lifecycle keeps working;
/// only its visual content is re-parented into the Command Center workspace.
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

        window.ShowInTaskbar =
            false;

        window.ShowActivated =
            false;

        window.WindowStartupLocation =
            WindowStartupLocation.Manual;

        window.Left =
            -32000;

        window.Top =
            -32000;

        window.Opacity =
            0;

        double width =
            Math.Max(
                1000,
                _surface.ActualWidth);

        double height =
            Math.Max(
                700,
                _surface.ActualHeight);

        window.Width =
            width;

        window.Height =
            height;

        window.Show();

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
            content.Resources.MergedDictionaries.Add(dictionary);
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

        window.Content =
            null;

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
            window.CommandBindings.Remove(binding);
            content.CommandBindings.Add(binding);
        }

        foreach (System.Windows.Input.InputBinding binding in
                 window.InputBindings
                     .Cast<System.Windows.Input.InputBinding>()
                     .ToArray())
        {
            window.InputBindings.Remove(binding);
            content.InputBindings.Add(binding);
        }

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

                _modules.Remove(key);

                if (ReferenceEquals(
                        _surface.Content,
                        content))
                {
                    _surface.Content =
                        null;
                }

                ModuleClosed?.Invoke(key);
            };

        return state;
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

    private sealed record ModuleState(
        Window Window,
        FrameworkElement Content);
}