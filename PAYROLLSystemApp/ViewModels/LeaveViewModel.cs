using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>Which of the three panels the section is showing.</summary>
public enum LeaveMode
{
    /// <summary>The filing queue — what needs deciding, and what was decided.</summary>
    Requests,

    /// <summary>One employee's credits across every type, for a year (FR-032).</summary>
    Balances,

    /// <summary>The leave types themselves and their annual credits (FR-030).</summary>
    Types
}

/// <summary>One filing as the queue renders it, with the employee it belongs to.</summary>
public sealed class LeaveRequestRow
{
    public LeaveRequestRow(LeaveRequest request, Employee? employee)
    {
        Request = request;
        Employee = employee;
    }

    public LeaveRequest Request { get; }

    public Employee? Employee { get; }

    public int Id => Request.Id;

    public string EmployeeName => Employee?.FullName ?? $"Employee #{Request.EmployeeId}";

    public string TypeName => Request.LeaveTypeName;

    public string PeriodDisplay => Request.PeriodDisplay;

    public string DaysDisplay => Request.DaysDisplay;

    public string StatusDisplay => Request.StatusDisplay;

    public bool IsPending => Request.IsPending;

    public bool IsCancellable => Request.IsCancellable;

    public Color StatusColor => Color.FromArgb(Request.Status switch
    {
        LeaveRequestStatus.Approved => "#15803D",
        LeaveRequestStatus.Pending => "#B45309",
        LeaveRequestStatus.Rejected => "#B91C1C",
        _ => "#6B7085"
    });

    /// <summary>The second line: what kind of leave it is, and where it stands.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string> { Request.PayDisplay };

            if (Request.WasOverdrawn)
                parts.Add("granted beyond the available credits");

            parts.Add(Request.IsPending ? Request.FiledDisplay : Request.DecisionDisplay);

            if (Request.Reason.Length > 0)
                parts.Add(Request.Reason);

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>One leave type and this employee's standing in it (FR-032).</summary>
public sealed class LeaveBalanceRow
{
    public LeaveBalanceRow(LeaveEntitlement entitlement)
    {
        Entitlement = entitlement;
    }

    public LeaveEntitlement Entitlement { get; }

    public int LeaveTypeId => Entitlement.LeaveTypeId;

    public string TypeName => Entitlement.Type.Name;

    public string RemainingDisplay => Entitlement.Balance.RemainingDisplay;

    public string UsedDisplay => Entitlement.Balance.UsedDisplay;

    public string TotalDisplay => Entitlement.Balance.TotalDisplay;

    public string Detail => Entitlement.Type.IsPaid
        ? Entitlement.Balance.Breakdown
        : "Unpaid — draws on no credits";

    public Color RemainingColor => Color.FromArgb(
        !Entitlement.Type.IsPaid ? "#6B7085"
        : Entitlement.Balance.Remaining <= 0m ? "#B91C1C"
        : Entitlement.Balance.Remaining < 1m ? "#B45309"
        : "#15803D");
}

/// <summary>One leave type as the configuration list renders it.</summary>
public sealed class LeaveTypeRow
{
    public LeaveTypeRow(LeaveType type) => Type = type;

    public LeaveType Type { get; }

    public int Id => Type.Id;

    public string Code => Type.Code;

    public string Name => Type.Name;

    public bool IsActive => Type.IsActive;

    public string CreditsDisplay => Type.CreditsDisplay;

    public string PayDisplay => Type.PayDisplay;

    public string ActionText => Type.IsActive ? "Retire" : "Restore";

    public Color PayColor => Color.FromArgb(Type.IsPaid ? "#15803D" : "#6B7085");

