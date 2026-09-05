using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// Every statutory schedule in force on one date, resolved in a single read.
///
/// <para>C-02 is the reason this exists as a snapshot rather than as four
/// lookups. A payroll run must use the tables that were in force on <b>its</b>
/// pay date — not today's — and a run recomputed next year has to produce the
/// same figures it produced when it was posted. Loading the schedules once,
/// against one date, is what makes that true by construction instead of by
/// remembering to pass the date to four different queries.</para>
/// </summary>
public sealed class StatutorySnapshot
{
    private readonly IReadOnlyList<SssBracket> _sss;
    private readonly IReadOnlyList<WithholdingTaxBracket> _tax;

    public StatutorySnapshot(
        DateTime asOf,
        IEnumerable<SssBracket> sss,
        PhilHealthRate? philHealth,
        PagIbigRate? pagIbig,
        IEnumerable<WithholdingTaxBracket> tax)
    {
        AsOf = asOf.Date;
        PhilHealth = philHealth;
        PagIbig = pagIbig;

        // Only rows already in force, and only the newest schedule among them:
        // a table dated next January must not start applying in December.
        _sss = Newest(sss.Where(b => b.IsActive && b.EffectiveFrom.Date <= AsOf), b => b.EffectiveFrom);
        _tax = Newest(tax.Where(b => b.IsActive && b.EffectiveFrom.Date <= AsOf), b => b.EffectiveFrom);
    }

    public DateTime AsOf { get; }

    public PhilHealthRate? PhilHealth { get; }

    public PagIbigRate? PagIbig { get; }

    public IReadOnlyList<SssBracket> SssSchedule => _sss;

    public IReadOnlyList<WithholdingTaxBracket> TaxBrackets => _tax;

    /// <summary>True when all four schedules are present, so a run can proceed.</summary>
    public bool IsComplete =>
        _sss.Count > 0 && PhilHealth is not null && PagIbig is not null && _tax.Count > 0;

    /// <summary>What is missing, phrased for the setup screen.</summary>
    public IReadOnlyList<string> Missing
    {
        get
        {
            var missing = new List<string>();

            if (_sss.Count == 0)
                missing.Add("SSS contribution schedule");

            if (PhilHealth is null)
                missing.Add("PhilHealth premium rate");

            if (PagIbig is null)
                missing.Add("Pag-IBIG contribution rate");

            if (_tax.Count == 0)
                missing.Add("withholding tax brackets");

            return missing;
        }
    }

    /// <summary>
    /// The SSS bracket a month's compensation falls into.
    ///
    /// <para>Returns null only when the schedule is missing altogether. A salary
    /// below the floor or above the ceiling still lands in a bracket, because
    /// the first and last ones are open-ended — the contribution is bounded, not
    /// the salary.</para>
    /// </summary>
    public SssBracket? SssFor(decimal monthlyCompensation) =>
        _sss.FirstOrDefault(b => b.Covers(monthlyCompensation))
        ?? _sss.OrderByDescending(b => b.MonthlySalaryCredit).FirstOrDefault();

    /// <summary>
    /// The tax band a taxable amount falls into, for a pay frequency or for the
    /// annual settlement.
    /// </summary>
    public WithholdingTaxBracket? TaxBandFor(decimal taxableIncome, PayFrequency frequency, bool annual = false)
    {
        var set = _tax.Where(b => b.IsAnnual == annual && (annual || b.Frequency == frequency)).ToList();

        return set.FirstOrDefault(b => b.Covers(taxableIncome))
               ?? set.OrderByDescending(b => b.LowerLimit).FirstOrDefault();
    }

    /// <summary>
    /// The tax on an amount. A negative base produces no tax rather than a
    /// negative withholding — an over-deduction is settled at year end, not by
    /// handing money back mid-year on one period's arithmetic.
    /// </summary>
    public decimal TaxOn(decimal taxableIncome, PayFrequency frequency, bool annual = false)
    {
        if (taxableIncome <= 0)
            return 0m;

        return TaxBandFor(taxableIncome, frequency, annual)?.TaxOn(taxableIncome) ?? 0m;
    }

    /// <summary>
    /// Keeps only the rows belonging to the newest effective date in the set, so
    /// two overlapping schedules cannot be read as one merged table.
    /// </summary>
    private static IReadOnlyList<T> Newest<T>(IEnumerable<T> rows, Func<T, DateTime> effective)
    {
        var list = rows.ToList();
        if (list.Count == 0)
            return list;

        var latest = list.Max(effective).Date;
        return list.Where(r => effective(r).Date == latest).ToList();
    }
}
