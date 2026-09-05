using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-031, FR-033, FR-034. One filing: who, which type, which dates, and what
/// an approver decided.
///
/// <para><b>Days are counted, not spanned.</b> <see cref="Days"/> holds working
/// days — rest days and non-working holidays inside the range are excluded — so
/// leave from a Friday to the following Monday costs two days, not four. The
/// count is stored rather than recomputed, because the work schedule and the
/// holiday calendar can both change after the fact (NFR-009).</para>
///
/// <para><b>The paid flag is decided at approval, not at filing.</b> FR-034 lets
/// an approver grant leave that exceeds the employee's credits by explicitly
/// treating it as unpaid, so whether a particular filing was paid is a property
/// of the decision.</para>
/// </summary>
[Table("leave_requests")]
public class LeaveRequest
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ix_leave_requests_employee", Order = 1)]
    public int EmployeeId { get; set; }

    public int LeaveTypeId { get; set; }

    /// <summary>
    /// The type's name as it stood when the request was filed, so a renamed or
    /// retired type does not rewrite the history of what was granted.
    /// </summary>
    [MaxLength(60)]
    public string LeaveTypeName { get; set; } = string.Empty;

    [Indexed(Name = "ix_leave_requests_employee", Order = 2)]
    public DateTime StartDate { get; set; } = DateTime.Today;

    public DateTime EndDate { get; set; } = DateTime.Today;

    /// <summary>Working days covered. Half a day is 0.5.</summary>
    public decimal Days { get; set; }

    /// <summary>
    /// A single day taken as a half. Only meaningful when the request covers one
    /// date; the service refuses it on a range, where "which half" has no answer.
    /// </summary>
    public bool IsHalfDay { get; set; }

    [MaxLength(250)]
    public string Reason { get; set; } = string.Empty;

    public LeaveRequestStatus Status { get; set; } = LeaveRequestStatus.Pending;

    /// <summary>
    /// FR-033. Whether the days were granted as paid. Copied from the leave type
    /// at approval, then forced false where FR-034's override was used.
    /// </summary>
    public bool IsPaid { get; set; } = true;

    /// <summary>
    /// Set when an approver knowingly granted leave beyond the available credits.
    /// Kept apart from <see cref="IsPaid"/> so an unpaid *type* and an overridden
    /// approval are distinguishable a year later.
    /// </summary>
    public bool WasOverdrawn { get; set; }

    [MaxLength(120)]
    public string FiledBy { get; set; } = string.Empty;

    public DateTime FiledUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(120)]
    public string DecidedBy { get; set; } = string.Empty;

    public DateTime? DecidedUtc { get; set; }

    [MaxLength(250)]
    public string DecisionRemarks { get; set; } = string.Empty;

    // -------------------------------------------------- derived display

    [Ignore]
    public bool IsPending => Status == LeaveRequestStatus.Pending;

    [Ignore]
    public bool IsApproved => Status == LeaveRequestStatus.Approved;

    /// <summary>An approved request still to come can be withdrawn; a decided one is closed.</summary>
    [Ignore]
    public bool IsCancellable => Status is LeaveRequestStatus.Pending or LeaveRequestStatus.Approved;

    [Ignore]
    public bool IsSingleDay => StartDate.Date == EndDate.Date;

    [Ignore]
    public string StatusDisplay => LeaveEnumNames.Display(Status);

    [Ignore]
    public string DaysDisplay => IsHalfDay ? "Half day" : $"{Days:0.##} day(s)";

    [Ignore]
    public string PeriodDisplay => IsSingleDay
        ? StartDate.ToString("ddd, dd MMM yyyy")
        : $"{StartDate:ddd, dd MMM} – {EndDate:ddd, dd MMM yyyy}";

    [Ignore]
    public string PayDisplay => IsPaid ? "Paid" : "Unpaid";

    [Ignore]
    public string FiledDisplay =>
        $"Filed by {FiledBy} on {DateTime.SpecifyKind(FiledUtc, DateTimeKind.Utc).ToLocalTime():dd MMM yyyy}";

    [Ignore]
    public string DecisionDisplay => DecidedUtc is not { } decided
        ? "Awaiting a decision"
        : $"{StatusDisplay} by {DecidedBy} on " +
          $"{DateTime.SpecifyKind(decided, DateTimeKind.Utc).ToLocalTime():dd MMM yyyy}";

    /// <summary>Point-in-time copy, so a form can be cancelled without touching the row.</summary>
    public LeaveRequest Clone() => (LeaveRequest)MemberwiseClone();
}
