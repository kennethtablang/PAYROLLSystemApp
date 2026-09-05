using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

public interface IStatutoryTableService
{
    Task<IReadOnlyList<SssBracket>> GetSssScheduleAsync(DateTime asOf);

    Task<IReadOnlyList<DateTime>> GetSssEffectiveDatesAsync();

    Task<IReadOnlyList<PhilHealthRate>> GetPhilHealthRatesAsync();

    Task<IReadOnlyList<PagIbigRate>> GetPagIbigRatesAsync();

    Task<IReadOnlyList<WithholdingTaxBracket>> GetTaxBracketsAsync(PayFrequency frequency, bool annual);

    /// <summary>Every schedule in force on a date, for a payroll run.</summary>
    Task<StatutorySnapshot> GetSnapshotAsync(DateTime asOf);

    Task<SaveResult<PhilHealthRate>> SavePhilHealthRateAsync(PhilHealthRate rate, User performedBy);

    Task<SaveResult<PagIbigRate>> SavePagIbigRateAsync(PagIbigRate rate, User performedBy);

    Task<SaveResult<SssBracket>> SaveSssBracketAsync(SssBracket bracket, User performedBy);

    Task<SaveResult<WithholdingTaxBracket>> SaveTaxBracketAsync(
        WithholdingTaxBracket bracket, User performedBy);
}

/// <summary>
/// FR-043. The SSS, PhilHealth, Pag-IBIG and BIR tables, held as editable,
/// effective-dated configuration.
///
/// <para><b>Effective-dated, not versioned in code.</b> All four change by
/// circular, sometimes mid-year and sometimes retroactively. C-02 requires that
/// a historical run keep the rates it was computed under, so a new schedule is a
/// <em>new set of rows with a later effective date</em>; the old rows are never
/// edited and never removed. <see cref="StatutorySnapshot"/> picks the winner
/// for a given pay date.</para>
///
/// <para><b>What is not enforced here.</b> Nothing in this service knows whether
/// the seeded figures are current — no application can. The screens say so, the
/// seed comments say so, and the figures must be checked against the agencies'
/// current circulars before the first live run.</para>
/// </summary>
public sealed class StatutoryTableService : IStatutoryTableService
{
    private readonly PayrollDatabase _database;
    private readonly IAuditService _audit;

    public StatutoryTableService(PayrollDatabase database, IAuditService audit)
    {
        _database = database;
        _audit = audit;
    }

    public async Task<IReadOnlyList<SssBracket>> GetSssScheduleAsync(DateTime asOf)
    {
        var snapshot = await GetSnapshotAsync(asOf).ConfigureAwait(false);

        return snapshot.SssSchedule
            .OrderBy(b => b.MonthlySalaryCredit)
            .ToList();
    }

    public async Task<IReadOnlyList<DateTime>> GetSssEffectiveDatesAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<SssBracket>().ToListAsync().ConfigureAwait(false);

