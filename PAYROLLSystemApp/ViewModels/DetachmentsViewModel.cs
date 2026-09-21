using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>A detachment as the list renders it, with how many people stand there.</summary>
public sealed class DetachmentRow
{
    public DetachmentRow(Detachment detachment, int headcount, int postedRates)
    {
        Detachment = detachment;
        Headcount = headcount;
        PostedRates = postedRates;
    }

    public Detachment Detachment { get; }

    public int Headcount { get; }

    public int PostedRates { get; }

    public int Id => Detachment.Id;

    public string Code => Detachment.Code;

    public string Name => Detachment.Name;

    public string RegionShort => LuzonRegionNames.Short(Detachment.Region);

    public bool IsActive => Detachment.IsActive;

    /// <summary>
    /// The two facts that decide whether payroll will run here: how many people
    /// are deployed, and whether any rate has been posted for them. A detachment
    /// with staff and no rates stops a run, so it says so on the row rather than
    /// only when the run fails.
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string> { Detachment.RegionDisplay };

            if (!Detachment.IsActive)
                parts.Add("retired");

            parts.Add(Headcount == 1 ? "1 employee" : $"{Headcount} employees");

            parts.Add(PostedRates switch
            {
                0 => "no rate posted",
                1 => "1 rate posted",
                _ => $"{PostedRates} rates posted"
            });

            if (!string.IsNullOrWhiteSpace(Detachment.Location))
                parts.Add(Detachment.Location);

            return string.Join(" · ", parts);
        }
    }

    public bool NeedsRates => Headcount > 0 && PostedRates == 0;

    public string ActionText => Detachment.IsActive ? "Retire" : "Restore";
}

/// <summary>One posted rate, as the rate list renders it.</summary>
public sealed class RateRow
{
    public RateRow(PostedRate posted)
    {
        Posted = posted;
    }

    public PostedRate Posted { get; }

    public int Id => Posted.Rate.Id;

    /// <summary>
    /// The heading for the row. A rate is identified by the day it starts —
    /// there is no position to name it by any more — so that is what leads, and
    /// the row in force says so rather than leaving the reader to compare dates.
    /// </summary>
    public string Position => IsSuperseded
        ? $"From {Posted.Rate.EffectiveFrom:dd MMM yyyy}"
        : $"From {Posted.Rate.EffectiveFrom:dd MMM yyyy} · in force";

    public string Amount => Posted.Rate.DailyRate.ToString("N2", CultureInfo.CurrentCulture);

    public string Effective => Posted.Rate.EffectiveDisplay;

    public bool IsSuperseded => Posted.IsSuperseded;

