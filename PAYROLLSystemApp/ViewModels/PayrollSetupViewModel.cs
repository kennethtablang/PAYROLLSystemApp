using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>
/// Which panel of the setup section is showing. Five subjects that are edited
/// separately and read together.
/// </summary>
public enum PayrollSetupPanel
{
    Calendar,
    Components,
    Premiums,
    Statutory,
    Company
}

/// <summary>A pay period as the calendar renders it.</summary>
public sealed class PayPeriodRow
{
    public PayPeriodRow(PayPeriod period) => Period = period;

    public PayPeriod Period { get; }

    public int Id => Period.Id;

    public string Code => Period.Code;

    public string Name => Period.Name;

    public string RangeDisplay => Period.RangeDisplay;

    public string CutOffDisplay => Period.CutOffDisplay;

    public string PayDateDisplay => Period.PayDate.ToString("dd MMM yyyy");

    public string StatusDisplay => Period.StatusDisplay;

    public bool IsOpen => Period.IsOpen;

    /// <summary>The action the state button offers: locking, or reopening.</summary>
    public string ActionText => Period.Status == PayPeriodStatus.Open ? "Lock" : "Reopen";

    /// <summary>
    /// Which run of the month this is, spelled out — it is what decides where the
    /// month's SSS, PhilHealth and Pag-IBIG are taken.
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();

            if (Period.RunsInMonth > 1)
            {
                parts.Add(Period.IsLastRunOfMonth
                    ? $"run {Period.SequenceInMonth} of {Period.RunsInMonth} · carries the month's contributions"
                    : $"run {Period.SequenceInMonth} of {Period.RunsInMonth}");
            }

            parts.Add($"{Period.DaysCovered} day(s)");

            return string.Join(" · ", parts);
        }
    }

    public Color StatusColor => Period.Status switch
    {
        PayPeriodStatus.Open => Color.FromArgb("#15803D"),
        PayPeriodStatus.Locked => Color.FromArgb("#B45309"),
        _ => Color.FromArgb("#6B7085")
    };
}

/// <summary>An earning type as the component list renders it.</summary>
public sealed class EarningTypeRow
{
    public EarningTypeRow(EarningType type) => Type = type;

    public EarningType Type { get; }

    public int Id => Type.Id;

    public string Code => Type.Code;

    public string Name => Type.Name;

    public bool IsActive => Type.IsActive;

    public bool IsSystem => Type.IsSystem;

    /// <summary>
    /// An engine-produced component cannot be retired — the engine would have no
    /// line to put the money on — so the row does not offer the action.
    /// </summary>
    public bool IsRetirable => !Type.IsSystem;

    public string ActionText => Type.IsActive ? "Retire" : "Restore";

