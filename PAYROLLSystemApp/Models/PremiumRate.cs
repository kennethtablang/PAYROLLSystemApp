using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-044. One cell of the DOLE premium pay matrix: what a worked hour is
/// multiplied by, given the kind of day it fell on and whether it was overtime.
///
/// <para><b>Data, not code.</b> The multipliers below are the statutory minima
/// under Arts. 87, 93 and 94; a company may pay more, and a wage order or an
/// advisory can move them. Holding them as rows means a change is a payroll
/// officer's edit rather than a release (NFR-031), and means a historical run
/// can be re-read against the rates that were in force when it was computed
/// (C-02) — which is what <see cref="EffectiveFrom"/> is for.</para>
///
/// <para>A combination with no row <b>stops the run</b> rather than defaulting to
/// something plausible: paying 1.00× because a rest-day-holiday row was missing
/// is an underpayment nobody would notice.</para>
/// </summary>
[Table("premium_rates")]
public class PremiumRate
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ux_premium_rates_code", Order = 1), MaxLength(24), NotNull]
    public string Code { get; set; } = string.Empty;

    [MaxLength(80), NotNull]
    public string Name { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The multiplier applied to the hourly rate. 1.00 is an ordinary hour;
    /// 2.00 is a regular holiday. For <see cref="IsAdditive"/> rows it is the
    /// amount <em>added</em> to whatever multiplier already applies.
    /// </summary>
    public decimal Multiplier { get; set; } = 1m;

    /// <summary>The kind of day this cell describes.</summary>
    public HolidayType DayType { get; set; } = HolidayType.None;

    /// <summary>True for the cells that only apply when the day is a rest day.</summary>
    public bool IsRestDay { get; set; }

    /// <summary>True for the cells that apply to hours beyond the standard day.</summary>
    public bool IsOvertime { get; set; }

    /// <summary>
    /// True for night differential, which is not a cell of the matrix at all: it
    /// is 10% <em>added</em> to whichever rate already applies to the hour
    /// (Art. 86), so it multiplies nothing on its own.
    /// </summary>
    public bool IsAdditive { get; set; }

    /// <summary>
    /// True for the regular holiday an employee did <em>not</em> work but is
    /// still paid for under Art. 94. It is matched by code rather than by the
    /// day classification, because it applies to an unworked day.
    /// </summary>
    public bool IsUnworked { get; set; }

    /// <summary>
    /// C-02. The date the multiplier takes effect. A run reads the row in force
    /// on its pay date, so superseding a rate is adding a row with a later date
    /// rather than editing the one that historical payslips were computed from.
    /// </summary>
    [Indexed]
    public DateTime EffectiveFrom { get; set; } = new DateTime(2023, 1, 1);

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    // -------------------------------------------------- derived display

    [Ignore]
    public string MultiplierDisplay => IsAdditive
        ? $"+{Multiplier:0.00}×"
        : $"{Multiplier:0.00}×";

    [Ignore]
    public string EffectiveDisplay => $"from {EffectiveFrom:dd MMM yyyy}";

    [Ignore]
    public string ConditionDisplay
    {
        get
        {
            if (IsAdditive)
                return "Added to the rate already applying";

            var parts = new List<string>
            {
                DayType switch
                {
                    HolidayType.Regular => "Regular holiday",
                    HolidayType.SpecialNonWorking => "Special non-working day",
                    HolidayType.SpecialWorking => "Special working day",
                    _ => "Ordinary day"
                }
            };

            if (IsRestDay)
                parts.Add("rest day");

            if (IsOvertime)
                parts.Add("overtime");

            if (IsUnworked)
                parts.Add("unworked");

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// The premium codes the payroll engine resolves a day to.
/// </summary>
public static class PremiumCodes
{
    public const string Ordinary = "ORD";
    public const string OrdinaryOvertime = "ORD_OT";
    public const string NightDifferential = "ND";

    public const string RestDay = "RD";
    public const string RestDayOvertime = "RD_OT";

    public const string SpecialWorking = "SPL_WORK";
    public const string SpecialNonWorking = "SPL";
    public const string SpecialNonWorkingOvertime = "SPL_OT";
    public const string SpecialNonWorkingRestDay = "SPL_RD";
    public const string SpecialNonWorkingRestDayOvertime = "SPL_RD_OT";

    public const string RegularHoliday = "REG_HOL";
    public const string RegularHolidayOvertime = "REG_HOL_OT";
    public const string RegularHolidayRestDay = "REG_HOL_RD";
    public const string RegularHolidayRestDayOvertime = "REG_HOL_RD_OT";
    public const string RegularHolidayUnworked = "REG_HOL_UNWORKED";
}

/// <summary>
/// The premium matrix resolved for one pay date, so a whole cut-off can be
/// priced from a single read.
///
/// <para>Built the way <see cref="Services.HolidayCalendar"/> is: the rows are
/// loaded once, the effective-dated winner is picked per code, and the engine
/// then asks it questions rather than querying per day.</para>
/// </summary>
public sealed class PremiumMatrix
{
    private readonly Dictionary<string, PremiumRate> _byCode;

    public PremiumMatrix(IEnumerable<PremiumRate> rates, DateTime asOf)
    {
        AsOf = asOf.Date;

        // Latest row not later than the pay date wins, per code. Anything dated
        // in the future belongs to a rate change that has not started yet.
        _byCode = rates
            .Where(r => r.IsActive && r.EffectiveFrom.Date <= AsOf)
            .GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.EffectiveFrom).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    public DateTime AsOf { get; }

    public int Count => _byCode.Count;

    public PremiumRate? Get(string code) =>
        _byCode.TryGetValue(code, out var rate) ? rate : null;

    /// <summary>
    /// The multiplier for a worked hour. Returns null when the combination has
    /// no configured row — the caller stops the run rather than guessing.
    /// </summary>
    public decimal? MultiplierFor(HolidayType dayType, bool isRestDay, bool isOvertime) =>
        Get(CodeFor(dayType, isRestDay, isOvertime))?.Multiplier;

    /// <summary>Art. 86: the amount added for an hour inside 22:00–06:00.</summary>
    public decimal NightDifferentialRate =>
        Get(PremiumCodes.NightDifferential)?.Multiplier ?? 0m;

    /// <summary>
    /// The code a day resolves to. Kept as a pure function so the mapping can be
    /// read in one place instead of being inferred from three flags on a row.
    /// </summary>
    public static string CodeFor(HolidayType dayType, bool isRestDay, bool isOvertime) => dayType switch
    {
        HolidayType.Regular => (isRestDay, isOvertime) switch
        {
            (true, true) => PremiumCodes.RegularHolidayRestDayOvertime,
            (true, false) => PremiumCodes.RegularHolidayRestDay,
            (false, true) => PremiumCodes.RegularHolidayOvertime,
            _ => PremiumCodes.RegularHoliday
        },

        HolidayType.SpecialNonWorking => (isRestDay, isOvertime) switch
        {
            (true, true) => PremiumCodes.SpecialNonWorkingRestDayOvertime,
            (true, false) => PremiumCodes.SpecialNonWorkingRestDay,
            (false, true) => PremiumCodes.SpecialNonWorkingOvertime,
            _ => PremiumCodes.SpecialNonWorking
        },

        // A special *working* day carries no premium of its own, so it follows
        // the ordinary-day cells — including the rest day ones, if it fell on one.
        _ => (isRestDay, isOvertime) switch
        {
            (true, true) => PremiumCodes.RestDayOvertime,
            (true, false) => PremiumCodes.RestDay,
            (false, true) => PremiumCodes.OrdinaryOvertime,
            _ => PremiumCodes.Ordinary
        }
    };

    /// <summary>
    /// Every code the engine can ask for. The setup screen checks the configured
    /// rows against this so a missing cell is found before a payroll run finds it.
    /// </summary>
    public static IReadOnlyList<string> RequiredCodes { get; } =
    [
        PremiumCodes.Ordinary,
        PremiumCodes.OrdinaryOvertime,
        PremiumCodes.NightDifferential,
        PremiumCodes.RestDay,
        PremiumCodes.RestDayOvertime,
        PremiumCodes.SpecialNonWorking,
        PremiumCodes.SpecialNonWorkingOvertime,
        PremiumCodes.SpecialNonWorkingRestDay,
        PremiumCodes.SpecialNonWorkingRestDayOvertime,
        PremiumCodes.RegularHoliday,
        PremiumCodes.RegularHolidayOvertime,
        PremiumCodes.RegularHolidayRestDay,
        PremiumCodes.RegularHolidayRestDayOvertime,
        PremiumCodes.RegularHolidayUnworked
    ];

    /// <summary>The required codes this matrix has no row for.</summary>
    public IReadOnlyList<string> MissingCodes() =>
        RequiredCodes.Where(c => !_byCode.ContainsKey(c)).ToList();
}
