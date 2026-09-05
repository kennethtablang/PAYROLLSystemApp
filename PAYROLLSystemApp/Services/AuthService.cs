using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Security;

namespace PAYROLLSystemApp.Services;

public interface IAuthService
{
    Task<AuthResult> AuthenticateAsync(string identifier, string password);

    Task<(PasswordChangeResult Result, string? TemporaryPassword)> ResetPasswordAsync(int userId, User performedBy);

    Task<PasswordChangeResult> UnlockAsync(int userId, User performedBy);

    Task<PasswordChangeResult> CreateUserAsync(User newUser, string password, User performedBy);

    Task<PasswordChangeResult> UpdateAccountAsync(
        int userId, string fullName, UserRole role, bool isActive, User performedBy);

    Task<User?> GetByIdAsync(int userId);

    Task<IReadOnlyList<User>> GetAllUsersAsync();
}

/// <summary>
/// Implements section 2.1 of REQUIREMENTS.md: credential verification (FR-001),
/// lockout (FR-003), password change (FR-004) and administrator reset (FR-005).
/// </summary>
public sealed class AuthService : IAuthService
{
    private readonly PayrollDatabase _database;
    private readonly IPasswordHasher _hasher;
    private readonly IAuditService _audit;
    private readonly AuthOptions _options;

    public AuthService(
        PayrollDatabase database,
        IPasswordHasher hasher,
        IAuditService audit,
        AuthOptions options)
    {
        _database = database;
        _hasher = hasher;
        _audit = audit;
        _options = options;
    }

    public async Task<AuthResult> AuthenticateAsync(string identifier, string password)
    {
        identifier = (identifier ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrEmpty(password))
            return new AuthResult(AuthOutcome.InvalidCredentials, "Enter your username and password.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var key = identifier.ToLowerInvariant();

        // FR-001: either the username or the e-mail address identifies the account.
        var user = await connection.Table<User>()
            .Where(u => u.Username == key || u.Email == key)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (user is null)
        {
            await _audit.WriteAsync(AuditActions.LoginFailed, nameof(User), null, false,
                "Sign-in attempted with an unknown identifier.", identifier).ConfigureAwait(false);

            // Same wording as a wrong password: do not confirm which accounts exist.
            return new AuthResult(AuthOutcome.InvalidCredentials, "Incorrect username or password.");
        }

        if (!user.IsActive)
        {
            await _audit.WriteAsync(AuditActions.LoginBlocked, nameof(User), user.Id, false,
                "Sign-in refused: account is deactivated.", user.Username, user.Id).ConfigureAwait(false);

            return new AuthResult(AuthOutcome.AccountDisabled,
                "This account has been deactivated. Contact your system administrator.", user);
        }

        // FR-003: a locked account is refused before the password is even checked,
        // so a lockout cannot be probed for a correct password.
        if (user.IsCurrentlyLockedOut)
        {
            var remaining = user.LockedOutUntilUtc!.Value - DateTime.UtcNow;

            await _audit.WriteAsync(AuditActions.LoginBlocked, nameof(User), user.Id, false,
                $"Sign-in refused: account locked for a further {FormatRemaining(remaining)}.",
                user.Username, user.Id).ConfigureAwait(false);

            return new AuthResult(AuthOutcome.AccountLockedOut,
                $"Account locked. Try again in {FormatRemaining(remaining)}, or ask an administrator to unlock it.",
                user, LockoutRemaining: remaining);
        }

        if (!_hasher.Verify(password, user.PasswordHash))
            return await RegisterFailedAttemptAsync(connection, user).ConfigureAwait(false);

        // Success: clear the failure counter and any expired lockout (FR-003).
        user.FailedLoginAttempts = 0;
        user.LockedOutUntilUtc = null;
        user.LastLoginUtc = DateTime.UtcNow;

        // Transparently upgrade a hash produced with weaker parameters.
        if (_hasher.NeedsRehash(user.PasswordHash))
            user.PasswordHash = _hasher.Hash(password);

        await connection.UpdateAsync(user).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.LoginSucceeded, nameof(User), user.Id, true,
            $"Signed in as {user.RoleDisplayName}.", user.Username, user.Id).ConfigureAwait(false);

        return new AuthResult(AuthOutcome.Success, "Signed in.", user);
    }

