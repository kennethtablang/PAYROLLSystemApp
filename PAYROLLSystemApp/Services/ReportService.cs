using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>The reports section 2.8 asks for.</summary>
public enum ReportKind
{
    PayrollRegister = 0,
    BankFile = 1,
    SssRemittance = 2,
    PhilHealthRemittance = 3,
    PagIbigRemittance = 4,
    WithholdingTaxRemittance = 5,
    Attendance = 6,
    Alphalist = 7
}

/// <summary>What a report has to be told before it can be built.</summary>
public enum ReportScope
{
    /// <summary>One payroll run.</summary>
    Run = 0,

    /// <summary>One calendar month, taken from the pay period rather than the pay date.</summary>
    Month = 1,

    /// <summary>A from/to range of dates.</summary>
    DateRange = 2,

    /// <summary>One calendar year.</summary>
    Year = 3
}

/// <summary>One entry in the list of reports, and what it needs to be given.</summary>
public sealed record ReportDefinition(
    ReportKind Kind,
    string Title,
    string Description,
    ReportScope Scope,
    string RequirementRef,
    bool SupportsDepartmentFilter = false);

/// <summary>A request for one report, at one scope.</summary>
public sealed record ReportRequest(
    ReportKind Kind,
    int? RunId = null,
    int? Year = null,
    int? Month = null,
    DateTime? From = null,
    DateTime? To = null,
    int? DepartmentId = null);

/// <summary>A built report, or the reason it could not be built.</summary>
public sealed record ReportResult(bool Succeeded, string Message, ReportGrid? Grid = null)
{
    public static ReportResult Fail(string message) => new(false, message);

    public static ReportResult Ok(ReportGrid grid) => new(true, string.Empty, grid);
}

public interface IReportService
{
    /// <summary>The reports on offer, in the order the section lists them.</summary>
    IReadOnlyList<ReportDefinition> Definitions { get; }

    /// <summary>Runs a report can be built over, newest first. Cancelled runs are left out.</summary>
    Task<IReadOnlyList<PayrollRun>> GetRunsAsync();

    /// <summary>Years that carry payroll, newest first.</summary>
    Task<IReadOnlyList<int>> GetYearsAsync();

    Task<IReadOnlyList<Department>> GetDepartmentsAsync();

    /// <summary>FR-080 – FR-084. Builds one report.</summary>
    Task<ReportResult> BuildAsync(ReportRequest request, User asUser);

    /// <summary>FR-085. Writes a built report to a file.</summary>
    Task<ExportResult> ExportAsync(ReportGrid grid, ReportFormat format, User asUser);
}

/// <summary>
/// Section 2.8 (FR-080 – FR-085). Builds every report from the payroll that has
/// already been computed, and writes it out.
///
/// <para><b>Nothing here recomputes payroll.</b> Every figure comes off a
/// snapshotted payslip or its lines. A report that worked its own figures out
/// would eventually disagree with the payslip the employee is holding, and the
/// payslip is the one that was paid (NFR-009).</para>
///
/// <para><b>The permission gate is here, not in the view model.</b>
/// <see cref="Permission.ViewReports"/> is what section 2.8 requires, and an
/// employee does not have it — a self-service user reading a payroll register
/// would be reading everybody's salary. A refused request is audited like any
/// other privileged read.</para>
///
/// <para><b>The builders are pure.</b> This class does the I/O and hands each
/// one everything it needs; the arithmetic and the checks live in
/// <see cref="PayrollRegisterReport"/>, <see cref="RemittanceReport"/> and
/// <see cref="SummaryReports"/>, where they can be reasoned about without a
/// database.</para>
/// </summary>
public sealed class ReportService : IReportService
{
    private readonly PayrollDatabase _database;
    private readonly IPayrollConfigService _config;
    private readonly IOrganizationService _organization;
    private readonly IEmployeeService _employees;
    private readonly IAttendanceService _attendance;
    private readonly IAuditService _audit;

    public ReportService(
        PayrollDatabase database,
        IPayrollConfigService config,
        IOrganizationService organization,
        IEmployeeService employees,
        IAttendanceService attendance,
        IAuditService audit)
    {
        _database = database;
        _config = config;
        _organization = organization;
        _employees = employees;
        _attendance = attendance;
        _audit = audit;
    }

