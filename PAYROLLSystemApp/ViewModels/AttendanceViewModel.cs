using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>Which of the three panels the section is showing.</summary>
public enum AttendanceMode
{
    /// <summary>Everyone, one date. The screen a timekeeper works from daily.</summary>
    Daily,

    /// <summary>One employee, a whole cut-off, with its summary (FR-027).</summary>
    Timesheet,

    /// <summary>The holiday calendar the days are classified against (FR-025).</summary>
    Holidays
}

/// <summary>One day of one employee, as the daily board and the timesheet render it.</summary>
public sealed class AttendanceRow
{
    public AttendanceRow(Employee employee, AttendanceRecord record)
    {
        Employee = employee;
        Record = record;
    }

    public Employee Employee { get; }

    public AttendanceRecord Record { get; }

    public int Id => Record.Id;

    /// <summary>A draft has never been saved, so there is nothing yet to correct.</summary>
    public bool IsDraft => Record.Id == 0;

    public bool IsRecorded => Record.Id != 0;

    public string EmployeeName => Employee.FullName;

    public string EmployeeNumber => Employee.EmployeeNumber;

    public string Initials => Employee.Initials;

    public string DateDisplay => Record.DateDisplay;

    public string DayDisplay => Record.DayDisplay;

    public string TimeInDisplay => Record.TimeInDisplay;

    public string TimeOutDisplay => Record.TimeOutDisplay;

    public string HoursDisplay => Record.HoursDisplay;

    public string LateDisplay => Record.LateDisplay;

    public string UndertimeDisplay => Record.UndertimeDisplay;

    public string NightDifferentialDisplay => Record.NightDifferentialDisplay;

    public string OvertimeDisplay => Record.OvertimeDisplay;

    public bool IsLocked => Record.IsLocked;

    public bool HasPendingOvertime => Record.HasPendingOvertime && !Record.IsLocked;

    /// <summary>
    /// A day can only be removed once it exists and before a posted payroll run
    /// has locked it (NFR-037).
    /// </summary>
    public bool IsRemovable => IsRecorded && !Record.IsLocked;

    public string ActionText => IsDraft ? "Record" : "Edit";

    /// <summary>
    /// The status, plus why the row looks the way it does. A day nobody has
    /// entered is called out as such rather than shown as an absence — payroll
    /// treats the two very differently.
    /// </summary>
    public string StatusDisplay => IsDraft ? "Not recorded" : Record.StatusDisplay;

    /// <summary>
    /// A <see cref="Color"/> rather than a hex string: binding a string to
    /// <c>TextColor</c> leans on a runtime type conversion that fails quietly
    /// and leaves the label the default colour.
    /// </summary>
    public Color StatusColor => Color.FromArgb(IsDraft
        ? "#9AA0B2"
        : Record.Status switch
        {
            AttendanceStatus.Present => "#15803D",
            AttendanceStatus.HalfDay => "#B45309",
            AttendanceStatus.Absent => "#B91C1C",
            AttendanceStatus.OnLeave => "#4338CA",
            _ => "#6B7085"
        });

    /// <summary>The second line of the row: what the day was worth, and any flags.</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string> { Record.DayTypeDisplay };

            if (Record.HolidayName.Length > 0)
                parts.Add(Record.HolidayName);

            if (Record.IsLocked)
                parts.Add("locked by a posted run");
            else if (Record.HasPendingOvertime)
                parts.Add($"{Record.OvertimePending:0.##} h overtime awaiting approval");
            else if (Record.WasOverridden)
                parts.Add($"corrected by {Record.OverriddenBy}");
            else if (Record.Source == AttendanceSource.Generated)
                parts.Add("generated, no punches");

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>A holiday as the calendar list renders it, resolved into one year.</summary>
public sealed class HolidayRow
{
    public HolidayRow(Holiday holiday, int year)
    {
        Holiday = holiday;
        Occurrence = holiday.OccurrenceIn(year);
    }

    public Holiday Holiday { get; }

    public DateTime Occurrence { get; }

    public int Id => Holiday.Id;

    public string Name => Holiday.Name;

    public bool IsActive => Holiday.IsActive;

    public string DateDisplay => Occurrence.ToString("ddd, dd MMM yyyy");

    public string TypeDisplay => Holiday.TypeDisplay;

    public string ActionText => Holiday.IsActive ? "Retire" : "Restore";

    public Color TypeColor => Color.FromArgb(Holiday.Type switch
    {
        HolidayType.Regular => "#B91C1C",
        HolidayType.SpecialNonWorking => "#B45309",
        _ => "#6B7085"
    });

