namespace PAYROLLSystemApp.Handlers;

/// <summary>
/// Makes the platform's own controls follow Settings → Theme.
///
/// <para>Setting <see cref="Application.UserAppTheme"/> re-evaluates every
/// <c>AppThemeBinding</c>, but on Windows it does not reach WinUI: the
/// dropdown lists, date and time pickers, switches, check boxes, scroll bars,
/// message boxes and the title bar keep following the Windows setting. With
/// Windows in light mode and the app in dark, those stayed light (white
/// dropdowns, light switch tracks, a light title bar), and the reverse.</para>
///
/// <para>Pinning the window content's <c>RequestedTheme</c> fixes everything
/// inside the window, including the popups the controls open, which inherit it.
/// The caption buttons are drawn by Windows and take
/// <c>AppWindow.TitleBar.PreferredTheme</c> instead.</para>
/// </summary>
public static class NativeTheme
{
    /// <summary>Applies the app's chosen theme to every open window.</summary>
    public static void Apply(Application app)
    {
        foreach (var window in app.Windows)
            Apply(window, app.UserAppTheme);
    }

    public static void Apply(Window window, AppTheme theme)
    {
#if WINDOWS
        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
            return;

        if (native.Content is Microsoft.UI.Xaml.FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => Microsoft.UI.Xaml.ElementTheme.Light,
                AppTheme.Dark => Microsoft.UI.Xaml.ElementTheme.Dark,
                _ => Microsoft.UI.Xaml.ElementTheme.Default
            };
        }

        try
        {
            native.AppWindow.TitleBar.PreferredTheme = theme switch
            {
                AppTheme.Light => Microsoft.UI.Windowing.TitleBarTheme.Light,
                AppTheme.Dark => Microsoft.UI.Windowing.TitleBarTheme.Dark,
                _ => Microsoft.UI.Windowing.TitleBarTheme.UseDefaultAppMode
            };
        }
        catch (Exception ex)
        {
            // Older Windows builds without the API keep the system caption colours.
            System.Diagnostics.Debug.WriteLine($"[NativeTheme] {ex.Message}");
        }
#endif
    }
}
