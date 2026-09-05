using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>Which payslips a list should show.</summary>
public sealed record PayslipQuery(int? EmployeeId = null, int? Year = null, int? PayrollRunId = null);

/// <summary>A payslip with the run it came from, which the list needs and the row does not carry.</summary>
public sealed record PayslipListItem(Payslip Payslip, PayrollRun Run)
{
    public int Id => Payslip.Id;

    public bool IsPosted => Run.Status == PayrollRunStatus.Posted;
}

/// <summary>Where an exported document ended up, and whether it can be opened.</summary>
public sealed record ExportResult(bool Succeeded, string Message, string FilePath = "")
{
    public static ExportResult Fail(string message) => new(false, message);

    public bool HasFile => Succeeded && !string.IsNullOrWhiteSpace(FilePath);
}

public interface IPayslipService
{
    /// <summary>
    /// FR-070, FR-072. The payslips a user is allowed to see: everyone's with
    /// <see cref="Permission.ViewAllPayslips"/>, their own otherwise.
    /// </summary>
    Task<IReadOnlyList<PayslipListItem>> GetPayslipsAsync(PayslipQuery query, User asUser);

    Task<IReadOnlyList<PayslipLine>> GetLinesAsync(int payslipId);

    /// <summary>FR-074. The employee's year to this payslip's pay date, inclusive.</summary>
    Task<PayslipYearToDate> GetYearToDateAsync(Payslip payslip);

    /// <summary>The calendar years a user has payslips in, newest first.</summary>
    Task<IReadOnlyList<int>> GetYearsAsync(User asUser);

    /// <summary>FR-071. One payslip as a PDF.</summary>
    Task<ExportResult> ExportAsync(int payslipId, User asUser);

    /// <summary>FR-071. Every payslip on a run, one to a page.</summary>
    Task<ExportResult> ExportRunAsync(int payrollRunId, User asUser);
}

/// <summary>
/// Section 2.7 (FR-070 – FR-074). Reading and exporting payslips.
///
/// <para><b>Only posted runs are payslips.</b> A draft is a working figure and an
/// approved run is a figure waiting to be released; neither is something an
/// employee should be shown or handed as a PDF. The payroll and approval screens
/// show their own runs at every state, which is where that belongs — this
/// service deals in what has actually been paid.</para>
///
/// <para><b>Self-service is a filter here, not in the view model</b> (FR-072). A
/// user without <see cref="Permission.ViewAllPayslips"/> is narrowed to the
/// employee their account is linked to, and an account with no employee link
/// sees nothing at all rather than everything.</para>
/// </summary>
public sealed class PayslipService : IPayslipService
{
    private readonly PayrollDatabase _database;
    private readonly IPayrollConfigService _config;
    private readonly IAuditService _audit;

    public PayslipService(PayrollDatabase database, IPayrollConfigService config, IAuditService audit)
    {
        _database = database;
        _config = config;
        _audit = audit;
    }

    // =====================================================================
    // Reading
    // =====================================================================

    public async Task<IReadOnlyList<PayslipListItem>> GetPayslipsAsync(PayslipQuery query, User asUser)
    {
        var scope = ScopeFor(query, asUser);
        if (scope is null)
            return [];

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var runs = (await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false))
            .Where(r => r.Status == PayrollRunStatus.Posted)
            .ToDictionary(r => r.Id);

        var payslips = await connection.Table<Payslip>().ToListAsync().ConfigureAwait(false);

        return payslips
            .Where(p => runs.ContainsKey(p.PayrollRunId))
            .Where(p => scope.EmployeeId is not { } id || p.EmployeeId == id)
            .Where(p => query.Year is not { } year || p.PeriodEnd.Year == year)
            .Where(p => query.PayrollRunId is not { } runId || p.PayrollRunId == runId)
            .Select(p => new PayslipListItem(p, runs[p.PayrollRunId]))
            .OrderByDescending(i => i.Payslip.PayDate)
            .ThenBy(i => i.Payslip.EmployeeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<PayslipLine>> GetLinesAsync(int payslipId)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var rows = await connection.Table<PayslipLine>()
            .Where(l => l.PayslipId == payslipId)
            .ToListAsync()
            .ConfigureAwait(false);

        return rows.OrderBy(l => l.Kind).ThenBy(l => l.Sequence).ToList();
    }

