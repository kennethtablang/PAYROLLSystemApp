using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// FR-051 – FR-063. The payroll engine: one employee's cut-off in, one payslip
/// out.
///
/// <para><b>Pure.</b> No database, no clock, no configuration lookups of its own
/// — everything arrives in the <see cref="PayrollContext"/>. That is what makes
/// a run reproducible: the same input and the same context produce the same
/// payslip in three years' time, when the rates, the schedules and the employee
/// have all moved on (NFR-009).</para>
///
/// <para><b>The one idea to hold on to: salary coverage.</b> A premium line pays
/// only what the employee's pay arrangement does not already cover. With the 313
/// factor — 365 days less 52 Sundays — a <b>monthly</b> salary already pays for
/// every calendar day except a rest day, regular holidays included. So an
/// ordinary worked day produces no premium line at all; working a regular
/// holiday adds the remaining 1.00× rather than the full 2.00×; a rest day is
/// covered by nothing and pays its full 1.30×. A <b>daily- or hourly-paid</b>
/// employee is covered by nothing, so every worked day pays its full multiplier.
/// Getting this wrong pays the same hours twice.</para>
///
/// <para><b>Overtime is never covered</b> by either arrangement: it is time
/// beyond the standard day, so it always pays its full multiplier.</para>
/// </summary>
public static class PayrollCalculator
{
    /// <summary>
    /// The ₱90,000 exclusion for 13th month pay and other benefits
    /// (NIRC §32(B)(7)(e), as amended by TRAIN). Held here rather than in
    /// configuration because it is set by statute, not by the employer.
    /// </summary>
    public const decimal BenefitsExclusionCeiling = 90_000m;

    /// <summary>
    /// Art. 95: unused Service Incentive Leave converted to cash is a de-minimis
    /// benefit up to ten days' worth. Beyond that it is ordinary taxable income.
    /// </summary>
    public const decimal ConvertibleLeaveDeMinimisDays = 10m;

    public static PayslipDraft Calculate(EmployeePayrollInput input, PayrollContext ctx)
    {
        var employee = input.Employee;
        var run = ctx.Run;

        var lines = new List<PayslipLine>();
        var blockers = new List<string>();
        var loanCollections = new List<(EmployeeLoan, decimal)>();

        // ---------------------------------------------------------- rates

        var rates = ResolveRates(employee, ctx.Settings);

        if (rates.Hourly <= 0m)
        {
            blockers.Add(
                $"{employee.FullName} has no basic rate set, so nothing can be computed for them.");
        }

        var payslip = NewPayslip(input, ctx, rates);

        // A 13th month run takes its own branch entirely: it pays the PD 851
        // entitlement and takes no contributions, no loans and no attendance.
        if (run.RunType == PayrollRunType.ThirteenthMonth)
        {
            AddThirteenthMonth(input, ctx, payslip, lines, isFinalPay: false);
            AddAdjustments(input, lines, PayslipLineKind.Earning);
            AddAdjustments(input, lines, PayslipLineKind.Deduction);

            // No contributions and no loan amortisations on this branch — but
            // the part of the 13th month beyond the ₱90,000 exclusion is
            // ordinary taxable income, and skipping tax here would let it out
            // untaxed.
            AddWithholdingTax(input, ctx, payslip, lines, blockers);

            Finalise(input, ctx, payslip, lines, blockers);

            return new PayslipDraft
            {
                Payslip = payslip,
                Lines = lines,
                LoanCollections = loanCollections,
                Blockers = blockers
            };
        }

        // ----------------------------------------------------------- time

        var time = Summarise(input, ctx);
        ApplyTime(payslip, time);

        if (input.Attendance.Count == 0)
        {
            // Paying without a timesheet is how an employee is paid for a
            // cut-off nobody recorded. The cut-off is generated on the
            // Time & Attendance screen; until then there is nothing to compute.
            blockers.Add(
                $"No attendance is recorded for {employee.FullName} inside the cut-off " +
                $"({run.CutOffStart:dd MMM} – {run.CutOffEnd:dd MMM yyyy}).");
        }

        // -------------------------------------------------------- earnings

        var isMonthly = employee.PayType == PayType.Monthly;

        if (isMonthly)
            AddMonthlyBasic(input, ctx, rates, time, lines);
        else
            AddDailyBasic(input, ctx, rates, time, lines);

        AddPremiums(input, ctx, rates, lines, blockers, isMonthly);
        AddAllowance(input, ctx, rates, time, lines);
        AddAdjustments(input, lines, PayslipLineKind.Earning);

        if (run.RunType == PayrollRunType.FinalPay)
        {
            AddLeaveConversion(input, ctx, rates, lines);
            AddThirteenthMonth(input, ctx, payslip, lines, isFinalPay: true);
        }

        // ------------------------------------------------------ deductions

        if (isMonthly)
            AddTimeDeductions(input, ctx, rates, time, lines);

        AddStatutory(input, ctx, payslip, lines, blockers);
        AddLoans(input, ctx, lines, loanCollections);
        AddAdjustments(input, lines, PayslipLineKind.Deduction);

        // ------------------------------------------------------------ tax

        AddWithholdingTax(input, ctx, payslip, lines, blockers);

        Finalise(input, ctx, payslip, lines, blockers);

        return new PayslipDraft
        {
            Payslip = payslip,
            Lines = lines,
            LoanCollections = loanCollections,
            Blockers = blockers
        };
    }

    // =====================================================================
    // Rates
    // =====================================================================

    private sealed record Rates(decimal Monthly, decimal Daily, decimal Hourly, decimal MonthlyBasis);

