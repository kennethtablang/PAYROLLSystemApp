using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>One report in the list on the left.</summary>
public sealed partial class ReportChoice : ObservableObject
{
    public ReportChoice(ReportDefinition definition)
    {
        Definition = definition;
    }

    public ReportDefinition Definition { get; }

    public ReportKind Kind => Definition.Kind;

    public string Title => Definition.Title;

    public string Description => Definition.Description;

    public string RequirementRef => Definition.RequirementRef;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowColor))]
    public partial bool IsSelected { get; set; }

    public Color RowColor => IsSelected ? Color.FromArgb("#EEF0FF") : Colors.Transparent;
}

/// <summary>One rendered cell. Width and alignment come from the column.</summary>
public sealed class ReportCellView
{
    public ReportCellView(ReportCell cell, ReportColumn column, bool bold)
    {
        Text = cell.Text;
        Width = column.Width;
        Alignment = column.IsNumeric ? TextAlignment.End : TextAlignment.Start;
        Weight = bold ? FontAttributes.Bold : FontAttributes.None;
    }

    public ReportCellView(ReportColumn column)
    {
        Text = column.Header;
        Width = column.Width;
        Alignment = column.IsNumeric ? TextAlignment.End : TextAlignment.Start;
        Weight = FontAttributes.None;
    }

    public string Text { get; }

    public double Width { get; }

    public TextAlignment Alignment { get; }

    /// <summary>
    /// Carried on the cell rather than the row: the cells are rendered by a
    /// nested template whose binding context is the cell, with no way back up to
    /// the row it belongs to.
    /// </summary>
    public FontAttributes Weight { get; }
}

/// <summary>
/// One rendered row.
///
/// <para>The invariant worth stating: a row carries exactly one cell per column.
/// A row one cell short does not throw — it shifts every figure one column left
/// and produces a report that is wrong everywhere and looks right — so the cells
/// are built from the column list rather than from the row's own length.</para>
/// </summary>
public sealed class ReportRowView
{
    public ReportRowView(ReportRow row, IReadOnlyList<ReportColumn> columns)
    {
        var cells = new List<ReportCellView>(columns.Count);

        for (var i = 0; i < columns.Count; i++)
        {
            var cell = i < row.Cells.Count ? row.Cells[i] : ReportCell.Blank;
            cells.Add(new ReportCellView(cell, columns[i], row.IsTotal));
        }

        Cells = cells;
        IsTotal = row.IsTotal;
        IsFlagged = row.IsFlagged;
    }

    public IReadOnlyList<ReportCellView> Cells { get; }

    public bool IsTotal { get; }

    public bool IsFlagged { get; }

    public Color Background => IsTotal
        ? Color.FromArgb("#EEF0FF")
        : IsFlagged
            ? Color.FromArgb("#FDF3E4")
            : Colors.Transparent;
}

/// <summary>One of the report's statements about itself, coloured by severity.</summary>
public sealed class ReportNoteView
{
    public ReportNoteView(ReportNote note)
    {
        Severity = note.Severity;

        Text = note.Severity switch
        {
            ReportNoteSeverity.Blocking => $"DO NOT FILE OR PAY FROM THIS REPORT — {note.Text}",
            ReportNoteSeverity.Warning => $"Check — {note.Text}",
            _ => note.Text
        };
    }

    public ReportNoteSeverity Severity { get; }

    public string Text { get; }

    public bool IsBlocking => Severity == ReportNoteSeverity.Blocking;

    public Color TextColor => Severity switch
    {
        ReportNoteSeverity.Blocking => Color.FromArgb("#B91C1C"),
        ReportNoteSeverity.Warning => Color.FromArgb("#B45309"),
        _ => Color.FromArgb("#6B7085")
    };

    public Color Background => Severity switch
    {
        ReportNoteSeverity.Blocking => Color.FromArgb("#FDECEC"),
        ReportNoteSeverity.Warning => Color.FromArgb("#FDF3E4"),
        _ => Colors.Transparent
    };
}

/// <summary>
/// Section 2.8 — reporting (FR-080 – FR-085).
///
/// <para><b>One screen, eight reports, two file formats.</b> Everything renders
/// through <see cref="ReportGrid"/>, so the table, the CSV and the PDF are the
/// same report three times rather than three renderings that can drift apart.
/// Adding a ninth report is a builder and a definition; nothing on this screen
/// changes.</para>
///
/// <para><b>The scope controls follow the report.</b> A register wants a run, a
/// remittance form wants a month, the attendance report wants a range and the
/// alphalist wants a year — so only the control the selected report actually
/// uses is shown, rather than four disabled ones.</para>
/// </summary>
public sealed partial class ReportsViewModel : BaseViewModel
{
    private readonly IReportService _reports;
    private readonly IUserPreferences _preferences;

