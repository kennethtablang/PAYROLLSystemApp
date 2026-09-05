using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-032. One employee's standing in one leave type for one calendar year:
/// what they started with, what they earned, what they have spent.
///
/// <para><b>Four figures, not one running total.</b> A single "remaining" column
/// would answer the question the screen asks and none of the questions a dispute
/// asks — where the credits came from, and what happened to them. Keeping the
/// opening balance, the accrual, the manual adjustments and the usage apart
/// means <see cref="Remaining"/> is always derivable and never has to be
/// trusted on its own.</para>
///
/// <para>Rows are per year, so last year's position is still readable after the
/// new year's credits are granted.</para>
/// </summary>
[Table("leave_balances")]
public class LeaveBalance
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ix_leave_balances_key", Order = 1)]
    public int EmployeeId { get; set; }

    [Indexed(Name = "ix_leave_balances_key", Order = 2)]
    public int LeaveTypeId { get; set; }

    [Indexed(Name = "ix_leave_balances_key", Order = 3)]
    public int Year { get; set; } = DateTime.Today.Year;

    /// <summary>Days carried in from the previous year, where policy allows it.</summary>
    public decimal OpeningCredits { get; set; }

    /// <summary>Days granted or accrued this year, from the type's annual credits.</summary>
    public decimal EarnedCredits { get; set; }

    /// <summary>Days spent on approved, paid leave.</summary>
    public decimal UsedCredits { get; set; }

    /// <summary>
    /// Manual correction, positive or negative, with the reason in the audit log.
    /// Held separately so an adjustment never looks like an accrual.
    /// </summary>
    public decimal AdjustmentCredits { get; set; }

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    /// <summary>Everything granted this year, before anything was spent.</summary>
    [Ignore]
    public decimal TotalCredits => OpeningCredits + EarnedCredits + AdjustmentCredits;

    /// <summary>
    /// FR-032, FR-034. What is left to file against. Can go negative only through
    /// an adjustment; an approval never takes it below zero on its own.
    /// </summary>
    [Ignore]
    public decimal Remaining => TotalCredits - UsedCredits;

    [Ignore]
    public bool IsExhausted => Remaining <= 0m;

    [Ignore]
    public string RemainingDisplay => $"{Remaining:0.##}";

    [Ignore]
    public string UsedDisplay => $"{UsedCredits:0.##}";

    [Ignore]
    public string TotalDisplay => $"{TotalCredits:0.##}";

    /// <summary>Where the total came from, for the row that shows the balance.</summary>
    [Ignore]
    public string Breakdown
    {
        get
        {
            var parts = new List<string>();

            if (OpeningCredits != 0m)
                parts.Add($"{OpeningCredits:0.##} carried over");

            parts.Add($"{EarnedCredits:0.##} earned");

            if (AdjustmentCredits != 0m)
                parts.Add($"{AdjustmentCredits:+0.##;-0.##} adjusted");

            parts.Add($"{UsedCredits:0.##} used");

            return string.Join(" · ", parts);
        }
    }
}
