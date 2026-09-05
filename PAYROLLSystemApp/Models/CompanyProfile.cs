using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-045. The employer, as it must appear on a payslip, a remittance report and
/// an alphalist.
///
/// <para>One row, always <see cref="SingletonId"/>. A payroll installation runs
/// one company (C-03), and giving the table a key it does not need would invite
/// a second profile that half the reports would not find.</para>
///
/// <para>The employer registration numbers are held as typed rather than
/// normalised to digits the way an employee's member numbers are: an employer
/// SSS or Pag-IBIG number is not the same shape as a member's, and the figure
/// that has to match the agency's records is whatever appears on the employer's
/// certificate.</para>
/// </summary>
[Table("company_profile")]
public class CompanyProfile
{
    public const int SingletonId = 1;

    [PrimaryKey]
    public int Id { get; set; } = SingletonId;

    [MaxLength(120), NotNull]
    public string RegisteredName { get; set; } = string.Empty;

    /// <summary>Trading name, where it differs from the registered one.</summary>
    [MaxLength(120)]
    public string TradeName { get; set; } = string.Empty;

    [MaxLength(240)]
    public string Address { get; set; } = string.Empty;

    /// <summary>Employer TIN, stored digits only. Printed on every payslip.</summary>
    [MaxLength(20)]
    public string Tin { get; set; } = string.Empty;

    /// <summary>BIR Revenue District Office code the employer files under.</summary>
    [MaxLength(10)]
    public string RdoCode { get; set; } = string.Empty;

    [MaxLength(30)]
    public string SssEmployerNumber { get; set; } = string.Empty;

    [MaxLength(30)]
    public string PhilHealthEmployerNumber { get; set; } = string.Empty;

    [MaxLength(30)]
    public string PagIbigEmployerNumber { get; set; } = string.Empty;

    [MaxLength(60)]
    public string ContactNumber { get; set; } = string.Empty;

    [MaxLength(120)]
    public string EmailAddress { get; set; } = string.Empty;

    /// <summary>
    /// Path to the logo used on payslips and reports. A path rather than the
    /// bytes: the file lives with the installation, and a report that cannot
    /// find it prints without it instead of failing.
    /// </summary>
    [MaxLength(260)]
    public string LogoPath { get; set; } = string.Empty;

    /// <summary>Who signs a payslip or a certificate on the employer's behalf.</summary>
    [MaxLength(120)]
    public string AuthorisedSignatory { get; set; } = string.Empty;

    [MaxLength(80)]
    public string SignatoryPosition { get; set; } = string.Empty;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    [Ignore]
    public string DisplayName =>
        string.IsNullOrWhiteSpace(TradeName) ? RegisteredName : TradeName;

    [Ignore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(RegisteredName);

    [Ignore]
    public string TinDisplay =>
        Security.GovernmentId.Format(Security.GovernmentIdKind.Tin, Tin);

    /// <summary>
    /// What is still missing before a payslip can be printed honestly. Shown on
    /// the setup screen rather than discovered when the first payslip comes out
    /// with a blank header.
    /// </summary>
    [Ignore]
    public IReadOnlyList<string> MissingForPayslip
    {
        get
        {
            var missing = new List<string>();

            if (string.IsNullOrWhiteSpace(RegisteredName))
                missing.Add("registered name");

            if (string.IsNullOrWhiteSpace(Address))
                missing.Add("address");

            if (string.IsNullOrWhiteSpace(Tin))
                missing.Add("employer TIN");

            return missing;
        }
    }
}

/// <summary>
/// The company-wide payroll rules: how a monthly salary becomes a daily and an
/// hourly rate, how a pay calendar is laid out, and where a month's statutory
/// contributions are taken.
///
/// <para>One row, always <see cref="SingletonId"/>, for the same reason as
/// <see cref="CompanyProfile"/>.</para>
///
/// <para><b>The factor is the number to get right.</b> Every premium, every
/// tardiness deduction and every absence is priced off the daily rate it
/// produces. 313 — 365 days less 52 Sundays — is the common choice for monthly-
/// paid staff and means the salary already covers regular holidays; 261 counts
/// only weekdays; 365 counts every day. Changing it changes everyone's pay.</para>
/// </summary>
[Table("payroll_settings")]
public class PayrollSettings
{
    public const int SingletonId = 1;

    [PrimaryKey]
    public int Id { get; set; } = SingletonId;

    public PayFrequency PayFrequency { get; set; } = PayFrequency.SemiMonthly;

    /// <summary>
    /// Days a monthly salary is spread over: DailyRate = Monthly × 12 ÷ factor.
    /// </summary>
    public decimal WorkingDaysFactor { get; set; } = 313m;

