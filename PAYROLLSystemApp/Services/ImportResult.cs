using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>What an import intends to do with one row, or why it will not.</summary>
public enum ImportRowOutcome
{
    /// <summary>A new record will be created.</summary>
    Create = 0,

    /// <summary>An existing record will be updated.</summary>
    Update = 1,

    /// <summary>Nothing to do — the row matches what is already stored.</summary>
    Unchanged = 2,

    /// <summary>The row will be left alone because it cannot be read or fails validation.</summary>
    Rejected = 3
}

/// <summary>
/// One row of an import file, as the preview shows it.
///
/// <para>Every row appears, including the ones that will do nothing, because
/// "eighteen rows imported" out of a file of twenty is only useful alongside
/// which two did not and why.</para>
/// </summary>
public sealed record ImportRow(
    int LineNumber,
    string Key,
    string Description,
    ImportRowOutcome Outcome,
    string Message)
{
    public bool IsRejected => Outcome == ImportRowOutcome.Rejected;

    public bool WillWrite => Outcome is ImportRowOutcome.Create or ImportRowOutcome.Update;

    public string OutcomeDisplay => Outcome switch
    {
        ImportRowOutcome.Create => "New",
        ImportRowOutcome.Update => "Update",
        ImportRowOutcome.Unchanged => "No change",
        _ => "Rejected"
    };
}

/// <summary>
/// FR-019, FR-028. The outcome of reading an import file: what was detected,
/// what would happen, and what was actually written.
///
/// <para><b>An import previews by default.</b> Nothing is written until the
/// caller asks a second time with <c>commit</c> — a mis-mapped column would
/// otherwise overwrite a month of records with no way back, and the person who
/// chose the file is the only one who can tell whether the mapping is right.</para>
/// </summary>
public sealed record ImportResult(
    bool Succeeded,
    string Message,
    bool WasCommitted,
    IReadOnlyList<ImportRow> Rows,
    IReadOnlyList<string> DetectedColumns,
    IReadOnlyList<string> Warnings)
{
    public static ImportResult Fail(string message) =>
        new(false, message, false, [], [], []);

    public int CreateCount => Rows.Count(r => r.Outcome == ImportRowOutcome.Create);

    public int UpdateCount => Rows.Count(r => r.Outcome == ImportRowOutcome.Update);

    public int UnchangedCount => Rows.Count(r => r.Outcome == ImportRowOutcome.Unchanged);

    public int RejectedCount => Rows.Count(r => r.IsRejected);

    public int WriteCount => CreateCount + UpdateCount;

    public bool HasRejections => RejectedCount > 0;

    public string Summary =>
        WasCommitted
            ? $"{CreateCount} created, {UpdateCount} updated, {UnchangedCount} unchanged, {RejectedCount} rejected."
            : $"Preview: {CreateCount} would be created, {UpdateCount} updated, " +
              $"{UnchangedCount} unchanged, {RejectedCount} rejected.";

    /// <summary>
    /// The error report FR-019 asks for, as text that can be saved beside the
    /// file it describes.
    /// </summary>
    public string BuildReport(string fileName)
    {
        var report = new System.Text.StringBuilder();

        report.AppendLine($"Import report — {fileName}");
        report.AppendLine($"Generated {DateTime.Now:dd MMM yyyy HH:mm}");
        report.AppendLine(WasCommitted ? "Committed." : "Preview only — nothing was written.");
        report.AppendLine();
        report.AppendLine(Summary);
        report.AppendLine();

        if (DetectedColumns.Count > 0)
        {
            report.AppendLine("Columns read");

            foreach (var column in DetectedColumns)
                report.AppendLine($"  {column}");

            report.AppendLine();
        }

        if (Warnings.Count > 0)
        {
            report.AppendLine("Warnings");

            foreach (var warning in Warnings)
                report.AppendLine($"  {warning}");

            report.AppendLine();
        }

        report.AppendLine("Line  Outcome    Key            Detail");

        foreach (var row in Rows)
        {
            report.AppendLine(
                $"{row.LineNumber,4}  {row.OutcomeDisplay,-10} {Truncate(row.Key, 14),-14} " +
                $"{row.Description}{(row.Message.Length > 0 ? " — " + row.Message : string.Empty)}");
        }

        return report.ToString();
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
