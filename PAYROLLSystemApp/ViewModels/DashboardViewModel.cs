using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>A headline figure on the dashboard.</summary>
public sealed record StatCard(string Caption, string Value, string Detail);

/// <summary>
/// Something in the payroll cycle that is waiting on the person signed in, and
/// the screen where it is dealt with. It carries its own command, the same way
/// a sidebar entry does, so the template needs no RelativeSource.
/// </summary>
public sealed class AttentionItem
{
    public AttentionItem(string title, string detail, bool isUrgent, string actionText, Action go)
    {
        Title = title;
        Detail = detail;
        IsUrgent = isUrgent;
        ActionText = actionText;
        GoCommand = new RelayCommand(go);
    }

    public string Title { get; }

    public string Detail { get; }

    /// <summary>Urgent items stop a payroll from being paid; the rest are reminders.</summary>
    public bool IsUrgent { get; }

    public string ActionText { get; }

    public IRelayCommand GoCommand { get; }
}

/// <summary>A shortcut to a section the signed-in role can open.</summary>
public sealed class QuickAction
{
    public QuickAction(string text, Action go)
    {
        Text = text;
        GoCommand = new RelayCommand(go);
    }

    public string Text { get; }

    public IRelayCommand GoCommand { get; }
}

/// <summary>
/// The first screen after sign-in. It answers one question — what needs doing
/// now — by reading the same records the payroll screens work on, and sends
/// each item to the screen that resolves it.
/// </summary>
public sealed partial class DashboardViewModel : BaseViewModel
{
    private readonly IAppNavigator _navigator;
    private readonly IAuditService _audit;
    private readonly IEmployeeService _employees;
    private readonly IDetachmentService _detachments;
    private readonly IPayrollRunService _runs;
    private readonly IPayrollConfigService _config;
    private readonly ILeaveService _leave;
    private readonly IDataManagementService _data;

    public DashboardViewModel(
        ISessionService session,
        IAppNavigator navigator,
        IAuditService audit,
        IEmployeeService employees,
        IDetachmentService detachments,
        IPayrollRunService runs,
        IPayrollConfigService config,
        ILeaveService leave,
        IDataManagementService data)
        : base(session)
    {
        _navigator = navigator;
        _audit = audit;
        _employees = employees;
        _detachments = detachments;
        _runs = runs;
        _config = config;
        _leave = leave;
        _data = data;

        Title = "Dashboard";
        Greeting = string.Empty;
        LastLoginText = string.Empty;
    }

    public ObservableCollection<StatCard> Stats { get; } = new();

    public ObservableCollection<AttentionItem> Attention { get; } = new();

    public ObservableCollection<QuickAction> QuickActions { get; } = new();

    public ObservableCollection<AuditEntry> RecentActivity { get; } = new();

    [ObservableProperty]
    public partial string Greeting { get; set; }

    [ObservableProperty]
    public partial string LastLoginText { get; set; }

    [ObservableProperty]
    public partial bool ShowActivity { get; set; }

    [ObservableProperty]
    public partial bool IsAllClear { get; set; }

