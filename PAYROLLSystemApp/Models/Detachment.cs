using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// The regional wage boards a Luzon detachment can sit under.
///
/// <para>Held because the daily rate a guard is paid is set by a regional wage
/// order, not by the company: the same position at the same agency is paid
/// differently in Metro Manila and in Bicol. The region is on the detachment
/// rather than on the employee — a person's pay follows the post they are
/// deployed to, not where they live.</para>
///
/// <para>Luzon only, which is the area the system covers. The numbering is the
/// wage boards' own (Region IV split into IV-A and MIMAROPA), so a rate can be
/// checked against a published wage order without a translation step.</para>
/// </summary>
public enum LuzonRegion
{
    NationalCapitalRegion = 0,
    Cordillera = 1,
    Ilocos = 2,
    CagayanValley = 3,
    CentralLuzon = 4,
    Calabarzon = 5,
    Mimaropa = 6,
    Bicol = 7
}

public static class LuzonRegionNames
{
    public static string Display(LuzonRegion region) => region switch
    {
        LuzonRegion.NationalCapitalRegion => "NCR — National Capital Region",
        LuzonRegion.Cordillera => "CAR — Cordillera",
        LuzonRegion.Ilocos => "Region I — Ilocos",
        LuzonRegion.CagayanValley => "Region II — Cagayan Valley",
        LuzonRegion.CentralLuzon => "Region III — Central Luzon",
        LuzonRegion.Calabarzon => "Region IV-A — CALABARZON",
        LuzonRegion.Mimaropa => "MIMAROPA — Region IV-B",
        LuzonRegion.Bicol => "Region V — Bicol",
        _ => region.ToString()
    };

    /// <summary>The short form, for a column on a printed report.</summary>
    public static string Short(LuzonRegion region) => region switch
    {
        LuzonRegion.NationalCapitalRegion => "NCR",
        LuzonRegion.Cordillera => "CAR",
        LuzonRegion.Ilocos => "I",
        LuzonRegion.CagayanValley => "II",
        LuzonRegion.CentralLuzon => "III",
        LuzonRegion.Calabarzon => "IV-A",
        LuzonRegion.Mimaropa => "IV-B",
        LuzonRegion.Bicol => "V",
        _ => string.Empty
    };

    public static IReadOnlyList<LuzonRegion> All { get; } = Enum.GetValues<LuzonRegion>();
}

/// <summary>
/// A post an employee is deployed to, and the unit the payroll summary is
/// grouped and billed by.
///
/// <para><b>A detachment is where a rate comes from.</b> It is not a label on a
/// department — it carries its own daily rate, so the same guard is paid the
/// Metro Manila rate at one post and the Bicol rate at another. That rate is
/// <see cref="DetachmentRate"/>; without one, payroll for anyone posted here
/// cannot be computed and says so rather than guessing.</para>
///
/// <para><b>The code is the unit the client's timesheet is written in.</b> One
/// client site appears on the accounting sheet as several codes — CS75, CS77,
/// SS04 — each with its own figure, because the rate is what distinguishes
/// them. So each of those is its own detachment here, and
/// <see cref="ClientName"/> is what groups them back together.</para>
///
/// <para><b>Distinct from the department.</b> A department is the internal unit
/// an employee belongs to (Operations, Administration); a detachment is the
/// client site they stand at. Head-office staff have a department and no
/// detachment, and are paid their own rate.</para>
///
/// <para>Deactivated, never deleted, for the reason every reference table here
/// is: a payslip printed two years from now still names the post it was earned
/// at.</para>
/// </summary>
[Table("detachments")]
public class Detachment
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>
    /// The code quoted on a billing statement, e.g. "AYL-7". Unique and
    /// upper-cased, because it is matched against a client's own paperwork.
    /// </summary>
    [Indexed(Name = "ux_detachments_code", Order = 1, Unique = true), MaxLength(20), NotNull]
    public string Code { get; set; } = string.Empty;

    [MaxLength(80), NotNull]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The client this post belongs to, e.g. "SM City San Jose Del Monte".
    /// Several codes share one client, and the timesheet is filed under it —
    /// one sheet per client per cut-off, banded by code.
    /// </summary>
    [Indexed, MaxLength(120)]
    public string ClientName { get; set; } = string.Empty;

    /// <summary>
    /// The billing group the sheet is filed under, e.g. "C". Free text and
    /// optional; it is the first thing the keyer is asked for, so it is held
    /// to narrow the list of clients rather than to compute anything.
    /// </summary>
    [Indexed, MaxLength(20)]
    public string GroupCode { get; set; } = string.Empty;

    /// <summary>Which wage board's orders set the rates below.</summary>
    public LuzonRegion Region { get; set; } = LuzonRegion.NationalCapitalRegion;

    /// <summary>Where the post actually is. Free text; the region is the part that pays.</summary>
    [MaxLength(200)]
    public string Location { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public string RegionDisplay => LuzonRegionNames.Display(Region);

    [Ignore]
    public string Display => $"{Code} — {Name}";

    /// <summary>The client, falling back to the post's own name when none is set.</summary>
    [Ignore]
    public string ClientDisplay =>
        string.IsNullOrWhiteSpace(ClientName) ? Name : ClientName;

    [Ignore]
    public string Filed => $"{Code} · {Name} · {LuzonRegionNames.Short(Region)}";
}

