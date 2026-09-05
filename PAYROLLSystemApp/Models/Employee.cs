using PAYROLLSystemApp.Security;
using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// The employee masterfile (FR-010 – FR-018). Every later module — attendance,
/// leave, payroll runs, payslips — hangs off this record.
///
/// <para>Deactivation, never deletion (FR-017). A resigned employee still
/// appears on last year's payroll register and on their own BIR 2316, so the
/// record is retained and excluded from new payroll runs instead.</para>
///
/// <para>Government identifiers are stored digits only; see
/// <see cref="GovernmentId"/> for why.</para>
/// </summary>
[Table("employees")]
public class Employee
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>FR-015: unique across the company. Compared case-insensitively.</summary>
    [Indexed(Name = "ux_employees_number", Order = 1, Unique = true), MaxLength(20), NotNull]
    public string EmployeeNumber { get; set; } = string.Empty;

    // ------------------------------------------------------------ FR-011

    [MaxLength(60), NotNull]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(60), NotNull]
    public string FirstName { get; set; } = string.Empty;

    [MaxLength(60)]
    public string MiddleName { get; set; } = string.Empty;

    /// <summary>Jr., III, and so on. Kept apart so sorting by surname still works.</summary>
    [MaxLength(10)]
    public string Suffix { get; set; } = string.Empty;

    public DateTime? BirthDate { get; set; }

    public Gender Gender { get; set; } = Gender.Unspecified;

    public CivilStatus CivilStatus { get; set; } = CivilStatus.Single;

    [MaxLength(30)]
    public string ContactNumber { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(250)]
    public string Address { get; set; } = string.Empty;

    [MaxLength(80)]
    public string EmergencyContactName { get; set; } = string.Empty;

    [MaxLength(30)]
    public string EmergencyContactNumber { get; set; } = string.Empty;

    /// <summary>Relative path under the app data directory. Empty when none is set.</summary>
    [MaxLength(250)]
    public string PhotoPath { get; set; } = string.Empty;

    // ------------------------------------------------------------ FR-012

    public DateTime HireDate { get; set; } = DateTime.Today;

    /// <summary>When probation ended. Null while the employee is still on it.</summary>
    public DateTime? RegularizationDate { get; set; }

    /// <summary>
    /// Set when the employee leaves (FR-017). Its presence, not the active
    /// flag alone, is what keeps them out of a run covering a later period.
    /// </summary>
    public DateTime? SeparationDate { get; set; }

    [MaxLength(120)]
    public string SeparationReason { get; set; } = string.Empty;

    public EmploymentStatus EmploymentStatus { get; set; } = EmploymentStatus.Probationary;

    [Indexed]
    public int? DepartmentId { get; set; }

    public int? PositionId { get; set; }

    /// <summary>Another employee's <see cref="Id"/>. Null for the top of the tree.</summary>
    public int? SupervisorId { get; set; }

    public int? WorkScheduleId { get; set; }

    // ------------------------------------------------------------ FR-013

    public PayType PayType { get; set; } = PayType.Monthly;

    /// <summary>
    /// The rate itself, read according to <see cref="PayType"/>. Money is
    /// <c>decimal</c> throughout, never a floating point type (NFR-035).
    /// </summary>
    public decimal BasicRate { get; set; }

    public PayFrequency PayFrequency { get; set; } = PayFrequency.SemiMonthly;

    /// <summary>
    /// Recurring allowance paid every period. Itemised allowance types arrive
    /// with payroll configuration (FR-041); this covers the common single
    /// figure so an employee can be paid before that exists.
    /// </summary>
    public decimal MonthlyAllowance { get; set; }

    /// <summary>
    /// A minimum wage earner is exempt from income tax on basic pay, holiday
    /// pay, overtime, night differential and hazard pay (RA 9504). The engine
    /// still produces those lines, marked non-taxable, so they stay visible.
    /// </summary>
    public bool IsMinimumWageEarner { get; set; }

    // ------------------------------------------------------------ FR-014

    [MaxLength(20)]
    public string SssNumber { get; set; } = string.Empty;

    [MaxLength(20)]
    public string PhilHealthNumber { get; set; } = string.Empty;

    [MaxLength(20)]
    public string PagIbigNumber { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Tin { get; set; } = string.Empty;

    /// <summary>Set for an engagement genuinely outside a contribution's coverage.</summary>
    public bool ExemptFromSss { get; set; }

    public bool ExemptFromPhilHealth { get; set; }

    public bool ExemptFromPagIbig { get; set; }

    // ------------------------------------------- bank details (FR-082)

    [MaxLength(60)]
    public string BankName { get; set; } = string.Empty;

    [MaxLength(40)]
    public string BankAccountNumber { get; set; } = string.Empty;

    // ------------------------------------------------------------ record

    /// <summary>FR-017: false hides the employee from new runs but keeps the history.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    /// <summary>"Dela Cruz, Juan P." — the order payroll listings sort in.</summary>
    [Ignore]
    public string FullName
    {
        get
        {
            var given = FirstName;

            if (!string.IsNullOrWhiteSpace(MiddleName))
                given += " " + char.ToUpperInvariant(MiddleName.Trim()[0]) + ".";

            if (!string.IsNullOrWhiteSpace(Suffix))
                given += " " + Suffix;

            return $"{LastName}, {given}".Trim();
        }
    }

    /// <summary>"Juan Dela Cruz" — how a person is addressed rather than filed.</summary>
    [Ignore]
    public string DisplayName =>
        string.Join(" ", new[] { FirstName, LastName, Suffix }
            .Where(p => !string.IsNullOrWhiteSpace(p)));

    [Ignore]
    public string Initials
    {
        get
        {
            var first = string.IsNullOrWhiteSpace(FirstName)
                ? "?"
                : FirstName.Trim().Substring(0, 1);

            var last = string.IsNullOrWhiteSpace(LastName)
                ? string.Empty
                : LastName.Trim().Substring(0, 1);

            return (first + last).ToUpperInvariant();
        }
    }

    [Ignore]
    public string EmploymentStatusDisplay => EmployeeEnumNames.Display(EmploymentStatus);

    [Ignore]
    public string PayTypeDisplay => EmployeeEnumNames.Display(PayType);

    /// <summary>What the list column shows: active, or why not.</summary>
    [Ignore]
    public string StatusDisplay =>
        SeparationDate.HasValue ? $"Separated {SeparationDate.Value:dd MMM yyyy}"
        : !IsActive ? "Inactive"
        : "Active";

    [Ignore]
    public bool IsSeparated => SeparationDate.HasValue;

    /// <summary>NFR-020: what a list or a report shows instead of the full number.</summary>
    [Ignore]
    public string SssMasked => GovernmentId.Masked(SssNumber);

    [Ignore]
    public string TinMasked => GovernmentId.Masked(Tin);

    [Ignore]
    public string BankAccountMasked => GovernmentId.Masked(BankAccountNumber);

    [Ignore]
    public string RateDisplay => PayType switch
    {
        PayType.Monthly => $"{BasicRate:N2} / month",
        PayType.Daily => $"{BasicRate:N2} / day",
        PayType.Hourly => $"{BasicRate:N2} / hour",
        _ => BasicRate.ToString("N2")
    };

    /// <summary>
    /// Years of service to the separation date, or to today while employed.
    /// Used by the dashboard and by final-pay computations (FR-062).
    /// </summary>
    [Ignore]
    public int YearsOfService
    {
        get
        {
            var end = SeparationDate ?? DateTime.Today;
            var years = end.Year - HireDate.Year;

            if (end < HireDate.AddYears(years))
                years--;

            return Math.Max(0, years);
        }
    }

    /// <summary>
    /// FR-017: whether this employee belongs in a payroll run covering the
    /// given period. Someone hired mid-period or separated mid-period is still
    /// included — the engine prorates them; someone whose whole employment
    /// falls outside the cutoff is not.
    /// </summary>
    public bool IsPayableOver(DateTime periodStart, DateTime periodEnd)
    {
        if (!IsActive && !SeparationDate.HasValue)
            return false;

        if (HireDate.Date > periodEnd.Date)
            return false;

        return !SeparationDate.HasValue || SeparationDate.Value.Date >= periodStart.Date;
    }
}