    public IReadOnlyList<ReportDefinition> Definitions { get; } =
    [
        new(ReportKind.PayrollRegister, "Payroll register",
            "Every employee on a run, every earning and deduction, and what they were paid.",
            ReportScope.Run, "FR-080", SupportsDepartmentFilter: true),

        new(ReportKind.SssRemittance, "SSS contribution report (R-3 / R-5)",
            "A month's SS, WISP and EC contributions, member by member.",
            ReportScope.Month, "FR-081"),

        new(ReportKind.PhilHealthRemittance, "PhilHealth remittance report (RF-1)",
            "A month's premiums, employee and employer share.",
            ReportScope.Month, "FR-081"),

        new(ReportKind.PagIbigRemittance, "Pag-IBIG remittance report (MCRF)",
            "A month's contributions, employee and employer share.",
            ReportScope.Month, "FR-081"),

        new(ReportKind.WithholdingTaxRemittance, "Withholding tax (BIR 1601-C)",
            "A month's tax withheld on compensation, employee by employee.",
            ReportScope.Month, "FR-081"),

        new(ReportKind.BankFile, "Bank disbursement file",
            "Account details and net pay for one run, ready for the bank's own template.",
            ReportScope.Run, "FR-082"),

        new(ReportKind.Attendance, "Attendance and absences",
            "Days, hours, overtime, tardiness and absences over a range, by department.",
            ReportScope.DateRange, "FR-083", SupportsDepartmentFilter: true),

        new(ReportKind.Alphalist, "Annual alphalist",
            "A year's compensation, contributions and tax withheld per employee.",
            ReportScope.Year, "FR-084")
    ];

    // =====================================================================
    // Scope options
    // =====================================================================

