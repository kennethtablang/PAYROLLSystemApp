using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>A headline figure on the dashboard.</summary>
public sealed record StatCard(string Caption, string Value, string Detail);

/// <summary>
/// One stage of the payroll cycle, with its build status. The status colours
/// are chosen in XAML by DataTrigger so they follow the light/dark theme.
/// </summary>
public sealed record CycleStage(string Name, string Detail, bool IsReady, bool IsPermitted)
{
    public string StatusText => !IsPermitted ? "No access" : IsReady ? "Ready" : "Pending";
}

public sealed partial class DashboardViewModel : BaseViewModel
{
    private readonly IAppNavigator _navigator;
    private readonly IAuditService _audit;

    public DashboardViewModel(
        ISessionService session,
        IAppNavigator navigator,
        IAuditService audit)
        : base(session)
    {
        _navigator = navigator;
        _audit = audit;

        Title = "Dashboard";
        Greeting = string.Empty;
        LastLoginText = string.Empty;
    }

    public ObservableCollection<StatCard> Stats { get; } = new();

    public ObservableCollection<CycleStage> Cycle { get; } = new();

    public ObservableCollection<string> Permissions { get; } = new();

    public ObservableCollection<AuditEntry> RecentActivity { get; } = new();

    [ObservableProperty]
    public partial string Greeting { get; set; }

    [ObservableProperty]
    public partial string LastLoginText { get; set; }

    [ObservableProperty]
    public partial bool ShowActivity { get; set; }

    [RelayCommand]
    private void OpenUsers()
    {
        _navigator.NavigateTo(AppSection.Users);
    }

    [RelayCommand]
    private void OpenAudit()
    {
        _navigator.NavigateTo(AppSection.AuditLog);
    }

    public async Task LoadAsync()
    {
        var user = Session.CurrentUser;
        if (user is null)
            return;

        var displayName = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;
        Greeting = $"{TimeOfDayGreeting()}, {displayName.Split(' ')[0]}";

        LastLoginText = user.LastLoginUtc is { } last
            ? DateTime.SpecifyKind(last, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy, h:mm tt")
            : "First sign-in";

        BuildStats(user);
        BuildCycle();
        BuildPermissions(user);

        ShowActivity = Session.Has(Permission.ViewAuditLog);
        if (ShowActivity)
            await LoadActivityAsync();
    }

    private void BuildStats(User user)
    {
        var permitted = AppSections.All.Count(s => Session.Has(s.RequiredPermission));
        var ready = AppSections.All.Count(s => s.IsImplemented && Session.Has(s.RequiredPermission));

        Stats.Clear();
        Stats.Add(new StatCard("Your role", user.RoleDisplayName, $"{RolePermissions.For(user.Role).Count} permissions granted"));
        Stats.Add(new StatCard("Modules available", permitted.ToString(), $"{ready} ready to use now"));
        Stats.Add(new StatCard("Last sign-in", LastLoginText, "Recorded in the audit log"));
    }

    /// <summary>
    /// Mirrors the payroll cycle from section 1.2 of the requirements, so the
    /// dashboard shows where the build has actually reached.
    /// </summary>
    private void BuildCycle()
    {
        Cycle.Clear();
        Cycle.Add(new CycleStage("Access control", "Sign-in, roles, audit trail",
            true, true));
        Cycle.Add(new CycleStage("Employee records", "Onboard and maintain employees",
            true, Session.Has(Permission.ManageEmployees)));
        Cycle.Add(new CycleStage("Attendance capture", "Daily time records and overtime",
            false, Session.Has(Permission.ManageAttendance)));
        Cycle.Add(new CycleStage("Payroll run", "Compute gross, deductions and net pay",
            false, Session.Has(Permission.RunPayroll)));
        Cycle.Add(new CycleStage("Review & approval", "Approve before posting",
            false, Session.Has(Permission.ApprovePayroll)));
        Cycle.Add(new CycleStage("Payslips & reports", "Release payslips and remittances",
            false, Session.Has(Permission.ViewReports)));
    }

    private void BuildPermissions(User user)
    {
        Permissions.Clear();

        foreach (var permission in RolePermissions.For(user.Role).OrderBy(p => p.ToString()))
            Permissions.Add(Humanise(permission));
    }

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

    private static string Humanise(Permission permission)
    {
        var text = permission.ToString();
        var builder = new System.Text.StringBuilder(text.Length + 8);

        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]))
                builder.Append(' ');

            builder.Append(i == 0 ? text[i] : char.ToLowerInvariant(text[i]));
        }

        return builder.ToString();
    }
}
