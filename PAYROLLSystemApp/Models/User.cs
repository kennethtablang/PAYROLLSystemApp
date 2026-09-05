using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// A system login account. Distinct from the employee master record (section 2.2),
/// which is linked later through <see cref="EmployeeId"/>.
/// </summary>
[Table("users")]
public class User
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Login name. Stored lower-cased so lookups are case-insensitive (FR-001).</summary>
    [Indexed(Name = "ux_users_username", Order = 1, Unique = true), MaxLength(50), NotNull]
    public string Username { get; set; } = string.Empty;

    /// <summary>Alternate login identifier (FR-001).</summary>
    [Indexed(Name = "ux_users_email", Order = 1, Unique = true), MaxLength(120), NotNull]
    public string Email { get; set; } = string.Empty;

    [MaxLength(120)]
    public string FullName { get; set; } = string.Empty;

    /// <summary>PBKDF2 composite hash. Never stores a reversible password (NFR-014).</summary>
    [NotNull]
    public string PasswordHash { get; set; } = string.Empty;

    public UserRole Role { get; set; } = UserRole.Employee;

    /// <summary>Deactivated accounts are retained for audit history but cannot sign in.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Consecutive failures since the last success. Reset on success (FR-003).</summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>UTC instant until which sign-in is refused. Null when not locked (FR-003).</summary>
    public DateTime? LockedOutUntilUtc { get; set; }

    public DateTime? LastLoginUtc { get; set; }

    public DateTime PasswordChangedUtc { get; set; } = DateTime.UtcNow;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Links this account to an employee record once section 2.2 exists.</summary>
    public int? EmployeeId { get; set; }

    [Ignore]
    public bool IsCurrentlyLockedOut =>
        LockedOutUntilUtc.HasValue && LockedOutUntilUtc.Value > DateTime.UtcNow;

    [Ignore]
    public string RoleDisplayName => RolePermissions.DisplayName(Role);

    [Ignore]
    public string StatusDisplay =>
        !IsActive ? "Deactivated"
        : IsCurrentlyLockedOut ? "Locked out"
        : "Active";

    public bool Can(Permission permission) =>
        IsActive && RolePermissions.Has(Role, permission);
}
