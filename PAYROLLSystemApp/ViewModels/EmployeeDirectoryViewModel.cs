using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Security;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>
/// A picker entry. <see cref="Id"/> is null for the "none" and "all" choices,
/// so an unset foreign key and an unfiltered list use the same option type.
/// </summary>
public sealed record LookupOption(int? Id, string Label);

/// <summary>
/// One choice in an enumeration picker.
///
/// <para>Deliberately not generic. A generic option type has to be written in
/// XAML as <c>EnumOption(models:Gender)</c> at every binding, and as a nested
/// type it needs a <c>+</c> as well — so the picker markup becomes the hardest
/// thing on the screen to read. Holding the value as its underlying <c>int</c>
/// keeps every picker binding identical; the view model casts it back.</para>
/// </summary>
public sealed record EnumOption(int Value, string Label)
{
    public static IReadOnlyList<EnumOption> From<T>(Func<T, string> label) where T : struct, Enum =>
        Enum.GetValues<T>()
            .Select(v => new EnumOption(Convert.ToInt32(v), label(v)))
            .ToList();

    public T As<T>() where T : struct, Enum => (T)Enum.ToObject(typeof(T), Value);
}

/// <summary>An employee as the list renders them, with the reference data resolved.</summary>
public sealed class EmployeeRow
{
    public EmployeeRow(Employee employee, string departmentName, string positionTitle, string scheduleName)
    {
        Employee = employee;
        DepartmentName = departmentName;
        PositionTitle = positionTitle;
        ScheduleName = scheduleName;
    }

    public Employee Employee { get; }

    public string DepartmentName { get; }

    public string PositionTitle { get; }

    public string ScheduleName { get; }

    public int Id => Employee.Id;

    public string EmployeeNumber => Employee.EmployeeNumber;

    public string FullName => Employee.FullName;

    public string Initials => Employee.Initials;

    public string StatusDisplay => Employee.StatusDisplay;

    public string RateDisplay => Employee.RateDisplay;

    public bool IsSeparated => Employee.IsSeparated;

    public bool IsNotSeparated => !Employee.IsSeparated;

    public string EmploymentStatusDisplay => Employee.EmploymentStatusDisplay;

    public string HiredDisplay => $"hired {Employee.HireDate:dd MMM yyyy}";

    public string PostingDisplay =>
        string.IsNullOrEmpty(PositionTitle) ? DepartmentName : $"{PositionTitle} · {DepartmentName}";

    public bool IsArchived => Employee.IsArchived;

    /// <summary>
    /// "Archive" for a live record, "Restore" for one already archived. One
    /// button rather than two, because only ever one of them applies.
    /// </summary>
    public string ArchiveActionText => Employee.IsArchived ? "Restore" : "Archive";
}

/// <summary>
/// Section 2.2 of the requirements: the employee directory (FR-010 – FR-018).
///
/// <para>The list is loaded once and filtered in memory, so typing in the
/// search box does not read the table on every keystroke (NFR-003). The filter
/// itself is <see cref="EmployeeService.Filter"/>, shared with the service, so
/// this screen and any later report agree on who counts as active.</para>
///
/// <para>Add, edit, separate and reinstate each open a modal over the list, the
/// same pattern user management uses, so the surrounding context stays visible
/// and anything destructive is a deliberate second step (NFR-022).</para>
/// </summary>
public sealed partial class EmployeeDirectoryViewModel : BaseViewModel
{
    private readonly IEmployeeService _employees;
    private readonly IOrganizationService _organization;

    private List<Employee> _all = new();
    private IReadOnlyList<Department> _departments = Array.Empty<Department>();
    private IReadOnlyList<Position> _positions = Array.Empty<Position>();
    private IReadOnlyList<WorkSchedule> _schedules = Array.Empty<WorkSchedule>();
    private IReadOnlyList<Detachment> _detachments = Array.Empty<Detachment>();

    /// <summary>The record the open modal is acting on. Null while creating.</summary>
    private Employee? _target;

    private readonly IDetachmentService _detachmentService;

    public EmployeeDirectoryViewModel(
        IEmployeeService employees,
        IOrganizationService organization,
        IDetachmentService detachments,
        ISessionService session)
        : base(session)
    {
        _employees = employees;
        _organization = organization;
        _detachmentService = detachments;

        Title = "Employees";

        StatusOptions =
        [
            new EnumOption((int)EmployeeStatusFilter.Active, "Active only"),
            new EnumOption((int)EmployeeStatusFilter.Separated, "Separated / inactive"),
            new EnumOption((int)EmployeeStatusFilter.All, "All employees"),
            new EnumOption((int)EmployeeStatusFilter.Archived, "Archived")
        ];

        GenderOptions = EnumOption.From<Gender>(EmployeeEnumNames.Display);
        CivilStatusOptions = EnumOption.From<CivilStatus>(EmployeeEnumNames.Display);
        EmploymentStatusOptions = EnumOption.From<EmploymentStatus>(EmployeeEnumNames.Display);
        PayTypeOptions = EnumOption.From<PayType>(EmployeeEnumNames.Display);
        PayFrequencyOptions = EnumOption.From<PayFrequency>(EmployeeEnumNames.Display);

        SelectedStatus = StatusOptions[0];
        SearchText = string.Empty;
        ResultSummary = string.Empty;
        HeadcountSummary = string.Empty;

        ModalError = string.Empty;
        FormTitle = string.Empty;
        FormSubtitle = string.Empty;
        SeparateSubject = string.Empty;
        SeparateReason = string.Empty;
        SeparateDate = DateTime.Today;
        ReinstateMessage = string.Empty;
        HistorySubject = string.Empty;
        HistoryEmptyMessage = string.Empty;

        ResetForm();
    }

