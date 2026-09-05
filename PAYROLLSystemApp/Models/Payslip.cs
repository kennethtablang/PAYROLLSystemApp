using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>What a payslip line is doing to the total.</summary>
public enum PayslipLineKind
{
    Earning = 0,
    Deduction = 1,

    /// <summary>
    /// Neither. A figure printed for explanation — the annual taxable income a
    /// year-end adjustment was worked out from, a loan's remaining balance —
    /// which must never be added to anything.
    /// </summary>
    Information = 2
}

/// <summary>
/// FR-070. One employee's pay for one run: the header figures, with the
/// itemisation in <see cref="PayslipLine"/>.
///
/// <para><b>Everything is snapshotted.</b> Name, department, rate, the schedule
/// factor — all copied at calculation. A payslip reprinted in three years must
/// come out identical, and by then the employee may have moved department, been
/// given a rise, or left (NFR-009).</para>
///
/// <para><b>Header totals are the sum of rounded lines</b>, never a re-rounded
/// sum, so a payslip always adds up exactly against its own itemisation
/// (FR-063).</para>
/// </summary>
[Table("payslips")]
public class Payslip
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ix_payslips_run_employee", Order = 1)]
    public int PayrollRunId { get; set; }

    [Indexed(Name = "ix_payslips_run_employee", Order = 2)]
    public int EmployeeId { get; set; }

    // --------------------------------------------- employee, snapshotted

    [MaxLength(20)]
    public string EmployeeNumber { get; set; } = string.Empty;

    [MaxLength(140)]
    public string EmployeeName { get; set; } = string.Empty;

    [MaxLength(80)]
    public string DepartmentName { get; set; } = string.Empty;

    [MaxLength(80)]
    public string PositionTitle { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Tin { get; set; } = string.Empty;

    [MaxLength(20)]
    public string SssNumber { get; set; } = string.Empty;

    [MaxLength(20)]
    public string PhilHealthNumber { get; set; } = string.Empty;

    [MaxLength(20)]
    public string PagIbigNumber { get; set; } = string.Empty;

    [MaxLength(60)]
    public string BankName { get; set; } = string.Empty;

    [MaxLength(40)]
    public string BankAccountNumber { get; set; } = string.Empty;

    // ------------------------------------------------ period, snapshotted

    [MaxLength(20)]
    public string PeriodCode { get; set; } = string.Empty;

    public DateTime PeriodStart { get; set; }

    public DateTime PeriodEnd { get; set; }

    public DateTime PayDate { get; set; }

    // --------------------------------------------------- rates, resolved

    public PayType PayType { get; set; } = PayType.Monthly;

    /// <summary>The employee's rate as configured, read according to <see cref="PayType"/>.</summary>
    public decimal BasicRate { get; set; }

    public decimal DailyRate { get; set; }

    public decimal HourlyRate { get; set; }

    /// <summary>The factor the daily rate came from. Shown so the arithmetic can be followed.</summary>
    public decimal WorkingDaysFactor { get; set; }

    /// <summary>
    /// Monthly compensation as the statutory schedules read it. Held because a
    /// semi-monthly figure would put the employee in a bracket less than half
    /// as high, and the remittance report has to be able to show the basis.
    /// </summary>
    public decimal MonthlyBasis { get; set; }

    public bool IsMinimumWageEarner { get; set; }

    // -------------------------------------------------- time, from FR-027

    public decimal DaysWorked { get; set; }

    public decimal RegularHours { get; set; }

    public decimal OvertimeHours { get; set; }

    public decimal NightDifferentialHours { get; set; }

    public int LateMinutes { get; set; }

    public int UndertimeMinutes { get; set; }

    public decimal AbsentDays { get; set; }

    public decimal PaidLeaveDays { get; set; }

    public decimal UnpaidLeaveDays { get; set; }

    // ------------------------------------------------------------ money

    public decimal GrossPay { get; set; }

    /// <summary>Earnings that entered the withholding base, before deductions.</summary>
    public decimal TaxableEarnings { get; set; }

    public decimal NonTaxableEarnings { get; set; }

    /// <summary>
    /// FR-054. The base the withholding table was actually read against, after
    /// the statutory shares and any tax-reducing deductions came off.
    ///
    /// <para>Stored <b>unclamped</b> — a period whose deductions exceeded its
    /// taxable pay keeps its negative figure, because the year-end settlement
    /// adds these up and a base rounded to zero overstates the year.</para>
    /// </summary>
    public decimal TaxableIncome { get; set; }

    public decimal EmployeeSss { get; set; }

    public decimal EmployeeSssWisp { get; set; }

    public decimal EmployeePhilHealth { get; set; }

    public decimal EmployeePagIbig { get; set; }

    public decimal WithholdingTax { get; set; }

    public decimal TotalEarnings { get; set; }

    public decimal TotalDeductions { get; set; }

    public decimal NetPay { get; set; }

    // ------------------------------------------- employer cost, FR-053

    public decimal EmployerSss { get; set; }

    public decimal EmployerSssWisp { get; set; }

    public decimal EmployerEc { get; set; }

    public decimal EmployerPhilHealth { get; set; }

    public decimal EmployerPagIbig { get; set; }

    // ------------------------------------------------------------ record

    /// <summary>FR-056. Set when net pay came out at or below zero.</summary>
    public bool IsFlaggedForReview { get; set; }

    [MaxLength(250)]
    public string ReviewNote { get; set; } = string.Empty;

    [MaxLength(250)]
    public string Remarks { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    [Ignore]
    public decimal TotalEmployerShare =>
        EmployerSss + EmployerSssWisp + EmployerEc + EmployerPhilHealth + EmployerPagIbig;

    [Ignore]
    public decimal TotalStatutoryEmployee =>
        EmployeeSss + EmployeeSssWisp + EmployeePhilHealth + EmployeePagIbig;

    [Ignore]
    public string GrossDisplay => PayrollRounding.Format(GrossPay);

    [Ignore]
    public string NetDisplay => PayrollRounding.Format(NetPay);

    [Ignore]
    public string DeductionsDisplay => PayrollRounding.Format(TotalDeductions);

    [Ignore]
    public string TaxDisplay => PayrollRounding.Format(WithholdingTax);

    [Ignore]
    public string RateDisplay => PayType switch
    {
        PayType.Monthly => $"{PayrollRounding.Format(BasicRate)} / month",
        PayType.Daily => $"{PayrollRounding.Format(BasicRate)} / day",
        _ => $"{PayrollRounding.Format(BasicRate)} / hour"
    };

    /// <summary>The time behind the pay, in one line for the run's employee list.</summary>
    [Ignore]
    public string TimeDisplay
    {
        get
        {
            var parts = new List<string> { $"{DaysWorked:0.##} day(s)" };

            if (OvertimeHours > 0)
                parts.Add($"{OvertimeHours:0.##} h OT");

            if (NightDifferentialHours > 0)
                parts.Add($"{NightDifferentialHours:0.##} h ND");

            if (LateMinutes > 0)
                parts.Add($"{LateMinutes}m late");

            if (AbsentDays > 0)
                parts.Add($"{AbsentDays:0.##} absent");

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// FR-070. One itemised line of a payslip.
///
/// <para><b>The line carries its own tax treatment.</b> It is copied from the
/// component at calculation rather than looked up when a report runs, because a
/// company that re-flags an allowance as taxable next year must not thereby
/// change what last year's payslips said.</para>
/// </summary>
[Table("payslip_lines")]
public class PayslipLine
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ix_payslip_lines_payslip", Order = 1)]
    public int PayslipId { get; set; }

    /// <summary>Denormalised so a whole run's lines can be read for a register in one query.</summary>
    [Indexed]
    public int PayrollRunId { get; set; }

    public PayslipLineKind Kind { get; set; } = PayslipLineKind.Earning;

    [MaxLength(24), NotNull]
    public string Code { get; set; } = string.Empty;

    [MaxLength(80), NotNull]
    public string Name { get; set; } = string.Empty;

    /// <summary>Hours, days, or 0 where the line is a flat amount.</summary>
    public decimal Quantity { get; set; }

    /// <summary>The rate the quantity was priced at, or 0 for a flat amount.</summary>
    public decimal Rate { get; set; }

    /// <summary>The multiplier applied, where a premium was involved. 0 where none was.</summary>
    public decimal Multiplier { get; set; }

    /// <summary>Rounded to two places (FR-063). The header is the sum of these.</summary>
    public decimal Amount { get; set; }

    public bool IsTaxable { get; set; }

    public bool ReducesTaxableIncome { get; set; }

    /// <summary>Order on the printed payslip. Lower is higher up.</summary>
    public int Sequence { get; set; }

    /// <summary>How the figure was arrived at, for the line an employee queries.</summary>
    [MaxLength(200)]
    public string Note { get; set; } = string.Empty;

    // -------------------------------------------------- derived display

    [Ignore]
    public bool IsEarning => Kind == PayslipLineKind.Earning;

    [Ignore]
    public bool IsDeduction => Kind == PayslipLineKind.Deduction;

    [Ignore]
    public bool IsInformation => Kind == PayslipLineKind.Information;

    [Ignore]
    public string AmountDisplay => PayrollRounding.Format(Amount);

    /// <summary>
    /// The working, where there is one: "8.00 h × ₱125.00 × 1.25". A flat amount
    /// shows nothing rather than "1 × itself".
    /// </summary>
    [Ignore]
    public string BasisDisplay
    {
        get
        {
            if (Quantity == 0m || Rate == 0m)
                return string.Empty;

            var basis = $"{Quantity:0.##} × {Rate:N2}";

            return Multiplier is 0m or 1m ? basis : $"{basis} × {Multiplier:0.00}";
        }
    }
}
