using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>
/// Settings: how the app looks, the signed-in account, where printed and
/// exported files go, and which screen opens after sign-in.
///
/// <para>Everything here except the report paper is a preference of this PC
/// (<see cref="IUserPreferences"/>) and takes effect as soon as it is chosen.
/// The paper is company-wide payroll configuration, so it has an explicit,
/// audited save and only an account that may configure payroll can change it.</para>
/// </summary>
public sealed partial class SettingsViewModel : BaseViewModel
{
    private readonly IUserPreferences _preferences;
    private readonly IPayrollConfigService _config;
    private readonly IAuthService _auth;

    // Set while the form is being filled, so loading does not count as a choice.
    private bool _loading;

    public SettingsViewModel(
        ISessionService session,
        IUserPreferences preferences,
        IPayrollConfigService config,
        IAuthService auth)
        : base(session)
    {
        _preferences = preferences;
        _config = config;
        _auth = auth;
        Title = "Settings";

        ThemeOptions =
        [
            new EnumOption((int)ThemeChoice.MatchWindows, "Match Windows"),
            new EnumOption((int)ThemeChoice.Light, "Light"),
            new EnumOption((int)ThemeChoice.Dark, "Dark")
        ];

        TextSizeOptions =
        [
            new EnumOption((int)TextSizeChoice.Normal, "Normal"),
            new EnumOption((int)TextSizeChoice.Large, "Large (115%)"),
            new EnumOption((int)TextSizeChoice.Larger, "Larger (130%)")
        ];

        PaperOptions = ReportPaperSizes.All
            .Select(p => new EnumOption((int)p, ReportPaperSizes.Display(p)))
            .ToList();

        AccountName = string.Empty;
        AccountUsername = string.Empty;
        AccountEmail = string.Empty;
        AccountRole = string.Empty;
        AccountLastSignIn = string.Empty;
        AccountPasswordChanged = string.Empty;
        StartOptions = [];
        ExportsFolder = Path.GetDirectoryName(ReportService.ExportFolder()) ?? string.Empty;
    }

    // ============================================================ appearance

    public IReadOnlyList<EnumOption> ThemeOptions { get; }

    [ObservableProperty]
    public partial EnumOption? SelectedTheme { get; set; }

    partial void OnSelectedThemeChanged(EnumOption? value)
    {
        if (!_loading && value is not null)
            _preferences.Theme = value.As<ThemeChoice>();
    }

    public IReadOnlyList<EnumOption> TextSizeOptions { get; }

    [ObservableProperty]
    public partial EnumOption? SelectedTextSize { get; set; }

    /// <summary>The saved size differs from the one this window was built with.</summary>
    [ObservableProperty]
    public partial bool TextSizeNeedsRestart { get; set; }

    partial void OnSelectedTextSizeChanged(EnumOption? value)
    {
        if (value is null)
            return;

        if (!_loading)
            _preferences.TextSize = value.As<TextSizeChoice>();

        TextSizeNeedsRestart = value.As<TextSizeChoice>() != TextScale.Choice;
    }

    [ObservableProperty]
    public partial bool CompactSidebar { get; set; }

    partial void OnCompactSidebarChanged(bool value)
    {
        if (!_loading)
            _preferences.CompactSidebar = value;
    }

    // ============================================================ behaviour

    [ObservableProperty]
    public partial bool ConfirmSignOut { get; set; }

    partial void OnConfirmSignOutChanged(bool value)
    {
        if (!_loading)
            _preferences.ConfirmSignOut = value;
    }

    [ObservableProperty]
    public partial bool RememberReportChoice { get; set; }

    partial void OnRememberReportChoiceChanged(bool value)
    {
        if (!_loading)
            _preferences.RememberReportChoice = value;
    }

    // ============================================================ my account

    [ObservableProperty]
    public partial string AccountName { get; set; }

    [ObservableProperty]
    public partial string AccountUsername { get; set; }

    [ObservableProperty]
    public partial string AccountEmail { get; set; }

    [ObservableProperty]
    public partial string AccountRole { get; set; }

    [ObservableProperty]
    public partial string AccountLastSignIn { get; set; }

    [ObservableProperty]
    public partial string AccountPasswordChanged { get; set; }

