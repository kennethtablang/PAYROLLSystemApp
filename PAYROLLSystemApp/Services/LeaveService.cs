using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>Outcome of filing or deciding a request, with any per-field messages.</summary>
public sealed record LeaveSaveResult(
    bool Succeeded,
    string Message,
    LeaveRequest? Request = null,
    IReadOnlyList<FieldError>? Errors = null)
{
    public IReadOnlyList<FieldError> FieldErrors => Errors ?? Array.Empty<FieldError>();

    public static LeaveSaveResult Ok(LeaveRequest request, string message) =>
        new(true, message, request);

    public static LeaveSaveResult Invalid(IReadOnlyList<FieldError> errors) =>
        new(false, "Correct the highlighted fields and try again.", null, errors);

    public static LeaveSaveResult Fail(string message) => new(false, message);
}

/// <summary>
/// FR-032. One leave type and this employee's standing in it, paired for display
/// so a screen never has to join the two itself.
/// </summary>
public sealed record LeaveEntitlement(LeaveType Type, LeaveBalance Balance)
{
    public int LeaveTypeId => Type.Id;

    public decimal Remaining => Balance.Remaining;

    public bool IsPaid => Type.IsPaid;
}

/// <summary>What a range would cost before anyone commits to it.</summary>
public sealed record LeavePreview(
    int WorkingDays,
    decimal Days,
    decimal Remaining,
    bool IsPaidType,
    bool WouldOverdraw,
    string? Conflict)
{
    /// <summary>
    /// FR-034. True when approving this would take the employee past their
    /// credits, which requires the explicit unpaid override.
    /// </summary>
    public bool NeedsOverride => IsPaidType && WouldOverdraw;
}

/// <summary>Which requests a queue should show.</summary>
public sealed record LeaveRequestQuery(
    int? EmployeeId = null,
    LeaveRequestStatus? Status = null,
    int? Year = null);

/// <summary>What granting or adjusting credits across a roster actually did.</summary>
public sealed record LeaveGrantResult(bool Succeeded, string Message, int Created, int Updated);

public interface ILeaveService
{
    // ------------------------------------------------------------- FR-030

    Task<IReadOnlyList<LeaveType>> GetTypesAsync(bool includeInactive = false);

    Task<SaveResult<LeaveType>> SaveTypeAsync(LeaveType type, User performedBy);

    Task<SaveResult<LeaveType>> SetTypeActiveAsync(int id, bool active, User performedBy);

    // ------------------------------------------------------------- FR-032

    /// <summary>
    /// One employee's credits across every leave type for a year, creating the
    /// rows that do not exist yet and bringing the accrual up to date.
    /// </summary>
    Task<IReadOnlyList<LeaveEntitlement>> GetEntitlementsAsync(Employee employee, int year);

    /// <summary>A manual correction, positive or negative, with a required reason.</summary>
    Task<SaveResult<LeaveBalance>> AdjustBalanceAsync(
        Employee employee, int leaveTypeId, int year, decimal delta, string reason, User performedBy);

    // ------------------------------------------------- FR-031, FR-033, FR-034

    Task<IReadOnlyList<LeaveRequest>> GetRequestsAsync(LeaveRequestQuery query);

    Task<LeavePreview> PreviewAsync(
        Employee employee, int leaveTypeId, DateTime from, DateTime to, bool halfDay, int? excludeRequestId = null);

    Task<LeaveSaveResult> FileAsync(LeaveRequest request, User performedBy);

    /// <summary>
    /// FR-031, FR-034. Approves or rejects. <paramref name="grantAsUnpaid"/> is
    /// the explicit override that lets leave beyond the available credits through.
    /// </summary>
    Task<LeaveSaveResult> DecideAsync(
        int id, bool approve, string remarks, bool grantAsUnpaid, User performedBy);

    Task<LeaveSaveResult> CancelAsync(int id, string reason, User performedBy);
}

