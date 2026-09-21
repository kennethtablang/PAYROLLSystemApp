namespace PAYROLLSystemApp.Models;

/// <summary>
/// What a column holds. It decides alignment on screen, the number format in a
/// file, and — the part that matters — whether the exporter may write the value
/// as a bare number a spreadsheet can sum.
/// </summary>
public enum ReportColumnKind
{
    Text = 0,

    /// <summary>Money. Two decimal places, right aligned, summable.</summary>
    Money = 1,

    /// <summary>A count, an hour figure, a day figure. Right aligned, summable.</summary>
    Number = 2,

    Date = 3
}

/// <summary>How loudly a note should be read.</summary>
public enum ReportNoteSeverity
{
    /// <summary>Context. A footnote about what a column means.</summary>
    Information = 0,

    /// <summary>Something is missing or provisional. The report is still usable.</summary>
    Warning = 1,

    /// <summary>The report must not be filed or paid from. Leads the page.</summary>
    Blocking = 2
}

/// <summary>
/// One statement a report makes about itself: a footnote, a missing member
/// number, a failed reconciliation.
///
/// <para>Notes travel with the grid rather than being written to a log, because
/// the person holding the printout is the person who has to see them. A report
/// that quietly disagrees with the payroll it came from is worse than no
/// report.</para>
/// </summary>
public sealed record ReportNote(ReportNoteSeverity Severity, string Text)
{
    public static ReportNote Info(string text) => new(ReportNoteSeverity.Information, text);

    public static ReportNote Warn(string text) => new(ReportNoteSeverity.Warning, text);

    public static ReportNote Blocking(string text) => new(ReportNoteSeverity.Blocking, text);
}

/// <summary>One column of a <see cref="ReportGrid"/>.</summary>
/// <param name="Key">Stable identifier — a pay code, a field name. Not shown.</param>
/// <param name="Header">What the column is called on screen and in the file.</param>
/// <param name="Width">Rendered width in device units on screen; scaled for the PDF.</param>
public sealed record ReportColumn(
    string Key,
    string Header,
    ReportColumnKind Kind = ReportColumnKind.Text,
    double Width = 110)
{
    public bool IsNumeric => Kind is ReportColumnKind.Money or ReportColumnKind.Number;
}

/// <summary>
/// One cell.
///
/// <para><b>Blank is not zero.</b> A cell with no value carries
/// <see cref="Value"/> null and renders as empty everywhere — "this employee has
/// no SSS loan" and "this employee's SSS loan took nothing this cut-off" are
/// different statements, and a register that prints ₱0.00 for both has lost the
/// difference.</para>
/// </summary>
public sealed record ReportCell(string Text, decimal? Value = null)
{
    public static readonly ReportCell Blank = new(string.Empty);

    public static ReportCell Of(string? text) => new(text ?? string.Empty);

    /// <summary>A money cell, or a blank one when there is nothing to say.</summary>
    public static ReportCell Money(decimal? amount) =>
        amount is { } value ? new ReportCell(PayrollRounding.Format(value), value) : Blank;

    public static ReportCell Number(decimal? amount, string format = "0.##") =>
        amount is { } value ? new ReportCell(value.ToString(format), value) : Blank;

    public static ReportCell Count(int value) => new(value.ToString(), value);

    public static ReportCell Date(DateTime? value) =>
        value is { } date ? new ReportCell(date.ToString("dd MMM yyyy")) : Blank;
}

/// <summary>
/// One row. <see cref="IsTotal"/> marks the grand total, which is emphasised on
/// screen and in the PDF and is written last to the CSV.
/// </summary>
public sealed record ReportRow(IReadOnlyList<ReportCell> Cells, bool IsTotal = false, bool IsFlagged = false);

/// <summary>
/// FR-080 – FR-085. A rendered report: columns, rows, and what the report has to
/// say about its own trustworthiness.
///
/// <para><b>Every report renders through this one shape</b>, and the CSV and PDF
/// exporters are written against it rather than against any particular report.
/// Section 2.8 has six reports and two file formats; without this it would have
/// twelve renderers, and the twelfth would disagree with the first.</para>
///
/// <para><b>Columns are discovered where the data decides them.</b> The payroll
/// register's columns are whichever pay codes actually occurred on the run — a
/// fixed list would silently drop a code added to the engine later, and a
/// dropped deduction column is a register that does not add up.</para>
/// </summary>
public sealed class ReportGrid
{
    public ReportGrid(
        string title,
        string subtitle,
        IReadOnlyList<ReportColumn> columns,
        IReadOnlyList<ReportRow> rows,
        IReadOnlyList<ReportNote>? notes = null,
        string fileStem = "",
        int keyColumnCount = 2)
    {
        Title = title;
        Subtitle = subtitle;
        Columns = columns;
        Rows = rows;
        Notes = notes ?? [];
        FileStem = string.IsNullOrWhiteSpace(fileStem) ? Slug(title) : fileStem;
        KeyColumnCount = keyColumnCount;
    }

    public string Title { get; }

    /// <summary>The scope: which run, which month, which range.</summary>
    public string Subtitle { get; }

    public IReadOnlyList<ReportColumn> Columns { get; }

    public IReadOnlyList<ReportRow> Rows { get; }

    public IReadOnlyList<ReportNote> Notes { get; }

    /// <summary>Base name of an exported file, without an extension.</summary>
    public string FileStem { get; }

    /// <summary>
    /// How many leading columns identify a row, and so are repeated on every
    /// page a wide table is carried onto.
    ///
    /// <para>Two — employee number and name — for most reports. The payroll
    /// summary splits the name across surname, given name and initial, so two
    /// columns would carry a page headed by a column of surnames with no given
    /// names beside them.</para>
    /// </summary>
    public int KeyColumnCount { get; }

    /// <summary>Rows excluding the grand total — what "12 employees" counts.</summary>
    public int DataRowCount => Rows.Count(r => !r.IsTotal);

    public bool IsEmpty => DataRowCount == 0;

    /// <summary>
    /// True when the report has said it must not be filed or paid from. The
    /// screen leads with it and the exporters print it above the table.
    /// </summary>
    public bool IsBlocked => Notes.Any(n => n.Severity == ReportNoteSeverity.Blocking);

    public IEnumerable<ReportNote> BlockingNotes =>
        Notes.Where(n => n.Severity == ReportNoteSeverity.Blocking);

    /// <summary>An empty report still renders — with its columns and its reason.</summary>
    public static ReportGrid Empty(string title, string subtitle, string reason, string fileStem = "") =>
        new(title, subtitle, [new ReportColumn("note", "")], [], [ReportNote.Warn(reason)], fileStem);

    private static string Slug(string value)
    {
        var cleaned = new string(value
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray());

        while (cleaned.Contains("--"))
            cleaned = cleaned.Replace("--", "-");

        return cleaned.Trim('-');
    }
}