    public string Detail
    {
        get
        {
            var parts = new List<string>();

            if (IsSuperseded)
                parts.Add("superseded by a later rate");

            if (!string.IsNullOrWhiteSpace(Posted.Rate.Reference))
                parts.Add(Posted.Rate.Reference);

            return parts.Count == 0 ? "Per day" : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// FR-012, FR-013. The posts employees are deployed to across Luzon, and the
/// daily rate each position is paid at each of them.
///
/// <para><b>This screen is where a guard's wage is set.</b> The rate lives on
/// the (detachment, position) pair rather than on the person, because a regional
/// wage order moves everyone at a post at once. Selecting a detachment on the
/// left shows its rate table on the right.</para>
///
/// <para><b>A rate is posted, never edited.</b> Each row carries the date it
/// starts, and a run reads whichever was in force on its pay date — so entering
/// next quarter's wage order today does not change this fortnight's payroll.
/// Posting a rate for a date that already has one corrects that row instead of
/// creating an ambiguous second.</para>
/// </summary>
public sealed partial class DetachmentsViewModel : BaseViewModel
{
    private readonly IDetachmentService _detachments;
    private readonly IOrganizationService _organization;

    private Detachment? _target;
    private int? _rateToWithdraw;

    public DetachmentsViewModel(
        IDetachmentService detachments, IOrganizationService organization, ISessionService session)
        : base(session)
    {
        _detachments = detachments;
        _organization = organization;

        Title = "Detachments";

        Summary = string.Empty;
        ModalError = string.Empty;
        FormTitle = string.Empty;
        RatesTitle = "Rates";
        RatesSubtitle = "Choose a detachment to see the rates posted for it.";
        ConfirmTitle = string.Empty;
        ConfirmMessage = string.Empty;

        RegionOptions = EnumOption.From<LuzonRegion>(LuzonRegionNames.Display);

        ResetForm();
        ResetRateForm();
    }

    public bool IsAnyModalOpen => IsFormOpen || IsRateFormOpen || IsConfirmOpen;

    public ObservableCollection<DetachmentRow> Detachments { get; } = new();

    public ObservableCollection<RateRow> Rates { get; } = new();

    public ObservableCollection<LookupOption> PositionOptions { get; } = new();

    public IReadOnlyList<EnumOption> RegionOptions { get; }

    // ============================================================= listing

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    [ObservableProperty]
    public partial string Summary { get; set; }

    [ObservableProperty]
    public partial bool ShowRetired { get; set; }

    partial void OnShowRetiredChanged(bool value) => _ = RunAsync(ReloadAsync);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoDetachments))]
    public partial bool HasDetachments { get; set; }

