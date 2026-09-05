using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace PAYROLLSystemApp.Services;

/// <summary>A file that could not be read, and why, in words a user can act on.</summary>
public sealed class TabularFileException : Exception
{
    public TabularFileException(string message) : base(message) { }
}

/// <summary>
/// One sheet read off disk: a header row and the rows under it, all as text.
///
/// <para>Everything stays a string until a column is claimed by an importer.
/// A spreadsheet has no types worth trusting — an employee number is a number
/// in one file and text in the next, and a date is whatever the machine that
/// wrote it believed — so conversion happens where the meaning is known.</para>
/// </summary>
public sealed class TabularSheet
{
    public TabularSheet(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, string source)
    {
        Headers = headers;
        Rows = rows;
        Source = source;
    }

    public IReadOnlyList<string> Headers { get; }

    public IReadOnlyList<string[]> Rows { get; }

    /// <summary>The file name, for the error report.</summary>
    public string Source { get; }

    public int ColumnCount => Headers.Count;

    /// <summary>The value at a column index, or empty where the row is short.</summary>
    public static string Cell(string[] row, int index) =>
        index >= 0 && index < row.Length ? row[index].Trim() : string.Empty;
}

/// <summary>
/// Reads CSV, TSV, plain text and XLSX into a <see cref="TabularSheet"/>.
///
/// <para><b>XLSX is unzipped and read directly rather than through a library.</b>
/// An .xlsx is a zip of XML; the part needed here is one worksheet and the
/// shared-string table. Every general-purpose spreadsheet library is a large
/// dependency that would sit next to employee data for the sake of two XML
/// documents, which C-04 asks us not to do lightly — the same reasoning that
/// produced the hand-rolled <see cref="PdfWriter"/>.</para>
///
/// <para><b>The delimiter is sniffed, not assumed.</b> A file exported in a
/// locale that uses the comma as a decimal separator is semicolon-delimited, and
/// reading it as CSV yields one column and a useless error report.</para>
/// </summary>
public static class TabularFile
{
    public static readonly string[] SupportedExtensions = [".csv", ".tsv", ".txt", ".xlsx"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static async Task<TabularSheet> ReadAsync(string path)
    {
        if (!File.Exists(path))
            throw new TabularFileException("That file is no longer where it was chosen.");

        var extension = Path.GetExtension(path).ToLowerInvariant();

        if (!SupportedExtensions.Contains(extension))
        {
            throw new TabularFileException(
                $"{extension} files cannot be read. Use CSV, TSV, TXT or XLSX.");
        }

        var name = Path.GetFileName(path);

        return extension == ".xlsx"
            ? ReadWorkbook(path, name)
            : ReadDelimited(await File.ReadAllTextAsync(path, DetectEncoding(path)).ConfigureAwait(false), name);
    }

    // =====================================================================
    // Delimited text
    // =====================================================================

    /// <summary>
    /// A file with a UTF-8 byte-order mark is UTF-8; anything else is read as
    /// UTF-8 too, but with the replacement behaviour that keeps a Latin-1 file
    /// readable rather than throwing on the first accented name.
    /// </summary>
    private static Encoding DetectEncoding(string path)
    {
        using var stream = File.OpenRead(path);

        var bom = new byte[3];
        var read = stream.Read(bom, 0, 3);

        var hasBom = read == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;

        return hasBom ? Encoding.UTF8 : new UTF8Encoding(false);
    }

    public static TabularSheet ReadDelimited(string text, string source)
    {
        var lines = SplitLines(text);

        if (lines.Count == 0)
            throw new TabularFileException($"{source} is empty.");

        var delimiter = SniffDelimiter(lines);

        var parsed = lines
            .Select(line => ParseLine(line, delimiter))
            .Where(fields => fields.Any(f => !string.IsNullOrWhiteSpace(f)))
            .ToList();

        if (parsed.Count == 0)
            throw new TabularFileException($"{source} has no rows in it.");

        var headers = parsed[0].Select(h => h.Trim()).ToList();

        if (headers.All(string.IsNullOrWhiteSpace))
            throw new TabularFileException($"{source} has no column headings on its first row.");

        return new TabularSheet(headers, parsed.Skip(1).ToList(), source);
    }

    /// <summary>
    /// Splits on line endings, but only outside a quoted field — a remark or an
    /// address may legitimately carry a newline inside quotes.
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '"')
            {
                quoted = !quoted;
                current.Append(ch);
                continue;
            }