    private async Task<AuthResult> RegisterFailedAttemptAsync(
        SQLite.SQLiteAsyncConnection connection, User user)
    {
        user.FailedLoginAttempts++;

        if (user.FailedLoginAttempts >= _options.MaxFailedAttempts)
        {
            // FR-003: lock the account, then reset the counter so the next
            // lockout again requires a full run of failures.
            user.LockedOutUntilUtc = DateTime.UtcNow.Add(_options.LockoutDuration);
            user.FailedLoginAttempts = 0;

            await connection.UpdateAsync(user).ConfigureAwait(false);

            await _audit.WriteAsync(AuditActions.AccountLockedOut, nameof(User), user.Id, false,
                $"Account locked after {_options.MaxFailedAttempts} consecutive failed sign-in attempts.",
                user.Username, user.Id).ConfigureAwait(false);

            return new AuthResult(AuthOutcome.AccountLockedOut,
                $"Account locked after {_options.MaxFailedAttempts} failed attempts. " +
                $"Try again in {FormatRemaining(_options.LockoutDuration)}, or ask an administrator to unlock it.",
                user, LockoutRemaining: _options.LockoutDuration);
        }

        await connection.UpdateAsync(user).ConfigureAwait(false);

        var attemptsRemaining = _options.MaxFailedAttempts - user.FailedLoginAttempts;

        await _audit.WriteAsync(AuditActions.LoginFailed, nameof(User), user.Id, false,
            $"Incorrect password. {attemptsRemaining} attempt(s) remaining before lockout.",
            user.Username, user.Id).ConfigureAwait(false);

        return new AuthResult(AuthOutcome.InvalidCredentials,
            "Incorrect username or password.", null, attemptsRemaining);
    }

