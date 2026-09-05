using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Security;

namespace PAYROLLSystemApp.Services;

/// <summary>The agencies a remittance form can be produced for (FR-081).</summary>
public enum RemittanceAgency
{
    Sss = 0,
    PhilHealth = 1,
    PagIbig = 2,

    /// <summary>BIR 1601-C — the monthly remittance return for compensation.</summary>
    WithholdingTax = 3
}

/// <summary>
/// One column pair on a remittance form: what the member paid and what the
/// employer paid alongside it.
///
/// <para>Either side may be absent. EC is employer-only, and giving it an empty
/// employee column would read as a missed deduction; withholding tax is
/// employee-only for the same reason in reverse.</para>
/// </summary>
public sealed record RemittanceColumn(
    string Label,
    Func<Payslip, decimal>? Employee,
    Func<Payslip, decimal>? Employer);

/// <summary>
/// What makes one agency's form different from another's.
///
/// <para><b>The three forms are one report three times.</b> Each is an
/// applicable month, one row per member showing their number, the compensation
/// the schedule was read at, and the shares. Only three things actually differ —
/// which figures belong to the agency, which identifier the member files under,
/// and what the basis column means — so those are what a scheme declares, and
/// one builder serves all of them. A fourth agency is a scheme, not a
/// report.</para>
/// </summary>
public sealed record RemittanceScheme(
    RemittanceAgency Agency,
    string Title,
    string ShortName,
    string FileStem,
    string MemberIdLabel,
    Func<Payslip, string> MemberId,
    string BasisLabel,
    Func<IReadOnlyList<Payslip>, decimal> Basis,
    IReadOnlyList<RemittanceColumn> Columns,
    string EmployerNumberLabel,
    Func<CompanyProfile, string> EmployerNumber,
    Func<Employee, bool> IsExempt,
    IReadOnlyList<string> Footnotes)
{
    public static RemittanceScheme For(RemittanceAgency agency) => agency switch
    {
        RemittanceAgency.Sss => new RemittanceScheme(
            agency,
            "SSS contribution report (R-3 / R-5)",
            "SSS",
            "sss-r3",
            "SSS number",
            p => p.SssNumber,
            "Monthly basic pay",
            Latest(p => p.MonthlyBasis),
            [
                new RemittanceColumn("SS", p => p.EmployeeSss, p => p.EmployerSss),

                // The MSC above the WISP threshold splits in two, and the engine
                // has written them as separate lines since the day it was built
                // precisely so the form can put them in their own columns.
                new RemittanceColumn("WISP", p => p.EmployeeSssWisp, p => p.EmployerSssWisp),
                new RemittanceColumn("EC", null, p => p.EmployerEc)
            ],
            "SSS employer number",
            c => c.SssEmployerNumber,
            e => e.ExemptFromSss,
            [
                "The basis column is monthly basic pay, which is what the payslip stores. " +
                "Where an allowance counts towards the contribution base the schedule was read " +
                "at a higher figure than shown; the contribution amounts are unaffected."
            ]),

        RemittanceAgency.PhilHealth => new RemittanceScheme(
            agency,
            "PhilHealth remittance report (RF-1)",
            "PhilHealth",
            "philhealth-rf1",
            "PhilHealth number",
            p => p.PhilHealthNumber,
            "Monthly basic pay",
            Latest(p => p.MonthlyBasis),
            [new RemittanceColumn("Premium", p => p.EmployeePhilHealth, p => p.EmployerPhilHealth)],
            "PhilHealth employer number",
            c => c.PhilHealthEmployerNumber,
            e => e.ExemptFromPhilHealth,
            [
                "The employer share is the remainder of the premium rather than a second rounded " +
                "half, so the two sides add back to the premium exactly and may differ by one centavo."
            ]),

        RemittanceAgency.PagIbig => new RemittanceScheme(
            agency,
            "Pag-IBIG remittance report (MCRF)",
            "Pag-IBIG",
            "pagibig-mcrf",
            "Pag-IBIG MID number",
            p => p.PagIbigNumber,
            "Monthly basic pay",
            Latest(p => p.MonthlyBasis),
            [new RemittanceColumn("Contribution", p => p.EmployeePagIbig, p => p.EmployerPagIbig)],
            "Pag-IBIG employer number",
            c => c.PagIbigEmployerNumber,
            e => e.ExemptFromPagIbig,
            [
                "Pag-IBIG reads its schedule on monthly compensation, which may include an " +
                "allowance. The basis column shows monthly basic pay, which is what the payslip " +
                "stores; the contribution amounts were computed on the right base at run time."
            ]),

        _ => new RemittanceScheme(
            RemittanceAgency.WithholdingTax,
            "Withholding tax on compensation (BIR 1601-C)",
            "Withholding tax",
            "bir-1601c",
            "TIN",
            p => p.Tin,
            "Taxable income",
            slips => slips.Sum(p => Math.Max(0m, p.TaxableIncome)),
            [new RemittanceColumn("Tax withheld", p => p.WithholdingTax, null)],
            "Employer TIN",
            c => c.Tin,

            // A minimum wage earner is statutorily exempt from withholding, and
            // withholding nothing from them is correct rather than an omission.
            e => e.IsMinimumWageEarner,
            [
                "A minimum wage earner is exempt from withholding tax under RA 9504 and is listed " +
                "with a nil figure rather than left off."
            ])
    };

    /// <summary>
    /// A monthly figure taken from the month's last payslip rather than summed.
    ///
    /// <para>Monthly compensation is a level, not a flow: adding the same
    /// ₱30,000 across two semi-monthly payslips would report ₱60,000 and put the
    /// member in a bracket they are not in.</para>
    /// </summary>
    private static Func<IReadOnlyList<Payslip>, decimal> Latest(Func<Payslip, decimal> select) =>
        slips => slips
            .OrderBy(p => p.PayDate)
            .ThenBy(p => p.Id)
            .Select(select)
            .LastOrDefault();
}