    /// <summary>Drives the dialog layer; while it is false the layer must not be hit-testable.</summary>
    public bool IsAnyModalOpen =>
        IsFormOpen || IsSeparateOpen || IsReinstateOpen || IsHistoryOpen || IsArchiveOpen;

    public ObservableCollection<EmployeeRow> Employees { get; } = new();

    public ObservableCollection<SalaryRateHistory> History { get; } = new();

    public ObservableCollection<LookupOption> DepartmentFilterOptions { get; } = new();

    public ObservableCollection<LookupOption> DepartmentOptions { get; } = new();

    public ObservableCollection<LookupOption> PositionOptions { get; } = new();

    public ObservableCollection<LookupOption> ScheduleOptions { get; } = new();

    /// <summary>FR-013. The post an employee is deployed to, and paid by.</summary>
    public ObservableCollection<LookupOption> DetachmentOptions { get; } = new();

    public ObservableCollection<LookupOption> SupervisorOptions { get; } = new();

    public IReadOnlyList<EnumOption> StatusOptions { get; }

    public IReadOnlyList<EnumOption> GenderOptions { get; }

    public IReadOnlyList<EnumOption> CivilStatusOptions { get; }

    public IReadOnlyList<EnumOption> EmploymentStatusOptions { get; }

    public IReadOnlyList<EnumOption> PayTypeOptions { get; }

    public IReadOnlyList<EnumOption> PayFrequencyOptions { get; }

    // ------------------------------------------------------------ list state

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial string ResultSummary { get; set; }

    [ObservableProperty]
    public partial string HeadcountSummary { get; set; }

    [ObservableProperty]
    public partial LookupOption? SelectedDepartmentFilter { get; set; }

