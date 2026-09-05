using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>What one day's punches came to, before anything is written down.</summary>
public sealed record AttendanceComputation(
    decimal RegularHours,
    int LateMinutes,
    int UndertimeMinutes,
    decimal NightDifferentialHours,
    decimal OvertimeHoursRendered,
    decimal ScheduledHours,
    AttendanceStatus Status);

/// <summary>
/// FR-021 – FR-023, FR-026. Turns a day's punches and its classification into
/// rendered hours, lateness, undertime, night differential and overtime.
///
/// <para><b>Pure by design.</b> Nothing here reads the database or the clock, so
/// the same inputs always produce the same figures (NFR-009) and every bracket
/// and boundary can be exercised by a unit test without a database (NFR-032).
/// The service above it loads the schedule and the holiday calendar and calls
/// <see cref="Apply"/>.</para>
///
/// <para><b>Minutes throughout, decimal at the edge.</b> All arithmetic is in
/// whole minutes — integers cannot drift — and is converted to hours only when
/// the result is handed back, rounded to two places half-away-from-zero, the
/// same rule money follows (NFR-035).</para>
/// </summary>
public static class AttendanceCalculator
{
    public const int MinutesPerDay = 24 * 60;

    /// <summary>Art. 86: the night differential window opens at 22:00.</summary>
    public const int NightStartMinutes = 22 * 60;

    /// <summary>...and closes at 06:00 the following morning.</summary>
    public const int NightEndMinutes = 6 * 60;

    /// <summary>
    /// The paid day assumed when an employee has no work schedule assigned, and
    /// the point past which work on a rest day or holiday becomes overtime.
    /// </summary>
    public const decimal DefaultDailyHours = 8m;

    /// <summary>
    /// Computes the day and writes the result back onto the record.
    ///
    /// <para>Approved overtime is deliberately <b>not</b> touched. It is an
    /// authorisation, not a measurement (FR-022); recomputing a day must never
    /// grant hours nobody signed for. It is only clamped down when the recomputed
    /// claim turns out to be smaller than what was previously approved — approving
    /// six hours and then correcting the punches to show four cannot leave six
    /// standing.</para>
    /// </summary>
    public static void Apply(AttendanceRecord record, WorkSchedule? schedule)
    {
        var computed = Compute(record, schedule);

        record.RegularHours = computed.RegularHours;
        record.LateMinutes = computed.LateMinutes;
        record.UndertimeMinutes = computed.UndertimeMinutes;
        record.NightDifferentialHours = computed.NightDifferentialHours;
        record.OvertimeHoursRendered = computed.OvertimeHoursRendered;
        record.ScheduledHours = computed.ScheduledHours;
        record.Status = computed.Status;
        record.WorkScheduleId = schedule?.Id;

        if (record.OvertimeHoursApproved > record.OvertimeHoursRendered)
            record.OvertimeHoursApproved = record.OvertimeHoursRendered;
    }

