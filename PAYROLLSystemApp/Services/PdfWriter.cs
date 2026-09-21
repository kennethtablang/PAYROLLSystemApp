using System.Globalization;
using System.Text;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

/// <summary>
/// A minimal PDF writer: text, rules and filled rectangles on a page of any
/// size, in the two base-14 Helvetica faces.
///
/// <para><b>Why hand-rolled rather than a library.</b> A payslip is a fixed
/// layout of text at known positions — the smallest problem a PDF library
/// solves. Every general-purpose one either drags in <c>System.Drawing</c>, or
/// needs a per-platform font resolver to run on Android and iOS, for a document
/// that uses two fonts that every PDF reader already has. C-01 wants one
/// codebase across four platforms and C-04 wants as little third-party code
/// near employee data as possible; a few hundred lines that emit the format
/// directly satisfies both, and there is nothing here that a payslip does not
/// need.</para>
///
/// <para><b>Base-14 fonts, not embedded ones.</b> Helvetica and Helvetica-Bold
/// are guaranteed present in every conforming reader, so nothing has to be
/// shipped or resolved. The cost is the encoding: base-14 text is WinAnsi, which
/// has no peso sign. Rather than mangle it, money is printed as plain figures
/// and the currency is stated once in the document header — which is what a
/// payslip does anyway.</para>
///
/// <para>Coordinates are PDF points from the <b>bottom-left</b> of the page, as
/// the format defines them. <see cref="PdfPageBuilder"/> flips that for callers
/// so a layout can be written top-down.</para>
/// </summary>
public sealed class PdfWriter
{
    /// <summary>A4 in points: 210 × 297 mm at 72 dpi.</summary>
    public const double A4Short = ReportPaperSizes.A4Short;

    public const double A4Long = ReportPaperSizes.A4Long;

    private readonly List<StringBuilder> _pages = new();

    private StringBuilder _current = new();

    /// <param name="landscape">
    /// Turns the A4 page on its side. A payslip is a column of figures and fits
    /// portrait; a payroll register is a column per pay code and does not, so
    /// the orientation belongs to the document rather than to the writer.
    /// </param>
    public PdfWriter(bool landscape = false)
        : this(landscape ? A4Long : A4Short, landscape ? A4Short : A4Long)
    {
    }

