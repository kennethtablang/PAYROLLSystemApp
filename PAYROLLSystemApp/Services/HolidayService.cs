using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// The holiday calendar resolved over a date range, so a cut-off of attendance
/// can be classified from one read instead of one query per day.
///
/// <para>Where two entries land on the same date — an annual fixed holiday and a
/// separately proclaimed one — the <b>higher premium wins</b>. A regular holiday
/// pays 2.00× against a special day's 1.30×, and paying an employee the lesser of
/// two entitlements because of the order rows came back in would be a quiet
/// underpayment.</para>
/// </summary>
public sealed class HolidayCalendar
{
    private readonly Dictionary<DateTime, Holiday> _byDate = new();

    public HolidayCalendar(IEnumerable<Holiday> holidays, DateTime from, DateTime to)
    {
        var active = holidays.Where(h => h.IsActive).ToList();

        for (var date = from.Date; date <= to.Date; date = date.AddDays(1))
        {
            var match = active
                .Where(h => h.Covers(date))
                .OrderBy(h => Rank(h.Type))
                .FirstOrDefault();

            if (match is not null)
                _byDate[date] = match;
        }
    }

    /// <summary>An empty calendar, for a range with nothing proclaimed in it.</summary>
    public static HolidayCalendar Empty { get; } =
        new(Array.Empty<Holiday>(), DateTime.Today, DateTime.Today);

    public Holiday? On(DateTime date) =>
        _byDate.TryGetValue(date.Date, out var holiday) ? holiday : null;

    public HolidayType TypeOn(DateTime date) => On(date)?.Type ?? HolidayType.None;

    public string NameOn(DateTime date) => On(date)?.Name ?? string.Empty;

    /// <summary>Lower ranks first: the day that pays more.</summary>
    private static int Rank(HolidayType type) => type switch
    {
        HolidayType.Regular => 0,
        HolidayType.SpecialNonWorking => 1,
        HolidayType.SpecialWorking => 2,
        _ => 3
    };
}

public interface IHolidayService
{
    Task<IReadOnlyList<Holiday>> GetAllAsync(bool includeInactive = false);

    /// <summary>
    /// Everything that falls in a calendar year, with annual entries projected
    /// onto their occurrence in that year and the list in date order.
    /// </summary>
    Task<IReadOnlyList<Holiday>> GetForYearAsync(int year, bool includeInactive = false);

    Task<SaveResult<Holiday>> SaveAsync(Holiday holiday, User performedBy);

    Task<SaveResult<Holiday>> SetActiveAsync(int id, bool active, User performedBy);

    /// <summary>The calendar resolved across a range, for classifying attendance.</summary>
    Task<HolidayCalendar> GetCalendarAsync(DateTime from, DateTime to);
}

/// <summary>
/// FR-025. The company holiday calendar: which dates carry a premium, and which
/// premium they carry.
///
/// <para><b>Data, not code.</b> Philippine holidays are proclaimed annually, and
/// only some of them are fixed to a date. Hard-coding them would mean a new
/// release every time Malacañang moves one (NFR-031), so the fixed ones are
/// seeded as annual entries and the movable ones are entered per year as they
/// are proclaimed.</para>
///
/// <para>Nothing is deleted here either. An attendance day already computed
/// against a holiday must keep meaning what it meant, so an entry proclaimed in
/// error is retired and the days computed under it are recomputed deliberately.</para>
/// </summary>
public sealed class HolidayService : IHolidayService
{
    private readonly PayrollDatabase _database;
    private readonly IAuditService _audit;

    public HolidayService(PayrollDatabase database, IAuditService audit)
    {
        _database = database;
        _audit = audit;
    }

    public async Task<IReadOnlyList<Holiday>> GetAllAsync(bool includeInactive = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var query = connection.Table<Holiday>();
        if (!includeInactive)
            query = query.Where(h => h.IsActive);

        var rows = await query.ToListAsync().ConfigureAwait(false);

        return rows
            .OrderBy(h => h.Date.Month)
            .ThenBy(h => h.Date.Day)
            .ToList();
    }

    public async Task<IReadOnlyList<Holiday>> GetForYearAsync(int year, bool includeInactive = false)
    {
        var all = await GetAllAsync(includeInactive).ConfigureAwait(false);

        return all
            .Where(h => h.IsAnnual || h.Date.Year == year)
            .OrderBy(h => h.OccurrenceIn(year))
            .ToList();
    }