            if (!quoted && (ch == '\n' || ch == '\r'))
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;

                lines.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        // A leading '#' is how this application's own exports carry their notes,
        // so a file round-tripped through one is readable back.
        return lines
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith('#'))
            .ToList();
    }

    private static char SniffDelimiter(IReadOnlyList<string> lines)
    {
        var candidates = new[] { ',', ';', '\t', '|' };
        var sample = lines.Take(10).ToList();

        // The winner is the character that splits every sampled line into the
        // same number of fields, and the most of them. Consistency first:
        // a comma that appears twice on one line and five times on the next is
        // punctuation inside a field, not a delimiter.
        return candidates
            .Select(c => new
            {
                Delimiter = c,
                Counts = sample.Select(l => ParseLine(l, c).Length).ToList()
            })
            .Where(x => x.Counts[0] > 1)
            .OrderByDescending(x => x.Counts.Distinct().Count() == 1)
            .ThenByDescending(x => x.Counts[0])
            .Select(x => x.Delimiter)
            .FirstOrDefault(',');
    }

    private static string[] ParseLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];

            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }

                continue;
            }

            if (ch == '"')
                quoted = true;
            else if (ch == delimiter)
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        fields.Add(current.ToString());

        return fields.ToArray();
    }

    // =====================================================================
    // XLSX
    // =====================================================================

    private static readonly XNamespace Spreadsheet =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static TabularSheet ReadWorkbook(string path, string source)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);

            var shared = ReadSharedStrings(archive);

            var sheet = archive.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?? throw new TabularFileException($"{source} contains no worksheet.");

            using var stream = sheet.Open();

            var document = XDocument.Load(stream);

            var rows = document.Descendants(Spreadsheet + "row")
                .Select(row => ReadRow(row, shared))
                .Where(cells => cells.Any(c => !string.IsNullOrWhiteSpace(c)))
                .ToList();

            if (rows.Count == 0)
                throw new TabularFileException($"The first sheet of {source} is empty.");

            var width = rows.Max(r => r.Length);

            var padded = rows
                .Select(r => r.Length == width ? r : r.Concat(new string[width - r.Length]).ToArray())
                .Select(r => r.Select(c => c ?? string.Empty).ToArray())
                .ToList();

            return new TabularSheet(padded[0].Select(h => h.Trim()).ToList(), padded.Skip(1).ToList(), source);
        }
        catch (TabularFileException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw new TabularFileException(
                $"{source} is not a readable .xlsx file. If it is an older .xls, save it as .xlsx or CSV first.");
        }
    }

    private static string[] ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");

        if (entry is null)
            return [];

        using var stream = entry.Open();

        return XDocument.Load(stream)
            .Descendants(Spreadsheet + "si")

            // A string with mixed formatting is split into several <t> runs,
            // so the value is the concatenation rather than the first one.
            .Select(si => string.Concat(si.Descendants(Spreadsheet + "t").Select(t => t.Value)))
            .ToArray();
    }

    /// <summary>
    /// One worksheet row, placed by cell reference rather than by order.
    ///
    /// <para>A row omits its empty cells entirely, so reading them in sequence
    /// shifts every value after a gap one column to the left — which produces an
    /// import that is wrong everywhere and looks right.</para>
    /// </summary>
    private static string[] ReadRow(XElement row, string[] shared)
    {
        var cells = new SortedDictionary<int, string>();

        foreach (var cell in row.Elements(Spreadsheet + "c"))
        {
            var reference = cell.Attribute("r")?.Value ?? string.Empty;
            var index = ColumnIndex(reference);

            if (index < 0)
                continue;

            cells[index] = ReadCell(cell, shared);
        }

        if (cells.Count == 0)
            return [];

        var width = cells.Keys.Max() + 1;
        var values = new string[width];

        for (var i = 0; i < width; i++)
            values[i] = cells.TryGetValue(i, out var value) ? value : string.Empty;

        return values;
    }

    private static string ReadCell(XElement cell, string[] shared)
    {
        var type = cell.Attribute("t")?.Value;

        if (type == "inlineStr")
            return string.Concat(cell.Descendants(Spreadsheet + "t").Select(t => t.Value));

        var raw = cell.Element(Spreadsheet + "v")?.Value ?? string.Empty;

        if (type == "s")
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                   index >= 0 && index < shared.Length
                ? shared[index]
                : string.Empty;
        }

        return raw;
    }

    /// <summary>Turns a cell reference such as <c>AB7</c> into a zero-based column index.</summary>
    private static int ColumnIndex(string reference)
    {
        var index = 0;
        var seen = false;

        foreach (var ch in reference)
        {
            if (!char.IsLetter(ch))
                break;

            index = (index * 26) + (char.ToUpperInvariant(ch) - 'A' + 1);
            seen = true;
        }

        return seen ? index - 1 : -1;
    }

    // =====================================================================
    // Values
    // =====================================================================

    /// <summary>
    /// Reads a date, accepting what spreadsheets and biometric exports actually
    /// write — including an Excel serial number.
    ///
    /// <para><paramref name="dayFirst"/> settles <c>03/04</c>. It is decided by
    /// the importer from the whole file rather than guessed per value; reading
    /// it the wrong way moves a day's pay into a different month.</para>
    /// </summary>
    public static bool TryReadDate(string value, bool dayFirst, out DateTime date)
    {
        date = default;
        value = value.Trim();

        if (value.Length == 0)
            return false;

        // An Excel serial. The epoch is 30 December 1899 because the format
        // reproduces a 1900 leap year that never happened.
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) &&
            serial is > 1 and < 300000 && !value.Contains('/') && !value.Contains('-'))
        {
            date = new DateTime(1899, 12, 30).AddDays(serial);
            return true;
        }

        string[] formats = dayFirst
            ? ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd", "yyyy/MM/dd",
               "dd/MM/yy", "d/M/yy", "dd MMM yyyy", "d MMM yyyy", "MMM d, yyyy", "yyyyMMdd"]
            : ["MM/dd/yyyy", "M/d/yyyy", "MM-dd-yyyy", "M-d-yyyy", "yyyy-MM-dd", "yyyy/MM/dd",
               "MM/dd/yy", "M/d/yy", "dd MMM yyyy", "d MMM yyyy", "MMM d, yyyy", "yyyyMMdd"];

        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out date))
            {
                return true;
            }
        }

        // A value carrying a time as well — a punch log usually does.
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        return false;
    }

    /// <summary>
    /// Whether a column of dates can only be read one way round.
    ///
    /// <para>Returns null when nothing in the file settles it: no value has a
    /// first or second part above 12. The importer refuses to commit in that
    /// case rather than picking — reading <c>03/04</c> as the wrong month moves
    /// a day's pay.</para>
    /// </summary>
    public static bool? SettleDayFirst(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            var parts = value.Trim().Split('/', '-');

            if (parts.Length < 3)
                continue;

            if (!int.TryParse(parts[0], out var first) || !int.TryParse(parts[1], out var second))
                continue;

            // A four-digit leading part is an ISO date, which is unambiguous and
            // says nothing about the rest of the file.
            if (parts[0].Length == 4)
                continue;

            if (first > 12 && second <= 12)
                return true;

            if (second > 12 && first <= 12)
                return false;
        }

        return null;
    }

    /// <summary>Reads a time of day, in the shapes a punch log writes it.</summary>
    public static bool TryReadTime(string value, out TimeSpan time)
    {
        time = default;
        value = value.Trim();

        if (value.Length == 0)
            return false;

        // A fraction of a day, which is how a spreadsheet stores a bare time.
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction) &&
            fraction is >= 0 and < 1 && value.Contains('.'))
        {
            time = TimeSpan.FromDays(fraction);
            return true;
        }

        string[] formats =
        [
            "HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss",
            "hh:mm tt", "h:mm tt", "hh:mm:ss tt", "h:mm:ss tt",
            "HHmm", "HHmmss"
        ];

        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(value, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
            {
                time = parsed.TimeOfDay;
                return true;
            }
        }

        // A full timestamp in a column that only wanted the time of day.
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
        {
            time = stamp.TimeOfDay;
            return true;
        }

        return false;
    }

    public static bool TryReadDecimal(string value, out decimal number) =>
        decimal.TryParse(value.Replace(",", string.Empty).Replace("₱", string.Empty).Trim(),
            NumberStyles.Number, CultureInfo.InvariantCulture, out number);

    /// <summary>Reads the words a spreadsheet uses for yes.</summary>
    public static bool ReadFlag(string value) =>
        value.Trim().ToLowerInvariant() is "1" or "y" or "yes" or "true" or "t" or "x";
}

