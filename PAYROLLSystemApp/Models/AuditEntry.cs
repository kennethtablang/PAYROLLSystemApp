using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// Append-only audit record (FR-091). Nothing in the application updates or
/// deletes rows in this table — that immutability is what FR-092 requires.
/// </summary>
[Table("audit_log")]
public class AuditEntry
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Account that performed the action, or the identifier that was attempted.</summary>
    [MaxLength(120)]
    public string Actor { get; set; } = string.Empty;

    public int? ActorUserId { get; set; }

    [Indexed, MaxLength(60)]
    public string Action { get; set; } = string.Empty;

    /// <summary>Entity type touched, e.g. "User". Broadens as later modules land.</summary>
    [MaxLength(60)]
    public string Entity { get; set; } = string.Empty;

    public int? EntityId { get; set; }

    public bool Success { get; set; }

    /// <summary>
    /// Human-readable context. Must never contain a password, a hash, or other
    /// sensitive personal data (NFR-033).
    /// </summary>
    [MaxLength(500)]
    public string Details { get; set; } = string.Empty;

    [Ignore]
    public DateTime TimestampLocal => DateTime.SpecifyKind(TimestampUtc, DateTimeKind.Utc).ToLocalTime();
}

/// <summary>Canonical action names so the log stays queryable.</summary>
public static class AuditActions
{
    public const string LoginSucceeded = "LOGIN_SUCCEEDED";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string LoginBlocked = "LOGIN_BLOCKED";
    public const string AccountLockedOut = "ACCOUNT_LOCKED_OUT";
    public const string AccountUnlocked = "ACCOUNT_UNLOCKED";
    public const string Logout = "LOGOUT";
    public const string PasswordChanged = "PASSWORD_CHANGED";
    public const string PasswordReset = "PASSWORD_RESET";
    public const string UserCreated = "USER_CREATED";
    public const string UserUpdated = "USER_UPDATED";
    public const string UserDeactivated = "USER_DEACTIVATED";
    public const string UserReactivated = "USER_REACTIVATED";
    public const string AccessDenied = "ACCESS_DENIED";

    // The client's timesheet, keyed by accounting against a run.
    public const string TimesheetKeyed = "TIMESHEET_KEYED";
    public const string TimesheetCleared = "TIMESHEET_CLEARED";

    // Section 2.2 — employee masterfile and organisation reference data.
    public const string EmployeeCreated = "EMPLOYEE_CREATED";
    public const string EmployeeUpdated = "EMPLOYEE_UPDATED";
    public const string EmployeeSeparated = "EMPLOYEE_SEPARATED";
    public const string EmployeeReinstated = "EMPLOYEE_REINSTATED";
    public const string SalaryRateChanged = "SALARY_RATE_CHANGED";
    public const string EmployeeImported = "EMPLOYEE_IMPORTED";
    public const string DepartmentCreated = "DEPARTMENT_CREATED";
    public const string DepartmentUpdated = "DEPARTMENT_UPDATED";
    public const string DepartmentDeactivated = "DEPARTMENT_DEACTIVATED";
    public const string DepartmentReactivated = "DEPARTMENT_REACTIVATED";
    public const string PositionCreated = "POSITION_CREATED";
    public const string PositionUpdated = "POSITION_UPDATED";
    public const string PositionDeactivated = "POSITION_DEACTIVATED";
    public const string PositionReactivated = "POSITION_REACTIVATED";
    public const string WorkScheduleCreated = "WORK_SCHEDULE_CREATED";
    public const string WorkScheduleUpdated = "WORK_SCHEDULE_UPDATED";
    public const string WorkScheduleDeactivated = "WORK_SCHEDULE_DEACTIVATED";
    public const string WorkScheduleReactivated = "WORK_SCHEDULE_REACTIVATED";
    public const string EmployeeArchived = "EMPLOYEE_ARCHIVED";
    public const string EmployeeRestored = "EMPLOYEE_RESTORED";
    public const string DetachmentCreated = "DETACHMENT_CREATED";
    public const string DetachmentUpdated = "DETACHMENT_UPDATED";
    public const string DetachmentDeactivated = "DETACHMENT_DEACTIVATED";
    public const string DetachmentReactivated = "DETACHMENT_REACTIVATED";
    public const string DetachmentRatePosted = "DETACHMENT_RATE_POSTED";
    public const string DetachmentRateWithdrawn = "DETACHMENT_RATE_WITHDRAWN";

    // Section 2.3 — time and attendance, and the holiday calendar it is
    // classified against.
    public const string AttendanceRecorded = "ATTENDANCE_RECORDED";
    public const string AttendanceCorrected = "ATTENDANCE_CORRECTED";
    public const string AttendanceRemoved = "ATTENDANCE_REMOVED";
    public const string AttendanceGenerated = "ATTENDANCE_GENERATED";
    public const string AttendanceImported = "ATTENDANCE_IMPORTED";
    public const string AttendanceLocked = "ATTENDANCE_LOCKED";
    public const string AttendanceLeaveMarked = "ATTENDANCE_LEAVE_MARKED";
    public const string AttendanceLeaveCleared = "ATTENDANCE_LEAVE_CLEARED";
    public const string OvertimeApproved = "OVERTIME_APPROVED";
    public const string HolidayCreated = "HOLIDAY_CREATED";
    public const string HolidayUpdated = "HOLIDAY_UPDATED";
    public const string HolidayDeactivated = "HOLIDAY_DEACTIVATED";
    public const string HolidayReactivated = "HOLIDAY_REACTIVATED";