    public bool HasNoDetachments => !HasDetachments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRates))]
    [NotifyPropertyChangedFor(nameof(CanAddRate))]
    public partial DetachmentRow? SelectedDetachment { get; set; }

    partial void OnSelectedDetachmentChanged(DetachmentRow? value) => _ = RunAsync(LoadRatesAsync);

    public bool CanAddRate => SelectedDetachment is not null;

    [ObservableProperty]
    public partial string RatesTitle { get; set; }

    [ObservableProperty]
    public partial string RatesSubtitle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRates))]
    public partial bool HasRates { get; set; }

    public bool HasNoRates => !HasRates;

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.ManageEmployees, "open detachments"))
        {
            IsDenied = true;
            Detachments.Clear();
            Rates.Clear();
            return;
        }

        IsDenied = false;

        await RunAsync(async () =>
        {
            PositionOptions.Clear();
            foreach (var position in await _organization.GetPositionsAsync())
                PositionOptions.Add(new LookupOption(position.Id, position.Title));

            await ReloadAsync();
        });
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        var all = await _detachments.GetAllAsync(includeInactive: true);
        var headcounts = await _detachments.GetHeadcountsAsync();

        var visible = all.Where(d => ShowRetired || d.IsActive).ToList();

        // One read of the rate rows for the whole list, rather than one per
        // detachment: the count is on every row and the list can be long.
        var counts = new Dictionary<int, int>();

        foreach (var detachment in visible)
        {
            var rates = await _detachments.GetRatesAsync(detachment.Id);
            counts[detachment.Id] = rates.Count(r => !r.IsSuperseded);
        }

        var keepId = SelectedDetachment?.Id;

        Detachments.Clear();
        foreach (var detachment in visible)
        {
            Detachments.Add(new DetachmentRow(
                detachment,
                headcounts.TryGetValue(detachment.Id, out var people) ? people : 0,
                counts.GetValueOrDefault(detachment.Id)));
        }

        HasDetachments = Detachments.Count > 0;

        Summary = all.Count == visible.Count
            ? $"{all.Count} detachment(s)"
            : $"{visible.Count} of {all.Count} detachment(s)";

        SelectedDetachment = Detachments.FirstOrDefault(d => d.Id == keepId)
                             ?? Detachments.FirstOrDefault();

        await LoadRatesAsync();
    }

    private async Task LoadRatesAsync()
    {
        Rates.Clear();

        if (SelectedDetachment is null)
        {
            RatesTitle = "Rates";
            RatesSubtitle = "Choose a detachment to see the rates posted for it.";
            HasRates = false;
            return;
        }

        RatesTitle = $"Rates — {SelectedDetachment.Code}";
        RatesSubtitle = $"{SelectedDetachment.Name} · {SelectedDetachment.Detachment.RegionDisplay}";

        foreach (var posted in await _detachments.GetRatesAsync(SelectedDetachment.Id))
            Rates.Add(new RateRow(posted));

        HasRates = Rates.Count > 0;
    }

    // ======================================================== detachment form

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsFormOpen { get; set; }

    [ObservableProperty]
    public partial string FormTitle { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string FormCode { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    public partial string FormName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormClient { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormGroupCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial EnumOption? FormRegion { get; set; }

    [ObservableProperty]
    public partial string FormLocation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FormDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModalError { get; set; }

    [RelayCommand]
    private void OpenCreate()
    {
        ClearMessages();
        ResetForm();

        _target = null;
        FormTitle = "New detachment";
        IsFormOpen = true;
    }

    [RelayCommand]
    private void OpenEdit(DetachmentRow? row)
    {
        if (row is null)
            return;

        ClearMessages();
        ResetForm();

        _target = row.Detachment;
        FormTitle = $"Edit {row.Code}";
        FormCode = row.Detachment.Code;
        FormName = row.Detachment.Name;
        FormClient = row.Detachment.ClientName;
        FormGroupCode = row.Detachment.GroupCode;
        FormRegion = RegionOptions.FirstOrDefault(o => o.Value == (int)row.Detachment.Region);
        FormLocation = row.Detachment.Location;
        FormDescription = row.Detachment.Description;

        IsFormOpen = true;
    }

    [RelayCommand]
    private void CloseForm()
    {
        IsFormOpen = false;
        ModalError = string.Empty;
        _target = null;
    }

    private bool CanSubmit() =>
        !string.IsNullOrWhiteSpace(FormCode) && !string.IsNullOrWhiteSpace(FormName);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private Task SubmitAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrCode = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        var detachment = new Detachment
        {
            Id = _target?.Id ?? 0,
            Code = FormCode,
            Name = FormName,
            ClientName = FormClient,
            GroupCode = FormGroupCode,
            Region = FormRegion?.As<LuzonRegion>() ?? LuzonRegion.NationalCapitalRegion,
            Location = FormLocation,
            Description = FormDescription
        };

        var result = await _detachments.SaveAsync(detachment, performedBy);

        if (!result.Succeeded)
        {
            if (result.Message.Contains("already used", StringComparison.OrdinalIgnoreCase))
                ErrCode = result.Message;
            else
                ModalError = result.Message;

            return;
        }

        IsFormOpen = false;
        _target = null;

        ShowStatus(result.Message);
        await ReloadAsync();
    });

    private void ResetForm()
    {
        FormCode = string.Empty;
        FormName = string.Empty;
        FormClient = string.Empty;
        FormGroupCode = string.Empty;
        FormRegion = RegionOptions.FirstOrDefault();
        FormLocation = string.Empty;
        FormDescription = string.Empty;
        ErrCode = string.Empty;
    }

    // ============================================================== rate form

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsRateFormOpen { get; set; }

    [ObservableProperty]
    public partial string RateFormTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitRateCommand))]
    public partial string RateAmount { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTime RateEffective { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial string RateReference { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrRateAmount { get; set; } = string.Empty;

    [RelayCommand]
    private void OpenAddRate()
    {
        if (SelectedDetachment is null)
            return;

        ClearMessages();
        ResetRateForm();

        RateFormTitle = $"Post a rate — {SelectedDetachment.Code}";
        IsRateFormOpen = true;
    }

    [RelayCommand]
    private void CloseRateForm()
    {
        IsRateFormOpen = false;
        ModalError = string.Empty;
    }

    private bool CanSubmitRate() => !string.IsNullOrWhiteSpace(RateAmount);

    [RelayCommand(CanExecute = nameof(CanSubmitRate))]
    private Task SubmitRateAsync() => RunAsync(async () =>
    {
        ModalError = string.Empty;
        ErrRateAmount = string.Empty;

        var performedBy = Session.CurrentUser;
        if (performedBy is null || SelectedDetachment is null)
            return;

        if (!decimal.TryParse(RateAmount, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount))
        {
            ErrRateAmount = "Enter the daily rate as a number.";
            return;
        }

        var result = await _detachments.SaveRateAsync(new DetachmentRate
        {
            DetachmentId = SelectedDetachment.Id,
            DailyRate = amount,
            EffectiveFrom = RateEffective,
            Reference = RateReference
        }, performedBy);

        if (!result.Succeeded)
        {
            if (result.Message.Contains("daily rate", StringComparison.OrdinalIgnoreCase))
                ErrRateAmount = result.Message;
            else
                ModalError = result.Message;

            return;
        }

        IsRateFormOpen = false;
        ShowStatus(result.Message);
        await ReloadAsync();
    });

    private void ResetRateForm()
    {
        RateAmount = string.Empty;
        RateEffective = DateTime.Today;
        RateReference = string.Empty;
        ErrRateAmount = string.Empty;
    }

    // ============================================================ confirming

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnyModalOpen))]
    public partial bool IsConfirmOpen { get; set; }

    [ObservableProperty]
    public partial string ConfirmTitle { get; set; }

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; }

    [ObservableProperty]
    public partial string ConfirmAction { get; set; } = "Confirm";

    private bool _confirmActivates;

    [RelayCommand]
    private void OpenToggle(DetachmentRow? row)
    {
        if (row is null)
            return;

        ClearMessages();

        _target = row.Detachment;
        _rateToWithdraw = null;
        _confirmActivates = !row.IsActive;

        ConfirmTitle = _confirmActivates ? $"Restore {row.Code}?" : $"Retire {row.Code}?";
        ConfirmAction = _confirmActivates ? "Restore" : "Retire";

        ConfirmMessage = _confirmActivates
            ? $"{row.Name} will appear again when assigning employees."
            : $"{row.Name} will be hidden from the pickers. Payslips already earned there keep " +
              "its name, and its posted rates are kept so an earlier run can still be recomputed.";

        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void OpenWithdrawRate(RateRow? row)
    {
        if (row is null)
            return;

        ClearMessages();

        _target = null;
        _rateToWithdraw = row.Id;

        ConfirmTitle = "Withdraw this rate?";
        ConfirmAction = "Withdraw";
        ConfirmMessage =
            $"{row.Position} at {row.Amount} per day, {row.Effective}. Runs already posted keep the " +
            "figures they were computed with, but recomputing an earlier run will fall back to the " +
            "rate before this one — or fail if there is none.";

        IsConfirmOpen = true;
    }

    [RelayCommand]
    private void CloseConfirm()
    {
        IsConfirmOpen = false;
        _rateToWithdraw = null;
    }

    [RelayCommand]
    private Task ConfirmAsync() => RunAsync(async () =>
    {
        var performedBy = Session.CurrentUser;
        if (performedBy is null)
            return;

        if (_rateToWithdraw is { } rateId)
        {
            var withdrawn = await _detachments.WithdrawRateAsync(rateId, performedBy);

            IsConfirmOpen = false;
            _rateToWithdraw = null;

            if (withdrawn.Succeeded)
                ShowStatus(withdrawn.Message);
            else
                ShowError(withdrawn.Message);

            await ReloadAsync();
            return;
        }

        if (_target is null)
            return;

        var result = await _detachments.SetActiveAsync(_target.Id, _confirmActivates, performedBy);

        IsConfirmOpen = false;
        _target = null;

        if (result.Succeeded)
            ShowStatus(result.Message);
        else
            ShowError(result.Message);

        await ReloadAsync();
    });
}
