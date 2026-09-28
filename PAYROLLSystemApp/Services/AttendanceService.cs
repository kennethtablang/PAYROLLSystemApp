using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>Outcome of writing one attendance day, with any per-field messages.</summary>
public sealed record AttendanceSaveResult(
    bool Succeeded,
    string Message,
    AttendanceRecord? Record = null,
    IReadOnlyList<FieldError>? Errors = null)
{
    public IReadOnlyList<FieldError> FieldErrors => Errors ?? Array.Empty<FieldError>();

    public static AttendanceSaveResult Ok(AttendanceRecord record, string message) =>
        new(true, message, record);

    public static AttendanceSaveResult Invalid(IReadOnlyList<FieldError> errors) =>
        new(false, "Correct the highlighted fields and try again.", null, errors);

    public static AttendanceSaveResult Fail(string message) => new(false, message);
}

/// <summary>What filling in a cut-off skeleton actually did.</summary>
public sealed record GenerateResult(bool Succeeded, string Message, int Created, int Existing);

/// <summary>
/// What marking a range as leave actually did.
///
/// <para><see cref="Worked"/> and <see cref="Locked"/> are the days it could not
/// touch. Neither is an error — an approver may legitimately grant leave over a
/// day the employee then came in for — but both must be reported, because those
/// days will not carry the leave through to payroll.</para>
/// </summary>
public sealed record LeaveMarkResult(bool Succeeded, string Message, int Marked, int Worked, int Locked);

/// <summary>
/// FR-027. One employee's attendance over a cut-off, reduced to the figures
/// payroll and a supervisor actually ask for.
/// </summary>
public sealed record AttendanceSummary(
    Employee Employee,
    DateTime From,
    DateTime To,
    int DaysRecorded,
    int DaysPresent,
    int DaysHalf,
    int DaysAbsent,
    int DaysOnLeave,
    int DaysHoliday,
    int DaysRestDay,
    int DaysSuspended,
    int DaysUnrecorded,
    decimal RegularHours,
    decimal OvertimeRendered,
    decimal OvertimeApproved,
    decimal NightDifferentialHours,
    int LateMinutes,
    int UndertimeMinutes)
{
    public int EmployeeId => Employee.Id;

    public string EmployeeName => Employee.FullName;

    public string EmployeeNumber => Employee.EmployeeNumber;

    /// <summary>Overtime worked that nobody has authorised yet, and so is unpaid.</summary>
    public decimal OvertimePending => Math.Max(0m, OvertimeRendered - OvertimeApproved);

    public bool HasPendingOvertime => OvertimePending > 0m;

    /// <summary>
    /// Days in the cut-off with no record at all. Called out because an
    /// incomplete cut-off, not a wrong one, is the usual cause of a short
    /// payslip — the day simply was never entered.
    /// </summary>
    public bool IsIncomplete => DaysUnrecorded > 0;

    public string PeriodDisplay => $"{From:dd MMM} – {To:dd MMM yyyy}";

    public string RegularHoursDisplay => $"{RegularHours:0.##}";

    public string OvertimeDisplay => OvertimeApproved == OvertimeRendered
        ? $"{OvertimeApproved:0.##}"
        : $"{OvertimeApproved:0.##} / {OvertimeRendered:0.##}";

    public string NightDifferentialDisplay => $"{NightDifferentialHours:0.##}";

    public string LateDisplay => LateMinutes == 0 ? "—" : $"{LateMinutes} min";

    public string UndertimeDisplay => UndertimeMinutes == 0 ? "—" : $"{UndertimeMinutes} min";

    public string AttendanceDisplay =>
        $"{DaysPresent} present · {DaysHalf} half · {DaysAbsent} absent · {DaysOnLeave} leave";
}

public interface IAttendanceService
{
    /// <summary>Everything recorded for one employee across a range, in date order.</summary>
    Task<IReadOnlyList<AttendanceRecord>> GetRangeAsync(int employeeId, DateTime from, DateTime to);

    /// <summary>
    /// One row per employee for a single date (FR-020). Employees with nothing
    /// recorded come back as unsaved drafts — <c>Id == 0</c> — already classified
    /// against their schedule and the holiday calendar, so the grid can show what
    /// the day is worth before anyone has typed a punch into it.
    /// </summary>
    Task<IReadOnlyList<AttendanceRecord>> BuildBoardAsync(IReadOnlyList<Employee> employees, DateTime date);

    /// <summary>
    /// One employee's cut-off, every day present, missing ones as drafts. This is
    /// the timesheet the summary in FR-027 is computed from.
    /// </summary>
    Task<IReadOnlyList<AttendanceRecord>> BuildTimesheetAsync(Employee employee, DateTime from, DateTime to);

    /// <summary>A single day, existing or freshly classified.</summary>
    Task<AttendanceRecord> BuildDayAsync(Employee employee, DateTime date);