    /// <summary>
    /// §5.2. Monthly to daily to hourly, and the monthly figure the statutory
    /// schedules are read against.
    ///
    /// <para>A daily- or hourly-paid employee has no monthly salary, but SSS,
    /// PhilHealth and Pag-IBIG all read one, so it is derived from the same
    /// factor the daily rate came from. Reading a bracket against a fortnight's
    /// pay would put the employee in a bracket less than half as high.</para>
    /// </summary>
    private static Rates ResolveRates(Employee employee, PayrollSettings settings)
    {
        var hoursPerDay = settings.StandardHoursPerDay <= 0 ? 8m : settings.StandardHoursPerDay;

        switch (employee.PayType)
        {
            case PayType.Monthly:
                {
                    var monthly = employee.BasicRate;
                    var daily = settings.DailyRateFor(monthly);

                    return new Rates(monthly, daily, PayrollRounding.Rate(daily / hoursPerDay), monthly);
                }

            case PayType.Daily:
                {
                    var daily = employee.BasicRate;
                    var monthly = PayrollRounding.Rate(daily * settings.WorkingDaysFactor / 12m);

                    return new Rates(monthly, daily, PayrollRounding.Rate(daily / hoursPerDay), monthly);
                }

            default:
                {
                    var hourly = employee.BasicRate;
                    var daily = PayrollRounding.Rate(hourly * hoursPerDay);
                    var monthly = PayrollRounding.Rate(daily * settings.WorkingDaysFactor / 12m);

                    return new Rates(monthly, daily, hourly, monthly);
                }
        }
    }

    private static Payslip NewPayslip(EmployeePayrollInput input, PayrollContext ctx, Rates rates)
    {
        var employee = input.Employee;
        var run = ctx.Run;

        return new Payslip
        {
            PayrollRunId = run.Id,
            EmployeeId = employee.Id,
            EmployeeNumber = employee.EmployeeNumber,
            EmployeeName = employee.FullName,
            DepartmentName = input.DepartmentName,
            PositionTitle = input.PositionTitle,
            Tin = employee.Tin,
            SssNumber = employee.SssNumber,
            PhilHealthNumber = employee.PhilHealthNumber,
            PagIbigNumber = employee.PagIbigNumber,
            BankName = employee.BankName,
            BankAccountNumber = employee.BankAccountNumber,

            PeriodCode = run.PeriodCode,
            PeriodStart = run.PeriodStart,
            PeriodEnd = run.PeriodEnd,
            PayDate = run.PayDate,

            PayType = employee.PayType,
            BasicRate = employee.BasicRate,
            DailyRate = rates.Daily,
            HourlyRate = rates.Hourly,
            WorkingDaysFactor = ctx.Settings.WorkingDaysFactor,
            MonthlyBasis = rates.MonthlyBasis,
            IsMinimumWageEarner = employee.IsMinimumWageEarner
        };
    }

    // =====================================================================
    // Time
    // =====================================================================

    private sealed class TimeSummary
    {
        public decimal ScheduledDays;
        public decimal EmployedScheduledDays;
        public decimal DaysWorked;
        public decimal RegularHours;
        public decimal OvertimeHours;
        public decimal NightHours;
        public int LateMinutes;
        public int UndertimeMinutes;
        public decimal AbsentDays;
        public decimal PaidLeaveDays;
        public decimal UnpaidLeaveDays;

        /// <summary>
        /// What proportion of the period the employee was actually on the books
        /// for. 1 for anyone employed throughout, which is almost everyone.
        /// </summary>
        public decimal EmployedShare =>
            ScheduledDays <= 0m ? 1m : Math.Clamp(EmployedScheduledDays / ScheduledDays, 0m, 1m);
    }

    private static TimeSummary Summarise(EmployeePayrollInput input, PayrollContext ctx)
    {
        var employee = input.Employee;
        var summary = new TimeSummary();

        // Art. 82: managerial and supervisory staff sit outside the hours-of-work
        // provisions, so they earn no overtime, night differential or premium
        // pay. Tardiness is a different question — it is "no work, no pay"
        // rather than a premium — so it is not suppressed here.
        var earnsPremiums = !input.IsManagerial;

        foreach (var day in input.Attendance)
        {
            var scheduled = day.ScheduledHours > 0m
                ? day.ScheduledHours
                : ctx.Settings.StandardHoursPerDay;

            if (!day.IsRestDay)
            {
                summary.ScheduledDays += 1m;

                if (IsEmployedOn(employee, day.Date))
                    summary.EmployedScheduledDays += 1m;
            }

            summary.RegularHours += day.RegularHours;

            if (scheduled > 0m && day.RegularHours > 0m)
                summary.DaysWorked += Math.Min(1m, day.RegularHours / scheduled);

            if (earnsPremiums)
            {
                summary.OvertimeHours += day.OvertimeHoursApproved;
                summary.NightHours += day.NightDifferentialHours;
            }

            summary.LateMinutes += day.LateMinutes;
            summary.UndertimeMinutes += day.UndertimeMinutes;

            switch (day.Status)
            {
                case AttendanceStatus.Absent:
                    summary.AbsentDays += 1m;
                    break;

                case AttendanceStatus.OnLeave:
                    if (IsPaidLeaveOn(input, day.Date))
                        summary.PaidLeaveDays += 1m;
                    else
                        summary.UnpaidLeaveDays += 1m;
                    break;
            }
        }

        return summary;
    }

    /// <summary>
    /// FR-033. Whether an approved leave covering this date was granted as paid.
    /// An unpaid grant — or a day marked as leave with no surviving request —
    /// costs the employee the day.
    /// </summary>
    private static bool IsPaidLeaveOn(EmployeePayrollInput input, DateTime date) =>
        input.ApprovedLeave.Any(r =>
            r.IsPaid &&
            date.Date >= r.StartDate.Date &&
            date.Date <= r.EndDate.Date);

    private static bool IsEmployedOn(Employee employee, DateTime date) =>
        date.Date >= employee.HireDate.Date &&
        (employee.SeparationDate is not { } left || date.Date <= left.Date);

