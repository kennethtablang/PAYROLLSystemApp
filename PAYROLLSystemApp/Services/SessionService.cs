using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

public enum SessionEndReason
{
    SignedOut,

    /// <summary>
    /// FR-093. The database underneath the session was replaced. The signed-in
    /// account came from the database that is now gone, so the session cannot
    /// continue — the account may not exist in the restored copy, or may hold a
    /// different role.
    /// </summary>
    DatabaseRestored
}

public sealed class SessionEndedEventArgs : EventArgs
{
    public SessionEndedEventArgs(SessionEndReason reason) => Reason = reason;

    public SessionEndReason Reason { get; }

    public string Message => Reason switch
    {
        SessionEndReason.DatabaseRestored =>
            "The database was restored from a backup. Sign in again against the restored data.",
        _ => "You have been signed out."
    };
}

public interface ISessionService
{
    User? CurrentUser { get; }

    bool IsSignedIn { get; }

    event EventHandler<SessionEndedEventArgs>? SessionEnded;

    event EventHandler? SessionStarted;

    /// <summary>The holder asked to replace their password (FR-004).</summary>
    event EventHandler? PasswordChangeRequested;

    void SignIn(User user);

    void RequestPasswordChange();

    /// <summary>Returns a signed-in session to the workspace, e.g. after a password change.</summary>
    void ResumeWorkspace();

    Task SignOutAsync(SessionEndReason reason = SessionEndReason.SignedOut);

    bool Has(Permission permission);

    /// <summary>Guard for privileged operations; writes an audit entry when refused.</summary>
    Task<bool> RequireAsync(Permission permission, string attemptedAction);
}

/// <summary>
/// Holds the signed-in identity for the life of the session. The session stays
/// open until the user signs out or the database is replaced underneath it —
/// there is no idle timeout.
/// </summary>
public sealed class SessionService : ISessionService
{
    private readonly IAuditService _audit;
    private readonly object _gate = new();

    private bool _signingOut;

    public SessionService(IAuditService audit) => _audit = audit;

    public User? CurrentUser { get; private set; }

    public bool IsSignedIn => CurrentUser is not null;

    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    public event EventHandler? SessionStarted;

    public event EventHandler? PasswordChangeRequested;

    public void SignIn(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        lock (_gate)
        {
            CurrentUser = user;
            _signingOut = false;
        }

        SessionStarted?.Invoke(this, EventArgs.Empty);
    }

    public void RequestPasswordChange()
    {
        if (IsSignedIn)
            PasswordChangeRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ResumeWorkspace()
    {
        if (IsSignedIn)
            SessionStarted?.Invoke(this, EventArgs.Empty);
    }

    public async Task SignOutAsync(SessionEndReason reason = SessionEndReason.SignedOut)
    {
        User? user;

        lock (_gate)
        {
            if (CurrentUser is null || _signingOut)
                return;

            _signingOut = true;
            user = CurrentUser;
            CurrentUser = null;
        }

        await _audit.WriteAsync(AuditActions.Logout, nameof(User), user!.Id, true,
            "User signed out.", user.Username, user.Id).ConfigureAwait(false);

        RaiseSessionEnded(reason);
    }

    public bool Has(Permission permission) => CurrentUser?.Can(permission) == true;

    public async Task<bool> RequireAsync(Permission permission, string attemptedAction)
    {
        if (Has(permission))
            return true;

        var user = CurrentUser;

        await _audit.WriteAsync(AuditActions.AccessDenied, nameof(User), user?.Id, false,
            user is null
                ? $"Unauthenticated attempt to {attemptedAction}."
                : $"Role {user.RoleDisplayName} is not permitted to {attemptedAction}.",
            user?.Username, user?.Id).ConfigureAwait(false);

        return false;
    }

    private void RaiseSessionEnded(SessionEndReason reason)
    {
        var handler = SessionEnded;
        if (handler is null)
            return;

        // Sign-out can be raised off the UI thread; navigation must run on it.
        if (MainThread.IsMainThread)
            handler(this, new SessionEndedEventArgs(reason));
        else
            MainThread.BeginInvokeOnMainThread(() => handler(this, new SessionEndedEventArgs(reason)));
    }
}
