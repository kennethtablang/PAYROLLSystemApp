using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>A department as the management list renders it, with its headcount.</summary>
public sealed class DepartmentRow
{
    public DepartmentRow(Department department, int headcount)
    {
        Department = department;
        Headcount = headcount;
    }

    public Department Department { get; }

    public int Headcount { get; }

    public int Id => Department.Id;

    public string Code => Department.Code;

    public string Name => Department.Name;

    public bool IsActive => Department.IsActive;

    public bool IsRetired => !Department.IsActive;

    /// <summary>
    /// The headcount doubles as the reason a department cannot be retired, so
    /// it is on the row rather than only inside the refusal message.
    /// </summary>
    public string Detail
    {
        get
        {
            var people = Headcount switch
            {
                0 => "No employees assigned",
                1 => "1 employee assigned",
                _ => $"{Headcount} employees assigned"
            };

            return Department.IsActive ? people : $"Retired · {people}";
        }
    }

    public string ActionText => Department.IsActive ? "Retire" : "Restore";
}

/// <summary>A position as the management list renders it.</summary>
public sealed class PositionRow
{
    public PositionRow(Position position, int headcount)
    {
        Position = position;
        Headcount = headcount;
    }

    public Position Position { get; }

    public int Headcount { get; }

    public int Id => Position.Id;

    public string Code => Position.Code;

    public string Title => Position.Title;

    public bool IsActive => Position.IsActive;

    public bool IsRetired => !Position.IsActive;

    /// <summary>
    /// Art. 82 is spelled out on the row. A managerial flag removes overtime,
    /// night differential and premium pay from everyone holding the post, and
    /// that is not something anyone should have to open the form to discover.
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string> { Position.Code };

            if (!Position.IsActive)
                parts.Add("retired");

            parts.Add(Position.IsManagerial
                ? "managerial — no OT or premium pay"
                : Headcount == 1 ? "1 employee" : $"{Headcount} employees");

            return string.Join(" · ", parts);
        }
    }

    public string ActionText => Position.IsActive ? "Retire" : "Restore";
}

/// <summary>A work schedule as the management list renders it.</summary>
public sealed class WorkScheduleRow
{
    public WorkScheduleRow(WorkSchedule schedule, int headcount)
    {
        Schedule = schedule;
        Headcount = headcount;
    }

    public WorkSchedule Schedule { get; }

    public int Headcount { get; }

    public int Id => Schedule.Id;

    public string Name => Schedule.Name;

    public bool IsActive => Schedule.IsActive;

    public string TimeDisplay => Schedule.TimeDisplay;

    public string DaysDisplay => Schedule.DaysDisplay;

    public string HoursDisplay => $"{Schedule.StandardHours:0.##} h";

    public string GraceDisplay =>
        Schedule.GraceMinutes == 0 ? "No grace" : $"{Schedule.GraceMinutes} min grace";

    public string Detail
    {
        get
        {
            var parts = new List<string>();

            if (!Schedule.IsActive)
                parts.Add("Retired");

            parts.Add(Headcount == 1 ? "1 employee" : $"{Headcount} employees");
            parts.Add(GraceDisplay);

            // A shift running past midnight sits entirely inside the 22:00-06:00
            // night differential window, which is worth saying on the row.
            if (Schedule.CrossesMidnight)
                parts.Add("crosses midnight");

            return string.Join(" · ", parts);
        }
    }

    public string ActionText => Schedule.IsActive ? "Retire" : "Restore";
}

/// <summary>
/// FR-012 reference data: the departments, positions and work schedules an
/// employee record is assigned to.
///
/// <para>Both lists sit on one screen because they are edited together — a new
/// team usually arrives as a department and the posts inside it — and because
/// neither on its own justifies a section in the sidebar.</para>
///
/// <para><b>Nothing is deleted.</b> A department names the unit on every payslip
/// already produced against it, so it is retired instead, and retiring is
/// refused while anyone is still assigned (see
/// <see cref="OrganizationService.SetDepartmentActiveAsync"/>). That refusal is
/// the reason the headcount is shown on every row.</para>
/// </summary>
public sealed partial class OrganizationViewModel : BaseViewModel
{
    private readonly IOrganizationService _organization;

    private Department? _departmentTarget;
    private Position? _positionTarget;
    private WorkSchedule? _scheduleTarget;

    /// <summary>What the open confirmation is about. One dialog serves all three.</summary>
    private enum ConfirmTarget { Department, Position, WorkSchedule }