    public string Detail
    {
        get
        {
            var parts = new List<string>();

            if (!Holiday.IsActive)
                parts.Add("Retired");

            parts.Add(Holiday.IsAnnual ? "Every year on this date" : "Proclaimed for this year only");

            if (Holiday.Remarks.Length > 0)
                parts.Add(Holiday.Remarks);

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>A value a Picker can show and hand back, for the enum drop-downs.</summary>
public sealed record HolidayTypeOption(HolidayType Value, string Display);

public sealed record AttendanceStatusOption(AttendanceStatus Value, string Display);

public sealed class EmployeeOption
{
    public EmployeeOption(Employee employee) => Employee = employee;

    public Employee Employee { get; }

    public string Display => $"{Employee.EmployeeNumber} — {Employee.FullName}";
}

/// <summary>
/// Section 2.3 of the requirements: daily time records, overtime, the holiday
/// calendar and the cut-off summary (FR-020 – FR-027).
///
/// <para><b>Three panels, one screen.</b> The daily board is what a timekeeper
/// works from — everyone, one date. The timesheet is what a supervisor and
/// payroll read — one employee, a whole cut-off, with its totals. The calendar
/// is maintained a few times a year but decides what every worked hour on those
/// dates is worth, so it belongs beside them rather than buried in settings.</para>
///
/// <para><b>Nothing is computed here.</b> Hours, lateness, undertime, night
/// differential and overtime come from
/// <see cref="AttendanceCalculator"/> through
/// <see cref="IAttendanceService"/>. The dialog's live preview calls the very
/// same calculator, so what the form shows before saving and what the row shows
/// after it cannot disagree.</para>
/// </summary>
public sealed partial class AttendanceViewModel : BaseViewModel
{
    private readonly IAttendanceService _attendance;
    private readonly IHolidayService _holidays;
    private readonly IEmployeeService _employees;
    private readonly IOrganizationService _organization;

    private IReadOnlyList<Employee> _roster = Array.Empty<Employee>();
    private IReadOnlyDictionary<int, WorkSchedule> _schedules = new Dictionary<int, WorkSchedule>();

    private AttendanceRow? _entryTarget;
    private AttendanceRow? _approvalTarget;
    private Holiday? _holidayTarget;

    /// <summary>What the open confirmation is about. One dialog serves all three.</summary>
    private enum ConfirmTarget { GenerateCutoff, RemoveEntry, ToggleHoliday }

    private ConfirmTarget _confirmTarget;

    /// <summary>
    /// False until the permission gate in <see cref="LoadAsync"/> has been
    /// passed. The date and period properties are set in the constructor, and
    /// their change handlers would otherwise start reading the database before
    /// anyone had established that this user may see it.
    /// </summary>
    private bool _ready;

    /// <summary>
    /// Set while several properties that each trigger a reload are changed
    /// together. Without it, moving a cut-off would fire two reloads, and
    /// <see cref="BaseViewModel.RunAsync"/> would drop the second as re-entrant
    /// — leaving the sheet showing the half-applied range.
    /// </summary>
    private bool _suppressReload;

    public AttendanceViewModel(
        IAttendanceService attendance,
        IHolidayService holidays,
        IEmployeeService employees,
        IOrganizationService organization,
        ISessionService session)
        : base(session)
    {
        _attendance = attendance;
        _holidays = holidays;
        _employees = employees;
        _organization = organization;

        Title = "Time and attendance";

        Mode = AttendanceMode.Daily;

        SearchTerm = string.Empty;
        BoardSummary = string.Empty;
        DayClassification = string.Empty;
        TimesheetSummary = string.Empty;
        HolidaySummary = string.Empty;
        ModalError = string.Empty;

        EntryTitle = string.Empty;
        EntrySubtitle = string.Empty;
        ApprovalTitle = string.Empty;
        ApprovalMessage = string.Empty;
        ApprovalHours = string.Empty;
        ErrApprovalHours = string.Empty;
        HolidayFormTitle = string.Empty;
        ConfirmTitle = string.Empty;
        ConfirmMessage = string.Empty;
        ConfirmAction = "Confirm";

        SelectedDate = DateTime.Today;
        HolidayYear = DateTime.Today.Year;

        // Default to the cut-off the current date falls in (A-02: semi-monthly).
        var today = DateTime.Today;
        if (today.Day <= 15)
        {
            PeriodStart = new DateTime(today.Year, today.Month, 1);
            PeriodEnd = new DateTime(today.Year, today.Month, 15);
        }
        else
        {
            PeriodStart = new DateTime(today.Year, today.Month, 16);
            PeriodEnd = new DateTime(today.Year, today.Month,
                DateTime.DaysInMonth(today.Year, today.Month));
        }

        HolidayTypes =
        [
            new(HolidayType.None, AttendanceEnumNames.Display(HolidayType.None)),
            new(HolidayType.Regular, AttendanceEnumNames.Display(HolidayType.Regular)),
            new(HolidayType.SpecialNonWorking, AttendanceEnumNames.Display(HolidayType.SpecialNonWorking)),
            new(HolidayType.SpecialWorking, AttendanceEnumNames.Display(HolidayType.SpecialWorking))
        ];

        // Only the two an officer may assert. The rest are conclusions the
        // calculator draws from the punches, and offering them here would let
        // somebody mark a day "Present" that has no hours on it.
        StatusOptions =
        [
            new(AttendanceStatus.Present, "Derive from the punches"),
            new(AttendanceStatus.OnLeave, AttendanceEnumNames.Display(AttendanceStatus.OnLeave)),
            new(AttendanceStatus.Suspended, AttendanceEnumNames.Display(AttendanceStatus.Suspended))
        ];

        ResetEntryForm();
        ResetHolidayForm();
    }

    // -------------------------------------------------------------- panels

    public ObservableCollection<AttendanceRow> Board { get; } = new();

    public ObservableCollection<AttendanceRow> Timesheet { get; } = new();

    public ObservableCollection<HolidayRow> Holidays { get; } = new();

    public ObservableCollection<EmployeeOption> EmployeeOptions { get; } = new();

    public IReadOnlyList<HolidayTypeOption> HolidayTypes { get; }

    public IReadOnlyList<AttendanceStatusOption> StatusOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDaily))]
    [NotifyPropertyChangedFor(nameof(IsTimesheet))]
    [NotifyPropertyChangedFor(nameof(IsHolidays))]
    public partial AttendanceMode Mode { get; set; }

