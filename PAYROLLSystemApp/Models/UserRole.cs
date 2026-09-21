namespace PAYROLLSystemApp.Models;

/// <summary>
/// The two account types this system issues.
///
/// <para>REQUIREMENTS.md section 1.5 named five actors, but the company runs
/// payroll with two people: an <b>administrator</b> who owns the system and
/// approves what goes out, and an <b>accounting</b> clerk who prepares it.
/// Roles nobody holds are roles nobody maintains, and an unused role in the
/// matrix below is a permission grant that is never reviewed.</para>
///
/// <para><b>The stored values are deliberate.</b> <see cref="Administrator"/>
/// keeps 0, which is what the seeded bootstrap account and every existing
/// administrator row already carry, so collapsing the list does not rewrite
/// them. See <c>PayrollDatabase.MigrateUserRolesAsync</c> for what happens to
/// rows that carried one of the three roles that no longer exist.</para>
/// </summary>
public enum UserRole
{
    /// <summary>Owns the system: users, audit log, backups, and approval.</summary>
    Administrator = 0,

    /// <summary>Prepares payroll: employees, attendance, leave, runs, reports.</summary>
    Accounting = 1
}

/// <summary>
/// Discrete capabilities that a screen or an operation can require.
/// FR-002: a user may only reach a screen or a record permitted by their role.
/// </summary>
public enum Permission
{
    ManageUsers,
    ViewAuditLog,
    ManageSystemConfiguration,

    /// <summary>
    /// FR-093, FR-094. Backing the database up, restoring it, and archiving a
    /// closed payroll year.
    ///
    /// <para>Its own permission rather than part of
    /// <see cref="ManageSystemConfiguration"/>, because Accounting holds that
    /// one — and restoring a backup replaces every payslip in the system with
    /// an older copy of itself. That is an administrator's decision.</para>
    /// </summary>
    ManageBackups,

    /// <summary>
    /// FR-012. Departments, positions and work schedules — and with them the
    /// detachment code and name a department is deployed under, which Accounting
    /// assigns and the payroll summary prints.
    /// </summary>
    ManageEmployees,
    ManageAttendance,
    ManageLeave,

    RunPayroll,
    ApprovePayroll,
    ViewAllPayslips,
    ViewReports,

    ViewOwnPayslip,
    FileLeaveRequest
}

/// <summary>
/// The single source of truth for role to permission mapping (FR-002).
/// Kept as data so the matrix can be reviewed without reading control flow.
///
/// <para><b>Accounting prepares; the administrator approves.</b> Accounting
/// computes a run and reads every report off it, but cannot approve one, cannot
/// issue accounts, cannot read the audit log and cannot restore a backup. With
/// only two accounts in the building that separation is the whole of the
/// segregation of duties, so it is the one line in this matrix worth defending.
/// </para>
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<UserRole, HashSet<Permission>> Map =
        new Dictionary<UserRole, HashSet<Permission>>
        {
            [UserRole.Administrator] = new()
            {
                Permission.ManageUsers,
                Permission.ViewAuditLog,
                Permission.ManageSystemConfiguration,
                Permission.ManageBackups,
                Permission.ManageEmployees,
                Permission.ManageAttendance,
                Permission.ManageLeave,
                Permission.RunPayroll,
                Permission.ApprovePayroll,
                Permission.ViewAllPayslips,
                Permission.ViewReports,
                Permission.ViewOwnPayslip,
                Permission.FileLeaveRequest
            },
            [UserRole.Accounting] = new()
            {
                Permission.ManageSystemConfiguration,
                Permission.ManageEmployees,
                Permission.ManageAttendance,
                Permission.ManageLeave,
                Permission.RunPayroll,
                Permission.ViewAllPayslips,
                Permission.ViewReports,
                Permission.ViewOwnPayslip,
                Permission.FileLeaveRequest
            }
        };

    public static bool Has(UserRole role, Permission permission) =>
        Map.TryGetValue(role, out var granted) && granted.Contains(permission);

    public static IReadOnlyCollection<Permission> For(UserRole role) =>
        Map.TryGetValue(role, out var granted) ? granted : Array.Empty<Permission>();

    public static string DisplayName(UserRole role) => role switch
    {
        UserRole.Administrator => "Administrator",
        UserRole.Accounting => "Accounting",
        _ => role.ToString()
    };
}
