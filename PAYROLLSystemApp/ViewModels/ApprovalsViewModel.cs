using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>
/// FR-058. The approver's side of a payroll run: review, approve or return, and
/// post.
///
/// <para><b>A separate section, not a button on the runs screen.</b> Approval is
/// a different permission held by a different person, and the service refuses an
/// approval from whoever submitted the run. Putting both on one screen would
/// suggest one person does both, which is exactly the control this requirement
/// exists to impose.</para>
///
/// <para>Only two states appear here: runs waiting to be approved, and approved
/// runs waiting to be posted. A draft is the payroll officer's business, and a
/// posted run is finished.</para>
/// </summary>
public sealed partial class ApprovalsViewModel : BaseViewModel
{
    private readonly IPayrollRunService _runs;

    private enum ConfirmTarget { Approve, Return, Post }

    private ConfirmTarget _confirmTarget;

    public ApprovalsViewModel(IPayrollRunService runs, ISessionService session)
        : base(session)
    {
        _runs = runs;

        Title = "Approvals";

        QueueSummary = string.Empty;
        DetailTitle = string.Empty;
        DetailSubtitle = string.Empty;
        DetailTotals = string.Empty;
        DetailTrail = string.Empty;
        PayslipTitle = string.Empty;
        PayslipSubtitle = string.Empty;
        PayslipTotals = string.Empty;
        ConfirmTitle = string.Empty;
        ConfirmMessage = string.Empty;
        ConfirmAction = "Confirm";
        ConfirmReason = string.Empty;
        ModalError = string.Empty;
    }

    public ObservableCollection<PayrollRunRow> Queue { get; } = new();

    public ObservableCollection<PayslipRow> Payslips { get; } = new();

    public ObservableCollection<PayslipLine> Lines { get; } = new();

    // ======================================================= gate / state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    public bool IsAnyModalOpen => IsConfirmOpen || IsPayslipOpen;