/// <summary>
/// Section 2.4 of the requirements: leave types, credits and filings
/// (FR-030 – FR-034).
///
/// <para><b>Approval is the only thing that spends credits.</b> Filing reserves
/// nothing — a pending request that is never approved must not quietly hold days
/// hostage — and cancelling an approved request gives them back. That keeps
/// <see cref="LeaveBalance.UsedCredits"/> equal to the sum of approved paid days
/// at all times, which is the invariant the whole feature protects.</para>
///
/// <para><b>Accrual is computed when it is read, not by a scheduled job.</b> This
/// is a desktop application with no background service, so a monthly accrual has
/// nothing to run it. <see cref="GetEntitlementsAsync"/> therefore brings the
/// earned figure up to the current month each time it loads. It is pure
/// arithmetic over the type's annual credits and the employee's dates, so
/// recomputing it can only ever produce the same answer (NFR-009).</para>
///
/// <para><b>Carry-over is an adjustment, not an automatic rule.</b> Companies
/// differ on whether unused leave rolls over, expires, or converts to cash, so
/// rather than guess, last year's remainder is brought forward through
/// <see cref="AdjustBalanceAsync"/> with a reason attached.</para>
/// </summary>
public sealed class LeaveService : ILeaveService
{
    private readonly PayrollDatabase _database;
    private readonly IAttendanceService _attendance;
    private readonly IAuditService _audit;

    public LeaveService(
        PayrollDatabase database,
        IAttendanceService attendance,
        IAuditService audit)
    {
        _database = database;
        _attendance = attendance;
        _audit = audit;
    }

    // ------------------------------------------------------- FR-030 types

    public async Task<IReadOnlyList<LeaveType>> GetTypesAsync(bool includeInactive = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var query = connection.Table<LeaveType>();
        if (!includeInactive)
            query = query.Where(t => t.IsActive);

        var rows = await query.ToListAsync().ConfigureAwait(false);
        return rows.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<SaveResult<LeaveType>> SaveTypeAsync(LeaveType type, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageLeave))
            return await RefuseTypeAsync(performedBy, "maintain leave types").ConfigureAwait(false);

        type.Code = (type.Code ?? string.Empty).Trim().ToUpperInvariant();
        type.Name = (type.Name ?? string.Empty).Trim();
        type.Description = (type.Description ?? string.Empty).Trim();

        if (type.Code.Length == 0)
            return SaveResult<LeaveType>.Fail("Enter a leave type code.");

        if (type.Name.Length == 0)
            return SaveResult<LeaveType>.Fail("Enter a leave type name.");

        if (type.DefaultAnnualCredits < 0m)
            return SaveResult<LeaveType>.Fail("Annual credits cannot be negative.");

