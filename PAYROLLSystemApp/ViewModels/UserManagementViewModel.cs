using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Security;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

public sealed record RoleOption(UserRole Role, string DisplayName);

/// <summary>
/// FR-090, plus the administrator halves of FR-003 (unlock) and FR-005 (reset).
///
/// Add / edit / deactivate each open a modal over the list, so the surrounding
/// context stays visible and a destructive action is always a deliberate second
/// step (NFR-022). Every operation is re-checked in <see cref="AuthService"/>;
/// the dialogs are convenience, not the security boundary.
/// </summary>
public sealed partial class UserManagementViewModel : BaseViewModel
{
    private readonly IAuthService _auth;
    private List<User> _all = new();

    public UserManagementViewModel(IAuthService auth, ISessionService session)
        : base(session)
    {
        _auth = auth;
        Title = "User accounts";

        AvailableRoles = Enum.GetValues<UserRole>()
            .Select(r => new RoleOption(r, RolePermissions.DisplayName(r)))
            .ToList();

        SearchText = string.Empty;
        ResultSummary = string.Empty;
        PasswordPolicyHint =
            $"At least {PasswordPolicy.MinimumLength} characters with upper case, lower case, " +
            "a digit and a special character.";

        ResetCreateForm();
        ResetEditForm();

        TempPassword = string.Empty;
        TempPasswordFor = string.Empty;
        ModalError = string.Empty;
        ConfirmMessage = string.Empty;
        ConfirmTitle = string.Empty;
        ConfirmAction = "Confirm";
    }

    /// <summary>
    /// Drives the dialog layer's visibility. While no dialog is open the layer
    /// must not be hit-testable, or it covers the page and eats every click.
    /// </summary>
    public bool IsAnyModalOpen => IsCreateOpen || IsEditOpen || IsConfirmOpen || IsTempOpen;

    public ObservableCollection<User> Users { get; } = new();

    public IReadOnlyList<RoleOption> AvailableRoles { get; }

