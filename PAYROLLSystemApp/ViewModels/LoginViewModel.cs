using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>FR-001, FR-003: the sign-in screen.</summary>
public sealed partial class LoginViewModel : BaseViewModel
{
    private readonly IAuthService _auth;

    public LoginViewModel(IAuthService auth, ISessionService session)
        : base(session)
    {
        _auth = auth;
        Title = "Sign in";

        Identifier = string.Empty;
        Password = string.Empty;
        WarningMessage = string.Empty;
        IsPasswordHidden = true;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Identifier { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; }

    [ObservableProperty]
    public partial bool IsPasswordHidden { get; set; }

    /// <summary>Shown when the account is locked, so the reason is unmistakable (FR-003).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string WarningMessage { get; set; }

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningMessage);

    public string PasswordToggleText => IsPasswordHidden ? "Show" : "Hide";

    partial void OnIsPasswordHiddenChanged(bool value) => OnPropertyChanged(nameof(PasswordToggleText));

    private bool CanSignIn() =>
        !string.IsNullOrWhiteSpace(Identifier) && !string.IsNullOrEmpty(Password);

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private Task SignInAsync() => RunAsync(async () =>
    {
        ClearMessages();
        WarningMessage = string.Empty;

        var result = await _auth.AuthenticateAsync(Identifier, Password);

        if (!result.Succeeded)
        {
            if (result.Outcome == AuthOutcome.AccountLockedOut || result.Outcome == AuthOutcome.AccountDisabled)
                WarningMessage = result.Message;
            else
                ShowError(result.AttemptsRemaining is > 0 and <= 2
                    ? $"{result.Message} {result.AttemptsRemaining} attempt(s) remaining before the account is locked."
                    : result.Message);

            Password = string.Empty;
            return;
        }

        // Never leave credentials in memory on a page that stays alive.
        Password = string.Empty;
        Identifier = string.Empty;

        // App listens for SessionStarted and shows the main layout, or the
        // forced password change when the credential is temporary (FR-005).
        Session.SignIn(result.User!);
    });

    [RelayCommand]
    private void TogglePasswordVisibility()
    {
        Session.Touch();
        IsPasswordHidden = !IsPasswordHidden;
    }

    [RelayCommand]
    private void ForgotPassword()
    {
        // FR-005 is an administrator-issued reset; there is no self-service path.
        Session.Touch();
        ShowStatus("Ask your system administrator to issue a temporary password for your account.");
    }

    /// <summary>
    /// First-run helper. Shown only while the seeded administrator is the sole
    /// account, so it disappears as soon as real accounts exist.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowFirstRunHint { get; set; }

    public string FirstRunHint =>
        $"First run — sign in as '{PayrollDatabase.SeedAdminUsername}' with password " +
        $"'{PayrollDatabase.SeedAdminPassword}'. Change it from User Accounts before going live.";

    public async Task LoadAsync()
    {
        ClearMessages();
        WarningMessage = string.Empty;
        Password = string.Empty;

        var users = await _auth.GetAllUsersAsync();
        ShowFirstRunHint = users.Count == 1
                           && users[0].Username == PayrollDatabase.SeedAdminUsername;
    }
}
