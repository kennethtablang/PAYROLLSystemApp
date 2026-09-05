using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

public enum SessionEndReason
{
    SignedOut,
    TimedOut,

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
        SessionEndReason.TimedOut => "You were signed out because the session was idle.",
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

    void SignIn(User user);

    Task SignOutAsync(SessionEndReason reason = SessionEndReason.SignedOut);

    /// <summary>Records user interaction, resetting the inactivity countdown (FR-006).</summary>
    void Touch();

    bool Has(Permission permission);

    /// <summary>Guard for privileged operations; writes an audit entry when refused.</summary>
    Task<bool> RequireAsync(Permission permission, string attemptedAction);
}

/// <summary>
/// Holds the signed-in identity for the life of the session and enforces the
/// idle timeout (FR-006).
///
/// The countdown is advanced by <see cref="Touch"/>, which pages call when they
/// appear and view models call when a command runs. A background timer polls
/// every few seconds rather than scheduling a single long timer, so a device
/// that was suspended is caught on the next tick.
/// </summary>
public sealed class SessionService : ISessionService, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly AuthOptions _options;
    private readonly IAuditService _audit;
    private readonly object _gate = new();

    private Timer? _idleTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private bool _signingOut;

    public SessionService(AuthOptions options, IAuditService audit)
    {
        _options = options;
        _audit = audit;
    }

    public User? CurrentUser { get; private set; }

    public bool IsSignedIn => CurrentUser is not null;

    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    public event EventHandler? SessionStarted;

    public void SignIn(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        lock (_gate)
        {
            CurrentUser = user;
            _lastActivityUtc = DateTime.UtcNow;
            _signingOut = false;

            _idleTimer?.Dispose();
            _idleTimer = new Timer(OnIdleTick, null, PollInterval, PollInterval);
        }

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

            _idleTimer?.Dispose();
            _idleTimer = null;
        }

        await _audit.WriteAsync(
            reason == SessionEndReason.TimedOut ? AuditActions.SessionTimedOut : AuditActions.Logout,
            nameof(User), user!.Id, true,
            reason == SessionEndReason.TimedOut
                ? $"Session ended automatically after {_options.InactivityTimeout.TotalMinutes:0} minute(s) of inactivity."
                : "User signed out.",
            user.Username, user.Id).ConfigureAwait(false);

        RaiseSessionEnded(reason);
    }

    public void Touch()
    {
        lock (_gate)
        {
            if (CurrentUser is not null)
                _lastActivityUtc = DateTime.UtcNow;
        }
    }

    public bool Has(Permission permission) => CurrentUser?.Can(permission) == true;

    public async Task<bool> RequireAsync(Permission permission, string attemptedAction)
    {
        if (Has(permission))
        {
            Touch();
            return true;
        }

        var user = CurrentUser;

        await _audit.WriteAsync(AuditActions.AccessDenied, nameof(User), user?.Id, false,
            user is null
                ? $"Unauthenticated attempt to {attemptedAction}."
                : $"Role {user.RoleDisplayName} is not permitted to {attemptedAction}.",
            user?.Username, user?.Id).ConfigureAwait(false);

        return false;
    }

    private void OnIdleTick(object? state)
    {
        bool expired;

        lock (_gate)
        {
            expired = CurrentUser is not null
                      && !_signingOut
                      && DateTime.UtcNow - _lastActivityUtc >= _options.InactivityTimeout;
        }

        if (expired)
            _ = SignOutAsync(SessionEndReason.TimedOut);
    }

    private void RaiseSessionEnded(SessionEndReason reason)
    {
        var handler = SessionEnded;
        if (handler is null)
            return;

        // The timer fires on a pool thread; navigation must run on the UI thread.
        if (MainThread.IsMainThread)
            handler(this, new SessionEndedEventArgs(reason));
        else
            MainThread.BeginInvokeOnMainThread(() => handler(this, new SessionEndedEventArgs(reason)));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }
}