    /// <summary>
    /// FR-005: an administrator issues a replacement password. It is returned
    /// once, to be handed to the account holder, and stored only as a hash.
    /// </summary>
    public async Task<(PasswordChangeResult Result, string? TemporaryPassword)> ResetPasswordAsync(
        int userId, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageUsers))
            return (await DenyAsync(performedBy, "reset a password").ConfigureAwait(false), null);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var user = await connection.FindAsync<User>(userId).ConfigureAwait(false);

        if (user is null)
            return (PasswordChangeResult.Fail("Account not found."), null);

        var temporaryPassword = PasswordPolicy.GenerateTemporaryPassword();

        user.PasswordHash = _hasher.Hash(temporaryPassword);
        user.PasswordChangedUtc = DateTime.UtcNow;
        user.FailedLoginAttempts = 0;
        user.LockedOutUntilUtc = null;   // a reset also clears a lockout (FR-003)

        await connection.UpdateAsync(user).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.PasswordReset, nameof(User), user.Id, true,
            $"New password issued for '{user.Username}'.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return (PasswordChangeResult.Ok("New password issued."), temporaryPassword);
    }

    /// <summary>FR-003: the administrator path to clearing a lockout.</summary>
    public async Task<PasswordChangeResult> UnlockAsync(int userId, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageUsers))
            return await DenyAsync(performedBy, "unlock an account").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var user = await connection.FindAsync<User>(userId).ConfigureAwait(false);

        if (user is null)
            return PasswordChangeResult.Fail("Account not found.");

        user.LockedOutUntilUtc = null;
        user.FailedLoginAttempts = 0;

        await connection.UpdateAsync(user).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.AccountUnlocked, nameof(User), user.Id, true,
            $"Lockout cleared for '{user.Username}'.", performedBy.Username, performedBy.Id)
            .ConfigureAwait(false);

        return PasswordChangeResult.Ok($"'{user.Username}' has been unlocked.");
    }

    /// <summary>FR-090: administrator creates an account and assigns its role.</summary>
    public async Task<PasswordChangeResult> CreateUserAsync(User newUser, string password, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageUsers))
            return await DenyAsync(performedBy, "create a user account").ConfigureAwait(false);

        var username = (newUser.Username ?? string.Empty).Trim().ToLowerInvariant();
        var email = (newUser.Email ?? string.Empty).Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(username))
            return PasswordChangeResult.Fail("Username is required.");

        if (username.Length < 3)
            return PasswordChangeResult.Fail("Username must be at least 3 characters.");

        if (string.IsNullOrWhiteSpace(email) || !IsPlausibleEmail(email))
            return PasswordChangeResult.Fail("Enter a valid e-mail address.");

        var policy = PasswordPolicy.Evaluate(password);
        if (!policy.IsValid)
            return PasswordChangeResult.Fail("The password does not meet the policy:\n" + policy.FailureSummary);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var clash = await connection.Table<User>()
            .Where(u => u.Username == username || u.Email == email)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (clash is not null)
        {
            return PasswordChangeResult.Fail(clash.Username == username
                ? "That username is already taken."
                : "That e-mail address is already registered.");
        }

        newUser.Username = username;
        newUser.Email = email;
        newUser.FullName = (newUser.FullName ?? string.Empty).Trim();
        newUser.PasswordHash = _hasher.Hash(password);
        newUser.CreatedUtc = DateTime.UtcNow;
        newUser.PasswordChangedUtc = DateTime.UtcNow;
        newUser.FailedLoginAttempts = 0;
        newUser.LockedOutUntilUtc = null;

        await connection.InsertAsync(newUser).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.UserCreated, nameof(User), newUser.Id, true,
            $"Account '{newUser.Username}' created with role {newUser.RoleDisplayName}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return PasswordChangeResult.Ok($"Account '{newUser.Username}' created.");
    }

    /// <summary>
    /// FR-090: applies the edits made in the account dialog as one operation,
    /// so a rejected guard leaves nothing partially changed.
    ///
    /// Note there is no hard delete: FR-017 requires accounts to be retained as
    /// historical records, so removal is deactivation.
    /// </summary>
    public async Task<PasswordChangeResult> UpdateAccountAsync(
        int userId, string fullName, UserRole role, bool isActive, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageUsers))
            return await DenyAsync(performedBy, "edit a user account").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var user = await connection.FindAsync<User>(userId).ConfigureAwait(false);

        if (user is null)
            return PasswordChangeResult.Fail("Account not found.");

        if (userId == performedBy.Id && !isActive)
            return PasswordChangeResult.Fail("You cannot deactivate the account you are signed in with.");

        if (userId == performedBy.Id && role != user.Role)
            return PasswordChangeResult.Fail("You cannot change the role of the account you are signed in with.");

        // Never leave the system without a way back in.
        var losingAnAdministrator =
            user.Role == UserRole.SystemAdministrator &&
            user.IsActive &&
            (role != UserRole.SystemAdministrator || !isActive);

        if (losingAnAdministrator &&
            await CountActiveAdministratorsAsync(connection).ConfigureAwait(false) <= 1)
        {
            return PasswordChangeResult.Fail(
                "This is the last active administrator account. Assign another administrator first.");
        }

        var changes = new List<string>();

        var trimmedName = (fullName ?? string.Empty).Trim();
        if (!string.Equals(trimmedName, user.FullName, StringComparison.Ordinal))
        {
            changes.Add($"name '{user.FullName}' to '{trimmedName}'");
            user.FullName = trimmedName;
        }

        if (role != user.Role)
        {
            changes.Add($"role {user.RoleDisplayName} to {RolePermissions.DisplayName(role)}");
            user.Role = role;
        }

        if (isActive != user.IsActive)
        {
            changes.Add(isActive ? "reactivated" : "deactivated");
            user.IsActive = isActive;
        }

        if (changes.Count == 0)
            return PasswordChangeResult.Ok("No changes to save.");

        await connection.UpdateAsync(user).ConfigureAwait(false);

        await _audit.WriteAsync(
            isActive ? AuditActions.UserUpdated : AuditActions.UserDeactivated,
            nameof(User), user.Id, true,
            $"Account '{user.Username}': {string.Join("; ", changes)}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return PasswordChangeResult.Ok($"'{user.Username}' updated.");
    }

    public async Task<User?> GetByIdAsync(int userId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        return await connection.FindAsync<User>(userId).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<User>> GetAllUsersAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        return await connection.Table<User>()
            .OrderBy(u => u.Username)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Parameterised rather than expressed in LINQ: sqlite-net's expression
    /// compiler is unreliable for enum comparisons (NFR-018 still holds — the
    /// role is bound as an argument, never concatenated).
    /// </summary>
    private static Task<int> CountActiveAdministratorsAsync(SQLite.SQLiteAsyncConnection connection) =>
        connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM users WHERE IsActive = 1 AND Role = ?",
            (int)UserRole.SystemAdministrator);

    /// <summary>Records a rejected privileged operation (FR-002, FR-091).</summary>
    private async Task<PasswordChangeResult> DenyAsync(User actor, string attemptedAction)
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, nameof(User), actor.Id, false,
            $"Role {actor.RoleDisplayName} is not permitted to {attemptedAction}.",
            actor.Username, actor.Id).ConfigureAwait(false);

        return PasswordChangeResult.Fail("You do not have permission to perform this action.");
    }

    private static bool IsPlausibleEmail(string value)
    {
        var at = value.IndexOf('@');
        if (at <= 0 || at == value.Length - 1)
            return false;

        var domain = value[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.')
               && !value.Contains(' ') && value.IndexOf('@', at + 1) < 0;
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "a moment";

        if (remaining.TotalMinutes < 1)
            return $"{Math.Ceiling(remaining.TotalSeconds)} second(s)";

        return $"{Math.Ceiling(remaining.TotalMinutes)} minute(s)";
    }
}
