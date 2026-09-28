using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>
/// FR-019. Bulk import from CSV, TSV or XLSX.
///
/// <para>Three steps in one dialog: choose a file, read the preview, commit.
/// Nothing is written until the second step is confirmed — the preview is the
/// only point at which a person can see that a column was read as the wrong
/// field, and after the commit there is no undo.</para>
/// </summary>
public sealed partial class EmployeeDirectoryViewModel
{
    private enum ImportStage { Start, Preview, Done }

    private ImportStage _importStage = ImportStage.Start;

    /// <summary>The picked file, copied somewhere this app may read it again for the commit.</summary>
    private string? _importPath;

    private ImportResult? _importResult;

    /// <summary>The headings the importer recognises, one per field, in the order of the employee form.</summary>
    public static readonly string[] TemplateHeadings =
    [
        "Employee No", "Last Name", "First Name", "Middle Name", "Suffix",
        "Birth Date", "Gender", "Civil Status", "Contact Number", "Email", "Address",
        "Date Hired", "Employment Status", "Department", "Position", "Detach Code",
        "Pay Type", "Basic Rate", "Allowance",
        "SSS", "PhilHealth", "Pag-IBIG", "TIN", "Bank", "Account Number", "Minimum Wage"
    ];

    public string ImportGuide =>
        "The first row must be headings. Only Employee No and a name are required: either Last Name and " +
        "First Name, or one Employee Name column written \"Surname, Given\". Every other column is optional, " +
        "and a column left out of the file is left alone on existing records.\n\n" +
        "A row whose Employee No already exists updates that employee, so a corrected file can be imported " +
        "again. Department, Position and Detach Code must match what is already set up, by name or code. " +
        "Dates may be day-first or month-first; the whole file is read one way.\n\n" +
        "Employment status: Probationary, Regular, Contractual, ProjectBased, PartTime, Seasonal, Consultant. " +
        "Pay type: Monthly, Daily, Hourly. Minimum Wage: Yes or No.";

