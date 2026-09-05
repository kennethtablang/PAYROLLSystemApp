namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-025. What a calendar date is worth under the Labor Code.
///
/// <para>This is only half of a day's classification. The DOLE premium matrix
/// is genuinely two-dimensional — a regular holiday <i>falling on a rest day</i>
/// pays 2.6× rather than 2.0× — so the attendance record carries this enum
/// alongside an <c>IsRestDay</c> flag rather than trying to enumerate every
/// combination as a single day type.</para>
/// </summary>
public enum HolidayType
{
    /// <summary>An ordinary working day. No premium.</summary>
    None = 0,

    /// <summary>
    /// Art. 94. Paid even when unworked, and worked hours pay double.
    /// Independence Day, Christmas Day, and the rest of the twelve.
    /// </summary>
    Regular = 1,

    /// <summary>
    /// "No work, no pay" unless company policy says otherwise; worked hours
    /// pay 1.30×. All Saints' Day, Ninoy Aquino Day, and so on.
    /// </summary>
    SpecialNonWorking = 2,

    /// <summary>
    /// A special <i>working</i> day carries no premium at all — it is an
    /// ordinary day that happens to be proclaimed. Held separately from
    /// <see cref="None"/> so the calendar can still name it.
    /// </summary>
    SpecialWorking = 3
}

/// <summary>
/// FR-021, FR-023. What happened on one employee's day.
///
/// <para>The value is derived by <see cref="Services.AttendanceCalculator"/>
/// from the punches and the schedule, except for
/// <see cref="OnLeave"/> and <see cref="Suspended"/>, which are facts about the
/// day that no punch can reveal and are therefore set by hand.</para>
/// </summary>
public enum AttendanceStatus
{
    Present = 0,

    /// <summary>Scheduled to work, did not, and no leave covers it.</summary>
    Absent = 1,

    /// <summary>Covered by an approved leave; payroll decides whether it is paid.</summary>
    OnLeave = 2,

    /// <summary>A holiday the employee did not work.</summary>
    Holiday = 3,

    /// <summary>Not a working day under the assigned schedule.</summary>
    RestDay = 4,

    /// <summary>Worked, but less than half the scheduled hours.</summary>
    HalfDay = 5,

    /// <summary>Work suspension — typhoon, government order. Unworked, not absent.</summary>
    Suspended = 6
}

/// <summary>
/// Where a record came from. Kept because an imported row and a row an officer
/// typed carry different weight in a dispute (FR-024, FR-091).
/// </summary>
public enum AttendanceSource
{
    /// <summary>Entered or corrected by hand on the Time &amp; Attendance screen.</summary>
    Manual = 0,

    /// <summary>Read from a spreadsheet or CSV upload (FR-028, not built).</summary>
    Import = 1,

    /// <summary>Pushed by a biometric device (FR-028, not built).</summary>
    Biometric = 2,

    /// <summary>
    /// Created by the system when a cut-off skeleton was generated: rest days,
    /// holidays and absences that nobody had to type.
    /// </summary>
    Generated = 3
}

/// <summary>
/// Display names for the attendance enumerations, in one place so the daily
/// grid, the timesheet and a later payslip cannot word the same value
/// differently (NFR-024).
/// </summary>
public static class AttendanceEnumNames
{
    public static string Display(HolidayType value) => value switch
    {
        HolidayType.None => "Ordinary day",
        HolidayType.Regular => "Regular holiday",
        HolidayType.SpecialNonWorking => "Special non-working",
        HolidayType.SpecialWorking => "Special working",
        _ => value.ToString()
    };

    /// <summary>The short form a table cell has room for.</summary>
    public static string Short(HolidayType value) => value switch
    {
        HolidayType.None => string.Empty,
        HolidayType.Regular => "Regular holiday",
        HolidayType.SpecialNonWorking => "Special",
        HolidayType.SpecialWorking => "Special working",
        _ => value.ToString()
    };

    public static string Display(AttendanceStatus value) => value switch
    {
        AttendanceStatus.OnLeave => "On leave",
        AttendanceStatus.RestDay => "Rest day",
        AttendanceStatus.HalfDay => "Half day",
        _ => value.ToString()
    };

    public static string Display(AttendanceSource value) => value switch
    {
        AttendanceSource.Manual => "Entered by hand",
        AttendanceSource.Import => "Imported",
        AttendanceSource.Biometric => "Biometric device",
        AttendanceSource.Generated => "Generated",
        _ => value.ToString()
    };
}