    private static void ApplyTime(Payslip payslip, TimeSummary time)
    {
        payslip.DaysWorked = Math.Round(time.DaysWorked, 2, MidpointRounding.AwayFromZero);
        payslip.RegularHours = Math.Round(time.RegularHours, 2, MidpointRounding.AwayFromZero);
        payslip.OvertimeHours = Math.Round(time.OvertimeHours, 2, MidpointRounding.AwayFromZero);
        payslip.NightDifferentialHours = Math.Round(time.NightHours, 2, MidpointRounding.AwayFromZero);
        payslip.LateMinutes = time.LateMinutes;
        payslip.UndertimeMinutes = time.UndertimeMinutes;
        payslip.AbsentDays = time.AbsentDays;
        payslip.PaidLeaveDays = time.PaidLeaveDays;
        payslip.UnpaidLeaveDays = time.UnpaidLeaveDays;
    }

    // =====================================================================
    // Earnings
    // =====================================================================

    /// <summary>
    /// FR-051. A monthly salary is divided by the number of runs in the month,
    /// not multiplied by days worked: that is what "monthly" means, and it is
    /// why absences come off as their own deduction rather than by shrinking the
    /// basic line.
    ///
    /// <para>The one thing that does shrink it is <em>not being employed</em>.
    /// Someone hired on the 20th is paid for the days from the 20th, and the
    /// line's note says so.</para>
    /// </summary>
    private static void AddMonthlyBasic(
        EmployeePayrollInput input, PayrollContext ctx, Rates rates, TimeSummary time,
        List<PayslipLine> lines)
    {
        var runs = Math.Max(1, ctx.Run.RunsInMonth);
        var full = rates.Monthly / runs;
        var share = time.EmployedShare;
        var amount = full * share;

        var note = share >= 1m
            ? $"Monthly salary ÷ {runs} run(s) in the month"
            : $"Monthly salary ÷ {runs}, pro-rated for {time.EmployedScheduledDays:0.##} of " +
              $"{time.ScheduledDays:0.##} working day(s) employed";

        lines.Add(Earning(ctx, PayComponentCodes.BasicPay, "Basic pay", amount, input, note: note));
    }

    /// <summary>
    /// FR-051. A daily- or hourly-paid employee is paid for the hours they
    /// rendered, so lateness, undertime and absence are already priced in — they
    /// simply produced fewer hours. Deducting for them again would charge the
    /// employee twice, which is why <see cref="AddTimeDeductions"/> runs only for
    /// monthly-paid staff.
    ///
    /// <para>Only <b>ordinary</b> hours are basic pay. Hours worked on a rest day
    /// or a holiday are paid at their full multiplier by
    /// <see cref="AddPremiums"/>, because nothing covers them.</para>
    /// </summary>
    private static void AddDailyBasic(
        EmployeePayrollInput input, PayrollContext ctx, Rates rates, TimeSummary time,
        List<PayslipLine> lines)
    {
        var ordinaryHours = input.Attendance
            .Where(d => !d.IsRestDay && d.HolidayType is HolidayType.None or HolidayType.SpecialWorking)
            .Sum(d => d.RegularHours);

        if (ordinaryHours > 0m)
        {
            lines.Add(Earning(ctx, PayComponentCodes.BasicPay, "Basic pay",
                rates.Hourly * ordinaryHours, input,
                quantity: ordinaryHours, rate: rates.Hourly,
                note: "Ordinary hours rendered. Rest day and holiday hours are paid as premiums."));
        }

        // Art. 94: a daily-paid employee is paid for a regular holiday they did
        // not work, provided they were present on the preceding workday. A
        // monthly-paid employee is already covered by the 313 factor, which is
        // why this has no counterpart above.
        if (ctx.Settings.PayUnworkedRegularHoliday)
            AddUnworkedRegularHolidays(input, ctx, rates, lines);

        // Paid leave has to be paid explicitly here for the same reason: there is
        // no salary covering the day.
        if (time.PaidLeaveDays > 0m)
        {
            lines.Add(Earning(ctx, PayComponentCodes.PaidLeave, "Paid leave",
                rates.Daily * time.PaidLeaveDays, input,
                quantity: time.PaidLeaveDays, rate: rates.Daily,
                note: "Approved leave granted as paid (FR-033)."));
        }
    }

    private static void AddUnworkedRegularHolidays(
        EmployeePayrollInput input, PayrollContext ctx, Rates rates, List<PayslipLine> lines)
    {
        var days = 0m;

        foreach (var day in input.Attendance)
        {
            if (day.HolidayType != HolidayType.Regular || day.RegularHours > 0m)
                continue;

            if (!IsEmployedOn(input.Employee, day.Date))
                continue;

            // The Art. 94 condition. A day with no preceding record inside the
            // cut-off — the first day of the period — is given the benefit of
            // the doubt rather than silently refused, because the evidence for
            // refusing it lies in the previous cut-off.
            if (WasAbsentOnPrecedingWorkday(input, day.Date))
                continue;

            days += 1m;
        }

        if (days <= 0m)
            return;

        lines.Add(Earning(ctx, PayComponentCodes.HolidayUnworked, "Regular holiday (unworked)",
            rates.Daily * days, input,
            quantity: days, rate: rates.Daily, multiplier: 1m,
            note: "Art. 94 — paid though unworked, the employee having been present the preceding workday."));
    }

    private static bool WasAbsentOnPrecedingWorkday(EmployeePayrollInput input, DateTime holiday)
    {
        var preceding = input.Attendance
            .Where(d => d.Date.Date < holiday.Date && !d.IsRestDay && d.HolidayType == HolidayType.None)
            .OrderByDescending(d => d.Date)
            .FirstOrDefault();

        return preceding is not null && preceding.Status == AttendanceStatus.Absent;
    }

