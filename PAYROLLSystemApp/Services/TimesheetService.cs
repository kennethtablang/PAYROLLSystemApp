using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>One employee on a sheet: who they are, what is keyed, and at what rate.</summary>
public sealed record TimesheetLine(Employee Employee, PeriodTimesheet Sheet, decimal DailyRate)
{
    public string EmployeeNumber => Employee.EmployeeNumber;

    public string Name => Employee.FullName;

    /// <summary>True once anything has been keyed against this employee.</summary>
    public bool IsEntered => Sheet.HasFigures;
}

/// <summary>A client's sheet for one cut-off: the roster of one post, and its rate.</summary>
public sealed record TimesheetSheet(
    Detachment Detachment,
    decimal DailyRate,
    IReadOnlyList<TimesheetLine> Lines)
{
    public bool HasRate => DailyRate > 0m;

    public int EnteredCount => Lines.Count(l => l.IsEntered);

    public string Banner => $"{Detachment.Code} ({DailyRate:N2} per day)";

    /// <summary>The sheet is done when every name on it has figures.</summary>
    public bool IsComplete => Lines.Count > 0 && EnteredCount == Lines.Count;
}

public interface ITimesheetService
{
    /// <summary>
    /// The sheets a run covers, one per detachment, each banded exactly as the
    /// client's paper is.
    /// </summary>
    Task<IReadOnlyList<TimesheetSheet>> GetForRunAsync(PayrollRun run);

    /// <summary>Saves one keyed row. Creating it on first key, updating it after.</summary>
    Task<SaveResult<PeriodTimesheet>> SaveAsync(PeriodTimesheet sheet, User performedBy);

    /// <summary>Saves every changed row on a sheet in one pass, for the grid's Save.</summary>
    Task<SaveResult<IReadOnlyList<PeriodTimesheet>>> SaveManyAsync(
        IReadOnlyList<PeriodTimesheet> sheets, User performedBy);

    /// <summary>Clears one employee's keyed figures for a run.</summary>
    Task<SaveResult<PeriodTimesheet>> ClearAsync(int runId, int employeeId, User performedBy);

    /// <summary>Every keyed row for a run, for the payroll engine.</summary>
    Task<IReadOnlyDictionary<int, PeriodTimesheet>> GetByEmployeeAsync(int runId);
}

/// <summary>
/// The client's timesheet, as accounting keys it.
///
/// <para><b>Why this exists next to the attendance module.</b> Detachment staff
/// stand at a client's post, and the client keeps the logbook. What reaches
/// accounting is a printed sheet — one client, one cut-off, banded by detachment
/// code, with a count of days and three columns of already-classified overtime
/// hours against each name. There are no punch times on it and no dates. The
/// attendance module models a day; this models the sheet, and neither can be
/// derived from the other.</para>
///
/// <para><b>The sheet is filed against a run, not a pay period.</b> A cut-off
/// re-run after a correction gets its own sheet, so figures typed for a
/// discarded run can never be inherited by the one that replaces it.</para>
///
/// <para>Every write is checked against the caller's permission and audited
/// (FR-091). Accounting holds <see cref="Permission.RunPayroll"/>, which is
/// what keying a cut-off's time requires.</para>
/// </summary>
public sealed class TimesheetService : ITimesheetService
{
    private readonly PayrollDatabase _database;
    private readonly IDetachmentService _detachments;
    private readonly IAuditService _audit;

    public TimesheetService(
        PayrollDatabase database, IDetachmentService detachments, IAuditService audit)
    {
        _database = database;
        _detachments = detachments;
        _audit = audit;
    }