    /// <summary>FR-004. Cancel on the password screen comes back here.</summary>
    [RelayCommand]
    private void ChangePassword() => Session.RequestPasswordChange();

    // ============================================================ printing

    public IReadOnlyList<EnumOption> PaperOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaperDetail))]
    public partial EnumOption? SelectedPaper { get; set; }

    public string PaperDetail => ReportPaperSizes.Detail(
        SelectedPaper?.As<ReportPaper>() ?? ReportPaper.DotMatrix11x14);

    /// <summary>The paper is shared by everyone, so it follows the payroll configuration permission.</summary>
    public bool CanEditPaper => Session.Has(Permission.ManageSystemConfiguration);

    public bool CannotEditPaper => !CanEditPaper;

    [RelayCommand]
    private Task SavePaperAsync() => RunAsync(async () =>
    {
        ClearMessages();

        var user = Session.CurrentUser;
        if (user is null || SelectedPaper is null)
            return;

        var result = await _config.SaveReportPaperAsync(SelectedPaper.As<ReportPaper>(), user);

        if (result.Succeeded)
            ShowStatus(result.Message);
        else
            ShowError(result.Message);
    });

    [ObservableProperty]
    public partial bool OpenAfterExport { get; set; }

    partial void OnOpenAfterExportChanged(bool value)
    {
        if (!_loading)
            _preferences.OpenAfterExport = value;
    }

    /// <summary>Documents\Payroll MS: payslips, reports, imports and backups sit beneath it.</summary>
    public string ExportsFolder { get; }

    [RelayCommand]
    private void OpenExportsFolder()
    {
        ClearMessages();

        try
        {
            Directory.CreateDirectory(ExportsFolder);
#if WINDOWS
            System.Diagnostics.Process.Start("explorer.exe", ExportsFolder);
#else
            ShowStatus($"Files are saved under {ExportsFolder}");
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsViewModel] {ex}");
            ShowError($"The folder could not be opened. It is {ExportsFolder}");
        }
    }

    // ============================================================ start-up

    /// <summary>Only the sections this account can open.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<EnumOption> StartOptions { get; set; }

    [ObservableProperty]
    public partial EnumOption? SelectedStart { get; set; }

    partial void OnSelectedStartChanged(EnumOption? value)
    {
        if (_loading || value is null || Session.CurrentUser is not { } user)
            return;

        _preferences.SetStartSection(user, value.As<AppSection>());
        ShowStatus($"{value.Label} will open the next time you sign in.");
    }

    // ============================================================ load

    public Task LoadAsync() => RunAsync(async () =>
    {
        ClearMessages();

        var session = Session.CurrentUser;
        if (session is null)
            return;

        _loading = true;

        try
        {
            SelectedTheme = ThemeOptions.First(o => o.Value == (int)_preferences.Theme);
            OpenAfterExport = _preferences.OpenAfterExport;
            SelectedTextSize = TextSizeOptions.First(o => o.Value == (int)_preferences.TextSize);
            CompactSidebar = _preferences.CompactSidebar;
            ConfirmSignOut = _preferences.ConfirmSignOut;
            RememberReportChoice = _preferences.RememberReportChoice;

            // The session's copy was taken at sign-in; read the stored account
            // so a password changed since then shows its new date.
            var user = await _auth.GetByIdAsync(session.Id) ?? session;

            AccountName = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;
            AccountUsername = user.Username;
            AccountEmail = string.IsNullOrWhiteSpace(user.Email) ? "—" : user.Email;
            AccountRole = user.RoleDisplayName;
            AccountLastSignIn = user.LastLoginUtc is { } login ? FormatLocal(login) : "—";
            AccountPasswordChanged = FormatLocal(user.PasswordChangedUtc);

            var settings = await _config.GetSettingsAsync();
            SelectedPaper = PaperOptions.FirstOrDefault(o => o.Value == (int)settings.ReportPaper);

            StartOptions = AppSections.All
                .Where(s => session.Can(s.RequiredPermission))
                .Select(s => new EnumOption((int)s.Section, s.Title))
                .ToList();

            var start = _preferences.StartSectionFor(session);
            SelectedStart = StartOptions.FirstOrDefault(o => o.Value == (int)start);
        }
        finally
        {
            _loading = false;
        }
    });

    private static string FormatLocal(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("MMM d, yyyy h:mm tt");
}