    public string Detail
    {
        get
        {
            var parts = new List<string>();

            if (!Type.IsActive)
                parts.Add("Retired");

            if (Type.AppliesTo != LeaveApplicability.Everyone)
                parts.Add(Type.AppliesToDisplay);

            if (Type.IsConvertibleToCash)
                parts.Add("convertible to cash");

            if (Type.Description.Length > 0)
                parts.Add(Type.Description);

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>Values the Pickers show and hand back.</summary>
public sealed record LeaveStatusOption(LeaveRequestStatus? Value, string Display);

public sealed class LeaveTypeOption
{
    public LeaveTypeOption(LeaveType type) => Type = type;

    public LeaveType Type { get; }

    public int Id => Type.Id;

    public string Display => Type.Name;
}

public sealed record LeaveApplicabilityOption(LeaveApplicability Value, string Display);

/// <summary>
/// Section 2.4 of the requirements: leave types, credits and filings
/// (FR-030 – FR-034).
///
/// <para><b>Three panels, one screen.</b> The queue is the everyday work —
/// decide what is waiting. The balances answer the question every filing raises,
/// "do they have the days?". The types are set once a year but decide what every
/// balance means, so they sit beside them rather than in a settings screen
/// nobody finds.</para>
///
/// <para><b>Nothing here spends a credit or touches attendance.</b> Both happen
/// inside <see cref="ILeaveService.DecideAsync"/>, so the rule that a credit is
/// spent only by an approval — and given back only by a cancellation — holds
/// however the screen is driven.</para>
/// </summary>
public sealed partial class LeaveViewModel : BaseViewModel
{
    private readonly ILeaveService _leave;
    private readonly IEmployeeService _employees;

    private IReadOnlyList<Employee> _roster = Array.Empty<Employee>();
    private IReadOnlyList<LeaveType> _types = Array.Empty<LeaveType>();

    private LeaveRequestRow? _decisionTarget;
    private LeaveRequestRow? _cancelTarget;
    private LeaveBalanceRow? _adjustTarget;
    private LeaveType? _typeTarget;

    /// <summary>
    /// False until the permission gate in <see cref="LoadAsync"/> has been
    /// passed. The year and filter properties are set in the constructor, and
    /// their change handlers would otherwise read the database before anyone had
    /// established that this user may see it.
    /// </summary>
    private bool _ready;

    public LeaveViewModel(
        ILeaveService leave,
        IEmployeeService employees,
        ISessionService session)
        : base(session)
    {
        _leave = leave;
        _employees = employees;

        Title = "Leave";
        Mode = LeaveMode.Requests;

        RequestSummary = string.Empty;
        BalanceSummary = string.Empty;
        TypeSummary = string.Empty;
        ModalError = string.Empty;

        FileReason = string.Empty;
        FilePreview = string.Empty;
        DecisionTitle = string.Empty;
        DecisionMessage = string.Empty;
        DecisionBalanceNote = string.Empty;
        DecisionRemarks = string.Empty;
        CancelMessage = string.Empty;
        CancelReason = string.Empty;
        AdjustTitle = string.Empty;
        AdjustMessage = string.Empty;
        AdjustDays = string.Empty;
        AdjustReason = string.Empty;
        TypeFormTitle = string.Empty;
        ConfirmTitle = string.Empty;
        ConfirmMessage = string.Empty;
        ConfirmAction = "Confirm";

        ErrFileType = string.Empty;
        ErrFileDates = string.Empty;
        ErrFileReason = string.Empty;
        ErrDecisionRemarks = string.Empty;
        ErrCancelReason = string.Empty;
        ErrAdjustDays = string.Empty;
        ErrAdjustReason = string.Empty;

        Year = DateTime.Today.Year;
        FileStart = DateTime.Today;
        FileEnd = DateTime.Today;

        StatusFilters =
        [
            new(null, "All requests"),
            new(LeaveRequestStatus.Pending, "Awaiting a decision"),
            new(LeaveRequestStatus.Approved, "Approved"),
            new(LeaveRequestStatus.Rejected, "Rejected"),
            new(LeaveRequestStatus.Cancelled, "Cancelled")
        ];

        // The queue opens on what needs doing, not on everything ever filed.
        SelectedStatusFilter = StatusFilters[1];

        Applicabilities =
        [
            new(LeaveApplicability.Everyone, LeaveEnumNames.Display(LeaveApplicability.Everyone)),
            new(LeaveApplicability.FemaleOnly, LeaveEnumNames.Display(LeaveApplicability.FemaleOnly)),
            new(LeaveApplicability.MaleOnly, LeaveEnumNames.Display(LeaveApplicability.MaleOnly))
        ];

        ResetTypeForm();
    }

    // -------------------------------------------------------------- panels

    public ObservableCollection<LeaveRequestRow> Requests { get; } = new();

    public ObservableCollection<LeaveBalanceRow> Balances { get; } = new();

    public ObservableCollection<LeaveTypeRow> Types { get; } = new();

    public ObservableCollection<EmployeeOption> EmployeeOptions { get; } = new();

    public ObservableCollection<LeaveTypeOption> TypeOptions { get; } = new();

    public IReadOnlyList<LeaveStatusOption> StatusFilters { get; }

    public IReadOnlyList<LeaveApplicabilityOption> Applicabilities { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRequests))]
    [NotifyPropertyChangedFor(nameof(IsBalances))]
    [NotifyPropertyChangedFor(nameof(IsTypes))]
    public partial LeaveMode Mode { get; set; }

    public bool IsRequests => Mode == LeaveMode.Requests;

    public bool IsBalances => Mode == LeaveMode.Balances;

    public bool IsTypes => Mode == LeaveMode.Types;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    /// <summary>Drives the dialog layer; while false the layer must not be hit-testable.</summary>
    public bool IsAnyModalOpen =>
        IsFileOpen || IsDecisionOpen || IsCancelOpen || IsAdjustOpen || IsTypeFormOpen || IsConfirmOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YearDisplay))]
    public partial int Year { get; set; }

    public string YearDisplay => Year.ToString(CultureInfo.InvariantCulture);

    // -------------------------------------------------------------- queue

    [ObservableProperty]
    public partial LeaveStatusOption? SelectedStatusFilter { get; set; }

    [ObservableProperty]
    public partial string RequestSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRequests))]
    public partial bool HasRequests { get; set; }

    public bool HasNoRequests => !HasRequests;

    partial void OnSelectedStatusFilterChanged(LeaveStatusOption? value) => QueueReload();

    // ----------------------------------------------------------- balances

    [ObservableProperty]
    public partial EmployeeOption? SelectedEmployee { get; set; }

    [ObservableProperty]
    public partial string BalanceSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBalances))]
    public partial bool HasBalances { get; set; }

    public bool HasNoBalances => !HasBalances;

    partial void OnSelectedEmployeeChanged(EmployeeOption? value) => QueueReload();

    // -------------------------------------------------------------- types

    [ObservableProperty]
    public partial string TypeSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoTypes))]
    public partial bool HasTypes { get; set; }

    public bool HasNoTypes => !HasTypes;

    [ObservableProperty]
    public partial bool ShowRetired { get; set; }

    partial void OnShowRetiredChanged(bool value) => QueueReload();

    // ---------------------------------------------------------- file modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsFileOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePreview))]
    public partial EmployeeOption? FileEmployee { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePreview))]
    public partial LeaveTypeOption? FileType { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePreview))]
    public partial DateTime FileStart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePreview))]
    public partial DateTime FileEnd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilePreview))]
    public partial bool FileIsHalfDay { get; set; }

    [ObservableProperty]
    public partial string FileReason { get; set; }

    [ObservableProperty]
    public partial string ErrFileType { get; set; }

    [ObservableProperty]
    public partial string ErrFileDates { get; set; }

    [ObservableProperty]
    public partial string ErrFileReason { get; set; }

    /// <summary>
    /// What the range would actually cost, recomputed as it is chosen.
    ///
    /// <para>Held as text set by <see cref="RefreshFilePreviewAsync"/> rather
    /// than computed in the getter, because working out the working days means
    /// reading the schedule and the holiday calendar — asynchronous work a
    /// property getter cannot do.</para>
    /// </summary>
    [ObservableProperty]
    public partial string FilePreview { get; set; }

    partial void OnFileEmployeeChanged(EmployeeOption? value) => _ = RefreshFilePreviewAsync();

    partial void OnFileTypeChanged(LeaveTypeOption? value) => _ = RefreshFilePreviewAsync();

    partial void OnFileStartChanged(DateTime value) => _ = RefreshFilePreviewAsync();

    partial void OnFileEndChanged(DateTime value) => _ = RefreshFilePreviewAsync();

    partial void OnFileIsHalfDayChanged(bool value) => _ = RefreshFilePreviewAsync();

    // ------------------------------------------------------ decision modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsDecisionOpen { get; set; }

    [ObservableProperty]
    public partial string DecisionTitle { get; set; }

    [ObservableProperty]
    public partial string DecisionMessage { get; set; }

    /// <summary>FR-034: what the employee has left, shown before the decision.</summary>
    [ObservableProperty]
    public partial string DecisionBalanceNote { get; set; }

    [ObservableProperty]
    public partial string DecisionRemarks { get; set; }

    /// <summary>FR-034: the explicit override that lets an overdrawn filing through.</summary>
    [ObservableProperty]
    public partial bool GrantAsUnpaid { get; set; }

    /// <summary>Shown only when the credits are actually short, so it is not idle noise.</summary>
    [ObservableProperty]
    public partial bool ShowUnpaidOverride { get; set; }

    [ObservableProperty]
    public partial string ErrDecisionRemarks { get; set; }

    // -------------------------------------------------------- cancel modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsCancelOpen { get; set; }

    [ObservableProperty]
    public partial string CancelMessage { get; set; }

    [ObservableProperty]
    public partial string CancelReason { get; set; }

    [ObservableProperty]
    public partial string ErrCancelReason { get; set; }

    // -------------------------------------------------------- adjust modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsAdjustOpen { get; set; }

    [ObservableProperty]
    public partial string AdjustTitle { get; set; }

    [ObservableProperty]
    public partial string AdjustMessage { get; set; }

    [ObservableProperty]
    public partial string AdjustDays { get; set; }

    [ObservableProperty]
    public partial string AdjustReason { get; set; }

    [ObservableProperty]
    public partial string ErrAdjustDays { get; set; }

    [ObservableProperty]
    public partial string ErrAdjustReason { get; set; }

    // ----------------------------------------------------- type form modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsTypeFormOpen { get; set; }

    [ObservableProperty]
    public partial string TypeFormTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitTypeCommand))]
    public partial string TypeCode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitTypeCommand))]
    public partial string TypeName { get; set; }

    [ObservableProperty]
    public partial string TypeDescription { get; set; }

    [ObservableProperty]
    public partial string TypeCredits { get; set; }

    [ObservableProperty]
    public partial bool TypeIsPaid { get; set; }

    [ObservableProperty]
    public partial bool TypeAccruesMonthly { get; set; }

    [ObservableProperty]
    public partial bool TypeIsConvertible { get; set; }

    [ObservableProperty]
    public partial LeaveApplicabilityOption? TypeAppliesTo { get; set; }

    [ObservableProperty]
    public partial string ErrTypeCode { get; set; }

    [ObservableProperty]
    public partial string ErrTypeName { get; set; }

    // ------------------------------------------------------- confirm modal

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
    public partial string ModalError { get; set; }

    // ------------------------------------------------------------- loading

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.ManageLeave, "open leave management"))
        {
            IsDenied = true;
            Requests.Clear();
            Balances.Clear();
            Types.Clear();
            return;
        }

        IsDenied = false;
        _ready = true;

        await RunAsync(ReloadAsync);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(ReloadAsync);

    private void QueueReload()
    {
        if (_ready)
            _ = RefreshAsync();
    }

    private async Task ReloadAsync()
    {
        ClearMessages();

        _roster = await _employees.SearchAsync(new EmployeeQuery(Status: EmployeeStatusFilter.Active));
        _types = await _leave.GetTypesAsync(includeInactive: true);

        RebuildOptions();

        await ReloadRequestsAsync();
        await ReloadBalancesAsync();
        ReloadTypes();
    }

    private void RebuildOptions()
    {
        var currentEmployee = SelectedEmployee?.Employee.Id;

        EmployeeOptions.Clear();
        foreach (var employee in _roster.OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase))
            EmployeeOptions.Add(new EmployeeOption(employee));

        SelectedEmployee = EmployeeOptions.FirstOrDefault(o => o.Employee.Id == currentEmployee)
                           ?? EmployeeOptions.FirstOrDefault();

        // Only live types can be filed against, so a retired one is not offered
        // even though the list above still shows it.
        TypeOptions.Clear();
        foreach (var type in _types.Where(t => t.IsActive))
            TypeOptions.Add(new LeaveTypeOption(type));
    }

    private async Task ReloadRequestsAsync()
    {
        var byId = _roster.ToDictionary(e => e.Id);

        var requests = await _leave.GetRequestsAsync(
            new LeaveRequestQuery(Status: SelectedStatusFilter?.Value, Year: Year));

        Requests.Clear();
        foreach (var request in requests)
            Requests.Add(new LeaveRequestRow(request, byId.GetValueOrDefault(request.EmployeeId)));

        HasRequests = Requests.Count > 0;

        var pending = Requests.Count(r => r.IsPending);

        RequestSummary = Requests.Count == 0
            ? $"Nothing filed for {Year}."
            : $"{Requests.Count} request(s) in {Year}" +
              (pending > 0 ? $" · {pending} awaiting a decision" : string.Empty);
    }

    private async Task ReloadBalancesAsync()
    {
        Balances.Clear();
        HasBalances = false;
        BalanceSummary = string.Empty;

        if (SelectedEmployee is not { } option)
            return;

        var entitlements = await _leave.GetEntitlementsAsync(option.Employee, Year);

        foreach (var entitlement in entitlements)
            Balances.Add(new LeaveBalanceRow(entitlement));

        HasBalances = Balances.Count > 0;

        var paid = entitlements.Where(e => e.IsPaid).ToList();
        var remaining = paid.Sum(e => e.Remaining);

        BalanceSummary =
            $"{option.Employee.FullName} · {remaining:0.##} paid day(s) remaining across " +
            $"{paid.Count} type(s) in {Year}";
    }

    private void ReloadTypes()
    {
        var visible = _types.Where(t => ShowRetired || t.IsActive).ToList();

        Types.Clear();
        foreach (var type in visible)
            Types.Add(new LeaveTypeRow(type));

        HasTypes = Types.Count > 0;

        var retired = _types.Count - visible.Count;

        TypeSummary = $"{visible.Count} type(s)" +
                      (retired > 0 ? $" · {retired} retired hidden" : string.Empty);
    }

    // ---------------------------------------------------------- navigation

    [RelayCommand]
    private void ShowRequests() => SwitchTo(LeaveMode.Requests);

    [RelayCommand]
    private void ShowBalances() => SwitchTo(LeaveMode.Balances);

    [RelayCommand]
    private void ShowTypes() => SwitchTo(LeaveMode.Types);

    private void SwitchTo(LeaveMode mode)
    {
        Session.Touch();
        ClearMessages();
        Mode = mode;
    }

    [RelayCommand]
    private void PreviousYear() => SetYear(Year - 1);

    [RelayCommand]
    private void NextYear() => SetYear(Year + 1);

    private void SetYear(int year)
    {
        Session.Touch();
        Year = year;
        QueueReload();
    }

    // ------------------------------------------------------ FR-031 filing

    [RelayCommand]
    private void OpenFile()
    {
        Session.Touch();
        ClearMessages();

        ModalError = string.Empty;
        ErrFileType = string.Empty;
        ErrFileDates = string.Empty;
        ErrFileReason = string.Empty;

        FileEmployee = SelectedEmployee ?? EmployeeOptions.FirstOrDefault();
        FileType = TypeOptions.FirstOrDefault();
        FileStart = DateTime.Today;
        FileEnd = DateTime.Today;
        FileIsHalfDay = false;
        FileReason = string.Empty;

        IsFileOpen = true;

        _ = RefreshFilePreviewAsync();
    }

    [RelayCommand]
    private void CloseFile()
    {
        IsFileOpen = false;
        ModalError = string.Empty;
    }

    /// <summary>
    /// Asks the service what the chosen range would cost. Runs on every change
    /// to the form, so a range that is all rest days, or one that overlaps an
    /// existing filing, is visible before the user commits to it (NFR-023).
    /// </summary>
    private async Task RefreshFilePreviewAsync()
    {
        if (!IsFileOpen || FileEmployee is not { } employee || FileType is not { } type)
        {
            FilePreview = string.Empty;
            return;
        }

        try
        {
            var preview = await _leave.PreviewAsync(
                employee.Employee, type.Id, FileStart, FileEnd, FileIsHalfDay);

            if (preview.Conflict is { } conflict)
            {
                FilePreview = conflict;
                return;
            }

            if (preview.WorkingDays == 0)
            {
                FilePreview = "Every day in that range is a rest day or a holiday, so no leave would be used.";
                return;
            }

            var parts = new List<string> { $"{preview.Days:0.##} working day(s)" };

            if (preview.IsPaidType)
            {
                parts.Add($"{preview.Remaining:0.##} day(s) remaining");

                if (preview.WouldOverdraw)
                {
                    parts.Add("this exceeds the balance, so an approver would have to grant it as unpaid");
                }
            }
            else
            {
                parts.Add("unpaid — draws on no credits");
            }

            FilePreview = string.Join("  ·  ", parts);
        }
        catch (Exception ex)
        {
            // The preview is a convenience. A failure here must not stop the
            // form being filled in; the service validates again on submit.
            FilePreview = string.Empty;
            System.Diagnostics.Debug.WriteLine($"[{nameof(LeaveViewModel)}] preview: {ex}");
        }
    }

    [RelayCommand]
    private Task SubmitFileAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrFileType = string.Empty;
        ErrFileDates = string.Empty;
        ErrFileReason = string.Empty;

        if (Session.CurrentUser is not { } performedBy)
            return;

        if (FileEmployee is not { } employee || FileType is not { } type)
        {
            ModalError = "Choose an employee and a leave type.";
            return;
        }

        var request = new LeaveRequest
        {
            EmployeeId = employee.Employee.Id,
            LeaveTypeId = type.Id,
            StartDate = FileStart,
            EndDate = FileEnd,
            IsHalfDay = FileIsHalfDay,
            Reason = FileReason
        };

        var result = await _leave.FileAsync(request, performedBy);

        if (!result.Succeeded)
        {
            ApplyFileErrors(result);
            return;
        }

        IsFileOpen = false;

        await ReloadRequestsAsync();
        await ReloadBalancesAsync();

        ShowStatus(result.Message);
    });

    private void ApplyFileErrors(LeaveSaveResult result)
    {
        if (result.FieldErrors.Count == 0)
        {
            ModalError = result.Message;
            return;
        }

        foreach (var error in result.FieldErrors)
        {
            switch (error.Field)
            {
                case nameof(LeaveRequest.LeaveTypeId):
                    ErrFileType = error.Message;
                    break;
                case nameof(LeaveRequest.StartDate):
                case nameof(LeaveRequest.EndDate):
                case nameof(LeaveRequest.IsHalfDay):
                    ErrFileDates = error.Message;
                    break;
                case nameof(LeaveRequest.Reason):
                    ErrFileReason = error.Message;
                    break;
                default:
                    ModalError = error.Message;
                    break;
            }
        }

        if (ModalError.Length == 0)
            ModalError = result.Message;
    }

    // ------------------------------------------- FR-031, FR-034 decisions

    [RelayCommand]
    private Task OpenDecisionAsync(LeaveRequestRow? row) => RunAsync(async () =>
    {
        if (row is null || !row.IsPending)
            return;

        Session.Touch();
        ClearMessages();

        _decisionTarget = row;

        ModalError = string.Empty;
        ErrDecisionRemarks = string.Empty;
        DecisionRemarks = string.Empty;
        GrantAsUnpaid = false;

        DecisionTitle = "Decide this request";
        DecisionMessage =
            $"{row.EmployeeName} has asked for {row.TypeName}: {row.PeriodDisplay}, {row.DaysDisplay}.\n\n" +
            $"Reason given: {row.Request.Reason}";

        DecisionBalanceNote = string.Empty;
        ShowUnpaidOverride = false;

        // FR-034: whether the credits are actually short decides whether the
        // override is offered at all. Asking the service means the dialog and
        // the rule cannot disagree.
        if (row.Employee is { } employee)
        {
            var preview = await _leave.PreviewAsync(
                employee, row.Request.LeaveTypeId,
                row.Request.StartDate, row.Request.EndDate, row.Request.IsHalfDay,
                excludeRequestId: row.Id);

            if (!preview.IsPaidType)
            {
                DecisionBalanceNote = "This is an unpaid leave type; approving it draws on no credits.";
            }
            else if (preview.NeedsOverride)
            {
                DecisionBalanceNote =
                    $"{employee.DisplayName} has {preview.Remaining:0.##} day(s) of {row.TypeName} left " +
                    $"and this request is for {row.Request.Days:0.##}. It can only be approved as " +
                    "unpaid leave, or after the balance is adjusted.";

                ShowUnpaidOverride = true;
            }
            else
            {
                DecisionBalanceNote =
                    $"{employee.DisplayName} has {preview.Remaining:0.##} day(s) of {row.TypeName} left; " +
                    $"{preview.Remaining - row.Request.Days:0.##} would be left after this.";
            }
        }

        IsDecisionOpen = true;
    });

    [RelayCommand]
    private void CloseDecision()
    {
        IsDecisionOpen = false;
        ModalError = string.Empty;
        _decisionTarget = null;
    }

    [RelayCommand]
    private Task ApproveAsync() => DecideAsync(approve: true);

    [RelayCommand]
    private Task RejectAsync() => DecideAsync(approve: false);

    private Task DecideAsync(bool approve) => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrDecisionRemarks = string.Empty;

        if (_decisionTarget is null || Session.CurrentUser is not { } performedBy)
            return;

        var result = await _leave.DecideAsync(
            _decisionTarget.Id, approve, DecisionRemarks, GrantAsUnpaid, performedBy);

        if (!result.Succeeded)
        {
            var remarksError = result.FieldErrors
                .FirstOrDefault(e => e.Field == nameof(LeaveRequest.DecisionRemarks));

            if (remarksError is not null)
                ErrDecisionRemarks = remarksError.Message;
            else
                ModalError = result.Message;

            return;
        }

        IsDecisionOpen = false;
        _decisionTarget = null;

        await ReloadRequestsAsync();
        await ReloadBalancesAsync();

        ShowStatus(result.Message);
    });

    // ------------------------------------------------------- cancellation

    [RelayCommand]
    private void OpenCancel(LeaveRequestRow? row)
    {
        if (row is null || !row.IsCancellable)
            return;

        Session.Touch();
        ClearMessages();

        _cancelTarget = row;

        ModalError = string.Empty;
        ErrCancelReason = string.Empty;
        CancelReason = string.Empty;

        CancelMessage = row.Request.IsApproved
            ? $"Withdraw the approved {row.TypeName} for {row.EmployeeName} ({row.PeriodDisplay})?\n\n" +
              (row.Request.IsPaid
                  ? $"The {row.Request.Days:0.##} day(s) go back on the balance, and the days stop being " +
                    "marked as leave on attendance."
                  : "It was granted unpaid, so no credits come back; the days stop being marked as " +
                    "leave on attendance.")
            : $"Withdraw the pending {row.TypeName} for {row.EmployeeName} ({row.PeriodDisplay})?\n\n" +
              "Nothing has been spent yet, so nothing is returned.";

        IsCancelOpen = true;
    }

    [RelayCommand]
    private void CloseCancel()
    {
        IsCancelOpen = false;
        ModalError = string.Empty;
        _cancelTarget = null;
    }

    [RelayCommand]
    private Task SubmitCancelAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrCancelReason = string.Empty;

        if (_cancelTarget is null || Session.CurrentUser is not { } performedBy)
            return;

        var result = await _leave.CancelAsync(_cancelTarget.Id, CancelReason, performedBy);

        if (!result.Succeeded)
        {
            var reasonError = result.FieldErrors
                .FirstOrDefault(e => e.Field == nameof(LeaveRequest.DecisionRemarks));

            if (reasonError is not null)
                ErrCancelReason = reasonError.Message;
            else
                ModalError = result.Message;

            return;
        }

        IsCancelOpen = false;
        _cancelTarget = null;

        await ReloadRequestsAsync();
        await ReloadBalancesAsync();

        ShowStatus(result.Message);
    });

    // ------------------------------------------- FR-032 balance adjustment

    [RelayCommand]
    private void OpenAdjust(LeaveBalanceRow? row)
    {
        if (row is null || SelectedEmployee is not { } option)
            return;

        Session.Touch();
        ClearMessages();

        _adjustTarget = row;

        ModalError = string.Empty;
        ErrAdjustDays = string.Empty;
        ErrAdjustReason = string.Empty;
        AdjustDays = string.Empty;
        AdjustReason = string.Empty;

        AdjustTitle = $"Adjust {row.TypeName}";
        AdjustMessage =
            $"{option.Employee.FullName} has {row.RemainingDisplay} day(s) of {row.TypeName} left " +
            $"in {Year} ({row.Detail}).\n\n" +
            "Enter a positive number to add days — carrying last year's balance forward, say — or a " +
            "negative one to take them away. The reason is written to the audit log.";

        IsAdjustOpen = true;
    }

    [RelayCommand]
    private void CloseAdjust()
    {
        IsAdjustOpen = false;
        ModalError = string.Empty;
        _adjustTarget = null;
    }

    [RelayCommand]
    private Task SubmitAdjustAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrAdjustDays = string.Empty;
        ErrAdjustReason = string.Empty;

        if (_adjustTarget is null || SelectedEmployee is not { } option ||
            Session.CurrentUser is not { } performedBy)
        {
            return;
        }

        if (!decimal.TryParse(AdjustDays, NumberStyles.Number, CultureInfo.CurrentCulture, out var delta))
        {
            ErrAdjustDays = "Enter the number of days to add or take away.";
            return;
        }

        var result = await _leave.AdjustBalanceAsync(
            option.Employee, _adjustTarget.LeaveTypeId, Year, delta, AdjustReason, performedBy);

        if (!result.Succeeded)
        {
            if (result.Message.Contains("reason", StringComparison.OrdinalIgnoreCase))
                ErrAdjustReason = result.Message;
            else if (result.Message.Contains("days", StringComparison.OrdinalIgnoreCase))
                ErrAdjustDays = result.Message;
            else
                ModalError = result.Message;

            return;
        }

        IsAdjustOpen = false;
        _adjustTarget = null;

        await ReloadBalancesAsync();

        ShowStatus(result.Message);
    });

    // -------------------------------------------------- FR-030 leave types

    [RelayCommand]
    private void OpenCreateType()
    {
        Session.Touch();
        ClearMessages();
        ResetTypeForm();

        _typeTarget = null;
        ModalError = string.Empty;
        TypeFormTitle = "New leave type";
        IsTypeFormOpen = true;
    }

    [RelayCommand]
    private void OpenEditType(LeaveTypeRow? row)
    {
        if (row is null)
            return;

        Session.Touch();
        ClearMessages();
        ResetTypeForm();

        _typeTarget = row.Type;

        ModalError = string.Empty;
        TypeFormTitle = $"Edit {row.Name}";
        TypeCode = row.Type.Code;
        TypeName = row.Type.Name;
        TypeDescription = row.Type.Description;
        TypeCredits = row.Type.DefaultAnnualCredits.ToString("0.##", CultureInfo.CurrentCulture);
        TypeIsPaid = row.Type.IsPaid;
        TypeAccruesMonthly = row.Type.AccruesMonthly;
        TypeIsConvertible = row.Type.IsConvertibleToCash;
        TypeAppliesTo = Applicabilities.FirstOrDefault(a => a.Value == row.Type.AppliesTo)
                        ?? Applicabilities[0];

        IsTypeFormOpen = true;
    }

    [RelayCommand]
    private void CloseTypeForm()
    {
        IsTypeFormOpen = false;
        ModalError = string.Empty;
        _typeTarget = null;
    }

    private bool CanSubmitType() =>
        !string.IsNullOrWhiteSpace(TypeCode) && !string.IsNullOrWhiteSpace(TypeName);

    [RelayCommand(CanExecute = nameof(CanSubmitType))]
    private Task SubmitTypeAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrTypeCode = string.Empty;
        ErrTypeName = string.Empty;

        if (Session.CurrentUser is not { } performedBy)
            return;

        if (!decimal.TryParse(TypeCredits, NumberStyles.Number, CultureInfo.CurrentCulture, out var credits))
            credits = 0m;

        var type = new LeaveType
        {
            Id = _typeTarget?.Id ?? 0,
            Code = TypeCode,
            Name = TypeName,
            Description = TypeDescription,
            DefaultAnnualCredits = credits,
            IsPaid = TypeIsPaid,
            AccruesMonthly = TypeAccruesMonthly,
            IsConvertibleToCash = TypeIsConvertible,
            AppliesTo = TypeAppliesTo?.Value ?? LeaveApplicability.Everyone
        };

        var result = await _leave.SaveTypeAsync(type, performedBy);

        if (!result.Succeeded)
        {
            if (result.Message.Contains("already used", StringComparison.OrdinalIgnoreCase))
                ErrTypeCode = result.Message;
            else
                ModalError = result.Message;

            return;
        }

        IsTypeFormOpen = false;
        _typeTarget = null;

        await RunReloadAfterTypeChangeAsync();

        ShowStatus(result.Message);
    });

    [RelayCommand]
    private void OpenToggleType(LeaveTypeRow? row)
    {
        if (row is null)
            return;

        Session.Touch();
        ClearMessages();

        _typeTarget = row.Type;
        ModalError = string.Empty;

        var retiring = row.IsActive;

        ConfirmTitle = retiring ? $"Retire {row.Name}" : $"Restore {row.Name}";
        ConfirmMessage = retiring
            ? $"{row.Name} will stop being offered when filing leave.\n\n" +
              "Leave already taken against it keeps its history, and the balances stay readable. " +
              "Requests still awaiting a decision must be decided first."
            : $"{row.Name} will be available again when filing leave.";
        ConfirmAction = retiring ? "Retire" : "Restore";
        IsConfirmDestructive = retiring;

        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void CloseConfirm()
    {
        IsConfirmOpen = false;
        ModalError = string.Empty;
        _typeTarget = null;
    }

    [RelayCommand]
    private Task SubmitConfirmAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        if (_typeTarget is null || Session.CurrentUser is not { } performedBy)
            return;

        var result = await _leave.SetTypeActiveAsync(_typeTarget.Id, !_typeTarget.IsActive, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsConfirmOpen = false;
        _typeTarget = null;

        await RunReloadAfterTypeChangeAsync();

        ShowStatus(result.Message);
    });

    /// <summary>
    /// A type change moves the ground under the other two panels — the filing
    /// picker and every balance — so all three are reread rather than only the
    /// list that was edited.
    /// </summary>
    private async Task RunReloadAfterTypeChangeAsync()
    {
        _types = await _leave.GetTypesAsync(includeInactive: true);

        RebuildOptions();
        ReloadTypes();

        await ReloadBalancesAsync();
    }

    private void ResetTypeForm()
    {
        TypeCode = string.Empty;
        TypeName = string.Empty;
        TypeDescription = string.Empty;
        TypeCredits = "0";
        TypeIsPaid = true;
        TypeAccruesMonthly = false;
        TypeIsConvertible = false;
        TypeAppliesTo = Applicabilities.Count > 0 ? Applicabilities[0] : null;
        ErrTypeCode = string.Empty;
        ErrTypeName = string.Empty;
    }
}
