using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>
/// The multipliers one row is priced with, resolved once per load from the
/// premium matrix rather than written as constants here.
///
/// <para>Two of them are increments and one is not, for the same reason the
/// engine treats them that way: the day underneath a holiday or rest-day
/// premium is already paid by the days column, while overtime is worked beyond
/// the day and nothing covers it.</para>
/// </summary>
public sealed record TimesheetPricing(
    decimal HourlyRate,
    decimal OvertimeMultiplier,
    decimal SpecialIncrement,
    decimal LegalIncrement,
    decimal NightRate)
{
    public static TimesheetPricing None { get; } = new(0m, 0m, 0m, 0m, 0m);

    public static TimesheetPricing For(PremiumMatrix premiums, decimal dailyRate, decimal hoursPerDay)
    {
        if (dailyRate <= 0m || hoursPerDay <= 0m)
            return None;

        var hourly = PayrollRounding.Rate(dailyRate / hoursPerDay);

        return new TimesheetPricing(
            hourly,
            premiums.Get(PremiumCodes.OrdinaryOvertime)?.Multiplier ?? 0m,
            Increment(premiums.Get(PremiumCodes.SpecialNonWorking)?.Multiplier),
            Increment(premiums.Get(PremiumCodes.RegularHoliday)?.Multiplier),
            premiums.NightDifferentialRate);
    }

    private static decimal Increment(decimal? full) =>
        full is { } m && m > 1m ? m - 1m : 0m;
}

/// <summary>
/// One name on the sheet, with the seven figures the keyer types and the money
/// each of them makes.
///
/// <para><b>Every figure prices itself as it is typed.</b> That is not
/// decoration: it is the check the accounting staff already rely on in the
/// system this replaces, and it is how a slipped decimal point is caught before
/// the run rather than after the payslips print.</para>
/// </summary>
public sealed partial class TimesheetRow : ObservableObject
{
    private readonly PeriodTimesheet _sheet;
    private readonly Action _changed;

    public TimesheetRow(TimesheetLine line, TimesheetPricing pricing, Action changed)
    {
        Line = line;
        Pricing = pricing;
        _sheet = line.Sheet;
        _changed = changed;

        Days = Text(_sheet.Days);
        RegularOt = Text(_sheet.RegularOtHours);
        SpecialOt = Text(_sheet.SpecialHolidayOtHours);
        LegalOt = Text(_sheet.LegalHolidayOtHours);
        NightShift = Text(_sheet.NightShiftHours);
        CompanyLoan = Text(_sheet.CompanyLoan);
        Late = Text(_sheet.LateAmount);
        Note = _sheet.Note;

        WasEntered = _sheet.HasFigures;
    }

    public TimesheetLine Line { get; }

    public TimesheetPricing Pricing { get; }

    public int EmployeeId => Line.Employee.Id;

    public string EmployeeNumber => Line.EmployeeNumber;

    public string Name => Line.Name;

    /// <summary>Whether this row already had figures when the sheet was loaded.</summary>
    public bool WasEntered { get; }

    // ------------------------------------------------------- typed figures

    [ObservableProperty] public partial string Days { get; set; }

    [ObservableProperty] public partial string RegularOt { get; set; }

    [ObservableProperty] public partial string SpecialOt { get; set; }

    [ObservableProperty] public partial string LegalOt { get; set; }

    [ObservableProperty] public partial string NightShift { get; set; }

    [ObservableProperty] public partial string CompanyLoan { get; set; }

    [ObservableProperty] public partial string Late { get; set; }

    [ObservableProperty] public partial string Note { get; set; }

    partial void OnDaysChanged(string value) => Recalculate();

    partial void OnRegularOtChanged(string value) => Recalculate();

    partial void OnSpecialOtChanged(string value) => Recalculate();

    partial void OnLegalOtChanged(string value) => Recalculate();

    partial void OnNightShiftChanged(string value) => Recalculate();

    partial void OnCompanyLoanChanged(string value) => Recalculate();

    partial void OnLateChanged(string value) => Recalculate();

    // ------------------------------------------------------- priced figures

    public decimal DaysValue => Number(Days);

    public decimal RegularOtValue => Number(RegularOt);

