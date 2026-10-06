using SQLite;

namespace PAYROLLSystemApp.Models;

public enum SupportRequestKind
{
    Problem = 0,
    Change = 1,
    Question = 2
}

public enum SupportRequestPriority
{
    Low = 0,
    Normal = 1,

    /// <summary>Payroll cannot be finished until it is fixed.</summary>
    Urgent = 2
}

/// <summary>
/// A problem report or change request written in the app and sent to the
/// developer as a zip file. The row is the company's own record of what was
/// asked and when; the reference ties it to the developer's revision log and,
/// once done, to the release notes of the version that answers it.
/// </summary>
[Table("support_request")]
public class SupportRequest
{
    [PrimaryKey, AutoIncrement]
    public int Id { get; set; }

    /// <summary>REQ-yyMMdd-HHmm, e.g. REQ-261006-1430.</summary>
    [Indexed(Unique = true), MaxLength(20)]
    public string Reference { get; set; } = string.Empty;

    public SupportRequestKind Kind { get; set; }

    public SupportRequestPriority Priority { get; set; } = SupportRequestPriority.Normal;

    [MaxLength(150)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string Details { get; set; } = string.Empty;

    /// <summary>The screen it is about, as its sidebar title.</summary>
    [MaxLength(80)]
    public string Screen { get; set; } = string.Empty;

    [MaxLength(20)]
    public string AppVersion { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    [MaxLength(120)]
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>The zip that was written; may since have been moved or deleted.</summary>
    [MaxLength(400)]
    public string PackagePath { get; set; } = string.Empty;

    [Ignore]
    public DateTime CreatedLocal => DateTime.SpecifyKind(CreatedUtc, DateTimeKind.Utc).ToLocalTime();
}
