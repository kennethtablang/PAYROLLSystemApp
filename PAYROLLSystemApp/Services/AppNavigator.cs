using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>The destinations reachable from the sidebar.</summary>
public enum AppSection
{
    Dashboard,
    Employees,
    Organization,
    Detachments,
    Attendance,
    Timesheets,
    Leave,
    PayrollSetup,
    PayrollRuns,
    Approvals,
    Payslips,
    Reports,
    Users,
    AuditLog,
    DataManagement,
    Settings
}

/// <summary>
/// Static description of one sidebar destination: what it is called, which
/// permission it needs (FR-002) and whether the module exists yet.
/// </summary>
public sealed record SectionInfo(
    AppSection Section,
    string Title,
    string Group,
    string Description,
    Permission RequiredPermission,
    bool IsImplemented,
    string RequirementRef);

public static class AppSections
{
    public const string GroupMain = "MAIN";
    public const string GroupPayroll = "PAYROLL";
    public const string GroupAdmin = "ADMINISTRATION";
    public const string GroupSystem = "SYSTEM";

    public static readonly IReadOnlyList<SectionInfo> All =
    [
        new(AppSection.Dashboard, "Dashboard", GroupMain,
            "Overview of your access and the payroll cycle",
            Permission.ViewOwnPayslip, true, ""),

        new(AppSection.Employees, "Employees", GroupPayroll,
            "Employee master records, compensation and government IDs",
            Permission.ManageEmployees, true, ""),

        new(AppSection.Organization, "Departments & Positions", GroupPayroll,
            "Organisation data used when assigning employees",
            Permission.ManageEmployees, true, ""),

        new(AppSection.Detachments, "Detachments", GroupPayroll,
            "Client posts across Luzon, and the daily rate paid at each of them",
            Permission.ManageEmployees, true, ""),

        new(AppSection.Attendance, "Time & Attendance", GroupPayroll,
            "Daily time records, overtime and holidays",
            Permission.ManageAttendance, true, ""),

        new(AppSection.Timesheets, "Timesheets", GroupPayroll,
            "Key the client's cut-off sheet: days, overtime, night shift, loan and late",
            Permission.RunPayroll, true, ""),

        new(AppSection.Leave, "Leave", GroupPayroll,
            "Leave credits, filing and approval",
            Permission.ManageLeave, true, ""),

        new(AppSection.PayrollSetup, "Payroll Setup", GroupPayroll,
            "Pay calendar, components, premium and statutory tables",
            Permission.ManageSystemConfiguration, true, ""),

        new(AppSection.PayrollRuns, "Payroll Runs", GroupPayroll,
            "Compute a pay period, adjust it, and submit it",
            Permission.RunPayroll, true, ""),

        new(AppSection.Approvals, "Approvals", GroupPayroll,
            "Review, approve and post payroll runs",
            Permission.ApprovePayroll, true, ""),

        new(AppSection.Payslips, "Payslips", GroupPayroll,
            "Itemised payslips, year-to-date totals and PDF export",
            Permission.ViewOwnPayslip, true, ""),

        new(AppSection.Reports, "Reports", GroupPayroll,
            "Payroll register, remittances and summaries",
            Permission.ViewReports, true, ""),

        new(AppSection.Users, "User Accounts", GroupAdmin,
            "Create accounts, assign roles, reset passwords",
            Permission.ManageUsers, true, ""),

        new(AppSection.AuditLog, "Audit Log", GroupAdmin,
            "Every sign-in and account change",
            Permission.ViewAuditLog, true, ""),

        new(AppSection.DataManagement, "Backup & Archive", GroupAdmin,
            "Database backups, restore, and closed payroll years",
            Permission.ManageBackups, true, ""),

        // Every signed-in account holds ViewOwnPayslip, so everyone reaches it.
        new(AppSection.Settings, "Settings", GroupSystem,
            "Appearance, your account, printing and the screen you start on",
            Permission.ViewOwnPayslip, true, "")
    ];

    public static SectionInfo Get(AppSection section) =>
        All.First(s => s.Section == section);
}

public interface IAppNavigator
{
    AppSection Current { get; }

    event EventHandler<AppSection>? Navigated;

    void NavigateTo(AppSection section);

    /// <summary>
    /// Opens a section on a particular record — a run's timesheets, say. The
    /// section takes the value once, when it next loads.
    /// </summary>
    void NavigateTo(AppSection section, int recordId);

    /// <summary>The record a section was opened on, if any; cleared once read.</summary>
    int? TakeRecordFor(AppSection section);
}

/// <summary>
/// Section switching inside the main layout. Replaces Shell routing: this app
/// is a desktop-style console with a fixed sidebar rather than a stack of
/// pushed pages, so navigation is just swapping the content region.
/// </summary>
public sealed class AppNavigator : IAppNavigator
{
    public AppSection Current { get; private set; } = AppSection.Dashboard;

    public event EventHandler<AppSection>? Navigated;

    private (AppSection Section, int Id)? _record;

    public void NavigateTo(AppSection section, int recordId)
    {
        _record = (section, recordId);
        NavigateTo(section);
    }

    public int? TakeRecordFor(AppSection section)
    {
        if (_record is not { } record || record.Section != section)
            return null;

        _record = null;
        return record.Id;
    }

    public void NavigateTo(AppSection section)
    {
        Current = section;

        if (MainThread.IsMainThread)
            Navigated?.Invoke(this, section);
        else
            MainThread.BeginInvokeOnMainThread(() => Navigated?.Invoke(this, section));
    }
}