    /// <summary>
    /// The computation itself. See <see cref="Apply"/> for the write-back.
    /// </summary>
    public static AttendanceComputation Compute(AttendanceRecord record, WorkSchedule? schedule)
    {
        var scheduledHours = schedule?.StandardHours ?? DefaultDailyHours;
        if (scheduledHours <= 0m)
            scheduledHours = DefaultDailyHours;

        var breakMinutes = Math.Max(0, schedule?.BreakMinutes ?? 0);

        // A rest day has no shift to be measured against, so lateness and
        // undertime are meaningless on it and the paid window is notional:
        // the DOLE rule is that the first eight hours carry the day's premium
        // and anything beyond them is overtime.
        var hasShift = schedule is not null && !record.IsRestDay;

        var worked = WorkedInterval(record);

        // Nothing was punched. The day is classified from what it was, not from
        // what was worked, and there is nothing to compute.
        if (worked is null)
        {
            return new AttendanceComputation(
                RegularHours: 0m,
                LateMinutes: 0,
                UndertimeMinutes: 0,
                NightDifferentialHours: 0m,
                OvertimeHoursRendered: 0m,
                ScheduledHours: scheduledHours,
                Status: UnworkedStatus(record));
        }

        var (workStart, workEnd) = worked.Value;

        var shift = hasShift
            ? ScheduledInterval(schedule!)
            : (Start: workStart, End: workStart + (int)(scheduledHours * 60) + breakMinutes);

        var rest = BreakInterval(record, shift, breakMinutes);

        // ---------------------------------------------------------- FR-021

        var insideShift = Overlap(workStart, workEnd, shift.Start, shift.End);
        var breakInsideShift = rest is null
            ? 0
            : Overlap(Math.Max(rest.Value.Start, workStart), Math.Min(rest.Value.End, workEnd), shift.Start, shift.End);

        var regularMinutes = Math.Max(0, insideShift - breakInsideShift);
        regularMinutes = Math.Min(regularMinutes, (int)Math.Round(scheduledHours * 60m, MidpointRounding.AwayFromZero));

        // ------------------------------------------------- FR-021, FR-026

        var late = 0;
        var undertime = 0;

        if (hasShift)
        {
            var grace = Math.Max(0, schedule!.GraceMinutes);
            var arrivedLateBy = Math.Max(0, workStart - shift.Start);

            // The grace period forgives lateness entirely while it lasts, and
            // stops forgiving anything once it is exceeded: with 15 minutes of
            // grace, 8:10 is on time and 8:20 is twenty minutes late, not five.
            // That is the common local policy. A company that prefers to charge
            // only the excess would change this one line — it is called out
            // because the two rules differ by real money over a year.
            late = arrivedLateBy > grace ? arrivedLateBy : 0;

            // Time not worked at the end of the shift. Measured from the later
            // of the time-out and the shift start, so someone who never arrived
            // inside the shift is an absence rather than a full day of undertime.
            undertime = Math.Max(0, shift.End - Math.Max(workEnd, shift.Start));

            var shiftMinutes = shift.End - shift.Start;
            late = Math.Min(late, shiftMinutes);
            undertime = Math.Min(undertime, shiftMinutes - late);
        }

        // ---------------------------------------------------------- FR-022

        // Only work past the end of the shift counts. Reporting early is not
        // overtime; it is unrequested time, and paying it would let anyone
        // award themselves an hour by arriving at seven.
        var overtimeMinutes = Math.Max(0, workEnd - shift.End);

        if (rest is not null)
            overtimeMinutes -= Overlap(rest.Value.Start, rest.Value.End, shift.End, workEnd);

        overtimeMinutes = Math.Max(0, overtimeMinutes);

        // Punches that fall entirely outside the shift — a 05:00–07:00 stint
        // against an 08:00–17:00 schedule — leave nothing worked. That is one
        // absence, not an absence plus a full shift of undertime, which payroll
        // would deduct twice.
        if (regularMinutes == 0 && overtimeMinutes == 0)
        {
            late = 0;
            undertime = 0;
        }

        // ---------------------------------------------------------- FR-023

        // Art. 86 covers any work between 22:00 and 06:00, overtime included, so
        // the whole worked interval is measured rather than only its scheduled
        // part. The window is checked against the previous, current and next
        // day, which covers a shift starting after midnight and one running
        // past dawn without either being a special case.
        var nightMinutes = 0;

        for (var offset = -1; offset <= 1; offset++)
        {
            var windowStart = offset * MinutesPerDay + NightStartMinutes;
            var windowEnd = offset * MinutesPerDay + MinutesPerDay + NightEndMinutes;

            nightMinutes += Overlap(workStart, workEnd, windowStart, windowEnd);

            if (rest is not null)
                nightMinutes -= Overlap(rest.Value.Start, rest.Value.End, windowStart, windowEnd);
        }

        nightMinutes = Math.Max(0, nightMinutes);

        return new AttendanceComputation(
            RegularHours: ToHours(regularMinutes),
            LateMinutes: late,
            UndertimeMinutes: undertime,
            NightDifferentialHours: ToHours(nightMinutes),
            OvertimeHoursRendered: ToHours(overtimeMinutes),
            ScheduledHours: scheduledHours,
            Status: WorkedStatus(record, ToHours(regularMinutes), ToHours(overtimeMinutes), scheduledHours, hasShift));
    }