    /// <summary>
    /// The dates in a range the employee was actually expected to work — rest
    /// days and non-working holidays removed.
    ///
    /// <para>This is the definition leave counts against (FR-031), so that leave
    /// from a Friday to the following Monday costs two days rather than four.
    /// It lives here because attendance already owns the schedule and the
    /// holiday calendar; a second copy in the leave service would be a second
    /// answer to the same question.</para>
    /// </summary>
    Task<IReadOnlyList<DateTime>> GetWorkingDaysAsync(Employee employee, DateTime from, DateTime to);

    /// <summary>
    /// FR-033. Marks a range as leave, or takes the marking back off it.
    ///
    /// <para>Authorised by <see cref="Permission.ManageLeave"/> rather than
    /// <see cref="Permission.ManageAttendance"/>: the authority comes from
    /// approving the leave, and an Approver has the former but not the latter.</para>
    /// </summary>
    Task<LeaveMarkResult> MarkLeaveAsync(
        Employee employee, DateTime from, DateTime to, bool onLeave, string reason, User performedBy);

    /// <summary>FR-020, FR-024. Validates, recomputes and writes one day.</summary>
    Task<AttendanceSaveResult> SaveAsync(AttendanceRecord record, User performedBy);

    /// <summary>Removes a day recorded in error. Refused once the day is locked.</summary>
    Task<AttendanceSaveResult> DeleteAsync(int id, User performedBy);

    /// <summary>FR-022. Authorises overtime already rendered.</summary>
    Task<AttendanceSaveResult> SetOvertimeApprovedAsync(int id, decimal hours, User performedBy);

    /// <summary>
    /// Fills a cut-off with the days nobody would otherwise type: rest days,
    /// holidays and absences.
    /// </summary>
    Task<GenerateResult> GeneratePeriodAsync(
        IReadOnlyList<Employee> employees, DateTime from, DateTime to, User performedBy);

    /// <summary>FR-027. The cut-off summary for one employee.</summary>
    Task<AttendanceSummary> GetSummaryAsync(Employee employee, DateTime from, DateTime to);

    /// <summary>FR-027, FR-083. The same summary for a set of employees, in one pass.</summary>
    Task<IReadOnlyList<AttendanceSummary>> GetSummariesAsync(
        IReadOnlyList<Employee> employees, DateTime from, DateTime to);

    /// <summary>
    /// Locks every day in a range so it can no longer be edited. Called when a
    /// payroll run covering the cut-off is posted (NFR-037).
    /// </summary>
    Task<int> LockRangeAsync(IReadOnlyList<int> employeeIds, DateTime from, DateTime to, User performedBy);
}

/// <summary>
/// Section 2.3 of the requirements: daily time records and everything derived
/// from them (FR-020 – FR-027).
///
/// <para><b>Validation and computation live here, not in the view model.</b> The
/// entry dialog is one caller; the cut-off generator is a second, and a
/// biometric import (FR-028) would be a third. A rule enforced only in the
/// dialog is a rule the other two do not have.</para>
///
/// <para><b>The record classifies itself once, then keeps that classification.</b>
/// Whether a day was a rest day, and which holiday it fell on, is resolved when
/// the day is created and snapshotted onto the row. Re-reading the calendar on
/// every load would let a holiday proclaimed in March silently change what
/// January paid (NFR-009).</para>
/// </summary>
public sealed class AttendanceService : IAttendanceService
{
    /// <summary>
    /// Longest span a single day's punches may cover. Anything past this is a
    /// mistyped time-out rather than a genuine shift, and letting it through
    /// would pay sixteen hours of overtime nobody worked.
    /// </summary>
    private const int MaximumShiftMinutes = 20 * 60;

    private readonly PayrollDatabase _database;
    private readonly IOrganizationService _organization;
    private readonly IHolidayService _holidays;
    private readonly IAuditService _audit;

    public AttendanceService(
        PayrollDatabase database,
        IOrganizationService organization,
        IHolidayService holidays,
        IAuditService audit)
    {
        _database = database;
        _organization = organization;
        _holidays = holidays;
        _audit = audit;
    }

    // ------------------------------------------------------------- reading

    public async Task<IReadOnlyList<AttendanceRecord>> GetRangeAsync(int employeeId, DateTime from, DateTime to)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var start = from.Date;
        var end = to.Date;

