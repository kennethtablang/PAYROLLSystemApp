using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// FR-085. Renders any <see cref="ReportGrid"/> to PDF, on the same hand-rolled
/// writer the payslips use.
///
/// <para><b>Orientation follows the table, not the report.</b> A remittance form
/// is eight columns and fits portrait; a payroll register is a column per pay
/// code and does not. The choice is made from the widths the content actually
/// needs, so a register that grows a column does not have to be told twice.</para>
///
/// <para><b>A table too wide for the page is split, not squeezed.</b> Twenty pay
/// codes will not fit across an A4 sheet at a readable size, and scaling them
/// until they do produces a register whose figures read <c>15,0…</c> — which is
/// a register nobody can check. The columns are therefore carried over onto
/// further pages, each repeating the employee's number and name so every page
/// says who each row is.</para>
///
/// <para><b>Blocking notes are printed above the table</b>, on every page. A
/// report that must not be filed from has to say so where the figures are, not
/// in a footnote under them.</para>
/// </summary>
public static class ReportDocument
{
    private const double HeaderFontSize = 7.5;
    private const double BodyFontSize = 7.5;
    private const double RowHeight = 12;
    private const double CellPadding = 4;

    /// <summary>
    /// Columns repeated on every carried-over page so a row can still be
    /// identified. Two is the employee's number and their name on every report
    /// in section 2.8.
    /// </summary>
    private const int KeyColumns = 2;

    /// <summary>
    /// Slack allowed when deciding whether a value fits its column.
    ///
    /// <para>A column's width is its widest value plus the padding, and the room
    /// for a value is that width less the padding again. In binary floating
    /// point those two operations do not always round-trip: <c>29.19 + 8 - 8</c>
    /// comes back a hair <em>under</em> 29.19, so the value that decided the
    /// width fails to fit inside it and is truncated — which is how
    /// <c>1,000.00</c> printed as <c>1,000.…</c> while <c>2,200.00</c> beside it
    /// printed whole. A twentieth of a point is far below anything visible and
    /// well above the error.</para>
    /// </summary>
    private const double FitTolerance = 0.05;

    public static byte[] Render(CompanyProfile company, ReportGrid grid)
    {
        var widths = Measure(grid);
        var total = widths.Sum();

        // Nothing is ever squeezed. A table wider than portrait A4 is turned on
        // its side; one still too wide is carried over onto further pages. The
        // two rules have to agree — deciding orientation on a tolerance the
        // splitter then does not honour is how a form that fits ends up split
        // across two pages anyway.
        var portraitWidth = PdfWriter.A4Short - (2 * PdfPageBuilder.Margin);

        var writer = new PdfWriter(landscape: total > portraitWidth);
        var available = writer.PageWidth - (2 * PdfPageBuilder.Margin);

        var groups = Partition(grid, widths, available);

        for (var i = 0; i < groups.Count; i++)
        {
            if (i > 0)
                writer.NewPage();

            DrawGroup(writer, company, grid, groups[i], widths, available, i, groups.Count);
        }

        return writer.Build();
    }

    // =====================================================================
    // Widths and how the columns are split up
    // =====================================================================

    /// <summary>
    /// How wide each column actually needs to be, in points.
    ///
    /// <para><b>Measured, not taken from the screen layout.</b>
    /// <see cref="ReportColumn.Width"/> is in device-independent units for a
    /// table the reader can scroll sideways; a page cannot be scrolled. Base-14
    /// metrics are published, so the width a column needs is a sum rather than
    /// a guess.</para>
    /// </summary>
    private static double[] Measure(ReportGrid grid)
    {
        var widths = new double[grid.Columns.Count];

        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var needed = PdfWriter.Measure(grid.Columns[i].Header, HeaderFontSize, bold: true);

            foreach (var row in grid.Rows)
            {
                if (i >= row.Cells.Count)
                    continue;

                var text = row.Cells[i].Text;

                if (text.Length > 0)
                    needed = Math.Max(needed, PdfWriter.Measure(text, BodyFontSize, row.IsTotal));
            }

            widths[i] = needed + (2 * CellPadding);
        }

