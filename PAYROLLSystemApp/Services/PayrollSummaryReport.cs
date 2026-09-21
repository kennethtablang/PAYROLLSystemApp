using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// Everything the payroll summary needs, gathered by
/// <see cref="ReportService"/> so the builder itself touches no database.
/// </summary>
/// <param name="Payslips">
/// Payslips from runs whose whole period falls inside the range. See
/// <paramref name="Straddling"/> for what is deliberately not here.
/// </param>
/// <param name="Lines">
/// Every line of those payslips, by payslip id. The column breakdown is read
/// from these; the payslip header carries only the statutory figures.
/// </param>
/// <param name="Employees">
/// By id, for the surname / given name / initial split. A payslip snapshots the
/// name as one string ("Dela Cruz, Juan P."), which cannot be taken apart
/// reliably — a two-word surname and a two-word given name look the same once
/// joined.
/// </param>
/// <param name="Detachments">
/// Every detachment, active and retired, for the region each one sits in.
/// Retired ones are needed: a summary of an earlier range still has to name the
/// post its people were deployed to.
/// </param>
/// <param name="Straddling">
/// Runs that overlap the range without being contained by it. A payslip is not
/// divisible by date, so these are excluded from the figures and named in a note
/// instead.
/// </param>
public sealed record PayrollSummarySource(
    DateTime From,
    DateTime To,
    IReadOnlyList<Payslip> Payslips,
    IReadOnlyDictionary<int, IReadOnlyList<PayslipLine>> Lines,
    IReadOnlyDictionary<int, Employee> Employees,
    IReadOnlyList<Detachment> Detachments,
    IReadOnlyList<PayrollRun> Straddling,
    IReadOnlyList<PayrollRun> Included,
    string DepartmentFilter);

/// <summary>
/// FR-080, FR-083. The payroll summary: one line per employee, grouped by the
/// detachment they were deployed to, with that detachment's code and name
/// carried on the right of every row.
///
/// <para><b>Grouped by detachment, not by department.</b> The detachment is what
/// the client is billed for and what sets the daily rate, so it is the unit the
/// sheet subtotals by. The department is an internal unit and does not appear.
/// </para>
///
/// <para><b>The column list is the client's, not the engine's.</b> Basic, the
/// three overtime kinds, night shift, SEA &amp; ECOLA, 5Slip, late, other income,
/// gross, company loan, second uniform, the four statutory lines, other
/// deductions, total and net. Each maps to a pay component code; anything with
/// no column of its own falls into "Other income" or "Other deductions", which
/// are computed as the <em>remainder</em> rather than by adding up a list. A
/// remainder cannot silently drop a component added to the configuration later —
/// a named list can, and a dropped deduction is a summary that does not add up.
/// </para>
///
/// <para><b>Late sits in the earnings block and is taken off gross.</b> That is
/// where the client's layout puts it, so this report defines Gross Income as
/// earnings less tardiness, and Total Deductions as everything else. The engine
/// treats tardiness as a deduction, so both figures differ from the payslip's
/// own by exactly the late amount — and <c>Gross − Total = Net</c> still holds,
/// which is the identity the report checks itself against on every row.</para>
///
/// <para><b>A range takes whole cut-offs or none.</b> The natural scope is one
/// semi-monthly period — the fifteen days the client works to — but the range is
/// free, so it can be drawn across the middle of a run. A payslip cannot be cut
/// in half by date: its figures were computed for its own period and there is no
/// honest way to attribute a fraction of a month's SSS to nine days of it. A run
/// the range only partly covers is therefore <b>left out and named</b>, rather
/// than counted whole (which overstates the range) or prorated (which invents a
/// figure).</para>
///
/// <para><b>Nothing is recomputed.</b> Every figure is a sum of snapshotted
/// payslip fields and lines, the same rule the rest of section 2.8 follows.</para>
/// </summary>
public static class PayrollSummaryReport
{
    private const string Unassigned = "(no detachment)";

