using SQLite;

namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-093. When a backup is taken without anybody asking, and how many are kept.
///
/// <para>One row, always <see cref="SingletonId"/>, like the other settings
/// tables.</para>
///
/// <para><b>"Scheduled" here means checked on use, not run by a timer.</b> A
/// desktop application has nothing running when it is closed, so a nightly job
/// would simply not happen — the machine is off, or the app is not open. What
/// does happen reliably is that somebody signs in, so the interval is checked
/// then and a backup is taken if one is due. The guarantee that gives is
/// weaker and honest: <em>you are never more than one working session away from
/// a backup</em>, rather than a promise of 2 a.m. that would silently not be
/// kept. The same reasoning is why leave accrual is computed on read.</para>
/// </summary>
[Table("backup_settings")]
public class BackupSettings
{
    public const int SingletonId = 1;

    [PrimaryKey]
    public int Id { get; set; } = SingletonId;

    /// <summary>Whether a due backup is taken automatically when the section is opened.</summary>
    public bool IsAutomaticEnabled { get; set; } = true;

    /// <summary>How many days may pass before an automatic backup is due.</summary>
    public int IntervalDays { get; set; } = 1;

    /// <summary>
    /// How many automatic backups to keep. Manual ones are never swept — somebody
    /// took those deliberately, usually just before doing something they were
    /// nervous about.
    /// </summary>
    public int KeepCount { get; set; } = 14;

    public DateTime? LastBackupUtc { get; set; }

    /// <summary>
    /// NFR-007. Years within this window are never offered for purging, however
    /// long ago they closed.
    /// </summary>
    public int RetentionYears { get; set; } = 5;

    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [Ignore]
    public bool IsDue =>
        IsAutomaticEnabled &&
        (LastBackupUtc is not { } last || DateTime.UtcNow - last >= TimeSpan.FromDays(Math.Max(1, IntervalDays)));

    [Ignore]
    public string LastBackupDisplay =>
        LastBackupUtc is { } last
            ? DateTime.SpecifyKind(last, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy HH:mm")
            : "never";

    [Ignore]
    public string IntervalDisplay => IntervalDays switch
    {
        <= 1 => "every day the system is used",
        7 => "weekly",
        _ => $"every {IntervalDays} days"
    };
}

/// <summary>
/// FR-094. A payroll year that has been written out to files, and by whom.
///
/// <para>The row is the receipt. Purging a year is refused unless one of these
/// exists and the files it names are still on disk — an archive nobody can find
/// is not an archive, and finding that out after the purge is too late.</para>
/// </summary>
[Table("archive_records")]
public class ArchiveRecord
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    [Indexed]
    public int Year { get; set; }

    [MaxLength(260)]
    public string FolderPath { get; set; } = string.Empty;

    public int RunCount { get; set; }

    public int PayslipCount { get; set; }

    public int LineCount { get; set; }

    public decimal TotalNet { get; set; }

    [MaxLength(120)]
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the year's rows were afterwards removed from the live database.</summary>
    public bool IsPurged { get; set; }

    public DateTime? PurgedUtc { get; set; }

    [Ignore]
    public string CreatedDisplay =>
        DateTime.SpecifyKind(CreatedUtc, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy HH:mm");

    [Ignore]
    public string SummaryDisplay =>
        $"{RunCount} run(s), {PayslipCount} payslip(s), net {PayrollRounding.Format(TotalNet)}";
}
