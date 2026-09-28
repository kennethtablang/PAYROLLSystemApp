using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Security;

namespace PAYROLLSystemApp.Services;

public interface IEmployeeImportService
{
    /// <summary>
    /// FR-019. Reads an employee file and reports what it would do. Nothing is
    /// written unless <paramref name="commit"/> is true.
    /// </summary>
    Task<ImportResult> ImportAsync(string filePath, User performedBy, bool commit = false);
}

/// <summary>
/// FR-019. Bulk employee import from CSV, TSV or XLSX, with validation and an
/// error report.
///
/// <para><b>Validation is not reimplemented here.</b> Every row is built into an
/// <see cref="Employee"/> and handed to <see cref="IEmployeeService.SaveAsync"/>,
/// which owns the uniqueness check, the government-identifier formats and the
/// salary-history entry. An importer with its own rules would be a second
/// definition of a valid employee, and the two would drift.</para>
///
/// <para><b>The employee number is the key.</b> A row whose number already
/// exists updates that employee rather than being rejected as a duplicate —
/// re-importing a corrected file is the normal way this gets used — and only the
/// columns actually present in the file are touched, so a file of bank details
/// cannot blank out everybody's address.</para>
///
/// <para><b>Department and position are matched by name or code</b>, and a value
/// that matches neither is reported rather than silently dropped: an employee
/// filed under no department is one who quietly vanishes from a departmental
/// report.</para>
/// </summary>
public sealed class EmployeeImportService : IEmployeeImportService
{
    // Field names used by the column map. Public constants rather than strings
    // at the call sites so a typo is a compile error.
    private const string Number = "number";
    private const string LastName = "last";
    private const string FirstName = "first";
    private const string MiddleName = "middle";
    private const string Suffix = "suffix";
    private const string FullName = "fullname";
    private const string BirthDate = "birth";
    private const string GenderField = "gender";
    private const string CivilStatusField = "civil";
    private const string Contact = "contact";
    private const string Email = "email";
    private const string Address = "address";
    private const string HireDate = "hired";
    private const string StatusField = "employmentstatus";
    private const string Department = "department";
    private const string Position = "position";
    private const string DetachmentField = "detachment";
    private const string PayTypeField = "paytype";
    private const string Rate = "rate";
    private const string Allowance = "allowance";
    private const string Sss = "sss";
    private const string PhilHealth = "philhealth";
    private const string PagIbig = "pagibig";
    private const string Tin = "tin";
    private const string BankName = "bankname";
    private const string BankAccount = "bankaccount";
    private const string MinimumWage = "minimumwage";

    private readonly IEmployeeService _employees;
    private readonly IOrganizationService _organization;
    private readonly IDetachmentService _detachments;
    private readonly IAuditService _audit;

    public EmployeeImportService(
        IEmployeeService employees, IOrganizationService organization,
        IDetachmentService detachments, IAuditService audit)
    {
        _employees = employees;
        _organization = organization;
        _detachments = detachments;
        _audit = audit;
    }

    public async Task<ImportResult> ImportAsync(string filePath, User performedBy, bool commit = false)
    {
        if (!performedBy.Can(Permission.ManageEmployees))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(Employee), null, false,
                $"Role {performedBy.RoleDisplayName} is not permitted to import employees.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return ImportResult.Fail("You do not have permission to import employees.");
        }

        TabularSheet sheet;

        try
        {
            sheet = await TabularFile.ReadAsync(filePath).ConfigureAwait(false);
        }
        catch (TabularFileException ex)
        {
            return ImportResult.Fail(ex.Message);
        }

        var map = BuildMap(sheet);

        if (!map.Has(Number))
        {
            return ImportResult.Fail(
                "No employee number column was found. The file needs a heading such as " +
                "\"Employee No\", \"EmpNo\" or \"Employee ID\".");
        }

        if (!map.Has(LastName) && !map.Has(FullName))
        {
            return ImportResult.Fail(
                "No name column was found. The file needs either \"Last Name\" and \"First Name\", " +
                "or a single \"Employee Name\" column.");
        }