    private ConfirmTarget _confirmTarget;

    private bool _confirmActivates;

    public OrganizationViewModel(IOrganizationService organization, ISessionService session)
        : base(session)
    {
        _organization = organization;

        Title = "Departments & positions";

        DepartmentSummary = string.Empty;
        PositionSummary = string.Empty;
        ModalError = string.Empty;

        DepartmentFormTitle = string.Empty;
        PositionFormTitle = string.Empty;
        ScheduleFormTitle = string.Empty;
        ConfirmTitle = string.Empty;
        ConfirmMessage = string.Empty;
        ConfirmAction = "Confirm";

        ResetDepartmentForm();
        ResetPositionForm();
        ResetScheduleForm();
    }

    /// <summary>Drives the dialog layer; while false the layer must not be hit-testable.</summary>
    public bool IsAnyModalOpen =>
        IsDepartmentFormOpen || IsPositionFormOpen || IsScheduleFormOpen || IsConfirmOpen;

    public ObservableCollection<DepartmentRow> Departments { get; } = new();

    public ObservableCollection<PositionRow> Positions { get; } = new();

    public ObservableCollection<WorkScheduleRow> Schedules { get; } = new();

    // -------------------------------------------------------------- listing

    [ObservableProperty]
    public partial string DepartmentSummary { get; set; }

    [ObservableProperty]
    public partial string PositionSummary { get; set; }

