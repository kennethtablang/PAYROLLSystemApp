using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Security;

namespace PAYROLLSystemApp.Services;

/// <summary>Which employees a list should show (FR-016, FR-017).</summary>
public enum EmployeeStatusFilter
{
    Active,
    Separated,

    /// <summary>Everyone still on the books. Archived records stay hidden.</summary>
    All,

    /// <summary>The archived records themselves, so one can be restored.</summary>
    Archived
}

/// <summary>The search and filter criteria behind FR-016.</summary>
public sealed record EmployeeQuery(
    string? Term = null,
    int? DepartmentId = null,
    int? PositionId = null,
    EmployeeStatusFilter Status = EmployeeStatusFilter.Active);

/// <summary>
/// A field-level validation failure, so the form can put the message beside the
/// input that caused it rather than in one lump at the top (NFR-023).
/// </summary>
public sealed record FieldError(string Field, string Message);

/// <summary>Outcome of saving an employee, with any per-field messages.</summary>
public sealed record EmployeeSaveResult(
    bool Succeeded,
    string Message,
    Employee? Employee = null,
    IReadOnlyList<FieldError>? Errors = null)
{
    public IReadOnlyList<FieldError> FieldErrors => Errors ?? Array.Empty<FieldError>();

    public static EmployeeSaveResult Ok(Employee employee, string message) =>
        new(true, message, employee);

    public static EmployeeSaveResult Invalid(IReadOnlyList<FieldError> errors) =>
        new(false, "Correct the highlighted fields and try again.", null, errors);

    public static EmployeeSaveResult Fail(string message) => new(false, message);
}

public interface IEmployeeService
{
    /// <summary>Every employee, whatever their status. The caller narrows it.</summary>
    Task<IReadOnlyList<Employee>> GetAllAsync();

    Task<IReadOnlyList<Employee>> SearchAsync(EmployeeQuery query);

    Task<Employee?> GetByIdAsync(int id);

    Task<EmployeeSaveResult> SaveAsync(Employee employee, User performedBy, string rateChangeReason = "");

    Task<EmployeeSaveResult> SeparateAsync(int id, DateTime separationDate, string reason, User performedBy);

    Task<EmployeeSaveResult> ReinstateAsync(int id, User performedBy);

    /// <summary>
    /// FR-017. Hides an employee from every list and picker without touching a
    /// figure that has been paid or filed. This is what "delete" does here.
    /// </summary>
    Task<EmployeeSaveResult> ArchiveAsync(int id, string reason, User performedBy);

    /// <summary>Brings an archived employee back into the lists.</summary>
    Task<EmployeeSaveResult> RestoreAsync(int id, User performedBy);

    Task<IReadOnlyList<SalaryRateHistory>> GetRateHistoryAsync(int employeeId);

    /// <summary>Next unused employee number in the configured pattern, e.g. EMP-0007.</summary>
    Task<string> SuggestEmployeeNumberAsync();

    Task<EmployeeStatistics> GetStatisticsAsync();
}

/// <summary>Headline counts for the dashboard (FR-086).</summary>
public sealed record EmployeeStatistics(
    int Active,
    int Separated,
    int Probationary,
    int DueForRegularization,
    decimal MonthlyBasicPayroll);

/// <summary>
/// Section 2.2 of the requirements: the employee masterfile (FR-010 – FR-018).
///
/// <para><b>Validation happens here, not in the view model.</b> The form is one
/// caller; a CSV import (FR-019) and any later self-service edit would be
/// others, and a rule enforced only in the dialog is a rule the second caller
/// does not have.</para>
///
/// <para><b>Nothing is deleted.</b> Separation sets a date and clears the active
/// flag; the record and its salary history stay, because they are what a past
/// payslip, a BIR 2316 and an audit are read against (FR-017, NFR-037).</para>
/// </summary>
public sealed class EmployeeService : IEmployeeService
{
    private const string NumberPrefix = "EMP-";

    private readonly PayrollDatabase _database;
    private readonly IAuditService _audit;

    public EmployeeService(PayrollDatabase database, IAuditService audit)
    {
        _database = database;
        _audit = audit;
    }

    // ------------------------------------------------------------- reading