        var warnings = new List<string>();

        // The whole file settles day-first versus month-first, not each value.
        var dayFirst = TabularFile.SettleDayFirst(map.Column(HireDate).Concat(map.Column(BirthDate)));

        if (dayFirst is null && (map.Has(HireDate) || map.Has(BirthDate)))
        {
            warnings.Add(
                "No date in this file settles whether it is written day-first or month-first, " +
                "so day-first was assumed. Check the hire dates in the preview before committing.");
        }

        var departments = await _organization.GetDepartmentsAsync(includeInactive: true).ConfigureAwait(false);
        var positions = await _organization.GetPositionsAsync(includeInactive: true).ConfigureAwait(false);
        var detachments = await _detachments.GetAllAsync(includeInactive: true).ConfigureAwait(false);

        var existing = (await _employees.GetAllAsync().ConfigureAwait(false))
            .ToDictionary(e => e.EmployeeNumber, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ImportRow>();

        for (var i = 0; i < sheet.Rows.Count; i++)
        {
            var line = i + 2; // The heading is line 1.
            var row = sheet.Rows[i];

            var number = map.Value(row, Number).Trim();

            if (number.Length == 0)
            {
                rows.Add(new ImportRow(line, string.Empty, string.Empty,
                    ImportRowOutcome.Rejected, "No employee number on this row."));
                continue;
            }

            if (!seen.Add(number))
            {
                rows.Add(new ImportRow(line, number, string.Empty, ImportRowOutcome.Rejected,
                    "This employee number appears more than once in the file."));
                continue;
            }

            var isUpdate = existing.TryGetValue(number, out var current);

            // Updating starts from what is stored, so a file carrying three
            // columns changes three fields rather than clearing the rest.
            var employee = isUpdate ? Copy(current!) : new Employee { EmployeeNumber = number };

            var problems = new List<string>();

            Apply(employee, map, row, departments, positions, detachments, dayFirst ?? true, problems);

            var name = employee.FullName;

            if (problems.Count > 0)
            {
                rows.Add(new ImportRow(line, number, name, ImportRowOutcome.Rejected,
                    string.Join(" ", problems)));
                continue;
            }

            if (isUpdate && IsUnchanged(current!, employee))
            {
                rows.Add(new ImportRow(line, number, name, ImportRowOutcome.Unchanged,
                    "Already matches the stored record."));
                continue;
            }

            if (!commit)
            {
                // A dry run cannot call SaveAsync, so the rules that would reject
                // the row are checked here and the row is described as it would
                // be saved. SaveAsync remains the authority when it is committed.
                var preview = Validate(employee, existing, number);

                rows.Add(preview.Length > 0
                    ? new ImportRow(line, number, name, ImportRowOutcome.Rejected, preview)
                    : new ImportRow(line, number, name,
                        isUpdate ? ImportRowOutcome.Update : ImportRowOutcome.Create, string.Empty));

                continue;
            }

            var result = await _employees
                .SaveAsync(employee, performedBy, "Bulk import (FR-019)")
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                var message = result.FieldErrors.Count > 0
                    ? string.Join("; ", result.FieldErrors.Select(e => $"{e.Field}: {e.Message}"))
                    : result.Message;

                rows.Add(new ImportRow(line, number, name, ImportRowOutcome.Rejected, message));
                continue;
            }

            rows.Add(new ImportRow(line, number, name,
                isUpdate ? ImportRowOutcome.Update : ImportRowOutcome.Create, string.Empty));
        }

        var detected = Describe(map);

        var outcome = new ImportResult(
            Succeeded: true,
            Message: commit ? "Import finished." : "Nothing has been written yet.",
            WasCommitted: commit,
            Rows: rows,
            DetectedColumns: detected,
            Warnings: warnings);