    /// <summary>
    /// FR-051, FR-044. The premium lines, one per kind of day worked.
    ///
    /// <para>What is paid is the multiplier <em>less what the pay arrangement
    /// already covers</em> — see the class remarks. A missing cell stops the run
    /// rather than defaulting to something plausible: paying 1.00× because a
    /// rest-day-holiday row was never configured is an underpayment nobody would
    /// notice.</para>
    /// </summary>
    private static void AddPremiums(
        EmployeePayrollInput input, PayrollContext ctx, Rates rates,
        List<PayslipLine> lines, List<string> blockers, bool isMonthly)
    {
        if (input.IsManagerial)
            return;

        var premiums = new Dictionary<string, (string Name, decimal Hours, decimal Amount, decimal Multiplier)>(
            StringComparer.OrdinalIgnoreCase);

        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Accumulate(string code, decimal hours, decimal payableMultiplier, decimal fullMultiplier)
        {
            if (hours <= 0m || payableMultiplier <= 0m)
                return;

            var amount = rates.Hourly * hours * payableMultiplier;
            var name = ctx.Premiums.Get(code)?.Name ?? code;

            if (premiums.TryGetValue(code, out var existing))
            {
                premiums[code] = (existing.Name, existing.Hours + hours, existing.Amount + amount, fullMultiplier);
            }
            else
            {
                premiums[code] = (name, hours, amount, fullMultiplier);
            }
        }

        foreach (var day in input.Attendance)
        {
            // Ordinary hours on the day.
            if (day.RegularHours > 0m)
            {
                var code = PremiumMatrix.CodeFor(day.HolidayType, day.IsRestDay, isOvertime: false);
                var multiplier = ctx.Premiums.MultiplierFor(day.HolidayType, day.IsRestDay, isOvertime: false);

                if (multiplier is not { } dayRate)
                {
                    missing.Add(code);
                }
                else
                {
                    // Coverage: a monthly salary already pays for every day that
                    // is not a rest day. A daily rate covers nothing beyond the
                    // ordinary day already paid as basic.
                    var covered = isMonthly
                        ? (day.IsRestDay ? 0m : 1m)
                        : (day.IsRestDay || day.HolidayType is not (HolidayType.None or HolidayType.SpecialWorking)
                            ? 0m
                            : 1m);

                    Accumulate(code, day.RegularHours, dayRate - covered, dayRate);
                }
            }

            // Overtime is covered by nothing, under either arrangement.
            if (day.OvertimeHoursApproved > 0m)
            {
                var code = PremiumMatrix.CodeFor(day.HolidayType, day.IsRestDay, isOvertime: true);
                var multiplier = ctx.Premiums.MultiplierFor(day.HolidayType, day.IsRestDay, isOvertime: true);

                if (multiplier is not { } otRate)
                    missing.Add(code);
                else
                    Accumulate(code, day.OvertimeHoursApproved, otRate, otRate);
            }
        }

        foreach (var code in missing)
        {
            blockers.Add(
                $"No premium multiplier is configured for {code}, which this cut-off needs. " +
                "Set it on Payroll Setup → Premium rates.");
        }

        // Overtime lines are reported under the overtime earning; the rest under
        // holiday or rest day pay, so a payslip groups the way a payroll officer
        // reads it rather than one line per matrix cell.
        foreach (var (code, entry) in premiums.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var earningCode = code.Contains("_OT", StringComparison.OrdinalIgnoreCase)
                ? PayComponentCodes.Overtime
                : code.StartsWith("RD", StringComparison.OrdinalIgnoreCase)
                    ? PayComponentCodes.RestDayPremium
                    : PayComponentCodes.HolidayPay;

            lines.Add(Earning(ctx, earningCode, entry.Name, entry.Amount, input,
                quantity: entry.Hours, rate: rates.Hourly, multiplier: entry.Multiplier,
                code: earningCode,
                note: isMonthly && !code.Contains("_OT", StringComparison.OrdinalIgnoreCase)
                    ? "The excess over what the monthly salary already covers."
                    : string.Empty));
        }

        AddNightDifferential(input, ctx, rates, lines);
    }

    /// <summary>
    /// Art. 86. Ten per cent, added to the rate already applying to the hour.
    ///
    /// <para><b>Known simplification.</b> The attendance record carries one night
    /// figure for the day rather than a split between regular and overtime hours,
    /// so night hours are paid the differential on the ordinary rate. An employee
    /// whose overtime falls inside 22:00–06:00 is therefore paid slightly less
    /// than a full split would give them. Correcting it means splitting
    /// <see cref="AttendanceRecord.NightDifferentialHours"/> at source.</para>
    /// </summary>
    private static void AddNightDifferential(
        EmployeePayrollInput input, PayrollContext ctx, Rates rates, List<PayslipLine> lines)
    {
        var hours = input.Attendance.Sum(d => d.NightDifferentialHours);
        if (hours <= 0m)
            return;

        var rate = ctx.Premiums.NightDifferentialRate;
        if (rate <= 0m)
            return;

        lines.Add(Earning(ctx, PayComponentCodes.NightDifferential, "Night differential",
            rates.Hourly * hours * rate, input,
            quantity: hours, rate: rates.Hourly, multiplier: rate,
            note: "Art. 86 — hours worked between 22:00 and 06:00."));
    }

    /// <summary>
    /// The recurring allowance held on the employee record, split across the
    /// month's runs.
    ///
    /// <para>Its tax treatment and whether it counts towards the contribution
    /// base are read from the <c>ALW</c> earning type, so they are the company's
    /// to set on Payroll Setup rather than this engine's to assume.</para>
    /// </summary>
    private static void AddAllowance(
        EmployeePayrollInput input, PayrollContext ctx, Rates rates, TimeSummary time,
        List<PayslipLine> lines)
    {
        if (input.Employee.MonthlyAllowance <= 0m)
            return;

        var runs = Math.Max(1, ctx.Run.RunsInMonth);
        var amount = input.Employee.MonthlyAllowance / runs * time.EmployedShare;

        lines.Add(Earning(ctx, PayComponentCodes.RecurringAllowance, "Allowance", amount, input,
            note: $"Monthly allowance ÷ {runs} run(s) in the month"));
    }