        // An unpaid type with credits is a contradiction: credits exist to be
        // spent on paid days, so the number would be tracked and never mean
        // anything.
        if (!type.IsPaid && type.DefaultAnnualCredits > 0m)
        {
            return SaveResult<LeaveType>.Fail(
                "An unpaid leave type does not draw on credits. Set the annual credits to zero, " +
                "or mark the type paid.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var code = type.Code;
        var clash = await connection.Table<LeaveType>()
            .Where(t => t.Code == code && t.Id != type.Id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (clash is not null)
            return SaveResult<LeaveType>.Fail($"Code {type.Code} is already used by {clash.Name}.");

        var isNew = type.Id == 0;

        if (isNew)
        {
            await connection.InsertAsync(type).ConfigureAwait(false);
        }
        else
        {
            var existing = await connection.Table<LeaveType>()
                .Where(t => t.Id == type.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (existing is null)
                return SaveResult<LeaveType>.Fail("That leave type no longer exists.");

            type.IsActive = existing.IsActive;
            type.CreatedUtc = existing.CreatedUtc;

            await connection.UpdateAsync(type).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            isNew ? AuditActions.LeaveTypeCreated : AuditActions.LeaveTypeUpdated,
            nameof(LeaveType), type.Id, true,
            $"{(isNew ? "Created" : "Updated")} leave type {type.Code} — {type.Name}: " +
            $"{type.CreditsDisplay}, {type.PayDisplay.ToLowerInvariant()}, {type.AppliesToDisplay.ToLowerInvariant()}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<LeaveType>.Ok(type,
            isNew ? $"{type.Name} created." : $"{type.Name} updated.");
    }

    public async Task<SaveResult<LeaveType>> SetTypeActiveAsync(int id, bool active, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageLeave))
            return await RefuseTypeAsync(performedBy, "retire or restore a leave type").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var type = await connection.Table<LeaveType>()
            .Where(t => t.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (type is null)
            return SaveResult<LeaveType>.Fail("That leave type no longer exists.");

        if (!active)
        {
            // A pending request against a retired type could never be decided
            // sensibly — the entitlement behind it has been withdrawn.
            var typeId = type.Id;
            var pending = await connection.Table<LeaveRequest>()
                .Where(r => r.LeaveTypeId == typeId && r.Status == LeaveRequestStatus.Pending)
                .CountAsync()
                .ConfigureAwait(false);

            if (pending > 0)
            {
                return SaveResult<LeaveType>.Fail(
                    $"{type.Name} still has {pending} request(s) awaiting a decision. " +
                    "Decide them first.");
            }
        }

        type.IsActive = active;
        await connection.UpdateAsync(type).ConfigureAwait(false);

        await _audit.WriteAsync(
            active ? AuditActions.LeaveTypeReactivated : AuditActions.LeaveTypeDeactivated,
            nameof(LeaveType), type.Id, true,
            $"{(active ? "Restored" : "Retired")} leave type {type.Code} — {type.Name}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<LeaveType>.Ok(type,
            active
                ? $"{type.Name} restored."
                : $"{type.Name} retired. Leave already taken against it is unchanged.");
    }

    // ---------------------------------------------------- FR-032 balances

    public async Task<IReadOnlyList<LeaveEntitlement>> GetEntitlementsAsync(Employee employee, int year)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var employeeId = employee.Id;

        var balances = await connection.Table<LeaveBalance>()
            .Where(b => b.EmployeeId == employeeId && b.Year == year)
            .ToListAsync()
            .ConfigureAwait(false);

        var types = await GetTypesAsync(includeInactive: true).ConfigureAwait(false);

        // A retired type with a balance still shows, so last year's history does
        // not vanish the moment the policy changes. One with neither a balance
        // nor an active flag is simply gone.
        var relevant = types
            .Where(t => t.IsActive || balances.Any(b => b.LeaveTypeId == t.Id))
            .Where(t => LeaveEnumNames.Admits(t.AppliesTo, employee.Gender)
                        || balances.Any(b => b.LeaveTypeId == t.Id))
            .ToList();

        // The accrual figure is only ever as fresh as the last time it was read,
        // because nothing runs on a schedule here. See the class remarks.
        var asOf = year == DateTime.Today.Year
            ? DateTime.Today
            : new DateTime(year, 12, 31);

        var entitlements = new List<LeaveEntitlement>(relevant.Count);

        foreach (var type in relevant)
        {
            var balance = balances.FirstOrDefault(b => b.LeaveTypeId == type.Id);

            var earned = type.CreditsEarnedBy(asOf, employee.HireDate, employee.SeparationDate);

            if (balance is null)
            {
                balance = new LeaveBalance
                {
                    EmployeeId = employee.Id,
                    LeaveTypeId = type.Id,
                    Year = year,
                    EarnedCredits = earned,
                    UpdatedUtc = DateTime.UtcNow
                };

                await connection.InsertAsync(balance).ConfigureAwait(false);
            }
            else if (balance.EarnedCredits != earned)
            {
                balance.EarnedCredits = earned;
                balance.UpdatedUtc = DateTime.UtcNow;

                await connection.UpdateAsync(balance).ConfigureAwait(false);
            }

            entitlements.Add(new LeaveEntitlement(type, balance));
        }

        return entitlements
            .OrderBy(e => e.Type.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<SaveResult<LeaveBalance>> AdjustBalanceAsync(
        Employee employee, int leaveTypeId, int year, decimal delta, string reason, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageLeave))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(LeaveBalance), null, false,
                $"Role {performedBy.RoleDisplayName} is not permitted to adjust leave credits.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return SaveResult<LeaveBalance>.Fail("You do not have permission to perform this action.");
        }

        reason = (reason ?? string.Empty).Trim();

        // An adjustment with no reason is indistinguishable from an error, and
        // this is the one place credits appear from nowhere.
        if (reason.Length == 0)
            return SaveResult<LeaveBalance>.Fail("Give the reason for the adjustment.");

        if (delta == 0m)
            return SaveResult<LeaveBalance>.Fail("Enter the number of days to add or take away.");

        // Loads through the entitlement path so the row exists and its accrual is
        // current before anything is added to it.
        var entitlements = await GetEntitlementsAsync(employee, year).ConfigureAwait(false);

        var entitlement = entitlements.FirstOrDefault(e => e.LeaveTypeId == leaveTypeId);

        if (entitlement is null)
            return SaveResult<LeaveBalance>.Fail("That leave type does not apply to this employee.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var balance = entitlement.Balance;
        var before = balance.Remaining;

        balance.AdjustmentCredits += delta;
        balance.UpdatedUtc = DateTime.UtcNow;

        await connection.UpdateAsync(balance).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.LeaveBalanceAdjusted,
            nameof(LeaveBalance), balance.Id, true,
            $"Adjusted {entitlement.Type.Name} for {employee.EmployeeNumber} {employee.DisplayName} " +
            $"({year}) by {delta:+0.##;-0.##} day(s): {before:0.##} remaining before, " +
            $"{balance.Remaining:0.##} after. Reason: {reason}",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<LeaveBalance>.Ok(balance,
            $"{entitlement.Type.Name} adjusted by {delta:+0.##;-0.##} day(s); " +
            $"{balance.Remaining:0.##} now remaining.");
    }

    // ---------------------------------------------------- FR-031 requests

    public async Task<IReadOnlyList<LeaveRequest>> GetRequestsAsync(LeaveRequestQuery query)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<LeaveRequest>().ToListAsync().ConfigureAwait(false);

        IEnumerable<LeaveRequest> filtered = rows;

        if (query.EmployeeId is { } employeeId)
            filtered = filtered.Where(r => r.EmployeeId == employeeId);

        if (query.Status is { } status)
            filtered = filtered.Where(r => r.Status == status);

        if (query.Year is { } year)
            filtered = filtered.Where(r => r.StartDate.Year == year || r.EndDate.Year == year);

        // Pending first — the queue exists to be worked through — then the most
        // recent dates.
        return filtered
            .OrderBy(r => r.Status == LeaveRequestStatus.Pending ? 0 : 1)
            .ThenByDescending(r => r.StartDate)
            .ThenByDescending(r => r.Id)
            .ToList();
    }

