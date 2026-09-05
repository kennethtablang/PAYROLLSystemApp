using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-058. Where a payroll run has got to.
///
/// <para>The states are a one-way street with a single way back: a
/// <see cref="Draft"/> may be recalculated or discarded freely (FR-059), a run
/// <see cref="ForApproval"/> may be returned to draft, and an
/// <see cref="Approved"/> or <see cref="Posted"/> run may not be edited at all.
/// That last rule is the one the audit log exists to prove.</para>
/// </summary>
public enum PayrollRunStatus
{
    /// <summary>Created, and freely recalculable. Nothing outside the run has changed.</summary>
    Draft = 0,

    /// <summary>Submitted and waiting on an approver. Still returnable to draft.</summary>
    ForApproval = 1,

    /// <summary>Approved. The figures are frozen; only posting remains.</summary>
    Approved = 2,

    /// <summary>
    /// Posted. Attendance is locked, loan balances are decremented and the
    /// payslips are the record. There is no way back from here.
    /// </summary>
    Posted = 3,

    /// <summary>Discarded before approval. Kept so the reference number is never reused.</summary>
    Cancelled = 4
}

/// <summary>
/// What a run is for. The type changes the pipeline, not just the label.
///
/// <para>A <see cref="ThirteenthMonth"/> run takes its own branch entirely: it
/// pays the PD 851 entitlement, charges it against the ₱90,000 benefits
/// exclusion, and takes no contributions and no loan amortisations. A
/// <see cref="FinalPay"/> run adds unused leave conversion and a pro-rated 13th
/// month, and settles the year's tax on the way out because the employee has no
/// later payroll to be squared in.</para>
/// </summary>
public enum PayrollRunType
{
    Regular = 0,
    ThirteenthMonth = 1,
    FinalPay = 2,

    /// <summary>
    /// A correction to a cut-off that has already been paid. It does not
    /// annualise: the period it corrects was settled once already, and doing it
    /// again would collect the same difference twice.
    /// </summary>
    Adjustment = 3
}