    /// <summary>FR-062. Unused convertible leave, cashed out on separation.</summary>
    private static void AddLeaveConversion(
        EmployeePayrollInput input, PayrollContext ctx, Rates rates, List<PayslipLine> lines)
    {
        if (input.ConvertibleLeaveDays <= 0m)
            return;

        var days = input.ConvertibleLeaveDays;
        var amount = rates.Daily * days;

        // Art. 95 conversion is a de-minimis benefit up to ten days' worth; the
        // excess is ordinary taxable income, so it is a second line rather than a
        // flag on the first.
        var exemptDays = Math.Min(days, ConvertibleLeaveDeMinimisDays);
        var taxableDays = days - exemptDays;

        lines.Add(Earning(ctx, PayComponentCodes.LeaveConversion, "Leave conversion",
            rates.Daily * exemptDays, input,
            quantity: exemptDays, rate: rates.Daily,
            forceTaxable: false,
            note: $"Art. 95 — unused convertible leave, de-minimis up to {ConvertibleLeaveDeMinimisDays:0} day(s)."));

        if (taxableDays > 0m)
        {
            lines.Add(Earning(ctx, PayComponentCodes.LeaveConversion, "Leave conversion (taxable excess)",
                rates.Daily * taxableDays, input,
                quantity: taxableDays, rate: rates.Daily,
                forceTaxable: true,
                note: "Beyond the ten-day de-minimis ceiling, so taxable."));
        }
    }

    /// <summary>
    /// FR-061. A twelfth of the basic salary <em>actually earned</em> in the
    /// calendar year.
    ///
    /// <para>Taken from accumulated payslips rather than from twelve times the
    /// monthly rate, so a mid-year hire and an employee with unpaid leave need no
    /// special case — they simply earned less basic salary. On a final pay run
    /// the basic earned on this very run counts too, which is what makes it
    /// pro-rated (FR-062).</para>
    ///
    /// <para>Non-taxable within the running ₱90,000 benefits exclusion; the
    /// excess is added as its own taxable line rather than by flipping the whole
    /// amount taxable.</para>
    /// </summary>
    private static void AddThirteenthMonth(
        EmployeePayrollInput input, PayrollContext ctx, Payslip payslip,
        List<PayslipLine> lines, bool isFinalPay)
    {
        var basicEarned = input.YearToDate.BasicEarned;

        if (isFinalPay)
            basicEarned += lines.Where(l => l.Code == PayComponentCodes.BasicPay).Sum(l => l.Amount);

        var entitlement = PayrollRounding.Money(basicEarned / 12m);
        var alreadyPaid = input.YearToDate.ThirteenthMonthPaid;
        var due = PayrollRounding.Money(entitlement - alreadyPaid);

        if (due <= 0m)
        {
            lines.Add(Information(PayComponentCodes.ThirteenthMonth,
                "13th month pay — nothing further due",
                0m,
                $"Entitlement {PayrollRounding.Format(entitlement)} against " +
                $"{PayrollRounding.Format(alreadyPaid)} already paid this year."));
            return;
        }

        var poolLeft = Math.Max(0m, BenefitsExclusionCeiling - input.YearToDate.NonTaxableBenefits);
        var exempt = Math.Min(due, poolLeft);
        var taxable = due - exempt;

        if (exempt > 0m)
        {
            lines.Add(Earning(ctx, PayComponentCodes.ThirteenthMonth, "13th month pay", exempt, input,
                forceTaxable: false,
                note: $"PD 851 — {PayrollRounding.Format(basicEarned)} basic earned ÷ 12" +
                      (alreadyPaid > 0m ? $", less {PayrollRounding.Format(alreadyPaid)} already paid" : string.Empty)));
        }

        if (taxable > 0m)
        {
            lines.Add(Earning(ctx, PayComponentCodes.ThirteenthMonth, "13th month pay (taxable excess)",
                taxable, input,
                forceTaxable: true,
                note: $"Beyond the {PayrollRounding.Format(BenefitsExclusionCeiling)} benefits exclusion."));
        }

        payslip.Remarks = string.IsNullOrWhiteSpace(payslip.Remarks)
            ? $"13th month entitlement {PayrollRounding.Format(entitlement)}."
            : payslip.Remarks;
    }

    // =====================================================================
    // Deductions
    // =====================================================================

    /// <summary>
    /// FR-052. Tardiness, undertime and absence, priced off the employee's own
    /// hourly and daily rate.
    ///
    /// <para>Monthly-paid staff only — see <see cref="AddDailyBasic"/> for why.
    /// Unpaid leave is folded in with absence: both are authorised or not, but
    /// neither is paid.</para>
    /// </summary>
    private static void AddTimeDeductions(
        EmployeePayrollInput input, PayrollContext ctx, Rates rates, TimeSummary time,
        List<PayslipLine> lines)
    {
        if (time.LateMinutes > 0)
        {
            var hours = time.LateMinutes / 60m;
            lines.Add(Deduction(ctx, PayComponentCodes.Tardiness, "Tardiness",
                rates.Hourly * hours,
                quantity: hours, rate: rates.Hourly,
                note: $"{time.LateMinutes} minute(s) late, after the schedule's grace period."));
        }

        if (time.UndertimeMinutes > 0)
        {
            var hours = time.UndertimeMinutes / 60m;
            lines.Add(Deduction(ctx, PayComponentCodes.Undertime, "Undertime",
                rates.Hourly * hours,
                quantity: hours, rate: rates.Hourly,
                note: $"{time.UndertimeMinutes} minute(s) of the shift unworked."));
        }

        var unpaidDays = time.AbsentDays + time.UnpaidLeaveDays;

        if (unpaidDays > 0m)
        {
            var note = time.UnpaidLeaveDays > 0m
                ? $"{time.AbsentDays:0.##} absence(s) and {time.UnpaidLeaveDays:0.##} day(s) of unpaid leave."
                : $"{time.AbsentDays:0.##} day(s) absent.";

            lines.Add(Deduction(ctx, PayComponentCodes.Absence, "Absences",
                rates.Daily * unpaidDays,
                quantity: unpaidDays, rate: rates.Daily, note: note));
        }
    }