    // ------------------------------------------------------------------ list

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial string ResultSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    // --------------------------------------------------------- create modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsCreateOpen { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCreateCommand))]
    public partial string CreateUsername { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCreateCommand))]
    public partial string CreateEmail { get; set; }

    [ObservableProperty]
    public partial string CreateFullName { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCreateCommand))]
    public partial string CreatePassword { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCreateCommand))]
    public partial RoleOption? CreateRole { get; set; }

    [ObservableProperty]
    public partial string PasswordPolicyHint { get; set; }

    // ----------------------------------------------------------- edit modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsEditOpen { get; set; }

    [ObservableProperty]
    public partial string EditTitle { get; set; }

    [ObservableProperty]
    public partial string EditFullName { get; set; }

    [ObservableProperty]
    public partial RoleOption? EditRole { get; set; }

    [ObservableProperty]
    public partial bool EditIsActive { get; set; }

    // -------------------------------------------------------- confirm modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsConfirmOpen { get; set; }

    [ObservableProperty]
    public partial string ConfirmTitle { get; set; }

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; }

    [ObservableProperty]
    public partial string ConfirmAction { get; set; }

    [ObservableProperty]
    public partial bool IsConfirmDestructive { get; set; }

    // -------------------------------------------------- temporary password

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsTempOpen { get; set; }

    [ObservableProperty]
    public partial string TempPassword { get; set; }

    [ObservableProperty]
    public partial string TempPasswordFor { get; set; }

    /// <summary>Inline error shown in whichever dialog is open.</summary>
    [ObservableProperty]
    public partial string ModalError { get; set; }

    private User? _target;
    private ConfirmKind _confirmKind;

    private enum ConfirmKind
    {
        None,
        Reset,
        Unlock
    }

    // ------------------------------------------------------------ list load

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.ManageUsers, "open user management"))
        {
            IsDenied = true;
            Users.Clear();
            return;
        }

        IsDenied = false;
        await RunAsync(ReloadAsync);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        ClearMessages();
        _all = (await _auth.GetAllUsersAsync()).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term = (SearchText ?? string.Empty).Trim();

        var filtered = string.IsNullOrEmpty(term)
            ? _all
            : _all.Where(u =>
                u.Username.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                u.Email.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                u.FullName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                u.RoleDisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        Users.Clear();
        foreach (var user in filtered)
            Users.Add(user);

        ResultSummary = filtered.Count == _all.Count
            ? $"{_all.Count} account(s)"
            : $"{filtered.Count} of {_all.Count} account(s)";
    }

    // ---------------------------------------------------------------- create

    [RelayCommand]
    private void OpenCreate()
    {
        Session.Touch();
        ClearMessages();
        ResetCreateForm();
        IsCreateOpen = true;
    }

    [RelayCommand]
    private void CloseCreate()
    {
        IsCreateOpen = false;
        ModalError = string.Empty;
    }

    [RelayCommand]
    private void GeneratePassword()
    {
        Session.Touch();
        CreatePassword = PasswordPolicy.GenerateTemporaryPassword();
    }

    private bool CanSubmitCreate() =>
        !string.IsNullOrWhiteSpace(CreateUsername) &&
        !string.IsNullOrWhiteSpace(CreateEmail) &&
        !string.IsNullOrEmpty(CreatePassword) &&
        CreateRole is not null;

    [RelayCommand(CanExecute = nameof(CanSubmitCreate))]
    private Task SubmitCreateAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        var candidate = new User
        {
            Username = CreateUsername,
            Email = CreateEmail,
            FullName = CreateFullName,
            Role = CreateRole!.Role,
            IsActive = true
        };

        var result = await _auth.CreateUserAsync(candidate, CreatePassword, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        // Hand the password over once, then clear it from the form.
        TempPasswordFor = candidate.Username;
        TempPassword = CreatePassword;

        IsCreateOpen = false;
        ResetCreateForm();
        IsTempOpen = true;

        ShowStatus(result.Message);
        await ReloadAsync();
    });

    // ------------------------------------------------------------------ edit

    [RelayCommand]
    private void OpenEdit(User? user)
    {
        if (user is null)
            return;

        Session.Touch();
        ClearMessages();
        ModalError = string.Empty;

        _target = user;
        EditTitle = user.Username;
        EditFullName = user.FullName;
        EditIsActive = user.IsActive;
        EditRole = AvailableRoles.First(r => r.Role == user.Role);

        IsEditOpen = true;
    }

    [RelayCommand]
    private void CloseEdit()
    {
        IsEditOpen = false;
        ModalError = string.Empty;
        _target = null;
    }

    [RelayCommand]
    private Task SubmitEditAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null || _target is null || EditRole is null)
            return;

        var result = await _auth.UpdateAccountAsync(
            _target.Id, EditFullName, EditRole.Role, EditIsActive, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsEditOpen = false;
        _target = null;

        ShowStatus(result.Message);
        await ReloadAsync();
    });

    // --------------------------------------------------------------- confirm

    [RelayCommand]
    private void OpenReset(User? user)
    {
        if (user is null)
            return;

        Session.Touch();
        ClearMessages();

        _target = user;
        _confirmKind = ConfirmKind.Reset;
        ConfirmTitle = "Reset password";
        ConfirmMessage =
            $"Issue a new temporary password for '{user.Username}'? Their current password stops working " +
            "immediately, and they must set a new one at their next sign-in.";
        ConfirmAction = "Reset password";
        IsConfirmDestructive = true;
        ModalError = string.Empty;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void OpenUnlock(User? user)
    {
        if (user is null)
            return;

        Session.Touch();
        ClearMessages();

        _target = user;
        _confirmKind = ConfirmKind.Unlock;
        ConfirmTitle = "Unlock account";
        ConfirmMessage = $"Clear the lockout on '{user.Username}' so they can sign in again?";
        ConfirmAction = "Unlock";
        IsConfirmDestructive = false;
        ModalError = string.Empty;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void CloseConfirm()
    {
        IsConfirmOpen = false;
        ModalError = string.Empty;
        _confirmKind = ConfirmKind.None;
        _target = null;
    }

    [RelayCommand]
    private Task SubmitConfirmAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null || _target is null)
            return;

        switch (_confirmKind)
        {
            case ConfirmKind.Reset:
            {
                var (result, temporary) = await _auth.ResetPasswordAsync(_target.Id, performedBy);

                if (!result.Succeeded || temporary is null)
                {
                    ModalError = result.Message;
                    return;
                }

                TempPasswordFor = _target.Username;
                TempPassword = temporary;

                IsConfirmOpen = false;
                IsTempOpen = true;
                ShowStatus($"Temporary password issued for '{_target.Username}'.");
                break;
            }

            case ConfirmKind.Unlock:
            {
                var result = await _auth.UnlockAsync(_target.Id, performedBy);

                if (!result.Succeeded)
                {
                    ModalError = result.Message;
                    return;
                }

                IsConfirmOpen = false;
                ShowStatus(result.Message);
                break;
            }
        }

        _confirmKind = ConfirmKind.None;
        _target = null;
        await ReloadAsync();
    });

    // ------------------------------------------------------ temp password

    [RelayCommand]
    private void CloseTemp()
    {
        Session.Touch();
        IsTempOpen = false;

        // The clear value is held only as long as the dialog shows it.
        TempPassword = string.Empty;
        TempPasswordFor = string.Empty;
    }

    // ----------------------------------------------------------------- forms

    private void ResetCreateForm()
    {
        CreateUsername = string.Empty;
        CreateEmail = string.Empty;
        CreateFullName = string.Empty;
        CreatePassword = string.Empty;
        CreateRole = AvailableRoles.First(r => r.Role == UserRole.Employee);
    }

    private void ResetEditForm()
    {
        EditTitle = string.Empty;
        EditFullName = string.Empty;
        EditIsActive = true;
        EditRole = AvailableRoles.First(r => r.Role == UserRole.Employee);
    }
}
