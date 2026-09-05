using System.Globalization;
using System.Text;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>FR-085. The file formats a report can be taken away in.</summary>
public enum ReportFormat
{
    /// <summary>Opens in Excel, and every figure is a number the sheet can sum.</summary>
    Csv = 0,

    /// <summary>Printable, and what gets filed or signed.</summary>
    Pdf = 1
}

/// <summary>
/// FR-085. Renders any <see cref="ReportGrid"/> to CSV.
///
/// <para><b>Money is written as a bare number</b> — <c>1234.56</c>, no currency
/// symbol, no thousands separator, invariant decimal point. The whole reason to
/// export a register to a spreadsheet is to sum a column in it, and
/// <c>₱1,234.56</c> is text. The currency is stated once in the header block
/// instead.</para>
///
/// <para><b>A blank cell stays blank</b>, never 0 — see <see cref="ReportCell"/>.
/// A spreadsheet sums blanks as nothing, which is the intended reading.</para>
/// </summary>
public static class ReportCsv
{
    public static byte[] Render(ReportGrid grid)
    {
        var builder = new StringBuilder();

        Comment(builder, grid.Title);
        Comment(builder, grid.Subtitle);
        Comment(builder, "All amounts in Philippine pesos (PHP)");
        Comment(builder, $"Generated {DateTime.Now:dd MMM yyyy HH:mm}");

        foreach (var note in grid.Notes.OrderByDescending(n => n.Severity))
            Comment(builder, $"{Prefix(note.Severity)}{note.Text}");

        builder.AppendLine();

        builder.AppendLine(string.Join(',', grid.Columns.Select(c => Quote(c.Header))));

        foreach (var row in grid.Rows)
            builder.AppendLine(string.Join(',', Cells(grid, row)));

        // The BOM is what makes Excel read the file as UTF-8 rather than as the
        // machine's ANSI code page, which is where an employee's name loses its
        // accented characters. It has to be written explicitly: GetBytes never
        // emits the preamble, whatever the encoding was constructed with.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(builder.ToString());

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);

        return bytes;
    }

    private static IEnumerable<string> Cells(ReportGrid grid, ReportRow row)
    {
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var cell = i < row.Cells.Count ? row.Cells[i] : ReportCell.Blank;
            var column = grid.Columns[i];

            if (column.IsNumeric)
            {
                yield return cell.Value is { } value
                    ? value.ToString("0.##", CultureInfo.InvariantCulture)
                    : string.Empty;
            }
            else
            {
                yield return Quote(cell.Text);
            }
        }
    }

    private static void Comment(StringBuilder builder, string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            builder.Append("# ").AppendLine(text);
    }

    private static string Prefix(ReportNoteSeverity severity) => severity switch
    {
        ReportNoteSeverity.Blocking => "DO NOT FILE — ",
        ReportNoteSeverity.Warning => "Check — ",
        _ => string.Empty
    };

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // A leading =, + or - makes Excel treat the cell as a formula. An
        // employee's name will not start with one, but a remark might, and a
        // report is not a place to execute anything.
        var needsGuard = value[0] is '=' or '+' or '@';
        var text = needsGuard ? "'" + value : value;

        return text.Contains(',') || text.Contains('"') || text.Contains('\n')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }
}