    public Task LoadAsync() => RunAsync(async () =>
    {
        var user = Session.CurrentUser;
        if (user is null)
            return;

        var displayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;
        Greeting = $"{TimeOfDayGreeting()}, {displayName.Split(' ')[0]}";

        LastLoginText = user.LastLoginUtc is { } last
            ? DateTime.SpecifyKind(last, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy, h:mm tt")
            : "First sign-in";

        var runs = Session.Has(Permission.RunPayroll) || Session.Has(Permission.ApprovePayroll)
            ? await _runs.GetRunsAsync(new PayrollRunQuery())
            : [];

        await BuildStatsAsync(runs);
        await BuildAttentionAsync(runs);
        BuildQuickActions();

        ShowActivity = Session.Has(Permission.ViewAuditLog);
        if (ShowActivity)
            await LoadActivityAsync();
    });

    // ================================================================ figures

    private async Task BuildStatsAsync(IReadOnlyList<PayrollRun> runs)
    {
        Stats.Clear();

        if (Session.Has(Permission.ManageEmployees))
        {
            var stats = await _employees.GetStatisticsAsync();
            Stats.Add(new StatCard("Active employees", stats.Active.ToString("N0"),
                $"{stats.Probationary} on probation"));
        }

        if (runs.Count > 0 || Session.Has(Permission.RunPayroll))
        {
            var drafts = runs.Count(r => r.Status == PayrollRunStatus.Draft);
            var waiting = runs.Count(r => r.Status == PayrollRunStatus.ForApproval);
            var approved = runs.Count(r => r.Status == PayrollRunStatus.Approved);

            Stats.Add(new StatCard("Draft runs", drafts.ToString(), "Being prepared by Accounting"));
            Stats.Add(new StatCard("Awaiting approval", waiting.ToString(),
                approved == 0 ? "None approved and unposted" : $"{approved} approved, not yet posted"));

            var lastPosted = runs
                .Where(r => r.Status == PayrollRunStatus.Posted && r.PostedUtc is not null)
                .OrderByDescending(r => r.PostedUtc)
                .FirstOrDefault();

            Stats.Add(new StatCard("Last posted run",
                lastPosted?.PeriodCode ?? "None yet",
                lastPosted is null ? "Nothing has been paid" : lastPosted.ReferenceNumber));
        }

        if (Session.Has(Permission.ManageBackups))
        {
            var backup = await _data.GetSettingsAsync();
            Stats.Add(new StatCard("Last backup", backup.LastBackupDisplay,
                backup.IsAutomaticEnabled ? $"Automatic every {backup.IntervalDays} day(s)" : "Automatic backup is off"));
        }
        else
        {
            Stats.Add(new StatCard("Last sign-in", LastLoginText, "Recorded in the audit log"));
        }
    }

    // ======================================================= needs attention

    private async Task BuildAttentionAsync(IReadOnlyList<PayrollRun> runs)
    {
        Attention.Clear();

        if (Session.Has(Permission.ApprovePayroll))
            AddApprovalItems(runs);

        if (Session.Has(Permission.RunPayroll))
        {
            await AddMissingRateItemAsync();
            AddDraftItems(runs);
            await AddUnrunPeriodItemAsync(runs);

            var waiting = runs.Count(r => r.Status == PayrollRunStatus.ForApproval);
            if (waiting > 0 && !Session.Has(Permission.ApprovePayroll))
            {
                Attention.Add(new AttentionItem(
                    $"{waiting} run(s) with the Administrator",
                    "Submitted and waiting for approval. Nothing to do here unless one is returned to draft.",
                    false, "View runs", () => Go(AppSection.PayrollRuns)));
            }
        }

        if (Session.Has(Permission.ManageLeave))
            await AddLeaveItemAsync();

        if (Session.Has(Permission.ManageBackups))
            await AddBackupItemAsync(runs);

        IsAllClear = Attention.Count == 0;
    }

    private void AddApprovalItems(IReadOnlyList<PayrollRun> runs)
    {
        var waiting = runs.Where(r => r.Status == PayrollRunStatus.ForApproval).ToList();
        if (waiting.Count > 0)
        {
            Attention.Add(new AttentionItem(
                $"{waiting.Count} run(s) waiting for your approval",
                string.Join(", ", waiting.Take(4).Select(r => r.ReferenceNumber)) + (waiting.Count > 4 ? ", …" : string.Empty),
                true, "Review", () => Go(AppSection.Approvals)));
        }

        var approved = runs.Where(r => r.Status == PayrollRunStatus.Approved).ToList();
        if (approved.Count > 0)
        {
            Attention.Add(new AttentionItem(
                $"{approved.Count} approved run(s) not yet posted",
                "Payslips and reports appear, and loan balances move, only once a run is posted.",
                true, "Post", () => Go(AppSection.Approvals)));
        }
    }

    /// <summary>
    /// A detachment with guards on it and no rate in force blocks every run
    /// they are on, so it is worth knowing before the cut-off, not after.
    /// </summary>
    private async Task AddMissingRateItemAsync()
    {
        var staffed = (await _employees.GetAllAsync())
            .Where(e => e.IsActive && !e.IsArchived && !e.IsSeparated && !e.UsesOwnRate && e.DetachmentId is not null)
            .Select(e => e.DetachmentId!.Value)
            .ToHashSet();

        if (staffed.Count == 0)
            return;

        var rates = await _detachments.GetRateTableAsync(DateTime.Today);
        var missing = (await _detachments.GetAllAsync())
            .Where(d => staffed.Contains(d.Id) && rates.Get(d.Id) is null)
            .Select(d => d.Code)
            .ToList();

        if (missing.Count == 0)
            return;

        Attention.Add(new AttentionItem(
            $"{missing.Count} detachment(s) with guards but no daily rate",
            string.Join(", ", missing.Take(6)) + (missing.Count > 6 ? ", …" : string.Empty) +
            " — any run that includes their guards will be blocked.",
            true, "Post a rate", () => Go(AppSection.Detachments)));
    }

    private void AddDraftItems(IReadOnlyList<PayrollRun> runs)
    {
        var drafts = runs.Where(r => r.Status == PayrollRunStatus.Draft).ToList();
        if (drafts.Count == 0)
            return;

        var uncalculated = drafts.Count(r => !r.HasBeenCalculated);
        var flagged = drafts.Where(r => r.HasBeenCalculated && r.ExceptionCount > 0).ToList();
        var ready = drafts.Count - uncalculated - flagged.Count;

        var parts = new List<string>();
        if (uncalculated > 0) parts.Add($"{uncalculated} not calculated yet");
        if (flagged.Count > 0) parts.Add($"{flagged.Count} with payslips flagged for review");
        if (ready > 0) parts.Add($"{ready} ready to submit");

        Attention.Add(new AttentionItem(
            $"{drafts.Count} draft run(s) in progress",
            string.Join(" · ", parts),
            flagged.Count > 0, "Open runs", () => Go(AppSection.PayrollRuns)));
    }

    /// <summary>A cut-off that has closed with nobody paid for it yet.</summary>
    private async Task AddUnrunPeriodItemAsync(IReadOnlyList<PayrollRun> runs)
    {
        var today = DateTime.Today;
        var periods = (await _config.GetPayPeriodsAsync(today.Year)).ToList();
        if (today.Month == 1)
            periods.AddRange(await _config.GetPayPeriodsAsync(today.Year - 1));

        var covered = runs
            .Where(r => r.RunType == PayrollRunType.Regular && r.Status != PayrollRunStatus.Cancelled)
            .Select(r => r.PayPeriodId)
            .ToHashSet();

        var due = periods
            .Where(p => p.Status != PayPeriodStatus.Closed && p.CutOffEnd.Date < today && !covered.Contains(p.Id))
            .OrderBy(p => p.PayDate)
            .ToList();

        if (due.Count == 0)
            return;

        var next = due[0];

        Attention.Add(new AttentionItem(
            due.Count == 1
                ? $"{next.Code}: cut-off closed, no payroll run yet"
                : $"{due.Count} closed cut-offs with no payroll run",
            $"Earliest is {next.Code}, paid on {next.PayDate:dd MMM yyyy}.",
            next.PayDate.Date <= today.AddDays(3), "New run", () => Go(AppSection.PayrollRuns)));
    }

    private async Task AddLeaveItemAsync()
    {
        var pending = await _leave.GetRequestsAsync(new LeaveRequestQuery(Status: LeaveRequestStatus.Pending));
        if (pending.Count == 0)
            return;

        Attention.Add(new AttentionItem(
            $"{pending.Count} leave request(s) to decide",
            "Leave is paid only once it is approved, so decide these before the run is calculated.",
            false, "Decide", () => Go(AppSection.Leave)));
    }

    private async Task AddBackupItemAsync(IReadOnlyList<PayrollRun> runs)
    {
        var settings = await _data.GetSettingsAsync();

        var lastPosted = runs
            .Where(r => r.Status == PayrollRunStatus.Posted && r.PostedUtc is not null)
            .Max(r => r.PostedUtc);

        var stale = lastPosted is { } posted && (settings.LastBackupUtc is not { } backup || backup < posted);

        if (!stale && !settings.IsDue)
            return;

        Attention.Add(new AttentionItem(
            stale ? "No backup since the last posted run" : "A scheduled backup is due",
            $"Last backup: {settings.LastBackupDisplay}. A posted payroll exists only in this database until it is backed up.",
            stale, "Back up", () => Go(AppSection.DataManagement)));
    }

    // ========================================================= quick actions

    private void BuildQuickActions()
    {
        QuickActions.Clear();

        void Offer(string text, AppSection section)
        {
            if (Session.Has(AppSections.Get(section).RequiredPermission))
                QuickActions.Add(new QuickAction(text, () => Go(section)));
        }

        Offer("Payroll runs", AppSection.PayrollRuns);
        Offer("Key timesheets", AppSection.Timesheets);
        Offer("Time & attendance", AppSection.Attendance);
        Offer("Approvals", AppSection.Approvals);
        Offer("Payslips", AppSection.Payslips);
        Offer("Reports", AppSection.Reports);
        Offer("Employees", AppSection.Employees);
        Offer("User accounts", AppSection.Users);
        Offer("Backup", AppSection.DataManagement);
    }

    private void Go(AppSection section) => _navigator.NavigateTo(section);

    private async Task LoadActivityAsync()
    {
        var entries = await _audit.GetRecentAsync(8);

        RecentActivity.Clear();
        foreach (var entry in entries)
            RecentActivity.Add(entry);
    }

    private static string TimeOfDayGreeting() => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening"
    };
}
