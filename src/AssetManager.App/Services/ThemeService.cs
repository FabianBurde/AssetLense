using System.Collections;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace AssetManager.App.Services;

/// <summary>
/// Light/dark mode. Standard controls (buttons, menus, scrollbars) come from WPF's built-in Fluent theme
/// via ThemeMode (.NET 9 or newer); our own colors live in Themes/Light.xaml and Dark.xaml and are copied
/// into the application resources one by one, so they never touch the Fluent dictionaries.
/// </summary>
public sealed class ThemeService
{
    private readonly AppSettings _settings;
    private readonly List<Window> _windows = new();

    public ThemeService(AppSettings settings) => _settings = settings;

    public bool IsDark { get; private set; }

    /// <summary>Applies the saved choice, or the Windows app theme on the very first start.</summary>
    public void Initialize() => Apply(_settings.DarkMode ?? SystemPrefersDark());

    public void Toggle()
    {
        Apply(!IsDark);
        _settings.DarkMode = IsDark;
        _settings.Save();
    }

    /// <summary>Keeps a window's title bar in sync with the theme (it is drawn by Windows, not by WPF).</summary>
    public void Attach(Window window)
    {
        _windows.Add(window);
        window.SourceInitialized += (_, _) => SetDarkTitleBar(window, IsDark);
        SetDarkTitleBar(window, IsDark); // no-op until the window has a handle
    }

    private void Apply(bool dark)
    {
        var app = Application.Current;

        // Microsoft marks ThemeMode as experimental, so this is the only place that touches it.
#pragma warning disable WPF0001
        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        ApplyBrushes(app, dark);
        IsDark = dark;

        foreach (var window in _windows)
            SetDarkTitleBar(window, dark);
    }

    /// <summary>
    /// Copies our brushes into Application.Resources key by key. Everything bound with DynamicResource
    /// updates immediately, and WPF's own Fluent dictionaries are left completely alone.
    /// </summary>
    private static void ApplyBrushes(Application app, bool dark)
    {
        var source = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };

        foreach (DictionaryEntry entry in source)
            app.Resources[entry.Key] = entry.Value;
    }

    private static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    // ---- dark title bar (Windows 10 1903+ and Windows 11) ----

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeOld = 19; // Windows 10 before 20H1

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static void SetDarkTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeOld, ref value, sizeof(int));
    }
}
