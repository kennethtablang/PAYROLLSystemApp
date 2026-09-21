using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// Outcome of a masterfile operation (FR-010, FR-015). Carries the saved record
/// so a caller can use the generated key without a second read.
/// </summary>
public sealed record SaveResult<T>(bool Succeeded, string Message, T? Value = null)
    where T : class
{
    public static SaveResult<T> Ok(T value, string message) => new(true, message, value);

    public static SaveResult<T> Fail(string message) => new(false, message);
}

public interface IOrganizationService
{
    Task<IReadOnlyList<Department>> GetDepartmentsAsync(bool includeInactive = false);

    Task<IReadOnlyList<Position>> GetPositionsAsync(bool includeInactive = false);

    Task<IReadOnlyList<WorkSchedule>> GetWorkSchedulesAsync(bool includeInactive = false);

    Task<SaveResult<Department>> SaveDepartmentAsync(Department department, User performedBy);

    Task<SaveResult<Position>> SavePositionAsync(Position position, User performedBy);

    Task<SaveResult<WorkSchedule>> SaveWorkScheduleAsync(WorkSchedule schedule, User performedBy);

    /// <summary>How many employees are assigned to a department, active ones only.</summary>
    Task<int> CountEmployeesInDepartmentAsync(int departmentId);

    /// <summary>How many employees hold a position, active ones only.</summary>
    Task<int> CountEmployeesInPositionAsync(int positionId);

    /// <summary>How many employees are on a work schedule, active ones only.</summary>
    Task<int> CountEmployeesOnScheduleAsync(int scheduleId);

    /// <summary>Assigned headcounts for every department and position in one read.</summary>
    Task<OrganizationHeadcounts> GetHeadcountsAsync();

    Task<SaveResult<Department>> SetDepartmentActiveAsync(int id, bool active, User performedBy);

    Task<SaveResult<Position>> SetPositionActiveAsync(int id, bool active, User performedBy);

    Task<SaveResult<WorkSchedule>> SetWorkScheduleActiveAsync(int id, bool active, User performedBy);
}

/// <summary>
/// How many active employees each department, position and work schedule
/// currently holds.
///
/// All three maps come from a single pass over the employee table: the
/// management screen needs a count against every row, and asking per row would
/// be one query per department plus one per position plus one per schedule
/// every time the screen opens.
/// </summary>
public sealed record OrganizationHeadcounts(
    IReadOnlyDictionary<int, int> ByDepartment,
    IReadOnlyDictionary<int, int> ByPosition,
    IReadOnlyDictionary<int, int> BySchedule)
{
    public int Department(int? id) =>
        id is { } key && ByDepartment.TryGetValue(key, out var count) ? count : 0;

    public int Position(int? id) =>
        id is { } key && ByPosition.TryGetValue(key, out var count) ? count : 0;

    public int Schedule(int? id) =>
        id is { } key && BySchedule.TryGetValue(key, out var count) ? count : 0;
}

/// <summary>
/// The reference data behind FR-012: departments, positions and work schedules.
///
/// <para>Nothing here deletes. A department that is no longer used is
/// deactivated, because it still names the unit on every payslip already
/// printed against it — the same rule the employee record itself follows
/// (FR-017).</para>
///
/// <para>Every write is checked against the caller's permission and recorded in
/// the audit log (FR-091). The view models hide what a role cannot do; this is
/// where it is actually refused.</para>
/// </summary>
public sealed class OrganizationService : IOrganizationService
{
    private readonly PayrollDatabase _database;
    private readonly IAuditService _audit;

    public OrganizationService(PayrollDatabase database, IAuditService audit)
    {
        _database = database;
        _audit = audit;
    }