    /// <summary>
    /// FR-053. SSS, PhilHealth and Pag-IBIG, employee and employer sides, from
    /// the schedules in force on the run's pay date.
    ///
    /// <para>All three are computed on a <b>monthly</b> basis and then apportioned
    /// by <see cref="PayrollContext.ContributionShare"/>, because that is how the
    /// agencies define them. PhilHealth reads monthly <em>basic salary</em>; SSS
    /// and Pag-IBIG read monthly <em>compensation</em>, which includes the
    /// allowances flagged as part of the contribution base.</para>
    /// </summary>
    private static void AddStatutory(
        EmployeePayrollInput input, PayrollContext ctx, Payslip payslip,
        List<PayslipLine> lines, List<string> blockers)
    {
        if (ctx.Run.RunType == PayrollRunType.ThirteenthMonth)
            return;

        var share = ctx.ContributionShare;
        if (share <= 0m)
            return;

        var employee = input.Employee;

        // Monthly compensation for SSS and Pag-IBIG: the basic monthly figure
        // plus the allowances the company has flagged as part of the base,
        // scaled back up to a month. Reading a bracket at a semi-monthly figure
        // would put the employee in a bracket less than half as high.
        //
        // Basic pay is deliberately *not* added back: MonthlyBasis already is
        // the monthly basic salary, and counting the basic line again would
        // double the compensation and push everyone into a bracket they do not
        // belong in.
        var topUp = lines
            .Where(l => l.IsEarning && IsContributionTopUp(ctx, l.Code))
            .Sum(l => l.Amount) * Math.Max(1, ctx.Run.RunsInMonth);

        var compensation = payslip.MonthlyBasis + topUp;

        // ------------------------------------------------------------- SSS
        if (!employee.ExemptFromSss)
        {
            var bracket = ctx.Statutory.SssFor(compensation);

            if (bracket is null)
            {
                blockers.Add("No SSS contribution schedule is in force on the pay date.");
            }
            else
            {
                var ee = PayrollRounding.Money(bracket.EmployeeShare * share);
                var eeWisp = PayrollRounding.Money(bracket.EmployeeWisp * share);

                payslip.EmployerSss = PayrollRounding.Money(bracket.EmployerShare * share);
                payslip.EmployerSssWisp = PayrollRounding.Money(bracket.EmployerWisp * share);
                payslip.EmployerEc = PayrollRounding.Money(bracket.EmployerEc * share);

                if (ee > 0m)
                {
                    payslip.EmployeeSss = ee;
                    lines.Add(Deduction(ctx, PayComponentCodes.Sss, "SSS contribution", ee,
                        note: $"Salary credit {PayrollRounding.Format(bracket.MonthlySalaryCredit)}" +
                              ShareNote(share)));
                }

                if (eeWisp > 0m)
                {
                    payslip.EmployeeSssWisp = eeWisp;
                    lines.Add(Deduction(ctx, PayComponentCodes.SssWisp, "SSS WISP", eeWisp,
                        note: "Provident fund portion of a salary credit above ₱20,000." + ShareNote(share)));
                }
            }
        }

        // ------------------------------------------------------ PhilHealth
        if (!employee.ExemptFromPhilHealth)
        {
            if (ctx.Statutory.PhilHealth is not { } philHealth)
            {
                blockers.Add("No PhilHealth premium is in force on the pay date.");
            }
            else
            {
                var premium = philHealth.PremiumFor(payslip.MonthlyBasis);
                var half = PayrollRounding.Money(premium / 2m);

                var ee = PayrollRounding.Money(half * share);

                // The employer pays the remainder rather than a second rounded
                // half, so the two shares always add back to the premium exactly.
                payslip.EmployerPhilHealth = PayrollRounding.Money((premium - half) * share);

                if (ee > 0m)
                {
                    payslip.EmployeePhilHealth = ee;
                    lines.Add(Deduction(ctx, PayComponentCodes.PhilHealth, "PhilHealth premium", ee,
                        note: $"{philHealth.PremiumRatePercent:0.##}% of monthly basic salary, halved." +
                              ShareNote(share)));
                }
            }
        }

        // --------------------------------------------------------- Pag-IBIG
        if (!employee.ExemptFromPagIbig)
        {
            if (ctx.Statutory.PagIbig is not { } pagIbig)
            {
                blockers.Add("No Pag-IBIG contribution rate is in force on the pay date.");
            }
            else
            {
                var (employeeShare, employerShare) = pagIbig.SharesFor(compensation);

                var ee = PayrollRounding.Money(employeeShare * share);
                payslip.EmployerPagIbig = PayrollRounding.Money(employerShare * share);

                if (ee > 0m)
                {
                    payslip.EmployeePagIbig = ee;
                    lines.Add(Deduction(ctx, PayComponentCodes.PagIbig, "Pag-IBIG contribution", ee,
                        note: $"On a fund salary capped at {PayrollRounding.Format(pagIbig.FundSalaryCap)}." +
                              ShareNote(share)));
                }
            }
        }
    }

    /// <summary>
    /// Whether an earning adds to the contribution base <em>on top of</em> the
    /// monthly basic salary. Basic pay itself is already the basis, so it is
    /// excluded however it is flagged.
    /// </summary>
    private static bool IsContributionTopUp(PayrollContext ctx, string code)
    {
        var type = ctx.Earning(code);

        return type is { IsPartOfContributionBase: true } &&
               type.Category != EarningCategory.BasicPay;
    }

