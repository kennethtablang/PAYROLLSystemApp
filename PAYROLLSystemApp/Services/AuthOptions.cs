namespace PAYROLLSystemApp.Services;

/// <summary>
/// Tunable security settings. Held in <see cref="Preferences"/> rather than
/// compiled in, so an administrator can adjust them without a rebuild
/// (FR-003, FR-006, NFR-031).
/// </summary>
public sealed class AuthOptions
{
    private const string MaxFailedAttemptsKey = "auth.max_failed_attempts";
    private const string LockoutMinutesKey = "auth.lockout_minutes";
    private const string InactivityMinutesKey = "auth.inactivity_minutes";

    public const int DefaultMaxFailedAttempts = 5;      // FR-003
    public const int DefaultLockoutMinutes = 15;        // FR-003 timed unlock
    public const int DefaultInactivityMinutes = 15;     // FR-006

    /// <summary>Consecutive failures that trigger a lockout (FR-003).</summary>
    public int MaxFailedAttempts
    {
        get => Math.Clamp(Preferences.Default.Get(MaxFailedAttemptsKey, DefaultMaxFailedAttempts), 1, 20);
        set => Preferences.Default.Set(MaxFailedAttemptsKey, Math.Clamp(value, 1, 20));
    }

    /// <summary>How long a locked account stays locked before it unlocks itself (FR-003).</summary>
    public TimeSpan LockoutDuration
    {
        get => TimeSpan.FromMinutes(Math.Clamp(
            Preferences.Default.Get(LockoutMinutesKey, DefaultLockoutMinutes), 1, 1440));
        set => Preferences.Default.Set(LockoutMinutesKey,
            Math.Clamp((int)value.TotalMinutes, 1, 1440));
    }

    /// <summary>Idle time before the session is signed out (FR-006).</summary>
    public TimeSpan InactivityTimeout
    {
        get => TimeSpan.FromMinutes(Math.Clamp(
            Preferences.Default.Get(InactivityMinutesKey, DefaultInactivityMinutes), 1, 480));
        set => Preferences.Default.Set(InactivityMinutesKey,
            Math.Clamp((int)value.TotalMinutes, 1, 480));
    }
}