    public string Detail
    {
        get
        {
            var parts = new List<string> { Type.CategoryDisplay, Type.AmountDisplay, Type.FlagsDisplay };

            if (Type.IsSystem)
                parts.Add("engine-produced");

            if (!Type.IsActive)
                parts.Add("retired");

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>A deduction type as the component list renders it.</summary>
public sealed class DeductionTypeRow
{
    public DeductionTypeRow(DeductionType type) => Type = type;

    public DeductionType Type { get; }

    public int Id => Type.Id;

    public string Code => Type.Code;

    public string Name => Type.Name;

    public bool IsActive => Type.IsActive;

    public bool IsSystem => Type.IsSystem;

    /// <summary>
    /// An engine-produced component cannot be retired — the engine would have no
    /// line to put the money on — so the row does not offer the action.
    /// </summary>
    public bool IsRetirable => !Type.IsSystem;

    public string ActionText => Type.IsActive ? "Retire" : "Restore";

    public string Detail
    {
        get
        {
            var parts = new List<string> { Type.CategoryDisplay, Type.AmountDisplay, Type.FlagsDisplay };

            if (Type.IsSystem)
                parts.Add("engine-produced");

            if (!Type.IsActive)
                parts.Add("retired");

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>One cell of the premium matrix as the list renders it.</summary>
public sealed class PremiumRateRow
{
    public PremiumRateRow(PremiumRate rate, bool isSuperseded)
    {
        Rate = rate;
        IsSuperseded = isSuperseded;
    }

    public PremiumRate Rate { get; }

    /// <summary>True when a later row of the same code has taken over.</summary>
    public bool IsSuperseded { get; }

    public int Id => Rate.Id;

    public string Code => Rate.Code;

    public string Name => Rate.Name;

    public string MultiplierDisplay => Rate.MultiplierDisplay;

    public string ConditionDisplay => Rate.ConditionDisplay;

    public string Detail => IsSuperseded
        ? $"{Rate.ConditionDisplay} · superseded, kept for runs before {Rate.EffectiveFrom:dd MMM yyyy}"
        : $"{Rate.ConditionDisplay} · {Rate.EffectiveDisplay}";

    public Color MultiplierColor => IsSuperseded
        ? Color.FromArgb("#9AA0B2")
        : Color.FromArgb("#4338CA");
}

/// <summary>One SSS bracket as the schedule renders it.</summary>
public sealed class SssBracketRow
{
    public SssBracketRow(SssBracket bracket) => Bracket = bracket;

    public SssBracket Bracket { get; }

    public int Id => Bracket.Id;

    public string RangeDisplay => Bracket.RangeDisplay;

    public string MscDisplay => $"₱{Bracket.MonthlySalaryCredit:N0}";

    public string EmployeeDisplay => $"₱{Bracket.EmployeeTotal:N2}";

    public string EmployerDisplay => $"₱{Bracket.EmployerTotal:N2}";

    /// <summary>
    /// The WISP split, shown only where there is one. Above a ₱20,000 salary
    /// credit part of the contribution stops being regular SS, and the two are
    /// remitted separately.
    /// </summary>
    public string SplitDisplay => Bracket.EmployeeWisp > 0
        ? $"incl. WISP ₱{Bracket.EmployeeWisp:N2} / ₱{Bracket.EmployerWisp:N2}"
        : $"EC ₱{Bracket.EmployerEc:N2}";
}

/// <summary>One withholding tax band as the table renders it.</summary>
public sealed class TaxBracketRow
{
    public TaxBracketRow(WithholdingTaxBracket bracket) => Bracket = bracket;

    public WithholdingTaxBracket Bracket { get; }

    public int Id => Bracket.Id;

    public string RangeDisplay => Bracket.RangeDisplay;

    public string TaxDisplay => Bracket.TaxDisplay;

    public string EffectiveDisplay => $"from {Bracket.EffectiveFrom:dd MMM yyyy}";
}

/// <summary>
/// Section 2.5 — payroll configuration (FR-040 – FR-045).
///
/// <para>Five panels rather than five sidebar entries: a pay calendar, the
/// earning and deduction components, the DOLE premium matrix, the statutory
/// tables and the company profile. They are separate subjects but one job —
/// what a payroll officer sets up before the first run — and none of them is
/// large enough on its own to earn a place in the navigation.</para>
///
/// <para><b>Nothing here computes anything.</b> Every figure on these screens is
/// an input to a payroll run that has not happened yet, which is precisely why
/// they are dangerous: a wrong multiplier or a wrong taxable flag produces a
/// payslip that looks entirely ordinary. The screens therefore say what each
/// setting costs — in the field hints, in the confirmations, and in the audit
/// entries the service writes.</para>
/// </summary>
public sealed partial class PayrollSetupViewModel : BaseViewModel
{
    private readonly IPayrollConfigService _config;
    private readonly IStatutoryTableService _statutory;

    private PayPeriod? _periodTarget;
    private EarningType? _earningTarget;
    private DeductionType? _deductionTarget;
    private PremiumRate? _premiumTarget;
    private PhilHealthRate? _philHealthTarget;
    private PagIbigRate? _pagIbigTarget;
    private WithholdingTaxBracket? _taxTarget;

    /// <summary>What the open confirmation is about. One dialog serves all of them.</summary>
    private enum ConfirmTarget { PayPeriodState, EarningActive, DeductionActive, GenerateYear }

    private ConfirmTarget _confirmTarget;
    private int _confirmId;
    private bool _confirmActivates;

    public PayrollSetupViewModel(
        IPayrollConfigService config,
        IStatutoryTableService statutory,
        ISessionService session)
        : base(session)
    {
        _config = config;
        _statutory = statutory;

        Title = "Payroll setup";

        CategoryOptions = EnumOption.From<EarningCategory>(PayrollEnumNames.Display);
        DeductionCategoryOptions = EnumOption.From<DeductionCategory>(PayrollEnumNames.Display);
        MethodOptions = EnumOption.From<ComputationMethod>(PayrollEnumNames.Display);
        FrequencyOptions = EnumOption.From<PayFrequency>(EmployeeEnumNames.Display);
        ContributionOptions = EnumOption.From<ContributionSchedule>(PayrollEnumNames.Display);
        PaperOptions = EnumOption.From<ReportPaper>(ReportPaperSizes.Display);

        CalendarSummary = string.Empty;
        EarningSummary = string.Empty;
        DeductionSummary = string.Empty;
        PremiumSummary = string.Empty;
        PremiumWarning = string.Empty;
        SssSummary = string.Empty;
        PhilHealthSummary = string.Empty;
        PagIbigSummary = string.Empty;
        TaxSummary = string.Empty;
        TaxWarning = string.Empty;
        StatutoryWarning = string.Empty;
        CompanyWarning = string.Empty;
        RatePreview = string.Empty;
        ModalError = string.Empty;

        PeriodFormTitle = string.Empty;
        EarningFormTitle = string.Empty;
        DeductionFormTitle = string.Empty;
        PremiumFormTitle = string.Empty;
        PhilHealthFormTitle = string.Empty;
        PagIbigFormTitle = string.Empty;
        TaxFormTitle = string.Empty;
        ConfirmTitle = string.Empty;
        ConfirmMessage = string.Empty;
        ConfirmAction = "Confirm";

        SelectedYear = DateTime.Today.Year;
        SelectedTaxScope = null;

        ResetPeriodForm();
        ResetEarningForm();
        ResetDeductionForm();
        ResetPremiumForm();
        ResetPhilHealthForm();
        ResetPagIbigForm();
        ResetTaxForm();
        ResetCompanyForm();
    }

    // ======================================================== collections

    public ObservableCollection<PayPeriodRow> Periods { get; } = new();

    public ObservableCollection<EarningTypeRow> Earnings { get; } = new();

    public ObservableCollection<DeductionTypeRow> Deductions { get; } = new();

    public ObservableCollection<PremiumRateRow> Premiums { get; } = new();

    public ObservableCollection<SssBracketRow> SssSchedule { get; } = new();

    public ObservableCollection<TaxBracketRow> TaxBands { get; } = new();

    public ObservableCollection<LookupOption> CalendarYears { get; } = new();

    public IReadOnlyList<EnumOption> CategoryOptions { get; }

    public IReadOnlyList<EnumOption> DeductionCategoryOptions { get; }

    public IReadOnlyList<EnumOption> MethodOptions { get; }

    public IReadOnlyList<EnumOption> FrequencyOptions { get; }

    public IReadOnlyList<EnumOption> ContributionOptions { get; }

    /// <summary>FR-085. The stock report PDFs are laid out for.</summary>
    public IReadOnlyList<EnumOption> PaperOptions { get; }

    /// <summary>
    /// The tax tables on offer: one per frequency the BIR publishes, plus the
    /// annual table the year-end settlement reads.
    /// </summary>
    public IReadOnlyList<LookupOption> TaxScopeOptions { get; } =
    [
        new((int)PayFrequency.Daily, "Daily"),
        new((int)PayFrequency.Weekly, "Weekly"),
        new((int)PayFrequency.SemiMonthly, "Semi-monthly"),
        new((int)PayFrequency.Monthly, "Monthly"),
        new(-1, "Annual (year-end settlement)")
    ];

    // ============================================================ panels

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalendar))]
    [NotifyPropertyChangedFor(nameof(IsComponents))]
    [NotifyPropertyChangedFor(nameof(IsPremiums))]
    [NotifyPropertyChangedFor(nameof(IsStatutory))]
    [NotifyPropertyChangedFor(nameof(IsCompany))]
    public partial PayrollSetupPanel Panel { get; set; }

    public bool IsCalendar => Panel == PayrollSetupPanel.Calendar;

    public bool IsComponents => Panel == PayrollSetupPanel.Components;

    public bool IsPremiums => Panel == PayrollSetupPanel.Premiums;

    public bool IsStatutory => Panel == PayrollSetupPanel.Statutory;

    public bool IsCompany => Panel == PayrollSetupPanel.Company;

    [RelayCommand]
    private void ShowCalendar() => Switch(PayrollSetupPanel.Calendar);

    [RelayCommand]
    private void ShowComponents() => Switch(PayrollSetupPanel.Components);

    [RelayCommand]
    private void ShowPremiums() => Switch(PayrollSetupPanel.Premiums);

    [RelayCommand]
    private void ShowStatutory() => Switch(PayrollSetupPanel.Statutory);

    [RelayCommand]
    private void ShowCompany() => Switch(PayrollSetupPanel.Company);

    private void Switch(PayrollSetupPanel panel)
    {
        ClearMessages();
        Panel = panel;
    }

    // ======================================================= gate / state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    [ObservableProperty]
    public partial bool ShowRetired { get; set; }

    partial void OnShowRetiredChanged(bool value) => _ = RunAsync(ReloadComponentsAsync);

    /// <summary>Drives the dialog layer; while false the layer must not be hit-testable.</summary>
    public bool IsAnyModalOpen =>
        IsPeriodFormOpen || IsEarningFormOpen || IsDeductionFormOpen || IsPremiumFormOpen ||
        IsPhilHealthFormOpen || IsPagIbigFormOpen || IsTaxFormOpen || IsConfirmOpen;

    // ------------------------------------------------------- summaries

    [ObservableProperty]
    public partial string CalendarSummary { get; set; }

    [ObservableProperty]
    public partial string EarningSummary { get; set; }

    [ObservableProperty]
    public partial string DeductionSummary { get; set; }

    [ObservableProperty]
    public partial string PremiumSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPremiumWarning))]
    public partial string PremiumWarning { get; set; }

    public bool HasPremiumWarning => !string.IsNullOrWhiteSpace(PremiumWarning);

    [ObservableProperty]
    public partial string SssSummary { get; set; }

    [ObservableProperty]
    public partial string PhilHealthSummary { get; set; }

    [ObservableProperty]
    public partial string PagIbigSummary { get; set; }

