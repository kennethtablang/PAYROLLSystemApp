using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// FR-074. One employee's year to the pay date of the payslip being printed,
/// inclusive.
///
/// <para>Inclusive because a payslip's year-to-date column is what an employee
/// checks their own December total against, and a figure that stopped at the
/// previous period would never reconcile with the BIR 2316 they are handed in
/// January.</para>
/// </summary>
public sealed record PayslipYearToDate(
    decimal GrossPay,
    decimal TaxableIncome,
    decimal TaxWithheld,
    decimal Sss,
    decimal PhilHealth,
    decimal PagIbig,
    decimal ThirteenthMonth)
{
    public static PayslipYearToDate Empty { get; } = new(0m, 0m, 0m, 0m, 0m, 0m, 0m);

    public decimal TotalContributions => Sss + PhilHealth + PagIbig;
}

/// <summary>
/// FR-070, FR-071. Renders payslips to PDF.
///
/// <para><b>Everything printed comes off the payslip row and its lines</b>, not
/// from the employee record or the configuration as they stand today. The
/// figures were snapshotted when the run was calculated, and a payslip
/// reprinted in three years has to come out identical to the one that was
/// handed over (NFR-009). The only thing read live is the company profile, which
/// is the employer's own letterhead rather than a fact about the pay.</para>
/// </summary>
public static class PayslipDocument
{
    /// <summary>One payslip, one page.</summary>
    public static byte[] Render(
        CompanyProfile company, Payslip payslip, IReadOnlyList<PayslipLine> lines,
        PayslipYearToDate yearToDate) =>
        Render(company, [(payslip, lines, yearToDate)]);

    /// <summary>
    /// A batch — a whole run's payslips, one to a page. The same routine draws
    /// one as draws three hundred, so a bulk export cannot drift away from what
    /// a single payslip looks like.
    /// </summary>
    public static byte[] Render(
        CompanyProfile company,
        IReadOnlyList<(Payslip Payslip, IReadOnlyList<PayslipLine> Lines, PayslipYearToDate YearToDate)> payslips)
    {
        var writer = new PdfWriter();

        for (var i = 0; i < payslips.Count; i++)
        {
            if (i > 0)
                writer.NewPage();

            var (payslip, lines, ytd) = payslips[i];
            DrawOne(writer, company, payslip, lines, ytd);
        }

        return writer.Build();
    }

    // =====================================================================

    private static void DrawOne(
        PdfWriter writer, CompanyProfile company, Payslip payslip,
        IReadOnlyList<PayslipLine> lines, PayslipYearToDate ytd)
    {
        var page = new PdfPageBuilder(writer);

        DrawHeader(page, company, payslip);
        DrawEmployee(page, payslip);
        DrawTime(page, payslip);

        var earnings = lines.Where(l => l.IsEarning).ToList();
        var deductions = lines.Where(l => l.IsDeduction).ToList();
        var information = lines.Where(l => l.IsInformation).ToList();

        DrawSection(page, "EARNINGS", earnings, payslip.TotalEarnings, "Total earnings");
        DrawSection(page, "DEDUCTIONS", deductions, payslip.TotalDeductions, "Total deductions");

        DrawNetPay(page, payslip);

        if (information.Count > 0)
            DrawInformation(page, information);

        DrawYearToDate(page, ytd);
        DrawEmployerShare(page, payslip);
        DrawFooter(page, payslip);
    }