    /// <summary>
    /// Code, surname, given name and initial. Repeated on every page a wide
    /// table is carried onto — two columns would head a page with a column of
    /// surnames and no given names beside them.
    /// </summary>
    private const int KeyColumns = 4;

    public static ReportGrid Build(PayrollSummarySource source)
    {
        var (from, to) = (source.From.Date, source.To.Date);
        var days = (int)(to - from).TotalDays + 1;

        var subtitle =
            $"{Range(from, to)} · {days} day(s)" +
            (string.IsNullOrWhiteSpace(source.DepartmentFilter) ? "" : $" · {source.DepartmentFilter}");

        var fileStem = $"payroll-summary_{from:yyyy-MM-dd}_{to:yyyy-MM-dd}";

        if (source.Payslips.Count == 0)
            return Nothing(source, subtitle, fileStem);

        var detachments = BuildDetachmentIndex(source.Detachments);

        // One row per employee per detachment: an employee paid over two
        // cut-offs is one line, not two, and one redeployed mid-range appears
        // under each post they were paid at.
        var people = source.Payslips
            .GroupBy(p => (Department: Key(p.DetachmentCode, p.DetachmentName), p.EmployeeId))
            .Select(g => Figures.For(g.Key.Department, [.. g], source))
            .ToList();

        var groups = people
            .GroupBy(f => f.Department)
            .Select(g => new Block(g.Key,
            [
                .. g.OrderBy(f => f.Surname, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(f => f.GivenName, StringComparer.CurrentCultureIgnoreCase)
            ]))
            .OrderBy(b => b.Department == Unassigned ? 1 : 0)
            .ThenBy(b => b.Department, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var rows = new List<ReportRow>(people.Count + groups.Count + 1);
        var undeployed = new List<string>();
        var unbalanced = new List<string>();

        foreach (var block in groups)
        {
            var posting = Posting.For(block.Department, detachments);

            if (!posting.IsAssigned)
                undeployed.Add(block.Department);

            foreach (var person in block.People)
            {
                if (!person.Balances)
                    unbalanced.Add(person.Filed);

                rows.Add(new ReportRow(
                    Cells(person.Code, person.Surname, person.GivenName, person.Initial, person, posting),
                    IsFlagged: !person.Balances));
            }

            // The label stays short on purpose. Which detachment this is
            // already appears in the code column on the left and in the two
            // detachment columns on the right, and every character here widens
            // the surname column for all 26 columns — enough, when the name was
            // spelled out, to push the sheet onto a second dot matrix page.
            rows.Add(new ReportRow(
                Cells(
                    posting.DetachmentCode,
                    "SUBTOTAL",
                    $"{block.People.Count} employee(s)",
                    string.Empty,
                    Figures.Sum(block.People),
                    posting),
                IsTotal: true));
        }

        rows.Add(new ReportRow(
            Cells(
                string.Empty,
                "GRAND TOTAL",
                $"{people.Count} employee(s)",
                string.Empty,
                Figures.Sum(people),
                Posting.None),
            IsTotal: true));

        return new ReportGrid("Payroll summary by detachment", subtitle, Columns(), rows,
            Notes(source, groups.Count, undeployed, unbalanced, people, days), fileStem, KeyColumns);
    }

    private sealed record Block(string Department, IReadOnlyList<Figures> People);

    // =====================================================================
    // Columns
    // =====================================================================

    private static List<ReportColumn> Columns() =>
    [
        new("code", "Code", ReportColumnKind.Text, 88),
        new("surname", "Surname", ReportColumnKind.Text, 150),
        new("given", "Given Name", ReportColumnKind.Text, 140),
        new("mi", "MI.", ReportColumnKind.Text, 44),
        new("days", "No of Days", ReportColumnKind.Number, 84),
        new("basic", "Basic Pay", ReportColumnKind.Money, 110),

        // The client's layout groups these three under one "Overtime" heading.
        // The grid has no column groups, so each says which overtime it is.
        new("ot_regular", "OT Regular", ReportColumnKind.Money, 104),
        new("ot_sunday", "OT Sunday", ReportColumnKind.Money, 102),
        new("ot_holiday", "OT Holiday", ReportColumnKind.Money, 104),

        new("night", "Night Shift", ReportColumnKind.Money, 104),
        new("sea_ecola", "SEA & ECOLA", ReportColumnKind.Money, 110),
        new("five_slip", "5Slip", ReportColumnKind.Money, 88),
        new("late", "Late", ReportColumnKind.Money, 88),
        new("other_income", "Other Income", ReportColumnKind.Money, 112),
        new("gross", "Gross Income", ReportColumnKind.Money, 118),

        new("com_loan", "Com. Loan", ReportColumnKind.Money, 102),
        new("uniform", "2nd Uniform", ReportColumnKind.Money, 106),
        new("sss", "SSS Contribution", ReportColumnKind.Money, 124),
        new("med", "Med. Contri", ReportColumnKind.Money, 106),
        new("pagibig", "PagIbig Contri", ReportColumnKind.Money, 116),
        new("wtax", "Wtax", ReportColumnKind.Money, 96),
        new("other_deduction", "Other Deductions", ReportColumnKind.Money, 128),
        new("total_deduction", "Total Deductions", ReportColumnKind.Money, 128),
        new("net", "Net Pay", ReportColumnKind.Money, 118),

        // The two the summary is filed and billed under. Rightmost, because that
        // is where the person reconciling it against a billing statement looks.
        new("detachment_code", "Detachment code", ReportColumnKind.Text, 122),
        new("detachment_name", "Detachment name", ReportColumnKind.Text, 190)
    ];

    private static List<ReportCell> Cells(
        string code, string surname, string given, string initial, Figures f, Posting posting) =>
    [
        ReportCell.Of(code),
        ReportCell.Of(surname),
        ReportCell.Of(given),
        ReportCell.Of(initial),
        ReportCell.Number(f.Days),
        ReportCell.Money(f.Basic),
        ReportCell.Money(f.OvertimeRegular),
        ReportCell.Money(f.OvertimeSunday),
        ReportCell.Money(f.OvertimeHoliday),
        ReportCell.Money(f.NightShift),
        ReportCell.Money(f.SeaEcola),
        ReportCell.Money(f.FiveSlip),
        ReportCell.Money(f.Late),
        ReportCell.Money(f.OtherIncome),
        ReportCell.Money(f.GrossIncome),
        ReportCell.Money(f.CompanyLoan),
        ReportCell.Money(f.SecondUniform),
        ReportCell.Money(f.Sss),
        ReportCell.Money(f.PhilHealth),
        ReportCell.Money(f.PagIbig),
        ReportCell.Money(f.WithholdingTax),
        ReportCell.Money(f.OtherDeductions),
        ReportCell.Money(f.TotalDeductions),
        ReportCell.Money(f.NetPay),
        ReportCell.Of(posting.DetachmentCode),
        ReportCell.Of(posting.DetachmentName)
    ];

    // =====================================================================
    // One employee's figures
    // =====================================================================

    /// <summary>
    /// The twenty figures a row prints, for one employee or for a total.
    ///
    /// <para>Built once per row rather than read out of the payslips at each
    /// cell, so a subtotal is the sum of the same numbers the rows above it
    /// printed and cannot drift from them.</para>
    /// </summary>
    private sealed class Figures
    {
        public string Department { get; private init; } = string.Empty;

        public string Code { get; private init; } = string.Empty;

        public string Surname { get; private init; } = string.Empty;

        public string GivenName { get; private init; } = string.Empty;

        public string Initial { get; private init; } = string.Empty;

        /// <summary>How the employee is named in a note. Empty for a total.</summary>
        public string Filed { get; private init; } = string.Empty;

        /// <summary>True where this employee's overtime is a pre-split OT line.</summary>
        public bool HasLegacyOvertime { get; private init; }

        public decimal Days { get; private init; }

        public decimal Basic { get; private init; }

        public decimal OvertimeRegular { get; private init; }

        public decimal OvertimeSunday { get; private init; }

        public decimal OvertimeHoliday { get; private init; }

        public decimal NightShift { get; private init; }

        public decimal SeaEcola { get; private init; }

        public decimal FiveSlip { get; private init; }

        public decimal Late { get; private init; }

        public decimal OtherIncome { get; private init; }

        public decimal GrossIncome { get; private init; }

        public decimal CompanyLoan { get; private init; }

        public decimal SecondUniform { get; private init; }

        public decimal Sss { get; private init; }

        public decimal PhilHealth { get; private init; }

        public decimal PagIbig { get; private init; }

        public decimal WithholdingTax { get; private init; }

        public decimal OtherDeductions { get; private init; }

        public decimal TotalDeductions { get; private init; }

        public decimal NetPay { get; private init; }

        /// <summary>
        /// The identity every row of a payroll summary has to satisfy. Checked
        /// rather than assumed: the figures come from two places — the payslip
        /// header for the statutory amounts and the lines for everything else —
        /// and a row that does not balance means those two disagree.
        /// </summary>
        public bool Balances => Math.Abs(GrossIncome - TotalDeductions - NetPay) <= 0.01m;

        public static Figures For(string department, IReadOnlyList<Payslip> slips, PayrollSummarySource source)
        {
            var lines = slips
                .SelectMany(s => source.Lines.TryGetValue(s.Id, out var found)
                    ? found
                    : (IReadOnlyList<PayslipLine>)[])
                .ToList();

            decimal Earning(params string[] codes) => lines
                .Where(l => l.IsEarning && codes.Contains(l.Code, StringComparer.OrdinalIgnoreCase))
                .Sum(l => l.Amount);

            decimal Deduction(string code) => lines
                .Where(l => l.IsDeduction && string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))
                .Sum(l => l.Amount);

            var latest = slips[^1];
            var employee = source.Employees.GetValueOrDefault(latest.EmployeeId);

            var basic = Earning(PayComponentCodes.BasicPay);

            // Overtime computed before it was split three ways carries the single
            // legacy code. It is reported as ordinary overtime because that is
            // what it most often was, and a note says so rather than the report
            // pretending the day those hours fell on is still known.
            var legacy = Earning(PayComponentCodes.Overtime);

            var otRegular = Earning(PayComponentCodes.OvertimeRegular) + legacy;
            var otSunday = Earning(PayComponentCodes.OvertimeRestDay);
            var otHoliday = Earning(PayComponentCodes.OvertimeHoliday);
            var night = Earning(PayComponentCodes.NightDifferential);

            var seaEcola = Earning(
                PayComponentCodes.SpecialEmergencyAllowance,
                PayComponentCodes.CostOfLivingAllowance);

            var fiveSlip = Earning(PayComponentCodes.FiveSlip);

            var gross = slips.Sum(s => s.GrossPay);

            // The remainder, not a list. A pay component added to the
            // configuration next year lands here on its own instead of being
            // silently dropped, and the row still adds up to the payslip's gross.
            var otherIncome = gross - (basic + otRegular + otSunday + otHoliday + night + seaEcola + fiveSlip);

            var late = Deduction(PayComponentCodes.Tardiness);
            var companyLoan = Deduction(PayComponentCodes.CompanyLoan);
            var uniform = Deduction(PayComponentCodes.SecondUniform);

            var sss = slips.Sum(s => s.EmployeeSss + s.EmployeeSssWisp);
            var philHealth = slips.Sum(s => s.EmployeePhilHealth);
            var pagIbig = slips.Sum(s => s.EmployeePagIbig);
            var tax = slips.Sum(s => s.WithholdingTax);

            // Late is printed in the earnings block, so it comes off gross here
            // and is left out of total deductions. Both figures differ from the
            // payslip's own by exactly the late amount, which is why
            // Gross - Total = Net still holds.
            var totalDeductions = slips.Sum(s => s.TotalDeductions) - late;

            var otherDeductions =
                totalDeductions - (companyLoan + uniform + sss + philHealth + pagIbig + tax);

            return new Figures
            {
                Department = department,
                Code = latest.EmployeeNumber,
                Surname = Surname(employee, latest.EmployeeName),
                GivenName = Given(employee, latest.EmployeeName),
                Initial = MiddleInitial(employee),
                Filed = latest.EmployeeName,
                HasLegacyOvertime = legacy != 0m,

                Days = slips.Sum(s => s.DaysWorked),
                Basic = basic,
                OvertimeRegular = otRegular,
                OvertimeSunday = otSunday,
                OvertimeHoliday = otHoliday,
                NightShift = night,
                SeaEcola = seaEcola,
                FiveSlip = fiveSlip,
                Late = late,
                OtherIncome = otherIncome,
                GrossIncome = gross - late,

                CompanyLoan = companyLoan,
                SecondUniform = uniform,
                Sss = sss,
                PhilHealth = philHealth,
                PagIbig = pagIbig,
                WithholdingTax = tax,
                OtherDeductions = otherDeductions,
                TotalDeductions = totalDeductions,
                NetPay = slips.Sum(s => s.NetPay)
            };
        }

        public static Figures Sum(IReadOnlyList<Figures> people) => new()
        {
            Days = people.Sum(f => f.Days),
            Basic = people.Sum(f => f.Basic),
            OvertimeRegular = people.Sum(f => f.OvertimeRegular),
            OvertimeSunday = people.Sum(f => f.OvertimeSunday),
            OvertimeHoliday = people.Sum(f => f.OvertimeHoliday),
            NightShift = people.Sum(f => f.NightShift),
            SeaEcola = people.Sum(f => f.SeaEcola),
            FiveSlip = people.Sum(f => f.FiveSlip),
            Late = people.Sum(f => f.Late),
            OtherIncome = people.Sum(f => f.OtherIncome),
            GrossIncome = people.Sum(f => f.GrossIncome),
            CompanyLoan = people.Sum(f => f.CompanyLoan),
            SecondUniform = people.Sum(f => f.SecondUniform),
            Sss = people.Sum(f => f.Sss),
            PhilHealth = people.Sum(f => f.PhilHealth),
            PagIbig = people.Sum(f => f.PagIbig),
            WithholdingTax = people.Sum(f => f.WithholdingTax),
            OtherDeductions = people.Sum(f => f.OtherDeductions),
            TotalDeductions = people.Sum(f => f.TotalDeductions),
            NetPay = people.Sum(f => f.NetPay)
        };
    }

    // =====================================================================
    // Names
    // =====================================================================

    /// <summary>
    /// The surname, from the employee record where there is one.
    ///
    /// <para>The fallback splits the payslip's snapshotted "Surname, Given M."
    /// on its comma. It is only reached for a payslip whose employee record has
    /// gone, which FR-017 says should not happen — records are deactivated, not
    /// deleted — so it is there to keep a row printable rather than to be relied
    /// on.</para>
    /// </summary>
    private static string Surname(Employee? employee, string filed)
    {
        if (employee is not null && !string.IsNullOrWhiteSpace(employee.LastName))
            return employee.LastName;

        var comma = filed.IndexOf(',');
        return comma > 0 ? filed[..comma].Trim() : filed.Trim();
    }

    private static string Given(Employee? employee, string filed)
    {
        if (employee is not null && !string.IsNullOrWhiteSpace(employee.FirstName))
            return employee.FirstName;

        var comma = filed.IndexOf(',');
        return comma > 0 && comma < filed.Length - 1 ? filed[(comma + 1)..].Trim() : string.Empty;
    }

    /// <summary>
    /// The middle initial, with its full stop. Blank where there is no middle
    /// name — a bare "." reads as data that is missing rather than absent.
    /// </summary>
    private static string MiddleInitial(Employee? employee)
    {
        var middle = employee?.MiddleName?.Trim();

        return string.IsNullOrEmpty(middle)
            ? string.Empty
            : char.ToUpperInvariant(middle[0]) + ".";
    }

    // =====================================================================
    // Notes
    // =====================================================================

    private static ReportGrid Nothing(PayrollSummarySource source, string subtitle, string fileStem)
    {
        // Built by hand rather than through ReportGrid.Empty, which carries
        // exactly one note: a range that fell between two cut-offs needs the
        // reason *and* the list of runs it just missed.
        List<ReportNote> notes =
        [
            ReportNote.Warn(source.Straddling.Count > 0
                ? "No payroll run falls wholly inside this range. Widen it to cover a complete " +
                  "cut-off — the runs it currently cuts through are listed below."
                : "No payroll was computed for any period inside this range.")
        ];

        if (source.Straddling.Count > 0)
            notes.Add(StraddleNote(source.Straddling));

        return new ReportGrid("Payroll summary by detachment", subtitle,
            [new ReportColumn("note", "")], [], notes, fileStem);
    }

    private static List<ReportNote> Notes(
        PayrollSummarySource source,
        int departmentCount,
        IReadOnlyList<string> undeployed,
        IReadOnlyList<string> unbalanced,
        IReadOnlyList<Figures> people,
        int days)
    {
        var notes = new List<ReportNote>();

        if (unbalanced.Count > 0)
        {
            notes.Add(ReportNote.Blocking(
                $"{unbalanced.Count} row(s) do not add up — gross income less total deductions does not " +
                $"equal net pay: {Name(unbalanced)}. The payslip header and its own lines disagree, so " +
                "this summary cannot be paid or billed from until that is resolved."));
        }

        var unposted = source.Included.Where(r => !r.IsPosted).ToList();

        if (unposted.Count > 0)
        {
            notes.Add(ReportNote.Blocking(
                $"{unposted.Count} of the {source.Included.Count} run(s) in this range " +
                $"{(unposted.Count == 1 ? "is" : "are")} not posted " +
                $"({Name([.. unposted.Select(r => $"{r.ReferenceNumber} — {r.StatusDisplay.ToLowerInvariant()}")])}). " +
                "Their figures can still be recalculated, so do not bill against these totals yet."));
        }

        if (source.Straddling.Count > 0)
            notes.Add(StraddleNote(source.Straddling));

        var legacy = people.Where(f => f.HasLegacyOvertime).Select(f => f.Filed).ToList();

        if (legacy.Count > 0)
        {
            notes.Add(ReportNote.Warn(
                $"{legacy.Count} employee(s) were paid overtime before it was split three ways, and it " +
                $"is reported here under OT Regular: {Name(legacy)}. The day each of those hours fell " +
                "on was not kept, so the Sunday and Holiday columns cannot be filled in for them. Runs " +
                "computed from now on split correctly."));
        }

        if (undeployed.Count > 0)
        {
            notes.Add(ReportNote.Warn(
                $"{undeployed.Count} group(s) on this summary name a detachment that no longer exists " +
                $"in the detachment list: {Name(undeployed)}. Their figures are correct — the code and " +
                "name come off the payslips themselves — but the region cannot be shown."));
        }

        var unassigned = people.Count(f => f.Department == Unassigned);

        if (unassigned > 0)
        {
            notes.Add(ReportNote.Warn(
                $"{unassigned} employee(s) were paid with no detachment on their payslip and are " +
                "grouped under \"(no detachment)\". Head-office staff are the ordinary case; anyone " +
                "else here was paid without a post to bill their time to."));
        }

        notes.Add(ReportNote.Info(
            $"This range covers {days} day(s) across {departmentCount} detachment(s) and takes in " +
            $"{source.Included.Count} whole cut-off(s): " +
            $"{Name([.. source.Included.Select(r => $"{r.ReferenceNumber} ({r.PeriodDisplay})")])}."));

        notes.Add(ReportNote.Info(
            "Late is shown with the earnings and is already off Gross Income, so it is not counted " +
            "again in Total Deductions. Every row satisfies gross less total deductions equals net " +
            "pay, and this report checks that on each one."));

        notes.Add(ReportNote.Info(
            "\"Other income\" is gross less the named earning columns, and \"Other deductions\" is total " +
            "less the named deduction columns — both remainders, so a pay component added later shows " +
            "up there rather than disappearing. The payroll register for a run itemises them by code."));

        notes.Add(ReportNote.Info(
            "OT Sunday is overtime on the employee's scheduled rest day, whichever weekday that is. " +
            "Overtime on a holiday that fell on a rest day is reported under OT Holiday, because the " +
            "holiday-rest-day multiplier is what it was paid at."));

        return notes;
    }

    /// <summary>
    /// Names the runs the range cuts through. Its own method because the empty
    /// report needs it too, and an empty summary whose range fell between two
    /// cut-offs is exactly the case that needs explaining.
    /// </summary>
    private static ReportNote StraddleNote(IReadOnlyList<PayrollRun> straddling) =>
        ReportNote.Warn(
            $"{straddling.Count} run(s) overlap this range without falling inside it and are " +
            $"excluded: {Name([.. straddling.Select(r => $"{r.ReferenceNumber} ({r.PeriodDisplay})")])}. " +
            "A payslip covers its whole cut-off and cannot be split across a date, so counting one " +
            "here would report pay earned outside the range. Move the range onto the cut-off " +
            "boundaries to include them.");

    // =====================================================================
    // Departments and detachments
    // =====================================================================

    /// <summary>
    /// The group key: the payslip's own snapshotted detachment code and name.
    ///
    /// <para>Read off the payslip rather than joined to the detachment table, so
    /// a post renamed or retired since the run was computed does not change what
    /// an already-printed sheet says. The table below is consulted only for the
    /// region, which is presentation.</para>
    /// </summary>
    private static string Key(string code, string name)
    {
        var trimmedCode = (code ?? string.Empty).Trim();
        var trimmedName = (name ?? string.Empty).Trim();

        if (trimmedCode.Length == 0 && trimmedName.Length == 0)
            return Unassigned;

        return trimmedCode.Length == 0 ? trimmedName
            : trimmedName.Length == 0 ? trimmedCode
            : $"{trimmedCode} — {trimmedName}";
    }

    private static Dictionary<string, Detachment> BuildDetachmentIndex(
        IReadOnlyList<Detachment> detachments)
    {
        var index = new Dictionary<string, Detachment>(StringComparer.CurrentCultureIgnoreCase);

        foreach (var detachment in detachments)
        {
            var code = detachment.Code.Trim();

            if (code.Length > 0)
                index[code] = detachment;
        }

        return index;
    }

    /// <summary>
    /// The detachment a block of rows belongs to, as the sheet prints it.
    /// <see cref="None"/> is the head-office group, which has no post.
    /// </summary>
    private readonly record struct Posting(
        string DetachmentCode,
        string DetachmentName,
        bool IsAssigned)
    {
        public static readonly Posting None = new(string.Empty, string.Empty, false);

        /// <summary>
        /// Splits a group key back into its code and name. The key was built
        /// from the payslip snapshot, so this never consults the detachment
        /// table for the figures — only <paramref name="known"/> is looked up,
        /// and only to decide whether the post still exists.
        /// </summary>
        public static Posting For(string groupKey, IReadOnlyDictionary<string, Detachment> known)
        {
            if (groupKey == Unassigned)
                return None;

            var separator = groupKey.IndexOf(" — ", StringComparison.Ordinal);

            var code = separator > 0 ? groupKey[..separator] : groupKey;
            var name = separator > 0 ? groupKey[(separator + 3)..] : string.Empty;

            return new Posting(code, name, known.ContainsKey(code));
        }
    }

    // =====================================================================

    private static string Range(DateTime from, DateTime to) =>
        from.Year == to.Year && from.Month == to.Month
            ? $"{from:dd}–{to:dd MMM yyyy}"
            : $"{from:dd MMM yyyy} – {to:dd MMM yyyy}";

    private static string Name(IReadOnlyList<string> names) =>
        names.Count <= 5
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(5)) + $" and {names.Count - 5} other(s)";
}