    // Section 2.4 — leave types, credits and the request queue.
    public const string LeaveTypeCreated = "LEAVE_TYPE_CREATED";
    public const string LeaveTypeUpdated = "LEAVE_TYPE_UPDATED";
    public const string LeaveTypeDeactivated = "LEAVE_TYPE_DEACTIVATED";
    public const string LeaveTypeReactivated = "LEAVE_TYPE_REACTIVATED";
    public const string LeaveRequestFiled = "LEAVE_REQUEST_FILED";
    public const string LeaveRequestApproved = "LEAVE_REQUEST_APPROVED";
    public const string LeaveRequestRejected = "LEAVE_REQUEST_REJECTED";
    public const string LeaveRequestCancelled = "LEAVE_REQUEST_CANCELLED";
    public const string LeaveBalanceAdjusted = "LEAVE_BALANCE_ADJUSTED";
    public const string LeaveCreditsGranted = "LEAVE_CREDITS_GRANTED";

    // Section 2.5 — payroll configuration. Every one of these changes what a
    // payslip comes out at, so the log records the figures that moved, not just
    // that something was saved (FR-091, NFR-011).
    public const string PayPeriodCreated = "PAY_PERIOD_CREATED";
    public const string PayPeriodUpdated = "PAY_PERIOD_UPDATED";
    public const string PayPeriodGenerated = "PAY_PERIOD_GENERATED";
    public const string PayPeriodStatusChanged = "PAY_PERIOD_STATUS_CHANGED";
    public const string EarningTypeCreated = "EARNING_TYPE_CREATED";
    public const string EarningTypeUpdated = "EARNING_TYPE_UPDATED";
    public const string EarningTypeDeactivated = "EARNING_TYPE_DEACTIVATED";
    public const string EarningTypeReactivated = "EARNING_TYPE_REACTIVATED";
    public const string DeductionTypeCreated = "DEDUCTION_TYPE_CREATED";
    public const string DeductionTypeUpdated = "DEDUCTION_TYPE_UPDATED";
    public const string DeductionTypeDeactivated = "DEDUCTION_TYPE_DEACTIVATED";
    public const string DeductionTypeReactivated = "DEDUCTION_TYPE_REACTIVATED";
    public const string PremiumRateChanged = "PREMIUM_RATE_CHANGED";
    public const string StatutoryTableChanged = "STATUTORY_TABLE_CHANGED";
    public const string CompanyProfileUpdated = "COMPANY_PROFILE_UPDATED";
    public const string PayrollSettingsUpdated = "PAYROLL_SETTINGS_UPDATED";

    // Section 2.6 — payroll runs. FR-058's central rule is that an approved or
    // posted run cannot be edited, and NFR-011 wants every attempt recorded, so
    // each state change is its own action rather than a generic "updated".
    public const string PayrollRunCreated = "PAYROLL_RUN_CREATED";
    public const string PayrollRunCalculated = "PAYROLL_RUN_CALCULATED";
    public const string PayrollRunSubmitted = "PAYROLL_RUN_SUBMITTED";
    public const string PayrollRunReturned = "PAYROLL_RUN_RETURNED";
    public const string PayrollRunApproved = "PAYROLL_RUN_APPROVED";
    public const string PayrollRunPosted = "PAYROLL_RUN_POSTED";
    public const string PayrollRunDiscarded = "PAYROLL_RUN_DISCARDED";
    public const string PayrollAdjustmentSaved = "PAYROLL_ADJUSTMENT_SAVED";
    public const string PayrollAdjustmentRemoved = "PAYROLL_ADJUSTMENT_REMOVED";
    public const string LoanCreated = "LOAN_CREATED";
    public const string LoanUpdated = "LOAN_UPDATED";
    public const string LoanStatusChanged = "LOAN_STATUS_CHANGED";
    public const string StandingDeductionSaved = "STANDING_DEDUCTION_SAVED";
    public const string StandingDeductionStopped = "STANDING_DEDUCTION_STOPPED";

    // Section 2.7 — payslips. An export is the moment pay data becomes a file
    // somebody can forward, so it is logged like any other privileged read
    // (NFR-012).
    public const string PayslipExported = "PAYSLIP_EXPORTED";

    // Section 2.8 - reports. A report leaves the application as a file that can
    // be forwarded, so the export is logged for the same reason a payslip's is.
    // The entry names the report and its scope, never the figures (NFR-033).
    public const string ReportExported = "REPORT_EXPORTED";

    // Section 2.9 - backup, restore and archival. A restore replaces every row
    // in the system, so the entry that records it is written to the database
    // that is about to be replaced *and* to the one that replaces it.
    public const string BackupCreated = "BACKUP_CREATED";
    public const string BackupDeleted = "BACKUP_DELETED";
    public const string BackupRestored = "BACKUP_RESTORED";
    public const string BackupSettingsUpdated = "BACKUP_SETTINGS_UPDATED";
    public const string YearArchived = "YEAR_ARCHIVED";
    public const string YearPurged = "YEAR_PURGED";

    // Help & Updates - installing a new version and asking the developer for one.
    public const string UpdateStarted = "UPDATE_STARTED";
    public const string SupportRequestCreated = "SUPPORT_REQUEST_CREATED";
}
