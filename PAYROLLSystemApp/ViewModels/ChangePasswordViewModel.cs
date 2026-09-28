using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Security;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>
/// FR-004: the holder replaces their own password. Opened from the top bar, or
/// forced straight after sign-in when someone else chose the current password —
/// the seeded administrator, a new account, an administrator reset (FR-005).
/// </summary>
public sealed partial class ChangePasswordViewModel : BaseViewModel
{
    private readonly IAuthService _auth;

    public ChangePasswordViewModel(IAuthService auth, ISessionService session)
        : base(session)
    {
        _auth = auth;
        Title = "Change password";

        CurrentPassword = string.Empty;
        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;
        PolicyChecklist = string.Empty;
        UpdateChecklist();
    }

    /// <summary>
    /// A forced change has no way back to the workspace: its only exit other
    /// than saving is signing out.
    /// </summary>
    public bool IsForced => Session.CurrentUser?.MustChangePassword == true;

    public string Heading => IsForced ? "Set a new password" : "Change password";

    public string Explanation => IsForced
        ? "Your current password was issued to you by someone else. Choose your own before continuing — " +
          "nobody else should know it."
        : "Choose a new password for " + (Session.CurrentUser?.Username ?? "your account") + ".";

    public string CancelText => IsForced ? "Sign out" : "Cancel";

    [ObservableProperty]
    public partial string CurrentPassword { get; set; }

    [ObservableProperty]
    public partial string NewPassword { get; set; }

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; }

    /// <summary>NFR-013, shown live so the holder is not left guessing.</summary>
    [ObservableProperty]
    public partial string PolicyChecklist { get; set; }

    partial void OnNewPasswordChanged(string value) => UpdateChecklist();

    private void UpdateChecklist() =>
        PolicyChecklist = string.Join("\n",
            PasswordPolicy.Evaluate(NewPassword).Rules.Select(r => (r.Satisfied ? "✓  " : "•  ") + r.Description));

    [RelayCommand]
    private Task SaveAsync() => RunAsync(async () =>
    {
        ClearMessages();

        var user = Session.CurrentUser;
        if (user is null)
            return;

        if (NewPassword != ConfirmPassword)
        {
            ShowError("The new password and its confirmation do not match.");
            return;
        }

        var result = await _auth.ChangePasswordAsync(user, CurrentPassword, NewPassword);

        CurrentPassword = string.Empty;

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        NewPassword = string.Empty;
        ConfirmPassword = string.Empty;

        Session.ResumeWorkspace();
    });

    [RelayCommand]
    private async Task CancelAsync()
    {
        CurrentPassword = NewPassword = ConfirmPassword = string.Empty;

        if (IsForced)
            await Session.SignOutAsync();
        else
            Session.ResumeWorkspace();
    }
}