    private ReportGrid? _grid;
    private bool _suspendRebuild;

    public ReportsViewModel(IReportService reports, IUserPreferences preferences, ISessionService session)
        : base(session)
    {
        _reports = reports;
        _preferences = preferences;

        Title = "Reports";
        ScopeSummary = string.Empty;
        GridTitle = string.Empty;
        GridSubtitle = string.Empty;
        RowSummary = string.Empty;
        ExportedPath = string.Empty;
        EmptyMessage = string.Empty;

        From = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        To = From.AddMonths(1).AddDays(-1);
    }

    public ObservableCollection<ReportChoice> Reports { get; } = new();

    public ObservableCollection<LookupOption> RunOptions { get; } = new();

    public ObservableCollection<LookupOption> YearOptions { get; } = new();

    public ObservableCollection<LookupOption> MonthOptions { get; } = new();

    public ObservableCollection<LookupOption> DepartmentOptions { get; } = new();

    public ObservableCollection<ReportCellView> HeaderCells { get; } = new();

    public ObservableCollection<ReportRowView> Rows { get; } = new();

    public ObservableCollection<ReportNoteView> Notes { get; } = new();

    // ======================================================= gate / state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    [ObservableProperty]
    public partial ReportChoice? SelectedReport { get; set; }

    [ObservableProperty]
    public partial string ScopeSummary { get; set; }

