using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

public enum ThemeChoice
{
    MatchWindows = 0,
    Light = 1,
    Dark = 2
}

public enum TextSizeChoice
{
    Normal = 0,
    Large = 1,
    Larger = 2
}

/// <summary>
/// The text size chosen in Settings, as a multiplier. Static because it is read
/// while XAML is being loaded — by the theme's metrics and by
/// <c>{markup:Font}</c> — before any service exists. Fixed for the life of the
/// process: the theme's styles are built once, so a change waits for a restart.
/// </summary>
public static class TextScale
{
    internal const string Key = "ui.textSize";

    public static TextSizeChoice Choice { get; } = Read();

    public static double Factor { get; } = Choice switch
    {
        TextSizeChoice.Large => 1.15,
        TextSizeChoice.Larger => 1.3,
        _ => 1.0
    };

    /// <summary>A size scaled and rounded to the half point, so text stays crisp.</summary>
    public static double Apply(double size) => Math.Round(size * Factor * 2, MidpointRounding.AwayFromZero) / 2;

    private static TextSizeChoice Read()
    {
        try
        {
            var saved = (TextSizeChoice)Preferences.Default.Get(Key, (int)TextSizeChoice.Normal);
            return Enum.IsDefined(saved) ? saved : TextSizeChoice.Normal;
        }
        catch
        {
            // The XAML designer and unit tests have no preference store.
            return TextSizeChoice.Normal;
        }
    }
}

/// <summary>
/// Choices that belong to this PC rather than to the payroll data, so they live
/// in the platform's preference store and never in the database: a restore
/// from backup should not change how the screen looks.
/// </summary>
public interface IUserPreferences
{
    ThemeChoice Theme { get; set; }

    /// <summary>Pushes <see cref="Theme"/> onto the application.</summary>
    void ApplyTheme(Application app);

    /// <summary>Whether a payslip or report export is opened as soon as it is saved.</summary>
    bool OpenAfterExport { get; set; }

    /// <summary>
    /// The section opened after this account signs in. Falls back to the
    /// dashboard when the saved section is one the account can no longer open.
    /// </summary>
    AppSection StartSectionFor(User user);

    void SetStartSection(User user, AppSection section);

    /// <summary>Takes effect at the next start; see <see cref="TextScale"/>.</summary>
    TextSizeChoice TextSize { get; set; }

    /// <summary>The sidebar shows icons only. Raises <see cref="Changed"/>.</summary>
    bool CompactSidebar { get; set; }

    /// <summary>Ask "Are you sure?" before signing out.</summary>
    bool ConfirmSignOut { get; set; }

    /// <summary>Reports reopens on the report and department last used.</summary>
    bool RememberReportChoice { get; set; }

    /// <summary>A per-account value a screen keeps between visits, e.g. the last report.</summary>
    string? GetRemembered(User user, string name);

    void SetRemembered(User user, string name, string? value);

    /// <summary>Raised with the property name when a live preference changes.</summary>
    event EventHandler<string>? Changed;
}

public sealed class UserPreferences : IUserPreferences
{
    private const string ThemeKey = "ui.theme";
    private const string OpenAfterExportKey = "export.openAfter";
    private const string StartSectionKeyPrefix = "ui.startSection.";
    private const string CompactSidebarKey = "ui.compactSidebar";
    private const string ConfirmSignOutKey = "ui.confirmSignOut";
    private const string RememberReportKey = "reports.remember";
    private const string RememberedKeyPrefix = "remembered.";

    public event EventHandler<string>? Changed;

    public ThemeChoice Theme
    {
        get
        {
            var saved = (ThemeChoice)Preferences.Default.Get(ThemeKey, (int)ThemeChoice.MatchWindows);
            return Enum.IsDefined(saved) ? saved : ThemeChoice.MatchWindows;
        }
        set
        {
            Preferences.Default.Set(ThemeKey, (int)value);

            if (Application.Current is { } app)
                ApplyTheme(app);
        }
    }

    public void ApplyTheme(Application app)
    {
        app.UserAppTheme = Theme switch
        {
            ThemeChoice.Light => AppTheme.Light,
            ThemeChoice.Dark => AppTheme.Dark,
            _ => AppTheme.Unspecified
        };
    }

    public bool OpenAfterExport
    {
        get => Preferences.Default.Get(OpenAfterExportKey, true);
        set => Preferences.Default.Set(OpenAfterExportKey, value);
    }

    // Keyed by account id: two people sharing the PC each keep their own.
    public AppSection StartSectionFor(User user)
    {
        var saved = Preferences.Default.Get(StartSectionKeyPrefix + user.Id, (int)AppSection.Dashboard);

        if (!Enum.IsDefined((AppSection)saved))
            return AppSection.Dashboard;

        var section = (AppSection)saved;
        var info = AppSections.All.FirstOrDefault(s => s.Section == section);

        return info is not null && user.Can(info.RequiredPermission) ? section : AppSection.Dashboard;
    }

    public void SetStartSection(User user, AppSection section) =>
        Preferences.Default.Set(StartSectionKeyPrefix + user.Id, (int)section);

    public TextSizeChoice TextSize
    {
        get
        {
            var saved = (TextSizeChoice)Preferences.Default.Get(TextScale.Key, (int)TextSizeChoice.Normal);
            return Enum.IsDefined(saved) ? saved : TextSizeChoice.Normal;
        }
        set => Preferences.Default.Set(TextScale.Key, (int)value);
    }

    public bool CompactSidebar
    {
        get => Preferences.Default.Get(CompactSidebarKey, false);
        set
        {
            Preferences.Default.Set(CompactSidebarKey, value);
            Changed?.Invoke(this, nameof(CompactSidebar));
        }
    }

    public bool ConfirmSignOut
    {
        get => Preferences.Default.Get(ConfirmSignOutKey, true);
        set => Preferences.Default.Set(ConfirmSignOutKey, value);
    }

    public bool RememberReportChoice
    {
        get => Preferences.Default.Get(RememberReportKey, true);
        set => Preferences.Default.Set(RememberReportKey, value);
    }

    public string? GetRemembered(User user, string name)
    {
        var value = Preferences.Default.Get<string?>(RememberedKeyPrefix + name + "." + user.Id, null);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void SetRemembered(User user, string name, string? value)
    {
        var key = RememberedKeyPrefix + name + "." + user.Id;

        if (string.IsNullOrEmpty(value))
            Preferences.Default.Remove(key);
        else
            Preferences.Default.Set(key, value);
    }
}