        if (commit)
        {
            await _audit.WriteAsync(AuditActions.EmployeeImported, nameof(Employee), null, true,
                $"Imported {Path.GetFileName(filePath)}: {outcome.Summary}",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);
        }

        return outcome;
    }

    // =====================================================================

    private static ColumnMap BuildMap(TabularSheet sheet)
    {
        var map = new ColumnMap(sheet);

        map.Detect(Number, "employee no", "employee number", "empno", "emp no", "employee id",
            "empid", "id no", "badge", "employee code");

        map.Detect(LastName, "last name", "lastname", "surname", "family name", "apelyido");
        map.Detect(FirstName, "first name", "firstname", "given name", "pangalan");
        map.Detect(MiddleName, "middle name", "middlename", "middle initial");
        map.Detect(Suffix, "suffix", "name suffix", "extension name");
        map.Detect(FullName, "employee name", "full name", "name of employee", "name");

        map.Detect(BirthDate, "birth date", "birthdate", "date of birth", "dob", "birthday");
        map.Detect(GenderField, "gender", "sex");
        map.Detect(CivilStatusField, "civil status", "marital status");
        map.Detect(Contact, "contact number", "mobile", "phone", "cellphone", "contact no");
        map.Detect(Email, "email", "e-mail", "email address");
        map.Detect(Address, "address", "home address", "residence");

        map.Detect(HireDate, "date hired", "hire date", "hired", "date of hire", "start date",
            "employment date");
        map.Detect(StatusField, "employment status", "status", "employment type");
        map.Detect(Department, "department", "dept", "division");
        map.Detect(Position, "position", "job title", "designation", "title");
        map.Detect(DetachmentField, "detach code", "detachment code", "detachment", "detach",
            "assignment");

        map.Detect(PayTypeField, "pay type", "paytype", "salary type", "wage type", "rate type");
        map.Detect(Rate, "basic rate", "rate", "basic salary", "salary", "monthly rate",
            "daily rate", "basic pay");
        map.Detect(Allowance, "allowance", "monthly allowance", "fixed allowance");

        map.Detect(Sss, "sss", "sss no", "sss number");
        map.Detect(PhilHealth, "philhealth", "phic", "philhealth no", "philhealth number");
        map.Detect(PagIbig, "pagibig", "pag-ibig", "hdmf", "pagibig no", "pag-ibig mid");
        map.Detect(Tin, "tin", "tin no", "tax identification");

        map.Detect(BankName, "bank", "bank name");
        map.Detect(BankAccount, "account number", "bank account", "account no", "payroll account");

        map.Detect(MinimumWage, "minimum wage", "mwe", "minimum wage earner");

        return map;
    }

    private static void Apply(
        Employee employee, ColumnMap map, string[] row,
        IReadOnlyList<Department> departments, IReadOnlyList<Position> positions,
        IReadOnlyList<Detachment> detachments, bool dayFirst, List<string> problems)
    {
        // Name: either the split columns, or one "Surname, Given" field.
        if (map.Has(LastName))
        {
            Set(map.Value(row, LastName), v => employee.LastName = v);
            Set(map.Value(row, FirstName), v => employee.FirstName = v);
            Set(map.Value(row, MiddleName), v => employee.MiddleName = v);
            Set(map.Value(row, Suffix), v => employee.Suffix = v);
        }
        else if (map.Has(FullName))
        {
            var whole = map.Value(row, FullName);

            if (whole.Length > 0)
            {
                var comma = whole.IndexOf(',');

                if (comma > 0)
                {
                    employee.LastName = whole[..comma].Trim();
                    employee.FirstName = whole[(comma + 1)..].Trim();
                }
                else
                {
                    // No comma: the last word is the surname, which is the
                    // convention a single-column name list follows.
                    var parts = whole.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    employee.LastName = parts[^1];
                    employee.FirstName = string.Join(' ', parts[..^1]);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(employee.LastName) || string.IsNullOrWhiteSpace(employee.FirstName))
            problems.Add("A first and last name are required.");

        ReadDate(map, row, BirthDate, dayFirst, problems, "birth date", d => employee.BirthDate = d);
        ReadDate(map, row, HireDate, dayFirst, problems, "hire date", d =>
        {
            if (d is { } value)
                employee.HireDate = value;
        });

        Set(map.Value(row, Contact), v => employee.ContactNumber = v);
        Set(map.Value(row, Email), v => employee.Email = v);
        Set(map.Value(row, Address), v => employee.Address = v);

        var gender = map.Value(row, GenderField);

        if (gender.Length > 0)
        {
            employee.Gender = gender.Trim().ToLowerInvariant() switch
            {
                "m" or "male" => Gender.Male,
                "f" or "female" => Gender.Female,
                _ => Gender.Unspecified
            };
        }

        var civil = map.Value(row, CivilStatusField);

        if (civil.Length > 0 && Enum.TryParse<CivilStatus>(civil.Replace(" ", string.Empty), true, out var parsedCivil))
            employee.CivilStatus = parsedCivil;

        var status = map.Value(row, StatusField);

        if (status.Length > 0)
        {
            if (Enum.TryParse<EmploymentStatus>(status.Replace(" ", string.Empty).Replace("-", string.Empty),
                    true, out var parsedStatus))
            {
                employee.EmploymentStatus = parsedStatus;
            }
            else
            {
                problems.Add($"\"{status}\" is not an employment status.");
            }
        }

        var department = map.Value(row, Department);

        if (department.Length > 0)
        {
            var match = departments.FirstOrDefault(d =>
                Same(d.Name, department) || Same(d.Code, department));

            if (match is null)
                problems.Add($"There is no department called \"{department}\".");
            else
                employee.DepartmentId = match.Id;
        }

        var position = map.Value(row, Position);

        if (position.Length > 0)
        {
            var match = positions.FirstOrDefault(p =>
                Same(p.Title, position) || Same(p.Code, position));

            if (match is null)
                problems.Add($"There is no position called \"{position}\".");
            else
                employee.PositionId = match.Id;
        }

        // The detachment is where a guard's daily rate comes from, so a code
        // that matches nothing is refused rather than dropped: the guard would
        // otherwise be paid on their own rate without anyone noticing.
        var detachment = map.Value(row, DetachmentField);

        if (detachment.Length > 0)
        {
            var match = detachments.FirstOrDefault(d => Same(d.Code, detachment))
                        ?? detachments.FirstOrDefault(d => Same(d.Name, detachment));

            if (match is null)
                problems.Add($"There is no detachment with the code \"{detachment}\". Add it on Detachments first.");
            else if (!match.IsActive)
                problems.Add($"Detachment {match.Code} is retired.");
            else
                employee.DetachmentId = match.Id;
        }

        var payType = map.Value(row, PayTypeField);

        if (payType.Length > 0)
        {
            if (Enum.TryParse<PayType>(payType.Trim(), true, out var parsedPayType))
                employee.PayType = parsedPayType;
            else
                problems.Add($"\"{payType}\" is not a pay type. Use Monthly, Daily or Hourly.");
        }

        var rate = map.Value(row, Rate);

        if (rate.Length > 0)
        {
            if (TabularFile.TryReadDecimal(rate, out var amount) && amount >= 0m)
                employee.BasicRate = amount;
            else
                problems.Add($"\"{rate}\" is not a rate.");
        }

        var allowance = map.Value(row, Allowance);

        if (allowance.Length > 0 && TabularFile.TryReadDecimal(allowance, out var allowanceAmount))
            employee.MonthlyAllowance = allowanceAmount;

        ReadIdentifier(map, row, Sss, GovernmentIdKind.Sss, problems, v => employee.SssNumber = v);
        ReadIdentifier(map, row, PhilHealth, GovernmentIdKind.PhilHealth, problems, v => employee.PhilHealthNumber = v);
        ReadIdentifier(map, row, PagIbig, GovernmentIdKind.PagIbig, problems, v => employee.PagIbigNumber = v);
        ReadIdentifier(map, row, Tin, GovernmentIdKind.Tin, problems, v => employee.Tin = v);

        Set(map.Value(row, BankName), v => employee.BankName = v);
        Set(map.Value(row, BankAccount), v => employee.BankAccountNumber = v);

        if (map.Has(MinimumWage))
        {
            var flag = map.Value(row, MinimumWage);

            if (flag.Length > 0)
                employee.IsMinimumWageEarner = TabularFile.ReadFlag(flag);
        }
    }

    private static void ReadDate(
        ColumnMap map, string[] row, string field, bool dayFirst,
        List<string> problems, string label, Action<DateTime?> assign)
    {
        var value = map.Value(row, field);

        if (value.Length == 0)
            return;

        if (TabularFile.TryReadDate(value, dayFirst, out var date))
            assign(date);
        else
            problems.Add($"\"{value}\" is not a {label}.");
    }

    private static void ReadIdentifier(
        ColumnMap map, string[] row, string field, GovernmentIdKind kind,
        List<string> problems, Action<string> assign)
    {
        var value = map.Value(row, field);

        if (value.Length == 0)
            return;

        var result = GovernmentId.Validate(kind, value);

        if (result.IsValid)
            assign(result.Normalised);
        else
            problems.Add($"{GovernmentId.DisplayName(kind)}: {result.Message}");
    }

    /// <summary>
    /// The rules a dry run can check without writing. Deliberately a subset —
    /// <see cref="IEmployeeService.SaveAsync"/> stays the authority, and a row
    /// that passes here can still be rejected on commit, which the report says.
    /// </summary>
    private static string Validate(
        Employee employee, IReadOnlyDictionary<string, Employee> existing, string number)
    {
        if (employee.EmployeeNumber.Length > 20)
            return "The employee number is longer than 20 characters.";

        if (employee.BasicRate < 0m)
            return "The basic rate cannot be negative.";

        if (employee.BirthDate is { } birth && birth > DateTime.Today)
            return "The birth date is in the future.";

        // FR-015 is enforced on the number, and matching one is an update rather
        // than a clash — but a row that would create a second employee under a
        // number already held by somebody with a different name is worth naming.
        if (existing.TryGetValue(number, out var current) &&
            !string.Equals(current.LastName, employee.LastName, StringComparison.OrdinalIgnoreCase) &&
            current.LastName.Length > 0 && employee.LastName.Length > 0)
        {
            return $"{number} is already held by {current.FullName}. " +
                   "The import would rename that employee.";
        }

        return string.Empty;
    }

    private static bool IsUnchanged(Employee stored, Employee incoming) =>
        stored.LastName == incoming.LastName &&
        stored.FirstName == incoming.FirstName &&
        stored.MiddleName == incoming.MiddleName &&
        stored.Suffix == incoming.Suffix &&
        stored.BirthDate == incoming.BirthDate &&
        stored.Gender == incoming.Gender &&
        stored.CivilStatus == incoming.CivilStatus &&
        stored.ContactNumber == incoming.ContactNumber &&
        stored.Email == incoming.Email &&
        stored.Address == incoming.Address &&
        stored.HireDate == incoming.HireDate &&
        stored.EmploymentStatus == incoming.EmploymentStatus &&
        stored.DepartmentId == incoming.DepartmentId &&
        stored.PositionId == incoming.PositionId &&
        stored.DetachmentId == incoming.DetachmentId &&
        stored.PayType == incoming.PayType &&
        stored.BasicRate == incoming.BasicRate &&
        stored.MonthlyAllowance == incoming.MonthlyAllowance &&
        stored.SssNumber == incoming.SssNumber &&
        stored.PhilHealthNumber == incoming.PhilHealthNumber &&
        stored.PagIbigNumber == incoming.PagIbigNumber &&
        stored.Tin == incoming.Tin &&
        stored.BankName == incoming.BankName &&
        stored.BankAccountNumber == incoming.BankAccountNumber &&
        stored.IsMinimumWageEarner == incoming.IsMinimumWageEarner;

    private static Employee Copy(Employee source) => new()
    {
        Id = source.Id,
        EmployeeNumber = source.EmployeeNumber,
        LastName = source.LastName,
        FirstName = source.FirstName,
        MiddleName = source.MiddleName,
        Suffix = source.Suffix,
        BirthDate = source.BirthDate,
        Gender = source.Gender,
        CivilStatus = source.CivilStatus,
        ContactNumber = source.ContactNumber,
        Email = source.Email,
        Address = source.Address,
        EmergencyContactName = source.EmergencyContactName,
        EmergencyContactNumber = source.EmergencyContactNumber,
        PhotoPath = source.PhotoPath,
        HireDate = source.HireDate,
        RegularizationDate = source.RegularizationDate,
        SeparationDate = source.SeparationDate,
        SeparationReason = source.SeparationReason,
        EmploymentStatus = source.EmploymentStatus,
        DepartmentId = source.DepartmentId,
        PositionId = source.PositionId,
        DetachmentId = source.DetachmentId,
        UsesOwnRate = source.UsesOwnRate,
        SupervisorId = source.SupervisorId,
        WorkScheduleId = source.WorkScheduleId,
        PayType = source.PayType,
        BasicRate = source.BasicRate,
        PayFrequency = source.PayFrequency,
        MonthlyAllowance = source.MonthlyAllowance,
        IsMinimumWageEarner = source.IsMinimumWageEarner,
        SssNumber = source.SssNumber,
        PhilHealthNumber = source.PhilHealthNumber,
        PagIbigNumber = source.PagIbigNumber,
        Tin = source.Tin,
        ExemptFromSss = source.ExemptFromSss,
        ExemptFromPhilHealth = source.ExemptFromPhilHealth,
        ExemptFromPagIbig = source.ExemptFromPagIbig,
        BankName = source.BankName,
        BankAccountNumber = source.BankAccountNumber,
        IsActive = source.IsActive,
        IsArchived = source.IsArchived,
        ArchivedUtc = source.ArchivedUtc,
        ArchivedReason = source.ArchivedReason,
        CreatedUtc = source.CreatedUtc,
        UpdatedUtc = source.UpdatedUtc
    };

    private static List<string> Describe(ColumnMap map)
    {
        var fields = new (string Field, string Label)[]
        {
            (Number, "Employee number"), (LastName, "Last name"), (FirstName, "First name"),
            (MiddleName, "Middle name"), (Suffix, "Suffix"), (FullName, "Employee name"),
            (BirthDate, "Birth date"), (GenderField, "Gender"), (CivilStatusField, "Civil status"),
            (Contact, "Contact number"), (Email, "Email"), (Address, "Address"),
            (HireDate, "Date hired"), (StatusField, "Employment status"),
            (Department, "Department"), (Position, "Position"), (DetachmentField, "Detachment"),
            (PayTypeField, "Pay type"), (Rate, "Basic rate"), (Allowance, "Allowance"),
            (Sss, "SSS number"), (PhilHealth, "PhilHealth number"), (PagIbig, "Pag-IBIG number"),
            (Tin, "TIN"), (BankName, "Bank"), (BankAccount, "Bank account"),
            (MinimumWage, "Minimum wage earner")
        };

        return fields
            .Where(f => map.Has(f.Field))
            // "Name" also matches inside "Last Name", but the split columns win
            // in Apply, so listing it would describe a mapping that is not used.
            .Where(f => f.Field != FullName || !map.Has(LastName))
            .Select(f => $"{f.Label} ← \"{map.HeaderOf(f.Field)}\"")
            .ToList();
    }

    private static void Set(string value, Action<string> assign)
    {
        if (!string.IsNullOrWhiteSpace(value))
            assign(value.Trim());
    }

    private static bool Same(string a, string b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