    public async Task<IReadOnlyList<Employee>> GetAllAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        return await connection.Table<Employee>().ToListAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Employee>> SearchAsync(EmployeeQuery query) =>
        Filter(await GetAllAsync().ConfigureAwait(false), query);

    /// <summary>
    /// Applies a query to an already-loaded set.
    ///
    /// <para>Pure and static, so the directory screen can re-filter on each
    /// keystroke against the list it already holds instead of reading the table
    /// again — while the meaning of a filter stays defined in exactly one
    /// place, which is what stops a screen and a report disagreeing about who
    /// counts as active.</para>
    /// </summary>
    public static IReadOnlyList<Employee> Filter(IEnumerable<Employee> source, EmployeeQuery query)
    {
        IEnumerable<Employee> results = source;

        // Archived records are out of every view but their own. That is the
        // whole point of archiving: the row survives for the payslips and the
        // alphalist that point at it, and nobody has to look at it again.
        results = query.Status switch
        {
            EmployeeStatusFilter.Active =>
                results.Where(e => e.IsActive && !e.IsSeparated && !e.IsArchived),
            EmployeeStatusFilter.Separated =>
                results.Where(e => (e.IsSeparated || !e.IsActive) && !e.IsArchived),
            EmployeeStatusFilter.Archived => results.Where(e => e.IsArchived),
            _ => results.Where(e => !e.IsArchived)
        };

        if (query.DepartmentId is { } department)
            results = results.Where(e => e.DepartmentId == department);

        if (query.PositionId is { } position)
            results = results.Where(e => e.PositionId == position);

        var term = (query.Term ?? string.Empty).Trim();
        if (term.Length > 0)
        {
            // Government identifiers are searchable by their digits, so a number
            // pasted from an agency form finds its employee whatever separators
            // it was copied with.
            var digits = GovernmentId.Digits(term);

            results = results.Where(e =>
                Contains(e.EmployeeNumber, term) ||
                Contains(e.LastName, term) ||
                Contains(e.FirstName, term) ||
                Contains(e.MiddleName, term) ||
                Contains(e.FullName, term) ||
                Contains(e.Email, term) ||
                Contains(e.ContactNumber, term) ||
                (digits.Length >= 4 && (
                    e.SssNumber.Contains(digits, StringComparison.Ordinal) ||
                    e.PhilHealthNumber.Contains(digits, StringComparison.Ordinal) ||
                    e.PagIbigNumber.Contains(digits, StringComparison.Ordinal) ||
                    e.Tin.Contains(digits, StringComparison.Ordinal))));
        }

        return results
            .OrderBy(e => e.LastName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(e => e.FirstName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<Employee?> GetByIdAsync(int id)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        return await connection.Table<Employee>()
            .Where(e => e.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SalaryRateHistory>> GetRateHistoryAsync(int employeeId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<SalaryRateHistory>()
            .Where(h => h.EmployeeId == employeeId)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows
            .OrderByDescending(h => h.EffectiveDate)
            .ThenByDescending(h => h.Id)
            .ToList();
    }

    public async Task<string> SuggestEmployeeNumberAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var rows = await connection.Table<Employee>().ToListAsync().ConfigureAwait(false);

        // The highest number already issued, not the row count: separated
        // employees keep their numbers, so counting would reissue one.
        var highest = 0;

        foreach (var row in rows)
        {
            var digits = new string(row.EmployeeNumber.SkipWhile(c => !char.IsDigit(c)).ToArray());

            if (int.TryParse(digits, out var value) && value > highest)
                highest = value;
        }

        return $"{NumberPrefix}{highest + 1:D4}";
    }

    public async Task<EmployeeStatistics> GetStatisticsAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var rows = await connection.Table<Employee>().ToListAsync().ConfigureAwait(false);

        var active = rows.Where(e => e.IsActive && !e.IsSeparated).ToList();

        // Probation runs six months by default under Art. 296; anyone at or past
        // that point needs a decision, and the count is what surfaces it.
        var threshold = DateTime.Today.AddMonths(-6);

        var due = active.Count(e =>
            e.EmploymentStatus == EmploymentStatus.Probationary &&
            !e.RegularizationDate.HasValue &&
            e.HireDate.Date <= threshold);

        return new EmployeeStatistics(
            Active: active.Count,
            Separated: rows.Count(e => e.IsSeparated),
            Probationary: active.Count(e => e.EmploymentStatus == EmploymentStatus.Probationary),
            DueForRegularization: due,
            MonthlyBasicPayroll: active
                .Where(e => e.PayType == PayType.Monthly)
                .Sum(e => e.BasicRate));
    }

    // ------------------------------------------------------------- writing

    public async Task<EmployeeSaveResult> SaveAsync(
        Employee employee, User performedBy, string rateChangeReason = "")
    {
        if (!performedBy.Can(Permission.ManageEmployees))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(Employee), employee.Id, false,
                $"Role {performedBy.RoleDisplayName} is not permitted to maintain employee records.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return EmployeeSaveResult.Fail("You do not have permission to maintain employee records.");
        }

        Normalise(employee);

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var errors = await ValidateAsync(connection, employee).ConfigureAwait(false);

        if (errors.Count > 0)
            return EmployeeSaveResult.Invalid(errors);

        var isNew = employee.Id == 0;
        employee.UpdatedUtc = DateTime.UtcNow;

        if (isNew)
        {
            employee.CreatedUtc = DateTime.UtcNow;
            await connection.InsertAsync(employee).ConfigureAwait(false);

            await _audit.WriteAsync(AuditActions.EmployeeCreated, nameof(Employee), employee.Id, true,
                $"Created employee {employee.EmployeeNumber} — {employee.FullName}, " +
                $"{employee.EmploymentStatusDisplay}, hired {employee.HireDate:dd MMM yyyy}.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            // The opening rate is the first row of the history, so the series
            // starts where the employment does rather than at the first raise.
            await RecordRateAsync(connection, employee, null, "Initial rate on hire", performedBy)
                .ConfigureAwait(false);

            return EmployeeSaveResult.Ok(employee,
                $"{employee.DisplayName} created as {employee.EmployeeNumber}.");
        }

        var existing = await connection.Table<Employee>()
            .Where(e => e.Id == employee.Id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (existing is null)
            return EmployeeSaveResult.Fail("That employee record no longer exists.");

        // Separation is its own operation with its own reason and audit entry,
        // so an ordinary edit must not be able to set or clear it by accident.
        employee.SeparationDate = existing.SeparationDate;
        employee.SeparationReason = existing.SeparationReason;
        employee.CreatedUtc = existing.CreatedUtc;

        var rateChanged =
            existing.BasicRate != employee.BasicRate ||
            existing.PayType != employee.PayType ||
            existing.PayFrequency != employee.PayFrequency;

        await connection.UpdateAsync(employee).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.EmployeeUpdated, nameof(Employee), employee.Id, true,
            $"Updated employee {employee.EmployeeNumber} — {employee.FullName}." +
            (rateChanged ? " Compensation changed; see the salary rate history." : string.Empty),
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        if (rateChanged)
        {
            await RecordRateAsync(connection, employee, existing,
                string.IsNullOrWhiteSpace(rateChangeReason) ? "Rate adjustment" : rateChangeReason,
                performedBy).ConfigureAwait(false);
        }

        return EmployeeSaveResult.Ok(employee, $"{employee.DisplayName} updated.");
    }

    public async Task<EmployeeSaveResult> SeparateAsync(
        int id, DateTime separationDate, string reason, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(Employee), id, false,
                $"Role {performedBy.RoleDisplayName} is not permitted to separate an employee.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return EmployeeSaveResult.Fail("You do not have permission to separate an employee.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var employee = await connection.Table<Employee>()
            .Where(e => e.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (employee is null)
            return EmployeeSaveResult.Fail("That employee record no longer exists.");

        if (employee.IsSeparated)
            return EmployeeSaveResult.Fail(
                $"{employee.DisplayName} was already separated on {employee.SeparationDate:dd MMM yyyy}.");

        if (separationDate.Date < employee.HireDate.Date)
            return EmployeeSaveResult.Invalid(
            [
                new FieldError(nameof(Employee.SeparationDate),
                    $"Cannot be before the hire date of {employee.HireDate:dd MMM yyyy}.")
            ]);

        if (string.IsNullOrWhiteSpace(reason))
            return EmployeeSaveResult.Invalid(
            [
                new FieldError(nameof(Employee.SeparationReason), "Give a reason for the separation.")
            ]);

        employee.SeparationDate = separationDate.Date;
        employee.SeparationReason = reason.Trim();
        employee.IsActive = false;
        employee.UpdatedUtc = DateTime.UtcNow;

        await connection.UpdateAsync(employee).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.EmployeeSeparated, nameof(Employee), employee.Id, true,
            $"Separated employee {employee.EmployeeNumber} — {employee.FullName} " +
            $"effective {separationDate:dd MMM yyyy}. Reason: {employee.SeparationReason}",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return EmployeeSaveResult.Ok(employee,
            $"{employee.DisplayName} separated effective {separationDate:dd MMM yyyy}. " +
            "The record is retained for payroll history.");
    }

    public async Task<EmployeeSaveResult> ReinstateAsync(int id, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(Employee), id, false,
                $"Role {performedBy.RoleDisplayName} is not permitted to reinstate an employee.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return EmployeeSaveResult.Fail("You do not have permission to reinstate an employee.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var employee = await connection.Table<Employee>()
            .Where(e => e.Id == id)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (employee is null)
            return EmployeeSaveResult.Fail("That employee record no longer exists.");

        var previous = employee.SeparationDate;

        employee.SeparationDate = null;
        employee.SeparationReason = string.Empty;
        employee.IsActive = true;
        employee.UpdatedUtc = DateTime.UtcNow;

        await connection.UpdateAsync(employee).ConfigureAwait(false);

        // The date that was cleared is written into the audit detail, because
        // clearing the column is the one thing that removes it from the record.
        await _audit.WriteAsync(AuditActions.EmployeeReinstated, nameof(Employee), employee.Id, true,
            $"Reinstated employee {employee.EmployeeNumber} — {employee.FullName}. " +
            $"Previous separation date {previous:dd MMM yyyy} cleared.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return EmployeeSaveResult.Ok(employee, $"{employee.DisplayName} reinstated.");
    }

    // =====================================================================
    // Archiving — what "delete" means here
    // =====================================================================

    /// <summary>
    /// FR-017, NFR-009. Takes an employee out of every list and picker while
    /// leaving the record itself intact.
    ///
    /// <para><b>There is no hard delete, and there cannot be one.</b> Payslips,
    /// the payroll register, each remittance report and the annual alphalist all
    /// key on this row; a BIR 2316 already issued names it. Removing it would
    /// change payroll that has been paid, remitted and filed — figures that are
    /// no longer the company's alone to alter.</para>
    ///
    /// <para>Archiving does what someone deleting a record actually wants: the
    /// employee stops appearing anywhere they have to be looked at. Reversible
    /// through <see cref="RestoreAsync"/>, and audited both ways.</para>
    /// </summary>
    public async Task<EmployeeSaveResult> ArchiveAsync(int id, string reason, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(Employee), id, false,
                $"Role {performedBy.RoleDisplayName} is not permitted to archive an employee.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return EmployeeSaveResult.Fail("You do not have permission to archive an employee.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var employee = await connection.FindAsync<Employee>(id).ConfigureAwait(false);

        if (employee is null)
            return EmployeeSaveResult.Fail("That employee record no longer exists.");

        if (employee.IsArchived)
            return EmployeeSaveResult.Fail($"{employee.DisplayName} is already archived.");

        employee.IsArchived = true;
        employee.IsActive = false;
        employee.ArchivedUtc = DateTime.UtcNow;
        employee.ArchivedReason = (reason ?? string.Empty).Trim();
        employee.UpdatedUtc = DateTime.UtcNow;

        await connection.UpdateAsync(employee).ConfigureAwait(false);

        var payslips = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM payslips WHERE EmployeeId = ?", id).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.EmployeeArchived, nameof(Employee), employee.Id, true,
            $"Archived employee {employee.EmployeeNumber} — {employee.FullName}. " +
            $"{payslips} payslip(s) retained." +
            (employee.ArchivedReason.Length > 0 ? $" Reason: {employee.ArchivedReason}" : string.Empty),
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return EmployeeSaveResult.Ok(employee,
            payslips > 0
                ? $"{employee.DisplayName} archived. Their {payslips} payslip(s) and everything " +
                  "reported from them are unchanged."
                : $"{employee.DisplayName} archived.");
    }

    public async Task<EmployeeSaveResult> RestoreAsync(int id, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(Employee), id, false,
                $"Role {performedBy.RoleDisplayName} is not permitted to restore an employee.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return EmployeeSaveResult.Fail("You do not have permission to restore an employee.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
        var employee = await connection.FindAsync<Employee>(id).ConfigureAwait(false);

        if (employee is null)
            return EmployeeSaveResult.Fail("That employee record no longer exists.");

        if (!employee.IsArchived)
            return EmployeeSaveResult.Fail($"{employee.DisplayName} is not archived.");

        employee.IsArchived = false;
        employee.ArchivedUtc = null;
        employee.ArchivedReason = string.Empty;

        // Active again only where nothing else is holding them back. A separated
        // employee restored from the archive is still separated.
        employee.IsActive = employee.SeparationDate is null;
        employee.UpdatedUtc = DateTime.UtcNow;

        await connection.UpdateAsync(employee).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.EmployeeRestored, nameof(Employee), employee.Id, true,
            $"Restored employee {employee.EmployeeNumber} — {employee.FullName} from the archive.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return EmployeeSaveResult.Ok(employee, $"{employee.DisplayName} restored.");
    }

    // ----------------------------------------------------------- internals

    private static void Normalise(Employee employee)
    {
        employee.EmployeeNumber = (employee.EmployeeNumber ?? string.Empty).Trim().ToUpperInvariant();
        employee.LastName = Tidy(employee.LastName);
        employee.FirstName = Tidy(employee.FirstName);
        employee.MiddleName = Tidy(employee.MiddleName);
        employee.Suffix = Tidy(employee.Suffix);
        employee.Email = (employee.Email ?? string.Empty).Trim().ToLowerInvariant();
        employee.ContactNumber = Tidy(employee.ContactNumber);
        employee.Address = Tidy(employee.Address);
        employee.EmergencyContactName = Tidy(employee.EmergencyContactName);
        employee.EmergencyContactNumber = Tidy(employee.EmergencyContactNumber);
        employee.BankName = Tidy(employee.BankName);
        employee.BankAccountNumber = Tidy(employee.BankAccountNumber);
        employee.SeparationReason = Tidy(employee.SeparationReason);

        // Digits only, so the same person cannot be entered twice by typing the
        // separators differently. See GovernmentId.
        employee.SssNumber = GovernmentId.Digits(employee.SssNumber);
        employee.PhilHealthNumber = GovernmentId.Digits(employee.PhilHealthNumber);
        employee.PagIbigNumber = GovernmentId.Digits(employee.PagIbigNumber);
        employee.Tin = GovernmentId.Digits(employee.Tin);

        employee.HireDate = employee.HireDate.Date;
        employee.BirthDate = employee.BirthDate?.Date;
        employee.RegularizationDate = employee.RegularizationDate?.Date;
        employee.SeparationDate = employee.SeparationDate?.Date;

        // Money is held at two decimal places from the moment it is stored
        // (FR-063), so nothing downstream inherits a stray third digit.
        employee.BasicRate = Math.Round(employee.BasicRate, 2, MidpointRounding.AwayFromZero);
        employee.MonthlyAllowance = Math.Round(employee.MonthlyAllowance, 2, MidpointRounding.AwayFromZero);
    }

    private async Task<List<FieldError>> ValidateAsync(
        SQLite.SQLiteAsyncConnection connection, Employee employee)
    {
        var errors = new List<FieldError>();

        if (employee.EmployeeNumber.Length == 0)
            errors.Add(new FieldError(nameof(Employee.EmployeeNumber), "Enter an employee number."));

        if (employee.LastName.Length == 0)
            errors.Add(new FieldError(nameof(Employee.LastName), "Enter a surname."));

        if (employee.FirstName.Length == 0)
            errors.Add(new FieldError(nameof(Employee.FirstName), "Enter a first name."));

        // FR-015: the number identifies the employee on every payslip, remittance
        // and bank file, so a duplicate is refused rather than disambiguated.
        if (employee.EmployeeNumber.Length > 0)
        {
            var number = employee.EmployeeNumber;

            var clash = await connection.Table<Employee>()
                .Where(e => e.EmployeeNumber == number && e.Id != employee.Id)
                .FirstOrDefaultAsync()
                .ConfigureAwait(false);

            if (clash is not null)
            {
                errors.Add(new FieldError(nameof(Employee.EmployeeNumber),
                    $"Already used by {clash.FullName}."));
            }
        }

        if (employee.Email.Length > 0 && !IsPlausibleEmail(employee.Email))
            errors.Add(new FieldError(nameof(Employee.Email), "Enter a valid e-mail address."));

        if (employee.BasicRate < 0)
            errors.Add(new FieldError(nameof(Employee.BasicRate), "The rate cannot be negative."));

        if (employee.MonthlyAllowance < 0)
            errors.Add(new FieldError(nameof(Employee.MonthlyAllowance), "The allowance cannot be negative."));

        if (employee.BirthDate is { } birth)
        {
            if (birth.Date >= DateTime.Today)
                errors.Add(new FieldError(nameof(Employee.BirthDate), "The birth date must be in the past."));
            else if (birth.Date > DateTime.Today.AddYears(-15))
                errors.Add(new FieldError(nameof(Employee.BirthDate),
                    "The employee would be under 15, which the Labor Code does not permit."));
        }

        if (employee.HireDate.Date > DateTime.Today.AddYears(1))
            errors.Add(new FieldError(nameof(Employee.HireDate), "The hire date is more than a year ahead."));

        if (employee.RegularizationDate is { } regular && regular.Date < employee.HireDate.Date)
        {
            errors.Add(new FieldError(nameof(Employee.RegularizationDate),
                "Regularisation cannot precede the hire date."));
        }

        // FR-014: a malformed identifier is rejected by the agency at the
        // counter, months after the remittance was filed. Catch it here.
        AddIdError(errors, GovernmentIdKind.Sss, employee.SssNumber, nameof(Employee.SssNumber));
        AddIdError(errors, GovernmentIdKind.PhilHealth, employee.PhilHealthNumber, nameof(Employee.PhilHealthNumber));
        AddIdError(errors, GovernmentIdKind.PagIbig, employee.PagIbigNumber, nameof(Employee.PagIbigNumber));
        AddIdError(errors, GovernmentIdKind.Tin, employee.Tin, nameof(Employee.Tin));

        return errors;
    }

    private static void AddIdError(List<FieldError> errors, GovernmentIdKind kind, string value, string field)
    {
        var result = GovernmentId.Validate(kind, value);

        if (!result.IsValid)
            errors.Add(new FieldError(field, result.Message));
    }

    private async Task RecordRateAsync(
        SQLite.SQLiteAsyncConnection connection,
        Employee employee,
        Employee? previous,
        string reason,
        User performedBy)
    {
        var entry = new SalaryRateHistory
        {
            EmployeeId = employee.Id,
            EffectiveDate = previous is null ? employee.HireDate : DateTime.Today,
            PreviousPayType = previous?.PayType ?? employee.PayType,
            PreviousRate = previous?.BasicRate ?? 0m,
            PreviousFrequency = previous?.PayFrequency ?? employee.PayFrequency,
            NewPayType = employee.PayType,
            NewRate = employee.BasicRate,
            NewFrequency = employee.PayFrequency,
            Reason = reason,
            RecordedBy = performedBy.Username,
            RecordedUtc = DateTime.UtcNow
        };

        await connection.InsertAsync(entry).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.SalaryRateChanged, nameof(Employee), employee.Id, true,
            $"{employee.EmployeeNumber} compensation: {entry.FromDisplay} to {entry.ToDisplay} " +
            $"effective {entry.EffectiveDate:dd MMM yyyy}. Reason: {reason}",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) &&
        haystack.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    private static string Tidy(string? value) => (value ?? string.Empty).Trim();

    /// <summary>
    /// A deliberately loose check. The address is not verified by sending to it,
    /// so the only failure worth reporting is one that is obviously not an
    /// address at all — rejecting valid but unusual addresses would be worse.
    /// </summary>
    private static bool IsPlausibleEmail(string value)
    {
        var at = value.IndexOf('@');

        return at > 0 &&
               at < value.Length - 1 &&
               value.IndexOf('@', at + 1) < 0 &&
               value.LastIndexOf('.') > at + 1 &&
               !value.EndsWith('.') &&
               !value.Contains(' ');
    }
}
