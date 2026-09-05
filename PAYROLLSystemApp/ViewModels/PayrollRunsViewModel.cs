using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>Which panel of the payroll section is showing.</summary>
public enum PayrollRunPanel
{
    Runs,
    Loans
}

/// <summary>A payroll run as the list renders it.</summary>
public sealed partial class PayrollRunRow : ObservableObject
{
    public PayrollRunRow(PayrollRun run) => Run = run;

    public PayrollRun Run { get; }

    public int Id => Run.Id;

    public string Reference => Run.ReferenceNumber;

    public string TypeDisplay => Run.TypeDisplay;

    public string PeriodDisplay => $"{Run.PeriodCode} · {Run.PeriodDisplay}";

    public string PayDateDisplay => Run.PayDateDisplay;

    public string StatusDisplay => Run.StatusDisplay;

    public string NetDisplay => Run.NetDisplay;

    public string HeadcountDisplay => Run.HeadcountDisplay;

    public bool HasExceptions => Run.HasExceptions;

    public string ExceptionDisplay => Run.ExceptionCount == 1
        ? "1 payslip needs review"
        : $"{Run.ExceptionCount} payslips need review";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowColor))]
    public partial bool IsSelected { get; set; }

    public Color RowColor => IsSelected
        ? Color.FromArgb("#EEF0FF")
        : Colors.Transparent;

    public Color StatusColor => Run.Status switch
    {
        PayrollRunStatus.Draft => Color.FromArgb("#6B7085"),
        PayrollRunStatus.ForApproval => Color.FromArgb("#B45309"),
        PayrollRunStatus.Approved => Color.FromArgb("#4338CA"),
        PayrollRunStatus.Posted => Color.FromArgb("#15803D"),
        _ => Color.FromArgb("#9AA0B2")
    };
}

/// <summary>A payslip as the run's employee list renders it.</summary>
public sealed class PayslipRow
{
    public PayslipRow(Payslip payslip) => Payslip = payslip;

    public Payslip Payslip { get; }

    public int Id => Payslip.Id;

    public string EmployeeName => string.IsNullOrWhiteSpace(Payslip.EmployeeName)
        ? "Not yet calculated"
        : Payslip.EmployeeName;

    public string EmployeeNumber => Payslip.EmployeeNumber;

    public string GrossDisplay => Payslip.GrossDisplay;

    public string DeductionsDisplay => Payslip.DeductionsDisplay;

    public string NetDisplay => Payslip.NetDisplay;

    public string TimeDisplay => Payslip.TimeDisplay;

    public bool IsFlagged => Payslip.IsFlaggedForReview;

    public string ReviewNote => Payslip.ReviewNote;

    public Color NetColor => Payslip.NetPay < 0m
        ? Color.FromArgb("#B91C1C")
        : Color.FromArgb("#1E2130");
}

/// <summary>An employee offered for a new run, with a tick box.</summary>
public sealed partial class RunCandidateRow : ObservableObject
{
    public RunCandidateRow(RunCandidate candidate)
    {
        Candidate = candidate;
        IsSelected = candidate.IsAvailable;
    }

    public RunCandidate Candidate { get; }

    public int Id => Candidate.Id;

    public string FullName => Candidate.FullName;

    public string EmployeeNumber => Candidate.EmployeeNumber;

    public bool IsAvailable => Candidate.IsAvailable;

    public string Detail => Candidate.IsAvailable
        ? Candidate.EmployeeNumber
        : $"{Candidate.EmployeeNumber} · {Candidate.Reason}";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A one-off adjustment as the run's adjustment list renders it.</summary>
public sealed class AdjustmentRow
{
    public AdjustmentRow(PayrollAdjustment adjustment, string employeeName)
    {
        Adjustment = adjustment;
        EmployeeName = employeeName;
    }

    public PayrollAdjustment Adjustment { get; }

    public string EmployeeName { get; }

    public int Id => Adjustment.Id;

    public string Name => Adjustment.Name;

    public string AmountDisplay => Adjustment.AmountDisplay;

    public string Detail => $"{EmployeeName} · {Adjustment.TaxDisplay} · {Adjustment.Remark}";

    public Color AmountColor => Adjustment.IsEarning
        ? Color.FromArgb("#15803D")
        : Color.FromArgb("#B91C1C");
}

/// <summary>A loan as the ledger list renders it.</summary>
public sealed class LoanRow
{
    public LoanRow(EmployeeLoan loan, string employeeName)
    {
        Loan = loan;
        EmployeeName = employeeName;
    }

