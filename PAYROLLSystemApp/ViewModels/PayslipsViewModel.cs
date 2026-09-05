using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>A payslip as the history list renders it.</summary>
public sealed partial class PayslipHistoryRow : ObservableObject
{
    public PayslipHistoryRow(PayslipListItem item)
    {
        Item = item;
        Payslip = item.Payslip;
    }

    public PayslipListItem Item { get; }

    public Payslip Payslip { get; }

    public int Id => Payslip.Id;

    public string PeriodDisplay => $"{Payslip.PeriodCode} · {Payslip.PeriodStart:dd MMM} – {Payslip.PeriodEnd:dd MMM yyyy}";

    public string PayDateDisplay => Payslip.PayDate.ToString("dd MMM yyyy");

    public string EmployeeName => Payslip.EmployeeName;

    public string NetDisplay => Payslip.NetDisplay;

    public string GrossDisplay => Payslip.GrossDisplay;

    public string RunReference => Item.Run.ReferenceNumber;

    public string Detail => $"{Item.Run.TypeDisplay} · {Item.Run.ReferenceNumber} · paid {PayDateDisplay}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowColor))]
    public partial bool IsSelected { get; set; }

    public Color RowColor => IsSelected ? Color.FromArgb("#EEF0FF") : Colors.Transparent;
}

/// <summary>
/// Section 2.7 — payslips (FR-070 – FR-074).
///
/// <para><b>One screen serves two people.</b> A payroll officer with
/// <see cref="Permission.ViewAllPayslips"/> sees everybody and can filter by
/// employee; an ordinary employee sees their own history and nothing else. The
/// narrowing is done by <see cref="IPayslipService"/> rather than here, so a
/// mistake in this view model cannot widen it.</para>
///
/// <para>Only posted runs appear. A draft is a working figure and an approved
/// run is one waiting to be released; handing either to an employee as a PDF
/// would be handing them a number that can still change.</para>
/// </summary>
public sealed partial class PayslipsViewModel : BaseViewModel
{
    private readonly IPayslipService _payslips;
    private readonly IEmployeeService _employees;

    public PayslipsViewModel(
        IPayslipService payslips, IEmployeeService employees, ISessionService session)
        : base(session)
    {
        _payslips = payslips;
        _employees = employees;

        Title = "Payslips";

        Summary = string.Empty;
        DetailTitle = string.Empty;
        DetailSubtitle = string.Empty;
        DetailTotals = string.Empty;
        YearToDateDisplay = string.Empty;
        EmployerShareDisplay = string.Empty;
        ExportedPath = string.Empty;
        SelectedYear = DateTime.Today.Year;
    }

    public ObservableCollection<PayslipHistoryRow> Payslips { get; } = new();

    public ObservableCollection<PayslipLine> Lines { get; } = new();

    public ObservableCollection<LookupOption> YearOptions { get; } = new();

    public ObservableCollection<LookupOption> EmployeeOptions { get; } = new();