    private static void DrawHeader(PdfPageBuilder page, CompanyProfile company, Payslip payslip)
    {
        page.Text(page.Left, string.IsNullOrWhiteSpace(company.RegisteredName)
            ? "(company profile not set)"
            : company.DisplayName, 13, bold: true);

        page.TextRight(page.Right, "PAYSLIP", 13, bold: true);
        page.Down(13);

        if (!string.IsNullOrWhiteSpace(company.Address))
        {
            page.Text(page.Left, company.Address, 8);
            page.Down(10);
        }

        var registration = new List<string>();

        if (!string.IsNullOrWhiteSpace(company.Tin))
            registration.Add($"TIN {company.TinDisplay}");

        if (!string.IsNullOrWhiteSpace(company.SssEmployerNumber))
            registration.Add($"SSS {company.SssEmployerNumber}");

        if (!string.IsNullOrWhiteSpace(company.PagIbigEmployerNumber))
            registration.Add($"Pag-IBIG {company.PagIbigEmployerNumber}");

        if (registration.Count > 0)
        {
            page.Text(page.Left, string.Join("   ", registration), 8);
            page.Down(10);
        }

        // The currency is stated once, because the peso sign has no place in the
        // WinAnsi encoding the base-14 fonts use. See PdfWriter.Sanitise.
        page.TextRight(page.Right, "All amounts in Philippine pesos (PHP)", 7.5);
        page.Down(6);

        page.Rule(0.4, 1);
        page.Down(16);
    }