    public EmployeeLoan Loan { get; }

    public string EmployeeName { get; }

    public int Id => Loan.Id;

    public string Name => Loan.DeductionName;

    public string BalanceDisplay => Loan.BalanceDisplay;

    public bool IsActive => Loan.Status == LoanStatus.Active;

    public string ActionText => Loan.Status == LoanStatus.Active ? "Suspend" : "Resume";

    public bool IsClosed => Loan.Status is LoanStatus.Completed or LoanStatus.Cancelled;

    /// <summary>A completed or cancelled loan has nothing left to suspend or resume.</summary>
    public bool IsToggleable => !IsClosed;

    public string Detail
    {
        get
        {
            var parts = new List<string> { EmployeeName };

            if (!string.IsNullOrWhiteSpace(Loan.Reference))
                parts.Add($"ref {Loan.Reference}");

            parts.Add($"{PayrollRounding.Format(Loan.AmortisationAmount)} per period");
            parts.Add($"of {PayrollRounding.Format(Loan.PrincipalAmount)}");

            if (Loan.Status != LoanStatus.Active)
                parts.Add(Loan.StatusDisplay.ToLowerInvariant());

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Section 2.6 — payroll processing (FR-050 – FR-063).
///
/// <para>Master and detail on one screen: the runs of a year on the left, the
/// selected run's payslips on the right. A payroll officer works a run for days
/// — create, calculate, look at the exceptions, adjust, recalculate — and
/// pushing the payslips behind a navigation step would make that loop slower
/// than the arithmetic it is checking.</para>
///
/// <para><b>What this screen cannot do is approve.</b> Submitting is here;
/// approving is the Approvals section, gated on a different permission, and the
/// service refuses an approval by the same person who submitted it.</para>
/// </summary>
public sealed partial class PayrollRunsViewModel : BaseViewModel
{
    private readonly IPayrollRunService _runs;
    private readonly IPayrollConfigService _config;
    private readonly IEmployeeService _employees;
    private readonly IDialogService _dialogs;

    private IReadOnlyList<PayPeriod> _periods = [];
    private IReadOnlyList<Employee> _roster = [];
    private PayrollAdjustment? _adjustmentTarget;
    private EmployeeLoan? _loanTarget;
    private int _confirmLoanId;

    private enum ConfirmTarget { Discard, Submit, LoanStatus }

    private ConfirmTarget _confirmTarget;

    public PayrollRunsViewModel(
        IPayrollRunService runs,
        IPayrollConfigService config,
        IEmployeeService employees,
        IDialogService dialogs,
        ISessionService session)
        : base(session)
    {
        _runs = runs;
        _config = config;
        _employees = employees;
        _dialogs = dialogs;

        Title = "Payroll runs";

        RunTypeOptions = EnumOption.From<PayrollRunType>(PayrollEnumNames.Display);
        AdjustmentKindOptions =
        [
            new EnumOption((int)PayslipLineKind.Earning, "Earning — adds to the pay"),
            new EnumOption((int)PayslipLineKind.Deduction, "Deduction — comes off the pay")
        ];

        RunSummary = string.Empty;
        DetailTitle = string.Empty;
        DetailSubtitle = string.Empty;
        DetailTotals = string.Empty;
        BlockerText = string.Empty;
        LoanSummary = string.Empty;
        ModalError = string.Empty;
        CreateSummary = string.Empty;
        ConfirmTitle = string.Empty;
        ConfirmMessage = string.Empty;
        ConfirmAction = "Confirm";
        ConfirmReason = string.Empty;
        SelectedYear = DateTime.Today.Year;

        ResetAdjustmentForm();
        ResetLoanForm();
    }

    // ======================================================== collections

    public ObservableCollection<PayrollRunRow> Runs { get; } = new();

    public ObservableCollection<PayslipRow> Payslips { get; } = new();

    public ObservableCollection<PayslipLine> Lines { get; } = new();

    public ObservableCollection<AdjustmentRow> Adjustments { get; } = new();

    public ObservableCollection<LoanRow> Loans { get; } = new();

    public ObservableCollection<RunCandidateRow> Candidates { get; } = new();

    public ObservableCollection<LookupOption> YearOptions { get; } = new();

    public ObservableCollection<LookupOption> PeriodOptions { get; } = new();

    public ObservableCollection<LookupOption> EmployeeOptions { get; } = new();

    public ObservableCollection<LookupOption> DeductionOptions { get; } = new();

    public IReadOnlyList<EnumOption> RunTypeOptions { get; }

    public IReadOnlyList<EnumOption> AdjustmentKindOptions { get; }

    // ============================================================ panels

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRuns))]
    [NotifyPropertyChangedFor(nameof(IsLoans))]
    public partial PayrollRunPanel Panel { get; set; }