    public decimal SpecialOtValue => Number(SpecialOt);

    public decimal LegalOtValue => Number(LegalOt);

    public decimal NightShiftValue => Number(NightShift);

    public decimal CompanyLoanValue => Number(CompanyLoan);

    public decimal LateValue => Number(Late);

    public decimal BasicAmount => PayrollRounding.Money(Line.DailyRate * DaysValue);

    public decimal RegularOtAmount =>
        PayrollRounding.Money(Pricing.HourlyRate * RegularOtValue * Pricing.OvertimeMultiplier);

    public decimal SpecialOtAmount =>
        PayrollRounding.Money(Pricing.HourlyRate * SpecialOtValue * Pricing.SpecialIncrement);

    public decimal LegalOtAmount =>
        PayrollRounding.Money(Pricing.HourlyRate * LegalOtValue * Pricing.LegalIncrement);

    public decimal NightAmount =>
        PayrollRounding.Money(Pricing.HourlyRate * NightShiftValue * Pricing.NightRate);

    /// <summary>
    /// Earnings less tardiness, which is where the client's own layout puts it
    /// and what the payroll summary already reports as gross.
    /// </summary>
    public decimal Gross => PayrollRounding.Money(
        BasicAmount + RegularOtAmount + SpecialOtAmount + LegalOtAmount + NightAmount - LateValue);

    /// <summary>
    /// Gross less the one deduction the sheet carries. <b>Not</b> net pay: SSS,
    /// PhilHealth, Pag-IBIG and withholding are read from the tables in force on
    /// the run's pay date and come off when the run is calculated. Naming it for
    /// what it is keeps anyone from reading it as a take-home figure.
    /// </summary>
    public decimal AfterSheetDeductions => PayrollRounding.Money(Gross - CompanyLoanValue);

    public string BasicDisplay => Money(BasicAmount);

    public string RegularOtDisplay => Money(RegularOtAmount);

    public string SpecialOtDisplay => Money(SpecialOtAmount);

    public string LegalOtDisplay => Money(LegalOtAmount);

    public string NightDisplay => Money(NightAmount);

    public string GrossDisplay => Money(Gross);

    public string AfterDeductionsDisplay => Money(AfterSheetDeductions);

    public string RateDisplay => Money(Line.DailyRate);

    public bool HasFigures =>
        DaysValue != 0m || RegularOtValue != 0m || SpecialOtValue != 0m || LegalOtValue != 0m ||
        NightShiftValue != 0m || CompanyLoanValue != 0m || LateValue != 0m;

    public bool IsBlank => !HasFigures;

    /// <summary>Anything that is not a number, so the grid can mark the cell before saving.</summary>
    public string Problem
    {
        get
        {
            foreach (var (text, label) in new[]
                     {
                         (Days, "days"), (RegularOt, "regular OT"), (SpecialOt, "special holiday OT"),
                         (LegalOt, "legal holiday OT"), (NightShift, "night shift"),
                         (CompanyLoan, "company loan"), (Late, "late")
                     })
            {
                if (!string.IsNullOrWhiteSpace(text) && !TryNumber(text, out _))
                    return $"The {label} figure is not a number.";
            }

            if (DaysValue > 31m)
                return "A cut-off cannot hold more than 31 days.";

            if (DaysValue < 0m || RegularOtValue < 0m || SpecialOtValue < 0m || LegalOtValue < 0m ||
                NightShiftValue < 0m || CompanyLoanValue < 0m || LateValue < 0m)
            {
                return "A figure cannot be negative.";
            }

            return string.Empty;
        }
    }

    public bool HasProblem => Problem.Length > 0;

    /// <summary>True when the typed figures differ from what is stored.</summary>
    public bool IsDirty =>
        _sheet.Days != DaysValue ||
        _sheet.RegularOtHours != RegularOtValue ||
        _sheet.SpecialHolidayOtHours != SpecialOtValue ||
        _sheet.LegalHolidayOtHours != LegalOtValue ||
        _sheet.NightShiftHours != NightShiftValue ||
        _sheet.CompanyLoan != CompanyLoanValue ||
        _sheet.LateAmount != LateValue ||
        !string.Equals(_sheet.Note, Note ?? string.Empty, StringComparison.Ordinal);