    public async Task<IReadOnlyList<TimesheetSheet>> GetForRunAsync(PayrollRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var runId = run.Id;

        var keyed = (await connection.Table<PeriodTimesheet>()
            .Where(t => t.RunId == runId)
            .ToListAsync()
            .ConfigureAwait(false))
            .ToDictionary(t => t.EmployeeId);

        // The rate table is read at the run's pay date, the same instant the
        // engine will read it, so the figure the keyer prices against is the
        // figure they will be paid at.
        var rates = await _detachments.GetRateTableAsync(run.PayDate).ConfigureAwait(false);

        var detachments = await _detachments.GetAllAsync(includeInactive: true).ConfigureAwait(false);

        // Only the people on the run. The engine pays the run's roster and
        // nobody else, so a row offered for anyone outside it would be keyed,
        // saved, and silently never paid.
        var members = await RosterAsync(connection, runId).ConfigureAwait(false);

        var employees = (await connection.Table<Employee>().ToListAsync().ConfigureAwait(false))
            .Where(e => members.Contains(e.Id) && e.DetachmentId.HasValue)
            .ToList();

        var byDetachment = employees
            .GroupBy(e => e.DetachmentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var sheets = new List<TimesheetSheet>();

        foreach (var detachment in detachments)
        {
            if (!byDetachment.TryGetValue(detachment.Id, out var roster) || roster.Count == 0)
                continue;

            var daily = rates.DailyRateFor(detachment.Id) ?? 0m;

            var lines = roster
                .OrderBy(e => e.LastName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.FirstName, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new TimesheetLine(
                    e,
                    keyed.TryGetValue(e.Id, out var found)
                        ? found
                        : new PeriodTimesheet
                        {
                            RunId = run.Id,
                            EmployeeId = e.Id,
                            DetachmentId = detachment.Id,
                            RateWhenKeyed = daily
                        },
                    daily))
                .ToList();

            sheets.Add(new TimesheetSheet(detachment, daily, lines));
        }

        return sheets
            .OrderBy(s => s.Detachment.ClientDisplay, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Detachment.Code, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<SaveResult<PeriodTimesheet>> SaveAsync(PeriodTimesheet sheet, User performedBy)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (!performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<PeriodTimesheet>(performedBy, "key a timesheet").ConfigureAwait(false);

        if (Validate(sheet) is { } error)
            return SaveResult<PeriodTimesheet>.Fail(error);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        if (await CheckRunAsync(connection, sheet.RunId, [sheet.EmployeeId]).ConfigureAwait(false) is { } refused)
            return SaveResult<PeriodTimesheet>.Fail(refused);

        var saved = await UpsertAsync(connection, sheet, performedBy).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.TimesheetKeyed, nameof(PeriodTimesheet), saved.Id, true,
            Describe(saved), performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PeriodTimesheet>.Ok(saved, "Timesheet saved.");
    }

    public async Task<SaveResult<IReadOnlyList<PeriodTimesheet>>> SaveManyAsync(
        IReadOnlyList<PeriodTimesheet> sheets, User performedBy)
    {
        ArgumentNullException.ThrowIfNull(sheets);

        if (!performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<IReadOnlyList<PeriodTimesheet>>(performedBy, "key a timesheet")
                .ConfigureAwait(false);

        foreach (var sheet in sheets)
        {
            if (Validate(sheet) is { } error)
                return SaveResult<IReadOnlyList<PeriodTimesheet>>.Fail(error);
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        // Checked for every run the batch touches before anything is written,
        // so a refused row cannot leave half a sheet saved.
        foreach (var run in sheets.GroupBy(s => s.RunId))
        {
            var ids = run.Select(s => s.EmployeeId).ToList();

            if (await CheckRunAsync(connection, run.Key, ids).ConfigureAwait(false) is { } refused)
                return SaveResult<IReadOnlyList<PeriodTimesheet>>.Fail(refused);
        }

        foreach (var sheet in sheets)
            await UpsertAsync(connection, sheet, performedBy).ConfigureAwait(false);

        // One audit entry for the sheet rather than one per name: the unit of
        // work is the sheet, and sixty entries would bury the log.
        if (sheets.Count > 0)
        {
            await _audit.WriteAsync(AuditActions.TimesheetKeyed, nameof(PeriodTimesheet), null, true,
                $"Keyed {sheets.Count} timesheet row(s) on run {sheets[0].RunId}.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);
        }

        return SaveResult<IReadOnlyList<PeriodTimesheet>>.Ok(sheets,
            sheets.Count == 1 ? "1 row saved." : $"{sheets.Count} rows saved.");
    }

    public async Task<SaveResult<PeriodTimesheet>> ClearAsync(
        int runId, int employeeId, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<PeriodTimesheet>(performedBy, "clear a timesheet").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var existing = (await connection.Table<PeriodTimesheet>()
            .Where(t => t.RunId == runId && t.EmployeeId == employeeId)
            .ToListAsync()
            .ConfigureAwait(false))
            .FirstOrDefault();

        if (existing is null)
            return SaveResult<PeriodTimesheet>.Fail("Nothing is keyed for that employee on this run.");

        if (await CheckRunAsync(connection, runId, [employeeId]).ConfigureAwait(false) is { } refused)
            return SaveResult<PeriodTimesheet>.Fail(refused);

        await connection.DeleteAsync(existing).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.TimesheetCleared, nameof(PeriodTimesheet), existing.Id, true,
            $"Cleared the timesheet for employee {employeeId} on run {runId}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PeriodTimesheet>.Ok(existing, "Row cleared.");
    }

    public async Task<IReadOnlyDictionary<int, PeriodTimesheet>> GetByEmployeeAsync(int runId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<PeriodTimesheet>()
            .Where(t => t.RunId == runId)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows
            .GroupBy(t => t.EmployeeId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.Id).First());
    }

    // =====================================================================
    // Internals
    // =====================================================================

    /// <summary>
    /// Nothing negative, and nothing beyond what a cut-off can physically hold.
    /// The ceilings are deliberately generous — they catch a slipped decimal
    /// point, not an unusual fortnight.
    /// </summary>
    private static string? Validate(PeriodTimesheet sheet)
    {
        if (sheet.RunId <= 0)
            return "The timesheet is not attached to a run.";

        if (sheet.EmployeeId <= 0)
            return "The timesheet is not attached to an employee.";

        if (sheet.Days < 0m || sheet.RegularOtHours < 0m || sheet.SpecialHolidayOtHours < 0m ||
            sheet.LegalHolidayOtHours < 0m || sheet.NightShiftHours < 0m ||
            sheet.CompanyLoan < 0m || sheet.LateAmount < 0m)
        {
            return "A timesheet figure cannot be negative.";
        }

        if (sheet.Days > 31m)
            return "A cut-off cannot hold more than 31 days.";

        if (sheet.RegularOtHours > 400m || sheet.SpecialHolidayOtHours > 400m ||
            sheet.LegalHolidayOtHours > 400m || sheet.NightShiftHours > 400m)
        {
            return "An overtime figure above 400 hours in one cut-off looks like a mis-key.";
        }

        return null;
    }

    /// <summary>
    /// A sheet may only be keyed against a draft run, and only for someone on
    /// it. Once a run is submitted the approver is looking at figures computed
    /// from these rows, so they must not move underneath them — the Timesheets
    /// screen lists draft runs only, but that is presentation, not the rule.
    /// </summary>
    private static async Task<string?> CheckRunAsync(
        SQLite.SQLiteAsyncConnection connection, int runId, IReadOnlyCollection<int> employeeIds)
    {
        var run = await connection.Table<PayrollRun>()
            .Where(r => r.Id == runId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (run is null)
            return "That payroll run no longer exists.";

        if (run.Status != PayrollRunStatus.Draft)
        {
            return $"{run.ReferenceNumber} is {PayrollEnumNames.Display(run.Status).ToLowerInvariant()}. " +
                   "Only a draft run can be keyed — return it to draft first.";
        }

        if (run.RunType is PayrollRunType.ThirteenthMonth or PayrollRunType.Adjustment)
        {
            return $"{run.ReferenceNumber} is a {PayrollEnumNames.Display(run.RunType).ToLowerInvariant()} run, " +
                   "which pays no time — a sheet keyed against it would never be paid.";
        }

        var members = await RosterAsync(connection, runId).ConfigureAwait(false);
        var outsiders = employeeIds.Where(id => !members.Contains(id)).Distinct().ToList();

        if (outsiders.Count > 0)
        {
            return outsiders.Count == 1
                ? $"That employee is not on {run.ReferenceNumber}, so nothing keyed for them would be paid."
                : $"{outsiders.Count} of these employees are not on {run.ReferenceNumber}, " +
                  "so nothing keyed for them would be paid.";
        }

        return null;
    }

    /// <summary>The run's roster, held as its payslip rows from the moment it is created.</summary>
    private static async Task<HashSet<int>> RosterAsync(SQLite.SQLiteAsyncConnection connection, int runId) =>
        (await connection.Table<Payslip>()
            .Where(p => p.PayrollRunId == runId)
            .ToListAsync()
            .ConfigureAwait(false))
        .Select(p => p.EmployeeId)
        .ToHashSet();

    private static async Task<PeriodTimesheet> UpsertAsync(
        SQLite.SQLiteAsyncConnection connection, PeriodTimesheet sheet, User performedBy)
    {
        var runId = sheet.RunId;
        var employeeId = sheet.EmployeeId;

        var existing = (await connection.Table<PeriodTimesheet>()
            .Where(t => t.RunId == runId && t.EmployeeId == employeeId)
            .ToListAsync()
            .ConfigureAwait(false))
            .FirstOrDefault();

        sheet.Note = (sheet.Note ?? string.Empty).Trim();
        sheet.RecordedBy = performedBy.Username;
        sheet.UpdatedUtc = DateTime.UtcNow;

        if (existing is null)
        {
            sheet.Id = 0;
            sheet.CreatedUtc = DateTime.UtcNow;

            await connection.InsertAsync(sheet).ConfigureAwait(false);
            return sheet;
        }

        sheet.Id = existing.Id;
        sheet.CreatedUtc = existing.CreatedUtc;

        await connection.UpdateAsync(sheet).ConfigureAwait(false);
        return sheet;
    }

    private static string Describe(PeriodTimesheet sheet) =>
        $"Keyed employee {sheet.EmployeeId} on run {sheet.RunId}: " +
        $"{sheet.Days:0.##} day(s), OT {sheet.RegularOtHours:0.##}/{sheet.SpecialHolidayOtHours:0.##}/" +
        $"{sheet.LegalHolidayOtHours:0.##} h, night {sheet.NightShiftHours:0.##} h, " +
        $"loan {sheet.CompanyLoan:N2}, late {sheet.LateAmount:N2}.";

    private async Task<SaveResult<T>> RefuseAsync<T>(User performedBy, string action)
        where T : class
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, nameof(PeriodTimesheet), null, false,
            $"Role {performedBy.RoleDisplayName} is not permitted to {action}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<T>.Fail("You do not have permission to perform this action.");
    }
}
