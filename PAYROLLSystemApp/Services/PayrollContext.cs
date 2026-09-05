using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// One employee's year to date, as the posted payslips have it.
///
/// <para>Needed by two things: PD 851's 13th month pay, which is a twelfth of
/// the <em>basic salary actually earned</em> in the year (FR-061), and the
/// year-end tax settlement, which needs what has already been withheld.</para>
///
/// <para><b>Posted runs only.</b> A draft can still be discarded, and counting
/// one would inflate an employee's year on the strength of a run that never
/// happened.</para>
/// </summary>
public sealed record YearToDateTotals(
    decimal BasicEarned,
    decimal GrossEarned,
    decimal TaxableIncome,
    decimal TaxWithheld,
    // What of the ₱90,000 benefits exclusion has been used already.
    decimal NonTaxableBenefits,
    // 13th month pay already released this year, so it is never paid twice.
    decimal ThirteenthMonthPaid,
    decimal Sss,
    decimal PhilHealth,
    decimal PagIbig)
{
    public static YearToDateTotals Empty { get; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m);
}

/// <summary>
/// Everything one employee brings to a run: their record, their cut-off, the
/// leave that covers it, their loans and their year so far.
/// </summary>
public sealed class EmployeePayrollInput
{
    public required Employee Employee { get; init; }

    public required string DepartmentName { get; init; }

    public required string PositionTitle { get; init; }

    /// <summary>Art. 82 — managerial staff earn no overtime, night differential or premium pay.</summary>
    public required bool IsManagerial { get; init; }

    /// <summary>The days inside the run's cut-off, in date order.</summary>
    public required IReadOnlyList<AttendanceRecord> Attendance { get; init; }

    /// <summary>Approved leave overlapping the cut-off, for deciding which days are paid.</summary>
    public required IReadOnlyList<LeaveRequest> ApprovedLeave { get; init; }

    public required IReadOnlyList<EmployeeLoan> Loans { get; init; }

    public required IReadOnlyList<PayrollAdjustment> Adjustments { get; init; }

    public required YearToDateTotals YearToDate { get; init; }

    /// <summary>
    /// Unused convertible leave credits, for a final pay run (FR-062). Zero on
    /// every other kind of run.
    /// </summary>
    public decimal ConvertibleLeaveDays { get; init; }
}

/// <summary>
/// Everything a payroll run needs, loaded once before any arithmetic happens.
///
/// <para><b>The calculator does no I/O.</b> That is the point of this class:
/// <see cref="PayrollCalculator"/> is a pure function from an
/// <see cref="EmployeePayrollInput"/> and this context to a payslip, so it can
/// be reasoned about, and so a run of three hundred employees is one set of
/// reads rather than three hundred (NFR-002).</para>
/// </summary>
public sealed class PayrollContext
{
    public PayrollContext(
        PayrollRun run,
        PayrollSettings settings,
        PremiumMatrix premiums,
        StatutorySnapshot statutory,
        IReadOnlyList<EarningType> earnings,
        IReadOnlyList<DeductionType> deductions,
        bool settlesTheYear)
    {
        Run = run;
        Settings = settings;
        Premiums = premiums;
        Statutory = statutory;
        SettlesTheYear = settlesTheYear;

        Earnings = earnings.ToDictionary(e => e.Code, StringComparer.OrdinalIgnoreCase);
        Deductions = deductions.ToDictionary(d => d.Code, StringComparer.OrdinalIgnoreCase);
    }

    public PayrollRun Run { get; }

    public PayrollSettings Settings { get; }

    public PremiumMatrix Premiums { get; }

    public StatutorySnapshot Statutory { get; }

    public IReadOnlyDictionary<string, EarningType> Earnings { get; }

    public IReadOnlyDictionary<string, DeductionType> Deductions { get; }

    /// <summary>
    /// True when this run settles the calendar year's withholding: the last
    /// regular cut-off of the year, or a final pay, and only while
    /// <see cref="PayrollSettings.AnnualiseTaxOnFinalPeriod"/> is on.
    /// </summary>
    public bool SettlesTheYear { get; }

    /// <summary>
    /// The share of a month's statutory contribution this run carries.
    ///
    /// <para>The three strategies all sum to exactly one month across the
    /// month's runs, which is what keeps a remittance report reconciling with
    /// what was actually deducted. On weekly and daily cycles "first" and
    /// "second payroll" have no meaning, so the contribution is spread evenly
    /// whichever strategy is set.</para>
    /// </summary>
    public decimal ContributionShare
    {
        get
        {
            var runs = Math.Max(1, Run.RunsInMonth);

            if (runs == 1)
                return 1m;

            var spreadOnly = Run.Frequency is PayFrequency.Weekly or PayFrequency.Daily;

            if (spreadOnly || Settings.ContributionSchedule == ContributionSchedule.SplitEvenly)
                return 1m / runs;

            return Settings.ContributionSchedule switch
            {
                ContributionSchedule.FirstPayrollOfMonth => Run.SequenceInMonth == 1 ? 1m : 0m,
                _ => Run.SequenceInMonth >= runs ? 1m : 0m
            };
        }
    }

    /// <summary>The earning type behind a code, or null when it has been retired.</summary>
    public EarningType? Earning(string code) =>
        Earnings.TryGetValue(code, out var type) && type.IsActive ? type : null;

    public DeductionType? Deduction(string code) =>
        Deductions.TryGetValue(code, out var type) && type.IsActive ? type : null;
}

/// <summary>
/// A computed payslip and its lines, before either is written. The run service
/// persists it; the calculator never touches the database.
/// </summary>
public sealed class PayslipDraft
{
    public required Payslip Payslip { get; init; }

    public required IReadOnlyList<PayslipLine> Lines { get; init; }

    /// <summary>
    /// Loans that were collected, and how much of each, so posting can move the
    /// balances without recomputing them.
    /// </summary>
    public required IReadOnlyList<(EmployeeLoan Loan, decimal Amount)> LoanCollections { get; init; }

    /// <summary>
    /// Anything that stopped the calculation being trustworthy: a missing
    /// premium cell, a missing statutory table, a rate of zero. A run with these
    /// cannot be submitted for approval.
    /// </summary>
    public required IReadOnlyList<string> Blockers { get; init; }

    public bool IsBlocked => Blockers.Count > 0;
}
