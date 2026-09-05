using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-043. One row of the SSS contribution schedule: a range of monthly
/// compensation, the Monthly Salary Credit it maps to, and what each side pays.
///
/// <para><b>Why the MSC is stored and not computed.</b> The contribution is not
/// a percentage of salary — it is a percentage of the salary <em>credit</em>,
/// which is the bracket the salary falls into. Two employees ₱400 apart can pay
/// the same amount, and one peso can move an employee a bracket. Storing the
/// schedule is the only way to be right at a boundary.</para>
///
/// <para>Above an MSC of ₱20,000 the contribution splits: the regular SS portion
/// stops there and the excess goes to the WISP, the mandatory provident fund.
/// Both are remitted, but they are separate lines on the remittance report, so
/// they are separate columns here.</para>
/// </summary>
[Table("sss_brackets")]
public class SssBracket
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>C-02: the circular this schedule came into force under.</summary>
    [Indexed]
    public DateTime EffectiveFrom { get; set; }

    /// <summary>Lower bound of monthly compensation, inclusive.</summary>
    public decimal RangeFrom { get; set; }

    /// <summary>
    /// Upper bound of monthly compensation, exclusive. <b>Null is the top
    /// bracket</b>, which is open-ended: a salary above the ceiling still lands
    /// somewhere rather than falling out of the table.
    ///
    /// <para>Null rather than a very large number, because "no upper bound" is a
    /// fact about the schedule and not a figure — and because a sentinel near
    /// <see cref="decimal.MaxValue"/> is exactly the kind of value that does not
    /// survive a round trip through the database intact.</para>
    /// </summary>
    public decimal? RangeTo { get; set; }

    /// <summary>The Monthly Salary Credit the range maps to.</summary>
    public decimal MonthlySalaryCredit { get; set; }

    /// <summary>Employee share of the regular SS contribution.</summary>
    public decimal EmployeeShare { get; set; }

    /// <summary>Employer share of the regular SS contribution.</summary>
    public decimal EmployerShare { get; set; }

    /// <summary>
    /// Employees' Compensation. Employer-only, and a flat ₱10 or ₱30 rather
    /// than a percentage.
    /// </summary>
    public decimal EmployerEc { get; set; }

    /// <summary>Employee share of the WISP portion, zero below the ₱20,000 MSC.</summary>
    public decimal EmployeeWisp { get; set; }

    /// <summary>Employer share of the WISP portion.</summary>
    public decimal EmployerWisp { get; set; }

    public bool IsActive { get; set; } = true;

    // -------------------------------------------------- derived display

    [Ignore]
    public decimal EmployeeTotal => EmployeeShare + EmployeeWisp;

    [Ignore]
    public decimal EmployerTotal => EmployerShare + EmployerWisp + EmployerEc;

    [Ignore]
    public string RangeDisplay => RangeTo is not { } upper
        ? $"₱{RangeFrom:N0} and above"
        : $"₱{RangeFrom:N0} – ₱{upper - 0.01m:N2}";

    [Ignore]
    public string Display => $"MSC ₱{MonthlySalaryCredit:N0} · EE ₱{EmployeeTotal:N2} · ER ₱{EmployerTotal:N2}";

    public bool Covers(decimal monthlyCompensation) =>
        monthlyCompensation >= RangeFrom && (RangeTo is not { } upper || monthlyCompensation < upper);
}

