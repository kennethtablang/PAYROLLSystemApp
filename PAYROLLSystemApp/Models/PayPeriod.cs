using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-040. One pay period: the days it covers, the attendance cut-off that
/// feeds it, and the date the money lands.
///
/// <para><b>Three dates, not one.</b> They are routinely different in Philippine
/// practice — a 1–15 pay period is commonly cut off on the 10th so payroll can
/// be processed, and paid on the 15th. Collapsing them would make it impossible
/// to say which time records a run was allowed to see.</para>
///
/// <para>The period is the unit everything downstream keys on: a payroll run
/// belongs to exactly one (FR-050), a duplicate run for the same employee and
/// period is refused (FR-060), and the year-end tax settlement is identified by
/// the <em>period</em> a cut-off belongs to rather than by its pay date — a
/// December cut-off paid in January is still December's payroll.</para>
/// </summary>
[Table("pay_periods")]
public class PayPeriod
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>
    /// Human key, e.g. "2026-09-2" for the second September period. Unique, so
    /// a generated calendar cannot silently produce the same period twice.
    /// </summary>
    [Indexed(Name = "ux_pay_periods_code", Order = 1, Unique = true), MaxLength(20), NotNull]
    public string Code { get; set; } = string.Empty;

    [MaxLength(60), NotNull]
    public string Name { get; set; } = string.Empty;

    public PayFrequency Frequency { get; set; } = PayFrequency.SemiMonthly;

    /// <summary>First day the period covers.</summary>
    [Indexed]
    public DateTime PeriodStart { get; set; }

    /// <summary>Last day the period covers.</summary>
    public DateTime PeriodEnd { get; set; }

    /// <summary>First day of attendance the run may read.</summary>
    public DateTime CutOffStart { get; set; }

    /// <summary>
    /// Last day of attendance the run may read. Usually before
    /// <see cref="PeriodEnd"/>, which is the point of having it.
    /// </summary>
    public DateTime CutOffEnd { get; set; }

    /// <summary>
    /// The date the employee is paid. C-02 reads the statutory tables in force
    /// on this date, so it is what decides which SSS schedule and which tax
    /// brackets a run uses.
    /// </summary>
    [Indexed]
    public DateTime PayDate { get; set; }

    /// <summary>
    /// Which run of the month this is: 1 for the first, 2 for the second. On a
    /// monthly cycle it is always 1. Read by <see cref="ContributionSchedule"/>
    /// to decide where the month's contributions are taken.
    /// </summary>
    public int SequenceInMonth { get; set; } = 1;

    /// <summary>How many runs of this frequency fall in the same month.</summary>
    public int RunsInMonth { get; set; } = 1;

    public PayPeriodStatus Status { get; set; } = PayPeriodStatus.Open;

    [MaxLength(200)]
    public string Remarks { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    /// <summary>The calendar month the period belongs to, taken from its end date.</summary>
    [Ignore]
    public int Year => PeriodEnd.Year;

    [Ignore]
    public int Month => PeriodEnd.Month;

    /// <summary>True when this is the run that carries the month's contributions.</summary>
    [Ignore]
    public bool IsLastRunOfMonth => SequenceInMonth >= RunsInMonth;

    [Ignore]
    public bool IsOpen => Status == PayPeriodStatus.Open;

    [Ignore]
    public int DaysCovered => (int)(PeriodEnd.Date - PeriodStart.Date).TotalDays + 1;

    [Ignore]
    public string RangeDisplay => Format(PeriodStart, PeriodEnd);

    [Ignore]
    public string CutOffDisplay => Format(CutOffStart, CutOffEnd);

    [Ignore]
    public string PayDateDisplay => PayDate.ToString("ddd, dd MMM yyyy");

    [Ignore]
    public string StatusDisplay => PayrollEnumNames.Display(Status);

    [Ignore]
    public string FrequencyDisplay => EmployeeEnumNames.Display(Frequency);

    [Ignore]
    public string Display => $"{Code} · {RangeDisplay}";

    /// <summary>True when a date falls inside the attendance cut-off.</summary>
    public bool CoversAttendance(DateTime date) =>
        date.Date >= CutOffStart.Date && date.Date <= CutOffEnd.Date;

    /// <summary>True when two periods overlap on the days they cover.</summary>
    public bool OverlapsPeriod(PayPeriod other) =>
        PeriodStart.Date <= other.PeriodEnd.Date && other.PeriodStart.Date <= PeriodEnd.Date;

    private static string Format(DateTime from, DateTime to) =>
        from.Year == to.Year && from.Month == to.Month
            ? $"{from:dd}–{to:dd MMM yyyy}"
            : $"{from:dd MMM} – {to:dd MMM yyyy}";
}