    /// <summary>
    /// Whether the schedule treats this weekday as a rest day. The record's own
    /// flag is what the calculator reads; this is how the service derives it
    /// before an officer has a chance to override it.
    /// </summary>
    public static bool IsRestDayFor(WorkSchedule? schedule, DateTime date) =>
        schedule is null ? date.DayOfWeek == DayOfWeek.Sunday : !schedule.IsWorkDay(date.DayOfWeek);

    // ------------------------------------------------------------- internals

    /// <summary>
    /// The worked interval in minutes from the record's own midnight, or null
    /// when the day cannot be measured.
    ///
    /// <para>Both punches are required. A day with a time-in and no time-out is
    /// still open — the employee has not left yet, or somebody forgot — and
    /// guessing an end would silently invent hours.</para>
    /// </summary>
    private static (int Start, int End)? WorkedInterval(AttendanceRecord record)
    {
        if (record.TimeInMinutes is not { } start || record.TimeOutMinutes is not { } end)
            return null;

        // A time-out at or before the time-in is the next day: a night shift.
        if (end <= start)
            end += MinutesPerDay;

        return (start, end);
    }

    private static (int Start, int End) ScheduledInterval(WorkSchedule schedule)
    {
        var end = schedule.EndMinutes;

        if (end <= schedule.StartMinutes)
            end += MinutesPerDay;

        return (schedule.StartMinutes, end);
    }

    /// <summary>
    /// When the unpaid break was taken.
    ///
    /// <para>Punched breaks are used as recorded. Where there are none — the
    /// common case, since most companies do not punch out for lunch — the
    /// schedule's break is assumed to sit in the middle of the shift. That is an
    /// assumption, and it matters only for the night differential: a night-shift
    /// break placed in the middle of the shift falls inside the 22:00–06:00
    /// window and is correctly excluded from it. Placing it anywhere else would
    /// pay a differential on an unpaid hour.</para>
    /// </summary>
    private static (int Start, int End)? BreakInterval(
        AttendanceRecord record,
        (int Start, int End) shift,
        int breakMinutes)
    {
        if (record.BreakOutMinutes is { } outAt && record.BreakInMinutes is { } inAt)
        {
            var end = inAt;
            if (end <= outAt)
                end += MinutesPerDay;

            return (outAt, end);
        }

        if (breakMinutes <= 0)
            return null;

        var midpoint = shift.Start + (shift.End - shift.Start) / 2;
        var start = midpoint - breakMinutes / 2;

        return (start, start + breakMinutes);
    }

    /// <summary>Minutes two half-open intervals share.</summary>
    private static int Overlap(int aStart, int aEnd, int bStart, int bEnd) =>
        Math.Max(0, Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart));

    private static decimal ToHours(int minutes) =>
        Math.Round(minutes / 60m, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// What an unpunched day was. Leave and work suspension are facts no punch
    /// can reveal, so a value an officer set by hand is left alone; everything
    /// else follows from the day's classification.
    /// </summary>
    private static AttendanceStatus UnworkedStatus(AttendanceRecord record)
    {
        if (record.Status is AttendanceStatus.OnLeave or AttendanceStatus.Suspended)
            return record.Status;

        if (record.IsRestDay)
            return AttendanceStatus.RestDay;

        // A special *working* day is an ordinary day that happens to be named,
        // so failing to report on one is an absence like any other.
        if (record.HolidayType is HolidayType.Regular or HolidayType.SpecialNonWorking)
            return AttendanceStatus.Holiday;

        return AttendanceStatus.Absent;
    }

    private static AttendanceStatus WorkedStatus(
        AttendanceRecord record,
        decimal regularHours,
        decimal overtimeHours,
        decimal scheduledHours,
        bool hasShift)
    {
        if (record.Status is AttendanceStatus.Suspended)
            return AttendanceStatus.Suspended;

        // Work on a rest day or an unscheduled day is simply attendance. There
        // is no shift for it to fall short of, so it is never a half day.
        if (!hasShift)
            return regularHours > 0m || overtimeHours > 0m
                ? AttendanceStatus.Present
                : UnworkedStatus(record);

        if (regularHours <= 0m)
            return overtimeHours > 0m ? AttendanceStatus.Present : AttendanceStatus.Absent;

        return regularHours < scheduledHours / 2m
            ? AttendanceStatus.HalfDay
            : AttendanceStatus.Present;
    }
}
