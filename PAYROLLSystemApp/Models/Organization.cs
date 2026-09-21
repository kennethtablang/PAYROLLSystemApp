using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-012. A cost centre an employee belongs to, and the unit the attendance
/// and payroll reports group by (FR-083).
///
/// Departments are deactivated, never deleted: a payslip printed years from now
/// still names the department the employee was in when it was computed.
/// </summary>
[Table("departments")]
public class Department
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ux_departments_code", Order = 1, Unique = true), MaxLength(20), NotNull]
    public string Code { get; set; } = string.Empty;

    [MaxLength(80), NotNull]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public string Display => $"{Code} — {Name}";
}

/// <summary>
/// FR-012. A job title, plus the one fact about it that changes a payslip.
/// </summary>
[Table("positions")]
public class Position
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ux_positions_code", Order = 1, Unique = true), MaxLength(20), NotNull]
    public string Code { get; set; } = string.Empty;

    [MaxLength(80), NotNull]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Art. 82 of the Labor Code: managerial and supervisory staff are outside
    /// the hours-of-work provisions, so they earn no overtime, night
    /// differential or premium pay. The payroll engine reads this flag; it is
    /// held here because it is a property of the job, not of the person.
    /// </summary>
    public bool IsManagerial { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public string Display => Title;
}

/// <summary>
/// FR-012, FR-021, FR-026. The shift an employee is measured against: without
/// one there is no way to say whether they were late or worked undertime.
///
/// Times are stored as minutes past midnight rather than as a
/// <see cref="TimeSpan"/>, so the column holds a plain integer that reads the
/// same on every platform and in a raw database query.
/// </summary>
[Table("work_schedules")]
public class WorkSchedule
{
    /// <summary>Bit positions match <see cref="System.DayOfWeek"/>: Sunday is 0.</summary>
    public const int MondayToFriday = 0b0111110;

    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ux_schedules_name", Order = 1, Unique = true), MaxLength(60), NotNull]
    public string Name { get; set; } = string.Empty;

    /// <summary>Shift start, minutes past midnight. 480 = 08:00.</summary>
    public int StartMinutes { get; set; } = 8 * 60;

    /// <summary>Shift end, minutes past midnight. 1020 = 17:00.</summary>
    public int EndMinutes { get; set; } = 17 * 60;

    /// <summary>Unpaid break inside the shift, deducted from rendered hours.</summary>
    public int BreakMinutes { get; set; } = 60;

    /// <summary>
    /// FR-026: minutes after the shift start that are not counted as tardiness.
    /// Zero means no grace at all, which is a policy some companies do run.
    /// </summary>
    public int GraceMinutes { get; set; }

    /// <summary>Working days as a bitmask; a day not set is a rest day.</summary>
    public int WorkDays { get; set; } = MondayToFriday;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public TimeSpan Start => TimeSpan.FromMinutes(StartMinutes);

    [Ignore]
    public TimeSpan End => TimeSpan.FromMinutes(EndMinutes);

    /// <summary>
    /// Paid hours in a full day. A shift ending before it starts has crossed
    /// midnight — a night shift — so a day is added rather than producing a
    /// negative span.
    /// </summary>
    [Ignore]
    public decimal StandardHours
    {
        get
        {
            var span = EndMinutes - StartMinutes;
            if (span <= 0)
                span += 24 * 60;

            return Math.Round((span - BreakMinutes) / 60m, 2);
        }
    }

    /// <summary>True when the shift runs past midnight into the following day.</summary>
    [Ignore]
    public bool CrossesMidnight => EndMinutes <= StartMinutes;

    public bool IsWorkDay(DayOfWeek day) => (WorkDays & (1 << (int)day)) != 0;

    [Ignore]
    public string DaysDisplay
    {
        get
        {
            var names = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
            var set = Enumerable.Range(0, 7).Where(d => (WorkDays & (1 << d)) != 0).ToList();

            if (set.Count == 0)
                return "No working days";

            if (WorkDays == MondayToFriday)
                return "Mon–Fri";

            return string.Join(", ", set.Select(d => names[d]));
        }
    }

    [Ignore]
    public string TimeDisplay =>
        $"{FormatTime(StartMinutes)} – {FormatTime(EndMinutes)}" + (CrossesMidnight ? " (+1)" : string.Empty);

    [Ignore]
    public string Display => $"{Name} · {TimeDisplay}";

    public static string FormatTime(int minutesPastMidnight)
    {
        var normalised = ((minutesPastMidnight % (24 * 60)) + 24 * 60) % (24 * 60);
        return DateTime.Today.AddMinutes(normalised).ToString("h:mm tt");
    }
}