    /// <summary>The row as it will be stored.</summary>
    public PeriodTimesheet ToSheet()
    {
        var copy = _sheet.Clone();

        copy.Days = DaysValue;
        copy.RegularOtHours = RegularOtValue;
        copy.SpecialHolidayOtHours = SpecialOtValue;
        copy.LegalHolidayOtHours = LegalOtValue;
        copy.NightShiftHours = NightShiftValue;
        copy.CompanyLoan = CompanyLoanValue;
        copy.LateAmount = LateValue;
        copy.Note = Note ?? string.Empty;
        copy.RateWhenKeyed = Line.DailyRate;

        return copy;
    }

    private void Recalculate()
    {
        OnPropertyChanged(nameof(BasicDisplay));
        OnPropertyChanged(nameof(RegularOtDisplay));
        OnPropertyChanged(nameof(SpecialOtDisplay));
        OnPropertyChanged(nameof(LegalOtDisplay));
        OnPropertyChanged(nameof(NightDisplay));
        OnPropertyChanged(nameof(GrossDisplay));
        OnPropertyChanged(nameof(AfterDeductionsDisplay));
        OnPropertyChanged(nameof(HasFigures));
        OnPropertyChanged(nameof(IsBlank));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));

        _changed();
    }

    private static string Money(decimal value) => value.ToString("N2", CultureInfo.CurrentCulture);

    /// <summary>A zero shows as an empty box, the way the paper leaves it blank.</summary>
    private static string Text(decimal value) =>
        value == 0m ? string.Empty : value.ToString("0.##", CultureInfo.CurrentCulture);

    private static decimal Number(string? text) => TryNumber(text, out var value) ? value : 0m;

    private static bool TryNumber(string? text, out decimal value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = 0m;
            return true;
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value);
    }
}

/// <summary>One banded sheet: a detachment code, its rate, and the names under it.</summary>
public sealed partial class TimesheetBand : ObservableObject
{
    public TimesheetBand(TimesheetSheet sheet, IReadOnlyList<TimesheetRow> rows)
    {
        Sheet = sheet;
        Rows = new ObservableCollection<TimesheetRow>(rows);
    }

    public TimesheetSheet Sheet { get; }

    public ObservableCollection<TimesheetRow> Rows { get; }

    public int Id => Sheet.Detachment.Id;

    /// <summary>The band line as the paper writes it: "CS75 (600.00 per day)".</summary>
    public string Banner => Sheet.HasRate
        ? $"{Sheet.Detachment.Code}  ({Sheet.DailyRate:N2} per day)"
        : $"{Sheet.Detachment.Code}  (no rate posted)";

    public string Client => Sheet.Detachment.ClientDisplay;

    public bool HasRate => Sheet.HasRate;

    public bool HasNoRate => !Sheet.HasRate;

    public string Progress => $"{Rows.Count(r => r.HasFigures)} of {Rows.Count} keyed";

    public string GrossDisplay =>
        Rows.Sum(r => r.Gross).ToString("N2", CultureInfo.CurrentCulture);

    public void Refresh()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(GrossDisplay));
    }
}

/// <summary>
/// The client's cut-off sheet, keyed as it is written.
///
/// <para><b>This screen exists because the paper does.</b> Detachment staff
/// stand at a client's post and the client keeps the logbook; what reaches
/// accounting is a printed sheet — one client, one cut-off, banded by detachment
/// code, seven figures against each name. The layout here follows that sheet
/// deliberately, band for band and column for column, so a keyer can read down
/// the paper and across the screen without translating between them.</para>
///
/// <para><b>Every figure prices itself as it is typed</b>, and the band and the
/// sheet carry running totals, because that is the check the staff use to catch
/// a mis-key before the run rather than after the payslips print.</para>
///
/// <para>A sheet belongs to a <em>run</em>, and only a draft one can be keyed:
/// once a run is submitted its figures are what an approver is looking at.</para>
/// </summary>
public sealed partial class TimesheetsViewModel : BaseViewModel
{
    private readonly ITimesheetService _timesheets;
    private readonly IPayrollRunService _runs;
    private readonly IPayrollConfigService _config;

