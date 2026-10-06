using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Security;

namespace PAYROLLSystemApp.Services;

public interface IPayrollConfigService
{
    // ------------------------------------------------ FR-040 pay calendar

    Task<IReadOnlyList<PayPeriod>> GetPayPeriodsAsync(int year);

    /// <summary>The period whose attendance cut-off covers a date, if any.</summary>
    Task<PayPeriod?> GetPeriodForDateAsync(DateTime date);

    /// <summary>The years the calendar holds periods for, newest first.</summary>
    Task<IReadOnlyList<int>> GetCalendarYearsAsync();

    Task<SaveResult<PayPeriod>> SavePayPeriodAsync(PayPeriod period, User performedBy);

    Task<SaveResult<PayPeriod>> SetPayPeriodStatusAsync(int id, PayPeriodStatus status, User performedBy);

    /// <summary>
    /// Generates a year of periods from the current settings. Existing periods
    /// in that year are kept and only the gaps are filled.
    /// </summary>
    Task<SaveResult<PayPeriod>> GenerateYearAsync(int year, User performedBy);

    // -------------------------------------- FR-041 / FR-042 pay components

    Task<IReadOnlyList<EarningType>> GetEarningTypesAsync(bool includeInactive = false);

    Task<IReadOnlyList<DeductionType>> GetDeductionTypesAsync(bool includeInactive = false);

    Task<SaveResult<EarningType>> SaveEarningTypeAsync(EarningType type, User performedBy);

    Task<SaveResult<DeductionType>> SaveDeductionTypeAsync(DeductionType type, User performedBy);

    Task<SaveResult<EarningType>> SetEarningTypeActiveAsync(int id, bool active, User performedBy);

    Task<SaveResult<DeductionType>> SetDeductionTypeActiveAsync(int id, bool active, User performedBy);

    // ------------------------------------------------ FR-044 premium rates

    Task<IReadOnlyList<PremiumRate>> GetPremiumRatesAsync(bool includeInactive = false);

    /// <summary>The matrix in force on a date, for the payroll engine.</summary>
    Task<PremiumMatrix> GetPremiumMatrixAsync(DateTime asOf);

    Task<SaveResult<PremiumRate>> SavePremiumRateAsync(PremiumRate rate, User performedBy);

    // ------------------------------- FR-045 company profile and settings

    Task<CompanyProfile> GetCompanyProfileAsync();

    Task<SaveResult<CompanyProfile>> SaveCompanyProfileAsync(CompanyProfile profile, User performedBy);

    Task<PayrollSettings> GetSettingsAsync();

    Task<SaveResult<PayrollSettings>> SaveSettingsAsync(PayrollSettings settings, User performedBy);

    /// <summary>FR-085. The stock report PDFs are laid out for; set from Settings.</summary>
    Task<SaveResult<PayrollSettings>> SaveReportPaperAsync(ReportPaper paper, User performedBy);
}

/// <summary>
/// Section 2.5. The configuration every payroll figure is derived from: the pay
/// calendar, the earning and deduction types, the DOLE premium matrix, the
/// company profile and the company-wide payroll rules.
///
/// <para><b>This is the most dangerous screen in the system.</b> Nothing here
/// computes a payslip, and every row here changes what one comes out at — the
/// working-days factor reprices every hour, a premium multiplier reprices every
/// holiday, a taxable flag moves money between the employee and the BIR. That is
/// why the whole section is gated on
/// <see cref="Permission.ManageSystemConfiguration"/> rather than on the
/// permission that runs payroll, and why the audit entries name the figures that
/// moved rather than saying a record was updated.</para>
///
/// <para><b>Nothing is deleted.</b> A component names a line on payslips already
/// issued; a premium rate is what a historical run was computed from. Components
/// are retired, and a rate change is a <em>new row with a later effective date</em>
/// rather than an edit to the one history was computed against (C-02).</para>
/// </summary>
public sealed class PayrollConfigService : IPayrollConfigService
{
    private readonly PayrollDatabase _database;
    private readonly IAuditService _audit;

    public PayrollConfigService(PayrollDatabase database, IAuditService audit)
    {
        _database = database;
        _audit = audit;
    }

    // =====================================================================
    // FR-040 — pay periods, cut-offs and pay dates
    // =====================================================================

    public async Task<IReadOnlyList<PayPeriod>> GetPayPeriodsAsync(int year)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<PayPeriod>().ToListAsync().ConfigureAwait(false);