    [ObservableProperty]
    public partial string TaxSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTaxWarning))]
    public partial string TaxWarning { get; set; }

    public bool HasTaxWarning => !string.IsNullOrWhiteSpace(TaxWarning);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatutoryWarning))]
    public partial string StatutoryWarning { get; set; }

    public bool HasStatutoryWarning => !string.IsNullOrWhiteSpace(StatutoryWarning);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompanyWarning))]
    public partial string CompanyWarning { get; set; }

    public bool HasCompanyWarning => !string.IsNullOrWhiteSpace(CompanyWarning);

    [ObservableProperty]
    public partial bool HasPeriods { get; set; }

    [ObservableProperty]
    public partial bool HasPremiums { get; set; }

    public bool HasNoPeriods => !HasPeriods;

    // =========================================================== loading

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.ManageSystemConfiguration, "open payroll setup"))
        {
            IsDenied = true;
            Periods.Clear();
            Earnings.Clear();
            Deductions.Clear();
            Premiums.Clear();
            SssSchedule.Clear();
            TaxBands.Clear();
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

        await ReloadSettingsAsync();
        await ReloadCalendarAsync();
        await ReloadComponentsAsync();
        await ReloadPremiumsAsync();
        await ReloadStatutoryAsync();
    }

    // =========================================== FR-040 the pay calendar

    [ObservableProperty]
    public partial int SelectedYear { get; set; }

    [ObservableProperty]
    public partial LookupOption? SelectedCalendarYear { get; set; }

    partial void OnSelectedCalendarYearChanged(LookupOption? value)
    {
        if (value?.Id is not { } year || year == SelectedYear)
            return;

        SelectedYear = year;
        _ = RunAsync(ReloadCalendarAsync);
    }

    private async Task ReloadCalendarAsync()
    {
        var years = await _config.GetCalendarYearsAsync();

        CalendarYears.Clear();
        foreach (var year in years)
            CalendarYears.Add(new LookupOption(year, year.ToString(CultureInfo.InvariantCulture)));

        if (!years.Contains(SelectedYear))
            SelectedYear = years.FirstOrDefault(DateTime.Today.Year);

        SelectedCalendarYear = CalendarYears.FirstOrDefault(y => y.Id == SelectedYear);

        var periods = await _config.GetPayPeriodsAsync(SelectedYear);

        Periods.Clear();
        foreach (var period in periods)
            Periods.Add(new PayPeriodRow(period));

        HasPeriods = Periods.Count > 0;
        OnPropertyChanged(nameof(HasNoPeriods));

        var open = periods.Count(p => p.IsOpen);

        CalendarSummary = periods.Count == 0
            ? $"No periods for {SelectedYear} yet."
            : $"{periods.Count} period(s) · {open} open · " +
              $"{EmployeeEnumNames.Display(periods[0].Frequency).ToLowerInvariant()}";
    }

    [RelayCommand]
    private void OpenGenerateYear()
    {
        ClearMessages();

        _confirmTarget = ConfirmTarget.GenerateYear;
        ConfirmTitle = $"Generate the {SelectedYear} calendar";
        ConfirmMessage =
            $"This lays out {SelectedYear} from the current payroll settings — the frequency, the cut-off " +
            "lead and the pay date lag on the Company panel.\n\n" +
            "Periods that already exist are left exactly as they are, including their dates: a cut-off that " +
            "has already been paid is never moved. Only the gaps are filled.";
        ConfirmAction = "Generate";
        IsConfirmDestructive = false;
        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void OpenCreatePeriod()
    {
        ClearMessages();
        ResetPeriodForm();

        _periodTarget = null;
        PeriodFormTitle = "New pay period";
        IsPeriodFormOpen = true;
    }

    [RelayCommand]
    private void OpenEditPeriod(PayPeriodRow? row)
    {
        if (row is null)
            return;

        ClearMessages();

        _periodTarget = row.Period;
        PeriodFormTitle = $"Edit {row.Code}";

        PeriodCode = row.Period.Code;
        PeriodName = row.Period.Name;
        PeriodStart = row.Period.PeriodStart;
        PeriodEnd = row.Period.PeriodEnd;
        PeriodCutOffStart = row.Period.CutOffStart;
        PeriodCutOffEnd = row.Period.CutOffEnd;
        PeriodPayDate = row.Period.PayDate;
        PeriodRemarks = row.Period.Remarks;

        IsPeriodFormOpen = true;
    }

    [RelayCommand]
    private void OpenTogglePeriod(PayPeriodRow? row)
    {
        if (row is null)
            return;

        ClearMessages();

        _confirmTarget = ConfirmTarget.PayPeriodState;
        _confirmId = row.Id;
        _confirmActivates = !row.IsOpen;

        ConfirmTitle = row.IsOpen ? $"Lock {row.Code}" : $"Reopen {row.Code}";

        ConfirmMessage = row.IsOpen
            ? $"Locking {row.Code} freezes the attendance inside its cut-off ({row.CutOffDisplay}) so a " +
              "payroll run computed against it cannot be undermined by a later correction.\n\n" +
              "It can be reopened, but anything already computed will need recalculating."
            : $"Reopening {row.Code} allows attendance inside its cut-off to be corrected again.\n\n" +
              "Any payroll already computed against this period is now out of date and should be " +
              "recalculated before it is approved.";

        ConfirmAction = row.IsOpen ? "Lock" : "Reopen";
        IsConfirmDestructive = !row.IsOpen;
        IsConfirmOpen = true;
    }

    // ------------------------------------------------------ period form

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsPeriodFormOpen { get; set; }

    [ObservableProperty]
    public partial string PeriodFormTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPeriodCommand))]
    public partial string PeriodCode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPeriodCommand))]
    public partial string PeriodName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodPreview))]
    public partial DateTime PeriodStart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodPreview))]
    public partial DateTime PeriodEnd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodPreview))]
    public partial DateTime PeriodCutOffStart { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodPreview))]
    public partial DateTime PeriodCutOffEnd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodPreview))]
    public partial DateTime PeriodPayDate { get; set; }

    [ObservableProperty]
    public partial string PeriodRemarks { get; set; }

    /// <summary>
    /// The three dates read back as a sentence while they are being typed, so a
    /// pay date that falls before the cut-off closes is visible before it is
    /// saved rather than after.
    /// </summary>
    public string PeriodPreview
    {
        get
        {
            var days = (int)(PeriodEnd.Date - PeriodStart.Date).TotalDays + 1;

            if (days <= 0)
                return "The period ends before it starts.";

            var gap = (int)(PeriodPayDate.Date - PeriodCutOffEnd.Date).TotalDays;

            var tail = gap < 0
                ? "Paid before the cut-off closes — the run would have nothing to compute from."
                : gap == 0
                    ? "Paid on the day the cut-off closes, which leaves no time to process the run."
                    : $"{gap} day(s) between the cut-off closing and the pay date.";

            return $"Covers {days} day(s); attendance read from " +
                   $"{PeriodCutOffStart:dd MMM} to {PeriodCutOffEnd:dd MMM}. {tail}";
        }
    }

    private void ResetPeriodForm()
    {
        var today = DateTime.Today;

        PeriodCode = string.Empty;
        PeriodName = string.Empty;
        PeriodStart = new DateTime(today.Year, today.Month, 1);
        PeriodEnd = new DateTime(today.Year, today.Month, 15);
        PeriodCutOffStart = new DateTime(today.Year, today.Month, 1).AddDays(-5);
        PeriodCutOffEnd = new DateTime(today.Year, today.Month, 10);
        PeriodPayDate = new DateTime(today.Year, today.Month, 15);
        PeriodRemarks = string.Empty;
    }

    [RelayCommand]
    private void ClosePeriodForm()
    {
        IsPeriodFormOpen = false;
        ModalError = string.Empty;
        _periodTarget = null;
    }

    private bool CanSubmitPeriod() =>
        !string.IsNullOrWhiteSpace(PeriodCode) && !string.IsNullOrWhiteSpace(PeriodName);

    [RelayCommand(CanExecute = nameof(CanSubmitPeriod))]
    private Task SubmitPeriodAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        var settings = await _config.GetSettingsAsync();

        var period = new PayPeriod
        {
            Id = _periodTarget?.Id ?? 0,
            Code = PeriodCode,
            Name = PeriodName,
            Frequency = _periodTarget?.Frequency ?? settings.PayFrequency,
            PeriodStart = PeriodStart,
            PeriodEnd = PeriodEnd,
            CutOffStart = PeriodCutOffStart,
            CutOffEnd = PeriodCutOffEnd,
            PayDate = PeriodPayDate,
            SequenceInMonth = _periodTarget?.SequenceInMonth ?? 1,
            RunsInMonth = _periodTarget?.RunsInMonth ??
                          PayrollEnumNames.RunsPerMonth(settings.PayFrequency),
            Remarks = PeriodRemarks
        };

        var result = await _config.SavePayPeriodAsync(period, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsPeriodFormOpen = false;
        _periodTarget = null;

        await ReloadCalendarAsync();
        ShowStatus(result.Message);
    });

    // ================================ FR-041 / FR-042 the pay components

    private async Task ReloadComponentsAsync()
    {
        var earnings = await _config.GetEarningTypesAsync(includeInactive: true);
        var deductions = await _config.GetDeductionTypesAsync(includeInactive: true);

        var visibleEarnings = earnings.Where(e => ShowRetired || e.IsActive).ToList();
        var visibleDeductions = deductions.Where(d => ShowRetired || d.IsActive).ToList();

        Earnings.Clear();
        foreach (var earning in visibleEarnings)
            Earnings.Add(new EarningTypeRow(earning));

        Deductions.Clear();
        foreach (var deduction in visibleDeductions)
            Deductions.Add(new DeductionTypeRow(deduction));

        EarningSummary = Summarise(earnings.Count, visibleEarnings.Count, "earning");
        DeductionSummary = Summarise(deductions.Count, visibleDeductions.Count, "deduction");
    }

    private static string Summarise(int total, int shown, string noun)
    {
        var retired = total - shown;

        return retired > 0
            ? $"{shown} active {noun}(s) · {retired} retired hidden"
            : $"{shown} {noun}(s)";
    }

    [RelayCommand]
    private void OpenCreateEarning()
    {
        ClearMessages();
        ResetEarningForm();

        _earningTarget = null;
        EarningFormTitle = "New earning type";
        IsEarningFormOpen = true;
    }

    [RelayCommand]
    private void OpenEditEarning(EarningTypeRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ResetEarningForm();

        var type = row.Type;
        _earningTarget = type;
        EarningFormTitle = $"Edit {type.Name}";

        EarningCode = type.Code;
        EarningName = type.Name;
        EarningDescription = type.Description;
        EarningCategoryOption = CategoryOptions.FirstOrDefault(o => o.Value == (int)type.Category);
        EarningMethod = MethodOptions.FirstOrDefault(o => o.Value == (int)type.Method);
        EarningAmount = type.DefaultAmount == 0 ? string.Empty : type.DefaultAmount.ToString("0.####");
        EarningIsTaxable = type.IsTaxable;
        EarningInContributionBase = type.IsPartOfContributionBase;
        EarningInThirteenthMonth = type.IsThirteenthMonthBase;
        EarningIsRecurring = type.IsRecurring;
        EarningIsSystem = type.IsSystem;

        IsEarningFormOpen = true;
    }

    [RelayCommand]
    private void OpenToggleEarning(EarningTypeRow? row)
    {
        if (row is null)
            return;

        ClearMessages();

        _confirmTarget = ConfirmTarget.EarningActive;
        _confirmId = row.Id;
        _confirmActivates = !row.IsActive;

        ConfirmTitle = row.IsActive ? $"Retire {row.Name}" : $"Restore {row.Name}";

        ConfirmMessage = row.IsActive
            ? $"{row.Name} will stop being offered on new payroll runs.\n\n" +
              "Payslips already issued against it keep the line exactly as it was printed — nothing here " +
              "is deleted, because a payslip has to keep meaning what it meant."
            : $"{row.Name} will be offered on payroll runs again.";

        ConfirmAction = row.IsActive ? "Retire" : "Restore";
        IsConfirmDestructive = row.IsActive;
        IsConfirmOpen = true;
    }

    // ----------------------------------------------------- earning form

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsEarningFormOpen { get; set; }

    [ObservableProperty]
    public partial string EarningFormTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitEarningCommand))]
    public partial string EarningCode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitEarningCommand))]
    public partial string EarningName { get; set; }

    [ObservableProperty]
    public partial string EarningDescription { get; set; }

    [ObservableProperty]
    public partial EnumOption? EarningCategoryOption { get; set; }

    [ObservableProperty]
    public partial EnumOption? EarningMethod { get; set; }

    [ObservableProperty]
    public partial string EarningAmount { get; set; }

    [ObservableProperty]
    public partial bool EarningIsTaxable { get; set; }

    [ObservableProperty]
    public partial bool EarningInContributionBase { get; set; }

    [ObservableProperty]
    public partial bool EarningInThirteenthMonth { get; set; }

    [ObservableProperty]
    public partial bool EarningIsRecurring { get; set; }

    /// <summary>
    /// True for an engine-produced row. The code, category and method are then
    /// read-only, because the engine finds its own lines by code.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EarningIsEditableShape))]
    public partial bool EarningIsSystem { get; set; }

    public bool EarningIsEditableShape => !EarningIsSystem;

    private void ResetEarningForm()
    {
        EarningCode = string.Empty;
        EarningName = string.Empty;
        EarningDescription = string.Empty;
        EarningCategoryOption = CategoryOptions.FirstOrDefault(o => o.Value == (int)EarningCategory.Allowance);
        EarningMethod = MethodOptions.FirstOrDefault(o => o.Value == (int)ComputationMethod.FixedAmount);
        EarningAmount = string.Empty;
        EarningIsTaxable = true;
        EarningInContributionBase = false;
        EarningInThirteenthMonth = false;
        EarningIsRecurring = false;
        EarningIsSystem = false;
    }

    [RelayCommand]
    private void CloseEarningForm()
    {
        IsEarningFormOpen = false;
        ModalError = string.Empty;
        _earningTarget = null;
    }

    private bool CanSubmitEarning() =>
        !string.IsNullOrWhiteSpace(EarningCode) && !string.IsNullOrWhiteSpace(EarningName);

    [RelayCommand(CanExecute = nameof(CanSubmitEarning))]
    private Task SubmitEarningAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        if (!TryParseAmount(EarningAmount, out var amount))
        {
            ModalError = "Enter the amount as a number, or leave it blank.";
            return;
        }

        var type = new EarningType
        {
            Id = _earningTarget?.Id ?? 0,
            Code = EarningCode,
            Name = EarningName,
            Description = EarningDescription,
            Category = EarningCategoryOption?.As<EarningCategory>() ?? EarningCategory.Allowance,
            Method = EarningMethod?.As<ComputationMethod>() ?? ComputationMethod.FixedAmount,
            DefaultAmount = amount,
            IsTaxable = EarningIsTaxable,
            IsPartOfContributionBase = EarningInContributionBase,
            IsThirteenthMonthBase = EarningInThirteenthMonth,
            IsRecurring = EarningIsRecurring,
            DisplayOrder = _earningTarget?.DisplayOrder ?? 200
        };

        var result = await _config.SaveEarningTypeAsync(type, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsEarningFormOpen = false;
        _earningTarget = null;

        await ReloadComponentsAsync();
        ShowStatus(result.Message);
    });

    // --------------------------------------------------- deduction form

    [RelayCommand]
    private void OpenCreateDeduction()
    {
        ClearMessages();
        ResetDeductionForm();

        _deductionTarget = null;
        DeductionFormTitle = "New deduction type";
        IsDeductionFormOpen = true;
    }

    [RelayCommand]
    private void OpenEditDeduction(DeductionTypeRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ResetDeductionForm();

        var type = row.Type;
        _deductionTarget = type;
        DeductionFormTitle = $"Edit {type.Name}";

        DeductionCode = type.Code;
        DeductionName = type.Name;
        DeductionDescription = type.Description;
        DeductionCategoryOption = DeductionCategoryOptions.FirstOrDefault(o => o.Value == (int)type.Category);
        DeductionMethod = MethodOptions.FirstOrDefault(o => o.Value == (int)type.Method);
        DeductionAmount = type.DefaultAmount == 0 ? string.Empty : type.DefaultAmount.ToString("0.####");
        DeductionReducesTax = type.ReducesTaxableIncome;
        DeductionIsAmortised = type.IsAmortised;
        DeductionIsSystem = type.IsSystem;

        IsDeductionFormOpen = true;
    }

    [RelayCommand]
    private void OpenToggleDeduction(DeductionTypeRow? row)
    {
        if (row is null)
            return;

        ClearMessages();

        _confirmTarget = ConfirmTarget.DeductionActive;
        _confirmId = row.Id;
        _confirmActivates = !row.IsActive;

        ConfirmTitle = row.IsActive ? $"Retire {row.Name}" : $"Restore {row.Name}";

        ConfirmMessage = row.IsActive
            ? $"{row.Name} will stop being applied on new payroll runs.\n\n" +
              "Payslips already issued against it are unchanged."
            : $"{row.Name} will be applied on payroll runs again.";

        ConfirmAction = row.IsActive ? "Retire" : "Restore";
        IsConfirmDestructive = row.IsActive;
        IsConfirmOpen = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsDeductionFormOpen { get; set; }

    [ObservableProperty]
    public partial string DeductionFormTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitDeductionCommand))]
    public partial string DeductionCode { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitDeductionCommand))]
    public partial string DeductionName { get; set; }

    [ObservableProperty]
    public partial string DeductionDescription { get; set; }

    [ObservableProperty]
    public partial EnumOption? DeductionCategoryOption { get; set; }

    [ObservableProperty]
    public partial EnumOption? DeductionMethod { get; set; }

    [ObservableProperty]
    public partial string DeductionAmount { get; set; }

    [ObservableProperty]
    public partial bool DeductionReducesTax { get; set; }

    [ObservableProperty]
    public partial bool DeductionIsAmortised { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeductionIsEditableShape))]
    public partial bool DeductionIsSystem { get; set; }

    public bool DeductionIsEditableShape => !DeductionIsSystem;

    private void ResetDeductionForm()
    {
        DeductionCode = string.Empty;
        DeductionName = string.Empty;
        DeductionDescription = string.Empty;
        DeductionCategoryOption = DeductionCategoryOptions.FirstOrDefault(o => o.Value == (int)DeductionCategory.Other);
        DeductionMethod = MethodOptions.FirstOrDefault(o => o.Value == (int)ComputationMethod.FixedAmount);
        DeductionAmount = string.Empty;
        DeductionReducesTax = false;
        DeductionIsAmortised = false;
        DeductionIsSystem = false;
    }

    [RelayCommand]
    private void CloseDeductionForm()
    {
        IsDeductionFormOpen = false;
        ModalError = string.Empty;
        _deductionTarget = null;
    }

    private bool CanSubmitDeduction() =>
        !string.IsNullOrWhiteSpace(DeductionCode) && !string.IsNullOrWhiteSpace(DeductionName);

    [RelayCommand(CanExecute = nameof(CanSubmitDeduction))]
    private Task SubmitDeductionAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        if (!TryParseAmount(DeductionAmount, out var amount))
        {
            ModalError = "Enter the amount as a number, or leave it blank.";
            return;
        }

        var type = new DeductionType
        {
            Id = _deductionTarget?.Id ?? 0,
            Code = DeductionCode,
            Name = DeductionName,
            Description = DeductionDescription,
            Category = DeductionCategoryOption?.As<DeductionCategory>() ?? DeductionCategory.Other,
            Method = DeductionMethod?.As<ComputationMethod>() ?? ComputationMethod.FixedAmount,
            DefaultAmount = amount,
            ReducesTaxableIncome = DeductionReducesTax,
            IsAmortised = DeductionIsAmortised,
            Priority = _deductionTarget?.Priority ?? 100,
            DisplayOrder = _deductionTarget?.DisplayOrder ?? 200
        };

        var result = await _config.SaveDeductionTypeAsync(type, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsDeductionFormOpen = false;
        _deductionTarget = null;

        await ReloadComponentsAsync();
        ShowStatus(result.Message);
    });

    // ========================================== FR-044 the premium matrix

    private async Task ReloadPremiumsAsync()
    {
        var rates = await _config.GetPremiumRatesAsync(includeInactive: false);
        var matrix = await _config.GetPremiumMatrixAsync(DateTime.Today);

        // A code with two rows has been superseded; only the one the matrix
        // resolved is in force today, and the rest are kept for history.
        var inForce = PremiumMatrix.RequiredCodes
            .Select(matrix.Get)
            .Where(r => r is not null)
            .Select(r => r!.Id)
            .ToHashSet();

        Premiums.Clear();
        foreach (var rate in rates)
            Premiums.Add(new PremiumRateRow(rate, isSuperseded: !inForce.Contains(rate.Id)));

        HasPremiums = Premiums.Count > 0;

        var missing = matrix.MissingCodes();

        PremiumSummary = $"{inForce.Count} cell(s) in force today · " +
                         $"{rates.Count - inForce.Count} superseded row(s) kept for history";

        // A missing cell is not a cosmetic gap: the engine stops rather than
        // guessing a multiplier, so it is worth naming the codes.
        PremiumWarning = missing.Count == 0
            ? string.Empty
            : $"No multiplier is configured for {string.Join(", ", missing)}. A payroll run that meets one " +
              "of these days will stop rather than guess a rate.";
    }

    [RelayCommand]
    private void OpenEditPremium(PremiumRateRow? row)
    {
        if (row is null)
            return;

        ClearMessages();

        var rate = row.Rate;
        _premiumTarget = rate;

        PremiumFormTitle = $"Edit {rate.Name}";
        PremiumCodeDisplay = rate.Code;
        PremiumConditionDisplay = rate.ConditionDisplay;
        PremiumName = rate.Name;
        PremiumDescription = rate.Description;
        PremiumMultiplier = rate.Multiplier.ToString("0.####");
        PremiumEffectiveFrom = NextEffectiveDate(rate.EffectiveFrom);
        PremiumWasInForce = rate.EffectiveFrom.Date <= DateTime.Today;

        IsPremiumFormOpen = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsPremiumFormOpen { get; set; }

    [ObservableProperty]
    public partial string PremiumFormTitle { get; set; }

    [ObservableProperty]
    public partial string PremiumCodeDisplay { get; set; }

    [ObservableProperty]
    public partial string PremiumConditionDisplay { get; set; }

    [ObservableProperty]
    public partial string PremiumName { get; set; }

    [ObservableProperty]
    public partial string PremiumDescription { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPremiumCommand))]
    public partial string PremiumMultiplier { get; set; }

    [ObservableProperty]
    public partial DateTime PremiumEffectiveFrom { get; set; }

    /// <summary>
    /// True when the rate being edited has already been in force, in which case
    /// changing the multiplier supersedes it rather than overwriting it.
    /// </summary>
    [ObservableProperty]
    public partial bool PremiumWasInForce { get; set; }

    private void ResetPremiumForm()
    {
        PremiumCodeDisplay = string.Empty;
        PremiumConditionDisplay = string.Empty;
        PremiumName = string.Empty;
        PremiumDescription = string.Empty;
        PremiumMultiplier = string.Empty;
        PremiumEffectiveFrom = DateTime.Today;
        PremiumWasInForce = false;
    }

    [RelayCommand]
    private void ClosePremiumForm()
    {
        IsPremiumFormOpen = false;
        ModalError = string.Empty;
        _premiumTarget = null;
    }

    private bool CanSubmitPremium() => !string.IsNullOrWhiteSpace(PremiumMultiplier);

    [RelayCommand(CanExecute = nameof(CanSubmitPremium))]
    private Task SubmitPremiumAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null || _premiumTarget is null)
            return;

        if (!decimal.TryParse(PremiumMultiplier, NumberStyles.Number, CultureInfo.CurrentCulture, out var multiplier) &&
            !decimal.TryParse(PremiumMultiplier, NumberStyles.Number, CultureInfo.InvariantCulture, out multiplier))
        {
            ModalError = "Enter the multiplier as a number, e.g. 1.25.";
            return;
        }

        var rate = new PremiumRate
        {
            Id = _premiumTarget.Id,
            Code = _premiumTarget.Code,
            Name = PremiumName,
            Description = PremiumDescription,
            Multiplier = multiplier,
            EffectiveFrom = PremiumEffectiveFrom,
            IsActive = true
        };

        var result = await _config.SavePremiumRateAsync(rate, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsPremiumFormOpen = false;
        _premiumTarget = null;

        await ReloadPremiumsAsync();
        ShowStatus(result.Message);
    });

    // ======================================= FR-043 the statutory tables

    [ObservableProperty]
    public partial LookupOption? SelectedTaxScope { get; set; }

    partial void OnSelectedTaxScopeChanged(LookupOption? value)
    {
        if (value is null)
            return;

        _ = RunAsync(ReloadTaxBandsAsync);
    }

    private async Task ReloadStatutoryAsync()
    {
        var snapshot = await _statutory.GetSnapshotAsync(DateTime.Today);

        SssSchedule.Clear();
        foreach (var bracket in snapshot.SssSchedule.OrderBy(b => b.MonthlySalaryCredit))
            SssSchedule.Add(new SssBracketRow(bracket));

        SssSummary = snapshot.SssSchedule.Count == 0
            ? "No SSS schedule is configured."
            : $"{snapshot.SssSchedule.Count} bracket(s), in force since " +
              $"{snapshot.SssSchedule.Max(b => b.EffectiveFrom):dd MMM yyyy} · " +
              $"salary credit ₱{snapshot.SssSchedule.Min(b => b.MonthlySalaryCredit):N0} – " +
              $"₱{snapshot.SssSchedule.Max(b => b.MonthlySalaryCredit):N0}";

        PhilHealthSummary = snapshot.PhilHealth is null
            ? "No PhilHealth premium is configured."
            : $"{snapshot.PhilHealth.Display} · {snapshot.PhilHealth.EffectiveDisplay}";

        PagIbigSummary = snapshot.PagIbig is null
            ? "No Pag-IBIG contribution is configured."
            : $"{snapshot.PagIbig.Display} · {snapshot.PagIbig.EffectiveDisplay}";

        // The one thing an application genuinely cannot check for itself.
        StatutoryWarning = snapshot.IsComplete
            ? "Seeded from the schedules current when this system was built. Contribution and tax tables " +
              "change by circular — verify all four against the agencies' current issuances before the " +
              "first live payroll run."
            : $"Payroll cannot run: {string.Join(", ", snapshot.Missing)} missing.";

        if (SelectedTaxScope is null)
        {
            var settings = await _config.GetSettingsAsync();
            SelectedTaxScope = TaxScopeOptions.FirstOrDefault(o => o.Id == (int)settings.PayFrequency)
                               ?? TaxScopeOptions[2];
        }

        await ReloadTaxBandsAsync();
    }

    private async Task ReloadTaxBandsAsync()
    {
        var scope = SelectedTaxScope;
        if (scope is null)
            return;

        var annual = scope.Id is null or < 0;
        var frequency = annual ? PayFrequency.Monthly : (PayFrequency)scope.Id!.Value;

        var bands = await _statutory.GetTaxBracketsAsync(frequency, annual);

        TaxBands.Clear();
        foreach (var band in bands.OrderBy(b => b.LowerLimit))
            TaxBands.Add(new TaxBracketRow(band));

        TaxSummary = bands.Count == 0
            ? "No table is configured for this frequency."
            : $"{bands.Count} band(s) · TRAIN (RA 10963), in force since " +
              $"{bands.Max(b => b.EffectiveFrom):dd MMM yyyy}";

        // A frequency with no published BIR table is a real gap, not a data
        // entry oversight, and saying so is more useful than an empty list.
        TaxWarning = !annual && !PayrollEnumNames.HasPublishedTaxTable(frequency)
            ? $"The BIR publishes no {EmployeeEnumNames.Display(frequency).ToLowerInvariant()} withholding " +
              "table. A company on this cycle has to withhold on another basis — commonly the weekly table " +
              "applied twice — and enter the bands here deliberately."
            : string.Empty;
    }

    // ------------------------------------------------- PhilHealth form

    [RelayCommand]
    private Task OpenEditPhilHealthAsync() => RunAsync(async () =>
    {
        ClearMessages();

        var current = (await _statutory.GetPhilHealthRatesAsync()).FirstOrDefault();
        _philHealthTarget = current;

        PhilHealthFormTitle = current is null ? "New PhilHealth premium" : "New PhilHealth premium schedule";
        PhilHealthRateText = (current?.PremiumRatePercent ?? 5m).ToString("0.####");
        PhilHealthFloor = (current?.SalaryFloor ?? 10000m).ToString("0.##");
        PhilHealthCeiling = (current?.SalaryCeiling ?? 100000m).ToString("0.##");
        PhilHealthRemarks = string.Empty;
        PhilHealthEffectiveFrom = NextEffectiveDate(current?.EffectiveFrom);

        IsPhilHealthFormOpen = true;
    });

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsPhilHealthFormOpen { get; set; }

    [ObservableProperty]
    public partial string PhilHealthFormTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPhilHealthCommand))]
    public partial string PhilHealthRateText { get; set; }

    [ObservableProperty]
    public partial string PhilHealthFloor { get; set; }

    [ObservableProperty]
    public partial string PhilHealthCeiling { get; set; }

    [ObservableProperty]
    public partial string PhilHealthRemarks { get; set; }

    [ObservableProperty]
    public partial DateTime PhilHealthEffectiveFrom { get; set; }

    private void ResetPhilHealthForm()
    {
        PhilHealthRateText = string.Empty;
        PhilHealthFloor = string.Empty;
        PhilHealthCeiling = string.Empty;
        PhilHealthRemarks = string.Empty;
        PhilHealthEffectiveFrom = DateTime.Today;
    }

    [RelayCommand]
    private void ClosePhilHealthForm()
    {
        IsPhilHealthFormOpen = false;
        ModalError = string.Empty;
        _philHealthTarget = null;
    }

    private bool CanSubmitPhilHealth() => !string.IsNullOrWhiteSpace(PhilHealthRateText);

    [RelayCommand(CanExecute = nameof(CanSubmitPhilHealth))]
    private Task SubmitPhilHealthAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        if (!TryParseAmount(PhilHealthRateText, out var rate) ||
            !TryParseAmount(PhilHealthFloor, out var floor) ||
            !TryParseAmount(PhilHealthCeiling, out var ceiling))
        {
            ModalError = "Enter the rate, floor and ceiling as numbers.";
            return;
        }

        // A change is always a new schedule: the old rows have to stay so a
        // payslip computed under them can still be explained (C-02).
        var result = await _statutory.SavePhilHealthRateAsync(new PhilHealthRate
        {
            PremiumRatePercent = rate,
            SalaryFloor = floor,
            SalaryCeiling = ceiling,
            Remarks = PhilHealthRemarks,
            EffectiveFrom = PhilHealthEffectiveFrom
        }, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsPhilHealthFormOpen = false;
        _philHealthTarget = null;

        await ReloadStatutoryAsync();
        ShowStatus(result.Message);
    });

    // ---------------------------------------------------- Pag-IBIG form

    [RelayCommand]
    private Task OpenEditPagIbigAsync() => RunAsync(async () =>
    {
        ClearMessages();

        var current = (await _statutory.GetPagIbigRatesAsync()).FirstOrDefault();
        _pagIbigTarget = current;

        PagIbigFormTitle = "New Pag-IBIG contribution schedule";
        PagIbigThreshold = (current?.LowerRateThreshold ?? 1500m).ToString("0.##");
        PagIbigEmployeeLow = (current?.EmployeeRateLowPercent ?? 1m).ToString("0.####");
        PagIbigEmployeeHigh = (current?.EmployeeRateHighPercent ?? 2m).ToString("0.####");
        PagIbigEmployer = (current?.EmployerRatePercent ?? 2m).ToString("0.####");
        PagIbigCap = (current?.FundSalaryCap ?? 10000m).ToString("0.##");
        PagIbigRemarks = string.Empty;
        PagIbigEffectiveFrom = NextEffectiveDate(current?.EffectiveFrom);

        IsPagIbigFormOpen = true;
    });

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsPagIbigFormOpen { get; set; }

    [ObservableProperty]
    public partial string PagIbigFormTitle { get; set; }

    [ObservableProperty]
    public partial string PagIbigThreshold { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitPagIbigCommand))]
    public partial string PagIbigEmployeeLow { get; set; }

    [ObservableProperty]
    public partial string PagIbigEmployeeHigh { get; set; }

    [ObservableProperty]
    public partial string PagIbigEmployer { get; set; }

    [ObservableProperty]
    public partial string PagIbigCap { get; set; }

    [ObservableProperty]
    public partial string PagIbigRemarks { get; set; }

    [ObservableProperty]
    public partial DateTime PagIbigEffectiveFrom { get; set; }

    private void ResetPagIbigForm()
    {
        PagIbigThreshold = string.Empty;
        PagIbigEmployeeLow = string.Empty;
        PagIbigEmployeeHigh = string.Empty;
        PagIbigEmployer = string.Empty;
        PagIbigCap = string.Empty;
        PagIbigRemarks = string.Empty;
        PagIbigEffectiveFrom = DateTime.Today;
    }

    [RelayCommand]
    private void ClosePagIbigForm()
    {
        IsPagIbigFormOpen = false;
        ModalError = string.Empty;
        _pagIbigTarget = null;
    }

    private bool CanSubmitPagIbig() => !string.IsNullOrWhiteSpace(PagIbigEmployeeLow);

    [RelayCommand(CanExecute = nameof(CanSubmitPagIbig))]
    private Task SubmitPagIbigAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        if (!TryParseAmount(PagIbigThreshold, out var threshold) ||
            !TryParseAmount(PagIbigEmployeeLow, out var low) ||
            !TryParseAmount(PagIbigEmployeeHigh, out var high) ||
            !TryParseAmount(PagIbigEmployer, out var employer) ||
            !TryParseAmount(PagIbigCap, out var cap))
        {
            ModalError = "Enter every rate and bound as a number.";
            return;
        }

        var result = await _statutory.SavePagIbigRateAsync(new PagIbigRate
        {
            LowerRateThreshold = threshold,
            EmployeeRateLowPercent = low,
            EmployeeRateHighPercent = high,
            EmployerRatePercent = employer,
            FundSalaryCap = cap,
            Remarks = PagIbigRemarks,
            EffectiveFrom = PagIbigEffectiveFrom
        }, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsPagIbigFormOpen = false;
        _pagIbigTarget = null;

        await ReloadStatutoryAsync();
        ShowStatus(result.Message);
    });

    // --------------------------------------------------- tax band form

    [RelayCommand]
    private void OpenEditTaxBand(TaxBracketRow? row)
    {
        if (row is null)
            return;

        ClearMessages();

        var band = row.Bracket;
        _taxTarget = band;

        TaxFormTitle = $"{band.ScopeDisplay} band · {band.RangeDisplay}";
        TaxLowerLimit = band.LowerLimit.ToString("0.##");
        TaxUpperLimit = band.UpperLimit is { } upper ? upper.ToString("0.##") : string.Empty;
        TaxBaseAmount = band.BaseTax.ToString("0.##");
        TaxRate = band.RateOnExcessPercent.ToString("0.####");
        TaxEffectiveFrom = NextEffectiveDate(band.EffectiveFrom);
        TaxWasInForce = band.EffectiveFrom.Date <= DateTime.Today;

        IsTaxFormOpen = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsTaxFormOpen { get; set; }

    [ObservableProperty]
    public partial string TaxFormTitle { get; set; }

    [ObservableProperty]
    public partial string TaxLowerLimit { get; set; }

    [ObservableProperty]
    public partial string TaxUpperLimit { get; set; }

    [ObservableProperty]
    public partial string TaxBaseAmount { get; set; }

    [ObservableProperty]
    public partial string TaxRate { get; set; }

    [ObservableProperty]
    public partial DateTime TaxEffectiveFrom { get; set; }

    [ObservableProperty]
    public partial bool TaxWasInForce { get; set; }

    private void ResetTaxForm()
    {
        TaxLowerLimit = string.Empty;
        TaxUpperLimit = string.Empty;
        TaxBaseAmount = string.Empty;
        TaxRate = string.Empty;
        TaxEffectiveFrom = DateTime.Today;
        TaxWasInForce = false;
    }

    [RelayCommand]
    private void CloseTaxForm()
    {
        IsTaxFormOpen = false;
        ModalError = string.Empty;
        _taxTarget = null;
    }

    [RelayCommand]
    private Task SubmitTaxBandAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null || _taxTarget is null)
            return;

        if (!TryParseAmount(TaxLowerLimit, out var lower) ||
            !TryParseAmount(TaxBaseAmount, out var baseTax) ||
            !TryParseAmount(TaxRate, out var rate))
        {
            ModalError = "Enter the limits, base tax and rate as numbers.";
            return;
        }

        // A blank upper limit is the top band, which is open-ended.
        decimal? upper = null;

        if (!string.IsNullOrWhiteSpace(TaxUpperLimit))
        {
            if (!TryParseAmount(TaxUpperLimit, out var parsed))
            {
                ModalError = "Enter the upper limit as a number, or leave it blank for the top band.";
                return;
            }

            upper = parsed;
        }

        var result = await _statutory.SaveTaxBracketAsync(new WithholdingTaxBracket
        {
            Id = _taxTarget.Id,
            Frequency = _taxTarget.Frequency,
            IsAnnual = _taxTarget.IsAnnual,
            LowerLimit = lower,
            UpperLimit = upper,
            BaseTax = baseTax,
            RateOnExcessPercent = rate,
            EffectiveFrom = TaxEffectiveFrom
        }, performedBy);

        if (!result.Succeeded)
        {
            ModalError = result.Message;
            return;
        }

        IsTaxFormOpen = false;
        _taxTarget = null;

        await ReloadTaxBandsAsync();
        ShowStatus(result.Message);
    });

    // ========================== FR-045 company profile and payroll rules

    private async Task ReloadSettingsAsync()
    {
        var profile = await _config.GetCompanyProfileAsync();
        var settings = await _config.GetSettingsAsync();

        CompanyName = profile.RegisteredName;
        CompanyTradeName = profile.TradeName;
        CompanyAddress = profile.Address;
        CompanyTin = profile.TinDisplay;
        CompanyRdo = profile.RdoCode;
        CompanySssNumber = profile.SssEmployerNumber;
        CompanyPhilHealthNumber = profile.PhilHealthEmployerNumber;
        CompanyPagIbigNumber = profile.PagIbigEmployerNumber;
        CompanyContact = profile.ContactNumber;
        CompanyEmail = profile.EmailAddress;
        CompanySignatory = profile.AuthorisedSignatory;
        CompanySignatoryPosition = profile.SignatoryPosition;

        SettingsFrequency = FrequencyOptions.FirstOrDefault(o => o.Value == (int)settings.PayFrequency);
        SettingsContribution = ContributionOptions.FirstOrDefault(o => o.Value == (int)settings.ContributionSchedule);
        SettingsFactor = settings.WorkingDaysFactor.ToString("0.##");
        SettingsHoursPerDay = settings.StandardHoursPerDay.ToString("0.##");
        SettingsCutOffLead = settings.CutOffLeadDays.ToString(CultureInfo.InvariantCulture);
        SettingsPayDateLag = settings.PayDateLagDays.ToString(CultureInfo.InvariantCulture);
        SettingsAnnualiseTax = settings.AnnualiseTaxOnFinalPeriod;
        SettingsPayUnworkedHoliday = settings.PayUnworkedRegularHoliday;
        SettingsFlagNegativeNet = settings.FlagNegativeNetPay;
        SettingsAccrueSil = settings.AccrueServiceIncentiveLeave;
        SettingsSilDays = settings.ServiceIncentiveLeaveDays.ToString("0.##");
        SettingsSilDivisor = settings.ServiceIncentiveLeaveDivisor.ToString("0.##");
        SettingsPaper = PaperOptions.FirstOrDefault(o => o.Value == (int)settings.ReportPaper);

        var missing = profile.MissingForPayslip;

        CompanyWarning = missing.Count == 0
            ? string.Empty
            : $"A payslip cannot be printed honestly yet — still missing: {string.Join(", ", missing)}.";

        UpdateRatePreview();
    }

    private void ResetCompanyForm()
    {
        CompanyName = string.Empty;
        CompanyTradeName = string.Empty;
        CompanyAddress = string.Empty;
        CompanyTin = string.Empty;
        CompanyRdo = string.Empty;
        CompanySssNumber = string.Empty;
        CompanyPhilHealthNumber = string.Empty;
        CompanyPagIbigNumber = string.Empty;
        CompanyContact = string.Empty;
        CompanyEmail = string.Empty;
        CompanySignatory = string.Empty;
        CompanySignatoryPosition = string.Empty;

        SettingsFactor = "313";
        SettingsHoursPerDay = "8";
        SettingsCutOffLead = "5";
        SettingsPayDateLag = "0";
        SettingsAnnualiseTax = true;
        SettingsPayUnworkedHoliday = true;
        SettingsFlagNegativeNet = true;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCompanyCommand))]
    public partial string CompanyName { get; set; }

    [ObservableProperty]
    public partial string CompanyTradeName { get; set; }

    [ObservableProperty]
    public partial string CompanyAddress { get; set; }

    [ObservableProperty]
    public partial string CompanyTin { get; set; }

    [ObservableProperty]
    public partial string CompanyRdo { get; set; }

    [ObservableProperty]
    public partial string CompanySssNumber { get; set; }

    [ObservableProperty]
    public partial string CompanyPhilHealthNumber { get; set; }

    [ObservableProperty]
    public partial string CompanyPagIbigNumber { get; set; }

    [ObservableProperty]
    public partial string CompanyContact { get; set; }

    [ObservableProperty]
    public partial string CompanyEmail { get; set; }

    [ObservableProperty]
    public partial string CompanySignatory { get; set; }

    [ObservableProperty]
    public partial string CompanySignatoryPosition { get; set; }

    [ObservableProperty]
    public partial EnumOption? SettingsFrequency { get; set; }

    [ObservableProperty]
    public partial EnumOption? SettingsContribution { get; set; }

    [ObservableProperty]
    public partial string SettingsFactor { get; set; }

    partial void OnSettingsFactorChanged(string value) => UpdateRatePreview();

    [ObservableProperty]
    public partial string SettingsHoursPerDay { get; set; }

    partial void OnSettingsHoursPerDayChanged(string value) => UpdateRatePreview();

    [ObservableProperty]
    public partial string SettingsCutOffLead { get; set; }

    [ObservableProperty]
    public partial string SettingsPayDateLag { get; set; }

    [ObservableProperty]
    public partial bool SettingsAnnualiseTax { get; set; }

    [ObservableProperty]
    public partial bool SettingsPayUnworkedHoliday { get; set; }

    [ObservableProperty]
    public partial bool SettingsFlagNegativeNet { get; set; }

    /// <summary>
    /// Art. 95. Whether the five days of service incentive leave are accrued
    /// into every payslip against the days rendered — the legacy screen's
    /// <c>5Days Inc.</c> — instead of banked as credits and converted later.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SilPreview))]
    public partial bool SettingsAccrueSil { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SilPreview))]
    public partial string SettingsSilDays { get; set; } = "5";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SilPreview))]
    public partial string SettingsSilDivisor { get; set; } = "365";

    /// <summary>
    /// What the accrual actually pays, at the rate on the client's own sheet.
    /// The same reasoning as <see cref="RatePreview"/>: ₱600 a day is a figure
    /// somebody can check against a payslip they have seen.
    /// </summary>
    public string SilPreview
    {
        get
        {
            if (!SettingsAccrueSil)
                return "Off — the five days are banked as leave credits and converted on separation instead.";

            if (!TryParseAmount(SettingsSilDays, out var days) ||
                !TryParseAmount(SettingsSilDivisor, out var divisor) ||
                divisor <= 0m || days <= 0m)
            {
                return "Enter the days and the divisor as numbers.";
            }

            var perDay = PayrollRounding.Rate(600m * days / divisor);

            return $"At ₱600.00 a day that accrues {PayrollRounding.Format(perDay)} per day rendered — " +
                   $"{PayrollRounding.Format(PayrollRounding.Money(perDay * 13m))} for a 13-day cut-off.";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaperDetail))]
    public partial EnumOption? SettingsPaper { get; set; }

    /// <summary>What the chosen stock means for a wide report.</summary>
    public string PaperDetail => ReportPaperSizes.Detail(
        SettingsPaper?.As<ReportPaper>() ?? ReportPaper.DotMatrix11x14);

    [ObservableProperty]
    public partial string RatePreview { get; set; }

    /// <summary>
    /// What the factor actually does to a salary, recomputed as it is typed.
    /// ₱30,000 a month is a figure a payroll officer can check against a payslip
    /// they have seen, which an abstract divisor is not.
    /// </summary>
    private void UpdateRatePreview()
    {
        if (!TryParseAmount(SettingsFactor, out var factor) ||
            !TryParseAmount(SettingsHoursPerDay, out var hours) ||
            factor <= 0 || hours <= 0)
        {
            RatePreview = "Enter a factor and a standard day to see what they produce.";
            return;
        }

        var settings = new PayrollSettings { WorkingDaysFactor = factor, StandardHoursPerDay = hours };

        const decimal sample = 30000m;

        RatePreview =
            $"A ₱{sample:N0} monthly salary becomes ₱{settings.DailyRateFor(sample):N2} a day and " +
            $"₱{settings.HourlyRateFor(sample):N2} an hour. Every premium, tardiness deduction and " +
            "absence is priced off those two figures.";
    }

    private bool CanSaveCompany() => !string.IsNullOrWhiteSpace(CompanyName);

    [RelayCommand(CanExecute = nameof(CanSaveCompany))]
    private Task SaveCompanyAsync() => RunAsync(async () =>
    {
        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        var existing = await _config.GetCompanyProfileAsync();

        var profile = new CompanyProfile
        {
            RegisteredName = CompanyName,
            TradeName = CompanyTradeName,
            Address = CompanyAddress,
            Tin = CompanyTin,
            RdoCode = CompanyRdo,
            SssEmployerNumber = CompanySssNumber,
            PhilHealthEmployerNumber = CompanyPhilHealthNumber,
            PagIbigEmployerNumber = CompanyPagIbigNumber,
            ContactNumber = CompanyContact,
            EmailAddress = CompanyEmail,
            LogoPath = existing.LogoPath,
            AuthorisedSignatory = CompanySignatory,
            SignatoryPosition = CompanySignatoryPosition
        };

        var result = await _config.SaveCompanyProfileAsync(profile, performedBy);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        await ReloadSettingsAsync();
        ShowStatus(result.Message);
    });

    [RelayCommand]
    private Task SaveSettingsAsync() => RunAsync(async () =>
    {
        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        if (!TryParseAmount(SettingsFactor, out var factor) ||
            !TryParseAmount(SettingsHoursPerDay, out var hours))
        {
            ShowError("Enter the working-days factor and standard hours as numbers.");
            return;
        }

        if (!int.TryParse(SettingsCutOffLead, out var lead) ||
            !int.TryParse(SettingsPayDateLag, out var lag))
        {
            ShowError("Enter the cut-off lead and pay date lag as whole numbers of days.");
            return;
        }

        if (!TryParseAmount(SettingsSilDays, out var silDays) ||
            !TryParseAmount(SettingsSilDivisor, out var silDivisor))
        {
            ShowError("Enter the service incentive leave days and divisor as numbers.");
            return;
        }

        // Only checked when the accrual is on: a company that banks the credits
        // has no reason to keep a divisor that means anything.
        if (SettingsAccrueSil && (silDays <= 0m || silDivisor <= 0m))
        {
            ShowError("The service incentive leave days and divisor must both be greater than zero.");
            return;
        }

        var settings = new PayrollSettings
        {
            PayFrequency = SettingsFrequency?.As<PayFrequency>() ?? PayFrequency.SemiMonthly,
            WorkingDaysFactor = factor,
            StandardHoursPerDay = hours,
            CutOffLeadDays = lead,
            PayDateLagDays = lag,
            ContributionSchedule = SettingsContribution?.As<ContributionSchedule>()
                                   ?? ContributionSchedule.LastPayrollOfMonth,
            AnnualiseTaxOnFinalPeriod = SettingsAnnualiseTax,
            PayUnworkedRegularHoliday = SettingsPayUnworkedHoliday,
            FlagNegativeNetPay = SettingsFlagNegativeNet,
            AccrueServiceIncentiveLeave = SettingsAccrueSil,
            ServiceIncentiveLeaveDays = silDays,
            ServiceIncentiveLeaveDivisor = silDivisor,
            ReportPaper = SettingsPaper?.As<ReportPaper>() ?? ReportPaper.DotMatrix11x14
        };

        var result = await _config.SaveSettingsAsync(settings, performedBy);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        await ReloadSettingsAsync();
        ShowStatus(result.Message);
    });

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
            case ConfirmTarget.GenerateYear:
                {
                    var result = await _config.GenerateYearAsync(SelectedYear, performedBy);

                    if (!result.Succeeded)
                    {
                        ModalError = result.Message;
                        return;
                    }

                    IsConfirmOpen = false;
                    await ReloadCalendarAsync();
                    ShowStatus(result.Message);
                    break;
                }

            case ConfirmTarget.PayPeriodState:
                {
                    var status = _confirmActivates ? PayPeriodStatus.Open : PayPeriodStatus.Locked;
                    var result = await _config.SetPayPeriodStatusAsync(_confirmId, status, performedBy);

                    if (!result.Succeeded)
                    {
                        ModalError = result.Message;
                        return;
                    }

                    IsConfirmOpen = false;
                    await ReloadCalendarAsync();
                    ShowStatus(result.Message);
                    break;
                }

            case ConfirmTarget.EarningActive:
                {
                    var result = await _config.SetEarningTypeActiveAsync(_confirmId, _confirmActivates, performedBy);

                    if (!result.Succeeded)
                    {
                        ModalError = result.Message;
                        return;
                    }

                    IsConfirmOpen = false;
                    await ReloadComponentsAsync();
                    ShowStatus(result.Message);
                    break;
                }

            case ConfirmTarget.DeductionActive:
                {
                    var result = await _config.SetDeductionTypeActiveAsync(_confirmId, _confirmActivates, performedBy);

                    if (!result.Succeeded)
                    {
                        ModalError = result.Message;
                        return;
                    }

                    IsConfirmOpen = false;
                    await ReloadComponentsAsync();
                    ShowStatus(result.Message);
                    break;
                }
        }
    });

    // ==================================================================

    /// <summary>
    /// Parses a typed figure. Blank is zero — a cleared amount field means "no
    /// amount", not a validation failure. Both the current culture and the
    /// invariant one are tried, so a decimal point typed on a keyboard set to a
    /// comma locale is still understood.
    /// </summary>
    private static bool TryParseAmount(string? text, out decimal value)
    {
        value = 0m;

        if (string.IsNullOrWhiteSpace(text))
            return true;

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
               decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The date a superseding schedule should start on: tomorrow at the earliest,
    /// because a schedule that takes effect today would silently reprice a run
    /// computed this morning.
    /// </summary>
    private static DateTime NextEffectiveDate(DateTime? current)
    {
        var earliest = DateTime.Today.AddDays(1);

        return current is { } existing && existing.Date >= earliest
            ? existing.Date
            : earliest;
    }
}
