using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-025. One entry in the company holiday calendar: a date, what it is called,
/// and the premium class it carries.
///
/// <para><b>Why a calendar rather than a rule engine.</b> Philippine holidays are
/// proclaimed annually. Only some are fixed to a date (Christmas Day, Rizal Day);
/// the rest move — Eid'l Fitr follows the lunar calendar, and the "holiday
/// economics" proclamations shift others to the nearest Monday. So the fixed ones
/// are stored once with <see cref="IsAnnual"/> set and matched by month and day
/// every year, and the movable ones are entered per year as they are proclaimed.</para>
///
/// <para>Holidays are retired, never deleted, for the same reason a department is:
/// an attendance record already computed against one must keep meaning what it
/// meant when it was computed.</para>
/// </summary>
[Table("holidays")]
public class Holiday
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>
    /// The date it falls on. For an annual holiday only the month and day are
    /// read; the year is the one it was first entered under.
    /// </summary>
    [Indexed]
    public DateTime Date { get; set; } = DateTime.Today;

    [MaxLength(80), NotNull]
    public string Name { get; set; } = string.Empty;

    public HolidayType Type { get; set; } = HolidayType.Regular;

    /// <summary>
    /// True for a holiday fixed to the same calendar date every year, so it
    /// need not be re-entered. False for one that must be proclaimed annually.
    /// </summary>
    public bool IsAnnual { get; set; }

    /// <summary>
    /// Something a payroll officer looking at a 2.6× premium next February will
    /// want: which proclamation put this day on the calendar.
    /// </summary>
    [MaxLength(200)]
    public string Remarks { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    [Ignore]
    public string TypeDisplay => AttendanceEnumNames.Display(Type);

    [Ignore]
    public string DateDisplay => IsAnnual
        ? Date.ToString("dd MMMM") + " (every year)"
        : Date.ToString("dd MMM yyyy (ddd)");

    [Ignore]
    public string Display => $"{DateDisplay} — {Name}";

    /// <summary>
    /// Whether this entry governs <paramref name="date"/>. An annual holiday
    /// matches on month and day alone; a proclaimed one on the exact date.
    /// </summary>
    public bool Covers(DateTime date) =>
        IsAnnual
            ? Date.Month == date.Month && Date.Day == date.Day
            : Date.Date == date.Date;

    /// <summary>The date this entry falls on within a given year.</summary>
    public DateTime OccurrenceIn(int year)
    {
        if (!IsAnnual)
            return Date.Date;

        // 29 February in a common year: the proclamation would name 28 February,
        // so clamp rather than throw.
        var day = Math.Min(Date.Day, DateTime.DaysInMonth(year, Date.Month));
        return new DateTime(year, Date.Month, day);
    }
}