    // ======================================================= gate / state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    /// <summary>Whether the employee filter is shown at all (FR-072).</summary>
    [ObservableProperty]
    public partial bool CanSeeEveryone { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPayslips))]
    public partial bool HasPayslips { get; set; }

    public bool HasNoPayslips => !HasPayslips;

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = string.Empty;

    // =========================================================== loading

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.ViewOwnPayslip, "open payslips"))
        {
            IsDenied = true;
            Payslips.Clear();
            return;
        }

        IsDenied = false;
        CanSeeEveryone = Session.Has(Permission.ViewAllPayslips);

        await RunAsync(ReloadAsync);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        ClearMessages();
        ExportedPath = string.Empty;

        var user = Session.CurrentUser;
        if (user is null)
            return;

        if (CanSeeEveryone && EmployeeOptions.Count == 0)
        {
            EmployeeOptions.Add(new LookupOption(null, "All employees"));

            foreach (var employee in (await _employees.GetAllAsync())
                     .OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase))
            {
                EmployeeOptions.Add(new LookupOption(employee.Id, employee.FullName));
            }

            SelectedEmployee = EmployeeOptions[0];
        }

        var years = await _payslips.GetYearsAsync(user);

        YearOptions.Clear();
        foreach (var year in years)
            YearOptions.Add(new LookupOption(year, year.ToString(CultureInfo.InvariantCulture)));

        if (!years.Contains(SelectedYear))
            SelectedYear = years.FirstOrDefault(DateTime.Today.Year);

        SelectedYearOption = YearOptions.FirstOrDefault(y => y.Id == SelectedYear);

        await ReloadListAsync();
    }

    private async Task ReloadListAsync()
    {
        var user = Session.CurrentUser;
        if (user is null)
            return;

        var previous = SelectedPayslip?.Id;

        var items = await _payslips.GetPayslipsAsync(
            new PayslipQuery(EmployeeId: SelectedEmployee?.Id, Year: SelectedYear), user);

        Payslips.Clear();
        foreach (var item in items)
            Payslips.Add(new PayslipHistoryRow(item));

        HasPayslips = Payslips.Count > 0;

        EmptyMessage = CanSeeEveryone
            ? $"No payslips have been issued for {SelectedYear}. A payslip appears once a payroll run is posted."
            : $"You have no payslips for {SelectedYear}.";

        var net = items.Sum(i => i.Payslip.NetPay);

        Summary = items.Count == 0
            ? EmptyMessage
            : $"{items.Count} payslip(s) · net {PayrollRounding.Format(net)} for {SelectedYear}";

        var restore = previous is { } id
            ? Payslips.FirstOrDefault(p => p.Id == id)
            : Payslips.FirstOrDefault();

        await SelectAsync(restore);
    }

    [ObservableProperty]
    public partial int SelectedYear { get; set; }

    [ObservableProperty]
    public partial LookupOption? SelectedYearOption { get; set; }

    partial void OnSelectedYearOptionChanged(LookupOption? value)
    {
        if (value?.Id is not { } year || year == SelectedYear)
            return;

        SelectedYear = year;
        _ = RunAsync(ReloadListAsync);
    }

    [ObservableProperty]
    public partial LookupOption? SelectedEmployee { get; set; }

    partial void OnSelectedEmployeeChanged(LookupOption? value) => _ = RunAsync(ReloadListAsync);

    // ============================================================ detail

    [ObservableProperty]
    public partial PayslipHistoryRow? SelectedPayslip { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSelection))]
    public partial bool HasSelection { get; set; }

    public bool HasNoSelection => !HasSelection;

    [ObservableProperty]
    public partial string DetailTitle { get; set; }

    [ObservableProperty]
    public partial string DetailSubtitle { get; set; }

    [ObservableProperty]
    public partial string DetailTotals { get; set; }

    /// <summary>FR-074.</summary>
    [ObservableProperty]
    public partial string YearToDateDisplay { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEmployerShare))]
    public partial string EmployerShareDisplay { get; set; }

    public bool HasEmployerShare => !string.IsNullOrWhiteSpace(EmployerShareDisplay);

    [RelayCommand]
    private Task SelectRowAsync(PayslipHistoryRow? row) => RunAsync(() => SelectAsync(row));

    private async Task SelectAsync(PayslipHistoryRow? row)
    {
        foreach (var item in Payslips)
            item.IsSelected = item.Id == row?.Id;

        SelectedPayslip = row;
        HasSelection = row is not null;

        Lines.Clear();
        ExportedPath = string.Empty;

        if (row is null)
        {
            DetailTitle = string.Empty;
            DetailSubtitle = string.Empty;
            DetailTotals = string.Empty;
            YearToDateDisplay = string.Empty;
            EmployerShareDisplay = string.Empty;
            return;
        }

        var payslip = row.Payslip;

        DetailTitle = CanSeeEveryone
            ? $"{payslip.EmployeeName} · {payslip.PeriodCode}"
            : $"Pay period {payslip.PeriodCode}";

        DetailSubtitle =
            $"{payslip.PeriodStart:dd MMM yyyy} – {payslip.PeriodEnd:dd MMM yyyy} · " +
            $"paid {payslip.PayDate:dd MMM yyyy} · {payslip.RateDisplay} · {payslip.TimeDisplay}";

        DetailTotals =
            $"Gross {payslip.GrossDisplay} · deductions {payslip.DeductionsDisplay} · net {payslip.NetDisplay}";

        foreach (var line in await _payslips.GetLinesAsync(payslip.Id))
            Lines.Add(line);

        var ytd = await _payslips.GetYearToDateAsync(payslip);

        YearToDateDisplay =
            $"Gross {PayrollRounding.Format(ytd.GrossPay)}   " +
            $"Taxable {PayrollRounding.Format(ytd.TaxableIncome)}   " +
            $"Tax withheld {PayrollRounding.Format(ytd.TaxWithheld)}   " +
            $"SSS {PayrollRounding.Format(ytd.Sss)}   " +
            $"PhilHealth {PayrollRounding.Format(ytd.PhilHealth)}   " +
            $"Pag-IBIG {PayrollRounding.Format(ytd.PagIbig)}";

        // The employer share is not a deduction and never touches net pay, but an
        // employee is entitled to see that it was remitted alongside theirs.
        EmployerShareDisplay = payslip.TotalEmployerShare > 0m
            ? $"Employer also remitted {PayrollRounding.Format(payslip.TotalEmployerShare)} " +
              $"— SSS {PayrollRounding.Format(payslip.EmployerSss + payslip.EmployerSssWisp)}, " +
              $"EC {PayrollRounding.Format(payslip.EmployerEc)}, " +
              $"PhilHealth {PayrollRounding.Format(payslip.EmployerPhilHealth)}, " +
              $"Pag-IBIG {PayrollRounding.Format(payslip.EmployerPagIbig)}."
            : string.Empty;
    }

    // ======================================================= FR-071 export

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExport))]
    public partial string ExportedPath { get; set; }

    public bool HasExport => !string.IsNullOrWhiteSpace(ExportedPath);

    [RelayCommand]
    private Task ExportAsync() => RunAsync(async () =>
    {
        var user = Session.CurrentUser;
        if (user is null || SelectedPayslip is null)
            return;

        var result = await _payslips.ExportAsync(SelectedPayslip.Id, user);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        ExportedPath = result.FilePath;
        ShowStatus(result.Message);
    });

    /// <summary>
    /// FR-071. Every payslip on the run the selected one belongs to, in a single
    /// PDF. Only offered to a user who may see everyone's.
    /// </summary>
    [RelayCommand]
    private Task ExportRunAsync() => RunAsync(async () =>
    {
        var user = Session.CurrentUser;
        if (user is null || SelectedPayslip is null)
            return;

        var result = await _payslips.ExportRunAsync(SelectedPayslip.Item.Run.Id, user);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        ExportedPath = result.FilePath;
        ShowStatus(result.Message);
    });

    /// <summary>
    /// Hands the exported file to whatever the platform uses to read a PDF. The
    /// application does not try to display it: every platform already has a
    /// viewer, and none of them would thank us for a second one.
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
                new OpenFileRequest("Payslip", new ReadOnlyFile(ExportedPath)));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[PayslipsViewModel] {ex}");
            ShowError($"No application is available to open the file. It is saved at {ExportedPath}");
        }
    });
}