/// <summary>
/// FR-050. One payroll run: a pay period, a set of employees, and the payslips
/// computed for them.
///
/// <para><b>The period is snapshotted onto the run.</b> Code, dates and pay date
/// are copied rather than read through the foreign key, because the run has to
/// be reproducible years later (NFR-009) and a pay period can still be edited
/// while it is open.</para>
///
/// <para><b>Totals are stored, not summed on read.</b> A register that adds up
/// differently from the payslips it lists is worse than one that is slow.</para>
/// </summary>
[Table("payroll_runs")]
public class PayrollRun
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>Human key, e.g. "PR-2026-09-2". Unique, and never reused.</summary>
    [Indexed(Name = "ux_payroll_runs_reference", Order = 1, Unique = true), MaxLength(30), NotNull]
    public string ReferenceNumber { get; set; } = string.Empty;

    [Indexed]
    public int PayPeriodId { get; set; }

    // ------------------------------------------- pay period, snapshotted

    [MaxLength(20)]
    public string PeriodCode { get; set; } = string.Empty;

    [MaxLength(60)]
    public string PeriodName { get; set; } = string.Empty;

    public DateTime PeriodStart { get; set; }

    public DateTime PeriodEnd { get; set; }

    public DateTime CutOffStart { get; set; }

    public DateTime CutOffEnd { get; set; }

    /// <summary>
    /// C-02. Which statutory tables the run read. Snapshotted because the whole
    /// point of effective dating is that this answer changes over time.
    /// </summary>
    public DateTime PayDate { get; set; }

    public PayFrequency Frequency { get; set; } = PayFrequency.SemiMonthly;

    /// <summary>Which run of the month this is, and out of how many.</summary>
    public int SequenceInMonth { get; set; } = 1;

    public int RunsInMonth { get; set; } = 1;

    // -------------------------------------------------------------- state

    public PayrollRunType RunType { get; set; } = PayrollRunType.Regular;

    public PayrollRunStatus Status { get; set; } = PayrollRunStatus.Draft;

    // ------------------------------------------------------------ totals

    public int EmployeeCount { get; set; }

    public decimal TotalGross { get; set; }

    public decimal TotalTaxable { get; set; }

    public decimal TotalDeductions { get; set; }

    public decimal TotalNet { get; set; }

    /// <summary>Employer share of SSS, PhilHealth, Pag-IBIG and EC — a cost, not a deduction.</summary>
    public decimal TotalEmployerShare { get; set; }

    /// <summary>FR-056. How many payslips came out at or below zero net pay.</summary>
    public int ExceptionCount { get; set; }

    // ------------------------------------------------------------ record

    [MaxLength(250)]
    public string Remarks { get; set; } = string.Empty;

    [MaxLength(120)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? CalculatedUtc { get; set; }

    [MaxLength(120)]
    public string SubmittedBy { get; set; } = string.Empty;

    public DateTime? SubmittedUtc { get; set; }

    [MaxLength(120)]
    public string ApprovedBy { get; set; } = string.Empty;

    public DateTime? ApprovedUtc { get; set; }

    [MaxLength(120)]
    public string PostedBy { get; set; } = string.Empty;

    public DateTime? PostedUtc { get; set; }

    /// <summary>Why it was returned to draft or discarded. Required for both.</summary>
    [MaxLength(250)]
    public string DecisionRemarks { get; set; } = string.Empty;

    // -------------------------------------------------- derived display

    /// <summary>FR-058, FR-059. Only a draft may be recalculated or edited.</summary>
    [Ignore]
    public bool IsEditable => Status == PayrollRunStatus.Draft;

    /// <summary>An approved or posted run is frozen — FR-058's central rule.</summary>
    [Ignore]
    public bool IsFrozen => Status is PayrollRunStatus.Approved or PayrollRunStatus.Posted;

    [Ignore]
    public bool IsPosted => Status == PayrollRunStatus.Posted;

    [Ignore]
    public bool IsCancelled => Status == PayrollRunStatus.Cancelled;

    [Ignore]
    public bool HasBeenCalculated => CalculatedUtc.HasValue;

    [Ignore]
    public bool HasExceptions => ExceptionCount > 0;

    /// <summary>
    /// True for the run that settles the calendar year's withholding: the last
    /// regular cut-off of the year, or any final pay. Read together with
    /// <see cref="PayrollSettings.AnnualiseTaxOnFinalPeriod"/>.
    ///
    /// <para>The year is taken from the <em>period</em>, not the pay date. A
    /// December cut-off paid in January is still December's payroll, and
    /// settling it against next year would put the adjustment in a year it does
    /// not belong to.</para>
    /// </summary>
    public bool SettlesTheYear(bool isLastRegularOfYear) => RunType switch
    {
        PayrollRunType.FinalPay => true,
        PayrollRunType.Regular => isLastRegularOfYear,
        _ => false
    };

    [Ignore]
    public int PeriodYear => PeriodEnd.Year;

    [Ignore]
    public string StatusDisplay => PayrollEnumNames.Display(Status);

    [Ignore]
    public string TypeDisplay => PayrollEnumNames.Display(RunType);

    [Ignore]
    public string PeriodDisplay =>
        PeriodStart.Month == PeriodEnd.Month && PeriodStart.Year == PeriodEnd.Year
            ? $"{PeriodStart:dd}–{PeriodEnd:dd MMM yyyy}"
            : $"{PeriodStart:dd MMM} – {PeriodEnd:dd MMM yyyy}";

    [Ignore]
    public string PayDateDisplay => PayDate.ToString("dd MMM yyyy");

    [Ignore]
    public string GrossDisplay => PayrollRounding.Format(TotalGross);

    [Ignore]
    public string NetDisplay => PayrollRounding.Format(TotalNet);

    [Ignore]
    public string DeductionsDisplay => PayrollRounding.Format(TotalDeductions);

    [Ignore]
    public string EmployerShareDisplay => PayrollRounding.Format(TotalEmployerShare);

    [Ignore]
    public string HeadcountDisplay => EmployeeCount == 1 ? "1 employee" : $"{EmployeeCount} employees";
}