    private static void DrawEmployee(PdfPageBuilder page, Payslip payslip)
    {
        page.Text(page.Left, payslip.EmployeeName, 11, bold: true);
        page.TextRight(page.Right, $"Pay period {payslip.PeriodCode}", 9, bold: true);
        page.Down(12);

        var role = string.Join(" / ", new[] { payslip.PositionTitle, payslip.DepartmentName }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        page.Text(page.Left, $"Employee no. {payslip.EmployeeNumber}", 8);
        page.TextRight(page.Right,
            $"{payslip.PeriodStart:dd MMM yyyy} - {payslip.PeriodEnd:dd MMM yyyy}", 8);
        page.Down(10);

        if (role.Length > 0)
        {
            page.Text(page.Left, role, 8);
            page.TextRight(page.Right, $"Paid {payslip.PayDate:dd MMM yyyy}", 8);
            page.Down(10);
        }

        // The employee's own identifiers, in full. NFR-020's masking is for lists
        // where many people's numbers appear together; this is the employee's own
        // document, and their TIN is what a BIR form will ask them for.
        var ids = new List<string>();

        if (!string.IsNullOrWhiteSpace(payslip.Tin))
            ids.Add($"TIN {Security.GovernmentId.Format(Security.GovernmentIdKind.Tin, payslip.Tin)}");

        if (!string.IsNullOrWhiteSpace(payslip.SssNumber))
            ids.Add($"SSS {Security.GovernmentId.Format(Security.GovernmentIdKind.Sss, payslip.SssNumber)}");

        if (!string.IsNullOrWhiteSpace(payslip.PhilHealthNumber))
            ids.Add($"PhilHealth {Security.GovernmentId.Format(Security.GovernmentIdKind.PhilHealth, payslip.PhilHealthNumber)}");

        if (!string.IsNullOrWhiteSpace(payslip.PagIbigNumber))
            ids.Add($"Pag-IBIG {Security.GovernmentId.Format(Security.GovernmentIdKind.PagIbig, payslip.PagIbigNumber)}");

        if (ids.Count > 0)
        {
            page.Text(page.Left, string.Join("   ", ids), 7.5);
            page.Down(10);
        }

        page.Down(6);
    }

    /// <summary>
    /// The rate the pay was worked out from, and the time behind it. Without
    /// these a payslip states a figure and gives no way to check it.
    /// </summary>
    private static void DrawTime(PdfPageBuilder page, Payslip payslip)
    {
        page.Band(13);
        page.Text(page.Left + 4, "BASIS", 7.5, bold: true);
        page.Down(14);

        var rate = payslip.PayType switch
        {
            PayType.Monthly => $"Monthly rate {Money(payslip.BasicRate)}",
            PayType.Daily => $"Daily rate {Money(payslip.BasicRate)}",
            _ => $"Hourly rate {Money(payslip.BasicRate)}"
        };

        page.Text(page.Left + 4,
            $"{rate}   Daily {Money(payslip.DailyRate)}   Hourly {Money(payslip.HourlyRate)}   " +
            $"Factor {payslip.WorkingDaysFactor:0.##}", 8);
        page.Down(10);

        var time = new List<string>
        {
            $"Days worked {payslip.DaysWorked:0.##}",
            $"Regular hours {payslip.RegularHours:0.##}"
        };

        if (payslip.OvertimeHours > 0) time.Add($"Overtime {payslip.OvertimeHours:0.##} h");
        if (payslip.NightDifferentialHours > 0) time.Add($"Night {payslip.NightDifferentialHours:0.##} h");
        if (payslip.LateMinutes > 0) time.Add($"Late {payslip.LateMinutes} min");
        if (payslip.UndertimeMinutes > 0) time.Add($"Undertime {payslip.UndertimeMinutes} min");
        if (payslip.AbsentDays > 0) time.Add($"Absent {payslip.AbsentDays:0.##}");
        if (payslip.PaidLeaveDays > 0) time.Add($"Paid leave {payslip.PaidLeaveDays:0.##}");
        if (payslip.UnpaidLeaveDays > 0) time.Add($"Unpaid leave {payslip.UnpaidLeaveDays:0.##}");

        page.Text(page.Left + 4, string.Join("   ", time), 8);
        page.Down(16);
    }

    private static void DrawSection(
        PdfPageBuilder page, string title, IReadOnlyList<PayslipLine> lines,
        decimal total, string totalLabel)
    {
        page.EnsureSpace(60);

        page.Band(13);
        page.Text(page.Left + 4, title, 7.5, bold: true);
        page.TextRight(page.Right - 4, "AMOUNT", 7.5, bold: true);
        page.Down(15);

        if (lines.Count == 0)
        {
            page.Text(page.Left + 4, "None", 8.5);
            page.Down(12);
        }

        foreach (var line in lines)
        {
            page.EnsureSpace(30);

            page.Text(page.Left + 4, line.Name, 8.5);
            page.TextRight(page.Right - 4, Money(line.Amount), 8.5);
            page.Down(10);

            var basis = line.BasisDisplay;

            if (basis.Length > 0)
            {
                page.Text(page.Left + 12, basis, 7.5);
                page.Down(9);
            }

            if (!string.IsNullOrWhiteSpace(line.Note))
                page.Paragraph(page.Left + 12, line.Note, page.Width - 130);

            page.Down(2);
        }

        page.Rule();
        page.Down(11);

        page.Text(page.Left + 4, totalLabel, 8.5, bold: true);
        page.TextRight(page.Right - 4, Money(total), 8.5, bold: true);
        page.Down(16);
    }

    private static void DrawNetPay(PdfPageBuilder page, Payslip payslip)
    {
        page.EnsureSpace(50);

        page.Band(20, 0.9);
        page.Down(4);

        page.Text(page.Left + 4, "NET PAY", 11, bold: true);
        page.TextRight(page.Right - 4, Money(payslip.NetPay), 12, bold: true);
        page.Down(18);

        page.Text(page.Left + 4,
            $"Gross pay {Money(payslip.GrossPay)}   less deductions {Money(payslip.TotalDeductions)}", 8);
        page.Down(12);

        if (payslip.IsFlaggedForReview && !string.IsNullOrWhiteSpace(payslip.ReviewNote))
        {
            page.Paragraph(page.Left + 4, payslip.ReviewNote, page.Width - 8, 7.5);
            page.Down(2);
        }

        if (!string.IsNullOrWhiteSpace(payslip.BankName))
        {
            page.Text(page.Left + 4,
                $"Credited to {payslip.BankName} {Security.GovernmentId.Masked(payslip.BankAccountNumber)}", 8);
            page.Down(12);
        }

        page.Down(4);
    }

    /// <summary>
    /// The figures a computation was worked out from — the year-end tax
    /// adjustment's annual base, a loan's remaining balance. They are never
    /// added to anything, so they are printed apart from the two totals.
    /// </summary>
    private static void DrawInformation(PdfPageBuilder page, IReadOnlyList<PayslipLine> lines)
    {
        page.EnsureSpace(40);

        page.Text(page.Left, "FOR INFORMATION", 7.5, bold: true);
        page.Down(12);

        foreach (var line in lines)
        {
            page.EnsureSpace(24);

            page.Text(page.Left + 4, line.Name, 8);
            page.TextRight(page.Right - 4, Money(line.Amount), 8);
            page.Down(10);

            if (!string.IsNullOrWhiteSpace(line.Note))
                page.Paragraph(page.Left + 12, line.Note, page.Width - 130);
        }

        page.Down(8);
    }

    /// <summary>FR-074.</summary>
    private static void DrawYearToDate(PdfPageBuilder page, PayslipYearToDate ytd)
    {
        page.EnsureSpace(60);

        page.Band(13);
        page.Text(page.Left + 4, "YEAR TO DATE", 7.5, bold: true);
        page.Down(15);

        // Two rows of three columns, written as two passes each, so the labels
        // and the figures on a row share a baseline.
        var column = page.Width / 3;
        var x0 = page.Left + 4;

        page.Text(x0, "Gross pay", 7.5);
        page.Text(x0 + column, "Taxable income", 7.5);
        page.Text(x0 + column * 2, "Tax withheld", 7.5);
        page.Down(10);

        page.Text(x0, Money(ytd.GrossPay), 9, bold: true);
        page.Text(x0 + column, Money(ytd.TaxableIncome), 9, bold: true);
        page.Text(x0 + column * 2, Money(ytd.TaxWithheld), 9, bold: true);
        page.Down(14);

        page.Text(x0, "SSS", 7.5);
        page.Text(x0 + column, "PhilHealth", 7.5);
        page.Text(x0 + column * 2, "Pag-IBIG", 7.5);
        page.Down(10);

        page.Text(x0, Money(ytd.Sss), 9, bold: true);
        page.Text(x0 + column, Money(ytd.PhilHealth), 9, bold: true);
        page.Text(x0 + column * 2, Money(ytd.PagIbig), 9, bold: true);
        page.Down(14);

        if (ytd.ThirteenthMonth > 0)
        {
            page.Text(x0, $"13th month pay released this year: {Money(ytd.ThirteenthMonth)}", 8);
            page.Down(12);
        }
    }

    /// <summary>
    /// FR-053. What the employer paid alongside. It is not a deduction and never
    /// touches net pay, but an employee is entitled to see that it was remitted.
    /// </summary>
    private static void DrawEmployerShare(PdfPageBuilder page, Payslip payslip)
    {
        if (payslip.TotalEmployerShare <= 0m)
            return;

        page.EnsureSpace(30);

        page.Text(page.Left, "EMPLOYER SHARE (not deducted from your pay)", 7.5, bold: true);
        page.Down(11);

        var parts = new List<string>
        {
            $"SSS {Money(payslip.EmployerSss + payslip.EmployerSssWisp)}",
            $"EC {Money(payslip.EmployerEc)}",
            $"PhilHealth {Money(payslip.EmployerPhilHealth)}",
            $"Pag-IBIG {Money(payslip.EmployerPagIbig)}"
        };

        page.Text(page.Left + 4, string.Join("   ", parts), 8);
        page.Down(14);
    }

    private static void DrawFooter(PdfPageBuilder page, Payslip payslip)
    {
        page.EnsureSpace(30);

        page.Rule(0.85);
        page.Down(11);

        page.Text(page.Left,
            "This payslip is computer-generated and is valid without a signature.", 7.5);

        page.TextRight(page.Right,
            $"Generated {DateTime.Now:dd MMM yyyy HH:mm}", 7.5);
    }

    /// <summary>
    /// Money as the document prints it: no currency sign, the sign before the
    /// figure. The peso sign cannot be encoded in a base-14 font, and the header
    /// states the currency instead.
    /// </summary>
    private static string Money(decimal value) =>
        value < 0 ? $"-{Math.Abs(value):N2}" : value.ToString("N2");
}
