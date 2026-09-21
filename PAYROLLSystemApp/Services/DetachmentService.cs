using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>A posted rate, and whether a later one has overtaken it.</summary>
public sealed record PostedRate(DetachmentRate Rate)
{
    /// <summary>True where a later row for the same detachment supersedes this one.</summary>
    public bool IsSuperseded { get; init; }

    public string Display =>
        $"{Rate.RateDisplay} {Rate.EffectiveDisplay}" +
        (IsSuperseded ? " · superseded" : string.Empty);
}

public interface IDetachmentService
{
    Task<IReadOnlyList<Detachment>> GetAllAsync(bool includeInactive = false);

    Task<Detachment?> GetAsync(int id);

    Task<SaveResult<Detachment>> SaveAsync(Detachment detachment, User performedBy);

    Task<SaveResult<Detachment>> SetActiveAsync(int id, bool active, User performedBy);

    /// <summary>Every rate row for one detachment, newest effective date first.</summary>
    Task<IReadOnlyList<PostedRate>> GetRatesAsync(int detachmentId);

    /// <summary>FR-013. Posts a new daily rate for a detachment.</summary>
    Task<SaveResult<DetachmentRate>> SaveRateAsync(DetachmentRate rate, User performedBy);

    /// <summary>Withdraws a posted rate. Deactivates it; nothing here is deleted.</summary>
    Task<SaveResult<DetachmentRate>> WithdrawRateAsync(int rateId, User performedBy);

    /// <summary>The rates in force on a date, for the payroll engine.</summary>
    Task<DetachmentRateTable> GetRateTableAsync(DateTime asOf);

    /// <summary>How many active employees are deployed to a detachment.</summary>
    Task<int> CountEmployeesAsync(int detachmentId);

    /// <summary>Assigned headcount for every detachment, in one read.</summary>
    Task<IReadOnlyDictionary<int, int>> GetHeadcountsAsync();
}

/// <summary>
/// FR-012, FR-013. The posts employees are deployed to, and the daily rate paid
/// at each of them.
///
/// <para><b>This is where a guard's wage comes from.</b> The system covers the
/// whole of Luzon, and a regional wage order sets a different rate for the same
/// work in Metro Manila and in Bicol. Holding the rate against the post means a
/// wage order is one row per post it covers, rather than an edit to every
/// employee standing there.</para>
///
/// <para><b>Rates are posted, never edited.</b> A new rate is a new row with its
/// own effective date, and <see cref="GetRateTableAsync"/> resolves whichever
/// was in force on a run's pay date. Editing a figure in place would change
/// what a recomputed earlier run pays — the mistake the statutory tables were
/// built to avoid, and this table follows them.</para>
///
/// <para>Every write is checked against the caller's permission and audited
/// (FR-091). Accounting holds <see cref="Permission.ManageEmployees"/>, which is
/// what maintaining a rate table requires.</para>
/// </summary>
public sealed class DetachmentService : IDetachmentService
{
    private readonly PayrollDatabase _database;
    private readonly IAuditService _audit;

    public DetachmentService(PayrollDatabase database, IAuditService audit)
    {
        _database = database;
        _audit = audit;
    }

    // =====================================================================
    // Detachments
    // =====================================================================