    public ObservableCollection<ImportRow> ImportRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsImportOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImportStart))]
    [NotifyPropertyChangedFor(nameof(IsImportPreview))]
    [NotifyPropertyChangedFor(nameof(HasImportResult))]
    [NotifyPropertyChangedFor(nameof(ImportPrimaryText))]
    [NotifyPropertyChangedFor(nameof(ImportSubtitle))]
    public partial int ImportStageValue { get; set; }

    public bool IsImportStart => _importStage == ImportStage.Start;

    public bool IsImportPreview => _importStage == ImportStage.Preview;

    public bool HasImportResult => _importStage != ImportStage.Start;

    public string ImportSubtitle => _importStage switch
    {
        ImportStage.Start => "Step 1 of 3 — choose a CSV, TSV or Excel (.xlsx) file",
        ImportStage.Preview => $"Step 2 of 3 — check the preview of {ImportFileName}. Nothing has been written yet.",
        _ => $"Step 3 of 3 — {ImportFileName} imported"
    };

    public string ImportPrimaryText => _importStage switch
    {
        ImportStage.Start => "Choose file…",
        ImportStage.Preview => _importResult is { WriteCount: > 0 } r
            ? $"Import {r.WriteCount} row(s)"
            : "Nothing to import",
        _ => "Done"
    };

    [ObservableProperty]
    public partial bool CanRunImportPrimary { get; set; } = true;

    [ObservableProperty]
    public partial string ImportFileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportColumns))]
    public partial string ImportColumns { get; set; } = string.Empty;

    public bool HasImportColumns => ImportColumns.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportWarnings))]
    public partial string ImportWarnings { get; set; } = string.Empty;

    public bool HasImportWarnings => ImportWarnings.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImportNote))]
    public partial string ImportNote { get; set; } = string.Empty;

    public bool HasImportNote => ImportNote.Length > 0;

    private void SetImportStage(ImportStage stage)
    {
        _importStage = stage;
        ImportStageValue = (int)stage;
        CanRunImportPrimary = stage != ImportStage.Preview || _importResult is { WriteCount: > 0 };
    }

    [RelayCommand]
    private void OpenImport()
    {
        ClearMessages();
        ModalError = string.Empty;

        _importPath = null;
        _importResult = null;
        ImportRows.Clear();
        ImportFileName = ImportSummary = ImportColumns = ImportWarnings = ImportNote = string.Empty;

        SetImportStage(ImportStage.Start);
        IsImportOpen = true;
    }

    [RelayCommand]
    private void CloseImport()
    {
        var changed = _importStage == ImportStage.Done;

        IsImportOpen = false;
        ModalError = string.Empty;
        _importPath = null;

        if (changed)
            _ = RunAsync(ReloadAsync);
    }

    /// <summary>The dialog's one primary button walks the three steps.</summary>
    [RelayCommand]
    private Task ImportPrimaryAsync()
    {
        switch (_importStage)
        {
            case ImportStage.Start:
                return ChooseImportFileAsync();

            case ImportStage.Preview:
                return CommitImportAsync();

            default:
                CloseImport();
                return Task.CompletedTask;
        }
    }

    [RelayCommand]
    private Task ChooseImportFileAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ImportNote = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        FileResult? picked;

        try
        {
            picked = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Choose the employee file to import",
                FileTypes = ImportFileTypes
            });
        }
        catch (Exception ex)
        {
            ModalError = "The file could not be opened: " + ex.Message;
            return;
        }

        if (picked is null)
            return;   // cancelled

        if (!TabularFile.IsSupported(picked.FileName))
        {
            ModalError = $"{picked.FileName} is not a CSV, TSV or .xlsx file. " +
                         "In Excel, use File → Save As and choose CSV or Excel Workbook.";
            return;
        }

        // The picker's own path is not always readable a second time — on
        // Android it is a content URI — so the file is copied once and both the
        // preview and the commit read the copy.
        var folder = Path.Combine(FileSystem.CacheDirectory, "imports");
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{picked.FileName}");

        await using (var source = await picked.OpenReadAsync())
        await using (var target = File.Create(copy))
            await source.CopyToAsync(target);

        _importPath = copy;
        ImportFileName = picked.FileName;

        var result = await _import.ImportAsync(copy, performedBy, commit: false);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        ShowImportResult(result);
        SetImportStage(ImportStage.Preview);
    });

    private Task CommitImportAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null || _importPath is null || _importResult is not { WriteCount: > 0 })
            return;

        var result = await _import.ImportAsync(_importPath, performedBy, commit: true);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        ShowImportResult(result);
        SetImportStage(ImportStage.Done);
        ShowStatus(result.Summary);
    });

    private void ShowImportResult(ImportResult result)
    {
        _importResult = result;

        ImportSummary = result.Summary;
        ImportColumns = string.Join("\n", result.DetectedColumns);
        ImportWarnings = string.Join("\n", result.Warnings);

        // Rejected rows first: they are the ones that need something done.
        ImportRows.Clear();
        foreach (var row in result.Rows.OrderByDescending(r => r.IsRejected).ThenBy(r => r.LineNumber))
            ImportRows.Add(row);
    }

    /// <summary>FR-019's error report, saved beside the other exports and opened.</summary>
    [RelayCommand]
    private Task SaveImportReportAsync() => RunAsync(async () =>
    {
        if (_importResult is null)
            return;

        var path = Path.Combine(ImportFolder(),
            $"Import report {DateTime.Now:yyyy-MM-dd HHmmss} - {Path.GetFileNameWithoutExtension(ImportFileName)}.txt");

        await File.WriteAllTextAsync(path, _importResult.BuildReport(ImportFileName), new UTF8Encoding(true));

        ImportNote = $"Report saved to {path}";
        await OpenFileAsync(path, "Import report");
    });

    /// <summary>
    /// A blank file with every heading the importer recognises, so nobody has to
    /// guess the spelling. Written with a byte-order mark so Excel opens it as
    /// UTF-8 and an accented surname survives the round trip.
    /// </summary>
    [RelayCommand]
    private Task SaveImportTemplateAsync() => RunAsync(async () =>
    {
        var path = Path.Combine(ImportFolder(), "Employee import template.csv");

        await File.WriteAllTextAsync(path, string.Join(",", TemplateHeadings) + "\r\n", new UTF8Encoding(true));

        ImportNote = $"Template saved to {path}";
        await OpenFileAsync(path, "Employee import template");
    });

    private static async Task OpenFileAsync(string path, string title)
    {
        try
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest(title, new ReadOnlyFile(path)));
        }
        catch
        {
            // Nothing is registered to open it; the note already says where it is.
        }
    }

    /// <summary>Beside the payslip and report exports: Documents\Payroll MS\Imports.</summary>
    private static string ImportFolder()
    {
        var folder = Path.Combine(Path.GetDirectoryName(PayslipService.ExportFolder())!, "Imports");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static readonly FilePickerFileType ImportFileTypes = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.WinUI] = [".csv", ".tsv", ".txt", ".xlsx"],
        [DevicePlatform.Android] =
        [
            "text/csv", "text/comma-separated-values", "text/tab-separated-values", "text/plain",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        ],
        [DevicePlatform.iOS] = ["public.comma-separated-values-text", "public.plain-text", "org.openxmlformats.spreadsheetml.sheet"],
        [DevicePlatform.MacCatalyst] = ["public.comma-separated-values-text", "public.plain-text", "org.openxmlformats.spreadsheetml.sheet"]
    });
}