    public async Task<IReadOnlyList<int>> GetYearsAsync(User asUser)
    {
        var all = await GetPayslipsAsync(new PayslipQuery(), asUser).ConfigureAwait(false);

        var years = all.Select(i => i.Payslip.PeriodEnd.Year).Distinct().ToList();

        if (years.Count == 0)
            years.Add(DateTime.Today.Year);

        return years.OrderByDescending(y => y).ToList();
    }

    /// <summary>
    /// FR-074. Everything the employee has been paid in the calendar year up to
    /// and including this payslip.
    ///
    /// <para>Bounded by <b>pay date</b> rather than by period, because that is
    /// the order money actually reached the employee, and it is the order the
    /// BIR 2316 adds them up in. Ties on the same pay date are broken by id, so
    /// two runs paid the same day still produce a running total rather than
    /// counting each other.</para>
    /// </summary>
    public async Task<PayslipYearToDate> GetYearToDateAsync(Payslip payslip)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var posted = (await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false))
            .Where(r => r.Status == PayrollRunStatus.Posted)
            .Select(r => r.Id)
            .ToHashSet();

        var year = payslip.PeriodEnd.Year;

        var siblings = (await connection.Table<Payslip>()
            .Where(p => p.EmployeeId == payslip.EmployeeId)
            .ToListAsync()
            .ConfigureAwait(false))
            .Where(p => posted.Contains(p.PayrollRunId))
            .Where(p => p.PeriodEnd.Year == year)
            .Where(p => p.PayDate.Date < payslip.PayDate.Date ||
                        (p.PayDate.Date == payslip.PayDate.Date && p.Id <= payslip.Id))
            .ToList();

        if (siblings.Count == 0)
            return PayslipYearToDate.Empty;

        var ids = siblings.Select(p => p.Id).ToHashSet();

        var thirteenth = (await connection.Table<PayslipLine>().ToListAsync().ConfigureAwait(false))
            .Where(l => ids.Contains(l.PayslipId))
            .Where(l => l.Kind == PayslipLineKind.Earning && l.Code == PayComponentCodes.ThirteenthMonth)
            .Sum(l => l.Amount);

        return new PayslipYearToDate(
            GrossPay: siblings.Sum(p => p.GrossPay),
            TaxableIncome: siblings.Sum(p => p.TaxableIncome),
            TaxWithheld: siblings.Sum(p => p.WithholdingTax),
            Sss: siblings.Sum(p => p.EmployeeSss + p.EmployeeSssWisp),
            PhilHealth: siblings.Sum(p => p.EmployeePhilHealth),
            PagIbig: siblings.Sum(p => p.EmployeePagIbig),
            ThirteenthMonth: thirteenth);
    }

    // =====================================================================
    // FR-071 — export
    // =====================================================================

    public async Task<ExportResult> ExportAsync(int payslipId, User asUser)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var payslip = await connection.Table<Payslip>()
            .Where(p => p.Id == payslipId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (payslip is null)
            return ExportResult.Fail("That payslip no longer exists.");

        if (!await MayReadAsync(payslip, asUser).ConfigureAwait(false))
            return ExportResult.Fail("You do not have permission to view that payslip.");

        var company = await _config.GetCompanyProfileAsync().ConfigureAwait(false);
        var lines = await GetLinesAsync(payslip.Id).ConfigureAwait(false);
        var ytd = await GetYearToDateAsync(payslip).ConfigureAwait(false);

        var bytes = PayslipDocument.Render(company, payslip, lines, ytd);

        var name = $"Payslip {payslip.PeriodCode} {Safe(payslip.EmployeeName)}.pdf";

        return await WriteAsync(bytes, name, asUser,
            $"Exported the payslip of {payslip.EmployeeName} for {payslip.PeriodCode}.",
            payslip.Id).ConfigureAwait(false);
    }

    public async Task<ExportResult> ExportRunAsync(int payrollRunId, User asUser)
    {
        if (!asUser.Can(Permission.ViewAllPayslips))
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(Payslip), payrollRunId, false,
                $"Role {asUser.RoleDisplayName} is not permitted to export a whole payroll run.",
                asUser.Username, asUser.Id).ConfigureAwait(false);

            return ExportResult.Fail("You do not have permission to export a whole run.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var run = await connection.Table<PayrollRun>()
            .Where(r => r.Id == payrollRunId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (run is null)
            return ExportResult.Fail("That payroll run no longer exists.");

        if (run.Status != PayrollRunStatus.Posted)
            return ExportResult.Fail($"{run.ReferenceNumber} is not posted yet, so it has no payslips to issue.");

        var payslips = (await connection.Table<Payslip>()
            .Where(p => p.PayrollRunId == payrollRunId)
            .ToListAsync()
            .ConfigureAwait(false))
            .OrderBy(p => p.EmployeeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (payslips.Count == 0)
            return ExportResult.Fail("That run has no payslips.");

        var company = await _config.GetCompanyProfileAsync().ConfigureAwait(false);

        var batch = new List<(Payslip, IReadOnlyList<PayslipLine>, PayslipYearToDate)>();

        foreach (var payslip in payslips)
        {
            batch.Add((
                payslip,
                await GetLinesAsync(payslip.Id).ConfigureAwait(false),
                await GetYearToDateAsync(payslip).ConfigureAwait(false)));
        }

        var bytes = PayslipDocument.Render(company, batch);

        return await WriteAsync(bytes, $"Payslips {run.ReferenceNumber}.pdf", asUser,
            $"Exported {payslips.Count} payslip(s) for {run.ReferenceNumber}.",
            run.Id).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the file and records that employee pay data left the application.
    ///
    /// <para>An export is the moment payroll data becomes a file somebody can
    /// forward, so it is audited like any other privileged read (FR-091,
    /// NFR-012) — the log names how many payslips and for what, never the
    /// figures.</para>
    /// </summary>
    private async Task<ExportResult> WriteAsync(
        byte[] bytes, string fileName, User asUser, string auditDetail, int? entityId)
    {
        try
        {
            var folder = ExportFolder();
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, fileName);

            // A second export of the same payslip should not silently overwrite
            // a file the user may already have sent on.
            if (File.Exists(path))
            {
                var stem = Path.GetFileNameWithoutExtension(fileName);
                path = Path.Combine(folder, $"{stem} ({DateTime.Now:HHmmss}).pdf");
            }

            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);

            await _audit.WriteAsync(AuditActions.PayslipExported, nameof(Payslip), entityId, true,
                auditDetail, asUser.Username, asUser.Id).ConfigureAwait(false);

            return new ExportResult(true, $"Saved to {path}", path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PayslipService] {ex}");

            return ExportResult.Fail(
                "The file could not be written. Check that the destination folder is available.");
        }
    }

    /// <summary>
    /// Where exports go: the user's Documents folder where the platform has one,
    /// and the application's own data directory otherwise — which is the case on
    /// Android and iOS, where an app cannot write wherever it likes.
    /// </summary>
    public static string ExportFolder()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var root = string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents)
            ? FileSystem.AppDataDirectory
            : documents;

        return Path.Combine(root, "Payroll MS", "Payslips");
    }

    // =====================================================================

    /// <summary>
    /// FR-072. Narrows a query to what the user may see, or null when they may
    /// see nothing.
    /// </summary>
    private static PayslipQuery? ScopeFor(PayslipQuery query, User asUser)
    {
        if (asUser.Can(Permission.ViewAllPayslips))
            return query;

        if (!asUser.Can(Permission.ViewOwnPayslip))
            return null;

        // An account that is not linked to an employee record has no payslips of
        // its own. Falling back to "everyone" here would be the whole point of
        // the permission undone.
        if (asUser.EmployeeId is not { } employeeId)
            return null;

        return query with { EmployeeId = employeeId };
    }

    private async Task<bool> MayReadAsync(Payslip payslip, User asUser)
    {
        if (asUser.Can(Permission.ViewAllPayslips))
            return true;

        if (!asUser.Can(Permission.ViewOwnPayslip) || asUser.EmployeeId != payslip.EmployeeId)
        {
            await _audit.WriteAsync(AuditActions.AccessDenied, nameof(Payslip), payslip.Id, false,
                $"{asUser.Username} attempted to read a payslip belonging to another employee.",
                asUser.Username, asUser.Id).ConfigureAwait(false);

            return false;
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var run = await connection.Table<PayrollRun>()
            .Where(r => r.Id == payslip.PayrollRunId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        return run?.Status == PayrollRunStatus.Posted;
    }

    /// <summary>Strips what a file name cannot carry, so an employee's name can be in it.</summary>
    private static string Safe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Where(c => !invalid.Contains(c)).ToArray()).Trim();

        return cleaned.Length == 0 ? "employee" : cleaned;
    }
}