    // Which scope control is on show. Bound directly rather than through a
    // converter, because four booleans read more plainly in the XAML than four
    // comparisons against an enum.
    [ObservableProperty]
    public partial bool NeedsRun { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowYear))]
    public partial bool NeedsMonth { get; set; }

    [ObservableProperty]
    public partial bool NeedsDateRange { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowYear))]
    public partial bool NeedsYear { get; set; }

    /// <summary>A remittance form needs a month <em>and</em> a year; the alphalist needs only the year.</summary>
    public bool ShowYear => NeedsMonth || NeedsYear;

    [ObservableProperty]
    public partial bool SupportsDepartment { get; set; }

    // =========================================================== loading

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.ViewReports, "open payroll reports"))
        {
            IsDenied = true;
            Reports.Clear();
            return;
        }

        IsDenied = false;

        await RunAsync(async () =>
        {
            if (Reports.Count == 0)
            {
                foreach (var definition in _reports.Definitions)
                    Reports.Add(new ReportChoice(definition));
            }

            await LoadScopeOptionsAsync();

            if (SelectedReport is null)
                await SelectAsync(RememberedReport() ?? Reports.FirstOrDefault());
            else
                await BuildAsync();
        });
    }

    private async Task LoadScopeOptionsAsync()
    {
        _suspendRebuild = true;

        try
        {
            var runs = await _reports.GetRunsAsync();

            RunOptions.Clear();
            foreach (var run in runs)
            {
                RunOptions.Add(new LookupOption(run.Id,
                    $"{run.ReferenceNumber} · {run.PeriodDisplay} · {run.StatusDisplay}"));
            }

            SelectedRun = RunOptions.FirstOrDefault(o => o.Id == SelectedRun?.Id) ?? RunOptions.FirstOrDefault();

            var years = await _reports.GetYearsAsync();

            YearOptions.Clear();
            foreach (var year in years)
                YearOptions.Add(new LookupOption(year, year.ToString(CultureInfo.InvariantCulture)));

            SelectedYear = YearOptions.FirstOrDefault(o => o.Id == SelectedYear?.Id) ?? YearOptions.FirstOrDefault();

            if (MonthOptions.Count == 0)
            {
                for (var month = 1; month <= 12; month++)
                {
                    MonthOptions.Add(new LookupOption(month,
                        CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month)));
                }

                SelectedMonth = MonthOptions[DateTime.Today.Month - 1];
            }

            DepartmentOptions.Clear();
            DepartmentOptions.Add(new LookupOption(null, "All departments"));

            foreach (var department in await _reports.GetDepartmentsAsync())
                DepartmentOptions.Add(new LookupOption(department.Id, department.Name));

            var wanted = SelectedDepartment?.Id ?? RememberedDepartmentId();

            SelectedDepartment = DepartmentOptions.FirstOrDefault(o => o.Id == wanted)
                                 ?? DepartmentOptions[0];
        }
        finally
        {
            _suspendRebuild = false;
        }
    }

    // ============================================================== scope

    [ObservableProperty]
    public partial LookupOption? SelectedRun { get; set; }

    [ObservableProperty]
    public partial LookupOption? SelectedYear { get; set; }

    [ObservableProperty]
    public partial LookupOption? SelectedMonth { get; set; }

    [ObservableProperty]
    public partial LookupOption? SelectedDepartment { get; set; }

    [ObservableProperty]
    public partial DateTime From { get; set; }

    [ObservableProperty]
    public partial DateTime To { get; set; }

    partial void OnSelectedRunChanged(LookupOption? value) => Rebuild();

    partial void OnSelectedYearChanged(LookupOption? value) => Rebuild();

    partial void OnSelectedMonthChanged(LookupOption? value) => Rebuild();

    partial void OnSelectedDepartmentChanged(LookupOption? value)
    {
        // A report without a department filter resets it under _suspendRebuild;
        // that is not the user's choice, so it is not remembered.
        if (!_suspendRebuild && value is not null)
            Remember(DepartmentMemory, value.Id?.ToString(CultureInfo.InvariantCulture));

        Rebuild();
    }

    partial void OnFromChanged(DateTime value) => Rebuild();

    partial void OnToChanged(DateTime value) => Rebuild();

    /// <summary>
    /// Rebuilds when a scope control moves. Suspended while the options are
    /// being loaded, so filling four pickers does not build the report four
    /// times.
    /// </summary>
    private void Rebuild()
    {
        if (_suspendRebuild || SelectedReport is null || IsDenied)
            return;

        _ = RunAsync(BuildAsync);
    }

    // ========================================================= selection

    [RelayCommand]
    private Task SelectReportAsync(ReportChoice? choice) => RunAsync(() => SelectAsync(choice));

    private async Task SelectAsync(ReportChoice? choice)
    {
        foreach (var report in Reports)
            report.IsSelected = ReferenceEquals(report, choice);

        SelectedReport = choice;

        if (choice is null)
            return;

        Remember(ReportMemory, choice.Kind.ToString());

        var definition = choice.Definition;

        NeedsRun = definition.Scope == ReportScope.Run;
        NeedsMonth = definition.Scope == ReportScope.Month;
        NeedsDateRange = definition.Scope == ReportScope.DateRange;
        NeedsYear = definition.Scope == ReportScope.Year;
        SupportsDepartment = definition.SupportsDepartmentFilter;

        if (!SupportsDepartment && DepartmentOptions.Count > 0)
        {
            _suspendRebuild = true;
            SelectedDepartment = DepartmentOptions[0];
            _suspendRebuild = false;
        }

        await BuildAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async () =>
    {
        await LoadScopeOptionsAsync();
        await BuildAsync();
    });

    // ====================================== Settings → remember last report

    private const string ReportMemory = "reports.report";
    private const string DepartmentMemory = "reports.department";

    private void Remember(string name, string? value)
    {
        if (_preferences.RememberReportChoice && Session.CurrentUser is { } user)
            _preferences.SetRemembered(user, name, value);
    }

    private string? Recall(string name) =>
        _preferences.RememberReportChoice && Session.CurrentUser is { } user
            ? _preferences.GetRemembered(user, name)
            : null;

    private ReportChoice? RememberedReport() =>
        Recall(ReportMemory) is { } kind
            ? Reports.FirstOrDefault(r => r.Kind.ToString() == kind)
            : null;

    private int? RememberedDepartmentId() =>
        int.TryParse(Recall(DepartmentMemory), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;

    // ========================================================== building

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResult))]
    public partial bool HasResult { get; set; }

    public bool HasNoResult => !HasResult;

    [ObservableProperty]
    public partial string GridTitle { get; set; }

    [ObservableProperty]
    public partial string GridSubtitle { get; set; }

    [ObservableProperty]
    public partial string RowSummary { get; set; }

    [ObservableProperty]
    public partial string EmptyMessage { get; set; }

    /// <summary>Total width of the table, so the header and rows scroll together.</summary>
    [ObservableProperty]
    public partial double TableWidth { get; set; }

    private async Task BuildAsync()
    {
        ClearMessages();
        ExportedPath = string.Empty;

        var user = Session.CurrentUser;

        if (user is null || SelectedReport is null)
            return;

        var request = new ReportRequest(
            SelectedReport.Kind,
            RunId: NeedsRun ? SelectedRun?.Id : null,
            Year: NeedsYear || NeedsMonth ? SelectedYear?.Id : null,
            Month: NeedsMonth ? SelectedMonth?.Id : null,
            From: NeedsDateRange ? From : null,
            To: NeedsDateRange ? To : null,
            DepartmentId: SupportsDepartment ? SelectedDepartment?.Id : null);

        ScopeSummary = DescribeScope();

        var result = await _reports.BuildAsync(request, user);

        if (!result.Succeeded || result.Grid is null)
        {
            Render(null);
            ShowError(result.Message);
            return;
        }

        Render(result.Grid);
    }

    private string DescribeScope()
    {
        if (NeedsRun)
            return SelectedRun?.Label ?? "no run chosen";

        if (NeedsMonth)
            return $"{SelectedMonth?.Label} {SelectedYear?.Label}";

        if (NeedsDateRange)
        {
            var scope = $"{From:dd MMM yyyy} – {To:dd MMM yyyy}";

            return SelectedDepartment?.Id is null ? scope : $"{scope} · {SelectedDepartment.Label}";
        }

        return SelectedYear?.Label ?? string.Empty;
    }

    private void Render(ReportGrid? grid)
    {
        _grid = grid;

        HeaderCells.Clear();
        Rows.Clear();
        Notes.Clear();

        if (grid is null)
        {
            HasResult = false;
            GridTitle = string.Empty;
            GridSubtitle = string.Empty;
            RowSummary = string.Empty;
            EmptyMessage = "The report could not be built.";
            return;
        }

        GridTitle = grid.Title;
        GridSubtitle = grid.Subtitle;

        foreach (var column in grid.Columns)
            HeaderCells.Add(new ReportCellView(column));

        foreach (var row in grid.Rows)
            Rows.Add(new ReportRowView(row, grid.Columns));

        foreach (var note in grid.Notes.OrderByDescending(n => n.Severity))
            Notes.Add(new ReportNoteView(note));

        TableWidth = grid.Columns.Sum(c => c.Width);

        HasResult = !grid.IsEmpty;
        EmptyMessage = grid.Notes.FirstOrDefault()?.Text ?? "There is nothing to report for this selection.";

        RowSummary = grid.IsEmpty
            ? EmptyMessage
            : $"{grid.DataRowCount} row(s)" + (grid.IsBlocked ? " · this report must not be filed" : string.Empty);
    }

    // ====================================================== FR-085 export

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExport))]
    public partial string ExportedPath { get; set; }

    public bool HasExport => !string.IsNullOrWhiteSpace(ExportedPath);

    [RelayCommand]
    private Task ExportCsvAsync() => ExportAsync(ReportFormat.Csv);

    [RelayCommand]
    private Task ExportPdfAsync() => ExportAsync(ReportFormat.Pdf);

    private Task ExportAsync(ReportFormat format) => RunAsync(async () =>
    {
        var user = Session.CurrentUser;

        if (user is null || _grid is null)
            return;

        var result = await _reports.ExportAsync(_grid, format, user);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        ExportedPath = result.FilePath;
        ShowStatus(result.Message);

        if (_preferences.OpenAfterExport)
            await OpenSilentlyAsync(result.FilePath, "Report");
    });

    /// <summary>
    /// Hands the exported file to whatever the platform opens it with. The
    /// application does not try to display it: every platform already has a
    /// reader for both formats.
    /// </summary>
    [RelayCommand]
    private Task OpenExportAsync() => RunAsync(async () =>
    {
        if (!HasExport || !File.Exists(ExportedPath))
        {
            ShowError("The exported file is no longer where it was saved.");
            return;
        }

        try
        {
            await Launcher.Default.OpenAsync(
                new OpenFileRequest("Report", new ReadOnlyFile(ExportedPath)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReportsViewModel] {ex}");
            ShowError($"No application is available to open the file. It is saved at {ExportedPath}");
        }
    });

    /// <summary>
    /// Settings → "Open files after export". Failing to open is not an error:
    /// the status line already says where the file was saved.
    /// </summary>
    private static async Task OpenSilentlyAsync(string path, string title)
    {
        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest(title, new ReadOnlyFile(path)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OpenAfterExport] {ex}");
        }
    }
}
