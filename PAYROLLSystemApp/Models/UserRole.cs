namespace PAYROLLSystemApp.Models;

/// <summary>
/// The three account types this system issues.
///
/// <para>REQUIREMENTS.md section 1.5 named five actors; the company runs
/// payroll with three: an <b>administrator</b> who owns the system and
/// approves what goes out, an <b>accounting</b> clerk who keeps the employee
/// master and the payroll configuration, and a <b>payroll officer</b> who
/// encodes the cut-off and computes it. Roles nobody holds are roles nobody
/// maintains, and an unused role in the matrix below is a permission grant
/// that is never reviewed.</para>
///
/// <para><b>The stored values are deliberate.</b> <see cref="Administrator"/>
/// keeps 0, which is what the seeded bootstrap account and every existing
/// administrator row already carry. <see cref="PayrollOfficer"/> takes back
/// 2, the value the same-named role held in the five-role matrix, so a row
/// from that era lands in the role of its own name — which is narrower than
/// Accounting, never wider. See <c>PayrollDatabase.MigrateUserRolesAsync</c>
/// for what happens to rows that carried 3 or 4, the roles that no longer
/// exist.</para>
/// </summary>
public enum UserRole
{
    /// <summary>Owns the system: users, audit log, backups, and approval.</summary>
    Administrator = 0,

    /// <summary>Keeps the employee master, detachment rates and payroll setup, and prepares payroll.</summary>
    Accounting = 1,

    /// <summary>Prepares payroll only: timesheets, attendance, leave, runs, payslips, reports.</summary>
    PayrollOfficer = 2
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
/// <para><b>Accounting and the payroll officer prepare; the administrator
/// approves.</b> Neither can approve a run, issue accounts, read the audit log
/// or restore a backup. That separation is the segregation of duties, so it is
/// the line in this matrix most worth defending.</para>
///
/// <para><b>The payroll officer encodes; Accounting owns what the figures are
/// priced from.</b> The payroll officer keys timesheets, attendance and leave
/// and computes the run, but cannot change an employee, a detachment's daily
/// rate or the payroll configuration — the inputs that decide what a day is
/// worth. Whoever keys the hours should not also be able to change the rate
/// they are multiplied by.</para>
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
            },
            [UserRole.PayrollOfficer] = new()
            {
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
        UserRole.PayrollOfficer => "Payroll Officer",
        _ => role.ToString()
    };

    /// <summary>One line for the role picker, so whoever issues an account can see what it reaches.</summary>
    public static string Describe(UserRole role) => role switch
    {
        UserRole.Administrator =>
            "Everything, including user accounts, the audit log, backups and approving payroll.",
        UserRole.Accounting =>
            "Employees, detachments and rates, payroll setup, and preparing payroll. Cannot approve.",
        UserRole.PayrollOfficer =>
            "Timesheets, attendance, leave, computing runs, payslips and reports. " +
            "Cannot change employees, rates or payroll setup, or approve.",
        _ => string.Empty
    };
}
