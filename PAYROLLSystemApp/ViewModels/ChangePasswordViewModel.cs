using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Security;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>FR-004: the holder replaces their own password. Opened from the top bar.</summary>
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

    public string Explanation =>
        "Choose a new password for " + (Session.CurrentUser?.Username ?? "your account") + ".";

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
    private void Cancel()
    {
        CurrentPassword = NewPassword = ConfirmPassword = string.Empty;
        Session.ResumeWorkspace();
    }
}