    private TimesheetRow? _rowToClear;

    public TimesheetsViewModel(
        ITimesheetService timesheets,
        IPayrollRunService runs,
        IPayrollConfigService config,
        ISessionService session)
        : base(session)
    {
        _timesheets = timesheets;
        _runs = runs;
        _config = config;

        Title = "Timesheets";

        Summary = string.Empty;
        ModalError = string.Empty;
        ConfirmTitle = string.Empty;
        ConfirmMessage = string.Empty;
        SheetTitle = "Timesheet";
        SheetSubtitle = "Choose a payroll run to key its sheets.";
    }

    public bool IsAnyModalOpen => IsCardOpen || IsConfirmOpen;

    public ObservableCollection<RunOption> Runs { get; } = new();

    public ObservableCollection<TimesheetBand> Bands { get; } = new();

    // ============================================================== listing

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial string SheetTitle { get; set; }

    [ObservableProperty]
    public partial string SheetSubtitle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoBands))]
    public partial bool HasBands { get; set; }

    public bool HasNoBands => !HasBands;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool HasChanges { get; set; }

    [ObservableProperty]
    public partial string TotalsLine { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRuns))]
    public partial RunOption? SelectedRun { get; set; }

    public bool HasNoRuns => Runs.Count == 0;

    partial void OnSelectedRunChanged(RunOption? value) => _ = RunAsync(LoadSheetsAsync);

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.RunPayroll, "key a timesheet"))
        {
            IsDenied = true;
            return;
        }

        IsDenied = false;

        await RunAsync(async () =>
        {
            // Only a draft run can be keyed. Once a run is submitted, the figures
            // an approver is looking at must not move underneath them.
            var runs = await _runs.GetRunsAsync(new PayrollRunQuery(Status: PayrollRunStatus.Draft));

            Runs.Clear();

            // A 13th month or adjustment run prices no time, so a sheet keyed
            // against one would be silently ignored.
            foreach (var run in runs
                         .Where(r => r.RunType is PayrollRunType.Regular or PayrollRunType.FinalPay)
                         .OrderByDescending(r => r.CutOffStart).ThenByDescending(r => r.Id))
                Runs.Add(new RunOption(run));

            OnPropertyChanged(nameof(HasNoRuns));

            Summary = Runs.Count switch
            {
                0 => "No draft payroll run is open. Create one on Payroll Runs, then key its sheets here.",
                1 => "1 draft run open.",
                _ => $"{Runs.Count} draft runs open."
            };

            SelectedRun = Runs.FirstOrDefault();

            if (SelectedRun is null)
            {
                Bands.Clear();
                HasBands = false;
                SheetSubtitle = "Choose a payroll run to key its sheets.";
            }
        });
    }

    private async Task LoadSheetsAsync()
    {
        Bands.Clear();
        HasChanges = false;
        TotalsLine = string.Empty;

        if (SelectedRun is null)
        {
            HasBands = false;
            return;
        }

        var run = SelectedRun.Run;

        SheetTitle = $"{run.ReferenceNumber} · {run.PeriodName}";
        SheetSubtitle = $"Period covered {run.CutOffStart:dd MMM yyyy} – {run.CutOffEnd:dd MMM yyyy} · " +
                        $"paid {run.PayDate:dd MMM yyyy}";

        // Read at the run's pay date, the same instant the engine will read them,
        // so what the keyer is shown is what they will be paid.
        var premiums = await _config.GetPremiumMatrixAsync(run.PayDate);
        var settings = await _config.GetSettingsAsync();

        var hoursPerDay = settings.StandardHoursPerDay <= 0m ? 8m : settings.StandardHoursPerDay;

        var sheets = await _timesheets.GetForRunAsync(run);

        foreach (var sheet in sheets)
        {
            var pricing = TimesheetPricing.For(premiums, sheet.DailyRate, hoursPerDay);

            var rows = sheet.Lines
                .Select(line => new TimesheetRow(line, pricing, OnRowChanged))
                .ToList();

            Bands.Add(new TimesheetBand(sheet, rows));
        }

        HasBands = Bands.Count > 0;

        if (!HasBands)
        {
            SheetSubtitle =
                "Nobody on this run is deployed to a detachment, so there is no client sheet to key. " +
                "Head-office staff are timed on Time & Attendance.";
        }

        RefreshTotals();
    }

    private void OnRowChanged()
    {
        HasChanges = Bands.Any(b => b.Rows.Any(r => r.IsDirty));
        RefreshTotals();
    }

    private void RefreshTotals()
    {
        foreach (var band in Bands)
            band.Refresh();

        var rows = Bands.SelectMany(b => b.Rows).ToList();
        var keyed = rows.Count(r => r.HasFigures);

        TotalsLine = rows.Count == 0
            ? string.Empty
            : $"{keyed} of {rows.Count} keyed · gross " +
              $"{rows.Sum(r => r.Gross).ToString("N2", CultureInfo.CurrentCulture)}";
    }

    // =============================================================== saving

    private bool CanSave() => HasChanges;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveAsync() => RunAsync(async () =>
    {
        ClearMessages();

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        var bad = Bands.SelectMany(b => b.Rows).FirstOrDefault(r => r.HasProblem);

        if (bad is not null)
        {
            ShowError($"{bad.Name}: {bad.Problem}");
            return;
        }

        var changed = Bands
            .SelectMany(b => b.Rows)
            .Where(r => r.IsDirty)
            .Select(r => r.ToSheet())
            .ToList();

        if (changed.Count == 0)
            return;

        var result = await _timesheets.SaveManyAsync(changed, performedBy);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        ShowStatus(result.Message);
        await LoadSheetsAsync();
    });

    [RelayCommand]
    private Task DiscardAsync() => RunAsync(LoadSheetsAsync);

    // ============================================================ the card

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsCardOpen { get; set; }

    [ObservableProperty]
    public partial TimesheetRow? CardRow { get; set; }

    [ObservableProperty]
    public partial string CardTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CardSubtitle { get; set; } = string.Empty;

    /// <summary>
    /// Opens the full card for one name — the same figures, priced line by line,
    /// for when a row is queried rather than merely typed.
    /// </summary>
    [RelayCommand]
    private void OpenCard(TimesheetRow? row)
    {
        if (row is null)
            return;

        ClearMessages();

        CardRow = row;
        CardTitle = $"{row.EmployeeNumber} · {row.Name}";
        CardSubtitle = $"Daily rate {row.RateDisplay} per day";
        IsCardOpen = true;
    }

    [RelayCommand]
    private void CloseCard()
    {
        IsCardOpen = false;
        CardRow = null;
        ModalError = string.Empty;
    }

    // ========================================================== confirming

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsConfirmOpen { get; set; }

    [ObservableProperty]
    public partial string ConfirmTitle { get; set; }

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; }

    [RelayCommand]
    private void AskClear(TimesheetRow? row)
    {
        if (row is null || !row.WasEntered)
            return;

        _rowToClear = row;

        ConfirmTitle = $"Clear {row.Name}";
        ConfirmMessage =
            $"Remove the keyed figures for {row.Name} on this run? They can be typed again, " +
            "and nothing else on the sheet is touched.";

        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelConfirm()
    {
        _rowToClear = null;
        IsConfirmOpen = false;
    }

    [RelayCommand]
    private Task ConfirmAsync() => RunAsync(async () =>
    {
        var row = _rowToClear;
        _rowToClear = null;
        IsConfirmOpen = false;

        var performedBy = Session.CurrentUser;
        if (row is null || performedBy is null || SelectedRun is null)
            return;

        var result = await _timesheets.ClearAsync(SelectedRun.Run.Id, row.EmployeeId, performedBy);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        ShowStatus(result.Message);
        await LoadSheetsAsync();
    });

    [ObservableProperty]
    public partial string ModalError { get; set; }
}

/// <summary>A draft run, as the picker names it.</summary>
public sealed class RunOption
{
    public RunOption(PayrollRun run) => Run = run;

    public PayrollRun Run { get; }

    public int Id => Run.Id;

    public string Label =>
        $"{Run.ReferenceNumber} · {Run.PeriodName} · " +
        $"{Run.CutOffStart:dd MMM} – {Run.CutOffEnd:dd MMM yyyy}";
}