    [ObservableProperty]
    public partial string QueueSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNothingToDo))]
    public partial bool HasQueue { get; set; }

    public bool HasNothingToDo => !HasQueue;

    // =========================================================== loading

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.ApprovePayroll, "open payroll approvals"))
        {
            IsDenied = true;
            Queue.Clear();
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

        var waiting = await _runs.GetRunsAsync(new PayrollRunQuery(Status: PayrollRunStatus.ForApproval));
        var approved = await _runs.GetRunsAsync(new PayrollRunQuery(Status: PayrollRunStatus.Approved));

        var previous = SelectedRun?.Id;

        Queue.Clear();

        foreach (var run in waiting.Concat(approved).OrderBy(r => r.PayDate).ThenBy(r => r.Id))
            Queue.Add(new PayrollRunRow(run));

        HasQueue = Queue.Count > 0;

        QueueSummary = Queue.Count == 0
            ? "Nothing is waiting on you."
            : $"{waiting.Count} awaiting approval · {approved.Count} approved and awaiting posting";

        var restore = previous is { } id
            ? Queue.FirstOrDefault(r => r.Id == id)
            : Queue.FirstOrDefault();

        await SelectRunAsync(restore);
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

    /// <summary>Who did what to this run, so an approver signs with the history in view.</summary>
    [ObservableProperty]
    public partial string DetailTrail { get; set; }

    [ObservableProperty]
    public partial bool HasRunSelected { get; set; }

    [ObservableProperty]
    public partial bool CanApprove { get; set; }

    [ObservableProperty]
    public partial bool CanPost { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOwnSubmissionWarning))]
    public partial string OwnSubmissionWarning { get; set; } = string.Empty;

    public bool HasOwnSubmissionWarning => !string.IsNullOrWhiteSpace(OwnSubmissionWarning);

    [RelayCommand]
    private Task SelectRunRowAsync(PayrollRunRow? row) => RunAsync(() => SelectRunAsync(row));

    private async Task SelectRunAsync(PayrollRunRow? row)
    {
        foreach (var item in Queue)
            item.IsSelected = item.Id == row?.Id;

        SelectedRun = row;
        HasRunSelected = row is not null;

        Payslips.Clear();
        Lines.Clear();
        OwnSubmissionWarning = string.Empty;

        if (row is null)
        {
            DetailTitle = string.Empty;
            DetailSubtitle = string.Empty;
            DetailTotals = string.Empty;
            DetailTrail = string.Empty;
            CanApprove = false;
            CanPost = false;
            return;
        }

        var run = row.Run;

        DetailTitle = $"{run.ReferenceNumber} · {run.TypeDisplay}";

        DetailSubtitle =
            $"{run.PeriodName} · attendance {run.CutOffStart:dd MMM} – {run.CutOffEnd:dd MMM yyyy} · " +
            $"paid {run.PayDateDisplay} · {run.HeadcountDisplay}";

        DetailTotals =
            $"Gross {run.GrossDisplay} · deductions {run.DeductionsDisplay} · net {run.NetDisplay} · " +
            $"employer share {run.EmployerShareDisplay}";

        var trail = new List<string> { $"Created by {run.CreatedBy}" };

        if (run.SubmittedUtc is { } submitted)
            trail.Add($"submitted by {run.SubmittedBy} on {submitted.ToLocalTime():dd MMM yyyy HH:mm}");

        if (run.ApprovedUtc is { } approved)
            trail.Add($"approved by {run.ApprovedBy} on {approved.ToLocalTime():dd MMM yyyy HH:mm}");

        DetailTrail = string.Join(" · ", trail);

        foreach (var payslip in await _runs.GetPayslipsAsync(run.Id))
            Payslips.Add(new PayslipRow(payslip));

        var isOwnSubmission = string.Equals(
            run.SubmittedBy, Session.CurrentUser?.Username, StringComparison.OrdinalIgnoreCase);

        CanApprove = run.Status == PayrollRunStatus.ForApproval && !isOwnSubmission;
        CanPost = run.Status == PayrollRunStatus.Approved;

        if (run.Status == PayrollRunStatus.ForApproval && isOwnSubmission)
        {
            OwnSubmissionWarning =
                "You submitted this run, so you cannot also approve it. Another approver has to review it.";
        }
    }

    // ======================================================== decisions

    [RelayCommand]
    private void OpenApprove()
    {
        if (SelectedRun is null)
            return;

        Session.Touch();
        ClearMessages();

        var run = SelectedRun.Run;

        _confirmTarget = ConfirmTarget.Approve;
        ConfirmTitle = $"Approve {run.ReferenceNumber}";
        ConfirmMessage =
            $"{run.HeadcountDisplay}, gross {run.GrossDisplay}, net {run.NetDisplay}.\n\n" +
            "Approving freezes the figures: the run can no longer be recalculated or adjusted. " +
            "It still has to be posted before anything reaches the employees' records.";
        ConfirmAction = "Approve";
        IsConfirmDestructive = false;
        NeedsReason = false;
        ConfirmReason = string.Empty;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void OpenReturn()
    {
        if (SelectedRun is null)
            return;

        Session.Touch();
        ClearMessages();

        _confirmTarget = ConfirmTarget.Return;
        ConfirmTitle = $"Return {SelectedRun.Reference} to draft";
        ConfirmMessage =
            "The payroll officer can then recalculate or adjust it. Nothing computed is lost.\n\n" +
            "Say what needs changing — it goes on the audit trail and is what they will act on.";
        ConfirmAction = "Return";
        IsConfirmDestructive = true;
        NeedsReason = true;
        ConfirmReason = string.Empty;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void OpenPost()
    {
        if (SelectedRun is null)
            return;

        Session.Touch();
        ClearMessages();

        var run = SelectedRun.Run;

        _confirmTarget = ConfirmTarget.Post;
        ConfirmTitle = $"Post {run.ReferenceNumber}";
        ConfirmMessage =
            $"Net pay of {run.NetDisplay} across {run.HeadcountDisplay}.\n\n" +
            "Posting locks every attendance day inside the cut-off, decrements the loan balances the run " +
            "collected against, and closes the pay period.\n\n" +
            "There is no way back. A posted run is corrected with an adjustment run, never by editing it.";
        ConfirmAction = "Post";
        IsConfirmDestructive = true;
        NeedsReason = false;
        ConfirmReason = string.Empty;
        IsConfirmOpen = true;
    }

    // ============================================== FR-070 payslip detail

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsPayslipOpen { get; set; }

    [ObservableProperty]
    public partial string PayslipTitle { get; set; }

    [ObservableProperty]
    public partial string PayslipSubtitle { get; set; }

    [ObservableProperty]
    public partial string PayslipTotals { get; set; }

    [RelayCommand]
    private Task OpenPayslipAsync(PayslipRow? row) => RunAsync(async () =>
    {
        if (row is null)
            return;

        Session.Touch();

        var payslip = row.Payslip;

        PayslipTitle = $"{payslip.EmployeeName} · {payslip.PeriodCode}";

        PayslipSubtitle =
            $"{payslip.RateDisplay} · daily {PayrollRounding.Format(payslip.DailyRate)} · " +
            $"hourly {PayrollRounding.Format(payslip.HourlyRate)} · {payslip.TimeDisplay}";

        PayslipTotals =
            $"Gross {payslip.GrossDisplay} · deductions {payslip.DeductionsDisplay} · net {payslip.NetDisplay}";

        Lines.Clear();
        foreach (var line in await _runs.GetLinesAsync(payslip.Id))
            Lines.Add(line);

        IsPayslipOpen = true;
    });

    [RelayCommand]
    private void ClosePayslip() => IsPayslipOpen = false;

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
        if (performedBy is null || SelectedRun is null)
            return;

        var result = _confirmTarget switch
        {
            ConfirmTarget.Approve => await _runs.ApproveAsync(SelectedRun.Id, performedBy),
            ConfirmTarget.Return => await _runs.ReturnToDraftAsync(SelectedRun.Id, ConfirmReason, performedBy),
            _ => await _runs.PostAsync(SelectedRun.Id, performedBy)
        };

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsConfirmOpen = false;

        await ReloadAsync();
        ShowStatus(result.Message);
    });
}
