using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Security;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// FR-082, FR-083, FR-084. The three reports that are neither a register nor a
/// remittance form: the bank disbursement file, the attendance summary, and the
/// year-end alphalist.
/// </summary>
public static class SummaryReports
{
    // =====================================================================
    // FR-082 — bank disbursement file
    // =====================================================================

    /// <summary>
    /// FR-082. The instruction to the bank: who to pay, into which account, how
    /// much.
    ///
    /// <para><b>The layout is generic, not a named bank's.</b> Every bank
    /// publishes its own fixed-width or delimited format, and inventing one
    /// bank's on the strength of a guess would produce a file that is rejected
    /// on upload. This carries the five fields every layout needs, in an order a
    /// bank's own template can be filled from, and says so on the report.</para>
    ///
    /// <para><b>Account numbers are printed in full.</b> NFR-020 masks them in
    /// list views; a disbursement instruction is the one place the full number
    /// is the point of the document.</para>
    /// </summary>
    public static ReportGrid BankFile(PayrollRun run, IReadOnlyList<Payslip> payslips)
    {
        var subtitle =
            $"{run.ReferenceNumber} · {run.PeriodDisplay} · value date {run.PayDateDisplay} · " +
            run.StatusDisplay;

        var fileStem = $"bank-disbursement_{run.ReferenceNumber}";

        if (payslips.Count == 0)
        {
            return ReportGrid.Empty("Bank disbursement file", subtitle,
                "This run has no payslips, so there is nothing to disburse.", fileStem);
        }

        List<ReportColumn> columns =
        [
            new("employee_no", "Employee no.", ReportColumnKind.Text, 100),
            new("employee", "Account name", ReportColumnKind.Text, 210),
            new("bank", "Bank", ReportColumnKind.Text, 150),
            new("account", "Account number", ReportColumnKind.Text, 170),
            new("amount", "Amount", ReportColumnKind.Money, 120),
            new("reference", "Reference", ReportColumnKind.Text, 150)
        ];

        var ordered = payslips
            .OrderBy(p => p.EmployeeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var rows = new List<ReportRow>(ordered.Count + 1);
        var unpayable = new List<string>();
        var zero = new List<string>();

        foreach (var payslip in ordered)
        {
            var hasAccount = !string.IsNullOrWhiteSpace(payslip.BankAccountNumber);

            if (!hasAccount)
                unpayable.Add(payslip.EmployeeName);

            if (payslip.NetPay <= 0m)
                zero.Add(payslip.EmployeeName);

            rows.Add(new ReportRow(
            [
                ReportCell.Of(payslip.EmployeeNumber),
                ReportCell.Of(payslip.EmployeeName),
                ReportCell.Of(payslip.BankName),
                ReportCell.Of(payslip.BankAccountNumber),
                ReportCell.Money(payslip.NetPay),
                ReportCell.Of(run.ReferenceNumber)
            ], IsFlagged: !hasAccount || payslip.NetPay <= 0m));
        }

        rows.Add(new ReportRow(
        [
            ReportCell.Of(string.Empty),
            ReportCell.Of($"TOTAL — {ordered.Count} account(s)"),
            ReportCell.Of(string.Empty),
            ReportCell.Of(string.Empty),
            ReportCell.Money(ordered.Sum(p => p.NetPay)),
            ReportCell.Of(string.Empty)
        ], IsTotal: true));

        var notes = new List<ReportNote>
        {
            ReportNote.Info(
                "This is a generic disbursement layout, not any particular bank's file format. " +
                "Name the bank and its published layout to have the file generated in it directly.")
        };

        if (!run.IsPosted)
        {
            notes.Add(ReportNote.Blocking(
                $"{run.ReferenceNumber} is {run.StatusDisplay.ToLowerInvariant()}, not posted. " +
                "Do not send this to the bank — the figures can still be recalculated."));
        }

        if (unpayable.Count > 0)
        {
            notes.Add(ReportNote.Blocking(
                $"{unpayable.Count} employee(s) have no bank account on file and cannot be paid " +
                $"by transfer: {Name(unpayable)}."));
        }

        if (zero.Count > 0)
        {
            notes.Add(ReportNote.Warn(
                $"{zero.Count} employee(s) have nothing or less than nothing to receive: {Name(zero)}. " +
                "Remove them from the file before it is uploaded."));
        }

        return new ReportGrid("Bank disbursement file", subtitle, columns, rows, notes, fileStem);
    }

    // =====================================================================
    // FR-083 — attendance and absences
    // =====================================================================

    /// <summary>
    /// FR-083. Attendance per employee over a range, ordered by department.
    ///
    /// <para>The figures come from <see cref="AttendanceSummary"/> — the same
    /// computation payroll reads — rather than being counted again here, so the
    /// report cannot disagree with what the employee was paid for.</para>
    /// </summary>
    public static ReportGrid Attendance(
        IReadOnlyList<AttendanceSummary> summaries,
        IReadOnlyDictionary<int, string> departments,
        DateTime from,
        DateTime to,
        string departmentFilter)
    {
        var subtitle = $"{from:dd MMM yyyy} – {to:dd MMM yyyy}" +
                       (string.IsNullOrWhiteSpace(departmentFilter) ? "" : $" · {departmentFilter}");

        var fileStem = $"attendance_{from:yyyy-MM-dd}_{to:yyyy-MM-dd}";

        if (summaries.Count == 0)
        {
            return ReportGrid.Empty("Attendance and absences", subtitle,
                "No employees match this selection.", fileStem);
        }

        List<ReportColumn> columns =
        [
            new("employee_no", "Employee no.", ReportColumnKind.Text, 100),
            new("employee", "Employee", ReportColumnKind.Text, 190),
            new("department", "Department", ReportColumnKind.Text, 140),
            new("present", "Present", ReportColumnKind.Number, 74),
            new("half", "Half day", ReportColumnKind.Number, 74),
            new("leave", "On leave", ReportColumnKind.Number, 76),
            new("absent", "Absent", ReportColumnKind.Number, 70),
            new("holiday", "Holiday", ReportColumnKind.Number, 72),
            new("restday", "Rest day", ReportColumnKind.Number, 76),
            new("unrecorded", "No record", ReportColumnKind.Number, 80),
            new("regular", "Regular hrs", ReportColumnKind.Number, 92),
            new("ot", "OT approved", ReportColumnKind.Number, 96),
            new("ot_pending", "OT pending", ReportColumnKind.Number, 92),
            new("nd", "Night diff hrs", ReportColumnKind.Number, 98),
            new("late", "Late min", ReportColumnKind.Number, 78),
            new("undertime", "Undertime min", ReportColumnKind.Number, 100)
        ];

        var ordered = summaries
            .OrderBy(s => Department(departments, s), StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.EmployeeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var rows = ordered.Select(s => new ReportRow(
        [
            ReportCell.Of(s.EmployeeNumber),
            ReportCell.Of(s.EmployeeName),
            ReportCell.Of(Department(departments, s)),
            ReportCell.Count(s.DaysPresent),
            ReportCell.Count(s.DaysHalf),
            ReportCell.Count(s.DaysOnLeave),
            ReportCell.Count(s.DaysAbsent),
            ReportCell.Count(s.DaysHoliday),
            ReportCell.Count(s.DaysRestDay),
            ReportCell.Count(s.DaysUnrecorded),
            ReportCell.Number(s.RegularHours),
            ReportCell.Number(s.OvertimeApproved),
            ReportCell.Number(s.OvertimePending),
            ReportCell.Number(s.NightDifferentialHours),
            ReportCell.Count(s.LateMinutes),
            ReportCell.Count(s.UndertimeMinutes)
        ], IsFlagged: s.IsIncomplete)).ToList();

        rows.Add(new ReportRow(
        [
            ReportCell.Of(string.Empty),
            ReportCell.Of($"TOTAL — {ordered.Count} employee(s)"),
            ReportCell.Of(string.Empty),
            ReportCell.Count(ordered.Sum(s => s.DaysPresent)),
            ReportCell.Count(ordered.Sum(s => s.DaysHalf)),
            ReportCell.Count(ordered.Sum(s => s.DaysOnLeave)),
            ReportCell.Count(ordered.Sum(s => s.DaysAbsent)),
            ReportCell.Count(ordered.Sum(s => s.DaysHoliday)),
            ReportCell.Count(ordered.Sum(s => s.DaysRestDay)),
            ReportCell.Count(ordered.Sum(s => s.DaysUnrecorded)),
            ReportCell.Number(ordered.Sum(s => s.RegularHours)),
            ReportCell.Number(ordered.Sum(s => s.OvertimeApproved)),
            ReportCell.Number(ordered.Sum(s => s.OvertimePending)),
            ReportCell.Number(ordered.Sum(s => s.NightDifferentialHours)),
            ReportCell.Count(ordered.Sum(s => s.LateMinutes)),
            ReportCell.Count(ordered.Sum(s => s.UndertimeMinutes))
        ], IsTotal: true));

        var notes = new List<ReportNote>();

        var incomplete = ordered.Where(s => s.IsIncomplete).Select(s => s.EmployeeName).ToList();

        if (incomplete.Count > 0)
        {
            notes.Add(ReportNote.Warn(
                $"{incomplete.Count} employee(s) have days in this range with no record at all: " +
                $"{Name(incomplete)}. An incomplete cut-off, not a wrong one, is the usual cause of " +
                "a short payslip."));
        }

        var pending = ordered.Where(s => s.HasPendingOvertime).Select(s => s.EmployeeName).ToList();

        if (pending.Count > 0)
        {
            notes.Add(ReportNote.Warn(
                $"{pending.Count} employee(s) have overtime that nobody has authorised, and so is " +
                $"unpaid: {Name(pending)}."));
        }

        return new ReportGrid("Attendance and absences", subtitle, columns, rows, notes, fileStem);
    }

    private static string Department(IReadOnlyDictionary<int, string> departments, AttendanceSummary summary) =>
        summary.Employee.DepartmentId is { } id && departments.TryGetValue(id, out var name)
            ? name
            : string.Empty;

    // =====================================================================
    // FR-084 — annual alphalist / employee tax summary
    // =====================================================================

    /// <summary>
    /// FR-084. One row per employee for a calendar year: what they earned, what
    /// was exempt, what was taxable, and what was withheld.
    ///
    /// <para>This is the data behind the BIR alphalist and behind each
    /// employee's 2316. It reads <b>posted runs only</b> — a year-end
    /// certificate is a statement of what was actually paid — and it is bounded
    /// by <b>pay date</b>, because that is the order money reached the employee
    /// and the order the 2316 adds it up in.</para>
    ///
    /// <para><b>Tax due is not recomputed here.</b> Where the year was settled by
    /// the December run's annualisation, the year's withholding already equals
    /// the year's tax; where it was not, the difference is real and is the
    /// employee's to settle, so the report shows what was withheld rather than
    /// quietly substituting a figure of its own.</para>
    /// </summary>
    public static ReportGrid Alphalist(
        int year,
        IReadOnlyList<Payslip> payslips,
        IReadOnlyDictionary<int, decimal> thirteenthMonthByEmployee,
        IReadOnlyDictionary<int, Employee> employees,
        bool annualisationEnabled)
    {
        var subtitle = $"Calendar year {year} · posted payroll only";
        var fileStem = $"alphalist_{year}";

        if (payslips.Count == 0)
        {
            return ReportGrid.Empty("Annual alphalist", subtitle,
                $"No payroll was posted in {year}.", fileStem);
        }

        List<ReportColumn> columns =
        [
            new("employee_no", "Employee no.", ReportColumnKind.Text, 100),
            new("employee", "Employee", ReportColumnKind.Text, 190),
            new("tin", "TIN", ReportColumnKind.Text, 140),
            new("status", "Status", ReportColumnKind.Text, 110),
            new("gross", "Gross compensation", ReportColumnKind.Money, 132),
            new("nontaxable", "Non-taxable", ReportColumnKind.Money, 116),
            new("thirteenth", "13th month & other benefits", ReportColumnKind.Money, 156),
            new("sss", "SSS", ReportColumnKind.Money, 100),
            new("phic", "PhilHealth", ReportColumnKind.Money, 108),
            new("hdmf", "Pag-IBIG", ReportColumnKind.Money, 104),
            new("taxable", "Taxable income", ReportColumnKind.Money, 124),
            new("tax", "Tax withheld", ReportColumnKind.Money, 118)
        ];

        var byEmployee = payslips
            .GroupBy(p => p.EmployeeId)
            .Select(g => g.OrderBy(p => p.PayDate).ThenBy(p => p.Id).ToList())
            .OrderBy(g => g[^1].EmployeeName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var rows = new List<ReportRow>(byEmployee.Count + 1);
        var missingTin = new List<string>();

        foreach (var slips in byEmployee)
        {
            var latest = slips[^1];
            var thirteenth = thirteenthMonthByEmployee.TryGetValue(latest.EmployeeId, out var amount)
                ? amount
                : 0m;

            if (string.IsNullOrWhiteSpace(latest.Tin))
                missingTin.Add(latest.EmployeeName);

            var status = employees.TryGetValue(latest.EmployeeId, out var employee)
                ? employee.StatusDisplay
                : string.Empty;

            rows.Add(new ReportRow(
            [
                ReportCell.Of(latest.EmployeeNumber),
                ReportCell.Of(latest.EmployeeName),
                ReportCell.Of(GovernmentId.Format(GovernmentIdKind.Tin, latest.Tin)),
                ReportCell.Of(status),
                ReportCell.Money(slips.Sum(p => p.GrossPay)),
                ReportCell.Money(slips.Sum(p => p.NonTaxableEarnings)),
                ReportCell.Money(thirteenth),
                ReportCell.Money(slips.Sum(p => p.EmployeeSss + p.EmployeeSssWisp)),
                ReportCell.Money(slips.Sum(p => p.EmployeePhilHealth)),
                ReportCell.Money(slips.Sum(p => p.EmployeePagIbig)),
                ReportCell.Money(slips.Sum(p => p.TaxableIncome)),
                ReportCell.Money(slips.Sum(p => p.WithholdingTax))
            ], IsFlagged: string.IsNullOrWhiteSpace(latest.Tin)));
        }

        rows.Add(new ReportRow(
        [
            ReportCell.Of(string.Empty),
            ReportCell.Of($"TOTAL — {byEmployee.Count} employee(s)"),
            ReportCell.Of(string.Empty),
            ReportCell.Of(string.Empty),
            ReportCell.Money(payslips.Sum(p => p.GrossPay)),
            ReportCell.Money(payslips.Sum(p => p.NonTaxableEarnings)),
            ReportCell.Money(thirteenthMonthByEmployee.Values.Sum()),
            ReportCell.Money(payslips.Sum(p => p.EmployeeSss + p.EmployeeSssWisp)),
            ReportCell.Money(payslips.Sum(p => p.EmployeePhilHealth)),
            ReportCell.Money(payslips.Sum(p => p.EmployeePagIbig)),
            ReportCell.Money(payslips.Sum(p => p.TaxableIncome)),
            ReportCell.Money(payslips.Sum(p => p.WithholdingTax))
        ], IsTotal: true));

        var notes = new List<ReportNote>();

        if (missingTin.Count > 0)
        {
            notes.Add(ReportNote.Blocking(
                $"{missingTin.Count} employee(s) have no TIN on file and cannot appear on an alphalist: " +
                $"{Name(missingTin)}."));
        }

        notes.Add(annualisationEnabled
            ? ReportNote.Info(
                "Year-end annualisation is on, so for anyone whose December run settled the year the " +
                "tax withheld already equals the tax due — which is the central figure of their 2316.")
            : ReportNote.Warn(
                "Year-end annualisation is off, so tax withheld is the sum of the per-period tables and " +
                "may differ from the annual tax due. Settle the difference outside payroll."));

        notes.Add(ReportNote.Info(
            "Taxable income is the base each period's withholding was read against, after the statutory " +
            "shares came off. A period whose deductions exceeded its taxable pay contributes its " +
            "negative figure, which is what keeps the year's total right."));

        return new ReportGrid("Annual alphalist", subtitle, columns, rows, notes, fileStem);
    }

    // =====================================================================

    private static string Name(IReadOnlyList<string> names) =>
        names.Count <= 5
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(5)) + $" and {names.Count - 5} other(s)";
}
