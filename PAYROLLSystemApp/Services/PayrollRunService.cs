using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>Which runs a list should show.</summary>
public sealed record PayrollRunQuery(int? Year = null, PayrollRunStatus? Status = null);

/// <summary>
/// An employee offered for a new run, with the reason they cannot be included
/// where there is one (FR-060).
/// </summary>
public sealed record RunCandidate(Employee Employee, bool IsAvailable, string Reason)
{
    public int Id => Employee.Id;

    public string FullName => Employee.FullName;

    public string EmployeeNumber => Employee.EmployeeNumber;
}

/// <summary>What a calculation produced, in the terms the screen reports it.</summary>
public sealed record CalculationResult(
    bool Succeeded,
    string Message,
    int PayslipCount,
    int FlaggedCount,
    IReadOnlyList<string> Blockers)
{
    public bool HasBlockers => Blockers.Count > 0;
}

public interface IPayrollRunService
{
    Task<IReadOnlyList<PayrollRun>> GetRunsAsync(PayrollRunQuery query);

    Task<PayrollRun?> GetRunAsync(int id);

    Task<IReadOnlyList<Payslip>> GetPayslipsAsync(int runId);

    Task<IReadOnlyList<PayslipLine>> GetLinesAsync(int payslipId);

    /// <summary>FR-050, FR-060. Who may be put on a run for this period.</summary>
    Task<IReadOnlyList<RunCandidate>> GetCandidatesAsync(int payPeriodId, PayrollRunType runType);

    Task<SaveResult<PayrollRun>> CreateRunAsync(
        int payPeriodId, PayrollRunType runType, IReadOnlyList<int> employeeIds,
        string remarks, User performedBy);

    /// <summary>FR-059. Throws the payslips away and rebuilds them from current data.</summary>
    Task<CalculationResult> CalculateAsync(int runId, User performedBy);

    Task<SaveResult<PayrollRun>> SubmitForApprovalAsync(int runId, User performedBy);

    Task<SaveResult<PayrollRun>> ReturnToDraftAsync(int runId, string reason, User performedBy);

    Task<SaveResult<PayrollRun>> ApproveAsync(int runId, User performedBy);

    Task<SaveResult<PayrollRun>> PostAsync(int runId, User performedBy);

    Task<SaveResult<PayrollRun>> DiscardAsync(int runId, string reason, User performedBy);

    // --------------------------------------------------------- FR-057

    Task<IReadOnlyList<PayrollAdjustment>> GetAdjustmentsAsync(int runId);

    Task<SaveResult<PayrollAdjustment>> SaveAdjustmentAsync(
        PayrollAdjustment adjustment, User performedBy);

    Task<SaveResult<PayrollAdjustment>> DeleteAdjustmentAsync(int id, User performedBy);

    // --------------------------------------------------------- FR-055

    Task<IReadOnlyList<EmployeeLoan>> GetLoansAsync(int? employeeId = null, bool includeClosed = false);

    Task<SaveResult<EmployeeLoan>> SaveLoanAsync(EmployeeLoan loan, User performedBy);

    Task<SaveResult<EmployeeLoan>> SetLoanStatusAsync(int id, LoanStatus status, User performedBy);

    Task<IReadOnlyList<LoanPayment>> GetLoanPaymentsAsync(int loanId);

    // ------------------------------------- standing deductions (FR-055)

    Task<IReadOnlyList<EmployeeDeduction>> GetStandingDeductionsAsync(
        int? employeeId = null, bool includeStopped = false);

    Task<SaveResult<EmployeeDeduction>> SaveStandingDeductionAsync(
        EmployeeDeduction deduction, User performedBy);

    Task<SaveResult<EmployeeDeduction>> SetStandingDeductionActiveAsync(
        int id, bool isActive, User performedBy);
}

/// <summary>
/// Section 2.6 (FR-050 – FR-063). Creates payroll runs, computes them, moves
/// them through their states, and posts them.
///
/// <para><b>This service does the I/O; <see cref="PayrollCalculator"/> does the
/// arithmetic.</b> Everything a run needs is loaded once into a
/// <see cref="PayrollContext"/> and handed to a pure function, so a run of three
/// hundred employees is one set of reads rather than three hundred (NFR-002),
/// and so the arithmetic can be reasoned about on its own.</para>
///
/// <para><b>Calculate is idempotent; post is not.</b> Recalculating throws the
/// payslips away and rebuilds them from current data, which is safe precisely
/// because nothing outside the run has changed yet (FR-059). Posting is the step
/// that reaches outside it — attendance is locked, loan balances move — and
/// there is no way back from it.</para>
/// </summary>
public sealed class PayrollRunService : IPayrollRunService
{
    private readonly PayrollDatabase _database;
    private readonly IPayrollConfigService _config;
    private readonly IStatutoryTableService _statutory;
    private readonly IOrganizationService _organization;
    private readonly IEmployeeService _employees;
    private readonly IDetachmentService _detachments;
    private readonly IAttendanceService _attendance;
    private readonly ITimesheetService _timesheets;
    private readonly ILeaveService _leave;
    private readonly IAuditService _audit;

    public PayrollRunService(
        PayrollDatabase database,
        IPayrollConfigService config,
        IStatutoryTableService statutory,
        IOrganizationService organization,
        IEmployeeService employees,
        IDetachmentService detachments,
        IAttendanceService attendance,
        ITimesheetService timesheets,
        ILeaveService leave,
        IAuditService audit)
    {
        _database = database;
        _config = config;
        _statutory = statutory;
        _organization = organization;
        _employees = employees;
        _detachments = detachments;
        _attendance = attendance;
        _timesheets = timesheets;
        _leave = leave;
        _audit = audit;
    }

    // =====================================================================
    // Reading
    // =====================================================================