/// <summary>
/// Which column of a file holds which field.
///
/// <para><b>Detected from the headings against a list of aliases</b>, because a
/// biometric export calls the same column <c>EmpNo</c>, <c>Employee ID</c> or
/// <c>BADGE</c> depending on who wrote it. Detection is reported back so it can
/// be seen and corrected before anything is written.</para>
/// </summary>
public sealed class ColumnMap
{
    private readonly Dictionary<string, int> _byField = new(StringComparer.OrdinalIgnoreCase);

    public ColumnMap(TabularSheet sheet) => Sheet = sheet;

    public TabularSheet Sheet { get; }

    /// <summary>Claims a column for a field, by the first alias that matches a heading.</summary>
    public int Detect(string field, params string[] aliases)
    {
        var index = Find(aliases);

        if (index >= 0)
            _byField[field] = index;

        return index;
    }

    /// <summary>Overrides detection with a column the user named.</summary>
    public void Assign(string field, int index)
    {
        if (index >= 0 && index < Sheet.ColumnCount)
            _byField[field] = index;
    }

    public bool Has(string field) => _byField.ContainsKey(field);

    public int IndexOf(string field) => _byField.TryGetValue(field, out var index) ? index : -1;

    public string Value(string[] row, string field) => TabularSheet.Cell(row, IndexOf(field));

    public string HeaderOf(string field)
    {
        var index = IndexOf(field);

        return index >= 0 && index < Sheet.Headers.Count ? Sheet.Headers[index] : string.Empty;
    }

    /// <summary>Every value in a field's column, for deciding a file-wide question.</summary>
    public IEnumerable<string> Column(string field)
    {
        var index = IndexOf(field);

        return index < 0 ? [] : Sheet.Rows.Select(r => TabularSheet.Cell(r, index));
    }

    private int Find(IReadOnlyList<string> aliases)
    {
        // Exact match first, so a file with both "Date" and "Date Hired" claims
        // the right one rather than whichever comes first alphabetically.
        for (var i = 0; i < Sheet.Headers.Count; i++)
        {
            var header = Normalise(Sheet.Headers[i]);

            if (aliases.Any(a => Normalise(a) == header))
                return i;
        }

        for (var i = 0; i < Sheet.Headers.Count; i++)
        {
            var header = Normalise(Sheet.Headers[i]);

            if (header.Length > 0 && aliases.Any(a => header.Contains(Normalise(a))))
                return i;
        }

        return -1;
    }

    private static string Normalise(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