    public async Task<HolidayCalendar> GetCalendarAsync(DateTime from, DateTime to)
    {
        var all = await GetAllAsync(includeInactive: false).ConfigureAwait(false);
        return new HolidayCalendar(all, from, to);
    }

    public async Task<SaveResult<Holiday>> SaveAsync(Holiday holiday, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageAttendance))
            return await RefuseAsync(performedBy, "maintain the holiday calendar").ConfigureAwait(false);

        holiday.Name = (holiday.Name ?? string.Empty).Trim();
        holiday.Remarks = (holiday.Remarks ?? string.Empty).Trim();
        holiday.Date = holiday.Date.Date;

        if (holiday.Name.Length == 0)
            return SaveResult<Holiday>.Fail("Enter the name of the holiday.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var existing = await connection.Table<Holiday>().ToListAsync().ConfigureAwait(false);

        // Two entries on the same day is how an employee ends up paid the wrong
        // premium, so it is refused here rather than resolved silently by the
        // calendar's ranking.
        var clash = existing.FirstOrDefault(h =>
            h.Id != holiday.Id &&
            h.IsActive &&
            SameOccurrence(h, holiday));

        if (clash is not null)
        {
            return SaveResult<Holiday>.Fail(
                $"{clash.Name} is already on the calendar for that date. Edit or retire it instead.");
        }

        var isNew = holiday.Id == 0;

        if (isNew)
        {
            await connection.InsertAsync(holiday).ConfigureAwait(false);
        }
        else
        {
            var current = existing.FirstOrDefault(h => h.Id == holiday.Id);

            if (current is null)
                return SaveResult<Holiday>.Fail("That holiday is no longer on the calendar.");

            // Retiring is its own guarded operation; an edit carries the current
            // flag forward so renaming a retired entry does not revive it.
            holiday.IsActive = current.IsActive;
            holiday.CreatedUtc = current.CreatedUtc;

            await connection.UpdateAsync(holiday).ConfigureAwait(false);
        }

        // The type is named in the audit detail because it, not the date, is what
        // changes the pay: moving an entry from special to regular takes every
        // worked hour on it from 1.30× to 2.00×.
        await _audit.WriteAsync(
            isNew ? AuditActions.HolidayCreated : AuditActions.HolidayUpdated,
            nameof(Holiday), holiday.Id, true,
            $"{(isNew ? "Added" : "Updated")} {holiday.DateDisplay} — {holiday.Name} " +
            $"({holiday.TypeDisplay}).",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<Holiday>.Ok(holiday,
            isNew ? $"{holiday.Name} added to the calendar." : $"{holiday.Name} updated.");
    }

    public async Task<SaveResult<Holiday>> SetActiveAsync(int id, bool active, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageAttendance))
            return await RefuseAsync(performedBy, "retire or restore a holiday").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var holiday = await connection.Table<Holiday>()
            .Where(h => h.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (holiday is null)
            return SaveResult<Holiday>.Fail("That holiday is no longer on the calendar.");

        holiday.IsActive = active;
        await connection.UpdateAsync(holiday).ConfigureAwait(false);

        await _audit.WriteAsync(
            active ? AuditActions.HolidayReactivated : AuditActions.HolidayDeactivated,
            nameof(Holiday), holiday.Id, true,
            $"{(active ? "Restored" : "Retired")} {holiday.DateDisplay} — {holiday.Name}. " +
            "Attendance already computed against it keeps the classification it was given.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<Holiday>.Ok(holiday,
            active
                ? $"{holiday.Name} restored."
                : $"{holiday.Name} retired. Days already computed against it are unchanged — " +
                  "regenerate the cut-off if they should follow the new calendar.");
    }

    /// <summary>
    /// Whether two entries would land on the same day. An annual entry clashes
    /// with anything sharing its month and day, in any year, because it recurs
    /// into that year too.
    /// </summary>
    private static bool SameOccurrence(Holiday a, Holiday b)
    {
        if (a.IsAnnual || b.IsAnnual)
            return a.Date.Month == b.Date.Month && a.Date.Day == b.Date.Day;

        return a.Date.Date == b.Date.Date;
    }

    private async Task<SaveResult<Holiday>> RefuseAsync(User performedBy, string action)
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, nameof(Holiday), null, false,
            $"Role {performedBy.RoleDisplayName} is not permitted to {action}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<Holiday>.Fail("You do not have permission to perform this action.");
    }
}