    [ObservableProperty]
    public partial string ScheduleSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDepartments))]
    public partial bool HasDepartments { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPositions))]
    public partial bool HasPositions { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSchedules))]
    public partial bool HasSchedules { get; set; }

    /// <summary>
    /// Retired rows are hidden by default — the everyday task is assigning
    /// somebody to a live department — but they must be reachable, because
    /// restoring one is otherwise impossible from this screen.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowRetired { get; set; }

    public bool IsGranted => !IsDenied;

    public bool HasNoDepartments => !HasDepartments;

    public bool HasNoPositions => !HasPositions;

    public bool HasNoSchedules => !HasSchedules;

    partial void OnShowRetiredChanged(bool value) => _ = RefreshAsync();

    // ------------------------------------------------------ department form

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsDepartmentFormOpen { get; set; }

    [ObservableProperty]
    public partial string DepartmentFormTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitDepartmentCommand))]
    public partial string DepartmentCode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitDepartmentCommand))]
    public partial string DepartmentName { get; set; }

    [ObservableProperty]
    public partial string DepartmentDescription { get; set; }


    [ObservableProperty]
    public partial string ErrDepartmentCode { get; set; }

    [ObservableProperty]
    public partial string ErrDepartmentName { get; set; }

    // -------------------------------------------------------- position form

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsPositionFormOpen { get; set; }

    [ObservableProperty]
    public partial string PositionFormTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPositionCommand))]
    public partial string PositionCode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPositionCommand))]
    public partial string PositionTitle { get; set; }

    [ObservableProperty]
    public partial bool PositionIsManagerial { get; set; }

    [ObservableProperty]
    public partial string ErrPositionCode { get; set; }

    [ObservableProperty]
    public partial string ErrPositionTitle { get; set; }

    // --------------------------------------------------- work schedule form

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsScheduleFormOpen { get; set; }

    [ObservableProperty]
    public partial string ScheduleFormTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitScheduleCommand))]
    public partial string ScheduleName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial TimeSpan ScheduleStart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial TimeSpan ScheduleEnd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial string ScheduleBreak { get; set; }

    [ObservableProperty]
    public partial string ScheduleGrace { get; set; }

    [ObservableProperty]
    public partial string ErrScheduleName { get; set; }

    // One property per day rather than a bitmask bound through a converter, so
    // each checkbox binds to something a reader can find.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial bool DaySunday { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial bool DayMonday { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial bool DayTuesday { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial bool DayWednesday { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial bool DayThursday { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial bool DayFriday { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SchedulePreview))]
    public partial bool DaySaturday { get; set; }

    /// <summary>
    /// What the entered figures actually amount to, recomputed as they are
    /// typed. Paid hours are derived from the times and the break rather than
    /// entered, and a schedule that pays nothing — a break as long as the shift
    /// — is refused on save, so the arithmetic is shown before that happens.
    /// </summary>
    public string SchedulePreview
    {
        get
        {
            var draft = BuildSchedule();

            if (draft.WorkDays == 0)
                return "No working days selected.";

            if (draft.StandardHours <= 0)
                return "The break is as long as the shift, so this schedule pays no hours.";

            var midnight = draft.CrossesMidnight
                ? "  ·  crosses midnight, so the whole shift falls in the night differential window"
                : string.Empty;

            return $"{draft.StandardHours:0.##} paid hours a day  ·  {draft.DaysDisplay}{midnight}";
        }
    }

    // -------------------------------------------------------- confirm modal

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

    // -------------------------------------------------------------- loading

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.ManageEmployees, "open departments and positions"))
        {
            IsDenied = true;
            Departments.Clear();
            Positions.Clear();
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

        var departments = await _organization.GetDepartmentsAsync(includeInactive: true);
        var positions = await _organization.GetPositionsAsync(includeInactive: true);
        var schedules = await _organization.GetWorkSchedulesAsync(includeInactive: true);
        var headcounts = await _organization.GetHeadcountsAsync();

        var visibleDepartments = departments.Where(d => ShowRetired || d.IsActive).ToList();
        var visiblePositions = positions.Where(p => ShowRetired || p.IsActive).ToList();
        var visibleSchedules = schedules.Where(x => ShowRetired || x.IsActive).ToList();

        Departments.Clear();
        foreach (var department in visibleDepartments)
            Departments.Add(new DepartmentRow(department, headcounts.Department(department.Id)));

        Positions.Clear();
        foreach (var position in visiblePositions)
            Positions.Add(new PositionRow(position, headcounts.Position(position.Id)));

        Schedules.Clear();
        foreach (var schedule in visibleSchedules)
            Schedules.Add(new WorkScheduleRow(schedule, headcounts.Schedule(schedule.Id)));

        HasDepartments = Departments.Count > 0;
        HasPositions = Positions.Count > 0;
        HasSchedules = Schedules.Count > 0;

        DepartmentSummary = Summarise(departments.Count, visibleDepartments.Count, "department");
        PositionSummary = Summarise(positions.Count, visiblePositions.Count, "position");
        ScheduleSummary = Summarise(schedules.Count, visibleSchedules.Count, "schedule");
    }

    private static string Summarise(int total, int shown, string noun)
    {
        var retired = total - shown;

        return retired > 0
            ? $"{shown} active {noun}(s) · {retired} retired hidden"
            : $"{shown} {noun}(s)";
    }

    // ------------------------------------------------------------ department

    [RelayCommand]
    private void OpenCreateDepartment()
    {
        ClearMessages();
        ResetDepartmentForm();

        _departmentTarget = null;
        DepartmentFormTitle = "New department";
        IsDepartmentFormOpen = true;
    }

    [RelayCommand]
    private void OpenEditDepartment(DepartmentRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ResetDepartmentForm();

        _departmentTarget = row.Department;
        DepartmentFormTitle = $"Edit {row.Name}";
        DepartmentCode = row.Department.Code;
        DepartmentName = row.Department.Name;
        DepartmentDescription = row.Department.Description;

        IsDepartmentFormOpen = true;
    }

    [RelayCommand]
    private void CloseDepartmentForm()
    {
        IsDepartmentFormOpen = false;
        ModalError = string.Empty;
        _departmentTarget = null;
    }

    private bool CanSubmitDepartment() =>
        !string.IsNullOrWhiteSpace(DepartmentCode) && !string.IsNullOrWhiteSpace(DepartmentName);

    [RelayCommand(CanExecute = nameof(CanSubmitDepartment))]
    private Task SubmitDepartmentAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrDepartmentCode = string.Empty;
        ErrDepartmentName = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        var department = new Department
        {
            Id = _departmentTarget?.Id ?? 0,
            Code = DepartmentCode,
            Name = DepartmentName,
            Description = DepartmentDescription
        };

        var result = await _organization.SaveDepartmentAsync(department, performedBy);

        if (!result.Succeeded)
        {
            // The only field-specific failure the service reports is a clashing
            // code, so it is placed beside that input rather than in the banner.
            if (result.Message.Contains("already used", StringComparison.OrdinalIgnoreCase))
                ErrDepartmentCode = result.Message;
            else
                ModalError = result.Message;

            return;
        }

        IsDepartmentFormOpen = false;
        _departmentTarget = null;

        ShowStatus(result.Message);
        await ReloadAsync();
    });

    // -------------------------------------------------------------- position

    [RelayCommand]
    private void OpenCreatePosition()
    {
        ClearMessages();
        ResetPositionForm();

        _positionTarget = null;
        PositionFormTitle = "New position";
        IsPositionFormOpen = true;
    }

    [RelayCommand]
    private void OpenEditPosition(PositionRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ResetPositionForm();

        _positionTarget = row.Position;
        PositionFormTitle = $"Edit {row.Title}";
        PositionCode = row.Position.Code;
        PositionTitle = row.Position.Title;
        PositionIsManagerial = row.Position.IsManagerial;

        IsPositionFormOpen = true;
    }

    [RelayCommand]
    private void ClosePositionForm()
    {
        IsPositionFormOpen = false;
        ModalError = string.Empty;
        _positionTarget = null;
    }

    private bool CanSubmitPosition() =>
        !string.IsNullOrWhiteSpace(PositionCode) && !string.IsNullOrWhiteSpace(PositionTitle);

    [RelayCommand(CanExecute = nameof(CanSubmitPosition))]
    private Task SubmitPositionAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrPositionCode = string.Empty;
        ErrPositionTitle = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        var wasManagerial = _positionTarget?.IsManagerial ?? false;

        var position = new Position
        {
            Id = _positionTarget?.Id ?? 0,
            Code = PositionCode,
            Title = PositionTitle,
            IsManagerial = PositionIsManagerial
        };

        var result = await _organization.SavePositionAsync(position, performedBy);

        if (!result.Succeeded)
        {
            if (result.Message.Contains("already used", StringComparison.OrdinalIgnoreCase))
                ErrPositionCode = result.Message;
            else
                ModalError = result.Message;

            return;
        }

        IsPositionFormOpen = false;

        // Turning the flag on silently stops overtime for everyone holding the
        // post, so the change is reported rather than left to be noticed on a
        // payslip that came out short.
        var message = result.Message;

        if (_positionTarget is not null && wasManagerial != PositionIsManagerial)
        {
            var affected = await _organization.CountEmployeesInPositionAsync(_positionTarget.Id);

            if (affected > 0)
            {
                message += PositionIsManagerial
                    ? $" {affected} employee(s) in this position will no longer earn overtime, " +
                      "night differential or premium pay (Art. 82)."
                    : $" {affected} employee(s) in this position will now earn overtime and premium pay.";
            }
        }

        _positionTarget = null;

        ShowStatus(message);
        await ReloadAsync();
    });

    // ------------------------------------------------------- work schedule

    [RelayCommand]
    private void OpenCreateSchedule()
    {
        ClearMessages();
        ResetScheduleForm();

        _scheduleTarget = null;
        ScheduleFormTitle = "New work schedule";
        IsScheduleFormOpen = true;
    }

    [RelayCommand]
    private void OpenEditSchedule(WorkScheduleRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ResetScheduleForm();

        _scheduleTarget = row.Schedule;
        ScheduleFormTitle = $"Edit {row.Name}";

        ScheduleName = row.Schedule.Name;
        ScheduleStart = TimeSpan.FromMinutes(row.Schedule.StartMinutes);
        ScheduleEnd = TimeSpan.FromMinutes(row.Schedule.EndMinutes);
        ScheduleBreak = row.Schedule.BreakMinutes.ToString();
        ScheduleGrace = row.Schedule.GraceMinutes.ToString();

        var days = row.Schedule.WorkDays;
        DaySunday = (days & (1 << (int)DayOfWeek.Sunday)) != 0;
        DayMonday = (days & (1 << (int)DayOfWeek.Monday)) != 0;
        DayTuesday = (days & (1 << (int)DayOfWeek.Tuesday)) != 0;
        DayWednesday = (days & (1 << (int)DayOfWeek.Wednesday)) != 0;
        DayThursday = (days & (1 << (int)DayOfWeek.Thursday)) != 0;
        DayFriday = (days & (1 << (int)DayOfWeek.Friday)) != 0;
        DaySaturday = (days & (1 << (int)DayOfWeek.Saturday)) != 0;

        IsScheduleFormOpen = true;
    }

    [RelayCommand]
    private void CloseScheduleForm()
    {
        IsScheduleFormOpen = false;
        ModalError = string.Empty;
        _scheduleTarget = null;
    }

    private bool CanSubmitSchedule() => !string.IsNullOrWhiteSpace(ScheduleName);

    [RelayCommand(CanExecute = nameof(CanSubmitSchedule))]
    private Task SubmitScheduleAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrScheduleName = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        var schedule = BuildSchedule();

        var result = await _organization.SaveWorkScheduleAsync(schedule, performedBy);

        if (!result.Succeeded)
        {
            if (result.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                ErrScheduleName = result.Message;
            else
                ModalError = result.Message;

            return;
        }

        IsScheduleFormOpen = false;

        // Editing the shift changes what counts as late and as overtime for
        // everyone on it from here on, so the affected headcount is reported
        // rather than left to surface as a run of unexpected tardiness.
        var message = result.Message;

        if (_scheduleTarget is not null)
        {
            var affected = await _organization.CountEmployeesOnScheduleAsync(_scheduleTarget.Id);

            if (affected > 0)
            {
                message += $" {affected} employee(s) are on this schedule; attendance recorded from now " +
                           "on is measured against the new times.";
            }
        }

        _scheduleTarget = null;

        ShowStatus(message);
        await ReloadAsync();
    });

    // ------------------------------------------------- retire and restore

    [RelayCommand]
    private void OpenToggleDepartment(DepartmentRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ModalError = string.Empty;

        _departmentTarget = row.Department;
        _confirmTarget = ConfirmTarget.Department;
        _confirmActivates = !row.IsActive;

        if (row.IsActive)
        {
            ConfirmTitle = "Retire department";
            ConfirmAction = "Retire";
            IsConfirmDestructive = true;

            // The refusal is stated up front when it is already known, rather
            // than after the user has committed to the action.
            ConfirmMessage = row.Headcount > 0
                ? $"{row.Name} still has {row.Headcount} active employee(s) assigned, so it cannot be " +
                  "retired yet. Move them to another department first."
                : $"{row.Name} will stop appearing when assigning employees. It is kept on every " +
                  "record already filed against it, and can be restored later.";
        }
        else
        {
            ConfirmTitle = "Restore department";
            ConfirmAction = "Restore";
            IsConfirmDestructive = false;
            ConfirmMessage = $"{row.Name} will be available again when assigning employees.";
        }

        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void OpenTogglePosition(PositionRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ModalError = string.Empty;

        _positionTarget = row.Position;
        _confirmTarget = ConfirmTarget.Position;
        _confirmActivates = !row.IsActive;

        if (row.IsActive)
        {
            ConfirmTitle = "Retire position";
            ConfirmAction = "Retire";
            IsConfirmDestructive = true;

            ConfirmMessage = row.Headcount > 0
                ? $"{row.Title} is still held by {row.Headcount} active employee(s), so it cannot be " +
                  "retired yet. Reassign them first."
                : $"{row.Title} will stop appearing when assigning employees. It is kept on every " +
                  "record already filed against it, and can be restored later.";
        }
        else
        {
            ConfirmTitle = "Restore position";
            ConfirmAction = "Restore";
            IsConfirmDestructive = false;
            ConfirmMessage = $"{row.Title} will be available again when assigning employees.";
        }

        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void OpenToggleSchedule(WorkScheduleRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ModalError = string.Empty;

        _scheduleTarget = row.Schedule;
        _confirmTarget = ConfirmTarget.WorkSchedule;
        _confirmActivates = !row.IsActive;

        if (row.IsActive)
        {
            ConfirmTitle = "Retire schedule";
            ConfirmAction = "Retire";
            IsConfirmDestructive = true;

            // The stakes are higher than for a name: a schedule decides what
            // counts as late, as undertime and as overtime, so anyone left on a
            // retired one would still be measured against a shift that appears
            // nowhere on screen.
            ConfirmMessage = row.Headcount > 0
                ? $"{row.Name} is still assigned to {row.Headcount} active employee(s), so it cannot be " +
                  "retired yet. Move them to another schedule first."
                : $"{row.Name} will stop appearing when assigning employees. Attendance already measured " +
                  "against it is unchanged, and it can be restored later.";
        }
        else
        {
            ConfirmTitle = "Restore schedule";
            ConfirmAction = "Restore";
            IsConfirmDestructive = false;
            ConfirmMessage = $"{row.Name} will be available again when assigning employees.";
        }

        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void CloseConfirm()
    {
        IsConfirmOpen = false;
        ModalError = string.Empty;
        _departmentTarget = null;
        _positionTarget = null;
        _scheduleTarget = null;
    }

    [RelayCommand]
    private Task SubmitConfirmAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        // The services re-check the headcount in every case. The dialog's
        // warning is guidance; those checks are the rule.
        bool succeeded;
        string message;

        switch (_confirmTarget)
        {
            case ConfirmTarget.Department when _departmentTarget is not null:
            {
                var result = await _organization.SetDepartmentActiveAsync(
                    _departmentTarget.Id, _confirmActivates, performedBy);

                (succeeded, message) = (result.Succeeded, result.Message);
                break;
            }

            case ConfirmTarget.Position when _positionTarget is not null:
            {
                var result = await _organization.SetPositionActiveAsync(
                    _positionTarget.Id, _confirmActivates, performedBy);

                (succeeded, message) = (result.Succeeded, result.Message);
                break;
            }

            case ConfirmTarget.WorkSchedule when _scheduleTarget is not null:
            {
                var result = await _organization.SetWorkScheduleActiveAsync(
                    _scheduleTarget.Id, _confirmActivates, performedBy);

                (succeeded, message) = (result.Succeeded, result.Message);
                break;
            }

            // The dialog is open with nothing behind it. Nothing to do, and
            // nothing to report either.
            default:
                return;
        }

        if (!succeeded)
        {
            ModalError = message;
            return;
        }

        IsConfirmOpen = false;
        _departmentTarget = null;
        _positionTarget = null;
        _scheduleTarget = null;

        ShowStatus(message);
        await ReloadAsync();
    });

    // ------------------------------------------------------------ internals

    private void ResetDepartmentForm()
    {
        DepartmentCode = string.Empty;
        DepartmentName = string.Empty;
        DepartmentDescription = string.Empty;
        ErrDepartmentCode = string.Empty;
        ErrDepartmentName = string.Empty;
    }

    private void ResetPositionForm()
    {
        PositionCode = string.Empty;
        PositionTitle = string.Empty;
        PositionIsManagerial = false;
        ErrPositionCode = string.Empty;
        ErrPositionTitle = string.Empty;
    }

    private void ResetScheduleForm()
    {
        ScheduleName = string.Empty;
        ScheduleStart = TimeSpan.FromHours(8);
        ScheduleEnd = TimeSpan.FromHours(17);
        ScheduleBreak = "60";
        ScheduleGrace = "15";
        ErrScheduleName = string.Empty;

        DaySunday = false;
        DayMonday = true;
        DayTuesday = true;
        DayWednesday = true;
        DayThursday = true;
        DayFriday = true;
        DaySaturday = false;
    }

    /// <summary>
    /// The form as a schedule. Used by the live preview and by the save, so the
    /// arithmetic shown while typing is the arithmetic that gets stored.
    ///
    /// <para>Minutes are parsed leniently — a blank or unreadable break is nil
    /// rather than an error, because the preview runs on every keystroke and
    /// must not blow up halfway through a number being typed. The service
    /// refuses a schedule that pays no hours, which is the case that matters.</para>
    /// </summary>
    private WorkSchedule BuildSchedule() => new()
    {
        Id = _scheduleTarget?.Id ?? 0,
        Name = ScheduleName ?? string.Empty,
        StartMinutes = (int)ScheduleStart.TotalMinutes,
        EndMinutes = (int)ScheduleEnd.TotalMinutes,
        BreakMinutes = ParseMinutes(ScheduleBreak),
        GraceMinutes = ParseMinutes(ScheduleGrace),
        WorkDays = SelectedWorkDays()
    };

    private static int ParseMinutes(string? value) =>
        int.TryParse(value, out var minutes) && minutes >= 0 ? minutes : 0;

    /// <summary>
    /// The seven checkboxes as the bitmask the record stores. Bit positions
    /// match <see cref="DayOfWeek"/>, so Sunday is bit 0.
    /// </summary>
    private int SelectedWorkDays()
    {
        var mask = 0;

        if (DaySunday) mask |= 1 << (int)DayOfWeek.Sunday;
        if (DayMonday) mask |= 1 << (int)DayOfWeek.Monday;
        if (DayTuesday) mask |= 1 << (int)DayOfWeek.Tuesday;
        if (DayWednesday) mask |= 1 << (int)DayOfWeek.Wednesday;
        if (DayThursday) mask |= 1 << (int)DayOfWeek.Thursday;
        if (DayFriday) mask |= 1 << (int)DayOfWeek.Friday;
        if (DaySaturday) mask |= 1 << (int)DayOfWeek.Saturday;

        return mask;
    }
}
