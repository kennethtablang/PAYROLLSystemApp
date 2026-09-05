using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-018: one row per salary rate adjustment, with the date it took effect.
///
/// <para>The employee record carries only the rate in force now. That is what a
/// new payroll run reads, and it is all it needs — but it cannot answer "what
/// was this person earning in March", which is the question an employee raises
/// when a back-dated increase lands, and the one an auditor asks about a payslip
/// from two years ago.</para>
///
/// <para>Rows are written by <c>EmployeeService</c> whenever the rate, the pay
/// type or the pay frequency changes, and they are never edited afterwards. A
/// correction is another row, so the sequence of what was decided when stays
/// intact.</para>
/// </summary>
[Table("salary_rate_history")]
public class SalaryRateHistory
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int EmployeeId { get; set; }

    /// <summary>
    /// The date the new rate applies from. Defaults to today, but a rate agreed
    /// late and back-dated to the start of a cutoff is normal, so it is entered
    /// rather than assumed.
    /// </summary>
    public DateTime EffectiveDate { get; set; } = DateTime.Today;

    public PayType PreviousPayType { get; set; }

    public decimal PreviousRate { get; set; }

    public PayFrequency PreviousFrequency { get; set; }

    public PayType NewPayType { get; set; }

    public decimal NewRate { get; set; }

    public PayFrequency NewFrequency { get; set; }

    /// <summary>Why the rate changed — promotion, regularisation, annual review.</summary>
    [MaxLength(200)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>Username of whoever made the change, matching the audit log.</summary>
    [MaxLength(120)]
    public string RecordedBy { get; set; } = string.Empty;

    public DateTime RecordedUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public decimal Difference => NewRate - PreviousRate;

    [Ignore]
    public bool IsIncrease => NewRate > PreviousRate;

    /// <summary>
    /// Percentage movement. A previous rate of zero — a first rate being set —
    /// has no percentage to report, so it returns null rather than dividing by
    /// zero or claiming an infinite rise.
    /// </summary>
    [Ignore]
    public decimal? PercentChange =>
        PreviousRate == 0 ? null : Math.Round((NewRate - PreviousRate) / PreviousRate * 100m, 2);

    [Ignore]
    public string ChangeDisplay
    {
        get
        {
            // The sign goes before the amount, not inside it: "-1,500.00", never
            // a bare minus glued to a formatted number that reads as a typo.
            var sign = Difference >= 0 ? "+" : "-";
            var body = $"{sign}{Math.Abs(Difference):N2}";

            return PercentChange is { } pct
                ? $"{body}  ({(pct >= 0 ? "+" : "-")}{Math.Abs(pct):N1}%)"
                : body;
        }
    }

    [Ignore]
    public string FromDisplay => Describe(PreviousPayType, PreviousRate);

    [Ignore]
    public string ToDisplay => Describe(NewPayType, NewRate);

    private static string Describe(PayType type, decimal rate) => type switch
    {
        PayType.Monthly => $"{rate:N2} / month",
        PayType.Daily => $"{rate:N2} / day",
        PayType.Hourly => $"{rate:N2} / hour",
        _ => rate.ToString("N2")
    };
}