        return rows
            .Where(p => p.PeriodEnd.Year == year)
            .OrderBy(p => p.PeriodStart)
            .ToList();
    }

    public async Task<IReadOnlyList<int>> GetCalendarYearsAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<PayPeriod>().ToListAsync().ConfigureAwait(false);

        var years = rows.Select(p => p.PeriodEnd.Year).Distinct().ToList();

        // The current year is always offered, so a database whose calendar has
        // run out still opens on a year the user can generate into.
        if (!years.Contains(DateTime.Today.Year))
            years.Add(DateTime.Today.Year);

        return years.OrderByDescending(y => y).ToList();
    }

    public async Task<PayPeriod?> GetPeriodForDateAsync(DateTime date)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<PayPeriod>().ToListAsync().ConfigureAwait(false);

        return rows
            .Where(p => p.CoversAttendance(date))
            .OrderBy(p => p.PeriodStart)
            .FirstOrDefault();
    }

    public async Task<SaveResult<PayPeriod>> SavePayPeriodAsync(PayPeriod period, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<PayPeriod>(performedBy, "maintain the pay calendar", nameof(PayPeriod))
                .ConfigureAwait(false);

        period.Code = (period.Code ?? string.Empty).Trim().ToUpperInvariant();
        period.Name = (period.Name ?? string.Empty).Trim();
        period.Remarks = (period.Remarks ?? string.Empty).Trim();
        period.PeriodStart = period.PeriodStart.Date;
        period.PeriodEnd = period.PeriodEnd.Date;
        period.CutOffStart = period.CutOffStart.Date;
        period.CutOffEnd = period.CutOffEnd.Date;
        period.PayDate = period.PayDate.Date;

        if (period.Code.Length == 0)
            return SaveResult<PayPeriod>.Fail("Enter a code for the period.");

        if (period.Name.Length == 0)
            return SaveResult<PayPeriod>.Fail("Enter a name for the period.");

        if (period.PeriodEnd < period.PeriodStart)
            return SaveResult<PayPeriod>.Fail("The period cannot end before it starts.");

        if (period.CutOffEnd < period.CutOffStart)
            return SaveResult<PayPeriod>.Fail("The cut-off cannot end before it starts.");

        // A pay date before the cut-off closes would pay for time nobody has
        // recorded yet. It is legal to pay in advance, but not to compute one.
        if (period.PayDate < period.CutOffEnd)
        {
            return SaveResult<PayPeriod>.Fail(
                "The pay date falls before the attendance cut-off closes, so the run would have " +
                "nothing to compute from. Move the pay date or bring the cut-off forward.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var existing = await connection.Table<PayPeriod>().ToListAsync().ConfigureAwait(false);

        if (existing.Any(p => p.Id != period.Id &&
                              string.Equals(p.Code, period.Code, StringComparison.OrdinalIgnoreCase)))
        {
            return SaveResult<PayPeriod>.Fail($"The code {period.Code} is already used by another period.");
        }

        // Overlapping periods are how an employee is paid twice for the same
        // days, or not at all for days that fall in the gap.
        var overlap = existing.FirstOrDefault(p => p.Id != period.Id && p.OverlapsPeriod(period));
        if (overlap is not null)
        {
            return SaveResult<PayPeriod>.Fail(
                $"Those dates overlap {overlap.Code} ({overlap.RangeDisplay}). " +
                "Two periods covering the same day would pay it twice.");
        }

        var isNew = period.Id == 0;

        if (isNew)
        {
            await connection.InsertAsync(period).ConfigureAwait(false);
        }
        else
        {
            var current = existing.FirstOrDefault(p => p.Id == period.Id);

            if (current is null)
                return SaveResult<PayPeriod>.Fail("That period is no longer in the calendar.");

            if (current.Status != PayPeriodStatus.Open)
            {
                return SaveResult<PayPeriod>.Fail(
                    $"{current.Code} is {current.StatusDisplay.ToLowerInvariant()}. " +
                    "Reopen it before changing its dates — payroll has already been computed against them.");
            }

            // Status is moved by its own guarded operation, so an edit carries
            // the current one forward.
            period.Status = current.Status;
            period.CreatedUtc = current.CreatedUtc;

            await connection.UpdateAsync(period).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            isNew ? AuditActions.PayPeriodCreated : AuditActions.PayPeriodUpdated,
            nameof(PayPeriod), period.Id, true,
            $"{(isNew ? "Added" : "Updated")} {period.Code}: covers {period.RangeDisplay}, " +
            $"cut-off {period.CutOffDisplay}, paid {period.PayDate:dd MMM yyyy}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayPeriod>.Ok(period,
            isNew ? $"{period.Code} added to the calendar." : $"{period.Code} updated.");
    }

    public async Task<SaveResult<PayPeriod>> SetPayPeriodStatusAsync(
        int id, PayPeriodStatus status, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<PayPeriod>(performedBy, "change the state of a pay period", nameof(PayPeriod))
                .ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var period = await connection.Table<PayPeriod>()
            .Where(p => p.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (period is null)
            return SaveResult<PayPeriod>.Fail("That period is no longer in the calendar.");

        var previous = period.Status;
        period.Status = status;

        await connection.UpdateAsync(period).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.PayPeriodStatusChanged, nameof(PayPeriod), period.Id, true,
            $"{period.Code} moved from {PayrollEnumNames.Display(previous)} to " +
            $"{PayrollEnumNames.Display(status)}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        var message = status switch
        {
            PayPeriodStatus.Locked =>
                $"{period.Code} locked. Attendance inside the cut-off can no longer be changed.",
            PayPeriodStatus.Closed =>
                $"{period.Code} closed.",
            _ =>
                $"{period.Code} reopened. Anything already computed against it should be recalculated."
        };

        return SaveResult<PayPeriod>.Ok(period, message);
    }

    public async Task<SaveResult<PayPeriod>> GenerateYearAsync(int year, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<PayPeriod>(performedBy, "generate a pay calendar", nameof(PayPeriod))
                .ConfigureAwait(false);

        if (year < 2000 || year > 2100)
            return SaveResult<PayPeriod>.Fail("Enter a year between 2000 and 2100.");

        var settings = await GetSettingsAsync().ConfigureAwait(false);
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var existing = await connection.Table<PayPeriod>().ToListAsync().ConfigureAwait(false);
        var proposed = PayPeriodGenerator.ForYear(year, settings);

        // Only the gaps are filled. Regenerating over a period that has already
        // been paid would silently move its cut-off, so an existing row wins
        // whether or not it matches what the generator would produce.
        var added = proposed
            .Where(p => !existing.Any(e =>
                string.Equals(e.Code, p.Code, StringComparison.OrdinalIgnoreCase) || e.OverlapsPeriod(p)))
            .ToList();

        if (added.Count == 0)
        {
            return SaveResult<PayPeriod>.Fail(
                $"{year} is already covered. Nothing was generated — existing periods are never overwritten.");
        }

        await connection.InsertAllAsync(added).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.PayPeriodGenerated, nameof(PayPeriod), null, true,
            $"Generated {added.Count} {settings.FrequencyDisplay.ToLowerInvariant()} period(s) for {year} " +
            $"(cut-off closes {settings.CutOffLeadDays} day(s) before period end; " +
            $"pay date {settings.PayDateLagDays} day(s) after).",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        var kept = proposed.Count - added.Count;

        return SaveResult<PayPeriod>.Ok(added[0],
            kept > 0
                ? $"{added.Count} period(s) added for {year}. {kept} existing period(s) were left as they are."
                : $"{added.Count} period(s) added for {year}.");
    }

    // =====================================================================
    // FR-041 / FR-042 — earning and deduction types
    // =====================================================================

    public async Task<IReadOnlyList<EarningType>> GetEarningTypesAsync(bool includeInactive = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var query = connection.Table<EarningType>();
        if (!includeInactive)
            query = query.Where(e => e.IsActive);

        var rows = await query.ToListAsync().ConfigureAwait(false);

        return rows.OrderBy(e => e.DisplayOrder).ThenBy(e => e.Code).ToList();
    }

    public async Task<IReadOnlyList<DeductionType>> GetDeductionTypesAsync(bool includeInactive = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var query = connection.Table<DeductionType>();
        if (!includeInactive)
            query = query.Where(d => d.IsActive);

        var rows = await query.ToListAsync().ConfigureAwait(false);

        return rows.OrderBy(d => d.DisplayOrder).ThenBy(d => d.Code).ToList();
    }

    public async Task<SaveResult<EarningType>> SaveEarningTypeAsync(EarningType type, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<EarningType>(performedBy, "maintain earning types", nameof(EarningType))
                .ConfigureAwait(false);

        type.Code = (type.Code ?? string.Empty).Trim().ToUpperInvariant();
        type.Name = (type.Name ?? string.Empty).Trim();
        type.Description = (type.Description ?? string.Empty).Trim();

        if (type.Code.Length == 0)
            return SaveResult<EarningType>.Fail("Enter a code for the earning.");

        if (type.Name.Length == 0)
            return SaveResult<EarningType>.Fail("Enter a name for the earning.");

        if (type.DefaultAmount < 0)
            return SaveResult<EarningType>.Fail("The amount cannot be negative.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var existing = await connection.Table<EarningType>().ToListAsync().ConfigureAwait(false);

        if (existing.Any(e => e.Id != type.Id &&
                              string.Equals(e.Code, type.Code, StringComparison.OrdinalIgnoreCase)))
        {
            return SaveResult<EarningType>.Fail($"The code {type.Code} is already used by another earning.");
        }

        var isNew = type.Id == 0;
        EarningType? current = null;

        if (!isNew)
        {
            current = existing.FirstOrDefault(e => e.Id == type.Id);

            if (current is null)
                return SaveResult<EarningType>.Fail("That earning type no longer exists.");

            // The engine finds its own lines by code, so a system row keeps its
            // code, its category and its system flag. Everything a company might
            // legitimately want to change — the name, the taxability, the
            // contribution base — stays open.
            if (current.IsSystem)
            {
                type.Code = current.Code;
                type.Category = current.Category;
                type.Method = current.Method;
            }

            type.IsSystem = current.IsSystem;
            type.IsActive = current.IsActive;
            type.CreatedUtc = current.CreatedUtc;

            await connection.UpdateAsync(type).ConfigureAwait(false);
        }
        else
        {
            await connection.InsertAsync(type).ConfigureAwait(false);
        }

        // The taxable and contribution-base flags are named in the log because
        // they are what moves money; a change to either is worth being able to
        // find later.
        await _audit.WriteAsync(
            isNew ? AuditActions.EarningTypeCreated : AuditActions.EarningTypeUpdated,
            nameof(EarningType), type.Id, true,
            $"{(isNew ? "Added" : "Updated")} earning {type.Code} — {type.Name}: " +
            $"{type.CategoryDisplay}, {type.AmountDisplay}, {type.FlagsDisplay}." +
            Changed(current, type),
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<EarningType>.Ok(type,
            isNew ? $"{type.Name} added." : $"{type.Name} updated.");
    }

    /// <summary>
    /// Names a change of taxability or contribution base explicitly in the audit
    /// detail. Both are silent on a payslip and both change what is remitted.
    /// </summary>
    private static string Changed(EarningType? before, EarningType after)
    {
        if (before is null)
            return string.Empty;

        var notes = new List<string>();

        if (before.IsTaxable != after.IsTaxable)
            notes.Add($"taxability changed from {before.TaxDisplay} to {after.TaxDisplay}");

        if (before.IsPartOfContributionBase != after.IsPartOfContributionBase)
        {
            notes.Add(after.IsPartOfContributionBase
                ? "now counts towards the SSS and Pag-IBIG base"
                : "no longer counts towards the SSS and Pag-IBIG base");
        }

        if (before.IsThirteenthMonthBase != after.IsThirteenthMonthBase)
        {
            notes.Add(after.IsThirteenthMonthBase
                ? "now counts towards 13th month pay"
                : "no longer counts towards 13th month pay");
        }

        return notes.Count == 0 ? string.Empty : " " + string.Join("; ", notes) + ".";
    }

    public async Task<SaveResult<DeductionType>> SaveDeductionTypeAsync(DeductionType type, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<DeductionType>(performedBy, "maintain deduction types", nameof(DeductionType))
                .ConfigureAwait(false);

        type.Code = (type.Code ?? string.Empty).Trim().ToUpperInvariant();
        type.Name = (type.Name ?? string.Empty).Trim();
        type.Description = (type.Description ?? string.Empty).Trim();

        if (type.Code.Length == 0)
            return SaveResult<DeductionType>.Fail("Enter a code for the deduction.");

        if (type.Name.Length == 0)
            return SaveResult<DeductionType>.Fail("Enter a name for the deduction.");

        if (type.DefaultAmount < 0)
            return SaveResult<DeductionType>.Fail("The amount cannot be negative.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var existing = await connection.Table<DeductionType>().ToListAsync().ConfigureAwait(false);

        if (existing.Any(d => d.Id != type.Id &&
                              string.Equals(d.Code, type.Code, StringComparison.OrdinalIgnoreCase)))
        {
            return SaveResult<DeductionType>.Fail($"The code {type.Code} is already used by another deduction.");
        }

        var isNew = type.Id == 0;
        DeductionType? current = null;

        if (!isNew)
        {
            current = existing.FirstOrDefault(d => d.Id == type.Id);

            if (current is null)
                return SaveResult<DeductionType>.Fail("That deduction type no longer exists.");

            if (current.IsSystem)
            {
                type.Code = current.Code;
                type.Category = current.Category;
                type.Method = current.Method;

                // Moving a statutory deduction out of the tax base would
                // under-withhold tax the employer is liable for, so it is not
                // an edit the screen is allowed to make.
                type.ReducesTaxableIncome = current.ReducesTaxableIncome;
            }

            type.IsSystem = current.IsSystem;
            type.IsActive = current.IsActive;
            type.CreatedUtc = current.CreatedUtc;

            await connection.UpdateAsync(type).ConfigureAwait(false);
        }
        else
        {
            await connection.InsertAsync(type).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            isNew ? AuditActions.DeductionTypeCreated : AuditActions.DeductionTypeUpdated,
            nameof(DeductionType), type.Id, true,
            $"{(isNew ? "Added" : "Updated")} deduction {type.Code} — {type.Name}: " +
            $"{type.CategoryDisplay}, {type.AmountDisplay}, {type.FlagsDisplay}." +
            (current is not null && current.ReducesTaxableIncome != type.ReducesTaxableIncome
                ? $" Tax treatment changed to {type.FlagsDisplay}."
                : string.Empty),
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<DeductionType>.Ok(type,
            isNew ? $"{type.Name} added." : $"{type.Name} updated.");
    }

    public async Task<SaveResult<EarningType>> SetEarningTypeActiveAsync(int id, bool active, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<EarningType>(performedBy, "retire or restore an earning type", nameof(EarningType))
                .ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var type = await connection.Table<EarningType>()
            .Where(e => e.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (type is null)
            return SaveResult<EarningType>.Fail("That earning type no longer exists.");

        // Retiring basic pay or overtime would leave the engine with no line to
        // put the money on, and it would fail at the least convenient moment.
        if (type.IsSystem && !active)
        {
            return SaveResult<EarningType>.Fail(
                $"{type.Name} is produced by the payroll engine and cannot be retired. " +
                "Rename it or change its tax treatment instead.");
        }

        type.IsActive = active;
        await connection.UpdateAsync(type).ConfigureAwait(false);

        await _audit.WriteAsync(
            active ? AuditActions.EarningTypeReactivated : AuditActions.EarningTypeDeactivated,
            nameof(EarningType), type.Id, true,
            $"{(active ? "Restored" : "Retired")} earning {type.Code} — {type.Name}. " +
            "Payslips already issued against it are unchanged.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<EarningType>.Ok(type,
            active ? $"{type.Name} restored." : $"{type.Name} retired.");
    }

    public async Task<SaveResult<DeductionType>> SetDeductionTypeActiveAsync(int id, bool active, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<DeductionType>(performedBy, "retire or restore a deduction type", nameof(DeductionType))
                .ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var type = await connection.Table<DeductionType>()
            .Where(d => d.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (type is null)
            return SaveResult<DeductionType>.Fail("That deduction type no longer exists.");

        if (type.IsSystem && !active)
        {
            return SaveResult<DeductionType>.Fail(
                $"{type.Name} is a statutory deduction the payroll engine produces and cannot be retired. " +
                "Withholding it is not optional.");
        }

        type.IsActive = active;
        await connection.UpdateAsync(type).ConfigureAwait(false);

        await _audit.WriteAsync(
            active ? AuditActions.DeductionTypeReactivated : AuditActions.DeductionTypeDeactivated,
            nameof(DeductionType), type.Id, true,
            $"{(active ? "Restored" : "Retired")} deduction {type.Code} — {type.Name}. " +
            "Payslips already issued against it are unchanged.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<DeductionType>.Ok(type,
            active ? $"{type.Name} restored." : $"{type.Name} retired.");
    }

    // =====================================================================
    // FR-044 — the premium matrix
    // =====================================================================

    public async Task<IReadOnlyList<PremiumRate>> GetPremiumRatesAsync(bool includeInactive = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var query = connection.Table<PremiumRate>();
        if (!includeInactive)
            query = query.Where(r => r.IsActive);

        var rows = await query.ToListAsync().ConfigureAwait(false);

        return rows
            .OrderBy(r => r.IsAdditive)
            .ThenBy(r => r.DayType)
            .ThenBy(r => r.IsRestDay)
            .ThenBy(r => r.IsOvertime)
            .ThenByDescending(r => r.EffectiveFrom)
            .ToList();
    }

    public async Task<PremiumMatrix> GetPremiumMatrixAsync(DateTime asOf)
    {
        var rates = await GetPremiumRatesAsync(includeInactive: false).ConfigureAwait(false);
        return new PremiumMatrix(rates, asOf);
    }

    public async Task<SaveResult<PremiumRate>> SavePremiumRateAsync(PremiumRate rate, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<PremiumRate>(performedBy, "change a premium multiplier", nameof(PremiumRate))
                .ConfigureAwait(false);

        rate.Code = (rate.Code ?? string.Empty).Trim().ToUpperInvariant();
        rate.Name = (rate.Name ?? string.Empty).Trim();
        rate.Description = (rate.Description ?? string.Empty).Trim();
        rate.EffectiveFrom = rate.EffectiveFrom.Date;

        if (rate.Code.Length == 0)
            return SaveResult<PremiumRate>.Fail("Enter a code for the premium.");

        if (rate.Multiplier < 0)
            return SaveResult<PremiumRate>.Fail("A multiplier cannot be negative.");

        // The multipliers are statutory minima. Paying more is a company's
        // choice; paying less is not, so it is refused rather than warned about.
        var floor = StatutoryFloor(rate.Code);
        if (floor is { } minimum && rate.Multiplier < minimum)
        {
            return SaveResult<PremiumRate>.Fail(
                $"{rate.Name} cannot be less than {minimum:0.00}× — that is the statutory minimum " +
                "under the Labor Code. A company may pay more than the floor, never less.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var existing = await connection.Table<PremiumRate>().ToListAsync().ConfigureAwait(false);

        var current = rate.Id == 0 ? null : existing.FirstOrDefault(r => r.Id == rate.Id);

        // C-02. A rate that has already priced a payslip is superseded, not
        // edited: the old row stays so a historical run can still be explained,
        // and the new one takes effect from its own date. Only a rate whose
        // effective date has not arrived yet is edited in place.
        var supersedes = current is not null &&
                         current.EffectiveFrom.Date <= DateTime.Today &&
                         current.Multiplier != rate.Multiplier;

        if (supersedes)
        {
            if (rate.EffectiveFrom.Date <= current!.EffectiveFrom.Date)
            {
                return SaveResult<PremiumRate>.Fail(
                    $"{current.Name} has been in force since {current.EffectiveFrom:dd MMM yyyy} and may have " +
                    "priced payslips already. Give the new multiplier a later effective date — the old row is " +
                    "kept so historical runs can still be explained.");
            }

            var replacement = new PremiumRate
            {
                Code = rate.Code,
                Name = rate.Name,
                Description = rate.Description,
                Multiplier = rate.Multiplier,
                DayType = current.DayType,
                IsRestDay = current.IsRestDay,
                IsOvertime = current.IsOvertime,
                IsAdditive = current.IsAdditive,
                IsUnworked = current.IsUnworked,
                EffectiveFrom = rate.EffectiveFrom,
                IsActive = true
            };

            await connection.InsertAsync(replacement).ConfigureAwait(false);

            await _audit.WriteAsync(
                AuditActions.PremiumRateChanged, nameof(PremiumRate), replacement.Id, true,
                $"{replacement.Code} superseded: {current.MultiplierDisplay} → {replacement.MultiplierDisplay}, " +
                $"effective {replacement.EffectiveFrom:dd MMM yyyy}. " +
                $"The row in force since {current.EffectiveFrom:dd MMM yyyy} was kept.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return SaveResult<PremiumRate>.Ok(replacement,
                $"{replacement.Name} is {replacement.MultiplierDisplay} from " +
                $"{replacement.EffectiveFrom:dd MMM yyyy}. Runs before that date keep the old rate.");
        }

        var isNew = rate.Id == 0;

        if (isNew)
        {
            await connection.InsertAsync(rate).ConfigureAwait(false);
        }
        else
        {
            rate.DayType = current!.DayType;
            rate.IsRestDay = current.IsRestDay;
            rate.IsOvertime = current.IsOvertime;
            rate.IsAdditive = current.IsAdditive;
            rate.IsUnworked = current.IsUnworked;
            rate.CreatedUtc = current.CreatedUtc;

            // Reaching here means the multiplier did not move, so this is a
            // rename or a note. The date a rate started applying is not part of
            // that, and moving it would shift the boundary a historical run was
            // priced either side of.
            if (current.EffectiveFrom.Date <= DateTime.Today)
                rate.EffectiveFrom = current.EffectiveFrom;

            await connection.UpdateAsync(rate).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            AuditActions.PremiumRateChanged, nameof(PremiumRate), rate.Id, true,
            $"{(isNew ? "Added" : "Updated")} premium {rate.Code} — {rate.Name} at " +
            $"{rate.MultiplierDisplay}, effective {rate.EffectiveFrom:dd MMM yyyy}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PremiumRate>.Ok(rate, $"{rate.Name} saved at {rate.MultiplierDisplay}.");
    }

    /// <summary>
    /// The statutory minimum for a premium code, or null where the Labor Code
    /// sets no floor.
    /// </summary>
    private static decimal? StatutoryFloor(string code) => code switch
    {
        PremiumCodes.OrdinaryOvertime => 1.25m,
        PremiumCodes.NightDifferential => 0.10m,
        PremiumCodes.RestDay => 1.30m,
        PremiumCodes.RestDayOvertime => 1.69m,
        PremiumCodes.SpecialNonWorking => 1.30m,
        PremiumCodes.SpecialNonWorkingOvertime => 1.69m,
        PremiumCodes.SpecialNonWorkingRestDay => 1.50m,
        PremiumCodes.SpecialNonWorkingRestDayOvertime => 1.95m,
        PremiumCodes.RegularHoliday => 2.00m,
        PremiumCodes.RegularHolidayOvertime => 2.60m,
        PremiumCodes.RegularHolidayRestDay => 2.60m,
        PremiumCodes.RegularHolidayRestDayOvertime => 3.38m,
        PremiumCodes.RegularHolidayUnworked => 1.00m,
        PremiumCodes.Ordinary => 1.00m,
        _ => null
    };

    // =====================================================================
    // FR-045 — company profile and payroll settings
    // =====================================================================

    public async Task<CompanyProfile> GetCompanyProfileAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var profile = await connection.Table<CompanyProfile>()
            .Where(p => p.Id == CompanyProfile.SingletonId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        return profile ?? new CompanyProfile();
    }

    public async Task<SaveResult<CompanyProfile>> SaveCompanyProfileAsync(
        CompanyProfile profile, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<CompanyProfile>(performedBy, "edit the company profile", nameof(CompanyProfile))
                .ConfigureAwait(false);

        profile.Id = CompanyProfile.SingletonId;
        profile.RegisteredName = (profile.RegisteredName ?? string.Empty).Trim();
        profile.TradeName = (profile.TradeName ?? string.Empty).Trim();
        profile.Address = (profile.Address ?? string.Empty).Trim();
        profile.RdoCode = (profile.RdoCode ?? string.Empty).Trim();
        profile.SssEmployerNumber = (profile.SssEmployerNumber ?? string.Empty).Trim();
        profile.PhilHealthEmployerNumber = (profile.PhilHealthEmployerNumber ?? string.Empty).Trim();
        profile.PagIbigEmployerNumber = (profile.PagIbigEmployerNumber ?? string.Empty).Trim();
        profile.ContactNumber = (profile.ContactNumber ?? string.Empty).Trim();
        profile.EmailAddress = (profile.EmailAddress ?? string.Empty).Trim();
        profile.LogoPath = (profile.LogoPath ?? string.Empty).Trim();
        profile.AuthorisedSignatory = (profile.AuthorisedSignatory ?? string.Empty).Trim();
        profile.SignatoryPosition = (profile.SignatoryPosition ?? string.Empty).Trim();

        if (profile.RegisteredName.Length == 0)
            return SaveResult<CompanyProfile>.Fail("Enter the registered name of the company.");

        // The employer TIN is printed on every payslip and every remittance, so
        // a malformed one is caught here rather than by the BIR.
        var tin = GovernmentId.Validate(GovernmentIdKind.Tin, profile.Tin);
        if (!tin.IsValid)
            return SaveResult<CompanyProfile>.Fail(tin.Message);

        profile.Tin = tin.Normalised;
        profile.UpdatedUtc = DateTime.UtcNow;

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        await connection.InsertOrReplaceAsync(profile).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.CompanyProfileUpdated, nameof(CompanyProfile), profile.Id, true,
            $"Company profile updated: {profile.RegisteredName}, TIN {profile.TinDisplay}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        var outstanding = profile.MissingForPayslip;

        return SaveResult<CompanyProfile>.Ok(profile,
            outstanding.Count == 0
                ? "Company profile saved."
                : $"Company profile saved. Still missing for a payslip: {string.Join(", ", outstanding)}.");
    }

    public async Task<PayrollSettings> GetSettingsAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var settings = await connection.Table<PayrollSettings>()
            .Where(s => s.Id == PayrollSettings.SingletonId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        return settings ?? new PayrollSettings();
    }

    public async Task<SaveResult<PayrollSettings>> SaveSettingsAsync(
        PayrollSettings settings, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<PayrollSettings>(performedBy, "change the payroll settings", nameof(PayrollSettings))
                .ConfigureAwait(false);

        if (settings.WorkingDaysFactor <= 0 || settings.WorkingDaysFactor > 400)
            return SaveResult<PayrollSettings>.Fail("The working-days factor must be between 1 and 400.");

        if (settings.StandardHoursPerDay <= 0 || settings.StandardHoursPerDay > 24)
            return SaveResult<PayrollSettings>.Fail("Standard hours per day must be between 1 and 24.");

        // Art. 83 caps the normal day at eight hours; more than that is overtime,
        // not a longer standard day, and setting it here would price overtime as
        // though it were ordinary time.
        if (settings.StandardHoursPerDay > 8m)
        {
            return SaveResult<PayrollSettings>.Fail(
                "Art. 83 sets the normal working day at eight hours. Hours beyond that are overtime, " +
                "which is priced from the premium matrix rather than by lengthening the standard day.");
        }

        if (settings.CutOffLeadDays < 0 || settings.CutOffLeadDays > 15)
            return SaveResult<PayrollSettings>.Fail("The cut-off lead must be between 0 and 15 days.");

        if (settings.PayDateLagDays < 0 || settings.PayDateLagDays > 15)
            return SaveResult<PayrollSettings>.Fail("The pay date lag must be between 0 and 15 days.");

        var previous = await GetSettingsAsync().ConfigureAwait(false);

        // The paper has its own screen (Settings) and its own save, below.
        settings.ReportPaper = previous.ReportPaper;
        settings.Id = PayrollSettings.SingletonId;
        settings.UpdatedUtc = DateTime.UtcNow;

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        await connection.InsertOrReplaceAsync(settings).ConfigureAwait(false);

        // The factor is spelled out because it reprices every hour in the
        // company: a run before and after this change is not comparable.
        var factorNote = previous.WorkingDaysFactor != settings.WorkingDaysFactor
            ? $" Working-days factor changed from {previous.WorkingDaysFactor:0.##} to " +
              $"{settings.WorkingDaysFactor:0.##} — this reprices the daily and hourly rate of every " +
              "monthly-paid employee."
            : string.Empty;

        await _audit.WriteAsync(
            AuditActions.PayrollSettingsUpdated, nameof(PayrollSettings), settings.Id, true,
            $"Payroll settings updated: {settings.FrequencyDisplay}, factor {settings.WorkingDaysFactor:0.##}, " +
            $"{settings.StandardHoursPerDay:0.##} h/day, contributions {settings.ContributionDisplay.ToLowerInvariant()}." +
            factorNote,
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayrollSettings>.Ok(settings,
            factorNote.Length > 0
                ? "Settings saved. The working-days factor changed, so existing draft runs should be recalculated."
                : "Settings saved.");
    }

    public async Task<SaveResult<PayrollSettings>> SaveReportPaperAsync(ReportPaper paper, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageSystemConfiguration))
            return await RefuseAsync<PayrollSettings>(performedBy, "change the report paper", nameof(PayrollSettings))
                .ConfigureAwait(false);

        if (!Enum.IsDefined(paper))
            return SaveResult<PayrollSettings>.Fail("Choose a paper size.");

        var settings = await GetSettingsAsync().ConfigureAwait(false);

        if (settings.ReportPaper == paper)
            return SaveResult<PayrollSettings>.Ok(settings, "No change to save.");

        var previousPaper = settings.ReportPaper;

        settings.ReportPaper = paper;
        settings.Id = PayrollSettings.SingletonId;
        settings.UpdatedUtc = DateTime.UtcNow;

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        await connection.InsertOrReplaceAsync(settings).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.PayrollSettingsUpdated, nameof(PayrollSettings), settings.Id, true,
            $"Report paper changed from {ReportPaperSizes.Display(previousPaper)} to {ReportPaperSizes.Display(paper)}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<PayrollSettings>.Ok(settings, $"Reports will now print on {ReportPaperSizes.Display(paper)}.");
    }

    // =====================================================================

    private async Task<SaveResult<T>> RefuseAsync<T>(User performedBy, string action, string entity)
        where T : class
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, entity, null, false,
            $"Role {performedBy.RoleDisplayName} is not permitted to {action}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<T>.Fail("You do not have permission to perform this action.");
    }
}
