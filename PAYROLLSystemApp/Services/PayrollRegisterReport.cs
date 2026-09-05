using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>Everything the register needs, read once by <see cref="ReportService"/>.</summary>
public sealed record RegisterSource(
    PayrollRun Run,
    IReadOnlyList<Payslip> Payslips,
    IReadOnlyDictionary<int, IReadOnlyList<PayslipLine>> Lines,
    IReadOnlyDictionary<string, string> ComponentNames,
    string DepartmentFilter);

/// <summary>
/// FR-080. The payroll register: every employee on a run, every earning, every
/// deduction, and what they were paid.
///
/// <para><b>The register carries its own proof.</b> It is the report the
/// remittance forms and the alphalist are checked against, so rather than being
/// verified once when it was written it re-checks itself on every render and
/// prints the result on its own face:</para>
///
/// <list type="number">
/// <item>its totals against the run's stored roll-ups,</item>
/// <item>each row's gross less deductions against that row's net,</item>
/// <item>each payslip's header totals against the sum of its own lines.</item>
/// </list>
///
/// <para>All three must come out at zero. When any does not, the report leads
/// with <b>"do not file or pay from this report"</b> and names the employees
/// involved — on the screen and in both files. A register that quietly disagrees
/// with the payroll it came from is worse than no register, and putting the
/// failure in a debug log leaves the one message that matters somewhere nobody
/// holding the printout will look.</para>
///
/// <para><b>Columns are discovered, not declared.</b> They are whichever line
/// codes actually occurred on the run, in the order they sit on a payslip. A
/// fixed column list would silently drop any code added to the engine later, and
/// a dropped deduction column is a register that does not add up.</para>
/// </summary>
public static class PayrollRegisterReport
{
    public static ReportGrid Build(RegisterSource source)
    {
        var run = source.Run;
        var payslips = source.Payslips;

        var subtitle =
            $"{run.ReferenceNumber} · {run.PeriodName} · {run.PeriodDisplay} · " +
            $"paid {run.PayDateDisplay} · {run.StatusDisplay}";

        if (!string.IsNullOrWhiteSpace(source.DepartmentFilter))
            subtitle += $" · {source.DepartmentFilter} only";

        var fileStem = $"payroll-register_{run.ReferenceNumber}";

        if (payslips.Count == 0)
        {
            return ReportGrid.Empty(
                "Payroll register", subtitle,
                "This run has no payslips for the selected scope. Calculate the run first.",
                fileStem);
        }

        var earningCodes = DiscoverCodes(source, PayslipLineKind.Earning);
        var deductionCodes = DiscoverCodes(source, PayslipLineKind.Deduction);

        var columns = BuildColumns(source, earningCodes, deductionCodes);
        var rows = new List<ReportRow>(payslips.Count + 1);

        foreach (var payslip in payslips)
            rows.Add(BuildRow(source, payslip, earningCodes, deductionCodes));

        rows.Add(BuildTotalRow(source, earningCodes, deductionCodes));

        var notes = Reconcile(source, earningCodes, deductionCodes);

        return new ReportGrid("Payroll register", subtitle, columns, rows, notes, fileStem);
    }

    // =====================================================================
    // Columns
    // =====================================================================

