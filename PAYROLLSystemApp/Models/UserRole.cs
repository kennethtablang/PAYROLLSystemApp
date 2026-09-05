namespace PAYROLLSystemApp.Models;

/// <summary>
/// The actor roles defined in REQUIREMENTS.md section 1.5.
/// </summary>
public enum UserRole
{
    SystemAdministrator = 0,
    HrOfficer = 1,
    PayrollOfficer = 2,
    Approver = 3,
    Employee = 4
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
    /// <see cref="ManageSystemConfiguration"/>, because a Payroll Officer holds
    /// that one — and restoring a backup replaces every payslip in the system
    /// with an older copy of itself. That is an administrator's decision.</para>
    /// </summary>
    ManageBackups,

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
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<UserRole, HashSet<Permission>> Map =
        new Dictionary<UserRole, HashSet<Permission>>
        {
            [UserRole.SystemAdministrator] = new()
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
            [UserRole.HrOfficer] = new()
            {
                Permission.ManageEmployees,
                Permission.ManageAttendance,
                Permission.ManageLeave,
                Permission.ViewReports,
                Permission.ViewOwnPayslip,
                Permission.FileLeaveRequest
            },
            [UserRole.PayrollOfficer] = new()
            {
                Permission.RunPayroll,
                Permission.ViewAllPayslips,
                Permission.ViewReports,
                Permission.ManageSystemConfiguration,
                Permission.ViewOwnPayslip,
                Permission.FileLeaveRequest
            },
            [UserRole.Approver] = new()
            {
                Permission.ApprovePayroll,
                Permission.ManageLeave,
                Permission.ViewReports,
                Permission.ViewOwnPayslip,
                Permission.FileLeaveRequest
            },
            [UserRole.Employee] = new()
            {
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
        UserRole.SystemAdministrator => "System Administrator",
        UserRole.HrOfficer => "HR Officer",
        UserRole.PayrollOfficer => "Payroll Officer",
        UserRole.Approver => "Approver / Manager",
        UserRole.Employee => "Employee",
        _ => role.ToString()
    };
}