    public async Task<IReadOnlyList<PayrollRun>> GetRunsAsync(PayrollRunQuery query)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false);

        return rows
            .Where(r => query.Year is not { } year || r.PeriodEnd.Year == year)
            .Where(r => query.Status is not { } status || r.Status == status)
            .OrderByDescending(r => r.PayDate)
            .ThenByDescending(r => r.Id)
            .ToList();
    }

    public async Task<PayrollRun?> GetRunAsync(int id)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        return await connection.Table<PayrollRun>()
            .Where(r => r.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Payslip>> GetPayslipsAsync(int runId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<Payslip>()
            .Where(p => p.PayrollRunId == runId)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.OrderBy(p => p.EmployeeName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<PayslipLine>> GetLinesAsync(int payslipId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<PayslipLine>()
            .Where(l => l.PayslipId == payslipId)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.OrderBy(l => l.Kind).ThenBy(l => l.Sequence).ToList();
    }

    // =====================================================================
    // FR-050, FR-060 — creating a run
    // =====================================================================

    public async Task<IReadOnlyList<RunCandidate>> GetCandidatesAsync(
        int payPeriodId, PayrollRunType runType)
    {
        var period = await FindPeriodAsync(payPeriodId).ConfigureAwait(false);
        if (period is null)
            return [];

        var employees = await _employees.GetAllAsync().ConfigureAwait(false);
        var taken = await EmployeesAlreadyOnAsync(payPeriodId, runType, excludeRunId: null).ConfigureAwait(false);

        var candidates = new List<RunCandidate>();

        foreach (var employee in employees
            .Where(e => !e.IsArchived)
            .OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase))
        {
            // FR-060. The same employee cannot be paid twice for the same period
            // and the same kind of run — the single most expensive mistake this
            // module can make.
            if (taken.Contains(employee.Id))
            {
                candidates.Add(new RunCandidate(employee, false,
                    "Already on another run for this period."));
                continue;
            }

            if (runType == PayrollRunType.FinalPay)
            {
                candidates.Add(employee.SeparationDate is null
                    ? new RunCandidate(employee, false, "Final pay is only for a separated employee.")
                    : new RunCandidate(employee, true, string.Empty));

                continue;
            }

            if (!employee.IsPayableOver(period.PeriodStart, period.PeriodEnd))
            {
                candidates.Add(new RunCandidate(employee, false,
                    employee.SeparationDate is { } left
                        ? $"Separated {left:dd MMM yyyy}, before this period."
                        : "Not employed during this period."));

                continue;
            }

            candidates.Add(new RunCandidate(employee, true, string.Empty));
        }

        return candidates;
    }

    public async Task<SaveResult<PayrollRun>> CreateRunAsync(
        int payPeriodId, PayrollRunType runType, IReadOnlyList<int> employeeIds,
        string remarks, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<PayrollRun>(performedBy, "create a payroll run").ConfigureAwait(false);

        if (employeeIds.Count == 0)
            return SaveResult<PayrollRun>.Fail("Select at least one employee for the run.");

        var period = await FindPeriodAsync(payPeriodId).ConfigureAwait(false);
        if (period is null)
            return SaveResult<PayrollRun>.Fail("That pay period is no longer in the calendar.");

        if (period.Status == PayPeriodStatus.Closed)
            return SaveResult<PayrollRun>.Fail($"{period.Code} is closed. Reopen it to run payroll against it.");

        var taken = await EmployeesAlreadyOnAsync(payPeriodId, runType, excludeRunId: null).ConfigureAwait(false);
        var clashing = employeeIds.Where(taken.Contains).ToList();

        if (clashing.Count > 0)
        {
            return SaveResult<PayrollRun>.Fail(
                $"{clashing.Count} of the selected employees are already on a " +
                $"{PayrollEnumNames.Display(runType).ToLowerInvariant()} run for {period.Code}. " +
                "Nothing was created — an employee is never paid twice for the same period.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var run = new PayrollRun
        {
            ReferenceNumber = await NextReferenceAsync(period, runType).ConfigureAwait(false),
            PayPeriodId = period.Id,
            PeriodCode = period.Code,
            PeriodName = period.Name,
            PeriodStart = period.PeriodStart,
            PeriodEnd = period.PeriodEnd,
            CutOffStart = period.CutOffStart,
            CutOffEnd = period.CutOffEnd,
            PayDate = period.PayDate,
            Frequency = period.Frequency,
            SequenceInMonth = period.SequenceInMonth,
            RunsInMonth = period.RunsInMonth,
            RunType = runType,
            Status = PayrollRunStatus.Draft,
            EmployeeCount = employeeIds.Count,
            Remarks = (remarks ?? string.Empty).Trim(),
            CreatedBy = performedBy.Username
        };

        await connection.InsertAsync(run).ConfigureAwait(false);

        // The roster is held as empty payslips until the first calculation, so
        // "who is on this run" is answerable before any arithmetic has happened.
        var placeholders = employeeIds.Distinct().Select(id => new Payslip
        {
            PayrollRunId = run.Id,
            EmployeeId = id,
            PeriodCode = run.PeriodCode,
            PeriodStart = run.PeriodStart,
            PeriodEnd = run.PeriodEnd,
            PayDate = run.PayDate
        }).ToList();

        await connection.InsertAllAsync(placeholders).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.PayrollRunCreated, nameof(PayrollRun), run.Id, true,
            $"Created {run.ReferenceNumber}: {run.TypeDisplay} for {period.Code} " +
            $"({run.PeriodDisplay}, paid {run.PayDateDisplay}), {employeeIds.Count} employee(s).",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayrollRun>.Ok(run,
            $"{run.ReferenceNumber} created with {employeeIds.Count} employee(s). Calculate it to produce payslips.");
    }

    // =====================================================================
    // FR-051 – FR-059 — calculating
    // =====================================================================

    public async Task<CalculationResult> CalculateAsync(int runId, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(PayrollRun), runId, false,
                $"Role {performedBy.RoleDisplayName} is not permitted to calculate payroll.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return new CalculationResult(false, "You do not have permission to perform this action.", 0, 0, []);
        }

        var run = await GetRunAsync(runId).ConfigureAwait(false);
        if (run is null)
            return new CalculationResult(false, "That run no longer exists.", 0, 0, []);

        // FR-058. Only a draft may be recalculated. An approved run's figures are
        // what an approver signed off; a posted run's are the record.
        if (!run.IsEditable)
        {
            return new CalculationResult(false,
                $"{run.ReferenceNumber} is {run.StatusDisplay.ToLowerInvariant()} and cannot be recalculated. " +
                "Return it to draft first.", 0, 0, []);
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var roster = await connection.Table<Payslip>()
            .Where(p => p.PayrollRunId == runId)
            .ToListAsync()
            .ConfigureAwait(false);

        var employeeIds = roster.Select(p => p.EmployeeId).Distinct().ToList();

        if (employeeIds.Count == 0)
            return new CalculationResult(false, "The run has no employees on it.", 0, 0, []);

        var context = await BuildContextAsync(run).ConfigureAwait(false);

        // A missing table stops the whole run rather than producing three hundred
        // payslips that are all wrong in the same way.
        if (!context.Statutory.IsComplete)
        {
            return new CalculationResult(false,
                $"Payroll cannot run: {string.Join(", ", context.Statutory.Missing)} missing on the pay date.",
                0, 0, context.Statutory.Missing);
        }

        var inputs = await BuildInputsAsync(run, employeeIds).ConfigureAwait(false);

        // Recalculating replaces everything. Deleting first is what makes the
        // operation idempotent: running it five times leaves the same five
        // payslips, not twenty-five.
        await ClearComputedAsync(runId).ConfigureAwait(false);

        var blockers = new List<string>();
        var drafts = new List<PayslipDraft>();

        foreach (var input in inputs)
        {
            var draft = PayrollCalculator.Calculate(input, context);
            drafts.Add(draft);
            blockers.AddRange(draft.Blockers);
        }

        foreach (var draft in drafts)
        {
            await connection.InsertAsync(draft.Payslip).ConfigureAwait(false);

            foreach (var line in draft.Lines)
            {
                line.PayslipId = draft.Payslip.Id;
                line.PayrollRunId = runId;
            }

            if (draft.Lines.Count > 0)
                await connection.InsertAllAsync(draft.Lines).ConfigureAwait(false);
        }

        var payslips = drafts.Select(d => d.Payslip).ToList();

        run.EmployeeCount = payslips.Count;
        run.TotalGross = payslips.Sum(p => p.GrossPay);
        run.TotalTaxable = payslips.Sum(p => p.TaxableIncome);
        run.TotalDeductions = payslips.Sum(p => p.TotalDeductions);
        run.TotalNet = payslips.Sum(p => p.NetPay);
        run.TotalEmployerShare = payslips.Sum(p => p.TotalEmployerShare);
        run.ExceptionCount = payslips.Count(p => p.IsFlaggedForReview);
        run.CalculatedUtc = DateTime.UtcNow;

        await connection.UpdateAsync(run).ConfigureAwait(false);

        var distinctBlockers = blockers.Distinct().ToList();

        await _audit.WriteAsync(
            AuditActions.PayrollRunCalculated, nameof(PayrollRun), run.Id, true,
            $"Calculated {run.ReferenceNumber}: {payslips.Count} payslip(s), " +
            $"gross {run.GrossDisplay}, net {run.NetDisplay}, " +
            $"{run.ExceptionCount} flagged for review, {distinctBlockers.Count} blocker(s).",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        var message = distinctBlockers.Count > 0
            ? $"Calculated {payslips.Count} payslip(s), but {distinctBlockers.Count} problem(s) must be " +
              "resolved before this run can be submitted."
            : run.ExceptionCount > 0
                ? $"Calculated {payslips.Count} payslip(s). {run.ExceptionCount} need review before approval."
                : $"Calculated {payslips.Count} payslip(s). Gross {run.GrossDisplay}, net {run.NetDisplay}.";

        return new CalculationResult(true, message, payslips.Count, run.ExceptionCount, distinctBlockers);
    }

    /// <summary>
    /// Removes everything a calculation produced, leaving the roster behind as
    /// empty payslips so the run still knows who is on it.
    /// </summary>
    private async Task ClearComputedAsync(int runId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        await connection.ExecuteAsync(
            "DELETE FROM payslip_lines WHERE PayrollRunId = ?", runId).ConfigureAwait(false);

        await connection.ExecuteAsync(
            "DELETE FROM payslips WHERE PayrollRunId = ?", runId).ConfigureAwait(false);
    }

    // =====================================================================
    // Context and inputs
    // =====================================================================

    private async Task<PayrollContext> BuildContextAsync(PayrollRun run)
    {
        var settings = await _config.GetSettingsAsync().ConfigureAwait(false);
        var premiums = await _config.GetPremiumMatrixAsync(run.PayDate).ConfigureAwait(false);
        var statutory = await _statutory.GetSnapshotAsync(run.PayDate).ConfigureAwait(false);
        var earnings = await _config.GetEarningTypesAsync(includeInactive: true).ConfigureAwait(false);
        var deductions = await _config.GetDeductionTypesAsync(includeInactive: true).ConfigureAwait(false);

        var settles = settings.AnnualiseTaxOnFinalPeriod &&
                      run.SettlesTheYear(await IsLastRegularOfYearAsync(run).ConfigureAwait(false));

        // Read at the pay date, like the premium matrix and the statutory
        // tables: a run cannot take its wage rate from one month and its
        // contribution schedule from another.
        var rates = await _detachments.GetRateTableAsync(run.PayDate).ConfigureAwait(false);

        return new PayrollContext(
            run, settings, premiums, statutory, earnings, deductions, rates, settles);
    }

    /// <summary>
    /// Whether this is the last regular cut-off of the calendar year, which is
    /// what triggers the year-end settlement.
    ///
    /// <para>The year is taken from the <em>period</em>, not the pay date: a
    /// December cut-off paid in January is still December's payroll, and
    /// settling it in the following year would put the adjustment in a year it
    /// does not belong to.</para>
    /// </summary>
    private async Task<bool> IsLastRegularOfYearAsync(PayrollRun run)
    {
        if (run.RunType != PayrollRunType.Regular)
            return false;

        var periods = await _config.GetPayPeriodsAsync(run.PeriodEnd.Year).ConfigureAwait(false);

        var last = periods
            .Where(p => p.PeriodEnd.Year == run.PeriodEnd.Year)
            .OrderByDescending(p => p.PeriodEnd)
            .FirstOrDefault();

        return last is not null && last.Id == run.PayPeriodId;
    }

    private async Task<IReadOnlyList<EmployeePayrollInput>> BuildInputsAsync(
        PayrollRun run, IReadOnlyList<int> employeeIds)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var everyone = await _employees.GetAllAsync().ConfigureAwait(false);
        var employees = everyone.Where(e => employeeIds.Contains(e.Id)).ToList();

        var departments = await _organization.GetDepartmentsAsync(includeInactive: true).ConfigureAwait(false);
        var positions = await _organization.GetPositionsAsync(includeInactive: true).ConfigureAwait(false);

        var departmentById = departments.ToDictionary(d => d.Id);
        var positionById = positions.ToDictionary(p => p.Id);

        var loans = await connection.Table<EmployeeLoan>().ToListAsync().ConfigureAwait(false);

        // Standing deductions — insurance, performance bond, processing fee.
        // Filtered here rather than in the engine so the calculator stays a pure
        // function of what it is handed, and read against the pay date, which is
        // the date the wage rates and statutory tables are read at too.
        var standing = (await connection.Table<EmployeeDeduction>().ToListAsync().ConfigureAwait(false))
            .Where(d => d.AppliesOn(run.PayDate))
            .ToList();

        var adjustments = await connection.Table<PayrollAdjustment>()
            .Where(a => a.PayrollRunId == run.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        var year = run.PeriodEnd.Year;
        var yearToDate = await BuildYearToDateAsync(year, run.Id).ConfigureAwait(false);

        var detachmentById = (await _detachments.GetAllAsync(includeInactive: true).ConfigureAwait(false))
            .ToDictionary(d => d.Id);

        // The client's timesheet, where accounting has keyed one. It replaces
        // daily attendance for whoever it covers; a 13th month run pays an
        // entitlement rather than time and reads neither.
        IReadOnlyDictionary<int, PeriodTimesheet> timesheets =
            run.RunType == PayrollRunType.ThirteenthMonth
                ? new Dictionary<int, PeriodTimesheet>()
                : await _timesheets.GetByEmployeeAsync(run.Id).ConfigureAwait(false);

        Detachment? Detachment(Employee employee) =>
            employee.DetachmentId is { } id && detachmentById.TryGetValue(id, out var found) ? found : null;

        var inputs = new List<EmployeePayrollInput>();

        foreach (var employee in employees)
        {
            var attendance = run.RunType == PayrollRunType.ThirteenthMonth
                ? []
                : await _attendance.GetRangeAsync(employee.Id, run.CutOffStart, run.CutOffEnd)
                    .ConfigureAwait(false);

            var leave = await ApprovedLeaveAsync(employee.Id, run.CutOffStart, run.CutOffEnd)
                .ConfigureAwait(false);

            var position = employee.PositionId is { } positionId && positionById.TryGetValue(positionId, out var p)
                ? p
                : null;

            var convertible = run.RunType == PayrollRunType.FinalPay
                ? await ConvertibleLeaveDaysAsync(employee, year).ConfigureAwait(false)
                : 0m;

            inputs.Add(new EmployeePayrollInput
            {
                Employee = employee,
                DepartmentName = employee.DepartmentId is { } deptId && departmentById.TryGetValue(deptId, out var d)
                    ? d.Name
                    : string.Empty,
                PositionTitle = position?.Title ?? string.Empty,
                DetachmentCode = Detachment(employee)?.Code ?? string.Empty,
                DetachmentName = Detachment(employee)?.Name ?? string.Empty,
                IsManagerial = position?.IsManagerial ?? false,
                Attendance = attendance,
                Timesheet = timesheets.GetValueOrDefault(employee.Id),
                ApprovedLeave = leave,
                Loans = loans.Where(l => l.EmployeeId == employee.Id).ToList(),
                RecurringDeductions = standing.Where(d => d.EmployeeId == employee.Id).ToList(),
                Adjustments = adjustments.Where(a => a.EmployeeId == employee.Id).ToList(),
                YearToDate = yearToDate.TryGetValue(employee.Id, out var totals) ? totals : YearToDateTotals.Empty,
                ConvertibleLeaveDays = convertible
            });
        }

        return inputs;
    }

    private async Task<IReadOnlyList<LeaveRequest>> ApprovedLeaveAsync(int employeeId, DateTime from, DateTime to)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<LeaveRequest>()
            .Where(r => r.EmployeeId == employeeId)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows
            .Where(r => r.Status == LeaveRequestStatus.Approved)
            .Where(r => r.StartDate.Date <= to.Date && r.EndDate.Date >= from.Date)
            .ToList();
    }

    /// <summary>FR-062. Unused credits on the leave types that convert to cash.</summary>
    private async Task<decimal> ConvertibleLeaveDaysAsync(Employee employee, int year)
    {
        var entitlements = await _leave.GetEntitlementsAsync(employee, year).ConfigureAwait(false);

        return entitlements
            .Where(e => e.Type.IsConvertibleToCash && e.Remaining > 0m)
            .Sum(e => e.Remaining);
    }

    /// <summary>
    /// Every employee's year to date, from <b>posted</b> runs only.
    ///
    /// <para>A draft can still be discarded, and counting one would inflate the
    /// year on the strength of a run that never happened. The run being
    /// calculated is excluded too — it has not happened yet either, and on a
    /// recalculation its own previous figures would double-count.</para>
    /// </summary>
    private async Task<IReadOnlyDictionary<int, YearToDateTotals>> BuildYearToDateAsync(int year, int excludeRunId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var runs = await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false);

        var postedIds = runs
            .Where(r => r.Status == PayrollRunStatus.Posted && r.PeriodEnd.Year == year && r.Id != excludeRunId)
            .Select(r => r.Id)
            .ToHashSet();

        if (postedIds.Count == 0)
            return new Dictionary<int, YearToDateTotals>();

        var payslips = (await connection.Table<Payslip>().ToListAsync().ConfigureAwait(false))
            .Where(p => postedIds.Contains(p.PayrollRunId))
            .ToList();

        var lines = (await connection.Table<PayslipLine>().ToListAsync().ConfigureAwait(false))
            .Where(l => postedIds.Contains(l.PayrollRunId))
            .ToList();

        var linesByPayslip = lines.ToLookup(l => l.PayslipId);

        var totals = new Dictionary<int, YearToDateTotals>();

        foreach (var group in payslips.GroupBy(p => p.EmployeeId))
        {
            var basic = 0m;
            var benefits = 0m;
            var thirteenth = 0m;

            foreach (var payslip in group)
            {
                foreach (var line in linesByPayslip[payslip.Id].Where(l => l.IsEarning))
                {
                    // PD 851 counts basic salary earned, which is exactly the
                    // lines the company has flagged as its 13th month base.
                    if (line.Code is PayComponentCodes.BasicPay
                                  or PayComponentCodes.HolidayUnworked
                                  or PayComponentCodes.PaidLeave)
                    {
                        basic += line.Amount;
                    }

                    if (!line.IsTaxable)
                        benefits += line.Amount;

                    if (line.Code == PayComponentCodes.ThirteenthMonth)
                        thirteenth += line.Amount;
                }
            }

            totals[group.Key] = new YearToDateTotals(
                BasicEarned: basic,
                GrossEarned: group.Sum(p => p.GrossPay),
                TaxableIncome: group.Sum(p => p.TaxableIncome),
                TaxWithheld: group.Sum(p => p.WithholdingTax),
                NonTaxableBenefits: benefits,
                ThirteenthMonthPaid: thirteenth,
                Sss: group.Sum(p => p.EmployeeSss + p.EmployeeSssWisp),
                PhilHealth: group.Sum(p => p.EmployeePhilHealth),
                PagIbig: group.Sum(p => p.EmployeePagIbig));
        }

        return totals;
    }

    // =====================================================================
    // FR-058 — the state machine
    // =====================================================================

    public async Task<SaveResult<PayrollRun>> SubmitForApprovalAsync(int runId, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<PayrollRun>(performedBy, "submit a payroll run").ConfigureAwait(false);

        var run = await GetRunAsync(runId).ConfigureAwait(false);
        if (run is null)
            return SaveResult<PayrollRun>.Fail("That run no longer exists.");

        if (run.Status != PayrollRunStatus.Draft)
            return SaveResult<PayrollRun>.Fail($"{run.ReferenceNumber} is already {run.StatusDisplay.ToLowerInvariant()}.");

        if (!run.HasBeenCalculated)
            return SaveResult<PayrollRun>.Fail("Calculate the run before submitting it for approval.");

        // A recalculation is cheap; an approver signing off figures that were
        // never checked is not. A flagged payslip has to be dealt with first.
        if (run.ExceptionCount > 0)
        {
            return SaveResult<PayrollRun>.Fail(
                $"{run.ExceptionCount} payslip(s) are flagged for review. Resolve them — with an adjustment, " +
                "a corrected time record, or by taking the employee off the run — before submitting.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        run.Status = PayrollRunStatus.ForApproval;
        run.SubmittedBy = performedBy.Username;
        run.SubmittedUtc = DateTime.UtcNow;
        run.DecisionRemarks = string.Empty;

        await connection.UpdateAsync(run).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.PayrollRunSubmitted, nameof(PayrollRun), run.Id, true,
            $"{run.ReferenceNumber} submitted for approval: {run.HeadcountDisplay}, " +
            $"gross {run.GrossDisplay}, net {run.NetDisplay}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayrollRun>.Ok(run, $"{run.ReferenceNumber} submitted for approval.");
    }

    public async Task<SaveResult<PayrollRun>> ReturnToDraftAsync(int runId, string reason, User performedBy)
    {
        var run = await GetRunAsync(runId).ConfigureAwait(false);
        if (run is null)
            return SaveResult<PayrollRun>.Fail("That run no longer exists.");

        // Either side may pull it back: the officer who submitted it, or the
        // approver who is not willing to sign it.
        if (!performedBy.Can(Permission.RunPayroll) && !performedBy.Can(Permission.ApprovePayroll))
            return await RefuseAsync<PayrollRun>(performedBy, "return a payroll run to draft").ConfigureAwait(false);

        if (run.Status != PayrollRunStatus.ForApproval)
        {
            return SaveResult<PayrollRun>.Fail(
                $"Only a run awaiting approval can be returned to draft. {run.ReferenceNumber} is " +
                $"{run.StatusDisplay.ToLowerInvariant()}.");
        }

        reason = (reason ?? string.Empty).Trim();

        if (reason.Length == 0)
            return SaveResult<PayrollRun>.Fail("Say why the run is being returned. It goes on the audit trail.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        run.Status = PayrollRunStatus.Draft;
        run.DecisionRemarks = reason;
        run.SubmittedBy = string.Empty;
        run.SubmittedUtc = null;

        await connection.UpdateAsync(run).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.PayrollRunReturned, nameof(PayrollRun), run.Id, true,
            $"{run.ReferenceNumber} returned to draft: {reason}",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayrollRun>.Ok(run, $"{run.ReferenceNumber} returned to draft.");
    }

    public async Task<SaveResult<PayrollRun>> ApproveAsync(int runId, User performedBy)
    {
        if (!performedBy.Can(Permission.ApprovePayroll))
            return await RefuseAsync<PayrollRun>(performedBy, "approve a payroll run").ConfigureAwait(false);

        var run = await GetRunAsync(runId).ConfigureAwait(false);
        if (run is null)
            return SaveResult<PayrollRun>.Fail("That run no longer exists.");

        if (run.Status != PayrollRunStatus.ForApproval)
        {
            return SaveResult<PayrollRun>.Fail(
                $"{run.ReferenceNumber} is {run.StatusDisplay.ToLowerInvariant()} and is not awaiting approval.");
        }

        // Separation of duties: the officer who computed a run does not also
        // sign it off. An administrator holding both permissions is still two
        // different acts, and the log shows the same name twice.
        if (string.Equals(run.SubmittedBy, performedBy.Username, StringComparison.OrdinalIgnoreCase))
        {
            await _audit.WriteAsync(
                AuditActions.AccessDenied, nameof(PayrollRun), run.Id, false,
                $"{performedBy.Username} attempted to approve {run.ReferenceNumber}, which they submitted.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return SaveResult<PayrollRun>.Fail(
                "You submitted this run, so you cannot also approve it. Ask another approver to review it.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        run.Status = PayrollRunStatus.Approved;
        run.ApprovedBy = performedBy.Username;
        run.ApprovedUtc = DateTime.UtcNow;

        await connection.UpdateAsync(run).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.PayrollRunApproved, nameof(PayrollRun), run.Id, true,
            $"{run.ReferenceNumber} approved: {run.HeadcountDisplay}, net {run.NetDisplay}. " +
            "The figures are now frozen.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayrollRun>.Ok(run,
            $"{run.ReferenceNumber} approved. It can no longer be edited — post it to release the pay.");
    }

    /// <summary>
    /// Posting is the step that reaches outside the run: attendance inside the
    /// cut-off is locked so a payslip can never drift away from the record it
    /// was computed from (NFR-037), and loan balances are decremented (FR-055).
    ///
    /// <para>There is no way back. Everything before this point is reversible.</para>
    /// </summary>
    public async Task<SaveResult<PayrollRun>> PostAsync(int runId, User performedBy)
    {
        if (!performedBy.Can(Permission.ApprovePayroll) && !performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<PayrollRun>(performedBy, "post a payroll run").ConfigureAwait(false);

        var run = await GetRunAsync(runId).ConfigureAwait(false);
        if (run is null)
            return SaveResult<PayrollRun>.Fail("That run no longer exists.");

        if (run.Status != PayrollRunStatus.Approved)
        {
            return SaveResult<PayrollRun>.Fail(
                $"Only an approved run can be posted. {run.ReferenceNumber} is " +
                $"{run.StatusDisplay.ToLowerInvariant()}.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var payslips = await connection.Table<Payslip>()
            .Where(p => p.PayrollRunId == runId)
            .ToListAsync()
            .ConfigureAwait(false);

        // ---------------------------------------------- FR-055, loan balances
        var collected = await ApplyLoanPaymentsAsync(run, payslips).ConfigureAwait(false);

        // ------------------------------------------------- NFR-037, lock time
        var lockedDays = 0;

        if (run.RunType != PayrollRunType.ThirteenthMonth)
        {
            lockedDays = await _attendance.LockRangeAsync(
                payslips.Select(p => p.EmployeeId).Distinct().ToList(),
                run.CutOffStart, run.CutOffEnd, performedBy).ConfigureAwait(false);
        }

        run.Status = PayrollRunStatus.Posted;
        run.PostedBy = performedBy.Username;
        run.PostedUtc = DateTime.UtcNow;

        await connection.UpdateAsync(run).ConfigureAwait(false);

        // The period is closed behind the run, so a later correction has to be
        // a deliberate reopening rather than an accident.
        await _config.SetPayPeriodStatusAsync(run.PayPeriodId, PayPeriodStatus.Closed, performedBy)
            .ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.PayrollRunPosted, nameof(PayrollRun), run.Id, true,
            $"{run.ReferenceNumber} posted: {run.HeadcountDisplay}, net {run.NetDisplay}, " +
            $"employer share {run.EmployerShareDisplay}. " +
            $"{lockedDays} attendance day(s) locked, {collected} loan instalment(s) applied.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayrollRun>.Ok(run,
            $"{run.ReferenceNumber} posted. {lockedDays} attendance day(s) locked and " +
            $"{collected} loan instalment(s) applied. Payslips are now final.");
    }

    /// <summary>
    /// Moves the loan balances the run collected against, and writes the ledger
    /// row that explains each movement.
    ///
    /// <para>Driven from the <em>payslip lines</em> rather than recomputed, so
    /// what is taken off the balance is exactly what the employee was charged.</para>
    /// </summary>
    private async Task<int> ApplyLoanPaymentsAsync(PayrollRun run, IReadOnlyList<Payslip> payslips)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var loans = (await connection.Table<EmployeeLoan>().ToListAsync().ConfigureAwait(false))
            .Where(l => l.Status == LoanStatus.Active)
            .ToList();

        if (loans.Count == 0)
            return 0;

        var lines = (await connection.Table<PayslipLine>()
            .Where(l => l.PayrollRunId == run.Id)
            .ToListAsync()
            .ConfigureAwait(false))
            .Where(l => l.IsDeduction)
            .ToList();

        var payslipById = payslips.ToDictionary(p => p.Id);
        var applied = 0;

        foreach (var line in lines)
        {
            if (!payslipById.TryGetValue(line.PayslipId, out var payslip))
                continue;

            // One line per loan: the calculator emits at most one instalment for
            // each, so matching on employee and deduction code is unambiguous.
            var loan = loans.FirstOrDefault(l =>
                l.EmployeeId == payslip.EmployeeId &&
                string.Equals(l.DeductionCode, line.Code, StringComparison.OrdinalIgnoreCase) &&
                l.OutstandingBalance > 0m);

            if (loan is null)
                continue;

            var amount = Math.Min(line.Amount, loan.OutstandingBalance);

            loan.OutstandingBalance = PayrollRounding.Money(loan.OutstandingBalance - amount);

            if (loan.OutstandingBalance <= 0m)
            {
                loan.OutstandingBalance = 0m;
                loan.Status = LoanStatus.Completed;
            }

            await connection.UpdateAsync(loan).ConfigureAwait(false);

            await connection.InsertAsync(new LoanPayment
            {
                LoanId = loan.Id,
                PayrollRunId = run.Id,
                PayslipId = payslip.Id,
                EmployeeId = payslip.EmployeeId,
                Amount = amount,
                BalanceAfter = loan.OutstandingBalance,
                PaidOn = run.PayDate,
                RunReference = run.ReferenceNumber
            }).ConfigureAwait(false);

            applied++;
        }

        return applied;
    }

    public async Task<SaveResult<PayrollRun>> DiscardAsync(int runId, string reason, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<PayrollRun>(performedBy, "discard a payroll run").ConfigureAwait(false);

        var run = await GetRunAsync(runId).ConfigureAwait(false);
        if (run is null)
            return SaveResult<PayrollRun>.Fail("That run no longer exists.");

        // FR-058. A posted run is the record of money that has been paid; an
        // approved one is what somebody signed. Neither is discardable.
        if (run.IsFrozen)
        {
            return SaveResult<PayrollRun>.Fail(
                $"{run.ReferenceNumber} is {run.StatusDisplay.ToLowerInvariant()} and cannot be discarded. " +
                "A posted run is corrected with an adjustment run, not deleted.");
        }

        reason = (reason ?? string.Empty).Trim();

        if (reason.Length == 0)
            return SaveResult<PayrollRun>.Fail("Say why the run is being discarded. It goes on the audit trail.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        // The payslips and adjustments go; the run itself stays as a cancelled
        // shell so its reference number is never reused (FR-059).
        await ClearComputedAsync(runId).ConfigureAwait(false);

        await connection.ExecuteAsync(
            "DELETE FROM payroll_adjustments WHERE PayrollRunId = ?", runId).ConfigureAwait(false);

        run.Status = PayrollRunStatus.Cancelled;
        run.DecisionRemarks = reason;
        run.EmployeeCount = 0;
        run.TotalGross = 0m;
        run.TotalTaxable = 0m;
        run.TotalDeductions = 0m;
        run.TotalNet = 0m;
        run.TotalEmployerShare = 0m;
        run.ExceptionCount = 0;

        await connection.UpdateAsync(run).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.PayrollRunDiscarded, nameof(PayrollRun), run.Id, true,
            $"{run.ReferenceNumber} discarded: {reason} Prior periods are untouched.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayrollRun>.Ok(run,
            $"{run.ReferenceNumber} discarded. Its reference number will not be reused.");
    }

    // =====================================================================
    // FR-057 — one-off adjustments
    // =====================================================================

    public async Task<IReadOnlyList<PayrollAdjustment>> GetAdjustmentsAsync(int runId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<PayrollAdjustment>()
            .Where(a => a.PayrollRunId == runId)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.OrderBy(a => a.Kind).ThenBy(a => a.Id).ToList();
    }

    public async Task<SaveResult<PayrollAdjustment>> SaveAdjustmentAsync(
        PayrollAdjustment adjustment, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<PayrollAdjustment>(performedBy, "adjust a payroll run").ConfigureAwait(false);

        var run = await GetRunAsync(adjustment.PayrollRunId).ConfigureAwait(false);
        if (run is null)
            return SaveResult<PayrollAdjustment>.Fail("That run no longer exists.");

        if (!run.IsEditable)
        {
            return SaveResult<PayrollAdjustment>.Fail(
                $"{run.ReferenceNumber} is {run.StatusDisplay.ToLowerInvariant()} and cannot be adjusted.");
        }

        adjustment.Code = (adjustment.Code ?? string.Empty).Trim().ToUpperInvariant();
        adjustment.Name = (adjustment.Name ?? string.Empty).Trim();
        adjustment.Remark = (adjustment.Remark ?? string.Empty).Trim();

        if (adjustment.Code.Length == 0)
            return SaveResult<PayrollAdjustment>.Fail("Choose what the adjustment is for.");

        if (adjustment.Name.Length == 0)
            return SaveResult<PayrollAdjustment>.Fail("Give the adjustment a name — it appears on the payslip.");

        if (adjustment.Amount <= 0m)
        {
            return SaveResult<PayrollAdjustment>.Fail(
                "Enter a positive amount. Whether it adds to or comes off the pay is the kind, not the sign.");
        }

        // FR-057. The remark is the requirement, not a courtesy: an unexplained
        // adjustment is exactly what an employee or an auditor will ask about.
        if (adjustment.Remark.Length == 0)
            return SaveResult<PayrollAdjustment>.Fail("A remark is required on every adjustment.");

        // An adjustment coded to something the engine produces itself would be
        // added on top of the computed line, not instead of it — so a 5Slip
        // typed here would pay the accrual twice, and an SSS one would remit a
        // figure the schedule never asked for.
        //
        // Withholding tax is the deliberate exception: the legacy screen's
        // E-Withtax, where an entered figure stands in place of the table's.
        // The calculator finds this line and stands down rather than adding to
        // it, which is why it is the one system code worth allowing.
        var systemEarning = (await _config.GetEarningTypesAsync(includeInactive: true).ConfigureAwait(false))
            .FirstOrDefault(e =>
                e.IsSystem && string.Equals(e.Code, adjustment.Code, StringComparison.OrdinalIgnoreCase));

        if (adjustment.Kind == PayslipLineKind.Earning && systemEarning is not null)
        {
            return SaveResult<PayrollAdjustment>.Fail(
                $"{systemEarning.Name} is computed by the payroll engine, so an adjustment here " +
                "would be paid on top of it. Correct the figures it is computed from instead.");
        }

        var isTaxOverride = adjustment.Kind == PayslipLineKind.Deduction &&
                            string.Equals(adjustment.Code, PayComponentCodes.WithholdingTax,
                                StringComparison.OrdinalIgnoreCase);

        var systemDeduction = (await _config.GetDeductionTypesAsync(includeInactive: true).ConfigureAwait(false))
            .FirstOrDefault(d =>
                d.IsSystem && string.Equals(d.Code, adjustment.Code, StringComparison.OrdinalIgnoreCase));

        if (adjustment.Kind == PayslipLineKind.Deduction && systemDeduction is not null && !isTaxOverride)
        {
            return SaveResult<PayrollAdjustment>.Fail(
                $"{systemDeduction.Name} is computed by the payroll engine, so an adjustment here " +
                "would be taken on top of it. Withholding tax is the only one that can be entered " +
                "to stand in place of the computed figure.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var onRun = await connection.Table<Payslip>()
            .Where(p => p.PayrollRunId == run.Id && p.EmployeeId == adjustment.EmployeeId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (onRun is null)
            return SaveResult<PayrollAdjustment>.Fail("That employee is not on this run.");

        var isNew = adjustment.Id == 0;

        if (isNew)
        {
            adjustment.CreatedBy = performedBy.Username;
            await connection.InsertAsync(adjustment).ConfigureAwait(false);
        }
        else
        {
            await connection.UpdateAsync(adjustment).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            AuditActions.PayrollAdjustmentSaved, nameof(PayrollAdjustment), adjustment.Id, true,
            $"{(isNew ? "Added" : "Updated")} {adjustment.KindDisplay.ToLowerInvariant()} " +
            $"{adjustment.Code} of {PayrollRounding.Format(adjustment.Amount)} for " +
            $"{onRun.EmployeeName} on {run.ReferenceNumber}: {adjustment.Remark}",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayrollAdjustment>.Ok(adjustment,
            $"Adjustment saved. Recalculate {run.ReferenceNumber} for it to reach the payslip.");
    }

    public async Task<SaveResult<PayrollAdjustment>> DeleteAdjustmentAsync(int id, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<PayrollAdjustment>(performedBy, "remove a payroll adjustment").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var adjustment = await connection.Table<PayrollAdjustment>()
            .Where(a => a.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (adjustment is null)
            return SaveResult<PayrollAdjustment>.Fail("That adjustment no longer exists.");

        var run = await GetRunAsync(adjustment.PayrollRunId).ConfigureAwait(false);

        if (run is not null && !run.IsEditable)
        {
            return SaveResult<PayrollAdjustment>.Fail(
                $"{run.ReferenceNumber} is {run.StatusDisplay.ToLowerInvariant()} and cannot be adjusted.");
        }

        await connection.DeleteAsync(adjustment).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.PayrollAdjustmentRemoved, nameof(PayrollAdjustment), id, true,
            $"Removed {adjustment.KindDisplay.ToLowerInvariant()} {adjustment.Code} of " +
            $"{PayrollRounding.Format(adjustment.Amount)} from {run?.ReferenceNumber ?? "a run"}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayrollAdjustment>.Ok(adjustment,
            $"Adjustment removed. Recalculate {run?.ReferenceNumber ?? "the run"} to take it off the payslip.");
    }

    // =====================================================================
    // FR-055 — loans and cash advances
    // =====================================================================

    public async Task<IReadOnlyList<EmployeeLoan>> GetLoansAsync(int? employeeId = null, bool includeClosed = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<EmployeeLoan>().ToListAsync().ConfigureAwait(false);

        return rows
            .Where(l => employeeId is not { } id || l.EmployeeId == id)
            .Where(l => includeClosed || l.Status is LoanStatus.Active or LoanStatus.Suspended)
            .OrderBy(l => l.Status)
            .ThenByDescending(l => l.Id)
            .ToList();
    }

    public async Task<SaveResult<EmployeeLoan>> SaveLoanAsync(EmployeeLoan loan, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<EmployeeLoan>(performedBy, "maintain employee loans").ConfigureAwait(false);

        loan.DeductionCode = (loan.DeductionCode ?? string.Empty).Trim().ToUpperInvariant();
        loan.Reference = (loan.Reference ?? string.Empty).Trim();
        loan.Remarks = (loan.Remarks ?? string.Empty).Trim();
        loan.StartDate = loan.StartDate.Date;

        if (loan.EmployeeId <= 0)
            return SaveResult<EmployeeLoan>.Fail("Choose the employee the loan belongs to.");

        if (loan.DeductionCode.Length == 0)
            return SaveResult<EmployeeLoan>.Fail("Choose which deduction the instalment is taken under.");

        if (loan.PrincipalAmount <= 0m)
            return SaveResult<EmployeeLoan>.Fail("Enter the amount borrowed.");

        if (loan.AmortisationAmount <= 0m)
            return SaveResult<EmployeeLoan>.Fail("Enter what is taken each period.");

        if (loan.AmortisationAmount > loan.PrincipalAmount)
        {
            return SaveResult<EmployeeLoan>.Fail(
                "The instalment is larger than the whole loan. Reduce it, or record the loan as a single deduction.");
        }

        var deduction = (await _config.GetDeductionTypesAsync(includeInactive: true).ConfigureAwait(false))
            .FirstOrDefault(d => string.Equals(d.Code, loan.DeductionCode, StringComparison.OrdinalIgnoreCase));

        if (deduction is null)
            return SaveResult<EmployeeLoan>.Fail($"There is no deduction type with the code {loan.DeductionCode}.");

        loan.DeductionName = deduction.Name;

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var isNew = loan.Id == 0;

        if (isNew)
        {
            loan.OutstandingBalance = loan.PrincipalAmount;
            loan.CreatedBy = performedBy.Username;
            await connection.InsertAsync(loan).ConfigureAwait(false);
        }
        else
        {
            var current = await connection.Table<EmployeeLoan>()
                .Where(l => l.Id == loan.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (current is null)
                return SaveResult<EmployeeLoan>.Fail("That loan no longer exists.");

            // The balance is moved only by posting a run, never by editing the
            // loan — otherwise a typo here would silently forgive or re-charge
            // instalments that have already been taken.
            loan.OutstandingBalance = current.OutstandingBalance;
            loan.Status = current.Status;
            loan.CreatedBy = current.CreatedBy;
            loan.CreatedUtc = current.CreatedUtc;

            await connection.UpdateAsync(loan).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            isNew ? AuditActions.LoanCreated : AuditActions.LoanUpdated,
            nameof(EmployeeLoan), loan.Id, true,
            $"{(isNew ? "Recorded" : "Updated")} {loan.DeductionName} for employee {loan.EmployeeId}: " +
            $"principal {PayrollRounding.Format(loan.PrincipalAmount)}, " +
            $"{PayrollRounding.Format(loan.AmortisationAmount)} per period, " +
            $"{PayrollRounding.Format(loan.OutstandingBalance)} outstanding.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<EmployeeLoan>.Ok(loan,
            isNew ? "Loan recorded." : "Loan updated. The outstanding balance was left as it stands.");
    }

    public async Task<SaveResult<EmployeeLoan>> SetLoanStatusAsync(int id, LoanStatus status, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
            return await RefuseAsync<EmployeeLoan>(performedBy, "change a loan's status").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var loan = await connection.Table<EmployeeLoan>()
            .Where(l => l.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (loan is null)
            return SaveResult<EmployeeLoan>.Fail("That loan no longer exists.");

        var previous = loan.Status;
        loan.Status = status;

        await connection.UpdateAsync(loan).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.LoanStatusChanged, nameof(EmployeeLoan), loan.Id, true,
            $"{loan.DeductionName} for employee {loan.EmployeeId} moved from {previous} to {status}, " +
            $"with {PayrollRounding.Format(loan.OutstandingBalance)} outstanding.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<EmployeeLoan>.Ok(loan, $"Loan marked {status.ToString().ToLowerInvariant()}.");
    }

    public async Task<IReadOnlyList<LoanPayment>> GetLoanPaymentsAsync(int loanId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<LoanPayment>()
            .Where(p => p.LoanId == loanId)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.OrderByDescending(p => p.PaidOn).ThenByDescending(p => p.Id).ToList();
    }

    // =====================================================================

    private async Task<PayPeriod?> FindPeriodAsync(int payPeriodId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        return await connection.Table<PayPeriod>()
            .Where(p => p.Id == payPeriodId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    /// <summary>
    /// FR-060. Everyone already on a live run for this period and run type.
    /// Cancelled runs do not count — that is the point of cancelling one.
    /// </summary>
    private async Task<HashSet<int>> EmployeesAlreadyOnAsync(
        int payPeriodId, PayrollRunType runType, int? excludeRunId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var runs = (await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false))
            .Where(r => r.PayPeriodId == payPeriodId &&
                        r.RunType == runType &&
                        r.Status != PayrollRunStatus.Cancelled &&
                        r.Id != excludeRunId)
            .Select(r => r.Id)
            .ToHashSet();

        if (runs.Count == 0)
            return [];

        var payslips = await connection.Table<Payslip>().ToListAsync().ConfigureAwait(false);

        return payslips
            .Where(p => runs.Contains(p.PayrollRunId))
            .Select(p => p.EmployeeId)
            .ToHashSet();
    }

    private async Task<string> NextReferenceAsync(PayPeriod period, PayrollRunType runType)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var prefix = runType switch
        {
            PayrollRunType.ThirteenthMonth => "13M",
            PayrollRunType.FinalPay => "FP",
            PayrollRunType.Adjustment => "ADJ",
            _ => "PR"
        };

        var stem = $"{prefix}-{period.Code}";

        var existing = (await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false))
            .Where(r => r.ReferenceNumber.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // A cancelled run keeps its number, so the suffix counts every run that
        // has ever carried this stem rather than the live ones.
        return existing.Count == 0 ? stem : $"{stem}-{existing.Count + 1}";
    }

    private async Task<SaveResult<T>> RefuseAsync<T>(User performedBy, string action) where T : class
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, nameof(PayrollRun), null, false,
            $"Role {performedBy.RoleDisplayName} is not permitted to {action}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<T>.Fail("You do not have permission to perform this action.");
    }

    // =====================================================================
    // Standing deductions — the insurance premium, the performance bond, the
    // processing fee. Beside loans because both recur, but these carry no
    // balance and stop by date rather than by being paid off.
    // =====================================================================

    public async Task<IReadOnlyList<EmployeeDeduction>> GetStandingDeductionsAsync(
        int? employeeId = null, bool includeStopped = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<EmployeeDeduction>().ToListAsync().ConfigureAwait(false);

        return rows
            .Where(d => employeeId is not { } id || d.EmployeeId == id)
            .Where(d => includeStopped || d.IsActive)
            .OrderBy(d => d.DeductionName)
            .ThenByDescending(d => d.Id)
            .ToList();
    }

    public async Task<SaveResult<EmployeeDeduction>> SaveStandingDeductionAsync(
        EmployeeDeduction deduction, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
        {
            return await RefuseAsync<EmployeeDeduction>(performedBy, "maintain standing deductions")
                .ConfigureAwait(false);
        }

        deduction.DeductionCode = (deduction.DeductionCode ?? string.Empty).Trim().ToUpperInvariant();
        deduction.Reference = (deduction.Reference ?? string.Empty).Trim();
        deduction.Remarks = (deduction.Remarks ?? string.Empty).Trim();
        deduction.StartsOn = deduction.StartsOn.Date;
        deduction.EndsOn = deduction.EndsOn?.Date;

        if (deduction.EmployeeId <= 0)
            return SaveResult<EmployeeDeduction>.Fail("Choose the employee the deduction belongs to.");

        if (deduction.DeductionCode.Length == 0)
            return SaveResult<EmployeeDeduction>.Fail("Choose which deduction this is taken under.");

        if (deduction.Amount <= 0m)
            return SaveResult<EmployeeDeduction>.Fail("Enter what is taken each period.");

        if (deduction.EndsOn is { } ends && ends < deduction.StartsOn)
        {
            return SaveResult<EmployeeDeduction>.Fail(
                "The end date is before the start date, so the deduction would never be taken.");
        }

        var type = (await _config.GetDeductionTypesAsync(includeInactive: true).ConfigureAwait(false))
            .FirstOrDefault(d => string.Equals(d.Code, deduction.DeductionCode, StringComparison.OrdinalIgnoreCase));

        if (type is null)
        {
            return SaveResult<EmployeeDeduction>.Fail(
                $"There is no deduction type with the code {deduction.DeductionCode}.");
        }

        // An amortised type belongs on the loan ledger, where a balance governs
        // when it stops. Set up here it would be taken for ever.
        if (type.IsAmortised)
        {
            return SaveResult<EmployeeDeduction>.Fail(
                $"{type.Name} is amortised, so it carries a balance. Record it under " +
                "Loans & advances instead — a standing deduction never stops on its own.");
        }

        if (type.IsSystem)
        {
            return SaveResult<EmployeeDeduction>.Fail(
                $"{type.Name} is produced by the payroll engine itself and cannot be set up by hand.");
        }

        deduction.DeductionName = type.Name;
        deduction.UpdatedUtc = DateTime.UtcNow;

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var isNew = deduction.Id == 0;

        if (isNew)
        {
            deduction.CreatedBy = performedBy.Username;
            await connection.InsertAsync(deduction).ConfigureAwait(false);
        }
        else
        {
            var current = await connection.Table<EmployeeDeduction>()
                .Where(d => d.Id == deduction.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (current is null)
                return SaveResult<EmployeeDeduction>.Fail("That standing deduction no longer exists.");

            deduction.CreatedBy = current.CreatedBy;
            deduction.CreatedUtc = current.CreatedUtc;

            await connection.UpdateAsync(deduction).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            AuditActions.StandingDeductionSaved,
            nameof(EmployeeDeduction), deduction.Id, true,
            $"{(isNew ? "Set up" : "Updated")} {deduction.DeductionName} for employee " +
            $"{deduction.EmployeeId}: {PayrollRounding.Format(deduction.Amount)} per period, " +
            $"{deduction.PeriodDisplay}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<EmployeeDeduction>.Ok(deduction,
            isNew
                ? "Standing deduction set up. It applies from the next run whose pay date it covers."
                : "Standing deduction updated. Recalculate any draft run to pick up the change.");
    }

    public async Task<SaveResult<EmployeeDeduction>> SetStandingDeductionActiveAsync(
        int id, bool isActive, User performedBy)
    {
        if (!performedBy.Can(Permission.RunPayroll))
        {
            return await RefuseAsync<EmployeeDeduction>(performedBy, "stop a standing deduction")
                .ConfigureAwait(false);
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var deduction = await connection.Table<EmployeeDeduction>()
            .Where(d => d.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (deduction is null)
            return SaveResult<EmployeeDeduction>.Fail("That standing deduction no longer exists.");

        deduction.IsActive = isActive;
        deduction.UpdatedUtc = DateTime.UtcNow;

        await connection.UpdateAsync(deduction).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.StandingDeductionStopped,
            nameof(EmployeeDeduction), deduction.Id, true,
            $"{(isActive ? "Resumed" : "Stopped")} {deduction.DeductionName} for employee " +
            $"{deduction.EmployeeId}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        // Posted runs keep what they took: stopping one stops it from here on.
        return SaveResult<EmployeeDeduction>.Ok(deduction,
            isActive
                ? $"{deduction.DeductionName} resumed."
                : $"{deduction.DeductionName} stopped. Runs already posted keep what they took.");
    }
}