    private static string ShareNote(decimal share) =>
        share >= 1m ? " Whole month taken this run." : $" {share:P0} of the month taken this run.";

    /// <summary>
    /// FR-055. Loan and cash-advance instalments, in the deduction types'
    /// priority order so that what falls short is a company deduction rather
    /// than a remittance.
    ///
    /// <para>Nothing is written to the loan here. Calculation reads the balance;
    /// only posting moves it, which is what makes a draft safe to recalculate.</para>
    /// </summary>
    private static void AddLoans(
        EmployeePayrollInput input, PayrollContext ctx,
        List<PayslipLine> lines, List<(EmployeeLoan, decimal)> collections)
    {
        if (ctx.Run.RunType == PayrollRunType.ThirteenthMonth)
            return;

        var ordered = input.Loans
            .Where(l => l.IsCollectable)
            .OrderBy(l => ctx.Deduction(l.DeductionCode)?.Priority ?? 100)
            .ThenBy(l => l.Id);

        foreach (var loan in ordered)
        {
            var due = loan.InstalmentDue(ctx.Run.PayDate);
            if (due <= 0m)
                continue;

            var remaining = PayrollRounding.Money(loan.OutstandingBalance - due);

            lines.Add(Deduction(ctx, loan.DeductionCode, loan.DeductionName, due,
                note: string.IsNullOrWhiteSpace(loan.Reference)
                    ? $"{PayrollRounding.Format(remaining)} outstanding after this instalment."
                    : $"Ref {loan.Reference} · {PayrollRounding.Format(remaining)} outstanding after this instalment."));

            collections.Add((loan, due));
        }
    }

    /// <summary>FR-057. The one-off earnings and deductions entered on this run.</summary>
    private static void AddAdjustments(
        EmployeePayrollInput input, List<PayslipLine> lines, PayslipLineKind kind)
    {
        foreach (var adjustment in input.Adjustments.Where(a => a.Kind == kind))
        {
            lines.Add(new PayslipLine
            {
                Kind = kind,
                Code = adjustment.Code,
                Name = adjustment.Name,
                Amount = PayrollRounding.Money(adjustment.Amount),
                IsTaxable = kind == PayslipLineKind.Earning && adjustment.IsTaxable,
                ReducesTaxableIncome = kind == PayslipLineKind.Deduction && adjustment.ReducesTaxableIncome,
                Sequence = kind == PayslipLineKind.Earning ? 190 : 290,
                Note = adjustment.Remark
            });
        }
    }

    // =====================================================================
    // Withholding tax
    // =====================================================================

    /// <summary>
    /// FR-054. Withholding on compensation, from the brackets in force on the
    /// pay date.
    ///
    /// <para><b>The base</b> is taxable earnings, less the statutory employee
    /// shares taken this run, less any deduction flagged as reducing taxable
    /// income. It is stored unclamped: a period whose deductions exceeded its
    /// taxable pay keeps its negative figure, because the year-end settlement
    /// adds these up and a base rounded to zero overstates the year.</para>
    ///
    /// <para><b>A minimum wage earner</b> pays no tax on basic, holiday, overtime
    /// or night differential pay (RA 9504) — those lines are marked non-taxable
    /// at source, so they simply never enter the base. Their absence deductions
    /// do not reduce it either, since the pay they came out of was never in
    /// it.</para>
    /// </summary>
    private static void AddWithholdingTax(
        EmployeePayrollInput input, PayrollContext ctx, Payslip payslip,
        List<PayslipLine> lines, List<string> blockers)
    {
        var taxableEarnings = lines.Where(l => l.IsEarning && l.IsTaxable).Sum(l => l.Amount);
        var nonTaxableEarnings = lines.Where(l => l.IsEarning && !l.IsTaxable).Sum(l => l.Amount);

        var reducers = lines
            .Where(l => l.IsDeduction && l.ReducesTaxableIncome)
            .Sum(l => l.Amount);

        // A time deduction comes out of taxable pay, so it reduces the base —
        // unless the employee is a minimum wage earner, whose pay was never in
        // the base to begin with.
        if (!input.Employee.IsMinimumWageEarner)
        {
            reducers += lines
                .Where(l => l.IsDeduction && IsTimeDeduction(l.Code))
                .Sum(l => l.Amount);
        }

        var basis = taxableEarnings - reducers;

        payslip.TaxableEarnings = PayrollRounding.Money(taxableEarnings);
        payslip.NonTaxableEarnings = PayrollRounding.Money(nonTaxableEarnings);
        payslip.TaxableIncome = PayrollRounding.Money(basis);

        if (ctx.Statutory.TaxBrackets.Count == 0)
        {
            blockers.Add("No withholding tax table is in force on the pay date.");
            return;
        }

        decimal tax;

        if (ctx.SettlesTheYear)
        {
            // RR 11-2018 §2.79(B)(5). The period table is an estimate applied
            // twenty-four times; this is the settlement. A negative result is a
            // genuine refund the employer is required to give back through
            // payroll, not an error to be clamped away.
            var annualTaxable = input.YearToDate.TaxableIncome + basis;
            var annualTax = ctx.Statutory.TaxOn(annualTaxable, ctx.Run.Frequency, annual: true);

            tax = PayrollRounding.Money(annualTax - input.YearToDate.TaxWithheld);

            lines.Add(Information("INFO_ANNUAL_TAXABLE", "Annual taxable income", annualTaxable,
                "Year to date plus this period."));

            lines.Add(Information("INFO_ANNUAL_TAX", "Annual tax due", annualTax,
                "From the annual table, not the period table."));

            lines.Add(Information("INFO_TAX_WITHHELD_YTD", "Tax withheld year to date",
                input.YearToDate.TaxWithheld,
                "What the period tables have already collected."));
        }
        else if (!PayrollEnumNames.HasPublishedTaxTable(ctx.Run.Frequency))
        {
            // Guessing here would under-withhold every period, so it stops.
            blockers.Add(
                $"The BIR publishes no {EmployeeEnumNames.Display(ctx.Run.Frequency).ToLowerInvariant()} " +
                "withholding table. Enter the bands the company withholds on before running this payroll.");
            return;
        }
        else
        {
            tax = ctx.Statutory.TaxOn(basis, ctx.Run.Frequency);
        }

        payslip.WithholdingTax = tax;

        if (tax == 0m)
            return;

        lines.Add(Deduction(ctx, PayComponentCodes.WithholdingTax, "Withholding tax", tax,
            note: ctx.SettlesTheYear
                ? (tax < 0m
                    ? "Year-end adjustment — more was withheld this year than the year turned out to owe."
                    : "Year-end adjustment settling the calendar year against the annual table.")
                : $"{EmployeeEnumNames.Display(ctx.Run.Frequency)} table on " +
                  $"{PayrollRounding.Format(Math.Max(0m, basis))} taxable income."));
    }