        var rows = await connection.Table<AttendanceRecord>()
            .Where(r => r.EmployeeId == employeeId && r.Date >= start && r.Date <= end)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.OrderBy(r => r.Date).ToList();
    }

    public async Task<IReadOnlyList<AttendanceRecord>> BuildBoardAsync(
        IReadOnlyList<Employee> employees, DateTime date)
    {
        if (employees.Count == 0)
            return Array.Empty<AttendanceRecord>();

        var day = date.Date;

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var saved = (await connection.Table<AttendanceRecord>()
                .Where(r => r.Date == day)
                .ToListAsync()
                .ConfigureAwait(false))
            .ToDictionary(r => r.EmployeeId);

        var schedules = await LoadSchedulesAsync().ConfigureAwait(false);
        var calendar = await _holidays.GetCalendarAsync(day, day).ConfigureAwait(false);

        var board = new List<AttendanceRecord>(employees.Count);

        foreach (var employee in employees)
        {
            board.Add(saved.TryGetValue(employee.Id, out var existing)
                ? existing
                : Draft(employee, day, schedules, calendar));
        }

        return board;
    }

    public async Task<IReadOnlyList<AttendanceRecord>> BuildTimesheetAsync(
        Employee employee, DateTime from, DateTime to)
    {
        var start = from.Date;
        var end = to.Date;

        if (end < start)
            return Array.Empty<AttendanceRecord>();

        var saved = (await GetRangeAsync(employee.Id, start, end).ConfigureAwait(false))
            .ToDictionary(r => r.Date.Date);

        var schedules = await LoadSchedulesAsync().ConfigureAwait(false);
        var calendar = await _holidays.GetCalendarAsync(start, end).ConfigureAwait(false);

        var sheet = new List<AttendanceRecord>();

        for (var date = start; date <= end; date = date.AddDays(1))
        {
            // A day outside the employment is not a gap in the timesheet: it is
            // a day this person was not employed, and showing it as unrecorded
            // would make every new hire's first cut-off look incomplete.
            if (!employee.IsPayableOver(date, date))
                continue;

            sheet.Add(saved.TryGetValue(date, out var existing)
                ? existing
                : Draft(employee, date, schedules, calendar));
        }

        return sheet;
    }

    public async Task<AttendanceRecord> BuildDayAsync(Employee employee, DateTime date)
    {
        var day = date.Date;

        var existing = (await GetRangeAsync(employee.Id, day, day).ConfigureAwait(false))
            .FirstOrDefault();

        if (existing is not null)
            return existing;

        var schedules = await LoadSchedulesAsync().ConfigureAwait(false);
        var calendar = await _holidays.GetCalendarAsync(day, day).ConfigureAwait(false);

        return Draft(employee, day, schedules, calendar);
    }

    public async Task<IReadOnlyList<DateTime>> GetWorkingDaysAsync(
        Employee employee, DateTime from, DateTime to)
    {
        var sheet = await BuildTimesheetAsync(employee, from, to).ConfigureAwait(false);

        // An officer may have overridden a day's classification — moving someone
        // off their usual rest day, say — and the saved record carries that
        // override. Reading the sheet rather than the schedule means leave
        // counts against the days as they actually stand.
        return sheet
            .Where(IsExpectedAtWork)
            .Select(d => d.Date)
            .ToList();
    }

    /// <summary>
    /// Whether the employee was expected to work this day. A special
    /// <i>working</i> day is an ordinary day that happens to be proclaimed, so it
    /// counts; a regular or special non-working holiday does not.
    /// </summary>
    private static bool IsExpectedAtWork(AttendanceRecord record) =>
        !record.IsRestDay &&
        record.HolidayType is not (HolidayType.Regular or HolidayType.SpecialNonWorking);

    // ------------------------------------------------------------- writing

    public async Task<LeaveMarkResult> MarkLeaveAsync(
        Employee employee, DateTime from, DateTime to, bool onLeave, string reason, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageLeave))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(AttendanceRecord), null, false,
                $"Role {performedBy.RoleDisplayName} is not permitted to mark leave on attendance.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return new LeaveMarkResult(false, "You do not have permission to perform this action.", 0, 0, 0);
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var schedules = await LoadSchedulesAsync().ConfigureAwait(false);
        var calendar = await _holidays.GetCalendarAsync(from.Date, to.Date).ConfigureAwait(false);

        var saved = (await GetRangeAsync(employee.Id, from, to).ConfigureAwait(false))
            .ToDictionary(r => r.Date.Date);

        var workingDays = await GetWorkingDaysAsync(employee, from, to).ConfigureAwait(false);

        var marked = 0;
        var worked = 0;
        var locked = 0;

        foreach (var date in workingDays)
        {
            saved.TryGetValue(date, out var record);

            if (record is not null && record.IsLocked)
            {
                locked++;
                continue;
            }

            // Somebody who punched in was at work, whatever the leave says. The
            // punches are the evidence; overwriting them to satisfy an approval
            // would erase the hours they actually rendered.
            if (record is not null && record.HasPunches)
            {
                worked++;
                continue;
            }

            if (onLeave)
            {
                record ??= Draft(employee, date, schedules, calendar);

                record.Status = AttendanceStatus.OnLeave;
                record.Remarks = reason;
                record.Source = AttendanceSource.Generated;
            }
            else
            {
                // Nothing to take the marking off.
                if (record is null || record.Status != AttendanceStatus.OnLeave)
                    continue;

                // Cleared first, because the calculator deliberately preserves a
                // status an officer asserted. Re-applying then lets the day fall
                // back to whatever it is without the leave — usually an absence.
                record.Status = AttendanceStatus.Present;
                record.Remarks = reason;

                AttendanceCalculator.Apply(record, ScheduleFor(employee, schedules));
            }

            record.UpdatedUtc = DateTime.UtcNow;

            if (record.Id == 0)
                await connection.InsertAsync(record).ConfigureAwait(false);
            else
                await connection.UpdateAsync(record).ConfigureAwait(false);

            marked++;
        }

        await _audit.WriteAsync(
            onLeave ? AuditActions.AttendanceLeaveMarked : AuditActions.AttendanceLeaveCleared,
            nameof(AttendanceRecord), null, true,
            $"{(onLeave ? "Marked" : "Cleared")} leave on {marked} day(s) over " +
            $"{from:dd MMM yyyy} – {to:dd MMM yyyy} for {employee.EmployeeNumber} " +
            $"{employee.DisplayName}. {reason}".Trim(),
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        var notes = new List<string>();

        if (worked > 0)
            notes.Add($"{worked} day(s) already have punches and were left as worked");

        if (locked > 0)
            notes.Add($"{locked} day(s) are locked by a posted payroll run");

        var message = notes.Count == 0
            ? string.Empty
            : string.Join("; ", notes) + ".";

        return new LeaveMarkResult(true, message, marked, worked, locked);
    }

    public async Task<AttendanceSaveResult> SaveAsync(AttendanceRecord record, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageAttendance))
            return await RefuseAsync(performedBy, "record or correct attendance").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var employee = await connection.Table<Employee>()
            .Where(e => e.Id == record.EmployeeId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (employee is null)
            return AttendanceSaveResult.Fail("That employee no longer exists.");

        record.Date = record.Date.Date;
        record.Remarks = (record.Remarks ?? string.Empty).Trim();

        var existing = record.Id == 0
            ? (await GetRangeAsync(record.EmployeeId, record.Date, record.Date).ConfigureAwait(false))
                .FirstOrDefault()
            : await connection.Table<AttendanceRecord>()
                .Where(r => r.Id == record.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

        // FR-020: one row per employee per day. Without this an employee could be
        // paid twice for the same date by entering it twice.
        if (record.Id == 0 && existing is not null)
        {
            return AttendanceSaveResult.Fail(
                $"{employee.DisplayName} already has an entry for {record.Date:dd MMM yyyy}. Edit that one instead.");
        }

        if (existing is not null && existing.IsLocked)
        {
            return AttendanceSaveResult.Fail(
                "That day has been locked by a posted payroll run and can no longer be edited. " +
                "Correct it through an adjustment run.");
        }

        if (await LockedPeriodCoveringAsync(connection, employee, record.Date).ConfigureAwait(false) is { } lockedIn)
            return AttendanceSaveResult.Fail(PeriodLockedMessage(lockedIn));

        var errors = Validate(record, employee);
        if (errors.Count > 0)
            return AttendanceSaveResult.Invalid(errors);

        // FR-024: a correction must say why. A new entry need not — there is
        // nothing yet to explain the difference from.
        if (existing is not null && record.Remarks.Length == 0)
        {
            return AttendanceSaveResult.Invalid(
                [new FieldError(nameof(AttendanceRecord.Remarks), "Give the reason for the correction.")]);
        }

        var schedule = await ScheduleForAsync(employee).ConfigureAwait(false);
        AttendanceCalculator.Apply(record, schedule);

        var isNew = existing is null;

        if (isNew)
        {
            record.Id = 0;
            record.Source = AttendanceSource.Manual;
            record.CreatedUtc = DateTime.UtcNow;
            record.UpdatedUtc = DateTime.UtcNow;

            await connection.InsertAsync(record).ConfigureAwait(false);
        }
        else
        {
            record.Id = existing!.Id;
            record.CreatedUtc = existing.CreatedUtc;
            record.IsLocked = existing.IsLocked;
            record.UpdatedUtc = DateTime.UtcNow;

            // FR-024: who changed it and when, kept on the row itself so a
            // dispute does not have to be reconstructed from the audit log.
            record.OverriddenBy = performedBy.Username;
            record.OverriddenUtc = DateTime.UtcNow;

            // A generated skeleton day that somebody has now typed into is no
            // longer generated; it is an entry a person is answerable for.
            if (record.Source == AttendanceSource.Generated)
                record.Source = AttendanceSource.Manual;

            await connection.UpdateAsync(record).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            isNew ? AuditActions.AttendanceRecorded : AuditActions.AttendanceCorrected,
            nameof(AttendanceRecord), record.Id, true,
            Describe(employee, record, isNew),
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return AttendanceSaveResult.Ok(record,
            isNew
                ? $"{record.Date:dd MMM} recorded for {employee.DisplayName}."
                : $"{record.Date:dd MMM} corrected for {employee.DisplayName}.");
    }

    public async Task<AttendanceSaveResult> DeleteAsync(int id, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageAttendance))
            return await RefuseAsync(performedBy, "remove an attendance entry").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var record = await connection.Table<AttendanceRecord>()
            .Where(r => r.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (record is null)
            return AttendanceSaveResult.Fail("That entry no longer exists.");

        if (record.IsLocked)
        {
            return AttendanceSaveResult.Fail(
                "That day has been locked by a posted payroll run and cannot be removed.");
        }

        if (await LockedPeriodCoveringAsync(connection, record.EmployeeId, record.Date).ConfigureAwait(false) is { } lockedIn)
            return AttendanceSaveResult.Fail(PeriodLockedMessage(lockedIn));

        var employee = await connection.Table<Employee>()
            .Where(e => e.Id == record.EmployeeId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        await connection.DeleteAsync(record).ConfigureAwait(false);

        // Unlike an employee or a department, an attendance day carries no
        // history of its own — it *is* the history — so removing one is a real
        // delete. The audit entry is what survives it.
        await _audit.WriteAsync(
            AuditActions.AttendanceRemoved,
            nameof(AttendanceRecord), record.Id, true,
            $"Removed the {record.Date:dd MMM yyyy} entry for " +
            $"{employee?.DisplayName ?? $"employee #{record.EmployeeId}"}: " +
            $"{record.TimeInDisplay}–{record.TimeOutDisplay}, {record.RegularHours:0.##} h.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return AttendanceSaveResult.Ok(record, $"{record.Date:dd MMM} removed.");
    }

    public async Task<AttendanceSaveResult> SetOvertimeApprovedAsync(int id, decimal hours, User performedBy)
    {
        // Overtime is money, and the requirement makes it subject to approval
        // (FR-022). Recording the hours is an attendance task; authorising them
        // is the approver's.
        if (!performedBy.Can(Permission.ApprovePayroll) && !performedBy.Can(Permission.ManageAttendance))
            return await RefuseAsync(performedBy, "approve overtime").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var record = await connection.Table<AttendanceRecord>()
            .Where(r => r.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (record is null)
            return AttendanceSaveResult.Fail("That entry no longer exists.");

        if (record.IsLocked)
            return AttendanceSaveResult.Fail("That day has been locked by a posted payroll run.");

        if (await LockedPeriodCoveringAsync(connection, record.EmployeeId, record.Date).ConfigureAwait(false) is { } lockedIn)
            return AttendanceSaveResult.Fail(PeriodLockedMessage(lockedIn));

        if (hours < 0m)
            return AttendanceSaveResult.Fail("Approved overtime cannot be negative.");

        // Approving more than was worked would invent hours. The rendered figure
        // comes from the punches and is the ceiling.
        if (hours > record.OvertimeHoursRendered)
        {
            return AttendanceSaveResult.Fail(
                $"Only {record.OvertimeHoursRendered:0.##} overtime hours were rendered on " +
                $"{record.Date:dd MMM}. Correct the time-out first if that is wrong.");
        }

        var employee = await connection.Table<Employee>()
            .Where(e => e.Id == record.EmployeeId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        var previous = record.OvertimeHoursApproved;

        record.OvertimeHoursApproved = Math.Round(hours, 2, MidpointRounding.AwayFromZero);
        record.UpdatedUtc = DateTime.UtcNow;

        await connection.UpdateAsync(record).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.OvertimeApproved,
            nameof(AttendanceRecord), record.Id, true,
            $"Overtime on {record.Date:dd MMM yyyy} for " +
            $"{employee?.DisplayName ?? $"employee #{record.EmployeeId}"} " +
            $"set from {previous:0.##} to {record.OvertimeHoursApproved:0.##} h " +
            $"of {record.OvertimeHoursRendered:0.##} rendered.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return AttendanceSaveResult.Ok(record,
            record.OvertimeHoursApproved == 0m
                ? $"Overtime on {record.Date:dd MMM} declined."
                : $"{record.OvertimeHoursApproved:0.##} overtime hours approved for {record.Date:dd MMM}.");
    }

    public async Task<GenerateResult> GeneratePeriodAsync(
        IReadOnlyList<Employee> employees, DateTime from, DateTime to, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageAttendance))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(AttendanceRecord), null, false,
                $"Role {performedBy.RoleDisplayName} is not permitted to generate a cut-off.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return new GenerateResult(false, "You do not have permission to perform this action.", 0, 0);
        }

        var start = from.Date;
        var end = to.Date;

        if (end < start)
            return new GenerateResult(false, "The end of the period falls before its start.", 0, 0);

        // A day that has not happened cannot be an absence. Generating into the
        // future would mark everyone absent for the rest of the cut-off.
        if (start > DateTime.Today)
            return new GenerateResult(false, "That period has not started yet.", 0, 0);

        if (end > DateTime.Today)
            end = DateTime.Today;

        if (employees.Count == 0)
            return new GenerateResult(false, "No employees are selected.", 0, 0);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var schedules = await LoadSchedulesAsync().ConfigureAwait(false);
        var calendar = await _holidays.GetCalendarAsync(start, end).ConfigureAwait(false);

        var created = 0;
        var existing = 0;
        var frozen = 0;

        var lockedPeriods = await connection.Table<PayPeriod>()
            .Where(p => p.Status == PayPeriodStatus.Locked)
            .ToListAsync()
            .ConfigureAwait(false);

        foreach (var employee in employees)
        {
            var already = (await GetRangeAsync(employee.Id, start, end).ConfigureAwait(false))
                .Select(r => r.Date.Date)
                .ToHashSet();

            var drafts = new List<AttendanceRecord>();

            for (var date = start; date <= end; date = date.AddDays(1))
            {
                if (already.Contains(date))
                {
                    existing++;
                    continue;
                }

                if (!employee.IsPayableOver(date, date))
                    continue;

                if (lockedPeriods.Any(p => Covers(p, employee.PayFrequency, date)))
                {
                    frozen++;
                    continue;
                }

                var draft = Draft(employee, date, schedules, calendar);
                draft.Source = AttendanceSource.Generated;
                draft.Remarks = "Generated with the cut-off; no punches recorded.";

                drafts.Add(draft);
            }

            if (drafts.Count == 0)
                continue;

            await connection.InsertAllAsync(drafts).ConfigureAwait(false);
            created += drafts.Count;
        }

        await _audit.WriteAsync(
            AuditActions.AttendanceGenerated,
            nameof(AttendanceRecord), null, true,
            $"Generated {created} day(s) over {start:dd MMM yyyy} – {end:dd MMM yyyy} " +
            $"for {employees.Count} employee(s); {existing} already recorded.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        var frozenNote = frozen == 0
            ? string.Empty
            : $" {frozen} day(s) were left alone because their pay period is locked.";

        return new GenerateResult(true,
            (created == 0
                ? "Every day in that period was already recorded."
                : $"{created} day(s) added. Rest days and holidays are classified; " +
                  "the rest stand as absences until punches are entered.") + frozenNote,
            created, existing);
    }

    // ----------------------------------------------------- period lock

    /// <summary>
    /// A pay period locked on Payroll Setup freezes the attendance inside its
    /// cut-off, so a run computed against it cannot be undermined by a later
    /// correction. It applies to the employees paid on that period's
    /// frequency; a weekly period says nothing about a semi-monthly employee.
    /// A closed period is not checked here — posting has already locked the
    /// days of everyone it paid, and anyone it did not pay may still need
    /// their days recorded for a run of their own.
    /// </summary>
    private static async Task<PayPeriod?> LockedPeriodCoveringAsync(
        SQLite.SQLiteAsyncConnection connection, Employee employee, DateTime date)
    {
        var day = date.Date;

        var locked = await connection.Table<PayPeriod>()
            .Where(p => p.Status == PayPeriodStatus.Locked && p.CutOffStart <= day && p.CutOffEnd >= day)
            .ToListAsync()
            .ConfigureAwait(false);

        return locked.FirstOrDefault(p => Covers(p, employee.PayFrequency, day));
    }

    private static async Task<PayPeriod?> LockedPeriodCoveringAsync(
        SQLite.SQLiteAsyncConnection connection, int employeeId, DateTime date)
    {
        var employee = await connection.Table<Employee>()
            .Where(e => e.Id == employeeId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        return employee is null
            ? null
            : await LockedPeriodCoveringAsync(connection, employee, date).ConfigureAwait(false);
    }

    private static bool Covers(PayPeriod period, PayFrequency frequency, DateTime date) =>
        period.Frequency == frequency &&
        period.CutOffStart.Date <= date.Date &&
        period.CutOffEnd.Date >= date.Date;

    private static string PeriodLockedMessage(PayPeriod period) =>
        $"{period.Code} is locked, which freezes the attendance inside its cut-off " +
        $"({period.CutOffStart:dd MMM} – {period.CutOffEnd:dd MMM yyyy}). " +
        "Reopen it on Payroll Setup → Pay calendar to make a correction.";

    public async Task<int> LockRangeAsync(
        IReadOnlyList<int> employeeIds, DateTime from, DateTime to, User performedBy)
    {
        var start = from.Date;
        var end = to.Date;
        var locked = 0;

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        foreach (var employeeId in employeeIds)
        {
            var days = await GetRangeAsync(employeeId, start, end).ConfigureAwait(false);

            foreach (var day in days.Where(d => !d.IsLocked))
            {
                day.IsLocked = true;
                day.UpdatedUtc = DateTime.UtcNow;

                await connection.UpdateAsync(day).ConfigureAwait(false);
                locked++;
            }
        }

        if (locked > 0)
        {
            await _audit.WriteAsync(
                AuditActions.AttendanceLocked,
                nameof(AttendanceRecord), null, true,
                $"Locked {locked} attendance day(s) over {start:dd MMM yyyy} – {end:dd MMM yyyy} " +
                $"for {employeeIds.Count} employee(s) following a posted payroll run.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);
        }

        return locked;
    }

    // ------------------------------------------------------------ FR-027

    public async Task<AttendanceSummary> GetSummaryAsync(Employee employee, DateTime from, DateTime to)
    {
        var sheet = await BuildTimesheetAsync(employee, from, to).ConfigureAwait(false);
        return Summarise(employee, from, to, sheet);
    }

    public async Task<IReadOnlyList<AttendanceSummary>> GetSummariesAsync(
        IReadOnlyList<Employee> employees, DateTime from, DateTime to)
    {
        var start = from.Date;
        var end = to.Date;

        if (employees.Count == 0 || end < start)
            return Array.Empty<AttendanceSummary>();

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        // One read for the whole period rather than one per employee: a
        // fifty-person cut-off is a hundred queries otherwise (NFR-001).
        var rows = await connection.Table<AttendanceRecord>()
            .Where(r => r.Date >= start && r.Date <= end)
            .ToListAsync()
            .ConfigureAwait(false);

        var byEmployee = rows
            .GroupBy(r => r.EmployeeId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Date.Date));

        var schedules = await LoadSchedulesAsync().ConfigureAwait(false);
        var calendar = await _holidays.GetCalendarAsync(start, end).ConfigureAwait(false);

        var summaries = new List<AttendanceSummary>(employees.Count);

        foreach (var employee in employees)
        {
            byEmployee.TryGetValue(employee.Id, out var saved);

            var sheet = new List<AttendanceRecord>();

            for (var date = start; date <= end; date = date.AddDays(1))
            {
                if (!employee.IsPayableOver(date, date))
                    continue;

                sheet.Add(saved is not null && saved.TryGetValue(date, out var existing)
                    ? existing
                    : Draft(employee, date, schedules, calendar));
            }

            summaries.Add(Summarise(employee, start, end, sheet));
        }

        return summaries;
    }

    /// <summary>
    /// Reduces a timesheet to its totals. Pure, so the view model can summarise
    /// the sheet it already holds instead of reading the range a second time.
    ///
    /// <para>A day that exists only as a draft counts as unrecorded rather than
    /// as an absence: nobody has said the employee did not turn up, only that
    /// nothing was entered. Payroll needs to be able to tell those apart.</para>
    /// </summary>
    public static AttendanceSummary Summarise(
        Employee employee, DateTime from, DateTime to, IEnumerable<AttendanceRecord> days)
    {
        var sheet = days.ToList();
        var recorded = sheet.Where(d => d.Id != 0).ToList();
        var drafts = sheet.Count - recorded.Count;

        return new AttendanceSummary(
            Employee: employee,
            From: from.Date,
            To: to.Date,
            DaysRecorded: recorded.Count,
            DaysPresent: recorded.Count(d => d.Status == AttendanceStatus.Present),
            DaysHalf: recorded.Count(d => d.Status == AttendanceStatus.HalfDay),
            DaysAbsent: recorded.Count(d => d.Status == AttendanceStatus.Absent),
            DaysOnLeave: recorded.Count(d => d.Status == AttendanceStatus.OnLeave),
            DaysHoliday: recorded.Count(d => d.Status == AttendanceStatus.Holiday),
            DaysRestDay: recorded.Count(d => d.Status == AttendanceStatus.RestDay),
            DaysSuspended: recorded.Count(d => d.Status == AttendanceStatus.Suspended),
            DaysUnrecorded: drafts,
            RegularHours: recorded.Sum(d => d.RegularHours),
            OvertimeRendered: recorded.Sum(d => d.OvertimeHoursRendered),
            OvertimeApproved: recorded.Sum(d => d.OvertimeHoursApproved),
            NightDifferentialHours: recorded.Sum(d => d.NightDifferentialHours),
            LateMinutes: recorded.Sum(d => d.LateMinutes),
            UndertimeMinutes: recorded.Sum(d => d.UndertimeMinutes));
    }

    // ----------------------------------------------------------- internals

    /// <summary>
    /// A day nobody has recorded yet, classified against the employee's schedule
    /// and the holiday calendar. <c>Id == 0</c> is what marks it as a draft.
    /// </summary>
    private static AttendanceRecord Draft(
        Employee employee,
        DateTime date,
        IReadOnlyDictionary<int, WorkSchedule> schedules,
        HolidayCalendar calendar)
    {
        var schedule = ScheduleFor(employee, schedules);
        var holiday = calendar.On(date);

        var record = new AttendanceRecord
        {
            EmployeeId = employee.Id,
            Date = date.Date,
            IsRestDay = AttendanceCalculator.IsRestDayFor(schedule, date),
            HolidayType = holiday?.Type ?? HolidayType.None,
            HolidayName = holiday?.Name ?? string.Empty,
            Source = AttendanceSource.Manual
        };

        AttendanceCalculator.Apply(record, schedule);

        return record;
    }

    private async Task<IReadOnlyDictionary<int, WorkSchedule>> LoadSchedulesAsync()
    {
        // Retired schedules are included: an employee should not be on one, but
        // if they are, measuring them against nothing would silently turn every
        // day into a rest day.
        var all = await _organization.GetWorkSchedulesAsync(includeInactive: true).ConfigureAwait(false);
        return all.ToDictionary(s => s.Id);
    }

    private async Task<WorkSchedule?> ScheduleForAsync(Employee employee)
    {
        if (employee.WorkScheduleId is not { } id)
            return null;

        var schedules = await LoadSchedulesAsync().ConfigureAwait(false);
        return schedules.TryGetValue(id, out var schedule) ? schedule : null;
    }

    private static WorkSchedule? ScheduleFor(
        Employee employee, IReadOnlyDictionary<int, WorkSchedule> schedules) =>
        employee.WorkScheduleId is { } id && schedules.TryGetValue(id, out var schedule)
            ? schedule
            : null;

    /// <summary>
    /// FR-020, NFR-023. Everything that would make the computation meaningless,
    /// reported per field so the dialog can put each message beside its input.
    /// </summary>
    private static IReadOnlyList<FieldError> Validate(AttendanceRecord record, Employee employee)
    {
        var errors = new List<FieldError>();

        if (!employee.IsPayableOver(record.Date, record.Date))
        {
            errors.Add(new FieldError(nameof(AttendanceRecord.Date),
                employee.SeparationDate is { } separated && record.Date > separated
                    ? $"{employee.DisplayName} was separated on {separated:dd MMM yyyy}."
                    : $"{employee.DisplayName} was not employed on {record.Date:dd MMM yyyy}."));
        }

        if (record.Date > DateTime.Today && record.HasPunches)
        {
            errors.Add(new FieldError(nameof(AttendanceRecord.Date),
                "That date is in the future, so there are no punches to record for it."));
        }

        foreach (var (value, field, label) in new[]
                 {
                     (record.TimeInMinutes, nameof(AttendanceRecord.TimeInMinutes), "Time in"),
                     (record.TimeOutMinutes, nameof(AttendanceRecord.TimeOutMinutes), "Time out"),
                     (record.BreakOutMinutes, nameof(AttendanceRecord.BreakOutMinutes), "Break out"),
                     (record.BreakInMinutes, nameof(AttendanceRecord.BreakInMinutes), "Break in")
                 })
        {
            if (value is { } minutes && (minutes < 0 || minutes >= AttendanceCalculator.MinutesPerDay))
                errors.Add(new FieldError(field, $"{label} must be a time of day."));
        }

        // A time-out on its own cannot be measured against anything, and a
        // time-in on its own is a day still in progress rather than an error.
        if (record.TimeOutMinutes.HasValue && !record.TimeInMinutes.HasValue)
        {
            errors.Add(new FieldError(nameof(AttendanceRecord.TimeInMinutes),
                "Enter the time in as well — a time out on its own cannot be measured."));
        }

        if (record.BreakOutMinutes.HasValue != record.BreakInMinutes.HasValue)
        {
            errors.Add(new FieldError(nameof(AttendanceRecord.BreakInMinutes),
                "Enter both ends of the break, or neither and let the schedule's break apply."));
        }

        if (record.TimeInMinutes is { } start && record.TimeOutMinutes is { } end)
        {
            var span = end <= start ? end + AttendanceCalculator.MinutesPerDay - start : end - start;

            if (span == 0)
            {
                errors.Add(new FieldError(nameof(AttendanceRecord.TimeOutMinutes),
                    "Time out is the same as time in, so no hours were worked."));
            }
            else if (span > MaximumShiftMinutes)
            {
                errors.Add(new FieldError(nameof(AttendanceRecord.TimeOutMinutes),
                    $"That is a {span / 60.0:0.#} hour shift. Check the time out — a shift longer than " +
                    $"{MaximumShiftMinutes / 60} hours is almost always a typing error."));
            }

            if (record.BreakOutMinutes is { } breakOut && record.BreakInMinutes is { } breakIn)
            {
                var breakStart = breakOut < start ? breakOut + AttendanceCalculator.MinutesPerDay : breakOut;
                var breakEnd = breakIn <= breakOut ? breakIn + AttendanceCalculator.MinutesPerDay : breakIn;

                if (breakStart < start || breakEnd > start + span)
                {
                    errors.Add(new FieldError(nameof(AttendanceRecord.BreakOutMinutes),
                        "The break falls outside the shift that was worked."));
                }
            }
        }

        if (record.Remarks.Length > 250)
        {
            errors.Add(new FieldError(nameof(AttendanceRecord.Remarks),
                "Keep the reason under 250 characters."));
        }

        return errors;
    }

    /// <summary>
    /// The audit detail. Written as the figures rather than as "updated", so the
    /// log answers what a day was changed *to* without a second lookup (FR-091).
    /// </summary>
    private static string Describe(Employee employee, AttendanceRecord record, bool isNew)
    {
        var what = isNew ? "Recorded" : "Corrected";

        var detail =
            $"{what} {record.Date:dd MMM yyyy} for {employee.EmployeeNumber} {employee.DisplayName}: " +
            $"{record.TimeInDisplay}–{record.TimeOutDisplay}, {record.DayTypeDisplay}, " +
            $"{record.RegularHours:0.##} h regular, {record.OvertimeHoursRendered:0.##} h OT rendered, " +
            $"{record.NightDifferentialHours:0.##} h night, " +
            $"{record.LateMinutes} min late, {record.UndertimeMinutes} min undertime, " +
            $"status {record.StatusDisplay}.";

        if (record.Remarks.Length > 0)
            detail += $" Reason: {record.Remarks}";

        return detail.Length <= 500 ? detail : detail[..497] + "...";
    }

    private async Task<AttendanceSaveResult> RefuseAsync(User performedBy, string action)
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, nameof(AttendanceRecord), null, false,
            $"Role {performedBy.RoleDisplayName} is not permitted to {action}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return AttendanceSaveResult.Fail("You do not have permission to perform this action.");
    }
}
