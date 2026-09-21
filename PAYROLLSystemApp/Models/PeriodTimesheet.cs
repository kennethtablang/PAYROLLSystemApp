using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// One employee's rendered time for one cut-off, as accounting keys it from the
/// client's timesheet.
///
/// <para><b>This is a period total, not a day.</b> The paper that reaches
/// accounting carries no dates — it carries a count of days and three buckets of
/// overtime hours already classified by whoever kept the post's logbook. There
/// is nothing to spread across a calendar, and inventing dates so the day-walking
/// path could consume it would be fabricating a record of when hours were worked.
/// So the totals are stored as totals, and
/// <see cref="Services.PayrollCalculator"/> prices them directly.</para>
///
/// <para><b>The three overtime buckets are premium buckets, not holiday
/// classifications.</b> The sheet's columns are the increments the client pays
/// over the day already counted in <see cref="Days"/>:</para>
/// <list type="bullet">
///   <item><description><see cref="RegularOtHours"/> — ordinary overtime, +25%.</description></item>
///   <item><description><see cref="SpecialHolidayOtHours"/> — the +30% bucket: the
///   sheet's "spcl hol. ot" column, which also carries rest-day hours because
///   both pay the same increment.</description></item>
///   <item><description><see cref="LegalHolidayOtHours"/> — the +100% bucket: the
///   sheet's "leg Hol ot" column.</description></item>
/// </list>
///
/// <para>One row per employee per run. Keyed by run rather than by pay period so
/// that a re-run of the same cut-off — a correction, or a second run after a
/// discarded one — starts from its own sheet and cannot silently inherit
/// figures typed for a different run.</para>
/// </summary>
[Table("period_timesheets")]
public class PeriodTimesheet
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ix_timesheet_run_employee", Order = 1)]
    public int RunId { get; set; }

    [Indexed(Name = "ix_timesheet_run_employee", Order = 2)]
    public int EmployeeId { get; set; }

    /// <summary>
    /// The post the sheet was filed under. Snapshotted from the employee at the
    /// time of keying so a later redeployment does not move a typed sheet to a
    /// different client's paperwork.
    /// </summary>
    [Indexed]
    public int DetachmentId { get; set; }

    // ------------------------------------------------------- the seven columns

    /// <summary>"# of days" — days rendered in the cut-off, priced at the daily rate.</summary>
    public decimal Days { get; set; }

    /// <summary>"regular ot" — ordinary overtime hours.</summary>
    public decimal RegularOtHours { get; set; }

    /// <summary>"spcl hol. ot" — the +30% overtime bucket.</summary>
    public decimal SpecialHolidayOtHours { get; set; }

    /// <summary>"leg Hol ot" — the +100% overtime bucket.</summary>
    public decimal LegalHolidayOtHours { get; set; }

    /// <summary>"night shift" — hours falling inside the night differential window.</summary>
    public decimal NightShiftHours { get; set; }

    /// <summary>
    /// "comp loan" — the company loan instalment for this cut-off, in pesos.
    ///
    /// <para>Typed rather than derived: the sheet is the instruction. A loan
    /// held in <see cref="EmployeeLoan"/> still amortises on its own; this is
    /// the figure the client's paperwork asks to be taken, and it is applied as
    /// keyed.</para>
    /// </summary>
    public decimal CompanyLoan { get; set; }

    /// <summary>
    /// "Late" — tardiness for the cut-off, in pesos, taken off gross.
    ///
    /// <para>An <b>amount</b>, not minutes. The post's logbook prices lateness
    /// before the sheet is typed, and re-deriving it here from a minute count
    /// nobody recorded would produce a different figure from the one the client
    /// was billed. Stored positive; it subtracts.</para>
    /// </summary>
    public decimal LateAmount { get; set; }

    // ---------------------------------------------------------------- context

    /// <summary>
    /// The free-text band the sheet carries against some rows — "UNIFORM",
    /// "new rate 2nd tranche", "NO ATM". Kept because it is what the keyer will
    /// look for when a figure is queried; it prices nothing.
    /// </summary>
    [MaxLength(120)]
    public string Note { get; set; } = string.Empty;

    /// <summary>
    /// The daily rate in force for the detachment when this row was keyed,
    /// snapshotted so the entry screen's arithmetic can be reproduced even after
    /// a later wage order supersedes it. The run reads the rate table itself and
    /// does not trust this figure.
    /// </summary>
    public decimal RateWhenKeyed { get; set; }

    [MaxLength(120)]
    public string RecordedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>True once any figure has been keyed, which is what "entered" means on the grid.</summary>
    [Ignore]
    public bool HasFigures =>
        Days != 0m || RegularOtHours != 0m || SpecialHolidayOtHours != 0m ||
        LegalHolidayOtHours != 0m || NightShiftHours != 0m ||
        CompanyLoan != 0m || LateAmount != 0m;

    /// <summary>Every overtime bucket added together, for a total on the grid.</summary>
    [Ignore]
    public decimal TotalOtHours => RegularOtHours + SpecialHolidayOtHours + LegalHolidayOtHours;

    public PeriodTimesheet Clone() => (PeriodTimesheet)MemberwiseClone();
}