/// <summary>
/// The daily rate one position is paid at one detachment, from a date.
///
/// <para><b>Effective-dated, like every other rate in this system.</b> A
/// regional wage order raises the rate for everyone at a post on a stated day.
/// Editing one figure in place would silently change what a recomputed earlier
/// run pays, so a new rate is a <em>new row</em> and the run reads whichever row
/// was in force on its pay date. That is the same rule the SSS schedule, the tax
/// brackets and the premium matrix already follow.</para>
///
/// <para><b>The rate hangs off the detachment alone.</b> It is not split by
/// position: the client's own paperwork quotes one figure against a code —
/// "CS75 (600.00 per day)" — and a post that pays two figures is two codes.
/// So a detachment is unique only together with the date, and three rows for
/// one detachment are three wage orders.</para>
/// </summary>
[Table("detachment_rates")]
public class DetachmentRate
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed(Name = "ix_detachment_rates_lookup", Order = 1)]
    public int DetachmentId { get; set; }

    /// <summary>The rate for a day's work, before any premium.</summary>
    public decimal DailyRate { get; set; }

    /// <summary>
    /// The day this rate starts applying, read against a run's <b>pay date</b> —
    /// the same date the statutory tables are read at, so a run cannot take its
    /// wage rate from one month and its contribution schedule from another.
    /// </summary>
    [Indexed]
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;

    /// <summary>
    /// The wage order or memo this rate came from. <b>Optional</b> — a rate with
    /// no reference is posted and read exactly like any other; nothing validates
    /// it and nothing prints it. Kept only so the audit trail can say where a
    /// figure came from when whoever entered it knew.
    /// </summary>
    [MaxLength(120)]
    public string Reference { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    [MaxLength(120)]
    public string RecordedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public string RateDisplay => $"{DailyRate:N2} / day";

    [Ignore]
    public string EffectiveDisplay => $"from {EffectiveFrom:dd MMM yyyy}";
}

/// <summary>
/// The rates in force on one date, indexed for the engine.
///
/// <para>Built once per run and handed to the calculator, which does no I/O.
/// The shape mirrors <see cref="PremiumMatrix"/> deliberately: latest row not
/// later than the date wins, per detachment, and a post with no rate returns
/// null so the caller stops the run instead of guessing a rate.</para>
/// </summary>
public sealed class DetachmentRateTable
{
    private readonly Dictionary<int, DetachmentRate> _rates;

    public DetachmentRateTable(IEnumerable<DetachmentRate> rates, DateTime asOf)
    {
        AsOf = asOf.Date;

        _rates = rates
            .Where(r => r.IsActive && r.EffectiveFrom.Date <= AsOf)
            .GroupBy(r => r.DetachmentId)
            .ToDictionary(
                g => g.Key,
                // Latest effective date wins; the highest id breaks a tie, so two
                // rows entered for the same day resolve to the one entered last
                // rather than to whichever the database happened to return first.
                g => g.OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id).First());
    }

    public DateTime AsOf { get; }

    public int Count => _rates.Count;

    /// <summary>The rate for a post, or null when none is posted.</summary>
    public DetachmentRate? Get(int detachmentId) =>
        _rates.TryGetValue(detachmentId, out var rate) ? rate : null;

    public decimal? DailyRateFor(int? detachmentId) =>
        detachmentId is { } detachment ? Get(detachment)?.DailyRate : null;
}
