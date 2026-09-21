using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// A deduction taken from one employee every period, at a flat figure, for as
/// long as it is in force — the insurance premium, the performance bond, the
/// processing fee. The legacy system's <c>Insuran Set-Up</c>.
///
/// <para><b>Why this is not an <see cref="EmployeeLoan"/>.</b> A loan is
/// authorised by its balance: it stops when the outstanding amount reaches zero
/// and the final instalment is whatever is left. None of these three behave that
/// way. An insurance premium runs while the cover runs; a performance bond
/// accumulates <em>towards</em> a figure instead of down from one, and is
/// refunded rather than repaid. Modelling them as loans would mean inventing a
/// principal nobody agreed to and having the deduction stop by itself on a
/// period nobody chose.</para>
///
/// <para><b>And why not a per-run adjustment.</b> An adjustment (FR-057) is a
/// one-off and demands a remark every time. Typing the same ₱100 premium for
/// three hundred guards every cut-off is how a period ends up with the premium
/// missing from four of them, and the remark stops meaning anything by the
/// second period.</para>
///
/// <para><b>It ends by date, not by exhaustion.</b> <see cref="EndsOn"/> is the
/// last pay date the deduction is taken on. Left null it runs until somebody
/// deactivates it, which is the honest default for a premium with no agreed
/// end.</para>
/// </summary>
[Table("employee_deductions")]
public class EmployeeDeduction
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int EmployeeId { get; set; }

    /// <summary>
    /// The <see cref="DeductionType"/> the line is taken under, so the payslip
    /// line and the deduction configuration cannot drift apart. The same reason
    /// <see cref="EmployeeLoan.DeductionCode"/> exists.
    /// </summary>
    [MaxLength(20), NotNull]
    public string DeductionCode { get; set; } = string.Empty;

    [MaxLength(60)]
    public string DeductionName { get; set; } = string.Empty;

    /// <summary>What is taken each period.</summary>
    public decimal Amount { get; set; }

    /// <summary>
    /// Taken from the first run whose <b>pay date</b> is on or after this — the
    /// same date the wage rates and statutory tables are read at, so a run
    /// cannot take one of these from one month and its contribution schedule
    /// from another.
    /// </summary>
    public DateTime StartsOn { get; set; } = DateTime.Today;

    /// <summary>
    /// The last pay date it is taken on, or null while it runs indefinitely.
    /// </summary>
    public DateTime? EndsOn { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>The policy number, the bond reference — whatever explains the figure.</summary>
    [MaxLength(40)]
    public string Reference { get; set; } = string.Empty;

    [MaxLength(250)]
    public string Remarks { get; set; } = string.Empty;

    [MaxLength(120)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    /// <summary>Whether this is taken on a run paying out on the given date.</summary>
    public bool AppliesOn(DateTime payDate) =>
        IsActive &&
        Amount > 0m &&
        payDate.Date >= StartsOn.Date &&
        (EndsOn is null || payDate.Date <= EndsOn.Value.Date);

    [Ignore]
    public string AmountDisplay => $"{PayrollRounding.Format(Amount)} per period";

    [Ignore]
    public string PeriodDisplay => EndsOn is null
        ? $"from {StartsOn:dd MMM yyyy}"
        : $"{StartsOn:dd MMM yyyy} – {EndsOn.Value:dd MMM yyyy}";

    [Ignore]
    public string StatusDisplay => IsActive ? "Active" : "Stopped";

    [Ignore]
    public string Display => $"{DeductionName} · {AmountDisplay} · {PeriodDisplay}";

    public EmployeeDeduction Clone() => (EmployeeDeduction)MemberwiseClone();
}