/// <summary>What a remittance form is built from, read once by <see cref="ReportService"/>.</summary>
public sealed record RemittanceSource(
    RemittanceScheme Scheme,
    int Year,
    int Month,
    CompanyProfile Company,
    IReadOnlyList<Payslip> Payslips,
    IReadOnlyDictionary<int, PayrollRun> Runs,
    IReadOnlyDictionary<int, Employee> Employees);

/// <summary>
/// FR-081. The statutory remittance forms: SSS R-3/R-5, PhilHealth RF-1,
/// Pag-IBIG MCRF and BIR 1601-C.
///
/// <para><b>The month is the unit, and it is the pay period's month rather than
/// the pay date's.</b> A cut-off running to 30 September and paid on 5 October
/// is September's contribution; remitting it as October's leaves September short
/// and October over, which is the kind of error the agency finds a year later
/// rather than we do.</para>
///
/// <para><b>Each form checks whether it can honestly be filed</b> before it lets
/// itself be printed — a member with no number, an employer with no registration
/// number, somebody paid this month who contributed nothing without being
/// exempt, and any contributing run that is not yet posted. A filing made from
/// figures that can still be recalculated is a filing that has to be corrected
/// with the agency later.</para>
/// </summary>
public static class RemittanceReport
{
    public static ReportGrid Build(RemittanceSource source)
    {
        var scheme = source.Scheme;
        var period = new DateTime(source.Year, source.Month, 1);

        var subtitle =
            $"Applicable month {period:MMMM yyyy} · " +
            $"{scheme.EmployerNumberLabel} " +
            (string.IsNullOrWhiteSpace(scheme.EmployerNumber(source.Company))
                ? "not set"
                : scheme.EmployerNumber(source.Company));

        var fileStem = $"{scheme.FileStem}_{source.Year:0000}-{source.Month:00}";

        if (source.Payslips.Count == 0)
        {
            return ReportGrid.Empty(
                scheme.Title, subtitle,
                $"No payroll was run for {period:MMMM yyyy}, so there is nothing to remit.",
                fileStem);
        }

        var columns = BuildColumns(scheme);

        var byEmployee = source.Payslips
            .GroupBy(p => p.EmployeeId)
            .Select(g => g.OrderBy(p => p.PayDate).ThenBy(p => p.Id).ToList())

            // Surname first: it is the order every one of these forms is filed
            // in, and the order the agency's own listing comes back in.
            .OrderBy(g => g[^1].EmployeeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var rows = new List<ReportRow>(byEmployee.Count + 1);
        var missingNumbers = new List<string>();
        var unexplained = new List<string>();
        var exempt = new List<string>();

        foreach (var slips in byEmployee)
        {
            var latest = slips[^1];
            var contributed = Total(scheme, slips);

            var memberId = scheme.MemberId(latest);

            if (string.IsNullOrWhiteSpace(memberId))
                missingNumbers.Add(latest.EmployeeName);

            var isExempt = source.Employees.TryGetValue(latest.EmployeeId, out var employee) &&
                           scheme.IsExempt(employee);

            if (isExempt)
                exempt.Add(latest.EmployeeName);
            else if (contributed == 0m)
                unexplained.Add(latest.EmployeeName);

            rows.Add(BuildRow(scheme, slips, memberId, isExempt));
        }

        rows.Add(BuildTotalRow(scheme, source.Payslips, byEmployee.Count));

        var notes = Check(source, missingNumbers, unexplained, exempt);

        return new ReportGrid(scheme.Title, subtitle, columns, rows, notes, fileStem);
    }

    // =====================================================================

    private static List<ReportColumn> BuildColumns(RemittanceScheme scheme)
    {
        var columns = new List<ReportColumn>
        {
            new("employee_no", "Employee no.", ReportColumnKind.Text, 100),
            new("employee", "Employee", ReportColumnKind.Text, 190),
            new("member_id", scheme.MemberIdLabel, ReportColumnKind.Text, 140),
            new("basis", scheme.BasisLabel, ReportColumnKind.Money, 120)
        };

        foreach (var column in scheme.Columns)
        {
            if (column.Employee is not null)
                columns.Add(new ReportColumn($"ee:{column.Label}", $"{column.Label} — EE",
                    ReportColumnKind.Money, 110));

            if (column.Employer is not null)
                columns.Add(new ReportColumn($"er:{column.Label}", $"{column.Label} — ER",
                    ReportColumnKind.Money, 110));
        }

        columns.Add(new ReportColumn("total", "Total remitted", ReportColumnKind.Money, 124));
        columns.Add(new ReportColumn("status", "Status", ReportColumnKind.Text, 130));

        return columns;
    }

    private static ReportRow BuildRow(
        RemittanceScheme scheme, IReadOnlyList<Payslip> slips, string memberId, bool isExempt)
    {
        var latest = slips[^1];

        var cells = new List<ReportCell>
        {
            ReportCell.Of(latest.EmployeeNumber),
            ReportCell.Of(latest.EmployeeName),
            ReportCell.Of(FormatMemberId(scheme.Agency, memberId)),
            ReportCell.Money(scheme.Basis(slips))
        };

        foreach (var column in scheme.Columns)
        {
            if (column.Employee is not null)
                cells.Add(ReportCell.Money(slips.Sum(column.Employee)));

            if (column.Employer is not null)
                cells.Add(ReportCell.Money(slips.Sum(column.Employer)));
        }

        var total = Total(scheme, slips);
        cells.Add(ReportCell.Money(total));

        var status = string.IsNullOrWhiteSpace(memberId)
            ? "No number on file"
            : isExempt
                ? "Exempt"
                : total == 0m
                    ? "Nothing deducted"
                    : string.Empty;

        cells.Add(ReportCell.Of(status));

        return new ReportRow(cells, IsFlagged: status.Length > 0 && !isExempt);
    }

    private static ReportRow BuildTotalRow(
        RemittanceScheme scheme, IReadOnlyList<Payslip> payslips, int members)
    {
        var cells = new List<ReportCell>
        {
            ReportCell.Of(string.Empty),
            ReportCell.Of($"TOTAL — {members} member(s)"),
            ReportCell.Of(string.Empty),
            ReportCell.Blank
        };

        foreach (var column in scheme.Columns)
        {
            if (column.Employee is not null)
                cells.Add(ReportCell.Money(payslips.Sum(column.Employee)));

            if (column.Employer is not null)
                cells.Add(ReportCell.Money(payslips.Sum(column.Employer)));
        }

        cells.Add(ReportCell.Money(Total(scheme, payslips)));
        cells.Add(ReportCell.Of(string.Empty));

        return new ReportRow(cells, IsTotal: true);
    }

    private static decimal Total(RemittanceScheme scheme, IReadOnlyList<Payslip> slips) =>
        scheme.Columns.Sum(c =>
            (c.Employee is null ? 0m : slips.Sum(c.Employee)) +
            (c.Employer is null ? 0m : slips.Sum(c.Employer)));

    private static string FormatMemberId(RemittanceAgency agency, string stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
            return string.Empty;

        // A remittance form is the one place the full identifier belongs — it is
        // what the agency matches the payment against — so it is formatted
        // rather than masked (NFR-020).
        var kind = agency switch
        {
            RemittanceAgency.Sss => GovernmentIdKind.Sss,
            RemittanceAgency.PhilHealth => GovernmentIdKind.PhilHealth,
            RemittanceAgency.PagIbig => GovernmentIdKind.PagIbig,
            _ => GovernmentIdKind.Tin
        };

        return GovernmentId.Format(kind, stored);
    }

    // =====================================================================
    // Whether the form can honestly be filed
    // =====================================================================

    private static IReadOnlyList<ReportNote> Check(
        RemittanceSource source,
        IReadOnlyList<string> missingNumbers,
        IReadOnlyList<string> unexplained,
        IReadOnlyList<string> exempt)
    {
        var scheme = source.Scheme;
        var notes = new List<ReportNote>();

        // A run that is still recalculable is not a figure to file against.
        var contributing = source.Payslips
            .Select(p => p.PayrollRunId)
            .Distinct()
            .Select(id => source.Runs.TryGetValue(id, out var run) ? run : null)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        var unposted = contributing.Where(r => !r.IsPosted).ToList();

        if (unposted.Count > 0)
        {
            notes.Add(ReportNote.Blocking(
                "NOT FOR FILING: " +
                string.Join(", ", unposted.Select(r => $"{r.ReferenceNumber} is {r.StatusDisplay.ToLowerInvariant()}")) +
                ". These figures can still change."));
        }

        if (string.IsNullOrWhiteSpace(scheme.EmployerNumber(source.Company)))
        {
            notes.Add(ReportNote.Blocking(
                $"the company profile has no {scheme.EmployerNumberLabel}, so there is nothing to file " +
                "under. Set it in Payroll Setup."));
        }

        if (missingNumbers.Count > 0)
        {
            notes.Add(ReportNote.Warn(
                $"{missingNumbers.Count} member(s) have no {scheme.MemberIdLabel} on file and will be " +
                $"rejected by the agency: {Name(missingNumbers)}. They are kept on the form — leaving " +
                "them off would understate the amount due."));
        }

        if (unexplained.Count > 0)
        {
            notes.Add(ReportNote.Warn(
                $"{unexplained.Count} member(s) were paid this month but nothing was deducted, and they " +
                $"are not flagged exempt: {Name(unexplained)}. Either the exemption is missing from the " +
                "employee record or the deduction was missed."));
        }

        if (exempt.Count > 0)
        {
            notes.Add(ReportNote.Info(
                $"{exempt.Count} member(s) are listed with nil figures because they are flagged exempt: " +
                $"{Name(exempt)}. They are shown so the omission is visibly deliberate."));
        }

        foreach (var footnote in scheme.Footnotes)
            notes.Add(ReportNote.Info(footnote));

        if (notes.All(n => n.Severity != ReportNoteSeverity.Blocking))
        {
            notes.Insert(0, ReportNote.Info(
                $"Every contributing run is posted and the {scheme.EmployerNumberLabel} is on file — " +
                "this form is ready to file."));
        }

        return notes;
    }

    private static string Name(IReadOnlyList<string> names) =>
        names.Count <= 5
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(5)) + $" and {names.Count - 5} other(s)";
}