    public async Task<IReadOnlyList<Department>> GetDepartmentsAsync(bool includeInactive = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var query = connection.Table<Department>();
        if (!includeInactive)
            query = query.Where(d => d.IsActive);

        var rows = await query.ToListAsync().ConfigureAwait(false);
        return rows.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<Position>> GetPositionsAsync(bool includeInactive = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var query = connection.Table<Position>();
        if (!includeInactive)
            query = query.Where(p => p.IsActive);

        var rows = await query.ToListAsync().ConfigureAwait(false);
        return rows.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<WorkSchedule>> GetWorkSchedulesAsync(bool includeInactive = false)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var query = connection.Table<WorkSchedule>();
        if (!includeInactive)
            query = query.Where(s => s.IsActive);

        var rows = await query.ToListAsync().ConfigureAwait(false);
        return rows.OrderBy(s => s.StartMinutes).ThenBy(s => s.Name).ToList();
    }

    public async Task<SaveResult<Department>> SaveDepartmentAsync(Department department, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
            return await RefuseAsync<Department>(performedBy, "maintain departments", nameof(Department));

        department.Code = (department.Code ?? string.Empty).Trim().ToUpperInvariant();
        department.Name = (department.Name ?? string.Empty).Trim();

        if (department.Code.Length == 0)
            return SaveResult<Department>.Fail("Enter a department code.");

        if (department.Name.Length == 0)
            return SaveResult<Department>.Fail("Enter a department name.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var code = department.Code;
        var clash = await connection.Table<Department>()
            .Where(d => d.Code == code && d.Id != department.Id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (clash is not null)
            return SaveResult<Department>.Fail($"Code {department.Code} is already used by {clash.Name}.");

        var isNew = department.Id == 0;

        if (isNew)
        {
            await connection.InsertAsync(department).ConfigureAwait(false);
        }
        else
        {
            // Retiring and restoring is its own guarded operation. An edit must
            // carry the current flag forward, or renaming a retired department
            // would quietly bring it back into every picker.
            var existing = await connection.Table<Department>()
                .Where(d => d.Id == department.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (existing is null)
                return SaveResult<Department>.Fail("That department no longer exists.");

            department.IsActive = existing.IsActive;
            department.CreatedUtc = existing.CreatedUtc;

            await connection.UpdateAsync(department).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            isNew ? AuditActions.DepartmentCreated : AuditActions.DepartmentUpdated,
            nameof(Department), department.Id, true,
            $"{(isNew ? "Created" : "Updated")} department {department.Code} — {department.Name}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<Department>.Ok(department,
            isNew ? $"Department {department.Code} created." : $"Department {department.Code} updated.");
    }

    public async Task<SaveResult<Position>> SavePositionAsync(Position position, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
            return await RefuseAsync<Position>(performedBy, "maintain positions", nameof(Position));

        position.Code = (position.Code ?? string.Empty).Trim().ToUpperInvariant();
        position.Title = (position.Title ?? string.Empty).Trim();

        if (position.Code.Length == 0)
            return SaveResult<Position>.Fail("Enter a position code.");

        if (position.Title.Length == 0)
            return SaveResult<Position>.Fail("Enter a position title.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var code = position.Code;
        var clash = await connection.Table<Position>()
            .Where(p => p.Code == code && p.Id != position.Id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (clash is not null)
            return SaveResult<Position>.Fail($"Code {position.Code} is already used by {clash.Title}.");

        var isNew = position.Id == 0;

        if (isNew)
        {
            await connection.InsertAsync(position).ConfigureAwait(false);
        }
        else
        {
            var existing = await connection.Table<Position>()
                .Where(p => p.Id == position.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (existing is null)
                return SaveResult<Position>.Fail("That position no longer exists.");

            position.IsActive = existing.IsActive;
            position.CreatedUtc = existing.CreatedUtc;

            await connection.UpdateAsync(position).ConfigureAwait(false);
        }

        // The managerial flag is called out in the audit detail because it
        // silently removes overtime and premium pay from everyone holding the
        // position, which is not obvious from a title change alone.
        await _audit.WriteAsync(
            isNew ? AuditActions.PositionCreated : AuditActions.PositionUpdated,
            nameof(Position), position.Id, true,
            $"{(isNew ? "Created" : "Updated")} position {position.Code} — {position.Title}; " +
            $"managerial (Art. 82): {(position.IsManagerial ? "yes" : "no")}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<Position>.Ok(position,
            isNew ? $"Position {position.Title} created." : $"Position {position.Title} updated.");
    }

    public async Task<SaveResult<WorkSchedule>> SaveWorkScheduleAsync(WorkSchedule schedule, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
            return await RefuseAsync<WorkSchedule>(performedBy, "maintain work schedules", nameof(WorkSchedule));

        schedule.Name = (schedule.Name ?? string.Empty).Trim();

        if (schedule.Name.Length == 0)
            return SaveResult<WorkSchedule>.Fail("Enter a schedule name.");

        if (schedule.WorkDays == 0)
            return SaveResult<WorkSchedule>.Fail("Select at least one working day.");

        // A break longer than the shift would produce negative paid hours, and
        // every hours figure computed from it afterwards would be wrong.
        if (schedule.StandardHours <= 0)
            return SaveResult<WorkSchedule>.Fail(
                "The break is as long as the shift, so the schedule pays no hours. Shorten it.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var name = schedule.Name;
        var clash = await connection.Table<WorkSchedule>()
            .Where(s => s.Name == name && s.Id != schedule.Id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (clash is not null)
            return SaveResult<WorkSchedule>.Fail($"A schedule named {schedule.Name} already exists.");

        var isNew = schedule.Id == 0;

        if (isNew)
        {
            await connection.InsertAsync(schedule).ConfigureAwait(false);
        }
        else
        {
            var existing = await connection.Table<WorkSchedule>()
                .Where(x => x.Id == schedule.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (existing is null)
                return SaveResult<WorkSchedule>.Fail("That schedule no longer exists.");

            schedule.IsActive = existing.IsActive;
            schedule.CreatedUtc = existing.CreatedUtc;

            await connection.UpdateAsync(schedule).ConfigureAwait(false);
        }

        await _audit.WriteAsync(
            isNew ? AuditActions.WorkScheduleCreated : AuditActions.WorkScheduleUpdated,
            nameof(WorkSchedule), schedule.Id, true,
            $"{(isNew ? "Created" : "Updated")} schedule {schedule.Name}: {schedule.TimeDisplay}, " +
            $"{schedule.DaysDisplay}, {schedule.StandardHours:0.##} paid hours, " +
            $"{schedule.GraceMinutes} min grace.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<WorkSchedule>.Ok(schedule,
            isNew ? $"Schedule {schedule.Name} created." : $"Schedule {schedule.Name} updated.");
    }

    /// <summary>
    /// Retires or restores a work schedule, under the same rule as a department
    /// or a position: nobody may still be on it.
    ///
    /// <para>The stakes are higher here than for a name. A schedule decides what
    /// counts as late, as undertime and as overtime, so an employee left on a
    /// retired one would still be measured against it while it appeared nowhere
    /// on screen.</para>
    /// </summary>
    public async Task<SaveResult<WorkSchedule>> SetWorkScheduleActiveAsync(int id, bool active, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
            return await RefuseAsync<WorkSchedule>(performedBy, "retire or restore a work schedule", nameof(WorkSchedule));

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var schedule = await connection.Table<WorkSchedule>()
            .Where(s => s.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (schedule is null)
            return SaveResult<WorkSchedule>.Fail("That schedule no longer exists.");

        if (!active)
        {
            var assigned = await CountEmployeesOnScheduleAsync(id).ConfigureAwait(false);

            if (assigned > 0)
            {
                return SaveResult<WorkSchedule>.Fail(
                    $"{schedule.Name} is still assigned to {assigned} active employee(s). " +
                    "Move them to another schedule first.");
            }
        }

        schedule.IsActive = active;
        await connection.UpdateAsync(schedule).ConfigureAwait(false);

        await _audit.WriteAsync(
            active ? AuditActions.WorkScheduleReactivated : AuditActions.WorkScheduleDeactivated,
            nameof(WorkSchedule), schedule.Id, true,
            $"{(active ? "Restored" : "Retired")} schedule {schedule.Name} ({schedule.TimeDisplay}, {schedule.DaysDisplay}).",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<WorkSchedule>.Ok(schedule,
            active ? $"{schedule.Name} restored." : $"{schedule.Name} retired.");
    }

    public async Task<int> CountEmployeesInDepartmentAsync(int departmentId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        return await connection.Table<Employee>()
            .Where(e => e.DepartmentId == departmentId && e.IsActive)
            .CountAsync()
            .ConfigureAwait(false);
    }

    public async Task<int> CountEmployeesInPositionAsync(int positionId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        return await connection.Table<Employee>()
            .Where(e => e.PositionId == positionId && e.IsActive)
            .CountAsync()
            .ConfigureAwait(false);
    }

    public async Task<int> CountEmployeesOnScheduleAsync(int scheduleId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        return await connection.Table<Employee>()
            .Where(e => e.WorkScheduleId == scheduleId && e.IsActive)
            .CountAsync()
            .ConfigureAwait(false);
    }

    public async Task<OrganizationHeadcounts> GetHeadcountsAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var active = await connection.Table<Employee>()
            .Where(e => e.IsActive)
            .ToListAsync()
            .ConfigureAwait(false);

        var byDepartment = active
            .Where(e => e.DepartmentId.HasValue)
            .GroupBy(e => e.DepartmentId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var byPosition = active
            .Where(e => e.PositionId.HasValue)
            .GroupBy(e => e.PositionId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        var bySchedule = active
            .Where(e => e.WorkScheduleId.HasValue)
            .GroupBy(e => e.WorkScheduleId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

        return new OrganizationHeadcounts(byDepartment, byPosition, bySchedule);
    }

    /// <summary>
    /// Deactivates or restores a department.
    ///
    /// <para>Deactivation is refused while active employees are still assigned.
    /// Letting it through would leave those employees pointing at a department
    /// that no longer appears in any picker — their records would still carry
    /// it, the departmental reports would quietly stop counting them, and
    /// nothing would say why.</para>
    ///
    /// <para>There is no delete. The department names the unit on every payslip
    /// already produced against it, so it is retired rather than removed.</para>
    /// </summary>
    public async Task<SaveResult<Department>> SetDepartmentActiveAsync(int id, bool active, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
            return await RefuseAsync<Department>(performedBy, "retire or restore a department", nameof(Department));

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var department = await connection.Table<Department>()
            .Where(d => d.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (department is null)
            return SaveResult<Department>.Fail("That department no longer exists.");

        if (!active)
        {
            var assigned = await CountEmployeesInDepartmentAsync(id).ConfigureAwait(false);

            if (assigned > 0)
            {
                return SaveResult<Department>.Fail(
                    $"{department.Name} still has {assigned} active employee(s) assigned. " +
                    "Move them to another department first.");
            }
        }

        department.IsActive = active;
        await connection.UpdateAsync(department).ConfigureAwait(false);

        await _audit.WriteAsync(
            active ? AuditActions.DepartmentReactivated : AuditActions.DepartmentDeactivated,
            nameof(Department), department.Id, true,
            $"{(active ? "Restored" : "Retired")} department {department.Code} — {department.Name}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<Department>.Ok(department,
            active
                ? $"{department.Name} restored."
                : $"{department.Name} retired. It stays on the records already filed against it.");
    }

    /// <summary>
    /// Deactivates or restores a position, under the same rule as a department:
    /// nobody may still hold it.
    /// </summary>
    public async Task<SaveResult<Position>> SetPositionActiveAsync(int id, bool active, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
            return await RefuseAsync<Position>(performedBy, "retire or restore a position", nameof(Position));

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var position = await connection.Table<Position>()
            .Where(p => p.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (position is null)
            return SaveResult<Position>.Fail("That position no longer exists.");

        if (!active)
        {
            var assigned = await CountEmployeesInPositionAsync(id).ConfigureAwait(false);

            if (assigned > 0)
            {
                return SaveResult<Position>.Fail(
                    $"{position.Title} is still held by {assigned} active employee(s). " +
                    "Reassign them first.");
            }
        }

        position.IsActive = active;
        await connection.UpdateAsync(position).ConfigureAwait(false);

        await _audit.WriteAsync(
            active ? AuditActions.PositionReactivated : AuditActions.PositionDeactivated,
            nameof(Position), position.Id, true,
            $"{(active ? "Restored" : "Retired")} position {position.Code} — {position.Title}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return SaveResult<Position>.Ok(position,
            active ? $"{position.Title} restored." : $"{position.Title} retired.");
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