    public async Task<LeavePreview> PreviewAsync(
        Employee employee, int leaveTypeId, DateTime from, DateTime to,
        bool halfDay, int? excludeRequestId = null)
    {
        var start = from.Date;
        var end = to.Date;

        if (end < start)
            return new LeavePreview(0, 0m, 0m, false, false, "The end date falls before the start date.");

        var workingDays = await _attendance.GetWorkingDaysAsync(employee, start, end).ConfigureAwait(false);

        var days = halfDay && start == end ? 0.5m : workingDays.Count;

        var entitlements = await GetEntitlementsAsync(employee, start.Year).ConfigureAwait(false);
        var entitlement = entitlements.FirstOrDefault(e => e.LeaveTypeId == leaveTypeId);

        var remaining = entitlement?.Remaining ?? 0m;
        var isPaid = entitlement?.IsPaid ?? false;

        var conflict = await FindConflictAsync(employee.Id, start, end, excludeRequestId).ConfigureAwait(false);

        return new LeavePreview(
            WorkingDays: workingDays.Count,
            Days: days,
            Remaining: remaining,
            IsPaidType: isPaid,
            WouldOverdraw: days > remaining,
            Conflict: conflict);
    }

    public async Task<LeaveSaveResult> FileAsync(LeaveRequest request, User performedBy)
    {
        // Filing on somebody else's behalf is an HR task; filing for yourself is
        // the self-service case. Either permission admits, and the ownership rule
        // below decides which one applies.
        var isOwnRequest = performedBy.EmployeeId is { } linked && linked == request.EmployeeId;

        if (!performedBy.Can(Permission.ManageLeave) &&
            !(isOwnRequest && performedBy.Can(Permission.FileLeaveRequest)))
        {
            return await RefuseRequestAsync(performedBy, "file a leave request").ConfigureAwait(false);
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var employee = await connection.Table<Employee>()
            .Where(e => e.Id == request.EmployeeId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (employee is null)
            return LeaveSaveResult.Fail("That employee no longer exists.");

        var type = await connection.Table<LeaveType>()
            .Where(t => t.Id == request.LeaveTypeId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (type is null)
            return LeaveSaveResult.Fail("That leave type no longer exists.");

        request.StartDate = request.StartDate.Date;
        request.EndDate = request.EndDate.Date;
        request.Reason = (request.Reason ?? string.Empty).Trim();

        var errors = await ValidateAsync(request, employee, type).ConfigureAwait(false);
        if (errors.Count > 0)
            return LeaveSaveResult.Invalid(errors);

        var workingDays = await _attendance
            .GetWorkingDaysAsync(employee, request.StartDate, request.EndDate)
            .ConfigureAwait(false);

        request.Days = request.IsHalfDay ? 0.5m : workingDays.Count;
        request.LeaveTypeName = type.Name;
        request.Status = LeaveRequestStatus.Pending;
        request.IsPaid = type.IsPaid;
        request.WasOverdrawn = false;
        request.FiledBy = performedBy.Username;
        request.FiledUtc = DateTime.UtcNow;
        request.DecidedBy = string.Empty;
        request.DecidedUtc = null;
        request.DecisionRemarks = string.Empty;
        request.Id = 0;

        await connection.InsertAsync(request).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.LeaveRequestFiled,
            nameof(LeaveRequest), request.Id, true,
            $"Filed {type.Name} for {employee.EmployeeNumber} {employee.DisplayName}: " +
            $"{request.PeriodDisplay}, {request.Days:0.##} working day(s). Reason: {request.Reason}",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return LeaveSaveResult.Ok(request,
            $"{type.Name} filed for {employee.DisplayName}: {request.Days:0.##} day(s) awaiting a decision.");
    }

    public async Task<LeaveSaveResult> DecideAsync(
        int id, bool approve, string remarks, bool grantAsUnpaid, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageLeave))
            return await RefuseRequestAsync(performedBy, "decide a leave request").ConfigureAwait(false);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var request = await connection.Table<LeaveRequest>()
            .Where(r => r.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (request is null)
            return LeaveSaveResult.Fail("That request no longer exists.");

        if (!request.IsPending)
        {
            return LeaveSaveResult.Fail(
                $"That request was already {request.StatusDisplay.ToLowerInvariant()} and cannot be decided again.");
        }

        // Nobody signs off their own leave. The filer and the approver may be the
        // same HR officer acting for someone else, but not for themselves.
        if (performedBy.EmployeeId is { } linked && linked == request.EmployeeId)
        {
            return LeaveSaveResult.Fail(
                "You cannot decide your own leave request. Ask another approver to review it.");
        }

        var employee = await connection.Table<Employee>()
            .Where(e => e.Id == request.EmployeeId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (employee is null)
            return LeaveSaveResult.Fail("That employee no longer exists.");

        remarks = (remarks ?? string.Empty).Trim();

        if (!approve)
        {
            // A refusal without a reason is the one the employee will come back
            // to ask about.
            if (remarks.Length == 0)
            {
                return LeaveSaveResult.Invalid(
                    [new FieldError(nameof(LeaveRequest.DecisionRemarks), "Give the reason for refusing.")]);
            }

            request.Status = LeaveRequestStatus.Rejected;
            request.DecidedBy = performedBy.Username;
            request.DecidedUtc = DateTime.UtcNow;
            request.DecisionRemarks = remarks;

            await connection.UpdateAsync(request).ConfigureAwait(false);

            await _audit.WriteAsync(
                AuditActions.LeaveRequestRejected,
                nameof(LeaveRequest), request.Id, true,
                $"Rejected {request.LeaveTypeName} for {employee.EmployeeNumber} {employee.DisplayName} " +
                $"({request.PeriodDisplay}). Reason: {remarks}",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return LeaveSaveResult.Ok(request, $"Request refused for {employee.DisplayName}.");
        }

        var entitlements = await GetEntitlementsAsync(employee, request.StartDate.Year).ConfigureAwait(false);
        var entitlement = entitlements.FirstOrDefault(e => e.LeaveTypeId == request.LeaveTypeId);

        if (entitlement is null)
            return LeaveSaveResult.Fail("That leave type no longer applies to this employee.");

        var overdrawn = entitlement.IsPaid && request.Days > entitlement.Remaining;

        // FR-034: the refusal is the default, and the override has to be asked
        // for by name. Approving silently as unpaid would be a pay cut nobody
        // agreed to.
        if (overdrawn && !grantAsUnpaid)
        {
            return LeaveSaveResult.Fail(
                $"{employee.DisplayName} has {entitlement.Remaining:0.##} day(s) of " +
                $"{entitlement.Type.Name} left and this request is for {request.Days:0.##}. " +
                "Approve it as unpaid leave instead, or adjust the balance first.");
        }

        request.Status = LeaveRequestStatus.Approved;
        request.IsPaid = entitlement.IsPaid && !overdrawn;
        request.WasOverdrawn = overdrawn;
        request.DecidedBy = performedBy.Username;
        request.DecidedUtc = DateTime.UtcNow;
        request.DecisionRemarks = remarks;

        await connection.UpdateAsync(request).ConfigureAwait(false);

        // Credits are spent here and nowhere else. An unpaid grant draws on
        // nothing, which is the whole point of the override.
        if (request.IsPaid)
        {
            entitlement.Balance.UsedCredits += request.Days;
            entitlement.Balance.UpdatedUtc = DateTime.UtcNow;

            await connection.UpdateAsync(entitlement.Balance).ConfigureAwait(false);
        }

        // FR-033: this is what carries the decision through to payroll. Without
        // it the days would compute as plain absences.
        var marked = await _attendance.MarkLeaveAsync(
            employee, request.StartDate, request.EndDate, onLeave: true,
            $"{request.LeaveTypeName} approved by {performedBy.Username}.", performedBy)
            .ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditActions.LeaveRequestApproved,
            nameof(LeaveRequest), request.Id, true,
            $"Approved {request.LeaveTypeName} for {employee.EmployeeNumber} {employee.DisplayName} " +
            $"({request.PeriodDisplay}), {request.Days:0.##} day(s), {request.PayDisplay.ToLowerInvariant()}" +
            (overdrawn ? ", granted beyond the available credits as unpaid" : string.Empty) +
            $". {marked.Marked} attendance day(s) marked.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        var message = $"{request.LeaveTypeName} approved for {employee.DisplayName} " +
                      $"({request.Days:0.##} day(s), {request.PayDisplay.ToLowerInvariant()}).";

        if (marked.Message.Length > 0)
            message += " " + marked.Message;

        return LeaveSaveResult.Ok(request, message);
    }

    public async Task<LeaveSaveResult> CancelAsync(int id, string reason, User performedBy)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var request = await connection.Table<LeaveRequest>()
            .Where(r => r.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (request is null)
            return LeaveSaveResult.Fail("That request no longer exists.");

        var isOwnRequest = performedBy.EmployeeId is { } linked && linked == request.EmployeeId;

        // Withdrawing your own pending request needs no approver; anything else
        // is an administrative act.
        if (!performedBy.Can(Permission.ManageLeave) &&
            !(isOwnRequest && request.IsPending && performedBy.Can(Permission.FileLeaveRequest)))
        {
            return await RefuseRequestAsync(performedBy, "cancel a leave request").ConfigureAwait(false);
        }

        if (!request.IsCancellable)
        {
            return LeaveSaveResult.Fail(
                $"That request was already {request.StatusDisplay.ToLowerInvariant()}.");
        }

        reason = (reason ?? string.Empty).Trim();

        if (reason.Length == 0)
        {
            return LeaveSaveResult.Invalid(
                [new FieldError(nameof(LeaveRequest.DecisionRemarks), "Give the reason for cancelling.")]);
        }

        var employee = await connection.Table<Employee>()
            .Where(e => e.Id == request.EmployeeId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (employee is null)
            return LeaveSaveResult.Fail("That employee no longer exists.");

        var wasApproved = request.IsApproved;
        var refunded = 0m;

        request.Status = LeaveRequestStatus.Cancelled;
        request.DecidedBy = performedBy.Username;
        request.DecidedUtc = DateTime.UtcNow;
        request.DecisionRemarks = reason;

        await connection.UpdateAsync(request).ConfigureAwait(false);

        if (wasApproved)
        {
            // Give the credits back. Only a paid approval ever took any, so an
            // unpaid or overdrawn grant refunds nothing — which is correct, and
            // is why the paid flag is stored on the request rather than reread
            // from the type.
            if (request.IsPaid)
            {
                var entitlements = await GetEntitlementsAsync(employee, request.StartDate.Year)
                    .ConfigureAwait(false);

                var entitlement = entitlements.FirstOrDefault(e => e.LeaveTypeId == request.LeaveTypeId);

                if (entitlement is not null)
                {
                    entitlement.Balance.UsedCredits =
                        Math.Max(0m, entitlement.Balance.UsedCredits - request.Days);
                    entitlement.Balance.UpdatedUtc = DateTime.UtcNow;

                    await connection.UpdateAsync(entitlement.Balance).ConfigureAwait(false);
                    refunded = request.Days;
                }
            }

            await _attendance.MarkLeaveAsync(
                employee, request.StartDate, request.EndDate, onLeave: false,
                $"{request.LeaveTypeName} cancelled by {performedBy.Username}: {reason}", performedBy)
                .ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            AuditActions.LeaveRequestCancelled,
            nameof(LeaveRequest), request.Id, true,
            $"Cancelled {request.LeaveTypeName} for {employee.EmployeeNumber} {employee.DisplayName} " +
            $"({request.PeriodDisplay}); {refunded:0.##} day(s) returned to the balance. Reason: {reason}",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return LeaveSaveResult.Ok(request,
            refunded > 0m
                ? $"Request cancelled; {refunded:0.##} day(s) returned to {employee.DisplayName}."
                : $"Request cancelled for {employee.DisplayName}.");
    }

    // ----------------------------------------------------------- internals

    /// <summary>
    /// NFR-023. Everything that would make a filing meaningless, reported per
    /// field so the dialog can put each message beside its input.
    /// </summary>
    private async Task<IReadOnlyList<FieldError>> ValidateAsync(
        LeaveRequest request, Employee employee, LeaveType type)
    {
        var errors = new List<FieldError>();

        if (!type.IsActive)
        {
            errors.Add(new FieldError(nameof(LeaveRequest.LeaveTypeId),
                $"{type.Name} has been retired and cannot be filed against."));
        }

        if (!LeaveEnumNames.Admits(type.AppliesTo, employee.Gender))
        {
            errors.Add(new FieldError(nameof(LeaveRequest.LeaveTypeId),
                $"{type.Name} is available to {type.AppliesToDisplay.ToLowerInvariant()}."));
        }

        if (request.EndDate < request.StartDate)
        {
            errors.Add(new FieldError(nameof(LeaveRequest.EndDate),
                "The end date falls before the start date."));
        }

        // "Which half?" has no answer across a range, and the day count would be
        // ambiguous the moment payroll read it.
        if (request.IsHalfDay && !request.IsSingleDay)
        {
            errors.Add(new FieldError(nameof(LeaveRequest.IsHalfDay),
                "A half day applies to a single date. Clear it, or make the range one day."));
        }

        if (!employee.IsPayableOver(request.StartDate, request.EndDate))
        {
            errors.Add(new FieldError(nameof(LeaveRequest.StartDate),
                employee.SeparationDate is { } separated && request.StartDate > separated
                    ? $"{employee.DisplayName} was separated on {separated:dd MMM yyyy}."
                    : $"{employee.DisplayName} was not employed over those dates."));
        }

        if (request.Reason.Length == 0)
            errors.Add(new FieldError(nameof(LeaveRequest.Reason), "Give a reason for the leave."));

        if (errors.Count > 0)
            return errors;

        var workingDays = await _attendance
            .GetWorkingDaysAsync(employee, request.StartDate, request.EndDate)
            .ConfigureAwait(false);

        // A range that is all rest days and holidays costs nothing and grants
        // nothing, so filing it would only put a meaningless row in the queue.
        if (workingDays.Count == 0)
        {
            errors.Add(new FieldError(nameof(LeaveRequest.StartDate),
                "Every day in that range is a rest day or a holiday, so no leave would be used."));
        }

        var conflict = await FindConflictAsync(request.EmployeeId, request.StartDate, request.EndDate, null)
            .ConfigureAwait(false);

        if (conflict is not null)
            errors.Add(new FieldError(nameof(LeaveRequest.StartDate), conflict));

        return errors;
    }

    /// <summary>
    /// An existing filing that overlaps the same dates. Two live requests over
    /// one day would each spend credits for it.
    /// </summary>
    private async Task<string?> FindConflictAsync(
        int employeeId, DateTime start, DateTime end, int? excludeRequestId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var existing = await connection.Table<LeaveRequest>()
            .Where(r => r.EmployeeId == employeeId)
            .ToListAsync()
            .ConfigureAwait(false);

        var clash = existing.FirstOrDefault(r =>
            r.Id != excludeRequestId &&
            r.Status is LeaveRequestStatus.Pending or LeaveRequestStatus.Approved &&
            r.StartDate.Date <= end.Date &&
            r.EndDate.Date >= start.Date);

        return clash is null
            ? null
            : $"This overlaps a {clash.StatusDisplay.ToLowerInvariant()} " +
              $"{clash.LeaveTypeName} request for {clash.PeriodDisplay}.";
    }

    private async Task<SaveResult<LeaveType>> RefuseTypeAsync(User performedBy, string action)
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, nameof(LeaveType), null, false,
            $"Role {performedBy.RoleDisplayName} is not permitted to {action}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<LeaveType>.Fail("You do not have permission to perform this action.");
    }

    private async Task<LeaveSaveResult> RefuseRequestAsync(User performedBy, string action)
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, nameof(LeaveRequest), null, false,
            $"Role {performedBy.RoleDisplayName} is not permitted to {action}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return LeaveSaveResult.Fail("You do not have permission to perform this action.");
    }
}