        return widths;
    }

    /// <summary>
    /// Splits the columns into pagefuls, each carrying the key columns first.
    ///
    /// <para>A single column wider than the page gets a page of its own rather
    /// than an empty group — better a truncated heading than an infinite
    /// loop.</para>
    /// </summary>
    private static List<int[]> Partition(ReportGrid grid, double[] widths, double available)
    {
        var count = grid.Columns.Count;
        var keys = Math.Min(KeyColumns, count);
        var keyWidth = Enumerable.Range(0, keys).Sum(i => widths[i]);

        if (widths.Sum() <= available)
            return [Enumerable.Range(0, count).ToArray()];

        var groups = new List<int[]>();
        var current = new List<int>(Enumerable.Range(0, keys));
        var used = keyWidth;

        for (var i = keys; i < count; i++)
        {
            var fits = used + widths[i] <= available;
            var isOnlyTheKeys = current.Count == keys;

            if (!fits && !isOnlyTheKeys)
            {
                groups.Add(current.ToArray());
                current = new List<int>(Enumerable.Range(0, keys)) { i };
                used = keyWidth + widths[i];
                continue;
            }

            current.Add(i);
            used += widths[i];
        }

        if (current.Count > keys || groups.Count == 0)
            groups.Add(current.ToArray());

        return groups;
    }

    // =====================================================================
    // Drawing
    // =====================================================================

    private static void DrawGroup(
        PdfWriter writer, CompanyProfile company, ReportGrid grid, int[] group,
        double[] widths, double available, int index, int groupCount)
    {
        var groupWidths = group.Select(i => widths[i]).ToArray();
        var total = groupWidths.Sum();

        var scale = total <= 0 ? 1d : Math.Min(1d, available / total);

        for (var i = 0; i < groupWidths.Length; i++)
            groupWidths[i] *= scale;

        // Whatever room is left goes to the employee's name, which is the column
        // that most wants it.
        if (groupWidths.Length > 1)
            groupWidths[1] += Math.Max(0, available - groupWidths.Sum());

        var page = new PdfPageBuilder(writer,
            p => DrawHeader(p, company, grid, group, groupWidths, index, groupCount));

        foreach (var row in grid.Rows.Where(r => !r.IsTotal))
        {
            page.EnsureSpace(RowHeight + 30);
            DrawRow(page, grid, row, group, groupWidths);
        }

        foreach (var row in grid.Rows.Where(r => r.IsTotal))
        {
            page.EnsureSpace(RowHeight + 30);
            page.Down(2);
            page.Rule(0.55);
            page.Down(RowHeight);
            DrawRow(page, grid, row, group, groupWidths, bold: true);
        }

        if (grid.IsEmpty)
        {
            page.Down(4);
            page.Text(page.Left, "There is nothing to report for this selection.", 8);
            page.Down(12);
        }

        // The footnotes belong under the report, not under each pageful of its
        // columns, so they are printed once.
        if (index == groupCount - 1)
            DrawFootnotes(page, grid);
    }

    private static void DrawHeader(
        PdfPageBuilder page, CompanyProfile company, ReportGrid grid, int[] group,
        double[] widths, int index, int groupCount)
    {
        page.Text(page.Left, company.IsConfigured ? company.DisplayName : "(company profile not set)",
            12, bold: true);
        page.TextRight(page.Right, grid.Title.ToUpperInvariant(), 11, bold: true);
        page.Down(12);

        if (!string.IsNullOrWhiteSpace(company.Address))
            page.Text(page.Left, company.Address, 7.5);

        page.TextRight(page.Right, grid.Subtitle, 8);
        page.Down(10);

        var registration = new List<string>();

        if (!string.IsNullOrWhiteSpace(company.Tin))
            registration.Add($"TIN {company.TinDisplay}");

        if (!string.IsNullOrWhiteSpace(company.SssEmployerNumber))
            registration.Add($"SSS {company.SssEmployerNumber}");

        if (!string.IsNullOrWhiteSpace(company.PhilHealthEmployerNumber))
            registration.Add($"PhilHealth {company.PhilHealthEmployerNumber}");

        if (!string.IsNullOrWhiteSpace(company.PagIbigEmployerNumber))
            registration.Add($"Pag-IBIG {company.PagIbigEmployerNumber}");

        if (registration.Count > 0)
            page.Text(page.Left, string.Join("   ", registration), 7.5);

        // The peso sign has no WinAnsi code point, so it is stated rather than
        // printed. See PdfWriter.Sanitise.
        page.TextRight(page.Right, $"All amounts in PHP · generated {DateTime.Now:dd MMM yyyy HH:mm}", 7);
        page.Down(11);

        if (groupCount > 1)
        {
            var carried = index + 1 < groupCount
                ? " — the remaining columns continue overleaf."
                : " — the final columns of this report.";

            page.Text(page.Left, $"Columns {index + 1} of {groupCount}{carried}", 7.5, bold: true);
            page.Down(11);
        }

        // FR-081: a form that must not be filed says so above its own figures,
        // on every page, not in a footnote at the end of page three.
        foreach (var note in grid.BlockingNotes)
        {
            page.Band(11, 0.88);
            page.Text(page.Left + CellPadding, $"DO NOT FILE OR PAY FROM THIS REPORT — {note.Text}",
                8, bold: true);
            page.Down(13);
        }

        page.Down(2);
        DrawColumnHeadings(page, grid, group, widths);
    }

    private static void DrawColumnHeadings(
        PdfPageBuilder page, ReportGrid grid, int[] group, double[] widths)
    {
        page.Band(RowHeight + 1, 0.9);

        var x = page.Left;

        for (var i = 0; i < group.Length; i++)
        {
            var column = grid.Columns[group[i]];
            var width = widths[i];
            var text = Fit(column.Header, width, HeaderFontSize, true);

            if (column.IsNumeric)
                page.TextRight(x + width - CellPadding, text, HeaderFontSize, bold: true);
            else
                page.Text(x + CellPadding, text, HeaderFontSize, bold: true);

            x += width;
        }

        page.Down(RowHeight + 2);
    }

    private static void DrawRow(
        PdfPageBuilder page, ReportGrid grid, ReportRow row, int[] group,
        double[] widths, bool bold = false)
    {
        var x = page.Left;

        for (var i = 0; i < group.Length; i++)
        {
            var index = group[i];
            var column = grid.Columns[index];
            var width = widths[i];
            var cell = index < row.Cells.Count ? row.Cells[index] : ReportCell.Blank;

            if (cell.Text.Length > 0)
            {
                var text = Fit(cell.Text, width, BodyFontSize, bold);

                if (column.IsNumeric)
                    page.TextRight(x + width - CellPadding, text, BodyFontSize, bold);
                else
                    page.Text(x + CellPadding, text, BodyFontSize, bold);
            }

            x += width;
        }

        page.Down(RowHeight);
    }

    private static void DrawFootnotes(PdfPageBuilder page, ReportGrid grid)
    {
        var notes = grid.Notes.Where(n => n.Severity != ReportNoteSeverity.Blocking).ToList();

        if (notes.Count == 0)
            return;

        page.EnsureSpace(20 + (notes.Count * 10));
        page.Down(8);
        page.Rule(0.85);
        page.Down(11);

        foreach (var note in notes.OrderByDescending(n => n.Severity))
        {
            var prefix = note.Severity == ReportNoteSeverity.Warning ? "Check: " : "Note: ";
            page.Paragraph(page.Left, prefix + note.Text, page.Width, 7, 9);
        }
    }

    /// <summary>
    /// Truncates a value that will not fit its column rather than letting it run
    /// into the next one. A figure that overlaps its neighbour is a report
    /// nobody can read a column of.
    /// </summary>
    private static string Fit(string text, double width, double size, bool bold)
    {
        var room = width - (2 * CellPadding);

        if (PdfWriter.Measure(text, size, bold) <= room + FitTolerance)
            return text;

        var trimmed = text;

        while (trimmed.Length > 1 && PdfWriter.Measure(trimmed + "…", size, bold) > room)
            trimmed = trimmed[..^1];

        return trimmed + "…";
    }
}