    public bool IsRuns => Panel == PayrollRunPanel.Runs;

    public bool IsLoans => Panel == PayrollRunPanel.Loans;

    [RelayCommand]
    private void ShowRuns()
    {
        Session.Touch();
        ClearMessages();
        Panel = PayrollRunPanel.Runs;
    }

    [RelayCommand]
    private void ShowLoans()
    {
        Session.Touch();
        ClearMessages();
        Panel = PayrollRunPanel.Loans;
    }

    // ======================================================= gate / state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    public bool IsAnyModalOpen =>
        IsCreateOpen || IsAdjustmentFormOpen || IsLoanFormOpen || IsConfirmOpen || IsPayslipOpen;

    [ObservableProperty]
    public partial string RunSummary { get; set; }

    [ObservableProperty]
    public partial string LoanSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRuns))]
    public partial bool HasRuns { get; set; }

    public bool HasNoRuns => !HasRuns;

    // =========================================================== loading

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.RunPayroll, "open payroll runs"))
        {
            IsDenied = true;
            Runs.Clear();
            Payslips.Clear();
            return;
        }

        IsDenied = false;
        await RunAsync(ReloadAsync);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        ClearMessages();

        _roster = await _employees.GetAllAsync();
        _periods = await _config.GetPayPeriodsAsync(SelectedYear);

        var years = await _config.GetCalendarYearsAsync();

        YearOptions.Clear();
        foreach (var year in years)
            YearOptions.Add(new LookupOption(year, year.ToString(CultureInfo.InvariantCulture)));

        SelectedYearOption = YearOptions.FirstOrDefault(y => y.Id == SelectedYear);

        EmployeeOptions.Clear();
        foreach (var employee in _roster.OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase))
            EmployeeOptions.Add(new LookupOption(employee.Id, employee.FullName));

        var deductions = await _config.GetDeductionTypesAsync();

        DeductionOptions.Clear();
        foreach (var deduction in deductions.Where(d => d.IsAmortised))
            DeductionOptions.Add(new LookupOption(deduction.Id, deduction.Display));

        await ReloadRunsAsync();
        await ReloadLoansAsync();
    }

    private async Task ReloadRunsAsync()
    {
        var rows = await _runs.GetRunsAsync(new PayrollRunQuery(SelectedYear));

        var previous = SelectedRun?.Id;

        Runs.Clear();
        foreach (var run in rows)
            Runs.Add(new PayrollRunRow(run));

        HasRuns = Runs.Count > 0;

        var posted = rows.Count(r => r.Status == PayrollRunStatus.Posted);
        var drafts = rows.Count(r => r.Status == PayrollRunStatus.Draft);

        RunSummary = rows.Count == 0
            ? $"No payroll runs for {SelectedYear} yet."
            : $"{rows.Count} run(s) · {drafts} draft · {posted} posted";

        var restore = previous is { } id
            ? Runs.FirstOrDefault(r => r.Id == id)
            : Runs.FirstOrDefault();

        await SelectRunAsync(restore);
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
        _ = RunAsync(ReloadRunsAsync);
    }

    // ======================================================= run detail

    [ObservableProperty]
    public partial PayrollRunRow? SelectedRun { get; set; }

    [ObservableProperty]
    public partial string DetailTitle { get; set; }

    [ObservableProperty]
    public partial string DetailSubtitle { get; set; }

    [ObservableProperty]
    public partial string DetailTotals { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockers))]
    public partial string BlockerText { get; set; }

    public bool HasBlockers => !string.IsNullOrWhiteSpace(BlockerText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRunSelected))]
    [NotifyPropertyChangedFor(nameof(CanEditRun))]
    public partial bool HasRunSelected { get; set; }

    public bool HasNoRunSelected => !HasRunSelected;

    /// <summary>FR-058. Everything on the detail pane that writes is gated on this.</summary>
    public bool CanEditRun => SelectedRun?.Run.IsEditable == true;

    [ObservableProperty]
    public partial bool CanSubmitRun { get; set; }

    [ObservableProperty]
    public partial bool HasAdjustments { get; set; }

    [RelayCommand]
    private Task SelectRunRowAsync(PayrollRunRow? row) => RunAsync(() => SelectRunAsync(row));

    private async Task SelectRunAsync(PayrollRunRow? row)
    {
        foreach (var item in Runs)
            item.IsSelected = item.Id == row?.Id;

        SelectedRun = row;
        HasRunSelected = row is not null;

        Payslips.Clear();
        Adjustments.Clear();
        Lines.Clear();
        BlockerText = string.Empty;

        OnPropertyChanged(nameof(CanEditRun));

        if (row is null)
        {
            DetailTitle = string.Empty;
            DetailSubtitle = string.Empty;
            DetailTotals = string.Empty;
            CanSubmitRun = false;
            HasAdjustments = false;
            return;
        }

        var run = row.Run;

        DetailTitle = $"{run.ReferenceNumber} · {run.TypeDisplay}";

        DetailSubtitle =
            $"{run.PeriodName} · attendance {run.CutOffStart:dd MMM} – {run.CutOffEnd:dd MMM yyyy} · " +
            $"paid {run.PayDateDisplay} · {run.StatusDisplay}";

        DetailTotals = run.HasBeenCalculated
            ? $"Gross {run.GrossDisplay} · deductions {run.DeductionsDisplay} · net {run.NetDisplay} · " +
              $"employer share {run.EmployerShareDisplay}"
            : "Not yet calculated.";

        var payslips = await _runs.GetPayslipsAsync(run.Id);

        foreach (var payslip in payslips)
            Payslips.Add(new PayslipRow(payslip));

        var adjustments = await _runs.GetAdjustmentsAsync(run.Id);

        foreach (var adjustment in adjustments)
            Adjustments.Add(new AdjustmentRow(adjustment, NameOf(adjustment.EmployeeId)));

        HasAdjustments = Adjustments.Count > 0;

        CanSubmitRun = run.Status == PayrollRunStatus.Draft &&
                       run.HasBeenCalculated &&
                       run.ExceptionCount == 0;

        if (run.ExceptionCount > 0)
        {
            var flagged = payslips.Where(p => p.IsFlaggedForReview).Take(3).ToList();

            BlockerText =
                $"{run.ExceptionCount} payslip(s) need review before this run can be submitted. " +
                string.Join(" ", flagged.Select(p => $"{p.EmployeeName}: {p.ReviewNote}"));
        }
    }

    private string NameOf(int employeeId) =>
        _roster.FirstOrDefault(e => e.Id == employeeId)?.FullName ?? $"Employee {employeeId}";

    // ================================================= FR-050 create a run

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsCreateOpen { get; set; }

    [ObservableProperty]
    public partial LookupOption? CreatePeriod { get; set; }

    partial void OnCreatePeriodChanged(LookupOption? value) => _ = RunAsync(ReloadCandidatesAsync);

    [ObservableProperty]
    public partial EnumOption? CreateRunType { get; set; }

    partial void OnCreateRunTypeChanged(EnumOption? value) => _ = RunAsync(ReloadCandidatesAsync);

    [ObservableProperty]
    public partial string CreateRemarks { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CreateSummary { get; set; }

    [RelayCommand]
    private Task OpenCreateAsync() => RunAsync(async () =>
    {
        Session.Touch();
        ClearMessages();
        ModalError = string.Empty;

        _periods = await _config.GetPayPeriodsAsync(SelectedYear);

        PeriodOptions.Clear();
        foreach (var period in _periods.Where(p => p.Status != PayPeriodStatus.Closed))
            PeriodOptions.Add(new LookupOption(period.Id, $"{period.Code} · {period.RangeDisplay}"));

        if (PeriodOptions.Count == 0)
        {
            ShowError(
                $"Every {SelectedYear} pay period is closed, or none has been generated. " +
                "Open one on Payroll Setup → Pay calendar first.");
            return;
        }

        CreateRunType = RunTypeOptions.FirstOrDefault(o => o.Value == (int)PayrollRunType.Regular);
        CreateRemarks = string.Empty;

        // The period whose cut-off has most recently closed is the one about to
        // be run, so it is the sensible default.
        CreatePeriod = PeriodOptions.FirstOrDefault();

        await ReloadCandidatesAsync();

        IsCreateOpen = true;
    });

    private async Task ReloadCandidatesAsync()
    {
        Candidates.Clear();
        CreateSummary = string.Empty;

        if (CreatePeriod?.Id is not { } periodId)
            return;

        var runType = CreateRunType?.As<PayrollRunType>() ?? PayrollRunType.Regular;
        var candidates = await _runs.GetCandidatesAsync(periodId, runType);

        foreach (var candidate in candidates)
            Candidates.Add(new RunCandidateRow(candidate));

        var available = Candidates.Count(c => c.IsAvailable);

        CreateSummary = available == 0
            ? "Nobody is available for this period and run type."
            : $"{available} of {Candidates.Count} employee(s) available.";
    }

    [RelayCommand]
    private void SelectAllCandidates()
    {
        foreach (var candidate in Candidates.Where(c => c.IsAvailable))
            candidate.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNoCandidates()
    {
        foreach (var candidate in Candidates)
            candidate.IsSelected = false;
    }

    [RelayCommand]
    private void CloseCreate()
    {
        IsCreateOpen = false;
        ModalError = string.Empty;
    }

    [RelayCommand]
    private Task SubmitCreateAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        if (CreatePeriod?.Id is not { } periodId)
        {
            ModalError = "Choose the pay period.";
            return;
        }

        var chosen = Candidates.Where(c => c.IsAvailable && c.IsSelected).Select(c => c.Id).ToList();

        if (chosen.Count == 0)
        {
            ModalError = "Select at least one employee.";
            return;
        }

        var result = await _runs.CreateRunAsync(
            periodId,
            CreateRunType?.As<PayrollRunType>() ?? PayrollRunType.Regular,
            chosen, CreateRemarks, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsCreateOpen = false;

        await ReloadRunsAsync();

        var created = Runs.FirstOrDefault(r => r.Id == result.Value?.Id);
        if (created is not null)
            await SelectRunAsync(created);

        ShowStatus(result.Message);
    });

    // ============================================ FR-059 calculate / discard

    [RelayCommand]
    private Task CalculateAsync() => RunAsync(async () =>
    {
        var performedBy = Session.CurrentUser;
        if (performedBy is null || SelectedRun is null)
            return;

        var result = await _runs.CalculateAsync(SelectedRun.Id, performedBy);

        await ReloadRunsAsync();

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        if (result.HasBlockers)
        {
            BlockerText = string.Join("  ", result.Blockers);
            ShowError(result.Message);
            return;
        }

        ShowStatus(result.Message);
    });

    [RelayCommand]
    private void OpenSubmit()
    {
        if (SelectedRun is null)
            return;

        Session.Touch();
        ClearMessages();

        var run = SelectedRun.Run;

        _confirmTarget = ConfirmTarget.Submit;
        ConfirmTitle = $"Submit {run.ReferenceNumber} for approval";
        ConfirmMessage =
            $"{run.HeadcountDisplay}, gross {run.GrossDisplay}, net {run.NetDisplay}.\n\n" +
            "It stays returnable to draft until an approver signs it off — after that the figures are frozen.";
        ConfirmAction = "Submit";
        IsConfirmDestructive = false;
        NeedsReason = false;
        ConfirmReason = string.Empty;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void OpenDiscard()
    {
        if (SelectedRun is null)
            return;

        Session.Touch();
        ClearMessages();

        var run = SelectedRun.Run;

        _confirmTarget = ConfirmTarget.Discard;
        ConfirmTitle = $"Discard {run.ReferenceNumber}";
        ConfirmMessage =
            "The payslips and adjustments on this run are deleted. Prior periods are untouched, and the " +
            "reference number will not be reused.\n\nSay why — it goes on the audit trail.";
        ConfirmAction = "Discard";
        IsConfirmDestructive = true;
        NeedsReason = true;
        ConfirmReason = string.Empty;
        IsConfirmOpen = true;
    }

    // ============================================== FR-070 payslip detail

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsPayslipOpen { get; set; }

    [ObservableProperty]
    public partial string PayslipTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PayslipSubtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PayslipTotals { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPayslipNote))]
    public partial string PayslipNote { get; set; } = string.Empty;

    public bool HasPayslipNote => !string.IsNullOrWhiteSpace(PayslipNote);

    [RelayCommand]
    private Task OpenPayslipAsync(PayslipRow? row) => RunAsync(async () =>
    {
        if (row is null)
            return;

        Session.Touch();
        ClearMessages();

        var payslip = row.Payslip;

        PayslipTitle = $"{payslip.EmployeeName} · {payslip.PeriodCode}";

        PayslipSubtitle =
            $"{payslip.RateDisplay} · daily {PayrollRounding.Format(payslip.DailyRate)} · " +
            $"hourly {PayrollRounding.Format(payslip.HourlyRate)} " +
            $"(factor {payslip.WorkingDaysFactor:0.##}) · {payslip.TimeDisplay}";

        PayslipTotals =
            $"Gross {payslip.GrossDisplay} · deductions {payslip.DeductionsDisplay} · " +
            $"net {payslip.NetDisplay}";

        PayslipNote = payslip.IsFlaggedForReview ? payslip.ReviewNote : string.Empty;

        Lines.Clear();
        foreach (var line in await _runs.GetLinesAsync(payslip.Id))
            Lines.Add(line);

        IsPayslipOpen = true;
    });

    [RelayCommand]
    private void ClosePayslip() => IsPayslipOpen = false;

    // ============================================== FR-057 adjustments

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsAdjustmentFormOpen { get; set; }

    [ObservableProperty]
    public partial string AdjustmentFormTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LookupOption? AdjustmentEmployee { get; set; }

    [ObservableProperty]
    public partial EnumOption? AdjustmentKind { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAdjustmentCommand))]
    public partial string AdjustmentName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAdjustmentCommand))]
    public partial string AdjustmentAmount { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitAdjustmentCommand))]
    public partial string AdjustmentRemark { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AdjustmentIsTaxable { get; set; } = true;

    [ObservableProperty]
    public partial bool AdjustmentReducesTax { get; set; }

    private void ResetAdjustmentForm()
    {
        AdjustmentEmployee = null;
        AdjustmentKind = AdjustmentKindOptions.FirstOrDefault();
        AdjustmentName = string.Empty;
        AdjustmentAmount = string.Empty;
        AdjustmentRemark = string.Empty;
        AdjustmentIsTaxable = true;
        AdjustmentReducesTax = false;
    }

    [RelayCommand]
    private void OpenCreateAdjustment()
    {
        if (SelectedRun is null)
            return;

        Session.Touch();
        ClearMessages();
        ResetAdjustmentForm();

        _adjustmentTarget = null;
        AdjustmentFormTitle = $"Adjustment on {SelectedRun.Reference}";

        // Only the employees actually on the run, so an adjustment cannot be
        // filed against somebody the service would then refuse.
        AdjustmentEmployee = EmployeeOptions
            .FirstOrDefault(o => Payslips.Any(p => p.Payslip.EmployeeId == o.Id));

        IsAdjustmentFormOpen = true;
    }

    [RelayCommand]
    private void OpenEditAdjustment(AdjustmentRow? row)
    {
        if (row is null || SelectedRun is null)
            return;

        Session.Touch();
        ClearMessages();

        var adjustment = row.Adjustment;
        _adjustmentTarget = adjustment;

        AdjustmentFormTitle = $"Edit adjustment on {SelectedRun.Reference}";
        AdjustmentEmployee = EmployeeOptions.FirstOrDefault(o => o.Id == adjustment.EmployeeId);
        AdjustmentKind = AdjustmentKindOptions.FirstOrDefault(o => o.Value == (int)adjustment.Kind);
        AdjustmentName = adjustment.Name;
        AdjustmentAmount = adjustment.Amount.ToString("0.##");
        AdjustmentRemark = adjustment.Remark;
        AdjustmentIsTaxable = adjustment.IsTaxable;
        AdjustmentReducesTax = adjustment.ReducesTaxableIncome;

        IsAdjustmentFormOpen = true;
    }

    [RelayCommand]
    private void CloseAdjustmentForm()
    {
        IsAdjustmentFormOpen = false;
        ModalError = string.Empty;
        _adjustmentTarget = null;
    }

    private bool CanSubmitAdjustment() =>
        !string.IsNullOrWhiteSpace(AdjustmentName) &&
        !string.IsNullOrWhiteSpace(AdjustmentAmount) &&
        !string.IsNullOrWhiteSpace(AdjustmentRemark);

    [RelayCommand(CanExecute = nameof(CanSubmitAdjustment))]
    private Task SubmitAdjustmentAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null || SelectedRun is null)
            return;

        if (AdjustmentEmployee?.Id is not { } employeeId)
        {
            ModalError = "Choose the employee the adjustment applies to.";
            return;
        }

        if (!decimal.TryParse(AdjustmentAmount, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) &&
            !decimal.TryParse(AdjustmentAmount, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
        {
            ModalError = "Enter the amount as a number.";
            return;
        }

        var kind = AdjustmentKind?.As<PayslipLineKind>() ?? PayslipLineKind.Earning;

        var adjustment = new PayrollAdjustment
        {
            Id = _adjustmentTarget?.Id ?? 0,
            PayrollRunId = SelectedRun.Id,
            EmployeeId = employeeId,
            Kind = kind,
            Code = kind == PayslipLineKind.Earning ? "ADJ_EARN" : "ADJ_DED",
            Name = AdjustmentName,
            Amount = amount,
            IsTaxable = AdjustmentIsTaxable,
            ReducesTaxableIncome = AdjustmentReducesTax,
            Remark = AdjustmentRemark,
            CreatedBy = _adjustmentTarget?.CreatedBy ?? performedBy.Username
        };

        var result = await _runs.SaveAdjustmentAsync(adjustment, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsAdjustmentFormOpen = false;
        _adjustmentTarget = null;

        await ReloadRunsAsync();
        ShowStatus(result.Message);
    });

    [RelayCommand]
    private Task RemoveAdjustmentAsync(AdjustmentRow? row) => RunAsync(async () =>
    {
        if (row is null)
            return;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        if (!await _dialogs.ConfirmAsync("Remove adjustment",
                $"Remove {row.Name} ({row.AmountDisplay}) from this run?", "Remove", "Keep"))
        {
            return;
        }

        var result = await _runs.DeleteAdjustmentAsync(row.Id, performedBy);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        await ReloadRunsAsync();
        ShowStatus(result.Message);
    });

    // ================================================== FR-055 loans

    private async Task ReloadLoansAsync()
    {
        var loans = await _runs.GetLoansAsync(includeClosed: ShowClosedLoans);

        Loans.Clear();
        foreach (var loan in loans)
            Loans.Add(new LoanRow(loan, NameOf(loan.EmployeeId)));

        var outstanding = loans.Where(l => l.Status == LoanStatus.Active).Sum(l => l.OutstandingBalance);

        LoanSummary = loans.Count == 0
            ? "No loans or cash advances are on file."
            : $"{loans.Count} loan(s) · {PayrollRounding.Format(outstanding)} outstanding";
    }

    [ObservableProperty]
    public partial bool ShowClosedLoans { get; set; }

    partial void OnShowClosedLoansChanged(bool value) => _ = RunAsync(ReloadLoansAsync);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsLoanFormOpen { get; set; }

    [ObservableProperty]
    public partial string LoanFormTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LookupOption? LoanEmployee { get; set; }

    [ObservableProperty]
    public partial LookupOption? LoanDeduction { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitLoanCommand))]
    public partial string LoanPrincipal { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitLoanCommand))]
    public partial string LoanAmortisation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LoanReference { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LoanRemarks { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTime LoanStartDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial string LoanBalanceNote { get; set; } = string.Empty;

    private void ResetLoanForm()
    {
        LoanEmployee = null;
        LoanDeduction = null;
        LoanPrincipal = string.Empty;
        LoanAmortisation = string.Empty;
        LoanReference = string.Empty;
        LoanRemarks = string.Empty;
        LoanStartDate = DateTime.Today;
        LoanBalanceNote = string.Empty;
    }

    [RelayCommand]
    private void OpenCreateLoan()
    {
        Session.Touch();
        ClearMessages();
        ResetLoanForm();

        _loanTarget = null;
        LoanFormTitle = "New loan or cash advance";
        LoanDeduction = DeductionOptions.FirstOrDefault();
        IsLoanFormOpen = true;
    }

    [RelayCommand]
    private void OpenEditLoan(LoanRow? row)
    {
        if (row is null)
            return;

        Session.Touch();
        ClearMessages();
        ResetLoanForm();

        var loan = row.Loan;
        _loanTarget = loan;

        LoanFormTitle = $"Edit {loan.DeductionName}";
        LoanEmployee = EmployeeOptions.FirstOrDefault(o => o.Id == loan.EmployeeId);
        LoanDeduction = DeductionOptions.FirstOrDefault(o =>
            o.Label.StartsWith(loan.DeductionCode + " ", StringComparison.OrdinalIgnoreCase));
        LoanPrincipal = loan.PrincipalAmount.ToString("0.##");
        LoanAmortisation = loan.AmortisationAmount.ToString("0.##");
        LoanReference = loan.Reference;
        LoanRemarks = loan.Remarks;
        LoanStartDate = loan.StartDate;

        LoanBalanceNote =
            $"{PayrollRounding.Format(loan.OutstandingBalance)} outstanding. The balance moves only when a " +
            "run is posted, so editing the loan here leaves it exactly as it stands.";

        IsLoanFormOpen = true;
    }

    [RelayCommand]
    private void CloseLoanForm()
    {
        IsLoanFormOpen = false;
        ModalError = string.Empty;
        _loanTarget = null;
    }

    private bool CanSubmitLoan() =>
        !string.IsNullOrWhiteSpace(LoanPrincipal) && !string.IsNullOrWhiteSpace(LoanAmortisation);

    [RelayCommand(CanExecute = nameof(CanSubmitLoan))]
    private Task SubmitLoanAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        if (LoanEmployee?.Id is not { } employeeId)
        {
            ModalError = "Choose the employee.";
            return;
        }

        if (LoanDeduction is null)
        {
            ModalError = "Choose which deduction the instalment is taken under. " +
                         "Only deductions flagged as amortised are offered.";
            return;
        }

        if (!TryAmount(LoanPrincipal, out var principal) || !TryAmount(LoanAmortisation, out var amortisation))
        {
            ModalError = "Enter the principal and the instalment as numbers.";
            return;
        }

        var code = LoanDeduction.Label.Split(' ')[0];

        var loan = new EmployeeLoan
        {
            Id = _loanTarget?.Id ?? 0,
            EmployeeId = employeeId,
            DeductionCode = code,
            Reference = LoanReference,
            PrincipalAmount = principal,
            AmortisationAmount = amortisation,
            StartDate = LoanStartDate,
            Remarks = LoanRemarks
        };

        var result = await _runs.SaveLoanAsync(loan, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsLoanFormOpen = false;
        _loanTarget = null;

        await ReloadLoansAsync();
        ShowStatus(result.Message);
    });

    [RelayCommand]
    private void OpenToggleLoan(LoanRow? row)
    {
        if (row is null)
            return;

        Session.Touch();
        ClearMessages();

        _confirmTarget = ConfirmTarget.LoanStatus;
        _confirmLoanId = row.Id;

        ConfirmTitle = row.IsActive ? $"Suspend {row.Name}" : $"Resume {row.Name}";

        ConfirmMessage = row.IsActive
            ? $"No further instalments will be taken from {row.EmployeeName} until the loan is resumed. " +
              $"The {row.BalanceDisplay} outstanding is unchanged."
            : $"Instalments resume on the next run. {row.BalanceDisplay} is outstanding.";

        ConfirmAction = row.IsActive ? "Suspend" : "Resume";
        IsConfirmDestructive = row.IsActive;
        NeedsReason = false;
        ConfirmReason = string.Empty;
        IsConfirmOpen = true;
    }

    // ==================================================== confirm dialog

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsConfirmOpen { get; set; }

    [ObservableProperty]
    public partial string ConfirmTitle { get; set; }

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; }

    [ObservableProperty]
    public partial string ConfirmAction { get; set; }

    [ObservableProperty]
    public partial bool IsConfirmDestructive { get; set; }

    [ObservableProperty]
    public partial bool NeedsReason { get; set; }

    [ObservableProperty]
    public partial string ConfirmReason { get; set; }

    [ObservableProperty]
    public partial string ModalError { get; set; }

    [RelayCommand]
    private void CloseConfirm()
    {
        IsConfirmOpen = false;
        ModalError = string.Empty;
    }

    [RelayCommand]
    private Task SubmitConfirmAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        switch (_confirmTarget)
        {
            case ConfirmTarget.Submit:
                {
                    if (SelectedRun is null)
                        return;

                    var result = await _runs.SubmitForApprovalAsync(SelectedRun.Id, performedBy);

                    if (!result.Succeeded)
                    {
                        ModalError = result.Message;
                        return;
                    }

                    IsConfirmOpen = false;
                    await ReloadRunsAsync();
                    ShowStatus(result.Message);
                    break;
                }

            case ConfirmTarget.Discard:
                {
                    if (SelectedRun is null)
                        return;

                    var result = await _runs.DiscardAsync(SelectedRun.Id, ConfirmReason, performedBy);

                    if (!result.Succeeded)
                    {
                        ModalError = result.Message;
                        return;
                    }

                    IsConfirmOpen = false;
                    await ReloadRunsAsync();
                    ShowStatus(result.Message);
                    break;
                }

            case ConfirmTarget.LoanStatus:
                {
                    var loan = Loans.FirstOrDefault(l => l.Id == _confirmLoanId);
                    if (loan is null)
                        return;

                    var status = loan.IsActive ? LoanStatus.Suspended : LoanStatus.Active;
                    var result = await _runs.SetLoanStatusAsync(_confirmLoanId, status, performedBy);

                    if (!result.Succeeded)
                    {
                        ModalError = result.Message;
                        return;
                    }

                    IsConfirmOpen = false;
                    await ReloadLoansAsync();
                    ShowStatus(result.Message);
                    break;
                }
        }
    });

    private static bool TryAmount(string? text, out decimal value)
    {
        value = 0m;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
               decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }
}