    public async Task<IReadOnlyList<Detachment>> GetAllAsync(bool includeInactive = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<Detachment>().ToListAsync().ConfigureAwait(false);

        return rows
            .Where(d => includeInactive || d.IsActive)
            .OrderBy(d => d.Region)
            .ThenBy(d => d.Code, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<Detachment?> GetAsync(int id)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        return await connection.FindAsync<Detachment>(id).ConfigureAwait(false);
    }

    public async Task<SaveResult<Detachment>> SaveAsync(Detachment detachment, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
            return await RefuseAsync<Detachment>(performedBy, "maintain detachments").ConfigureAwait(false);

        detachment.Code = (detachment.Code ?? string.Empty).Trim().ToUpperInvariant();
        detachment.Name = (detachment.Name ?? string.Empty).Trim();
        detachment.ClientName = (detachment.ClientName ?? string.Empty).Trim();
        detachment.GroupCode = (detachment.GroupCode ?? string.Empty).Trim().ToUpperInvariant();
        detachment.Location = (detachment.Location ?? string.Empty).Trim();
        detachment.Description = (detachment.Description ?? string.Empty).Trim();

        if (detachment.Code.Length == 0)
            return SaveResult<Detachment>.Fail("Enter a detachment code.");

        if (detachment.Name.Length == 0)
            return SaveResult<Detachment>.Fail("Enter a detachment name.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var code = detachment.Code;
        var clash = await connection.Table<Detachment>()
            .Where(d => d.Code == code && d.Id != detachment.Id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (clash is not null)
            return SaveResult<Detachment>.Fail($"Code {detachment.Code} is already used by {clash.Name}.");

        var isNew = detachment.Id == 0;

        if (isNew)
        {
            await connection.InsertAsync(detachment).ConfigureAwait(false);
        }
        else
        {
            var existing = await connection.FindAsync<Detachment>(detachment.Id).ConfigureAwait(false);

            if (existing is null)
                return SaveResult<Detachment>.Fail("That detachment no longer exists.");

            // Retiring is its own guarded operation, so an edit carries the
            // current flag forward rather than quietly reinstating a closed post.
            detachment.IsActive = existing.IsActive;
            detachment.CreatedUtc = existing.CreatedUtc;

            await connection.UpdateAsync(detachment).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            isNew ? AuditActions.DetachmentCreated : AuditActions.DetachmentUpdated,
            nameof(Detachment), detachment.Id, true,
            $"{(isNew ? "Created" : "Updated")} detachment {detachment.Code} — {detachment.Name} " +
            $"({detachment.RegionDisplay}).",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<Detachment>.Ok(detachment,
            isNew ? $"Detachment {detachment.Code} created." : $"Detachment {detachment.Code} updated.");
    }

    public async Task<SaveResult<Detachment>> SetActiveAsync(int id, bool active, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
            return await RefuseAsync<Detachment>(performedBy, "retire or restore a detachment")
                .ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var detachment = await connection.FindAsync<Detachment>(id).ConfigureAwait(false);

        if (detachment is null)
            return SaveResult<Detachment>.Fail("That detachment no longer exists.");

        if (!active)
        {
            var deployed = await CountEmployeesAsync(id).ConfigureAwait(false);

            if (deployed > 0)
            {
                return SaveResult<Detachment>.Fail(
                    $"{deployed} employee(s) are still deployed to {detachment.Code}. " +
                    "Move them to another detachment first — retiring this one would leave them " +
                    "with no posted rate.");
            }
        }

        detachment.IsActive = active;
        await connection.UpdateAsync(detachment).ConfigureAwait(false);

        await _audit.WriteAsync(
            active ? AuditActions.DetachmentReactivated : AuditActions.DetachmentDeactivated,
            nameof(Detachment), detachment.Id, true,
            $"{(active ? "Restored" : "Retired")} detachment {detachment.Code} — {detachment.Name}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<Detachment>.Ok(detachment,
            active ? $"{detachment.Code} restored." : $"{detachment.Code} retired.");
    }

    // =====================================================================
    // Rates
    // =====================================================================

    public async Task<IReadOnlyList<PostedRate>> GetRatesAsync(int detachmentId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = (await connection.Table<DetachmentRate>()
            .Where(r => r.DetachmentId == detachmentId)
            .ToListAsync()
            .ConfigureAwait(false))
            .Where(r => r.IsActive)
            .ToList();

        // A row is superseded when another starts on or after it. Marked rather
        // than hidden: the sequence of wage orders is what makes an old payslip
        // explicable.
        var latest = rows
            .OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id)
            .Select(r => r.Id)
            .FirstOrDefault();

        return rows
            .OrderByDescending(r => r.EffectiveFrom)
            .ThenByDescending(r => r.Id)
            .Select(r => new PostedRate(r) { IsSuperseded = latest != r.Id })
            .ToList();
    }

    public async Task<SaveResult<DetachmentRate>> SaveRateAsync(DetachmentRate rate, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
            return await RefuseAsync<DetachmentRate>(performedBy, "post a detachment rate")
                .ConfigureAwait(false);

        if (rate.DetachmentId <= 0)
            return SaveResult<DetachmentRate>.Fail("Choose the detachment this rate is for.");

        if (rate.DailyRate <= 0m)
            return SaveResult<DetachmentRate>.Fail("Enter a daily rate above zero.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var detachment = await connection.FindAsync<Detachment>(rate.DetachmentId).ConfigureAwait(false);

        if (detachment is null)
            return SaveResult<DetachmentRate>.Fail("That detachment no longer exists.");

        rate.EffectiveFrom = rate.EffectiveFrom.Date;
        rate.Reference = (rate.Reference ?? string.Empty).Trim();
        rate.RecordedBy = performedBy.Username;

        // Same post, same day, is a correction rather than a second wage order:
        // two rows dated identically would resolve on their id, which is not a
        // decision anybody made.
        var detachmentId = rate.DetachmentId;
        var effective = rate.EffectiveFrom;

        var sameDay = (await connection.Table<DetachmentRate>()
            .Where(r => r.DetachmentId == detachmentId)
            .ToListAsync()
            .ConfigureAwait(false))
            .FirstOrDefault(r => r.IsActive && r.EffectiveFrom.Date == effective && r.Id != rate.Id);

        if (sameDay is not null)
        {
            sameDay.DailyRate = rate.DailyRate;
            sameDay.Reference = rate.Reference;
            sameDay.RecordedBy = rate.RecordedBy;

            await connection.UpdateAsync(sameDay).ConfigureAwait(false);

            await _audit.WriteAsync(AuditActions.DetachmentRatePosted, nameof(DetachmentRate),
                sameDay.Id, true,
                $"Corrected {detachment.Code} to {sameDay.DailyRate:N2} per day " +
                $"effective {sameDay.EffectiveFrom:dd MMM yyyy}.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return SaveResult<DetachmentRate>.Ok(sameDay,
                $"{detachment.Code} corrected to {sameDay.DailyRate:N2} per day.");
        }

        rate.Id = 0;
        rate.IsActive = true;
        rate.CreatedUtc = DateTime.UtcNow;

        await connection.InsertAsync(rate).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.DetachmentRatePosted, nameof(DetachmentRate), rate.Id, true,
            $"Posted {detachment.Code} at {rate.DailyRate:N2} per day " +
            $"effective {rate.EffectiveFrom:dd MMM yyyy}" +
            (rate.Reference.Length > 0 ? $" ({rate.Reference})." : "."),
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<DetachmentRate>.Ok(rate,
            $"{detachment.Code} is {rate.DailyRate:N2} per day " +
            $"from {rate.EffectiveFrom:dd MMM yyyy}.");
    }

    public async Task<SaveResult<DetachmentRate>> WithdrawRateAsync(int rateId, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
            return await RefuseAsync<DetachmentRate>(performedBy, "withdraw a detachment rate")
                .ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var rate = await connection.FindAsync<DetachmentRate>(rateId).ConfigureAwait(false);

        if (rate is null)
            return SaveResult<DetachmentRate>.Fail("That rate no longer exists.");

        rate.IsActive = false;
        await connection.UpdateAsync(rate).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.DetachmentRateWithdrawn, nameof(DetachmentRate),
            rate.Id, true,
            $"Withdrew the rate of {rate.DailyRate:N2} per day effective " +
            $"{rate.EffectiveFrom:dd MMM yyyy}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<DetachmentRate>.Ok(rate,
            "Rate withdrawn. Runs already posted keep the figures they were computed with.");
    }

    public async Task<DetachmentRateTable> GetRateTableAsync(DateTime asOf)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<DetachmentRate>().ToListAsync().ConfigureAwait(false);

        return new DetachmentRateTable(rows, asOf);
    }

    // =====================================================================
    // Headcount
    // =====================================================================

    public async Task<int> CountEmployeesAsync(int detachmentId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var employees = await connection.Table<Employee>().ToListAsync().ConfigureAwait(false);

        return employees.Count(e => e.DetachmentId == detachmentId && e.IsActive && !e.IsArchived);
    }

    public async Task<IReadOnlyDictionary<int, int>> GetHeadcountsAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var employees = await connection.Table<Employee>().ToListAsync().ConfigureAwait(false);

        return employees
            .Where(e => e.IsActive && !e.IsArchived && e.DetachmentId.HasValue)
            .GroupBy(e => e.DetachmentId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private async Task<SaveResult<T>> RefuseAsync<T>(User performedBy, string action)
        where T : class
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, nameof(Detachment), null, false,
            $"Role {performedBy.RoleDisplayName} is not permitted to {action}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<T>.Fail("You do not have permission to perform this action.");
    }
}
