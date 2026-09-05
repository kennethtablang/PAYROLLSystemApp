using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

public enum AuthOutcome
{
    Success,
    InvalidCredentials,
    AccountLockedOut,
    AccountDisabled
}

/// <summary>Outcome of a sign-in attempt (FR-001, FR-003).</summary>
public sealed record AuthResult(
    AuthOutcome Outcome,
    string Message,
    User? User = null,
    int? AttemptsRemaining = null,
    TimeSpan? LockoutRemaining = null)
{
    public bool Succeeded => Outcome == AuthOutcome.Success && User is not null;
}

/// <summary>Outcome of an account or password operation (FR-005, FR-090).</summary>
public sealed record PasswordChangeResult(bool Succeeded, string Message)
{
    public static PasswordChangeResult Ok(string message = "Password updated.") => new(true, message);

    public static PasswordChangeResult Fail(string message) => new(false, message);
}
