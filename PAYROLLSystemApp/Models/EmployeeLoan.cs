using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>Where a loan has got to.</summary>
public enum LoanStatus
{
    Active = 0,

    /// <summary>Fully repaid. Kept, because the ledger behind it is a record.</summary>
    Completed = 1,

    /// <summary>Temporarily not deducted — an employee on unpaid leave, say.</summary>
    Suspended = 2,

    /// <summary>Written off or entered in error. Nothing further is taken.</summary>
    Cancelled = 3
}

/// <summary>
/// FR-055. A recurring deduction that carries a balance: an SSS or Pag-IBIG
/// salary loan, a company loan, a cash advance.
///
/// <para><b>The balance is the authority, not the schedule.</b> A loan stops
/// when the outstanding amount reaches zero, and the last instalment is whatever
/// is left rather than the full amortisation — which is how an employee avoids
/// being over-collected by a few pesos on the final period.</para>
///
/// <para><b>The balance moves only when a run is posted.</b> A draft that is
/// recalculated five times must not take five instalments, and a run that is
/// discarded must leave the balance where it was. Calculation reads the balance;
/// posting writes it, once, through <see cref="LoanPayment"/>.</para>
/// </summary>
[Table("employee_loans")]
public class EmployeeLoan
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int EmployeeId { get; set; }

    /// <summary>
    /// The <see cref="DeductionType"/> the instalment is taken under, so the
    /// payslip line and the deduction configuration cannot drift apart.
    /// </summary>
    [MaxLength(20), NotNull]
    public string DeductionCode { get; set; } = string.Empty;

    [MaxLength(60)]
    public string DeductionName { get; set; } = string.Empty;

    /// <summary>The lender's reference — the SSS loan number, the voucher number.</summary>
    [MaxLength(40)]
    public string Reference { get; set; } = string.Empty;

    public decimal PrincipalAmount { get; set; }

    /// <summary>What is taken each period, while the balance covers it.</summary>
    public decimal AmortisationAmount { get; set; }

    public decimal OutstandingBalance { get; set; }

    /// <summary>Instalments are taken from the first period whose pay date is on or after this.</summary>
    public DateTime StartDate { get; set; } = DateTime.Today;

    public LoanStatus Status { get; set; } = LoanStatus.Active;

    [MaxLength(250)]
    public string Remarks { get; set; } = string.Empty;

    [MaxLength(120)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    [Ignore]
    public bool IsCollectable => Status == LoanStatus.Active && OutstandingBalance > 0m;

    [Ignore]
    public decimal AmountPaid => PrincipalAmount - OutstandingBalance;

    [Ignore]
    public string StatusDisplay => Status.ToString();

    [Ignore]
    public string BalanceDisplay => PayrollRounding.Format(OutstandingBalance);

    [Ignore]
    public string Display =>
        $"{DeductionName} · {PayrollRounding.Format(AmortisationAmount)} per period · " +
        $"{PayrollRounding.Format(OutstandingBalance)} outstanding";

    /// <summary>
    /// The instalment due on a run with the given pay date — the amortisation,
    /// or whatever is left if that is less.
    /// </summary>
    public decimal InstalmentDue(DateTime payDate)
    {
        if (!IsCollectable || payDate.Date < StartDate.Date)
            return 0m;

        return PayrollRounding.Money(Math.Min(AmortisationAmount, OutstandingBalance));
    }
}

/// <summary>
/// One instalment actually taken, written when a run is posted.
///
/// <para>The ledger exists so a balance can be explained rather than merely
/// asserted: an employee querying what is left is owed the list of what was
/// taken and when, not a single number that has been decremented by a process
/// nobody can see.</para>
/// </summary>
[Table("loan_payments")]
public class LoanPayment
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int LoanId { get; set; }

    [Indexed]
    public int PayrollRunId { get; set; }

    public int PayslipId { get; set; }

    public int EmployeeId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>The balance after this instalment, so the ledger reconciles on its own.</summary>
    public decimal BalanceAfter { get; set; }

    public DateTime PaidOn { get; set; }

    [MaxLength(30)]
    public string RunReference { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public string AmountDisplay => PayrollRounding.Format(Amount);

    [Ignore]
    public string BalanceDisplay => PayrollRounding.Format(BalanceAfter);
}

/// <summary>
/// FR-057. A one-off earning or deduction applied to one employee on one run.
///
/// <para><b>The remark is required.</b> An unexplained adjustment on a payslip is
/// exactly the thing an employee, an auditor or a labour inspector will ask
/// about, and "the payroll officer typed it" is not an answer. The service
/// refuses a blank one.</para>
///
/// <para>Adjustments belong to the run, not to the employee: discarding a draft
/// takes them with it, which is what makes a draft safe to throw away.</para>
/// </summary>
[Table("payroll_adjustments")]
public class PayrollAdjustment
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ix_adjustments_run_employee", Order = 1)]
    public int PayrollRunId { get; set; }

    [Indexed(Name = "ix_adjustments_run_employee", Order = 2)]
    public int EmployeeId { get; set; }

    /// <summary>Earning or deduction. <see cref="PayslipLineKind.Information"/> is not offered.</summary>
    public PayslipLineKind Kind { get; set; } = PayslipLineKind.Earning;

    [MaxLength(24), NotNull]
    public string Code { get; set; } = string.Empty;

    [MaxLength(80), NotNull]
    public string Name { get; set; } = string.Empty;

    /// <summary>Always positive. The kind decides which way it moves the net.</summary>
    public decimal Amount { get; set; }

    public bool IsTaxable { get; set; } = true;

    /// <summary>Only meaningful on a deduction; ignored on an earning.</summary>
    public bool ReducesTaxableIncome { get; set; }

    [MaxLength(250), NotNull]
    public string Remark { get; set; } = string.Empty;

    [MaxLength(120)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    [Ignore]
    public bool IsEarning => Kind == PayslipLineKind.Earning;

    [Ignore]
    public string KindDisplay => IsEarning ? "Earning" : "Deduction";

    [Ignore]
    public string AmountDisplay =>
        (IsEarning ? "+" : "−") + PayrollRounding.Format(Amount);

    [Ignore]
    public string TaxDisplay => IsEarning
        ? (IsTaxable ? "Taxable" : "Non-taxable")
        : (ReducesTaxableIncome ? "Reduces taxable income" : "After tax");
}
