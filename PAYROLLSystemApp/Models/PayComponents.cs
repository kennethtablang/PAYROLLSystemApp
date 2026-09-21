using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-041. A kind of money that adds to gross pay, and the two facts that decide
/// what it costs: whether it is taxable, and whether it counts towards the
/// statutory contribution base.
///
/// <para><b>Those two flags are not the same question.</b> Withholding tax and
/// the SSS schedule read different bases — a de-minimis rice allowance within
/// its cap is neither taxable nor part of the contribution base, while a taxable
/// allowance may still sit outside the base depending on how it was granted. One
/// flag would force a wrong answer on one of the two.</para>
///
/// <para>Types are retired, never deleted: a payslip printed two years ago still
/// names the earning it was paid under.</para>
/// </summary>
[Table("earning_types")]
public class EarningType
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ux_earning_types_code", Order = 1, Unique = true), MaxLength(20), NotNull]
    public string Code { get; set; } = string.Empty;

    [MaxLength(60), NotNull]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    public EarningCategory Category { get; set; } = EarningCategory.Allowance;

    public ComputationMethod Method { get; set; } = ComputationMethod.FixedAmount;

    /// <summary>
    /// The amount or rate the method reads: pesos for
    /// <see cref="ComputationMethod.FixedAmount"/>, a percentage for the
    /// percent-of methods, a multiplier for the rate-per methods. Zero for the
    /// methods that take their figure from attendance or a statutory table.
    /// </summary>
    public decimal DefaultAmount { get; set; }

    /// <summary>
    /// FR-041. Taxable earnings enter the withholding base; non-taxable ones do
    /// not, though they still appear on the payslip and in the reports.
    /// </summary>
    public bool IsTaxable { get; set; } = true;

    /// <summary>
    /// Whether the amount is added to monthly compensation before the SSS and
    /// Pag-IBIG schedules are read. PhilHealth is computed on basic salary
    /// alone, so it ignores this.
    /// </summary>
    public bool IsPartOfContributionBase { get; set; }

    /// <summary>
    /// PD 851: 13th month pay is a twelfth of <em>basic salary</em> earned in the
    /// year. Overtime, night differential and allowances are excluded, so each
    /// earning states whether it counts.
    /// </summary>
    public bool IsThirteenthMonthBase { get; set; }

    /// <summary>
    /// True when the amount is paid every period without being asked for, e.g.
    /// a monthly transport allowance. One-off earnings are added per run
    /// instead (FR-057).
    /// </summary>
    public bool IsRecurring { get; set; }

    /// <summary>
    /// True for the codes the payroll engine itself produces — basic pay,
    /// overtime, the holiday premiums. Their code and category are fixed
    /// because the engine looks them up by code; everything else about them
    /// stays editable.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>Order the line appears in on a payslip. Lower is higher up.</summary>
    public int DisplayOrder { get; set; } = 100;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    [Ignore]
    public string Display => $"{Code} — {Name}";

    [Ignore]
    public string CategoryDisplay => PayrollEnumNames.Display(Category);

    [Ignore]
    public string MethodDisplay => PayrollEnumNames.Display(Method);

    [Ignore]
    public string TaxDisplay => IsTaxable ? "Taxable" : "Non-taxable";

    /// <summary>The configured figure, worded for the method that reads it.</summary>
    [Ignore]
    public string AmountDisplay => Method switch
    {
        ComputationMethod.FixedAmount => $"₱{DefaultAmount:N2} per period",
        ComputationMethod.PercentOfBasic => $"{DefaultAmount:0.##}% of basic",
        ComputationMethod.PercentOfGross => $"{DefaultAmount:0.##}% of gross",
        ComputationMethod.RatePerHour => $"{DefaultAmount:0.####}× hourly rate",
        ComputationMethod.RatePerDay => $"{DefaultAmount:0.####}× daily rate",
        ComputationMethod.StatutoryTable => "From the statutory table",
        ComputationMethod.TimeDerived => "From attendance",
        _ => "Entered per run"
    };

    [Ignore]
    public string FlagsDisplay
    {
        get
        {
            var parts = new List<string> { TaxDisplay };

            if (IsPartOfContributionBase)
                parts.Add("in contribution base");

            if (IsThirteenthMonthBase)
                parts.Add("13th month base");

            if (IsRecurring)
                parts.Add("recurring");

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// FR-042. A kind of money that comes off gross pay, and how it is worked out.
///
/// <para><see cref="ReducesTaxableIncome"/> is the flag that costs money if it
/// is wrong. The employee's SSS, PhilHealth and Pag-IBIG shares come off before
/// withholding tax is computed; a salary loan does not. Setting it on the wrong
/// row under-withholds tax the employer is liable for.</para>
/// </summary>
[Table("deduction_types")]
public class DeductionType
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ux_deduction_types_code", Order = 1, Unique = true), MaxLength(20), NotNull]
    public string Code { get; set; } = string.Empty;

    [MaxLength(60), NotNull]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    public DeductionCategory Category { get; set; } = DeductionCategory.Other;

    public ComputationMethod Method { get; set; } = ComputationMethod.FixedAmount;

    /// <summary>The amount or rate the method reads. See <see cref="EarningType.DefaultAmount"/>.</summary>
    public decimal DefaultAmount { get; set; }

    /// <summary>
    /// FR-054. True for the deductions taken off before withholding tax is
    /// computed — in practice the three statutory employee shares, and a union
    /// due where the company treats it that way.
    /// </summary>
    public bool ReducesTaxableIncome { get; set; }

    /// <summary>
    /// FR-055. True when the deduction carries an outstanding balance that is
    /// decremented each period rather than being taken indefinitely.
    /// </summary>
    public bool IsAmortised { get; set; }

    /// <summary>
    /// True for the codes the payroll engine produces itself — the statutory
    /// contributions, withholding tax, and the deductions derived from
    /// attendance. Their code and category are fixed.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// The order deductions are taken in when pay will not cover all of them.
    /// Statutory first, then loans, then everything else — so what gets short is
    /// the discretionary deduction rather than a remittance.
    /// </summary>
    public int Priority { get; set; } = 100;

    /// <summary>Order the line appears in on a payslip. Lower is higher up.</summary>
    public int DisplayOrder { get; set; } = 100;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    [Ignore]
    public string Display => $"{Code} — {Name}";

    [Ignore]
    public string CategoryDisplay => PayrollEnumNames.Display(Category);

    [Ignore]
    public string MethodDisplay => PayrollEnumNames.Display(Method);

    [Ignore]
    public string AmountDisplay => Method switch
    {
        ComputationMethod.FixedAmount => $"₱{DefaultAmount:N2} per period",
        ComputationMethod.PercentOfBasic => $"{DefaultAmount:0.##}% of basic",
        ComputationMethod.PercentOfGross => $"{DefaultAmount:0.##}% of gross",
        ComputationMethod.RatePerHour => $"{DefaultAmount:0.####}× hourly rate",
        ComputationMethod.RatePerDay => $"{DefaultAmount:0.####}× daily rate",
        ComputationMethod.StatutoryTable => "From the statutory table",
        ComputationMethod.TimeDerived => "From attendance",
        _ => "Entered per run"
    };

    [Ignore]
    public string FlagsDisplay
    {
        get
        {
            var parts = new List<string>();

            parts.Add(ReducesTaxableIncome ? "reduces taxable income" : "after tax");

            if (IsAmortised)
                parts.Add("amortised");

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// The codes the payroll engine looks components up by.
///
/// <para>They are constants rather than magic strings because the engine has to
/// find the basic pay line to compute a percentage against it, and the statutory
/// lines to take them off the tax base. A company may rename any of these; it
/// may not change the code, which is why the rows carry
/// <see cref="EarningType.IsSystem"/>.</para>
/// </summary>
public static class PayComponentCodes
{
    // Earnings produced by the engine.
    public const string BasicPay = "BASIC";

    /// <summary>
    /// Overtime as a single figure, whatever day it fell on.
    ///
    /// <para><b>Historical.</b> The engine now splits overtime across
    /// <see cref="OvertimeRegular"/>, <see cref="OvertimeRestDay"/> and
    /// <see cref="OvertimeHoliday"/>, because the payroll summary bills the
    /// three separately. Payslips computed before that change carry this code
    /// and cannot be split retroactively — the day each overtime hour fell on
    /// was not kept — so reports total them under the ordinary column and say
    /// so. The type stays seeded so those payslips still have a name.</para>
    /// </summary>
    public const string Overtime = "OT";

    /// <summary>Overtime on an ordinary working day.</summary>
    public const string OvertimeRegular = "OT_REG";

    /// <summary>
    /// Overtime on a rest day — the "Sunday" column of the payroll summary.
    /// Named for the rest day rather than the weekday because that is what the
    /// premium matrix and the employee's schedule actually model; a guard whose
    /// rest day is Tuesday is paid the same premium.
    /// </summary>
    public const string OvertimeRestDay = "OT_RD";

    /// <summary>
    /// Overtime on a regular or special non-working holiday, including one that
    /// fell on a rest day. <b>A holiday outranks a rest day here</b>: the hour
    /// was paid at the holiday-rest-day multiplier, so reporting it under the
    /// rest day column would understate what the holiday cost.
    /// </summary>
    public const string OvertimeHoliday = "OT_HOL";

    public const string NightDifferential = "ND";
    public const string RestDayPremium = "PREM_RD";
    public const string HolidayPay = "HOL";
    public const string HolidayUnworked = "HOL_UNWORKED";
    public const string RecurringAllowance = "ALW";
    public const string PaidLeave = "LEAVE";
    public const string ThirteenthMonth = "13TH";
    public const string LeaveConversion = "LEAVE_CONV";

    // Allowances the payroll summary reports in columns of their own. Ordinary
    // configurable earning types, not engine output — the engine never creates
    // them, but the report has to know which code is which.
    public const string SpecialEmergencyAllowance = "ALW_SEA";
    public const string CostOfLivingAllowance = "ALW_COLA";

    /// <summary>
    /// The legacy screen's <c>5Days Inc.</c> — the five days of Service Incentive
    /// Leave (Art. 95) accrued against the days actually rendered, rather than
    /// banked as credits and cashed out later.
    ///
    /// <para><b>The engine computes this; nobody types it.</b> Verified against
    /// the legacy entry screen for 08662 at ₱600/day: 600 × 5 ÷ 365 × 13 days =
    /// ₱106.85, to the centavo. The five and the 365 are
    /// <see cref="PayrollSettings.ServiceIncentiveLeaveDays"/> and
    /// <see cref="PayrollSettings.ServiceIncentiveLeaveDivisor"/>, because an
    /// agency that accrues over 261 working days instead pays a different figure
    /// for the same work and neither divisor belongs in this file.</para>
    /// </summary>
    public const string FiveSlip = "ALW_5SLIP";

    // Deductions produced by the engine.
    public const string Sss = "SSS";
    public const string SssWisp = "SSS_WISP";
    public const string PhilHealth = "PHIC";
    public const string PagIbig = "HDMF";
    public const string WithholdingTax = "WTAX";
    public const string Tardiness = "LATE";
    public const string Undertime = "UT";
    public const string Absence = "ABS";

    // Company deductions the payroll summary reports in columns of their own.
    public const string CompanyLoan = "CO_LOAN";
    public const string SecondUniform = "DED_UNIF2";

    // The legacy entry screen's remaining deduction slots. Ordinary configurable
    // deduction types — the engine never creates them — but they are constants
    // because they are what an EmployeeDeduction is normally set up against, and
    // because the seed has to be able to find them to avoid re-creating them.

    /// <summary>
    /// <c>Perf Bond</c>. Accumulated against the guard and refundable on
    /// separation, so it is a standing deduction rather than a loan: nothing
    /// amortises it down and it does not stop on its own.
    /// </summary>
    public const string PerformanceBond = "PERF_BOND";

    /// <summary>The legacy screen's <c>Prsing Fee</c> — the agency's processing charge.</summary>
    public const string ProcessingFee = "PRSG_FEE";

    /// <summary>
    /// <c>Insuran</c> on the legacy menu — the group life premium, taken every
    /// period at a flat figure for as long as the cover runs.
    /// </summary>
    public const string Insurance = "INSURANCE";
}