    private static bool IsTimeDeduction(string code) =>
        code is PayComponentCodes.Tardiness or PayComponentCodes.Undertime or PayComponentCodes.Absence;

    // =====================================================================
    // Assembly
    // =====================================================================

    /// <summary>
    /// FR-056, FR-063. Totals are the sum of already-rounded lines, so a payslip
    /// adds up exactly against its own itemisation.
    /// </summary>
    private static void Finalise(
        EmployeePayrollInput input, PayrollContext ctx, Payslip payslip,
        List<PayslipLine> lines, List<string> blockers)
    {
        var sequence = 0;
        foreach (var line in lines.OrderBy(l => l.Kind).ThenBy(l => l.Sequence))
            line.Sequence = ++sequence * 10;

        payslip.TotalEarnings = lines.Where(l => l.IsEarning).Sum(l => l.Amount);
        payslip.TotalDeductions = lines.Where(l => l.IsDeduction).Sum(l => l.Amount);
        payslip.GrossPay = payslip.TotalEarnings;
        payslip.NetPay = payslip.TotalEarnings - payslip.TotalDeductions;

        if (payslip.NetPay <= 0m && ctx.Settings.FlagNegativeNetPay)
        {
            payslip.IsFlaggedForReview = true;

            payslip.ReviewNote = payslip.NetPay < 0m
                ? $"Net pay is {PayrollRounding.Format(payslip.NetPay)} — deductions exceed gross pay. " +
                  "Nothing has been truncated; review the deductions before approving."
                : "Net pay is zero. Review before approving.";
        }

        if (blockers.Count > 0 && string.IsNullOrWhiteSpace(payslip.ReviewNote))
        {
            payslip.IsFlaggedForReview = true;
            payslip.ReviewNote = blockers[0];
        }
    }

    // =====================================================================
    // Line construction
    // =====================================================================

    /// <summary>
    /// An earning line. Tax treatment comes from the configured earning type, so
    /// a company that re-flags an allowance changes future payslips and not past
    /// ones — except where the caller forces it, which is how the taxable excess
    /// of a 13th month or a leave conversion is split off.
    /// </summary>
    private static PayslipLine Earning(
        PayrollContext ctx, string typeCode, string name, decimal amount,
        EmployeePayrollInput input,
        decimal quantity = 0m, decimal rate = 0m, decimal multiplier = 0m,
        bool? forceTaxable = null, string? code = null, string note = "")
    {
        var type = ctx.Earning(typeCode);

        // RA 9504: a minimum wage earner's basic, holiday, overtime and night
        // differential pay is exempt. The lines are still produced — marked
        // non-taxable — so they stay visible on the payslip and in the reports.
        var taxable = forceTaxable
                      ?? (type?.IsTaxable ?? true)
                      && !(input.Employee.IsMinimumWageEarner && IsWageExempt(typeCode));

        return new PayslipLine
        {
            Kind = PayslipLineKind.Earning,
            Code = code ?? typeCode,
            Name = type is null || !string.IsNullOrWhiteSpace(name) ? name : type.Name,
            Quantity = Math.Round(quantity, 2, MidpointRounding.AwayFromZero),
            Rate = rate,
            Multiplier = multiplier,
            Amount = PayrollRounding.Money(amount),
            IsTaxable = taxable,
            Sequence = type?.DisplayOrder ?? 150,
            Note = note
        };
    }

    private static bool IsWageExempt(string code) =>
        code is PayComponentCodes.BasicPay
             or PayComponentCodes.Overtime
             or PayComponentCodes.NightDifferential
             or PayComponentCodes.HolidayPay
             or PayComponentCodes.HolidayUnworked
             or PayComponentCodes.RestDayPremium;

    private static PayslipLine Deduction(
        PayrollContext ctx, string typeCode, string name, decimal amount,
        decimal quantity = 0m, decimal rate = 0m, string note = "")
    {
        var type = ctx.Deduction(typeCode);

        return new PayslipLine
        {
            Kind = PayslipLineKind.Deduction,
            Code = typeCode,
            Name = string.IsNullOrWhiteSpace(name) ? type?.Name ?? typeCode : name,
            Quantity = Math.Round(quantity, 2, MidpointRounding.AwayFromZero),
            Rate = rate,
            Amount = PayrollRounding.Money(amount),
            ReducesTaxableIncome = type?.ReducesTaxableIncome ?? false,
            Sequence = type?.DisplayOrder ?? 250,
            Note = note
        };
    }

    private static PayslipLine Information(string code, string name, decimal amount, string note) =>
        new()
        {
            Kind = PayslipLineKind.Information,
            Code = code,
            Name = name,
            Amount = PayrollRounding.Money(amount),
            Sequence = 900,
            Note = note
        };
}