    public async Task<IReadOnlyList<PayrollRun>> GetRunsAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var runs = await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false);

        return runs
            .Where(r => !r.IsCancelled)
            .OrderByDescending(r => r.PayDate)
            .ThenByDescending(r => r.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<int>> GetYearsAsync()
    {
        var runs = await GetRunsAsync().ConfigureAwait(false);

        var years = runs.Select(r => r.PeriodYear).Distinct().ToList();

        if (years.Count == 0)
            years.Add(DateTime.Today.Year);

        return years.OrderByDescending(y => y).ToList();
    }

    public Task<IReadOnlyList<Department>> GetDepartmentsAsync() =>
        _organization.GetDepartmentsAsync(includeInactive: true);

    // =====================================================================
    // Building
    // =====================================================================

    public async Task<ReportResult> BuildAsync(ReportRequest request, User asUser)
    {
        if (!asUser.Can(Permission.ViewReports))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, "Report", null, false,
                $"Role {asUser.RoleDisplayName} is not permitted to read payroll reports.",
                asUser.Username, asUser.Id).ConfigureAwait(false);

            return ReportResult.Fail("You do not have permission to read payroll reports.");
        }

        return request.Kind switch
        {
            ReportKind.PayrollRegister => await RegisterAsync(request).ConfigureAwait(false),
            ReportKind.BankFile => await BankFileAsync(request).ConfigureAwait(false),
            ReportKind.Attendance => await AttendanceAsync(request).ConfigureAwait(false),
            ReportKind.Alphalist => await AlphalistAsync(request).ConfigureAwait(false),
            _ => await RemittanceAsync(request).ConfigureAwait(false)
        };
    }

    // ------------------------------------------------------------ FR-080

    private async Task<ReportResult> RegisterAsync(ReportRequest request)
    {
        if (request.RunId is not { } runId)
            return ReportResult.Fail("Choose the payroll run the register covers.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var run = await connection.Table<PayrollRun>()
            .Where(r => r.Id == runId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (run is null)
            return ReportResult.Fail("That payroll run no longer exists.");

        var payslips = (await connection.Table<Payslip>()
            .Where(p => p.PayrollRunId == runId)
            .ToListAsync()
            .ConfigureAwait(false))
            .OrderBy(p => p.EmployeeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var departmentName = await DepartmentNameAsync(request.DepartmentId).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(departmentName))
        {
            payslips = payslips
                .Where(p => string.Equals(p.DepartmentName, departmentName, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
        }

        var lines = await LinesForRunAsync(runId, payslips).ConfigureAwait(false);
        var names = await ComponentNamesAsync().ConfigureAwait(false);

        var grid = PayrollRegisterReport.Build(
            new RegisterSource(run, payslips, lines, names, departmentName));

        return ReportResult.Ok(grid);
    }

    // ------------------------------------------------------------ FR-082

    private async Task<ReportResult> BankFileAsync(ReportRequest request)
    {
        if (request.RunId is not { } runId)
            return ReportResult.Fail("Choose the payroll run to be disbursed.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var run = await connection.Table<PayrollRun>()
            .Where(r => r.Id == runId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (run is null)
            return ReportResult.Fail("That payroll run no longer exists.");

        var payslips = await connection.Table<Payslip>()
            .Where(p => p.PayrollRunId == runId)
            .ToListAsync()
            .ConfigureAwait(false);

        return ReportResult.Ok(SummaryReports.BankFile(run, payslips));
    }

    // ------------------------------------------------------------ FR-081

    private async Task<ReportResult> RemittanceAsync(ReportRequest request)
    {
        if (request.Year is not { } year || request.Month is not { } month)
            return ReportResult.Fail("Choose the month being remitted.");

        if (month is < 1 or > 12)
            return ReportResult.Fail("That is not a month.");

        var agency = request.Kind switch
        {
            ReportKind.SssRemittance => RemittanceAgency.Sss,
            ReportKind.PhilHealthRemittance => RemittanceAgency.PhilHealth,
            ReportKind.PagIbigRemittance => RemittanceAgency.PagIbig,
            _ => RemittanceAgency.WithholdingTax
        };

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        // The applicable month is the pay period's, not the pay date's: a
        // cut-off ending 30 September and paid on 5 October is September's
        // contribution.
        var runs = (await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false))
            .Where(r => !r.IsCancelled)
            .Where(r => r.PeriodEnd.Year == year && r.PeriodEnd.Month == month)
            .ToDictionary(r => r.Id);

        var payslips = (await connection.Table<Payslip>().ToListAsync().ConfigureAwait(false))
            .Where(p => runs.ContainsKey(p.PayrollRunId))
            .ToList();

        var company = await _config.GetCompanyProfileAsync().ConfigureAwait(false);
        var employees = (await _employees.GetAllAsync().ConfigureAwait(false)).ToDictionary(e => e.Id);

        var grid = RemittanceReport.Build(new RemittanceSource(
            RemittanceScheme.For(agency), year, month, company, payslips, runs, employees));

        return ReportResult.Ok(grid);
    }

    // ------------------------------------------------------------ FR-083

    private async Task<ReportResult> AttendanceAsync(ReportRequest request)
    {
        if (request.From is not { } from || request.To is not { } to)
            return ReportResult.Fail("Choose the range the report covers.");

        if (to.Date < from.Date)
            return ReportResult.Fail("The end of the range comes before its start.");

        if ((to.Date - from.Date).TotalDays > 366)
            return ReportResult.Fail("Choose a range of a year or less.");

        var employees = (await _employees.GetAllAsync().ConfigureAwait(false))
            .Where(e => request.DepartmentId is not { } id || e.DepartmentId == id)
            .ToList();

        if (employees.Count == 0)
            return ReportResult.Fail("No employees match that department.");

        var departments = (await _organization.GetDepartmentsAsync(includeInactive: true)
            .ConfigureAwait(false))
            .ToDictionary(d => d.Id, d => d.Name);

        var summaries = await _attendance.GetSummariesAsync(employees, from.Date, to.Date)
            .ConfigureAwait(false);

        var departmentName = await DepartmentNameAsync(request.DepartmentId).ConfigureAwait(false);

        return ReportResult.Ok(
            SummaryReports.Attendance(summaries, departments, from.Date, to.Date, departmentName));
    }

    // ------------------------------------------------------------ FR-084

    private async Task<ReportResult> AlphalistAsync(ReportRequest request)
    {
        if (request.Year is not { } year)
            return ReportResult.Fail("Choose the year being reported.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        // Posted only: a year-end certificate states what was actually paid.
        var runs = (await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false))
            .Where(r => r.IsPosted && r.PeriodYear == year)
            .Select(r => r.Id)
            .ToHashSet();

        var payslips = (await connection.Table<Payslip>().ToListAsync().ConfigureAwait(false))
            .Where(p => runs.Contains(p.PayrollRunId))
            .ToList();

        var ids = payslips.Select(p => p.Id).ToHashSet();

        var thirteenth = (await connection.Table<PayslipLine>().ToListAsync().ConfigureAwait(false))
            .Where(l => ids.Contains(l.PayslipId))
            .Where(l => l.Kind == PayslipLineKind.Earning && l.Code == PayComponentCodes.ThirteenthMonth)
            .Join(payslips, l => l.PayslipId, p => p.Id, (l, p) => (p.EmployeeId, l.Amount))
            .GroupBy(x => x.EmployeeId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        var employees = (await _employees.GetAllAsync().ConfigureAwait(false)).ToDictionary(e => e.Id);
        var settings = await _config.GetSettingsAsync().ConfigureAwait(false);

        return ReportResult.Ok(SummaryReports.Alphalist(
            year, payslips, thirteenth, employees, settings.AnnualiseTaxOnFinalPeriod));
    }

    // =====================================================================
    // Reads shared by the builders
    // =====================================================================

    /// <summary>
    /// Every line on a run, in one read, grouped by payslip. A register for
    /// three hundred employees is one query rather than three hundred (NFR-002).
    /// </summary>
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<PayslipLine>>> LinesForRunAsync(
        int runId, IReadOnlyList<Payslip> payslips)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var wanted = payslips.Select(p => p.Id).ToHashSet();

        var lines = await connection.Table<PayslipLine>()
            .Where(l => l.PayrollRunId == runId)
            .ToListAsync()
            .ConfigureAwait(false);

        return lines
            .Where(l => wanted.Contains(l.PayslipId))
            .GroupBy(l => l.PayslipId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<PayslipLine>)g.OrderBy(l => l.Sequence).ToList());
    }

    /// <summary>
    /// The configured name of every pay component, by code. The register's
    /// column headings come from here rather than from the payslip lines, whose
    /// descriptions carry per-employee detail.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ComponentNamesAsync()
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in await _config.GetEarningTypesAsync(includeInactive: true).ConfigureAwait(false))
            names[type.Code] = type.Name;

        foreach (var type in await _config.GetDeductionTypesAsync(includeInactive: true).ConfigureAwait(false))
            names[type.Code] = type.Name;

        return names;
    }

    private async Task<string> DepartmentNameAsync(int? departmentId)
    {
        if (departmentId is not { } id)
            return string.Empty;

        var departments = await _organization.GetDepartmentsAsync(includeInactive: true)
            .ConfigureAwait(false);

        return departments.FirstOrDefault(d => d.Id == id)?.Name ?? string.Empty;
    }

    // =====================================================================
    // FR-085 — export
    // =====================================================================

    public async Task<ExportResult> ExportAsync(ReportGrid grid, ReportFormat format, User asUser)
    {
        if (!asUser.Can(Permission.ViewReports))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, "Report", null, false,
                $"Role {asUser.RoleDisplayName} is not permitted to export payroll reports.",
                asUser.Username, asUser.Id).ConfigureAwait(false);

            return ExportResult.Fail("You do not have permission to export payroll reports.");
        }

        try
        {
            var company = await _config.GetCompanyProfileAsync().ConfigureAwait(false);

            var (bytes, extension) = format == ReportFormat.Csv
                ? (ReportCsv.Render(grid), "csv")
                : (ReportDocument.Render(company, grid), "pdf");

            var folder = ExportFolder();
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, $"{grid.FileStem}.{extension}");

            // A second export should not silently overwrite a file the user may
            // already have sent on.
            if (File.Exists(path))
                path = Path.Combine(folder, $"{grid.FileStem} ({DateTime.Now:HHmmss}).{extension}");

            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);

            // An export is the moment payroll data becomes a file somebody can
            // forward, so it is audited like any other privileged read (FR-091).
            // The log names the report and its scope, never the figures.
            await _audit.WriteAsync(AuditActions.ReportExported, "Report", null, true,
                $"Exported \"{grid.Title}\" ({grid.Subtitle}) as {extension.ToUpperInvariant()}, " +
                $"{grid.DataRowCount} row(s).",
                asUser.Username, asUser.Id).ConfigureAwait(false);

            return new ExportResult(true, $"Saved to {path}", path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportService] {ex}");

            return ExportResult.Fail(
                "The file could not be written. Check that the destination folder is available.");
        }
    }

    /// <summary>
    /// Where exports go: the user's Documents folder where the platform has one,
    /// and the application's own data directory otherwise.
    /// </summary>
    public static string ExportFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var root = string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents)
            ? FileSystem.AppDataDirectory
            : documents;

        return Path.Combine(root, "Payroll MS", "Reports");
    }
}
