namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-040. Where a pay period has got to.
///
/// <para>A period is <see cref="Open"/> while attendance can still move,
/// <see cref="Locked"/> once payroll has been computed against it, and
/// <see cref="Closed"/> when the run is posted. The three states exist so a
/// corrected time record cannot quietly change a cut-off that has already been
/// paid (FR-058).</para>
/// </summary>
public enum PayPeriodStatus
{
    Open = 0,
    Locked = 1,
    Closed = 2
}

/// <summary>
/// FR-041. What kind of money an earning line is. The category is what the
/// payroll engine keys on; the taxable flag is held separately because two
/// earnings of the same category can differ (a de-minimis allowance within its
/// cap is not taxable, one above it is).
/// </summary>
public enum EarningCategory
{
    BasicPay = 0,
    Overtime = 1,
    Allowance = 2,
    Bonus = 3,
    Commission = 4,
    HolidayPay = 5,
    NightDifferential = 6,
    Other = 7
}

/// <summary>
/// FR-042. What kind of money a deduction line is.
///
/// <para><see cref="Statutory"/> deductions come off before withholding tax is
/// computed and are remitted to an agency; the rest do not and are not. That
/// difference is the whole reason the category is stored.</para>
/// </summary>
public enum DeductionCategory
{
    Statutory = 0,
    Loan = 1,
    CashAdvance = 2,
    Tardiness = 3,
    Absence = 4,
    Other = 5
}

/// <summary>
/// FR-041, FR-042. How a component's amount is arrived at.
///
/// <para><see cref="StatutoryTable"/> and <see cref="TimeDerived"/> are the two
/// that take no figure from the configuration row at all: the first reads the
/// effective-dated contribution or tax table, the second is computed from the
/// attendance record and the employee's rate.</para>
/// </summary>
public enum ComputationMethod
{
    /// <summary>A fixed peso amount each period.</summary>
    FixedAmount = 0,

    /// <summary>A percentage of the employee's basic pay for the period.</summary>
    PercentOfBasic = 1,

    /// <summary>A percentage of gross pay for the period.</summary>
    PercentOfGross = 2,

    /// <summary>Rate × hours, taken from the day's premium multiplier.</summary>
    RatePerHour = 3,

    /// <summary>Rate × days.</summary>
    RatePerDay = 4,

    /// <summary>Read from the effective-dated SSS, PhilHealth, Pag-IBIG or BIR table.</summary>
    StatutoryTable = 5,

    /// <summary>Derived from attendance — late minutes, undertime, absent days.</summary>
    TimeDerived = 6,

    /// <summary>Entered per run as a one-off (FR-057).</summary>
    Manual = 7
}

/// <summary>
/// How a monthly statutory contribution is spread across the runs of a month.
///
/// <para>SSS, PhilHealth and Pag-IBIG are all computed on a <b>monthly</b>
/// basis. On a semi-monthly payroll that monthly figure has to be applied
/// somewhere, and all three options below sum to exactly one month across the
/// month's runs — which is what keeps the remittance report reconciling with
/// what was actually deducted.</para>
///
/// <para>On weekly and daily cycles "first" and "second payroll" mean nothing,
/// so the contribution is spread evenly whichever option is set.</para>
/// </summary>
public enum ContributionSchedule
{
    /// <summary>Taken whole on the last run of the month. The common practice.</summary>
    LastPayrollOfMonth = 0,

    /// <summary>Split equally across the month's runs.</summary>
    SplitEvenly = 1,

    /// <summary>Taken whole on the first run of the month.</summary>
    FirstPayrollOfMonth = 2
}

/// <summary>
/// Display names for the payroll configuration enumerations, in one place so
/// the setup screens, the payslip and a later report cannot word the same value
/// differently (NFR-024).
/// </summary>
public static class PayrollEnumNames
{
    public static string Display(PayPeriodStatus value) => value switch
    {
        PayPeriodStatus.Open => "Open",
        PayPeriodStatus.Locked => "Locked",
        PayPeriodStatus.Closed => "Closed",
        _ => value.ToString()
    };

    public static string Display(EarningCategory value) => value switch
    {
        EarningCategory.BasicPay => "Basic pay",
        EarningCategory.HolidayPay => "Holiday pay",
        EarningCategory.NightDifferential => "Night differential",
        _ => value.ToString()
    };

    public static string Display(DeductionCategory value) => value.ToString();

    public static string Display(ComputationMethod value) => value switch
    {
        ComputationMethod.FixedAmount => "Fixed amount",
        ComputationMethod.PercentOfBasic => "Percent of basic pay",
        ComputationMethod.PercentOfGross => "Percent of gross pay",
        ComputationMethod.RatePerHour => "Rate per hour",
        ComputationMethod.RatePerDay => "Rate per day",
        ComputationMethod.StatutoryTable => "From statutory table",
        ComputationMethod.TimeDerived => "From attendance",
        ComputationMethod.Manual => "Entered per run",
        _ => value.ToString()
    };

    public static string Display(PayrollRunStatus value) => value switch
    {
        PayrollRunStatus.ForApproval => "For approval",
        _ => value.ToString()
    };

    public static string Display(PayrollRunType value) => value switch
    {
        PayrollRunType.ThirteenthMonth => "13th month pay",
        PayrollRunType.FinalPay => "Final pay",
        _ => value.ToString()
    };

    public static string Display(PayslipLineKind value) => value.ToString();

    public static string Display(ContributionSchedule value) => value switch
    {
        ContributionSchedule.LastPayrollOfMonth => "Whole, on the last run of the month",
        ContributionSchedule.SplitEvenly => "Split evenly across the month",
        ContributionSchedule.FirstPayrollOfMonth => "Whole, on the first run of the month",
        _ => value.ToString()
    };

    /// <summary>
    /// How many runs of a frequency fall inside one month. Used to spread a
    /// monthly statutory contribution across the month's runs.
    ///
    /// <para>The yearly count lives on
    /// <see cref="EmployeeEnumNames.PeriodsPerYear"/>, which already had it.</para>
    /// </summary>
    public static int RunsPerMonth(PayFrequency frequency) => frequency switch
    {
        PayFrequency.Monthly => 1,
        PayFrequency.SemiMonthly => 2,
        PayFrequency.BiWeekly => 2,
        PayFrequency.Weekly => 4,
        PayFrequency.Daily => 22,
        _ => 1
    };

    /// <summary>
    /// Whether the BIR publishes a withholding table for a pay frequency.
    ///
    /// <para>RR 11-2018 Annex E tabulates daily, weekly, semi-monthly and
    /// monthly. <b>There is no bi-weekly table.</b> A company paying every two
    /// weeks has to withhold on another basis — commonly the weekly table
    /// applied twice — and the setup screen says so rather than quietly reading
    /// the weekly brackets against a fortnight's pay, which would under-withhold
    /// every period.</para>
    /// </summary>
    public static bool HasPublishedTaxTable(PayFrequency frequency) =>
        frequency is PayFrequency.Daily or PayFrequency.Weekly
            or PayFrequency.SemiMonthly or PayFrequency.Monthly;
}