    /// <summary>Hours in a standard day: HourlyRate = DailyRate ÷ this.</summary>
    public decimal StandardHoursPerDay { get; set; } = 8m;

    /// <summary>
    /// How many days before the end of a pay period the attendance cut-off
    /// closes, so payroll has time to be processed. Five is common: a 1–15
    /// period cuts off on the 10th.
    /// </summary>
    public int CutOffLeadDays { get; set; } = 5;

    /// <summary>Days after the period end that the pay date falls.</summary>
    public int PayDateLagDays { get; set; }

    /// <summary>Where a month's SSS, PhilHealth and Pag-IBIG are taken.</summary>
    public ContributionSchedule ContributionSchedule { get; set; } =
        ContributionSchedule.LastPayrollOfMonth;

    /// <summary>
    /// RR 11-2018 §2.79(B)(5). When on, the last regular run of the calendar
    /// year and every final-pay run settle the year's tax against the annual
    /// table instead of the per-period one. Companies that square up outside
    /// payroll turn it off.
    /// </summary>
    public bool AnnualiseTaxOnFinalPeriod { get; set; } = true;

    /// <summary>
    /// Art. 94. Whether a daily-paid employee is paid for a regular holiday they
    /// did not work, which is conditioned on being present the preceding workday.
    /// Monthly-paid staff are already covered by the 313 factor.
    /// </summary>
    public bool PayUnworkedRegularHoliday { get; set; } = true;

    /// <summary>
    /// FR-056. A run that would produce a negative net pay is flagged rather
    /// than silently truncated. Off would mean hiding an over-deduction.
    /// </summary>
    public bool FlagNegativeNetPay { get; set; } = true;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived

    /// <summary>§5.2. The daily rate a monthly salary produces.</summary>
    public decimal DailyRateFor(decimal monthlyRate) =>
        WorkingDaysFactor <= 0
            ? 0m
            : PayrollRounding.Rate(monthlyRate * 12m / WorkingDaysFactor);

    /// <summary>§5.2. The hourly rate a monthly salary produces.</summary>
    public decimal HourlyRateFor(decimal monthlyRate) =>
        StandardHoursPerDay <= 0
            ? 0m
            : PayrollRounding.Rate(DailyRateFor(monthlyRate) / StandardHoursPerDay);

    [Ignore]
    public string FrequencyDisplay => EmployeeEnumNames.Display(PayFrequency);

    [Ignore]
    public string ContributionDisplay => PayrollEnumNames.Display(ContributionSchedule);

    [Ignore]
    public string FactorDisplay => WorkingDaysFactor switch
    {
        313m => "313 — 365 days less 52 Sundays; a monthly salary covers regular holidays",
        261m => "261 — weekdays only",
        365m => "365 — every calendar day",
        393.5m => "393.5 — includes a paid Saturday half-day",
        _ => $"{WorkingDaysFactor:0.##} days"
    };

    /// <summary>The standard factors, offered rather than typed.</summary>
    public static IReadOnlyList<decimal> CommonFactors { get; } = [313m, 261m, 365m, 393.5m];
}

/// <summary>
/// FR-063. The one rounding rule, in one place.
///
/// <para><b>Half away from zero, two decimal places, at the line.</b> Banker's
/// rounding — .NET's default — sends half-pesos alternately up and down, which
/// is defensible statistically and indefensible to an employee comparing two
/// payslips. Header totals are the sum of already-rounded lines, so a payslip
/// always adds up exactly rather than differing from its own lines by a
/// centavo.</para>
///
/// <para>Rates are the exception: a daily rate carried at two places loses up to
/// half a centavo an hour, which is visible across a month, so intermediate
/// rates are held at four and only the money is rounded.</para>
/// </summary>
public static class PayrollRounding
{
    public const int MoneyDecimals = 2;
    public const int RateDecimals = 4;

    /// <summary>Rounds a money amount for a payslip line.</summary>
    public static decimal Money(decimal value) =>
        Math.Round(value, MoneyDecimals, MidpointRounding.AwayFromZero);

    /// <summary>Rounds an intermediate rate, which is not yet money.</summary>
    public static decimal Rate(decimal value) =>
        Math.Round(value, RateDecimals, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Money as it is printed. The sign goes before the peso symbol — a negative
    /// tax adjustment reads as <c>-₱1,500.00</c>, because <c>₱-1,500.00</c> looks
    /// like a typo.
    /// </summary>
    public static string Format(decimal value) =>
        value < 0 ? $"-₱{Math.Abs(value):N2}" : $"₱{value:N2}";
}