    /// <summary>
    /// An explicit page box in points, for stock that is not A4.
    ///
    /// <para>Taken already oriented rather than as a size plus a flag: 11 × 14
    /// continuous form is fed one way through a tractor, so "landscape" is not a
    /// choice the document gets to make about it. See
    /// <see cref="ReportPaperSizes.Box"/>, which is where that decision is
    /// taken.</para>
    /// </summary>
    public PdfWriter(double pageWidth, double pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageWidth), "A page has to have a positive size.");

        PageWidth = pageWidth;
        PageHeight = pageHeight;
        _pages.Add(_current);
    }

    /// <summary>True when the page is wider than it is tall.</summary>
    public bool IsLandscape => PageWidth > PageHeight;

    public double PageWidth { get; }

    public double PageHeight { get; }

    public int PageCount => _pages.Count;

    public void NewPage()
    {
        _current = new StringBuilder();
        _pages.Add(_current);
    }

    // ------------------------------------------------------------ drawing

    /// <summary>
    /// Draws text with its baseline at (x, y), measured from the bottom-left.
    ///
    /// <para>Every operation is wrapped in <c>q … Q</c> — save and restore the
    /// graphics state — and text sets its own fill colour. Colour in a content
    /// stream is <b>sticky</b>: a filled rectangle leaves its grey as the
    /// current fill colour, and every glyph drawn after it would come out that
    /// same grey. Isolating each operation is cheaper than remembering to
    /// reset, and the failure it prevents is a page that looks washed out
    /// rather than one that throws.</para>
    /// </summary>
    public void Text(double x, double y, string text, double size = 9, bool bold = false)
    {
        if (string.IsNullOrEmpty(text))
            return;

        _current.Append("q 0 g BT /")
            .Append(bold ? "F2" : "F1")
            .Append(' ').Append(Num(size)).Append(" Tf 1 0 0 1 ")
            .Append(Num(x)).Append(' ').Append(Num(y))
            .Append(" Tm (").Append(Escape(text)).Append(") Tj ET Q\n");
    }

    /// <summary>Draws text ending at x rather than starting there.</summary>
    public void TextRight(double x, double y, string text, double size = 9, bool bold = false) =>
        Text(x - Measure(text, size, bold), y, text, size, bold);

    public void Line(double x1, double y1, double x2, double y2, double width = 0.5, double grey = 0.75)
    {
        _current.Append("q ").Append(Num(grey)).Append(" G ")
            .Append(Num(width)).Append(" w ")
            .Append(Num(x1)).Append(' ').Append(Num(y1)).Append(" m ")
            .Append(Num(x2)).Append(' ').Append(Num(y2)).Append(" l S Q\n");
    }

    public void Rect(double x, double y, double width, double height, double grey = 0.94)
    {
        _current.Append("q ").Append(Num(grey)).Append(" g ")
            .Append(Num(x)).Append(' ').Append(Num(y)).Append(' ')
            .Append(Num(width)).Append(' ').Append(Num(height)).Append(" re f Q\n");
    }

    // ------------------------------------------------------------ output

    /// <summary>
    /// Assembles the document.
    ///
    /// <para>The cross-reference table is the one part of the format that has to
    /// be exact: it holds the byte offset of every object, so the file is built
    /// into a buffer and the offsets are taken from it as objects are written,
    /// rather than computed afterwards from lengths that would have to agree.</para>
    /// </summary>
    public byte[] Build()
    {
        // Latin-1 is WinAnsi's byte-for-byte ancestor for the range this writer
        // admits, so a string that survived Escape encodes one byte per glyph.
        var encoding = Encoding.Latin1;

        using var buffer = new MemoryStream();

        var offsets = new List<long>();

        void Write(string text)
        {
            var bytes = encoding.GetBytes(text);
            buffer.Write(bytes, 0, bytes.Length);
        }

        void BeginObject(string body)
        {
            offsets.Add(buffer.Position);
            Write($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");

        // Object numbers are laid out before anything is written, because a page
        // has to name its content stream and a catalog has to name the pages.
        var pageCount = _pages.Count;
        const int catalog = 1;
        const int pagesNode = 2;
        const int fontRegular = 3;
        const int fontBold = 4;
        var firstPage = 5;
        var firstContent = firstPage + pageCount;

        var kids = string.Join(' ', Enumerable.Range(0, pageCount).Select(i => $"{firstPage + i} 0 R"));

        BeginObject($"<< /Type /Catalog /Pages {pagesNode} 0 R >>");
        BeginObject($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        BeginObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        BeginObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");

        for (var i = 0; i < pageCount; i++)
        {
            BeginObject(
                $"<< /Type /Page /Parent {pagesNode} 0 R " +
                $"/MediaBox [0 0 {Num(PageWidth)} {Num(PageHeight)}] " +
                $"/Resources << /Font << /F1 {fontRegular} 0 R /F2 {fontBold} 0 R >> >> " +
                $"/Contents {firstContent + i} 0 R >>");
        }

        for (var i = 0; i < pageCount; i++)
        {
            var content = _pages[i].ToString();
            var length = encoding.GetByteCount(content);

            BeginObject($"<< /Length {length} >>\nstream\n{content}endstream");
        }

        var xrefPosition = buffer.Position;

        var xref = new StringBuilder();
        xref.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        xref.Append("0000000000 65535 f \n");

        foreach (var offset in offsets)
            xref.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");

        xref.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
            .Append(" /Root ").Append(catalog).Append(" 0 R >>\nstartxref\n")
            .Append(xrefPosition).Append("\n%%EOF\n");

        Write(xref.ToString());

        return buffer.ToArray();
    }

    // ------------------------------------------------------------ text

    /// <summary>
    /// The width of a string at a size, in points.
    ///
    /// <para>Base-14 metrics are fixed by the format, so the widths are a table
    /// rather than a measurement. Right-aligned figures on a payslip need it —
    /// a column of amounts that does not line up is the first thing anyone
    /// notices.</para>
    /// </summary>
    public static double Measure(string text, double size, bool bold)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var widths = bold ? BoldWidths : RegularWidths;
        var total = 0;

        foreach (var ch in Sanitise(text))
        {
            var index = ch - 32;
            total += index >= 0 && index < widths.Length ? widths[index] : 556;
        }

        return total * size / 1000d;
    }

    /// <summary>
    /// Reduces a string to what WinAnsi can carry, mapping the characters this
    /// application actually produces rather than dropping them silently.
    ///
    /// <para>The peso sign is the one that matters: it has no WinAnsi code
    /// point, so it is removed here and the currency is stated in the document
    /// header instead. Emitting a byte the reader would render as something
    /// else would be worse than not emitting it.</para>
    /// </summary>
    public static string Sanitise(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var ch in text)
        {
            switch (ch)
            {
                case '₱':  // ₱
                    break;

                case '–':  // en dash
                case '—':  // em dash
                case '−':  // minus
                    builder.Append('-');
                    break;

                case '‘':
                case '’':
                    builder.Append('\'');
                    break;

                case '“':
                case '”':
                    builder.Append('"');
                    break;

                case '·':  // ·
                    builder.Append('.');
                    break;

                case '•':  // the bullet that masks a bank account number
                    builder.Append('*');
                    break;

                case '×':  // ×
                    builder.Append('x');
                    break;

                case '…':
                    builder.Append("...");
                    break;

                default:
                    builder.Append(ch <= 0xFF && !char.IsControl(ch) ? ch : ' ');
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Escapes the three characters a PDF string literal cannot carry raw.</summary>
    private static string Escape(string text) =>
        Sanitise(text)
            .Replace("\\", "\\\\")
            .Replace("(", "\\(")
            .Replace(")", "\\)");

    private static string Num(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    // Adobe's published widths for the two base-14 faces, code points 32-126,
    // in thousandths of the point size.
    private static readonly int[] RegularWidths =
    [
        278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
        1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
        333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
        556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584
    ];

    private static readonly int[] BoldWidths =
    [
        278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
        556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611,
        975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778,
        667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556,
        333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611,
        611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584
    ];
}

/// <summary>
/// A top-down cursor over a <see cref="PdfWriter"/>: margins, a running Y that
/// counts <em>downwards</em> the way a layout is read, and automatic page breaks.
///
/// <para>PDF measures from the bottom-left, which is right for the format and
/// wrong for writing a document. Everything above this line thinks in PDF
/// coordinates; everything below it thinks in reading order.</para>
/// </summary>
public sealed class PdfPageBuilder
{
    public const double Margin = 42;

    private readonly PdfWriter _writer;
    private readonly Action<PdfPageBuilder>? _header;

    private double _y;

    public PdfPageBuilder(PdfWriter writer, Action<PdfPageBuilder>? header = null)
    {
        _writer = writer;
        _header = header;
        _y = _writer.PageHeight - Margin;

        _header?.Invoke(this);
    }

    public double Left => Margin;

    public double Right => _writer.PageWidth - Margin;

    public double Width => Right - Left;

    /// <summary>How far down the page the cursor is, as a PDF y coordinate.</summary>
    public double Y => _y;

    public void Down(double points) => _y -= points;

    /// <summary>Starts a new page when less than <paramref name="needed"/> points remain.</summary>
    public bool EnsureSpace(double needed)
    {
        if (_y - needed >= Margin)
            return false;

        _writer.NewPage();
        _y = _writer.PageHeight - Margin;
        _header?.Invoke(this);

        return true;
    }

    public void Text(double x, string text, double size = 9, bool bold = false) =>
        _writer.Text(x, _y, text, size, bold);

    public void TextRight(double x, string text, double size = 9, bool bold = false) =>
        _writer.TextRight(x, _y, text, size, bold);

    public void Rule(double grey = 0.8, double width = 0.5) =>
        _writer.Line(Left, _y, Right, _y, width, grey);

    public void Band(double height, double grey = 0.94) =>
        _writer.Rect(Left, _y - height + 3, Width, height, grey);

    /// <summary>
    /// Wraps a long string to the given width and writes it, moving the cursor
    /// down a line for each. Used for the note under a payslip line.
    /// </summary>
    public void Paragraph(double x, string text, double maxWidth, double size = 7.5, double leading = 9)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();

        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : $"{line} {word}";

            if (PdfWriter.Measure(candidate, size, false) > maxWidth && line.Length > 0)
            {
                Text(x, line.ToString(), size);
                Down(leading);
                line.Clear().Append(word);
            }
            else
            {
                line.Clear().Append(candidate);
            }
        }

        if (line.Length > 0)
        {
            Text(x, line.ToString(), size);
            Down(leading);
        }
    }
}