    [ObservableProperty]
    public partial EnumOption? SelectedStatus { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoResults))]
    public partial bool HasResults { get; set; }

    public bool IsGranted => !IsDenied;

    public bool HasNoResults => !HasResults;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedDepartmentFilterChanged(LookupOption? value) => ApplyFilter();

    partial void OnSelectedStatusChanged(EnumOption? value) => ApplyFilter();

    // ------------------------------------------------------- employee form

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsFormOpen { get; set; }

    [ObservableProperty]
    public partial string FormTitle { get; set; }

    [ObservableProperty]
    public partial string FormSubtitle { get; set; }

    [ObservableProperty]
    public partial string ModalError { get; set; }

    // Identity and personal details (FR-011)
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitFormCommand))]
    public partial string FormEmployeeNumber { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitFormCommand))]
    public partial string FormLastName { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitFormCommand))]
    public partial string FormFirstName { get; set; }

    [ObservableProperty]
    public partial string FormMiddleName { get; set; }

    [ObservableProperty]
    public partial string FormSuffix { get; set; }

    [ObservableProperty]
    public partial bool FormHasBirthDate { get; set; }

    [ObservableProperty]
    public partial DateTime FormBirthDate { get; set; }

    [ObservableProperty]
    public partial EnumOption? FormGender { get; set; }

    [ObservableProperty]
    public partial EnumOption? FormCivilStatus { get; set; }

    [ObservableProperty]
    public partial string FormContactNumber { get; set; }

    [ObservableProperty]
    public partial string FormEmail { get; set; }

    [ObservableProperty]
    public partial string FormAddress { get; set; }

    [ObservableProperty]
    public partial string FormEmergencyName { get; set; }

    [ObservableProperty]
    public partial string FormEmergencyNumber { get; set; }

    // Employment (FR-012)
    [ObservableProperty]
    public partial DateTime FormHireDate { get; set; }

    [ObservableProperty]
    public partial bool FormIsRegularized { get; set; }

    [ObservableProperty]
    public partial DateTime FormRegularizationDate { get; set; }

    [ObservableProperty]
    public partial EnumOption? FormEmploymentStatus { get; set; }

    [ObservableProperty]
    public partial LookupOption? FormDepartment { get; set; }

    [ObservableProperty]
    public partial LookupOption? FormPosition { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateSourceHint))]
    public partial LookupOption? FormDetachment { get; set; }

    /// <summary>
    /// FR-013. Whether this employee is paid their own basic rate rather than
    /// the rate posted for their position at their detachment.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RateSourceHint))]
    [NotifyPropertyChangedFor(nameof(IsOwnRateEditable))]
    public partial bool FormUsesOwnRate { get; set; }

    /// <summary>
    /// The rate box is only meaningful where the employee is on their own rate.
    /// Left enabled but explained rather than greyed out, because it is still
    /// what they fall back to if the detachment has no posted rate.
    /// </summary>
    public bool IsOwnRateEditable => FormUsesOwnRate || FormDetachment?.Id is null;

    public string RateSourceHint =>
        FormDetachment?.Id is null
            ? "No detachment, so this employee is paid the basic rate below."
            : FormUsesOwnRate
                ? "On their own rate: the basic rate below is used, not the detachment's posted rate."
                : "Paid the daily rate posted for their position at this detachment, as at each run's " +
                  "pay date. The basic rate below is only a fallback if no rate is posted.";

    [ObservableProperty]
    public partial LookupOption? FormSupervisor { get; set; }

    [ObservableProperty]
    public partial LookupOption? FormSchedule { get; set; }

    // Compensation (FR-013)
    [ObservableProperty]
    public partial EnumOption? FormPayType { get; set; }

    [ObservableProperty]
    public partial string FormBasicRate { get; set; }

    [ObservableProperty]
    public partial EnumOption? FormPayFrequency { get; set; }

    [ObservableProperty]
    public partial string FormAllowance { get; set; }

    [ObservableProperty]
    public partial bool FormIsMinimumWageEarner { get; set; }

    /// <summary>
    /// Shown only when editing, and only once the rate has actually been
    /// changed — a reason is what makes the history row worth reading (FR-018).
    /// </summary>
    [ObservableProperty]
    public partial string FormRateReason { get; set; }

    [ObservableProperty]
    public partial bool ShowRateReason { get; set; }

    // Government identifiers (FR-014)
    [ObservableProperty]
    public partial string FormSss { get; set; }

    [ObservableProperty]
    public partial string FormPhilHealth { get; set; }

    [ObservableProperty]
    public partial string FormPagIbig { get; set; }

    [ObservableProperty]
    public partial string FormTin { get; set; }

    [ObservableProperty]
    public partial bool FormExemptSss { get; set; }

    [ObservableProperty]
    public partial bool FormExemptPhilHealth { get; set; }

    [ObservableProperty]
    public partial bool FormExemptPagIbig { get; set; }

    [ObservableProperty]
    public partial string FormBankName { get; set; }

    [ObservableProperty]
    public partial string FormBankAccount { get; set; }

    [ObservableProperty]
    public partial bool FormIsActive { get; set; }

    // Per-field messages, placed beside the input that caused them (NFR-023).
    [ObservableProperty]
    public partial string ErrEmployeeNumber { get; set; }

    [ObservableProperty]
    public partial string ErrLastName { get; set; }

    [ObservableProperty]
    public partial string ErrFirstName { get; set; }

    [ObservableProperty]
    public partial string ErrEmail { get; set; }

    [ObservableProperty]
    public partial string ErrBirthDate { get; set; }

    [ObservableProperty]
    public partial string ErrHireDate { get; set; }

    [ObservableProperty]
    public partial string ErrRegularization { get; set; }

    [ObservableProperty]
    public partial string ErrBasicRate { get; set; }

    [ObservableProperty]
    public partial string ErrAllowance { get; set; }

    [ObservableProperty]
    public partial string ErrSss { get; set; }

    [ObservableProperty]
    public partial string ErrPhilHealth { get; set; }

    [ObservableProperty]
    public partial string ErrPagIbig { get; set; }

    [ObservableProperty]
    public partial string ErrTin { get; set; }

    public string SssHint => GovernmentId.Mask(GovernmentIdKind.Sss);

    public string PhilHealthHint => GovernmentId.Mask(GovernmentIdKind.PhilHealth);

    public string PagIbigHint => GovernmentId.Mask(GovernmentIdKind.PagIbig);

    public string TinHint => GovernmentId.Mask(GovernmentIdKind.Tin);

    partial void OnFormBasicRateChanged(string value) => EvaluateRateChange();

    partial void OnFormPayTypeChanged(EnumOption? value) => EvaluateRateChange();

    partial void OnFormPayFrequencyChanged(EnumOption? value) => EvaluateRateChange();

    // -------------------------------------------------------- separate modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsSeparateOpen { get; set; }

    [ObservableProperty]
    public partial string SeparateSubject { get; set; }

    [ObservableProperty]
    public partial DateTime SeparateDate { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitSeparateCommand))]
    public partial string SeparateReason { get; set; }

    // ------------------------------------------------------- reinstate modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsReinstateOpen { get; set; }

    [ObservableProperty]
    public partial string ReinstateMessage { get; set; }

    // --------------------------------------------------------- history modal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsHistoryOpen { get; set; }

    [ObservableProperty]
    public partial string HistorySubject { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHistory))]
    [NotifyPropertyChangedFor(nameof(HasNoHistory))]
    public partial string HistoryEmptyMessage { get; set; }

    public bool HasHistory => string.IsNullOrEmpty(HistoryEmptyMessage);

    public bool HasNoHistory => !HasHistory;

    // -------------------------------------------------------------- loading

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.ManageEmployees, "open the employee directory"))
        {
            IsDenied = true;
            Employees.Clear();
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

        _departments = await _organization.GetDepartmentsAsync();
        _positions = await _organization.GetPositionsAsync();
        _schedules = await _organization.GetWorkSchedulesAsync();
        _detachments = await _detachmentService.GetAllAsync();
        _all = (await _employees.GetAllAsync()).ToList();

        RebuildLookups();
        ApplyFilter();

        var stats = await _employees.GetStatisticsAsync();

        HeadcountSummary =
            $"{stats.Active} active · {stats.Probationary} on probation · {stats.Separated} separated";

        // A probationary period that has run past six months without a decision
        // is the one thing on this screen that needs acting on, so it is said
        // rather than left to be noticed.
        if (stats.DueForRegularization > 0)
        {
            ShowStatus($"{stats.DueForRegularization} employee(s) have passed six months on probation " +
                       "and need a regularisation decision.");
        }
    }

    private void RebuildLookups()
    {
        DepartmentFilterOptions.Clear();
        DepartmentFilterOptions.Add(new LookupOption(null, "All departments"));

        DepartmentOptions.Clear();
        DepartmentOptions.Add(new LookupOption(null, "— Unassigned —"));

        foreach (var department in _departments)
        {
            DepartmentFilterOptions.Add(new LookupOption(department.Id, department.Name));
            DepartmentOptions.Add(new LookupOption(department.Id, department.Display));
        }

        PositionOptions.Clear();
        PositionOptions.Add(new LookupOption(null, "— Unassigned —"));

        foreach (var position in _positions)
        {
            // The managerial consequence is named in the picker, because it
            // removes overtime and premium pay from whoever is given the post.
            var label = position.IsManagerial
                ? $"{position.Title}  (managerial — no OT)"
                : position.Title;

            PositionOptions.Add(new LookupOption(position.Id, label));
        }

        DetachmentOptions.Clear();
        DetachmentOptions.Add(new LookupOption(null, "— Head office (own rate) —"));

        foreach (var detachment in _detachments)
        {
            DetachmentOptions.Add(new LookupOption(detachment.Id,
                $"{detachment.Display}  ({LuzonRegionNames.Short(detachment.Region)})"));
        }

        ScheduleOptions.Clear();
        ScheduleOptions.Add(new LookupOption(null, "— Unassigned —"));

        foreach (var schedule in _schedules)
            ScheduleOptions.Add(new LookupOption(schedule.Id, schedule.Display));

        SelectedDepartmentFilter ??= DepartmentFilterOptions[0];
    }

    /// <summary>
    /// Supervisors are the active employees other than the one being edited —
    /// a person cannot report to themselves, and offering it invites the typo.
    /// </summary>
    private void RebuildSupervisorOptions(int excludeId)
    {
        SupervisorOptions.Clear();
        SupervisorOptions.Add(new LookupOption(null, "— None —"));

        foreach (var candidate in _all
            .Where(e => e.IsActive && !e.IsSeparated && e.Id != excludeId)
            .OrderBy(e => e.LastName, StringComparer.CurrentCultureIgnoreCase))
        {
            SupervisorOptions.Add(new LookupOption(candidate.Id,
                $"{candidate.FullName} ({candidate.EmployeeNumber})"));
        }
    }

    private void ApplyFilter()
    {
        var query = new EmployeeQuery(
            Term: SearchText,
            DepartmentId: SelectedDepartmentFilter?.Id,
            Status: SelectedStatus?.As<EmployeeStatusFilter>() ?? EmployeeStatusFilter.Active);

        var filtered = EmployeeService.Filter(_all, query);

        Employees.Clear();
        foreach (var employee in filtered)
            Employees.Add(ToRow(employee));

        HasResults = filtered.Count > 0;

        ResultSummary = filtered.Count == _all.Count
            ? $"{_all.Count} employee(s)"
            : $"{filtered.Count} of {_all.Count} employee(s)";
    }

    private EmployeeRow ToRow(Employee employee) => new(
        employee,
        _departments.FirstOrDefault(d => d.Id == employee.DepartmentId)?.Name ?? "No department",
        _positions.FirstOrDefault(p => p.Id == employee.PositionId)?.Title ?? string.Empty,
        _schedules.FirstOrDefault(s => s.Id == employee.WorkScheduleId)?.Name ?? "No schedule");

    // --------------------------------------------------------------- create

    [RelayCommand]
    private Task OpenCreateAsync() => RunAsync(async () =>
    {
        ClearMessages();
        ResetForm();

        _target = null;
        FormTitle = "New employee";
        FormSubtitle = "Section 2.2 — employee masterfile";
        FormEmployeeNumber = await _employees.SuggestEmployeeNumberAsync();

        RebuildSupervisorOptions(0);
        IsFormOpen = true;
    });

    // ----------------------------------------------------------------- edit

    [RelayCommand]
    private void OpenEdit(EmployeeRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ResetForm();

        var employee = row.Employee;
        _target = employee;

        FormTitle = "Edit employee";
        FormSubtitle = $"{employee.EmployeeNumber} — {employee.FullName}";

        FormEmployeeNumber = employee.EmployeeNumber;
        FormLastName = employee.LastName;
        FormFirstName = employee.FirstName;
        FormMiddleName = employee.MiddleName;
        FormSuffix = employee.Suffix;

        FormHasBirthDate = employee.BirthDate.HasValue;
        FormBirthDate = employee.BirthDate ?? DefaultBirthDate;

        FormGender = Choice(GenderOptions, employee.Gender);
        FormCivilStatus = Choice(CivilStatusOptions, employee.CivilStatus);

        FormContactNumber = employee.ContactNumber;
        FormEmail = employee.Email;
        FormAddress = employee.Address;
        FormEmergencyName = employee.EmergencyContactName;
        FormEmergencyNumber = employee.EmergencyContactNumber;

        FormHireDate = employee.HireDate;
        FormIsRegularized = employee.RegularizationDate.HasValue;
        FormRegularizationDate = employee.RegularizationDate ?? DateTime.Today;
        FormEmploymentStatus = Choice(EmploymentStatusOptions, employee.EmploymentStatus);

        FormDepartment = Option(DepartmentOptions, employee.DepartmentId);
        FormPosition = Option(PositionOptions, employee.PositionId);
        FormDetachment = Option(DetachmentOptions, employee.DetachmentId);
        FormUsesOwnRate = employee.UsesOwnRate;
        FormSchedule = Option(ScheduleOptions, employee.WorkScheduleId);

        RebuildSupervisorOptions(employee.Id);
        FormSupervisor = Option(SupervisorOptions, employee.SupervisorId);

        FormPayType = Choice(PayTypeOptions, employee.PayType);
        FormPayFrequency = Choice(PayFrequencyOptions, employee.PayFrequency);
        FormBasicRate = employee.BasicRate.ToString("0.00");
        FormAllowance = employee.MonthlyAllowance.ToString("0.00");
        FormIsMinimumWageEarner = employee.IsMinimumWageEarner;

        // Identifiers are shown formatted for reading, and normalised back to
        // digits on save. See GovernmentId.
        FormSss = GovernmentId.Format(GovernmentIdKind.Sss, employee.SssNumber);
        FormPhilHealth = GovernmentId.Format(GovernmentIdKind.PhilHealth, employee.PhilHealthNumber);
        FormPagIbig = GovernmentId.Format(GovernmentIdKind.PagIbig, employee.PagIbigNumber);
        FormTin = GovernmentId.Format(GovernmentIdKind.Tin, employee.Tin);

        FormExemptSss = employee.ExemptFromSss;
        FormExemptPhilHealth = employee.ExemptFromPhilHealth;
        FormExemptPagIbig = employee.ExemptFromPagIbig;

        FormBankName = employee.BankName;
        FormBankAccount = employee.BankAccountNumber;
        FormIsActive = employee.IsActive;

        ShowRateReason = false;
        IsFormOpen = true;
    }

    [RelayCommand]
    private void CloseForm()
    {
        IsFormOpen = false;
        ModalError = string.Empty;
        _target = null;
    }

    private bool CanSubmitForm() =>
        !string.IsNullOrWhiteSpace(FormEmployeeNumber) &&
        !string.IsNullOrWhiteSpace(FormLastName) &&
        !string.IsNullOrWhiteSpace(FormFirstName);

    [RelayCommand(CanExecute = nameof(CanSubmitForm))]
    private Task SubmitFormAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ClearFieldErrors();

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        if (!TryParseMoney(FormBasicRate, out var basicRate))
        {
            ErrBasicRate = "Enter the rate as a number, e.g. 25000.00";
            ModalError = "Correct the highlighted fields and try again.";
            return;
        }

        if (!TryParseMoney(FormAllowance, out var allowance))
        {
            ErrAllowance = "Enter the allowance as a number, e.g. 2000.00";
            ModalError = "Correct the highlighted fields and try again.";
            return;
        }

        // Edits are applied to a copy, so a rejected save leaves the record in
        // the list exactly as it was rather than half-changed in memory.
        var employee = _target is null ? new Employee() : Clone(_target);

        employee.EmployeeNumber = FormEmployeeNumber;
        employee.LastName = FormLastName;
        employee.FirstName = FormFirstName;
        employee.MiddleName = FormMiddleName;
        employee.Suffix = FormSuffix;
        employee.BirthDate = FormHasBirthDate ? FormBirthDate : null;
        employee.Gender = FormGender?.As<Gender>() ?? Gender.Unspecified;
        employee.CivilStatus = FormCivilStatus?.As<CivilStatus>() ?? CivilStatus.Single;
        employee.ContactNumber = FormContactNumber;
        employee.Email = FormEmail;
        employee.Address = FormAddress;
        employee.EmergencyContactName = FormEmergencyName;
        employee.EmergencyContactNumber = FormEmergencyNumber;

        employee.HireDate = FormHireDate;
        employee.RegularizationDate = FormIsRegularized ? FormRegularizationDate : null;
        employee.EmploymentStatus = FormEmploymentStatus?.As<EmploymentStatus>() ?? EmploymentStatus.Probationary;
        employee.DepartmentId = FormDepartment?.Id;
        employee.PositionId = FormPosition?.Id;
        employee.DetachmentId = FormDetachment?.Id;

        // An employee with no post has nowhere to take a posted rate from, so
        // they are on their own rate whatever the switch says.
        employee.UsesOwnRate = FormUsesOwnRate || FormDetachment?.Id is null;
        employee.SupervisorId = FormSupervisor?.Id;
        employee.WorkScheduleId = FormSchedule?.Id;

        employee.PayType = FormPayType?.As<PayType>() ?? PayType.Monthly;
        employee.BasicRate = basicRate;
        employee.PayFrequency = FormPayFrequency?.As<PayFrequency>() ?? PayFrequency.SemiMonthly;
        employee.MonthlyAllowance = allowance;
        employee.IsMinimumWageEarner = FormIsMinimumWageEarner;

        employee.SssNumber = FormSss;
        employee.PhilHealthNumber = FormPhilHealth;
        employee.PagIbigNumber = FormPagIbig;
        employee.Tin = FormTin;
        employee.ExemptFromSss = FormExemptSss;
        employee.ExemptFromPhilHealth = FormExemptPhilHealth;
        employee.ExemptFromPagIbig = FormExemptPagIbig;

        employee.BankName = FormBankName;
        employee.BankAccountNumber = FormBankAccount;
        employee.IsActive = _target is null || FormIsActive;

        var result = await _employees.SaveAsync(employee, performedBy, FormRateReason);

        if (!result.Succeeded)
        {
            ApplyFieldErrors(result.FieldErrors);
            ModalError = result.Message;
            return;
        }

        IsFormOpen = false;
        _target = null;

        ShowStatus(result.Message);
        await ReloadAsync();
    });

    // ------------------------------------------------------------- separate

    [RelayCommand]
    private void OpenSeparate(EmployeeRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ModalError = string.Empty;

        _target = row.Employee;
        SeparateSubject = $"{row.EmployeeNumber} — {row.FullName}";
        SeparateDate = DateTime.Today;
        SeparateReason = string.Empty;

        IsSeparateOpen = true;
    }

    [RelayCommand]
    private void CloseSeparate()
    {
        IsSeparateOpen = false;
        ModalError = string.Empty;
        _target = null;
    }

    private bool CanSubmitSeparate() => !string.IsNullOrWhiteSpace(SeparateReason);

    [RelayCommand(CanExecute = nameof(CanSubmitSeparate))]
    private Task SubmitSeparateAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null || _target is null)
            return;

        var result = await _employees.SeparateAsync(_target.Id, SeparateDate, SeparateReason, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.FieldErrors.Count > 0
                ? string.Join(" ", result.FieldErrors.Select(e => e.Message))
                : result.Message;
            return;
        }

        IsSeparateOpen = false;
        _target = null;

        ShowStatus(result.Message);
        await ReloadAsync();
    });

    // -------------------------------------------------------------- archive

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsArchiveOpen { get; set; }

    [ObservableProperty]
    public partial string ArchiveTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ArchiveMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ArchiveAction { get; set; } = "Archive";

    [ObservableProperty]
    public partial string ArchiveReason { get; set; } = string.Empty;

    /// <summary>The reason box is only asked for when archiving, not restoring.</summary>
    [ObservableProperty]
    public partial bool ArchiveNeedsReason { get; set; }

    private bool _archiveRestores;

    /// <summary>
    /// FR-017. What "delete" does here.
    ///
    /// <para>The dialog says plainly that the payslips stay, because someone
    /// reaching for delete usually expects the opposite and would otherwise
    /// assume the figures had gone with the record.</para>
    /// </summary>
    [RelayCommand]
    private void OpenArchive(EmployeeRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ModalError = string.Empty;

        _target = row.Employee;
        _archiveRestores = row.Employee.IsArchived;

        ArchiveReason = string.Empty;
        ArchiveNeedsReason = !_archiveRestores;
        ArchiveAction = _archiveRestores ? "Restore" : "Archive";

        ArchiveTitle = _archiveRestores
            ? $"Restore {row.FullName}?"
            : $"Archive {row.FullName}?";

        ArchiveMessage = _archiveRestores
            ? $"{row.FullName} will appear in the employee list again. They stay separated if they " +
              "were separated before being archived."
            : $"{row.FullName} disappears from the employee list, every picker and every new payroll " +
              "run.\n\nTheir payslips, remittance figures and alphalist entries are not touched — " +
              "those have already been paid and filed, and cannot be unmade by removing the record " +
              "they point at. This is reversible.";

        IsArchiveOpen = true;
    }

    [RelayCommand]
    private void CloseArchive()
    {
        IsArchiveOpen = false;
        ModalError = string.Empty;
        _target = null;
    }

    [RelayCommand]
    private Task SubmitArchiveAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null || _target is null)
            return;

        var result = _archiveRestores
            ? await _employees.RestoreAsync(_target.Id, performedBy)
            : await _employees.ArchiveAsync(_target.Id, ArchiveReason, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsArchiveOpen = false;
        _target = null;

        ShowStatus(result.Message);
        await ReloadAsync();
    });

    // ------------------------------------------------------------ reinstate

    [RelayCommand]
    private void OpenReinstate(EmployeeRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ModalError = string.Empty;

        _target = row.Employee;

        ReinstateMessage =
            $"{row.FullName} was separated on {row.Employee.SeparationDate:dd MMM yyyy}. " +
            "Reinstating clears that date and returns them to active payroll. " +
            "The separation stays in the audit log.";

        IsReinstateOpen = true;
    }

    [RelayCommand]
    private void CloseReinstate()
    {
        IsReinstateOpen = false;
        ModalError = string.Empty;
        _target = null;
    }

    [RelayCommand]
    private Task SubmitReinstateAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null || _target is null)
            return;

        var result = await _employees.ReinstateAsync(_target.Id, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsReinstateOpen = false;
        _target = null;

        ShowStatus(result.Message);
        await ReloadAsync();
    });

    // -------------------------------------------------------------- history

    [RelayCommand]
    private Task OpenHistoryAsync(EmployeeRow? row) => RunAsync(async () =>
    {
        if (row is null)
            return;

        ClearMessages();

        HistorySubject = $"{row.EmployeeNumber} — {row.FullName}";

        var entries = await _employees.GetRateHistoryAsync(row.Id);

        History.Clear();
        foreach (var entry in entries)
            History.Add(entry);

        HistoryEmptyMessage = entries.Count == 0
            ? "No rate changes recorded for this employee yet."
            : string.Empty;

        IsHistoryOpen = true;
    });

    [RelayCommand]
    private void CloseHistory()
    {
        IsHistoryOpen = false;
        History.Clear();
    }

    // ------------------------------------------------------------ internals

    /// <summary>
    /// A plausible starting point for a date-of-birth picker. Today would put
    /// the field on a value the validator immediately rejects.
    /// </summary>
    private static DateTime DefaultBirthDate => DateTime.Today.AddYears(-25);

    private void EvaluateRateChange()
    {
        // Only an edit that actually moves the compensation writes a history
        // row, so only then is a reason worth asking for (FR-018).
        if (_target is null)
        {
            ShowRateReason = false;
            return;
        }

        var changed =
            (TryParseMoney(FormBasicRate, out var rate) && rate != _target.BasicRate) ||
            (FormPayType is not null && FormPayType.As<PayType>() != _target.PayType) ||
            (FormPayFrequency is not null && FormPayFrequency.As<PayFrequency>() != _target.PayFrequency);

        ShowRateReason = changed;
    }

    private void ApplyFieldErrors(IReadOnlyList<FieldError> errors)
    {
        foreach (var error in errors)
        {
            switch (error.Field)
            {
                case nameof(Employee.EmployeeNumber): ErrEmployeeNumber = error.Message; break;
                case nameof(Employee.LastName): ErrLastName = error.Message; break;
                case nameof(Employee.FirstName): ErrFirstName = error.Message; break;
                case nameof(Employee.Email): ErrEmail = error.Message; break;
                case nameof(Employee.BirthDate): ErrBirthDate = error.Message; break;
                case nameof(Employee.HireDate): ErrHireDate = error.Message; break;
                case nameof(Employee.RegularizationDate): ErrRegularization = error.Message; break;
                case nameof(Employee.BasicRate): ErrBasicRate = error.Message; break;
                case nameof(Employee.MonthlyAllowance): ErrAllowance = error.Message; break;
                case nameof(Employee.SssNumber): ErrSss = error.Message; break;
                case nameof(Employee.PhilHealthNumber): ErrPhilHealth = error.Message; break;
                case nameof(Employee.PagIbigNumber): ErrPagIbig = error.Message; break;
                case nameof(Employee.Tin): ErrTin = error.Message; break;

                // A message with no field to sit beside must still be seen,
                // so it falls back to the dialog's banner rather than vanishing.
                default:
                    ModalError = string.IsNullOrEmpty(ModalError)
                        ? error.Message
                        : ModalError + " " + error.Message;
                    break;
            }
        }
    }

    private void ClearFieldErrors()
    {
        ErrEmployeeNumber = string.Empty;
        ErrLastName = string.Empty;
        ErrFirstName = string.Empty;
        ErrEmail = string.Empty;
        ErrBirthDate = string.Empty;
        ErrHireDate = string.Empty;
        ErrRegularization = string.Empty;
        ErrBasicRate = string.Empty;
        ErrAllowance = string.Empty;
        ErrSss = string.Empty;
        ErrPhilHealth = string.Empty;
        ErrPagIbig = string.Empty;
        ErrTin = string.Empty;
    }

    private void ResetForm()
    {
        FormEmployeeNumber = string.Empty;
        FormLastName = string.Empty;
        FormFirstName = string.Empty;
        FormMiddleName = string.Empty;
        FormSuffix = string.Empty;
        FormHasBirthDate = false;
        FormBirthDate = DefaultBirthDate;
        FormGender = GenderOptions.FirstOrDefault();
        FormCivilStatus = CivilStatusOptions.FirstOrDefault();
        FormContactNumber = string.Empty;
        FormEmail = string.Empty;
        FormAddress = string.Empty;
        FormEmergencyName = string.Empty;
        FormEmergencyNumber = string.Empty;

        FormHireDate = DateTime.Today;
        FormIsRegularized = false;
        FormRegularizationDate = DateTime.Today;
        FormEmploymentStatus = EmploymentStatusOptions.FirstOrDefault();
        FormDepartment = DepartmentOptions.FirstOrDefault();
        FormPosition = PositionOptions.FirstOrDefault();
        FormDetachment = DetachmentOptions.FirstOrDefault();
        FormUsesOwnRate = false;
        FormSupervisor = SupervisorOptions.FirstOrDefault();
        FormSchedule = ScheduleOptions.FirstOrDefault();

        FormPayType = PayTypeOptions.FirstOrDefault();
        FormBasicRate = "0.00";
        FormPayFrequency = PayFrequencyOptions.FirstOrDefault();
        FormAllowance = "0.00";
        FormIsMinimumWageEarner = false;
        FormRateReason = string.Empty;
        ShowRateReason = false;

        FormSss = string.Empty;
        FormPhilHealth = string.Empty;
        FormPagIbig = string.Empty;
        FormTin = string.Empty;
        FormExemptSss = false;
        FormExemptPhilHealth = false;
        FormExemptPagIbig = false;

        FormBankName = string.Empty;
        FormBankAccount = string.Empty;
        FormIsActive = true;

        ClearFieldErrors();
    }

    private static LookupOption? Option(IEnumerable<LookupOption> options, int? id) =>
        options.FirstOrDefault(o => o.Id == id) ?? options.FirstOrDefault();

    private static EnumOption? Choice<T>(IEnumerable<EnumOption> options, T value) where T : struct, Enum =>
        options.FirstOrDefault(o => o.Value == Convert.ToInt32(value)) ?? options.FirstOrDefault();

    /// <summary>
    /// Accepts what a person actually types into a money field — thousands
    /// separators, a leading peso sign, surrounding spaces — because rejecting
    /// "25,000.00" as unparseable is a rule the user did not agree to.
    /// </summary>
    private static bool TryParseMoney(string? text, out decimal value)
    {
        value = 0m;

        var cleaned = (text ?? string.Empty).Replace("₱", string.Empty).Trim();

        if (cleaned.Length == 0)
            return true;

        return decimal.TryParse(
            cleaned,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.CurrentCulture,
            out value);
    }

    private static Employee Clone(Employee source) => new()
    {
        Id = source.Id,
        EmployeeNumber = source.EmployeeNumber,
        LastName = source.LastName,
        FirstName = source.FirstName,
        MiddleName = source.MiddleName,
        Suffix = source.Suffix,
        BirthDate = source.BirthDate,
        Gender = source.Gender,
        CivilStatus = source.CivilStatus,
        ContactNumber = source.ContactNumber,
        Email = source.Email,
        Address = source.Address,
        EmergencyContactName = source.EmergencyContactName,
        EmergencyContactNumber = source.EmergencyContactNumber,
        PhotoPath = source.PhotoPath,
        HireDate = source.HireDate,
        RegularizationDate = source.RegularizationDate,
        SeparationDate = source.SeparationDate,
        SeparationReason = source.SeparationReason,
        EmploymentStatus = source.EmploymentStatus,
        DepartmentId = source.DepartmentId,
        PositionId = source.PositionId,
        SupervisorId = source.SupervisorId,
        WorkScheduleId = source.WorkScheduleId,
        PayType = source.PayType,
        BasicRate = source.BasicRate,
        PayFrequency = source.PayFrequency,
        MonthlyAllowance = source.MonthlyAllowance,
        IsMinimumWageEarner = source.IsMinimumWageEarner,
        SssNumber = source.SssNumber,
        PhilHealthNumber = source.PhilHealthNumber,
        PagIbigNumber = source.PagIbigNumber,
        Tin = source.Tin,
        ExemptFromSss = source.ExemptFromSss,
        ExemptFromPhilHealth = source.ExemptFromPhilHealth,
        ExemptFromPagIbig = source.ExemptFromPagIbig,
        BankName = source.BankName,
        BankAccountNumber = source.BankAccountNumber,
        IsActive = source.IsActive,
        CreatedUtc = source.CreatedUtc,
        UpdatedUtc = source.UpdatedUtc
    };
}