        return rows
            .Select(b => b.EffectiveFrom.Date)
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();
    }

    public async Task<IReadOnlyList<PhilHealthRate>> GetPhilHealthRatesAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<PhilHealthRate>().ToListAsync().ConfigureAwait(false);

        return rows.OrderByDescending(r => r.EffectiveFrom).ToList();
    }

    public async Task<IReadOnlyList<PagIbigRate>> GetPagIbigRatesAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<PagIbigRate>().ToListAsync().ConfigureAwait(false);

        return rows.OrderByDescending(r => r.EffectiveFrom).ToList();
    }

    public async Task<IReadOnlyList<WithholdingTaxBracket>> GetTaxBracketsAsync(
        PayFrequency frequency, bool annual)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<WithholdingTaxBracket>().ToListAsync().ConfigureAwait(false);

        return rows
            .Where(b => b.IsActive && b.IsAnnual == annual && (annual || b.Frequency == frequency))
            .OrderByDescending(b => b.EffectiveFrom)
            .ThenBy(b => b.LowerLimit)
            .ToList();
    }

    public async Task<StatutorySnapshot> GetSnapshotAsync(DateTime asOf)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var sss = await connection.Table<SssBracket>().ToListAsync().ConfigureAwait(false);
        var philHealth = await connection.Table<PhilHealthRate>().ToListAsync().ConfigureAwait(false);
        var pagIbig = await connection.Table<PagIbigRate>().ToListAsync().ConfigureAwait(false);
        var tax = await connection.Table<WithholdingTaxBracket>().ToListAsync().ConfigureAwait(false);

        var date = asOf.Date;

        return new StatutorySnapshot(
            date,
            sss,
            philHealth
                .Where(r => r.IsActive && r.EffectiveFrom.Date <= date)
                .OrderByDescending(r => r.EffectiveFrom)
                .FirstOrDefault(),
            pagIbig
                .Where(r => r.IsActive && r.EffectiveFrom.Date <= date)
                .OrderByDescending(r => r.EffectiveFrom)
                .FirstOrDefault(),
            tax);
    }

    // =====================================================================

    public async Task<SaveResult<PhilHealthRate>> SavePhilHealthRateAsync(
        PhilHealthRate rate, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<PhilHealthRate>(performedBy, "edit the PhilHealth schedule", nameof(PhilHealthRate))
                .ConfigureAwait(false);

        rate.EffectiveFrom = rate.EffectiveFrom.Date;
        rate.Remarks = (rate.Remarks ?? string.Empty).Trim();

        if (rate.PremiumRatePercent is <= 0 or > 20)
            return SaveResult<PhilHealthRate>.Fail("The premium rate must be between 0 and 20 percent.");

        if (rate.SalaryFloor < 0)
            return SaveResult<PhilHealthRate>.Fail("The salary floor cannot be negative.");

        if (rate.SalaryCeiling <= rate.SalaryFloor)
            return SaveResult<PhilHealthRate>.Fail("The ceiling must be above the floor.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var guard = await GuardEffectiveDateAsync<PhilHealthRate>(
            rate.Id, rate.EffectiveFrom,
            await connection.Table<PhilHealthRate>().ToListAsync().ConfigureAwait(false),
            r => r.Id, r => r.EffectiveFrom, "PhilHealth premium").ConfigureAwait(false);

        if (guard is not null)
            return guard;

        var isNew = rate.Id == 0;

        if (isNew)
            await connection.InsertAsync(rate).ConfigureAwait(false);
        else
            await connection.UpdateAsync(rate).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.StatutoryTableChanged, nameof(PhilHealthRate), rate.Id, true,
            $"PhilHealth schedule {(isNew ? "added" : "updated")}, effective {rate.EffectiveFrom:dd MMM yyyy}: " +
            $"{rate.Display}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PhilHealthRate>.Ok(rate,
            $"PhilHealth premium saved, effective {rate.EffectiveFrom:dd MMM yyyy}.");
    }

    public async Task<SaveResult<PagIbigRate>> SavePagIbigRateAsync(PagIbigRate rate, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<PagIbigRate>(performedBy, "edit the Pag-IBIG schedule", nameof(PagIbigRate))
                .ConfigureAwait(false);

        rate.EffectiveFrom = rate.EffectiveFrom.Date;
        rate.Remarks = (rate.Remarks ?? string.Empty).Trim();

        if (rate.EmployeeRateLowPercent is < 0 or > 20 ||
            rate.EmployeeRateHighPercent is < 0 or > 20 ||
            rate.EmployerRatePercent is < 0 or > 20)
        {
            return SaveResult<PagIbigRate>.Fail("Contribution rates must be between 0 and 20 percent.");
        }

        if (rate.FundSalaryCap <= 0)
            return SaveResult<PagIbigRate>.Fail("The fund salary cap must be above zero.");

        if (rate.LowerRateThreshold < 0)
            return SaveResult<PagIbigRate>.Fail("The lower-rate threshold cannot be negative.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var guard = await GuardEffectiveDateAsync<PagIbigRate>(
            rate.Id, rate.EffectiveFrom,
            await connection.Table<PagIbigRate>().ToListAsync().ConfigureAwait(false),
            r => r.Id, r => r.EffectiveFrom, "Pag-IBIG contribution").ConfigureAwait(false);

        if (guard is not null)
            return guard;

        var isNew = rate.Id == 0;

        if (isNew)
            await connection.InsertAsync(rate).ConfigureAwait(false);
        else
            await connection.UpdateAsync(rate).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.StatutoryTableChanged, nameof(PagIbigRate), rate.Id, true,
            $"Pag-IBIG schedule {(isNew ? "added" : "updated")}, effective {rate.EffectiveFrom:dd MMM yyyy}: " +
            $"{rate.Display}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PagIbigRate>.Ok(rate,
            $"Pag-IBIG contribution saved, effective {rate.EffectiveFrom:dd MMM yyyy}.");
    }

    public async Task<SaveResult<SssBracket>> SaveSssBracketAsync(SssBracket bracket, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<SssBracket>(performedBy, "edit the SSS schedule", nameof(SssBracket))
                .ConfigureAwait(false);

        bracket.EffectiveFrom = bracket.EffectiveFrom.Date;

        if (bracket.MonthlySalaryCredit <= 0)
            return SaveResult<SssBracket>.Fail("The monthly salary credit must be above zero.");

        if (bracket.RangeTo is { } upperBound && upperBound <= bracket.RangeFrom)
            return SaveResult<SssBracket>.Fail("The upper bound must be above the lower bound.");

        if (bracket.EmployeeShare < 0 || bracket.EmployerShare < 0 ||
            bracket.EmployeeWisp < 0 || bracket.EmployerWisp < 0 || bracket.EmployerEc < 0)
        {
            return SaveResult<SssBracket>.Fail("A contribution cannot be negative.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var isNew = bracket.Id == 0;

        // A bracket belongs to a schedule, and a schedule is only meaningful as a
        // whole. Editing one row of a schedule that is already in force would
        // leave the rest of it on the old circular, so a live row is refused
        // rather than half-changed.
        if (!isNew)
        {
            var current = await connection.Table<SssBracket>()
                .Where(b => b.Id == bracket.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (current is null)
                return SaveResult<SssBracket>.Fail("That bracket no longer exists.");

            if (current.EffectiveFrom.Date <= DateTime.Today &&
                bracket.EffectiveFrom.Date == current.EffectiveFrom.Date)
            {
                return SaveResult<SssBracket>.Fail(
                    $"The schedule of {current.EffectiveFrom:dd MMM yyyy} is in force and may have priced " +
                    "payslips already. Enter the new circular as a schedule with a later effective date " +
                    "rather than editing this one.");
            }

            await connection.UpdateAsync(bracket).ConfigureAwait(false);
        }
        else
        {
            await connection.InsertAsync(bracket).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            AuditActions.StatutoryTableChanged, nameof(SssBracket), bracket.Id, true,
            $"SSS bracket {(isNew ? "added" : "updated")}, effective {bracket.EffectiveFrom:dd MMM yyyy}: " +
            $"{bracket.RangeDisplay} → {bracket.Display}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<SssBracket>.Ok(bracket, "SSS bracket saved.");
    }

    public async Task<SaveResult<WithholdingTaxBracket>> SaveTaxBracketAsync(
        WithholdingTaxBracket bracket, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<WithholdingTaxBracket>(
                performedBy, "edit the withholding tax table", nameof(WithholdingTaxBracket))
                .ConfigureAwait(false);

        bracket.EffectiveFrom = bracket.EffectiveFrom.Date;

        if (bracket.LowerLimit < 0)
            return SaveResult<WithholdingTaxBracket>.Fail("The lower limit cannot be negative.");

        if (bracket.UpperLimit is { } upper && upper <= bracket.LowerLimit)
            return SaveResult<WithholdingTaxBracket>.Fail("The upper limit must be above the lower limit.");

        if (bracket.BaseTax < 0)
            return SaveResult<WithholdingTaxBracket>.Fail("The base tax cannot be negative.");

        if (bracket.RateOnExcessPercent is < 0 or > 100)
            return SaveResult<WithholdingTaxBracket>.Fail("The rate must be between 0 and 100 percent.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var isNew = bracket.Id == 0;

        if (!isNew)
        {
            var current = await connection.Table<WithholdingTaxBracket>()
                .Where(b => b.Id == bracket.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (current is null)
                return SaveResult<WithholdingTaxBracket>.Fail("That bracket no longer exists.");

            if (current.EffectiveFrom.Date <= DateTime.Today &&
                bracket.EffectiveFrom.Date == current.EffectiveFrom.Date)
            {
                return SaveResult<WithholdingTaxBracket>.Fail(
                    $"The table of {current.EffectiveFrom:dd MMM yyyy} is in force and may have withheld tax " +
                    "already. Enter the new issuance as a table with a later effective date rather than " +
                    "editing this one.");
            }

            bracket.Frequency = current.Frequency;
            bracket.IsAnnual = current.IsAnnual;

            await connection.UpdateAsync(bracket).ConfigureAwait(false);
        }
        else
        {
            await connection.InsertAsync(bracket).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            AuditActions.StatutoryTableChanged, nameof(WithholdingTaxBracket), bracket.Id, true,
            $"{bracket.ScopeDisplay} tax band {(isNew ? "added" : "updated")}, " +
            $"effective {bracket.EffectiveFrom:dd MMM yyyy}: {bracket.RangeDisplay} → {bracket.TaxDisplay}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<WithholdingTaxBracket>.Ok(bracket, "Tax band saved.");
    }

    // =====================================================================

    /// <summary>
    /// Refuses an edit that would change a schedule already in force, and a new
    /// schedule dated no later than the one it supersedes.
    ///
    /// <para>Returns null when the save may proceed.</para>
    /// </summary>
    private Task<SaveResult<T>?> GuardEffectiveDateAsync<T>(
        int id,
        DateTime effectiveFrom,
        IReadOnlyList<T> existing,
        Func<T, int> idOf,
        Func<T, DateTime> effectiveOf,
        string label) where T : class
    {
        var current = id == 0 ? null : existing.FirstOrDefault(r => idOf(r) == id);

        if (current is not null &&
            effectiveOf(current).Date <= DateTime.Today &&
            effectiveOf(current).Date == effectiveFrom.Date)
        {
            return Task.FromResult<SaveResult<T>?>(SaveResult<T>.Fail(
                $"The {label} schedule of {effectiveOf(current):dd MMM yyyy} is in force and may have priced " +
                "payslips already. Give the change a later effective date — the old row is kept so historical " +
                "runs can still be explained."));
        }

        if (current is null && existing.Any(r => effectiveOf(r).Date == effectiveFrom.Date))
        {
            return Task.FromResult<SaveResult<T>?>(SaveResult<T>.Fail(
                $"A {label} schedule already takes effect on {effectiveFrom:dd MMM yyyy}. " +
                "Edit that one, or choose a different date."));
        }

        return Task.FromResult<SaveResult<T>?>(null);
    }

    private async Task<SaveResult<T>> RefuseAsync<T>(User performedBy, string action, string entity)
        where T : class
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, entity, null, false,
            $"Role {performedBy.RoleDisplayName} is not permitted to {action}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<T>.Fail("You do not have permission to perform this action.");
    }
}