    /// <summary>
    /// The codes that occurred, in payslip order. Ordering by the lowest
    /// sequence a code was seen at reproduces the order the payslip itself
    /// prints them in, so the register reads like the documents it summarises.
    /// </summary>
    private static IReadOnlyList<string> DiscoverCodes(RegisterSource source, PayslipLineKind kind)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var lines in source.Lines.Values)
        {
            foreach (var line in lines.Where(l => l.Kind == kind))
            {
                if (!seen.TryGetValue(line.Code, out var sequence) || line.Sequence < sequence)
                    seen[line.Code] = line.Sequence;
            }
        }

        return seen.OrderBy(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key)
            .ToList();
    }

    private static List<ReportColumn> BuildColumns(
        RegisterSource source, IReadOnlyList<string> earnings, IReadOnlyList<string> deductions)
    {
        var columns = new List<ReportColumn>
        {
            new("employee_no", "Employee no.", ReportColumnKind.Text, 100),
            new("employee", "Employee", ReportColumnKind.Text, 180),
            new("department", "Department", ReportColumnKind.Text, 130),
            new("days", "Days", ReportColumnKind.Number, 60)
        };

        columns.AddRange(earnings.Select(code =>
            new ReportColumn($"e:{code}", Heading(source, code), ReportColumnKind.Money, 108)));

        columns.Add(new ReportColumn("gross", "Gross pay", ReportColumnKind.Money, 112));

        columns.AddRange(deductions.Select(code =>
            new ReportColumn($"d:{code}", Heading(source, code), ReportColumnKind.Money, 108)));

        columns.Add(new ReportColumn("deductions", "Total deductions", ReportColumnKind.Money, 118));
        columns.Add(new ReportColumn("net", "Net pay", ReportColumnKind.Money, 112));

        columns.Add(new ReportColumn("er_sss", "ER SSS", ReportColumnKind.Money, 96));
        columns.Add(new ReportColumn("er_wisp", "ER WISP", ReportColumnKind.Money, 92));
        columns.Add(new ReportColumn("er_ec", "ER EC", ReportColumnKind.Money, 84));
        columns.Add(new ReportColumn("er_phic", "ER PhilHealth", ReportColumnKind.Money, 108));
        columns.Add(new ReportColumn("er_hdmf", "ER Pag-IBIG", ReportColumnKind.Money, 104));

        return columns;
    }

    /// <summary>
    /// What a code's column is called.
    ///
    /// <para>Headings come from the configured component names, <b>not</b> from
    /// the payslip lines. A line's own description carries per-employee detail —
    /// <c>Late (35 min)</c> — so using it as a heading would print one
    /// employee's figures under another's number. Where a code has no configured
    /// name, the shortest description seen is used with its trailing
    /// parenthetical stripped; failing that, the bare code. Ugly beats wrong.</para>
    /// </summary>
    private static string Heading(RegisterSource source, string code)
    {
        if (source.ComponentNames.TryGetValue(code, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        var descriptions = source.Lines.Values
            .SelectMany(l => l)
            .Where(l => l.Code == code)
            .Select(l => StripDetail(l.Name))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n.Length)
            .ToList();

        return descriptions.Count > 0 ? descriptions[0] : code;
    }

    private static string StripDetail(string name)
    {
        var open = name.IndexOf(" (", StringComparison.Ordinal);

        return open > 0 ? name[..open].Trim() : name.Trim();
    }

    // =====================================================================
    // Rows
    // =====================================================================

    private static ReportRow BuildRow(
        RegisterSource source, Payslip payslip,
        IReadOnlyList<string> earnings, IReadOnlyList<string> deductions)
    {
        var lines = source.Lines.TryGetValue(payslip.Id, out var found) ? found : [];

        var cells = new List<ReportCell>
        {
            ReportCell.Of(payslip.EmployeeNumber),
            ReportCell.Of(payslip.EmployeeName),
            ReportCell.Of(payslip.DepartmentName),
            ReportCell.Number(payslip.DaysWorked)
        };

        cells.AddRange(earnings.Select(code => ReportCell.Money(Amount(lines, PayslipLineKind.Earning, code))));
        cells.Add(ReportCell.Money(payslip.GrossPay));

        cells.AddRange(deductions.Select(code => ReportCell.Money(Amount(lines, PayslipLineKind.Deduction, code))));
        cells.Add(ReportCell.Money(payslip.TotalDeductions));
        cells.Add(ReportCell.Money(payslip.NetPay));

        cells.Add(ReportCell.Money(payslip.EmployerSss));
        cells.Add(ReportCell.Money(payslip.EmployerSssWisp));
        cells.Add(ReportCell.Money(payslip.EmployerEc));
        cells.Add(ReportCell.Money(payslip.EmployerPhilHealth));
        cells.Add(ReportCell.Money(payslip.EmployerPagIbig));

        return new ReportRow(cells, IsFlagged: payslip.IsFlaggedForReview);
    }

    private static ReportRow BuildTotalRow(
        RegisterSource source, IReadOnlyList<string> earnings, IReadOnlyList<string> deductions)
    {
        var payslips = source.Payslips;
        var allLines = source.Lines.Values.SelectMany(l => l).ToList();

        var cells = new List<ReportCell>
        {
            ReportCell.Of(string.Empty),
            ReportCell.Of($"TOTAL — {payslips.Count} employee(s)"),
            ReportCell.Of(string.Empty),
            ReportCell.Number(payslips.Sum(p => p.DaysWorked))
        };

        cells.AddRange(earnings.Select(code =>
            ReportCell.Money(allLines
                .Where(l => l.Kind == PayslipLineKind.Earning && l.Code == code)
                .Sum(l => l.Amount))));

        cells.Add(ReportCell.Money(payslips.Sum(p => p.GrossPay)));

        cells.AddRange(deductions.Select(code =>
            ReportCell.Money(allLines
                .Where(l => l.Kind == PayslipLineKind.Deduction && l.Code == code)
                .Sum(l => l.Amount))));

        cells.Add(ReportCell.Money(payslips.Sum(p => p.TotalDeductions)));
        cells.Add(ReportCell.Money(payslips.Sum(p => p.NetPay)));

        cells.Add(ReportCell.Money(payslips.Sum(p => p.EmployerSss)));
        cells.Add(ReportCell.Money(payslips.Sum(p => p.EmployerSssWisp)));
        cells.Add(ReportCell.Money(payslips.Sum(p => p.EmployerEc)));
        cells.Add(ReportCell.Money(payslips.Sum(p => p.EmployerPhilHealth)));
        cells.Add(ReportCell.Money(payslips.Sum(p => p.EmployerPagIbig)));

        return new ReportRow(cells, IsTotal: true);
    }

    private static decimal? Amount(IReadOnlyList<PayslipLine> lines, PayslipLineKind kind, string code)
    {
        var matching = lines.Where(l => l.Kind == kind && l.Code == code).ToList();

        // Blank, not zero: "no SSS loan" and "an SSS loan that took nothing this
        // cut-off" are different statements.
        return matching.Count == 0 ? null : matching.Sum(l => l.Amount);
    }

    // =====================================================================
    // The three checks
    // =====================================================================

    private static IReadOnlyList<ReportNote> Reconcile(
        RegisterSource source, IReadOnlyList<string> earnings, IReadOnlyList<string> deductions)
    {
        var notes = new List<ReportNote>();
        var run = source.Run;
        var payslips = source.Payslips;
        var isPartial = !string.IsNullOrWhiteSpace(source.DepartmentFilter);

        // ---- 1. against the run's stored roll-ups -----------------------
        if (isPartial)
        {
            notes.Add(ReportNote.Info(
                $"Filtered to {source.DepartmentFilter}. This is part of {run.ReferenceNumber}, " +
                "so it has not been checked against the run's own totals."));
        }
        else
        {
            var variances = new List<string>();

            Compare(variances, "gross", payslips.Sum(p => p.GrossPay), run.TotalGross);
            Compare(variances, "deductions", payslips.Sum(p => p.TotalDeductions), run.TotalDeductions);
            Compare(variances, "net", payslips.Sum(p => p.NetPay), run.TotalNet);
            Compare(variances, "employer share", payslips.Sum(p => p.TotalEmployerShare), run.TotalEmployerShare);

            if (payslips.Count != run.EmployeeCount)
                variances.Add($"headcount {payslips.Count} against {run.EmployeeCount}");

            if (variances.Count > 0)
            {
                notes.Add(ReportNote.Blocking(
                    $"the register does not agree with {run.ReferenceNumber}: " +
                    string.Join("; ", variances) + "."));
            }
        }

        // ---- 2. gross - deductions = net, per row -----------------------
        var brokenRows = payslips
            .Where(p => PayrollRounding.Money(p.GrossPay - p.TotalDeductions) != PayrollRounding.Money(p.NetPay))
            .Select(p => p.EmployeeName)
            .ToList();

        if (brokenRows.Count > 0)
        {
            notes.Add(ReportNote.Blocking(
                "gross less deductions does not equal net pay for " + Name(brokenRows) + "."));
        }

        // ---- 3. header totals against the payslip's own lines -----------
        var brokenSlips = new List<string>();

        foreach (var payslip in payslips)
        {
            var lines = source.Lines.TryGetValue(payslip.Id, out var found) ? found : [];

            var lineEarnings = lines.Where(l => l.Kind == PayslipLineKind.Earning).Sum(l => l.Amount);
            var lineDeductions = lines.Where(l => l.Kind == PayslipLineKind.Deduction).Sum(l => l.Amount);

            if (PayrollRounding.Money(lineEarnings) != PayrollRounding.Money(payslip.TotalEarnings) ||
                PayrollRounding.Money(lineDeductions) != PayrollRounding.Money(payslip.TotalDeductions))
            {
                brokenSlips.Add(payslip.EmployeeName);
            }
        }

        if (brokenSlips.Count > 0)
        {
            notes.Add(ReportNote.Blocking(
                "the payslip totals do not equal the sum of their own lines for " + Name(brokenSlips) + "."));
        }

        // ---- the columns must add back to the header figures ------------
        var allLines = source.Lines.Values.SelectMany(l => l).ToList();

        var earningColumns = allLines
            .Where(l => l.Kind == PayslipLineKind.Earning && earnings.Contains(l.Code))
            .Sum(l => l.Amount);

        var deductionColumns = allLines
            .Where(l => l.Kind == PayslipLineKind.Deduction && deductions.Contains(l.Code))
            .Sum(l => l.Amount);

        if (PayrollRounding.Money(earningColumns) != PayrollRounding.Money(payslips.Sum(p => p.TotalEarnings)) ||
            PayrollRounding.Money(deductionColumns) != PayrollRounding.Money(payslips.Sum(p => p.TotalDeductions)))
        {
            notes.Add(ReportNote.Blocking(
                "the earning and deduction columns do not add back to the totals, so a pay code is missing " +
                "a column."));
        }

        // ---- provisional, and exceptions --------------------------------
        if (!run.IsPosted)
        {
            notes.Add(ReportNote.Warn(
                $"{run.ReferenceNumber} is {run.StatusDisplay.ToLowerInvariant()}, not posted. " +
                "These figures can still be recalculated — treat the register as provisional."));
        }

        var flagged = payslips.Where(p => p.IsFlaggedForReview).Select(p => p.EmployeeName).ToList();

        if (flagged.Count > 0)
        {
            notes.Add(ReportNote.Warn(
                $"{flagged.Count} payslip(s) are flagged for review: " + Name(flagged) + "."));
        }

        if (notes.All(n => n.Severity != ReportNoteSeverity.Blocking) && !isPartial)
        {
            notes.Add(ReportNote.Info(
                $"Reconciled against {run.ReferenceNumber}: totals, per-row net, and every payslip's own " +
                "lines all agree exactly."));
        }

        return notes;
    }

    private static void Compare(List<string> variances, string what, decimal register, decimal run)
    {
        var difference = PayrollRounding.Money(register - run);

        if (difference != 0m)
            variances.Add($"{what} {PayrollRounding.Format(register)} against {PayrollRounding.Format(run)}");
    }

    /// <summary>Names the people involved, and stops before the list becomes a page.</summary>
    private static string Name(IReadOnlyList<string> names) =>
        names.Count <= 5
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(5)) + $" and {names.Count - 5} other(s)";
}
