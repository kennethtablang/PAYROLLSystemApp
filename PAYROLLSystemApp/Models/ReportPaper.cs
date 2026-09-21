namespace PAYROLLSystemApp.Models;

/// <summary>
/// FR-085. The stock a report is printed on.
///
/// <para>The two continuous-form sizes exist because the payroll summary is
/// printed on a dot matrix impact printer, which is what produces the carbon
/// copies a payroll sheet is signed on. Continuous form is fed one way through
/// a tractor and cannot be turned, so those two sizes carry their orientation
/// with them rather than being rotated to suit the table.</para>
/// </summary>
public enum ReportPaper
{
    /// <summary>
    /// 11 × 14 inch continuous form on a wide-carriage dot matrix, fed so the
    /// 14 inch edge runs across the page. Fourteen inches of width is what lets
    /// the payroll summary's two dozen columns land on one sheet.
    /// </summary>
    DotMatrix11x14 = 0,

    /// <summary>Legal, 8.5 × 14 inches, fed so the 14 inch edge runs across.</summary>
    EpsonLegal85x14 = 1,

    /// <summary>
    /// A4. The only size that turns itself: it is cut sheet in a laser or inkjet,
    /// so a wide table is printed landscape and a narrow one portrait.
    /// </summary>
    A4 = 2
}

/// <summary>
/// The page box each <see cref="ReportPaper"/> resolves to, in PDF points.
///
/// <para>One inch is 72 points by definition, so the figures below are exact
/// rather than converted.</para>
/// </summary>
public static class ReportPaperSizes
{
    private const double Inch = 72d;

    /// <summary>A4 in points: 210 × 297 mm at 72 dpi.</summary>
    public const double A4Short = 595.28;

    public const double A4Long = 841.89;

    /// <summary>
    /// The page box, already oriented.
    /// </summary>
    /// <param name="wideTable">
    /// Whether the table needs more width than the portrait edge gives. Read
    /// only by <see cref="ReportPaper.A4"/>; the continuous-form sizes are fixed
    /// by how the paper is fed and ignore it.
    /// </param>
    public static (double Width, double Height) Box(ReportPaper paper, bool wideTable) => paper switch
    {
        ReportPaper.DotMatrix11x14 => (14 * Inch, 11 * Inch),
        ReportPaper.EpsonLegal85x14 => (14 * Inch, 8.5 * Inch),
        _ => wideTable ? (A4Long, A4Short) : (A4Short, A4Long)
    };

    /// <summary>
    /// The width available to a table before anything has to be split onto a
    /// further page. For A4 this is the <em>portrait</em> width, because A4 can
    /// still be turned; for the fixed sizes it is simply the page.
    /// </summary>
    public static double PortraitWidth(ReportPaper paper) => paper switch
    {
        ReportPaper.DotMatrix11x14 => 14 * Inch,
        ReportPaper.EpsonLegal85x14 => 14 * Inch,
        _ => A4Short
    };

    /// <summary>
    /// Whether a table too wide for the page should be set in smaller type
    /// rather than carried onto a further page.
    ///
    /// <para>True for continuous form only. A payroll sheet is read across —
    /// the name and the net pay have to be on the same line — and an impact
    /// printer sets condensed type as a matter of course. Cut sheet keeps the
    /// plain rule: a second A4 page is cheap and stays legible.</para>
    /// </summary>
    public static bool Condenses(ReportPaper paper) =>
        paper is ReportPaper.DotMatrix11x14 or ReportPaper.EpsonLegal85x14;

    public static string Display(ReportPaper paper) => paper switch
    {
        ReportPaper.DotMatrix11x14 => "Dot matrix, 11 × 14 in",
        ReportPaper.EpsonLegal85x14 => "Epson legal, 8.5 × 14 in",
        _ => "A4"
    };

    public static string Detail(ReportPaper paper) => paper switch
    {
        ReportPaper.DotMatrix11x14 =>
            "Wide-carriage continuous form, 14 inches across. Fits the payroll summary on one sheet.",
        ReportPaper.EpsonLegal85x14 =>
            "Legal continuous form, 14 inches across and 8.5 deep. Fewer rows to a page than the 11 × 14.",
        _ =>
            "Cut sheet. Turned to landscape on its own when a table is too wide for portrait."
    };

    public static IReadOnlyList<ReportPaper> All { get; } =
        [ReportPaper.DotMatrix11x14, ReportPaper.EpsonLegal85x14, ReportPaper.A4];
}