/// <summary>
/// Builds a year of pay periods from the company's frequency and cut-off rules.
///
/// <para>Generating them is the ordinary way a payroll calendar is set up — a
/// year of semi-monthly periods is twenty-four rows nobody should type — but
/// every generated row stays editable afterwards, because holidays and bank
/// cut-offs move individual pay dates in ways no rule predicts.</para>
/// </summary>
public static class PayPeriodGenerator
{
    /// <summary>
    /// The periods a year of the configured frequency produces, with cut-off
    /// and pay dates offset by the settings' lead and lag.
    /// </summary>
    public static IReadOnlyList<PayPeriod> ForYear(int year, PayrollSettings settings)
    {
        var periods = new List<PayPeriod>();

        foreach (var range in Ranges(year, settings.PayFrequency))
        {
            var cutOffEnd = range.End.AddDays(-settings.CutOffLeadDays);

            // The cut-off runs from the day after the previous one closed, so
            // no worked day falls between two periods and goes unpaid.
            var previousCutOffEnd = periods.Count > 0
                ? periods[^1].CutOffEnd
                : range.Start.AddDays(-settings.CutOffLeadDays - 1);

            periods.Add(new PayPeriod
            {
                Code = Code(year, range.Start, range.End, range.Sequence, settings.PayFrequency),
                Name = Name(range.Start, range.End, settings.PayFrequency),
                Frequency = settings.PayFrequency,
                PeriodStart = range.Start,
                PeriodEnd = range.End,
                CutOffStart = previousCutOffEnd.AddDays(1),
                CutOffEnd = cutOffEnd,
                PayDate = range.End.AddDays(settings.PayDateLagDays),
                SequenceInMonth = range.Sequence,
                RunsInMonth = range.RunsInMonth,
                Status = PayPeriodStatus.Open,
                Remarks = "Generated from the payroll calendar settings."
            });
        }

        return periods;
    }

    private static IEnumerable<(DateTime Start, DateTime End, int Sequence, int RunsInMonth)> Ranges(
        int year, PayFrequency frequency)
    {
        switch (frequency)
        {
            case PayFrequency.SemiMonthly:
                for (var month = 1; month <= 12; month++)
                {
                    var last = DateTime.DaysInMonth(year, month);

                    yield return (new DateTime(year, month, 1), new DateTime(year, month, 15), 1, 2);
                    yield return (new DateTime(year, month, 16), new DateTime(year, month, last), 2, 2);
                }
                break;

            case PayFrequency.Weekly:
            case PayFrequency.BiWeekly:
                {
                    // Weeks run Monday to Sunday, and the calendar starts on the
                    // Monday of the week holding 1 January so no day of the year
                    // falls outside a period.
                    var length = frequency == PayFrequency.Weekly ? 7 : 14;
                    var runsInMonth = PayrollEnumNames.RunsPerMonth(frequency);

                    var cursor = new DateTime(year, 1, 1);
                    cursor = cursor.AddDays(-(((int)cursor.DayOfWeek + 6) % 7));

                    var counters = new Dictionary<(int, int), int>();

                    while (cursor.Year <= year)
                    {
                        var end = cursor.AddDays(length - 1);

                        if (end.Year >= year)
                        {
                            // The sequence within the month decides which run
                            // carries the month's contributions, so it is counted
                            // against the month the period *ends* in.
                            var key = (end.Year, end.Month);
                            counters[key] = counters.TryGetValue(key, out var n) ? n + 1 : 1;

                            yield return (cursor, end, counters[key],
                                frequency == PayFrequency.Weekly
                                    ? WeeksIn(end.Year, end.Month)
                                    : runsInMonth);
                        }

                        cursor = cursor.AddDays(length);
                    }
                }
                break;

            default:
                // Monthly, and daily — a daily cycle is not a calendar anyone
                // generates a year of, so it starts as one period per month
                // rather than 365 rows, and is split by hand where it is used.
                for (var month = 1; month <= 12; month++)
                {
                    yield return (
                        new DateTime(year, month, 1),
                        new DateTime(year, month, DateTime.DaysInMonth(year, month)),
                        1, 1);
                }
                break;
        }
    }

    /// <summary>Whole Monday-to-Sunday weeks whose end date falls in a month.</summary>
    private static int WeeksIn(int year, int month)
    {
        var count = 0;
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month));

        for (var day = new DateTime(year, month, 1); day <= last; day = day.AddDays(1))
        {
            if (day.DayOfWeek == DayOfWeek.Sunday)
                count++;
        }

        return Math.Max(1, count);
    }

    private static string Code(int year, DateTime start, DateTime end, int sequence, PayFrequency frequency) =>
        frequency switch
        {
            PayFrequency.Monthly => $"{year}-{end.Month:00}",
            PayFrequency.SemiMonthly => $"{year}-{end.Month:00}-{sequence}",
            PayFrequency.Weekly => $"{year}-W{System.Globalization.ISOWeek.GetWeekOfYear(start):00}",
            PayFrequency.BiWeekly => $"{year}-F{System.Globalization.ISOWeek.GetWeekOfYear(start):00}",
            _ => $"{year}-{end.Month:00}-D"
        };

    private static string Name(DateTime start, DateTime end, PayFrequency frequency) =>
        frequency switch
        {
            PayFrequency.Monthly => $"{end:MMMM yyyy}",
            PayFrequency.SemiMonthly => $"{end:MMMM yyyy} · {start:dd}–{end:dd}",
            PayFrequency.Weekly => $"Week of {start:dd MMM yyyy}",
            PayFrequency.BiWeekly => $"Fortnight of {start:dd MMM yyyy}",
            _ => $"{end:MMMM yyyy} · daily"
        };
}
