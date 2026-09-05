using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-020 – FR-024. One employee, one calendar day.
///
/// <para><b>Punches in, everything else derived.</b> The four time fields and the
/// day's classification are the only inputs; hours, lateness, undertime, night
/// differential and overtime are computed by
/// <see cref="Services.AttendanceCalculator"/> and written back here. They are
/// stored rather than recomputed on read because a payroll run must be able to
/// reproduce a payslip years later (NFR-009), by which time the work schedule
/// may have been edited.</para>
///
/// <para><b>Times are minutes past midnight</b>, 0–1439, matching
/// <see cref="WorkSchedule"/>. A shift that runs into the next day is recognised
/// by a time-out at or before the time-in rather than by a stored flag, so the
/// two can never disagree.</para>
///
/// <para><b>Day classification is two fields.</b> <see cref="IsRestDay"/> and
/// <see cref="HolidayType"/> are independent: a regular holiday falling on a rest
/// day pays 2.6× where either alone pays less. One combined enum would need every
/// pairing spelled out as its own member.</para>
/// </summary>
[Table("attendance_records")]
public class AttendanceRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ix_attendance_employee_date", Order = 1)]
    public int EmployeeId { get; set; }

    /// <summary>The calendar day, at midnight. One row per employee per date.</summary>
    [Indexed(Name = "ix_attendance_employee_date", Order = 2)]
    public DateTime Date { get; set; } = DateTime.Today;

    // ------------------------------------------------------------ FR-020

    /// <summary>Time in, minutes past midnight. Null when the employee did not report.</summary>
    public int? TimeInMinutes { get; set; }

    /// <summary>Time out, minutes past midnight. Null while the day is still open.</summary>
    public int? TimeOutMinutes { get; set; }

    /// <summary>Start of the unpaid break, if it was punched rather than assumed.</summary>
    public int? BreakOutMinutes { get; set; }

    /// <summary>End of the unpaid break, if it was punched.</summary>
    public int? BreakInMinutes { get; set; }

    // -------------------------------------------------- day classification

    /// <summary>
    /// True when the assigned schedule does not work this weekday, or when an
    /// officer has declared it a rest day for this employee specifically.
    /// </summary>
    public bool IsRestDay { get; set; }

    public HolidayType HolidayType { get; set; } = HolidayType.None;

    /// <summary>
    /// The holiday's name as it stood when the day was computed. Snapshotted so
    /// a retired or renamed calendar entry does not rewrite history.
    /// </summary>
    [MaxLength(80)]
    public string HolidayName { get; set; } = string.Empty;

    // ------------------------------------------------ computed, FR-021–023

    /// <summary>
    /// Paid hours worked inside the scheduled shift, net of the break. Capped at
    /// the scheduled hours: time before the shift starts is not overtime and is
    /// not regular pay either.
    /// </summary>
    public decimal RegularHours { get; set; }

    /// <summary>FR-021, FR-026. Minutes late, after the schedule's grace period.</summary>
    public int LateMinutes { get; set; }

    /// <summary>FR-021. Minutes of the shift left unworked at the end of the day.</summary>
    public int UndertimeMinutes { get; set; }

    /// <summary>FR-023. Hours worked inside the 22:00–06:00 window (Art. 86).</summary>
    public decimal NightDifferentialHours { get; set; }

    /// <summary>
    /// FR-022. Hours worked beyond the scheduled shift, as the punches show them.
    /// This is the claim, not the entitlement.
    /// </summary>
    public decimal OvertimeHoursRendered { get; set; }

    /// <summary>
    /// FR-022. The hours actually authorised, and the only figure payroll reads.
    /// Overtime that nobody approved is recorded and unpaid, which is the point
    /// of holding the two separately.
    /// </summary>
    public decimal OvertimeHoursApproved { get; set; }

    /// <summary>
    /// The scheduled paid hours this day was measured against, snapshotted from
    /// the work schedule at the time of computation (NFR-009).
    /// </summary>
    public decimal ScheduledHours { get; set; }

    /// <summary>Which schedule was used. Kept for diagnosis, not for recomputation.</summary>
    public int? WorkScheduleId { get; set; }

    // ------------------------------------------------------------- record

    public AttendanceStatus Status { get; set; } = AttendanceStatus.Present;

    public AttendanceSource Source { get; set; } = AttendanceSource.Manual;

    /// <summary>
    /// Set when the payroll run covering this day is posted. A locked day cannot
    /// be edited, so a payslip can never drift away from the record it was
    /// computed from (NFR-037).
    /// </summary>
    public bool IsLocked { get; set; }

    // ------------------------------------------------------------ FR-024

    /// <summary>Why the entry was corrected. Required for a manual override.</summary>
    [MaxLength(250)]
    public string Remarks { get; set; } = string.Empty;

    /// <summary>Username of whoever last corrected the day by hand.</summary>
    [MaxLength(120)]
    public string OverriddenBy { get; set; } = string.Empty;

    public DateTime? OverriddenUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    /// <summary>True when the punches show a shift running past midnight.</summary>
    [Ignore]
    public bool CrossesMidnight =>
        TimeInMinutes is { } inAt && TimeOutMinutes is { } outAt && outAt <= inAt;

    [Ignore]
    public bool HasPunches => TimeInMinutes.HasValue || TimeOutMinutes.HasValue;

    [Ignore]
    public bool IsHoliday => HolidayType != HolidayType.None;

    [Ignore]
    public bool WasWorked => RegularHours > 0 || OvertimeHoursRendered > 0;

    /// <summary>Overtime rendered but not yet authorised — what an approver acts on.</summary>
    [Ignore]
    public decimal OvertimePending => Math.Max(0m, OvertimeHoursRendered - OvertimeHoursApproved);

    [Ignore]
    public bool HasPendingOvertime => OvertimePending > 0m;

    [Ignore]
    public bool WasOverridden => OverriddenUtc.HasValue;

    [Ignore]
    public string DateDisplay => Date.ToString("ddd, dd MMM yyyy");

    [Ignore]
    public string DayDisplay => Date.ToString("ddd dd");

    [Ignore]
    public string TimeInDisplay =>
        TimeInMinutes is { } value ? WorkSchedule.FormatTime(value) : "—";

    [Ignore]
    public string TimeOutDisplay => TimeOutMinutes is null
        ? "—"
        : WorkSchedule.FormatTime(TimeOutMinutes.Value) + (CrossesMidnight ? " +1" : string.Empty);

    [Ignore]
    public string StatusDisplay => AttendanceEnumNames.Display(Status);

    /// <summary>
    /// What the day is worth before any hours are counted: the rest day and
    /// holiday flags read together, which is how the premium is selected.
    /// </summary>
    [Ignore]
    public string DayTypeDisplay
    {
        get
        {
            var holiday = AttendanceEnumNames.Short(HolidayType);

            if (IsRestDay && holiday.Length > 0)
                return $"{holiday} on rest day";

            if (IsRestDay)
                return "Rest day";

            return holiday.Length > 0 ? holiday : "Ordinary day";
        }
    }

    [Ignore]
    public string HoursDisplay => RegularHours == 0m ? "—" : $"{RegularHours:0.##}";

    [Ignore]
    public string LateDisplay => LateMinutes == 0 ? "—" : $"{LateMinutes}m";

    [Ignore]
    public string UndertimeDisplay => UndertimeMinutes == 0 ? "—" : $"{UndertimeMinutes}m";

    [Ignore]
    public string NightDifferentialDisplay =>
        NightDifferentialHours == 0m ? "—" : $"{NightDifferentialHours:0.##}";

    /// <summary>
    /// Approved over rendered, so the grid shows at a glance that four hours
    /// were worked and only two were authorised.
    /// </summary>
    [Ignore]
    public string OvertimeDisplay
    {
        get
        {
            if (OvertimeHoursRendered == 0m && OvertimeHoursApproved == 0m)
                return "—";

            return OvertimeHoursApproved == OvertimeHoursRendered
                ? $"{OvertimeHoursApproved:0.##}"
                : $"{OvertimeHoursApproved:0.##} / {OvertimeHoursRendered:0.##}";
        }
    }

    /// <summary>Point-in-time copy, so a form can be cancelled without touching the row.</summary>
    public AttendanceRecord Clone() => (AttendanceRecord)MemberwiseClone();
}