    public bool IsDaily => Mode == AttendanceMode.Daily;

    public bool IsTimesheet => Mode == AttendanceMode.Timesheet;

    public bool IsHolidays => Mode == AttendanceMode.Holidays;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    /// <summary>Drives the dialog layer; while false the layer must not be hit-testable.</summary>
    public bool IsAnyModalOpen =>
        IsEntryOpen || IsApprovalOpen || IsHolidayFormOpen || IsConfirmOpen;

    // --------------------------------------------------------- daily board

    [ObservableProperty]
    public partial DateTime SelectedDate { get; set; }

    [ObservableProperty]
    public partial string SearchTerm { get; set; }

    [ObservableProperty]
    public partial string BoardSummary { get; set; }

    /// <summary>What the selected date is worth before anyone works it.</summary>
    [ObservableProperty]
    public partial string DayClassification { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBoard))]
    public partial bool HasBoard { get; set; }

    public bool HasNoBoard => !HasBoard;

    partial void OnSelectedDateChanged(DateTime value) => QueueReload();

    partial void OnSearchTermChanged(string value) => QueueReload();

    // ----------------------------------------------------------- timesheet

    [ObservableProperty]
    public partial EmployeeOption? SelectedEmployee { get; set; }

    [ObservableProperty]
    public partial DateTime PeriodStart { get; set; }

    [ObservableProperty]
    public partial DateTime PeriodEnd { get; set; }

    [ObservableProperty]
    public partial string TimesheetSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoTimesheet))]
    public partial bool HasTimesheet { get; set; }

    public bool HasNoTimesheet => !HasTimesheet;

    /// <summary>FR-027. The totals strip above the sheet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    public partial AttendanceSummary? Summary { get; set; }

    public bool HasSummary => Summary is not null;

    partial void OnSelectedEmployeeChanged(EmployeeOption? value) => QueueReload();

    partial void OnPeriodStartChanged(DateTime value) => QueueReload();

    partial void OnPeriodEndChanged(DateTime value) => QueueReload();

    // ------------------------------------------------------------ calendar

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HolidayYearDisplay))]
    public partial int HolidayYear { get; set; }

    public string HolidayYearDisplay => HolidayYear.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string HolidaySummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoHolidays))]
    public partial bool HasHolidays { get; set; }

    public bool HasNoHolidays => !HasHolidays;

    /// <summary>Retired entries are hidden until asked for, as elsewhere in the app.</summary>
    [ObservableProperty]
    public partial bool ShowRetired { get; set; }

    partial void OnShowRetiredChanged(bool value) => QueueReload();

    // --------------------------------------------------------- entry modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsEntryOpen { get; set; }

    [ObservableProperty]
    public partial string EntryTitle { get; set; }

    [ObservableProperty]
    public partial string EntrySubtitle { get; set; }

    /// <summary>
    /// Clears the punches without clearing the day. An absence, a rest day and a
    /// holiday are all "did not report"; which of them it is follows from the
    /// classification below, not from a separate choice.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryHasPunches))]
    [NotifyPropertyChangedFor(nameof(EntryPreview))]
    public partial bool EntryDidNotReport { get; set; }

    public bool EntryHasPunches => !EntryDidNotReport;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryPreview))]
    public partial TimeSpan EntryTimeIn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryPreview))]
    public partial TimeSpan EntryTimeOut { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryPreview))]
    public partial bool EntryHasBreakPunches { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryPreview))]
    public partial TimeSpan EntryBreakOut { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryPreview))]
    public partial TimeSpan EntryBreakIn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryPreview))]
    public partial bool EntryIsRestDay { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryPreview))]
    public partial HolidayTypeOption? EntryHolidayType { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryPreview))]
    public partial AttendanceStatusOption? EntryStatus { get; set; }

    [ObservableProperty]
    public partial string EntryRemarks { get; set; }

    /// <summary>True when the day already exists, so a reason is required (FR-024).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EntryRemarksLabel))]
    public partial bool EntryRequiresReason { get; set; }

    public string EntryRemarksLabel => EntryRequiresReason
        ? "REASON FOR THE CORRECTION"
        : "REMARKS (OPTIONAL)";

    [ObservableProperty]
    public partial string ErrEntryTimeIn { get; set; }

    [ObservableProperty]
    public partial string ErrEntryTimeOut { get; set; }

    [ObservableProperty]
    public partial string ErrEntryBreak { get; set; }

    [ObservableProperty]
    public partial string ErrEntryRemarks { get; set; }

    /// <summary>
    /// What the entered punches come to, recomputed as they are changed.
    ///
    /// <para>It runs the production calculator against the employee's own
    /// schedule, so this is not an approximation of the result — it is the
    /// result, shown before it is committed. NFR-023: the officer sees a
    /// mistyped time-out as fourteen hours of overtime rather than discovering
    /// it on a payslip.</para>
    /// </summary>
    public string EntryPreview
    {
        get
        {
            if (_entryTarget is null)
                return string.Empty;

            var draft = BuildEntryRecord();
            var schedule = ScheduleFor(_entryTarget.Employee);
            var computed = AttendanceCalculator.Compute(draft, schedule);

            if (EntryDidNotReport)
            {
                return $"No punches · {draft.DayTypeDisplay} · " +
                       $"recorded as {AttendanceEnumNames.Display(computed.Status)}";
            }

            var parts = new List<string>
            {
                $"{computed.RegularHours:0.##} h regular",
                $"of {computed.ScheduledHours:0.##} h scheduled"
            };

            if (computed.OvertimeHoursRendered > 0m)
                parts.Add($"{computed.OvertimeHoursRendered:0.##} h overtime rendered");

            if (computed.NightDifferentialHours > 0m)
                parts.Add($"{computed.NightDifferentialHours:0.##} h night differential");

            if (computed.LateMinutes > 0)
                parts.Add($"{computed.LateMinutes} min late");

            if (computed.UndertimeMinutes > 0)
                parts.Add($"{computed.UndertimeMinutes} min undertime");

            parts.Add(AttendanceEnumNames.Display(computed.Status));

            return string.Join("  ·  ", parts);
        }
    }

    // ------------------------------------------------------ approval modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsApprovalOpen { get; set; }

    [ObservableProperty]
    public partial string ApprovalTitle { get; set; }

    [ObservableProperty]
    public partial string ApprovalMessage { get; set; }

    [ObservableProperty]
    public partial string ApprovalHours { get; set; }

    [ObservableProperty]
    public partial string ErrApprovalHours { get; set; }

    // ------------------------------------------------------- holiday modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsHolidayFormOpen { get; set; }

    [ObservableProperty]
    public partial string HolidayFormTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitHolidayCommand))]
    public partial string HolidayName { get; set; }

    [ObservableProperty]
    public partial DateTime HolidayDate { get; set; }

    [ObservableProperty]
    public partial HolidayTypeOption? HolidayTypeChoice { get; set; }

    [ObservableProperty]
    public partial bool HolidayIsAnnual { get; set; }

    [ObservableProperty]
    public partial string HolidayRemarks { get; set; }

    [ObservableProperty]
    public partial string ErrHolidayName { get; set; }

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
        if (!await Session.RequireAsync(Permission.ManageAttendance, "open time and attendance"))
        {
            IsDenied = true;
            Board.Clear();
            Timesheet.Clear();
            Holidays.Clear();
            return;
        }

        IsDenied = false;
        _ready = true;

        await RunAsync(ReloadAsync);
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(ReloadAsync);

    /// <summary>
    /// Reloads in response to a filter change, unless the screen is still being
    /// constructed or several filters are being moved at once.
    /// </summary>
    private void QueueReload()
    {
        if (_ready && !_suppressReload)
            _ = RefreshAsync();
    }

    private async Task ReloadAsync()
    {
        ClearMessages();

        _schedules = (await _organization.GetWorkSchedulesAsync(includeInactive: true))
            .ToDictionary(s => s.Id);

        _roster = await _employees.SearchAsync(
            new EmployeeQuery(SearchTerm, Status: EmployeeStatusFilter.Active));

        RebuildEmployeeOptions();

        await ReloadBoardAsync();
        await ReloadTimesheetAsync();
        await ReloadHolidaysAsync();
    }

    /// <summary>
    /// The timesheet picker lists everyone, not just the search hits: the search
    /// box belongs to the daily board, and narrowing it should not make the
    /// employee whose sheet is open disappear from under the user.
    /// </summary>
    private void RebuildEmployeeOptions()
    {
        var current = SelectedEmployee?.Employee.Id;

        EmployeeOptions.Clear();
        foreach (var employee in _roster.OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase))
            EmployeeOptions.Add(new EmployeeOption(employee));

        SelectedEmployee = EmployeeOptions.FirstOrDefault(o => o.Employee.Id == current)
                           ?? EmployeeOptions.FirstOrDefault();
    }

    private async Task ReloadBoardAsync()
    {
        var day = SelectedDate.Date;

        var roster = _roster
            .Where(e => e.IsPayableOver(day, day))
            .OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var records = await _attendance.BuildBoardAsync(roster, day);
        var byEmployee = records.ToDictionary(r => r.EmployeeId);

        Board.Clear();
        foreach (var employee in roster)
        {
            if (byEmployee.TryGetValue(employee.Id, out var record))
                Board.Add(new AttendanceRow(employee, record));
        }

        HasBoard = Board.Count > 0;

        var recorded = Board.Count(r => r.IsRecorded);
        var pending = Board.Count(r => r.HasPendingOvertime);

        BoardSummary = Board.Count == 0
            ? "Nobody was employed on this date."
            : $"{recorded} of {Board.Count} recorded" +
              (pending > 0 ? $" · {pending} with overtime awaiting approval" : string.Empty);

        var calendar = await _holidays.GetCalendarAsync(day, day);
        var holiday = calendar.On(day);

        DayClassification = holiday is null
            ? day.ToString("dddd") + " · ordinary day"
            : $"{day:dddd} · {holiday.Name} — {holiday.TypeDisplay}";
    }

    private async Task ReloadTimesheetAsync()
    {
        Timesheet.Clear();
        Summary = null;
        HasTimesheet = false;
        TimesheetSummary = string.Empty;

        if (SelectedEmployee is not { } option)
            return;

        if (PeriodEnd.Date < PeriodStart.Date)
        {
            TimesheetSummary = "The end of the cut-off falls before its start.";
            return;
        }

        var sheet = await _attendance.BuildTimesheetAsync(option.Employee, PeriodStart, PeriodEnd);

        foreach (var record in sheet)
            Timesheet.Add(new AttendanceRow(option.Employee, record));

        HasTimesheet = Timesheet.Count > 0;

        Summary = AttendanceService.Summarise(option.Employee, PeriodStart, PeriodEnd, sheet);

        TimesheetSummary = Summary.IsIncomplete
            ? $"{Summary.DaysUnrecorded} day(s) in this cut-off have no entry yet."
            : $"{Summary.DaysRecorded} day(s) recorded.";
    }

    private async Task ReloadHolidaysAsync()
    {
        var all = await _holidays.GetForYearAsync(HolidayYear, includeInactive: true);
        var visible = all.Where(h => ShowRetired || h.IsActive).ToList();

        Holidays.Clear();
        foreach (var holiday in visible)
            Holidays.Add(new HolidayRow(holiday, HolidayYear));

        HasHolidays = Holidays.Count > 0;

        var retired = all.Count - visible.Count;
        var regular = visible.Count(h => h.Type == HolidayType.Regular);

        HolidaySummary = $"{visible.Count} in {HolidayYear} · {regular} regular" +
                         (retired > 0 ? $" · {retired} retired hidden" : string.Empty);
    }

    // ---------------------------------------------------------- navigation

    [RelayCommand]
    private void ShowDaily() => SwitchTo(AttendanceMode.Daily);

    [RelayCommand]
    private void ShowTimesheet() => SwitchTo(AttendanceMode.Timesheet);

    [RelayCommand]
    private void ShowHolidays() => SwitchTo(AttendanceMode.Holidays);

    private void SwitchTo(AttendanceMode mode)
    {
        Session.Touch();
        ClearMessages();
        Mode = mode;
    }

    [RelayCommand]
    private void PreviousDay() => SelectedDate = SelectedDate.AddDays(-1);

    [RelayCommand]
    private void NextDay() => SelectedDate = SelectedDate.AddDays(1);

    [RelayCommand]
    private void Today() => SelectedDate = DateTime.Today;

    [RelayCommand]
    private void PreviousYear() => SetHolidayYear(HolidayYear - 1);

    [RelayCommand]
    private void NextYear() => SetHolidayYear(HolidayYear + 1);

    private void SetHolidayYear(int year)
    {
        Session.Touch();
        HolidayYear = year;
        QueueReload();
    }

    /// <summary>A-02: the two semi-monthly cut-offs, one click each.</summary>
    [RelayCommand]
    private void FirstHalf()
    {
        var anchor = PeriodStart == default ? DateTime.Today : PeriodStart;

        SetPeriod(
            new DateTime(anchor.Year, anchor.Month, 1),
            new DateTime(anchor.Year, anchor.Month, 15));
    }

    [RelayCommand]
    private void SecondHalf()
    {
        var anchor = PeriodStart == default ? DateTime.Today : PeriodStart;

        SetPeriod(
            new DateTime(anchor.Year, anchor.Month, 16),
            new DateTime(anchor.Year, anchor.Month, DateTime.DaysInMonth(anchor.Year, anchor.Month)));
    }

    /// <summary>Moves both ends of the cut-off, then reloads once.</summary>
    private void SetPeriod(DateTime start, DateTime end)
    {
        Session.Touch();

        _suppressReload = true;
        PeriodStart = start;
        PeriodEnd = end;
        _suppressReload = false;

        QueueReload();
    }

    // --------------------------------------------------------- entry modal

    [RelayCommand]
    private void OpenEntry(AttendanceRow? row)
    {
        if (row is null)
            return;

        Session.Touch();
        ClearMessages();
        ResetEntryForm();
        ModalError = string.Empty;

        _entryTarget = row;

        var record = row.Record;
        var schedule = ScheduleFor(row.Employee);

        EntryTitle = row.IsDraft ? "Record the day" : "Correct the day";
        EntrySubtitle = $"{row.EmployeeName} · {record.DateDisplay}";
        EntryRequiresReason = row.IsRecorded;

        EntryDidNotReport = !record.HasPunches;

        // A fresh day opens on the employee's own shift rather than at midnight,
        // so the common case — they worked their schedule — is two clicks.
        EntryTimeIn = record.TimeInMinutes is { } inAt
            ? TimeSpan.FromMinutes(inAt)
            : TimeSpan.FromMinutes(schedule?.StartMinutes ?? 8 * 60);

        EntryTimeOut = record.TimeOutMinutes is { } outAt
            ? TimeSpan.FromMinutes(outAt)
            : TimeSpan.FromMinutes(schedule?.EndMinutes ?? 17 * 60);

        EntryHasBreakPunches = record.BreakOutMinutes.HasValue && record.BreakInMinutes.HasValue;
        EntryBreakOut = TimeSpan.FromMinutes(record.BreakOutMinutes ?? 12 * 60);
        EntryBreakIn = TimeSpan.FromMinutes(record.BreakInMinutes ?? 13 * 60);

        EntryIsRestDay = record.IsRestDay;
        EntryHolidayType = HolidayTypes.FirstOrDefault(t => t.Value == record.HolidayType)
                           ?? HolidayTypes[0];

        EntryStatus = StatusOptions.FirstOrDefault(s => s.Value == record.Status)
                      ?? StatusOptions[0];

        EntryRemarks = record.Source == AttendanceSource.Generated ? string.Empty : record.Remarks;

        if (record.IsLocked)
        {
            ModalError = "This day was locked by a posted payroll run and can no longer be edited.";
        }

        IsEntryOpen = true;
        OnPropertyChanged(nameof(EntryPreview));
    }

    [RelayCommand]
    private void CloseEntry()
    {
        IsEntryOpen = false;
        ModalError = string.Empty;
        _entryTarget = null;
    }

    [RelayCommand]
    private Task SubmitEntryAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrEntryTimeIn = string.Empty;
        ErrEntryTimeOut = string.Empty;
        ErrEntryBreak = string.Empty;
        ErrEntryRemarks = string.Empty;

        if (_entryTarget is null || Session.CurrentUser is not { } performedBy)
            return;

        var record = BuildEntryRecord();
        record.Id = _entryTarget.Record.Id;

        var result = await _attendance.SaveAsync(record, performedBy);

        if (!result.Succeeded)
        {
            ApplyEntryErrors(result);
            return;
        }

        IsEntryOpen = false;
        _entryTarget = null;

        await ReloadBoardAsync();
        await ReloadTimesheetAsync();

        ShowStatus(result.Message);
    });

    /// <summary>
    /// Puts each field message beside its input, and anything unattributed into
    /// the dialog's footer (NFR-023).
    /// </summary>
    private void ApplyEntryErrors(AttendanceSaveResult result)
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
                case nameof(AttendanceRecord.TimeInMinutes):
                    ErrEntryTimeIn = error.Message;
                    break;
                case nameof(AttendanceRecord.TimeOutMinutes):
                    ErrEntryTimeOut = error.Message;
                    break;
                case nameof(AttendanceRecord.BreakOutMinutes):
                case nameof(AttendanceRecord.BreakInMinutes):
                    ErrEntryBreak = error.Message;
                    break;
                case nameof(AttendanceRecord.Remarks):
                    ErrEntryRemarks = error.Message;
                    break;
                default:
                    ModalError = error.Message;
                    break;
            }
        }

        if (ModalError.Length == 0)
            ModalError = result.Message;
    }

    /// <summary>
    /// The form as a record. Used both by the live preview and by the save, so
    /// the two cannot drift apart.
    /// </summary>
    private AttendanceRecord BuildEntryRecord()
    {
        var source = _entryTarget?.Record;

        var record = new AttendanceRecord
        {
            EmployeeId = source?.EmployeeId ?? 0,
            Date = source?.Date ?? SelectedDate.Date,
            IsRestDay = EntryIsRestDay,
            HolidayType = EntryHolidayType?.Value ?? HolidayType.None,
            HolidayName = source?.HolidayName ?? string.Empty,
            Status = EntryStatus?.Value ?? AttendanceStatus.Present,
            Source = source?.Source ?? AttendanceSource.Manual,
            Remarks = EntryRemarks ?? string.Empty,
            OvertimeHoursApproved = source?.OvertimeHoursApproved ?? 0m
        };

        // A holiday cleared on the form must clear its name too, or the row
        // would go on claiming to be Christmas Day while paying nothing extra.
        if (record.HolidayType == HolidayType.None)
            record.HolidayName = string.Empty;

        if (!EntryDidNotReport)
        {
            record.TimeInMinutes = (int)EntryTimeIn.TotalMinutes;
            record.TimeOutMinutes = (int)EntryTimeOut.TotalMinutes;

            if (EntryHasBreakPunches)
            {
                record.BreakOutMinutes = (int)EntryBreakOut.TotalMinutes;
                record.BreakInMinutes = (int)EntryBreakIn.TotalMinutes;
            }
        }

        return record;
    }

    private void ResetEntryForm()
    {
        EntryDidNotReport = false;
        EntryTimeIn = TimeSpan.FromHours(8);
        EntryTimeOut = TimeSpan.FromHours(17);
        EntryHasBreakPunches = false;
        EntryBreakOut = TimeSpan.FromHours(12);
        EntryBreakIn = TimeSpan.FromHours(13);
        EntryIsRestDay = false;
        EntryHolidayType = HolidayTypes.Count > 0 ? HolidayTypes[0] : null;
        EntryStatus = StatusOptions.Count > 0 ? StatusOptions[0] : null;
        EntryRemarks = string.Empty;
        EntryRequiresReason = false;

        ErrEntryTimeIn = string.Empty;
        ErrEntryTimeOut = string.Empty;
        ErrEntryBreak = string.Empty;
        ErrEntryRemarks = string.Empty;
    }

    // ------------------------------------------------------ FR-022 approval

    [RelayCommand]
    private void OpenApproval(AttendanceRow? row)
    {
        if (row is null || row.IsDraft)
            return;

        Session.Touch();
        ClearMessages();

        _approvalTarget = row;

        ModalError = string.Empty;
        ApprovalTitle = "Approve overtime";
        ApprovalMessage =
            $"{row.EmployeeName} rendered {row.Record.OvertimeHoursRendered:0.##} hours of overtime on " +
            $"{row.Record.DateDisplay}. Only approved hours are paid — enter fewer to authorise part of it, " +
            "or zero to decline it entirely.";

        ApprovalHours = row.Record.OvertimeHoursRendered.ToString("0.##", CultureInfo.CurrentCulture);
        ErrApprovalHours = string.Empty;

        IsApprovalOpen = true;
    }

    [RelayCommand]
    private void CloseApproval()
    {
        IsApprovalOpen = false;
        ModalError = string.Empty;
        _approvalTarget = null;
    }

    [RelayCommand]
    private Task SubmitApprovalAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrApprovalHours = string.Empty;

        if (_approvalTarget is null || Session.CurrentUser is not { } performedBy)
            return;

        if (!decimal.TryParse(ApprovalHours, NumberStyles.Number, CultureInfo.CurrentCulture, out var hours))
        {
            ErrApprovalHours = "Enter the number of hours to approve.";
            return;
        }

        var result = await _attendance.SetOvertimeApprovedAsync(_approvalTarget.Id, hours, performedBy);

        if (!result.Succeeded)
        {
            ErrApprovalHours = result.Message;
            return;
        }

        IsApprovalOpen = false;
        _approvalTarget = null;

        await ReloadBoardAsync();
        await ReloadTimesheetAsync();

        ShowStatus(result.Message);
    });

    // -------------------------------------------------- generate / remove

    [RelayCommand]
    private void OpenGenerate()
    {
        Session.Touch();
        ClearMessages();

        _confirmTarget = ConfirmTarget.GenerateCutoff;

        var from = IsTimesheet ? PeriodStart : SelectedDate;
        var to = IsTimesheet ? PeriodEnd : SelectedDate;
        var who = IsTimesheet && SelectedEmployee is { } option
            ? option.Employee.DisplayName
            : $"all {_roster.Count} active employee(s)";

        ConfirmTitle = "Fill in the cut-off";
        ConfirmMessage =
            $"Create the missing days from {from:dd MMM yyyy} to {to:dd MMM yyyy} for {who}.\n\n" +
            "Rest days and holidays are classified from the schedule and the calendar. " +
            "Everything else is entered as an absence until punches are recorded against it, " +
            "so payroll can tell a genuine absence from a day nobody got round to entering. " +
            "Days already recorded are left alone.";
        ConfirmAction = "Fill in";
        IsConfirmDestructive = false;

        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void OpenRemove(AttendanceRow? row)
    {
        if (row is null || row.IsDraft)
            return;

        Session.Touch();
        ClearMessages();

        _entryTarget = row;
        _confirmTarget = ConfirmTarget.RemoveEntry;

        ConfirmTitle = "Remove this entry";
        ConfirmMessage =
            $"Remove the {row.Record.DateDisplay} entry for {row.EmployeeName}?\n\n" +
            "Unlike an employee or a department, an attendance day carries no history of its own — " +
            "it is the history — so this is a real deletion. The audit log keeps what was removed.";
        ConfirmAction = "Remove";
        IsConfirmDestructive = true;

        IsConfirmOpen = true;
    }

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

        if (Session.CurrentUser is not { } performedBy)
            return;

        switch (_confirmTarget)
        {
            case ConfirmTarget.GenerateCutoff:
            {
                var from = IsTimesheet ? PeriodStart : SelectedDate;
                var to = IsTimesheet ? PeriodEnd : SelectedDate;

                var who = IsTimesheet && SelectedEmployee is { } option
                    ? new[] { option.Employee }
                    : _roster.ToArray();

                var result = await _attendance.GeneratePeriodAsync(who, from, to, performedBy);

                if (!result.Succeeded)
                {
                    ModalError = result.Message;
                    return;
                }

                IsConfirmOpen = false;
                await ReloadBoardAsync();
                await ReloadTimesheetAsync();
                ShowStatus(result.Message);
                break;
            }

            case ConfirmTarget.RemoveEntry:
            {
                if (_entryTarget is null)
                    return;

                var result = await _attendance.DeleteAsync(_entryTarget.Id, performedBy);

                if (!result.Succeeded)
                {
                    ModalError = result.Message;
                    return;
                }

                IsConfirmOpen = false;
                _entryTarget = null;

                await ReloadBoardAsync();
                await ReloadTimesheetAsync();
                ShowStatus(result.Message);
                break;
            }

            case ConfirmTarget.ToggleHoliday:
            {
                if (_holidayTarget is null)
                    return;

                var result = await _holidays.SetActiveAsync(
                    _holidayTarget.Id, !_holidayTarget.IsActive, performedBy);

                if (!result.Succeeded)
                {
                    ModalError = result.Message;
                    return;
                }

                IsConfirmOpen = false;
                _holidayTarget = null;

                await ReloadHolidaysAsync();
                await ReloadBoardAsync();
                ShowStatus(result.Message);
                break;
            }
        }
    });

    // -------------------------------------------------- FR-025 calendar

    [RelayCommand]
    private void OpenCreateHoliday()
    {
        Session.Touch();
        ClearMessages();
        ResetHolidayForm();

        _holidayTarget = null;
        ModalError = string.Empty;
        HolidayFormTitle = "Add a holiday";
        HolidayDate = new DateTime(HolidayYear, DateTime.Today.Month, DateTime.Today.Day);

        IsHolidayFormOpen = true;
    }

    [RelayCommand]
    private void OpenEditHoliday(HolidayRow? row)
    {
        if (row is null)
            return;

        Session.Touch();
        ClearMessages();
        ResetHolidayForm();

        _holidayTarget = row.Holiday;

        ModalError = string.Empty;
        HolidayFormTitle = $"Edit {row.Name}";
        HolidayName = row.Holiday.Name;
        HolidayDate = row.Holiday.Date;
        HolidayIsAnnual = row.Holiday.IsAnnual;
        HolidayRemarks = row.Holiday.Remarks;
        HolidayTypeChoice = HolidayTypes.FirstOrDefault(t => t.Value == row.Holiday.Type)
                            ?? HolidayTypes[1];

        IsHolidayFormOpen = true;
    }

    [RelayCommand]
    private void CloseHolidayForm()
    {
        IsHolidayFormOpen = false;
        ModalError = string.Empty;
        _holidayTarget = null;
    }

    private bool CanSubmitHoliday() => !string.IsNullOrWhiteSpace(HolidayName);

    [RelayCommand(CanExecute = nameof(CanSubmitHoliday))]
    private Task SubmitHolidayAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrHolidayName = string.Empty;

        if (Session.CurrentUser is not { } performedBy)
            return;

        var holiday = new Holiday
        {
            Id = _holidayTarget?.Id ?? 0,
            Date = HolidayDate.Date,
            Name = HolidayName,
            Type = HolidayTypeChoice?.Value ?? HolidayType.Regular,
            IsAnnual = HolidayIsAnnual,
            Remarks = HolidayRemarks
        };

        var result = await _holidays.SaveAsync(holiday, performedBy);

        if (!result.Succeeded)
        {
            if (result.Message.Contains("already on the calendar", StringComparison.OrdinalIgnoreCase))
                ErrHolidayName = result.Message;
            else
                ModalError = result.Message;

            return;
        }

        IsHolidayFormOpen = false;
        _holidayTarget = null;

        await ReloadHolidaysAsync();

        // A new holiday changes what the open date is worth, so the board is
        // reread rather than left showing the old classification.
        await ReloadBoardAsync();

        ShowStatus(result.Message);
    });

    [RelayCommand]
    private void OpenToggleHoliday(HolidayRow? row)
    {
        if (row is null)
            return;

        Session.Touch();
        ClearMessages();

        _holidayTarget = row.Holiday;
        _confirmTarget = ConfirmTarget.ToggleHoliday;

        var retiring = row.Holiday.IsActive;

        ConfirmTitle = retiring ? $"Retire {row.Name}" : $"Restore {row.Name}";
        ConfirmMessage = retiring
            ? $"{row.Name} will stop classifying new attendance days.\n\n" +
              "Days already computed against it keep the classification they were given, because a " +
              "payslip already produced must stay reproducible. Regenerate the cut-off if they should " +
              "follow the new calendar."
            : $"{row.Name} will classify attendance days again from now on.";
        ConfirmAction = retiring ? "Retire" : "Restore";
        IsConfirmDestructive = retiring;

        IsConfirmOpen = true;
    }

    private void ResetHolidayForm()
    {
        HolidayName = string.Empty;
        HolidayDate = new DateTime(HolidayYear, 1, 1);
        HolidayIsAnnual = false;
        HolidayRemarks = string.Empty;
        HolidayTypeChoice = HolidayTypes.Count > 1 ? HolidayTypes[1] : HolidayTypes.FirstOrDefault();
        ErrHolidayName = string.Empty;
    }

    // ----------------------------------------------------------- internals

    private WorkSchedule? ScheduleFor(Employee employee) =>
        employee.WorkScheduleId is { } id && _schedules.TryGetValue(id, out var schedule)
            ? schedule
            : null;
}