/// <summary>
/// FR-043. The PhilHealth premium, which unlike SSS really is a percentage —
/// of monthly <em>basic salary</em>, floored and capped.
///
/// <para>One row per rate change rather than a bracket table, because the
/// schedule is a single rate between two bounds. Universal Health Care Act
/// (RA 11223) sets the rate; it has moved most years since 2019.</para>
/// </summary>
[Table("philhealth_rates")]
public class PhilHealthRate
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public DateTime EffectiveFrom { get; set; }

    /// <summary>Premium as a percentage of basic salary, e.g. 5.0.</summary>
    public decimal PremiumRatePercent { get; set; }

    /// <summary>Salaries below this pay the premium on this figure.</summary>
    public decimal SalaryFloor { get; set; }

    /// <summary>Salaries above this pay the premium on this figure.</summary>
    public decimal SalaryCeiling { get; set; }

    [MaxLength(200)]
    public string Remarks { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    // -------------------------------------------------- derived display

    [Ignore]
    public string Display =>
        $"{PremiumRatePercent:0.##}% of basic · floor ₱{SalaryFloor:N0} · ceiling ₱{SalaryCeiling:N0}";

    [Ignore]
    public string EffectiveDisplay => $"from {EffectiveFrom:dd MMM yyyy}";

    /// <summary>
    /// The monthly premium for a basic salary, before it is split between the
    /// two sides. Half is the employee's; the employer pays the remainder
    /// rather than a second rounded half, so the two always add back exactly.
    /// </summary>
    public decimal PremiumFor(decimal monthlyBasic)
    {
        var basis = Math.Clamp(monthlyBasic, SalaryFloor, SalaryCeiling);
        return Math.Round(basis * PremiumRatePercent / 100m, 2, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// FR-043. The Pag-IBIG (HDMF) contribution: a percentage of monthly
/// compensation, capped at a fund salary ceiling, with a lower employee rate for
/// the lowest earners.
/// </summary>
[Table("pagibig_rates")]
public class PagIbigRate
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public DateTime EffectiveFrom { get; set; }

    /// <summary>Monthly compensation at or below this pays the lower employee rate.</summary>
    public decimal LowerRateThreshold { get; set; }

    /// <summary>Employee rate at or below the threshold, e.g. 1.0.</summary>
    public decimal EmployeeRateLowPercent { get; set; }

    /// <summary>Employee rate above the threshold, e.g. 2.0.</summary>
    public decimal EmployeeRateHighPercent { get; set; }

    /// <summary>Employer rate, which does not vary with the threshold.</summary>
    public decimal EmployerRatePercent { get; set; }

    /// <summary>
    /// The fund salary ceiling. Compensation above it contributes on this
    /// figure, which is what makes the contribution effectively flat at the top.
    /// </summary>
    public decimal FundSalaryCap { get; set; }

    [MaxLength(200)]
    public string Remarks { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    // -------------------------------------------------- derived display

    [Ignore]
    public string Display =>
        $"EE {EmployeeRateLowPercent:0.##}% up to ₱{LowerRateThreshold:N0}, then {EmployeeRateHighPercent:0.##}% · " +
        $"ER {EmployerRatePercent:0.##}% · cap ₱{FundSalaryCap:N0}";

    [Ignore]
    public string EffectiveDisplay => $"from {EffectiveFrom:dd MMM yyyy}";

    /// <summary>
    /// The employee and employer shares for a month's compensation.
    ///
    /// <para>The rate is chosen on the <em>uncapped</em> compensation but applied
    /// to the capped fund salary: an employee earning ₱30,000 is plainly above
    /// the ₱1,500 threshold even though their contribution is worked out on
    /// ₱10,000.</para>
    /// </summary>
    public (decimal Employee, decimal Employer) SharesFor(decimal monthlyCompensation)
    {
        var basis = Math.Min(monthlyCompensation, FundSalaryCap);

        var employeeRate = monthlyCompensation <= LowerRateThreshold
            ? EmployeeRateLowPercent
            : EmployeeRateHighPercent;

        return (
            Math.Round(basis * employeeRate / 100m, 2, MidpointRounding.AwayFromZero),
            Math.Round(basis * EmployerRatePercent / 100m, 2, MidpointRounding.AwayFromZero));
    }
}

/// <summary>
/// FR-043, FR-054. One band of the BIR withholding tax table (TRAIN, RA 10963,
/// as tabulated in RR 11-2018 Annex E).
///
/// <para>There is one bracket set per pay frequency plus an annual set. The
/// per-period sets are what a run withholds against; the annual set is what the
/// year-end adjustment settles against, and the two are stored the same way so
/// the same code reads both.</para>
/// </summary>
[Table("tax_brackets")]
public class WithholdingTaxBracket
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public DateTime EffectiveFrom { get; set; }

    /// <summary>
    /// The frequency this band belongs to. <see cref="IsAnnual"/> rows carry the
    /// annual table and ignore this.
    /// </summary>
    public PayFrequency Frequency { get; set; } = PayFrequency.Monthly;

    /// <summary>True for the annual table used by the year-end settlement.</summary>
    public bool IsAnnual { get; set; }

    /// <summary>Lower bound of taxable income for the band, inclusive.</summary>
    public decimal LowerLimit { get; set; }

    /// <summary>
    /// Upper bound, exclusive. Null is the top band, which is open-ended — see
    /// <see cref="SssBracket.RangeTo"/> for why it is not a sentinel figure.
    /// </summary>
    public decimal? UpperLimit { get; set; }

    /// <summary>The fixed amount of tax at the bottom of the band.</summary>
    public decimal BaseTax { get; set; }

    /// <summary>Percentage charged on income above <see cref="LowerLimit"/>.</summary>
    public decimal RateOnExcessPercent { get; set; }

    public bool IsActive { get; set; } = true;

    // -------------------------------------------------- derived display

    [Ignore]
    public string RangeDisplay => UpperLimit is not { } upper
        ? $"Over ₱{LowerLimit:N0}"
        : LowerLimit == 0
            ? $"₱0 – ₱{upper:N0}"
            : $"₱{LowerLimit:N0} – ₱{upper:N0}";

    [Ignore]
    public string TaxDisplay => BaseTax == 0 && RateOnExcessPercent == 0
        ? "No tax"
        : BaseTax == 0
            ? $"{RateOnExcessPercent:0.##}% of the excess over ₱{LowerLimit:N0}"
            : $"₱{BaseTax:N2} + {RateOnExcessPercent:0.##}% of the excess over ₱{LowerLimit:N0}";

    [Ignore]
    public string ScopeDisplay => IsAnnual
        ? "Annual"
        : EmployeeEnumNames.Display(Frequency);

    public bool Covers(decimal taxableIncome) =>
        taxableIncome >= LowerLimit && (UpperLimit is not { } upper || taxableIncome < upper);

    /// <summary>The tax due on an amount that falls in this band.</summary>
    public decimal TaxOn(decimal taxableIncome) =>
        Math.Round(BaseTax + (taxableIncome - LowerLimit) * RateOnExcessPercent / 100m,
            2, MidpointRounding.AwayFromZero);
}
