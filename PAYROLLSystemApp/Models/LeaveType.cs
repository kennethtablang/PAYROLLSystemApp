using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-030. A kind of leave, and the two facts about it that reach a payslip:
/// how many days a year it grants, and whether those days are paid.
///
/// <para><b>Configuration, not code.</b> Company leave policy differs between
/// employers and changes at renewal, so the credits and the paid flag are
/// editable rows (NFR-031). Only the statutory minima are fixed — five days of
/// Service Incentive Leave under Art. 95, 105 days of maternity leave under
/// RA 11210, seven of paternity leave under RA 8187 — and even those are floors
/// a company may exceed.</para>
///
/// <para>Types are retired, never deleted: a leave request approved two years
/// ago still names the type it was taken against.</para>
/// </summary>
[Table("leave_types")]
public class LeaveType
{
    /// <summary>
    /// Art. 95 service incentive leave. Named because payroll has to recognise
    /// it: when <c>PayrollSettings.AccrueServiceIncentiveLeave</c> is on, the
    /// entitlement is already paid a slice at a time as 5Days Inc., and cashing
    /// the credits out as well would pay it twice.
    /// </summary>
    public const string ServiceIncentiveLeaveCode = "SIL";

    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ux_leave_types_code", Order = 1, Unique = true), MaxLength(20), NotNull]
    public string Code { get; set; } = string.Empty;

    [MaxLength(60), NotNull]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Days granted for a full calendar year. Decimal because half-day filings
    /// are ordinary, and because a monthly accrual of 5 ÷ 12 is not a whole number.
    /// </summary>
    public decimal DefaultAnnualCredits { get; set; }

    /// <summary>
    /// FR-033. Paid leave is compensated as though the day were worked; unpaid
    /// leave is an authorised absence that still costs the employee the day.
    /// The payroll engine reads this, which is why it lives on the type rather
    /// than being decided per request.
    /// </summary>
    public bool IsPaid { get; set; } = true;

    /// <summary>
    /// True when credits are earned month by month rather than granted whole on
    /// 1 January. Service Incentive Leave accrues this way; most company leave
    /// is granted upfront.
    /// </summary>
    public bool AccruesMonthly { get; set; }

    /// <summary>
    /// Art. 95: unused Service Incentive Leave is convertible to cash at year
    /// end and on separation. Read by final pay (FR-062), not by an ordinary run.
    /// </summary>
    public bool IsConvertibleToCash { get; set; }

    public LeaveApplicability AppliesTo { get; set; } = LeaveApplicability.Everyone;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    [Ignore]
    public string Display => $"{Code} — {Name}";

    [Ignore]
    public string PayDisplay => IsPaid ? "Paid" : "Unpaid";

    [Ignore]
    public string CreditsDisplay => AccruesMonthly
        ? $"{DefaultAnnualCredits:0.##} a year, accrued monthly"
        : $"{DefaultAnnualCredits:0.##} a year";

    [Ignore]
    public string AppliesToDisplay => LeaveEnumNames.Display(AppliesTo);

    /// <summary>
    /// Credits an employee has earned by a given date in the year.
    ///
    /// <para>An upfront grant is whole from 1 January. A monthly accrual is
    /// earned a twelfth at a time, and only for months the employee was actually
    /// employed — someone hired in September has not earned a full year of
    /// Service Incentive Leave by December.</para>
    /// </summary>
    public decimal CreditsEarnedBy(DateTime asOf, DateTime hireDate, DateTime? separationDate)
    {
        var year = asOf.Year;

        // Nothing accrues before the employee arrives or after they leave.
        if (hireDate.Year > year)
            return 0m;

        if (separationDate is { } left && left.Year < year)
            return 0m;

        if (!AccruesMonthly)
            return DefaultAnnualCredits;

        var firstMonth = hireDate.Year == year ? hireDate.Month : 1;

        var lastMonth = asOf.Month;
        if (separationDate is { } end && end.Year == year)
            lastMonth = Math.Min(lastMonth, end.Month);

        var months = Math.Max(0, lastMonth - firstMonth + 1);

        return Math.Round(DefaultAnnualCredits * months / 12m, 2, MidpointRounding.AwayFromZero);
    }
}
