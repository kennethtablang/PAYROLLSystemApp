using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>One rendered sidebar entry. Mutable because the active item changes.</summary>
public sealed partial class NavItemViewModel : ObservableObject
{
    private readonly IAppNavigator _navigator;
    private readonly ISessionService _session;

    public NavItemViewModel(SectionInfo info, IAppNavigator navigator, ISessionService session)
    {
        Info = info;
        _navigator = navigator;
        _session = session;
        Badge = info.IsImplemented ? string.Empty : "soon";
    }

    public SectionInfo Info { get; }

    /// <summary>
    /// The item carries its own command rather than reaching back up to the
    /// page's view model, so the sidebar template needs no RelativeSource.
    /// </summary>
    [RelayCommand]
    private void Select()
    {
        _navigator.NavigateTo(Section);
    }

    public string Title => Info.Title;

    public AppSection Section => Info.Section;

    public string Badge { get; }

    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelColor))]
    [NotifyPropertyChangedFor(nameof(RowColor))]
    [NotifyPropertyChangedFor(nameof(IndicatorColor))]
    public partial bool IsActive { get; set; }

    public Color LabelColor => IsActive ? Color.FromArgb("#FFFFFF") : Color.FromArgb("#B9BACB");

    public Color RowColor => IsActive ? Color.FromArgb("#2A2748") : Colors.Transparent;

    public Color IndicatorColor => IsActive ? Color.FromArgb("#8B85FF") : Colors.Transparent;
}

public sealed partial class NavGroupViewModel
{
    public NavGroupViewModel(string name, IEnumerable<NavItemViewModel> items)
    {
        Name = name;
        Items = new ObservableCollection<NavItemViewModel>(items);
    }

    public string Name { get; }

    public ObservableCollection<NavItemViewModel> Items { get; }
}

/// <summary>
/// Drives the application chrome: sidebar, top bar and footer. The section
/// content itself is swapped by <see cref="MainPage"/>.
/// </summary>
public sealed partial class MainViewModel : BaseViewModel, IDisposable
{
    private readonly IAppNavigator _navigator;
    private readonly IDialogService _dialogs;
    private IDispatcherTimer? _clock;

    public MainViewModel(
        ISessionService session,
        IAppNavigator navigator,
        IDialogService dialogs)
        : base(session)
    {
        _navigator = navigator;
        _dialogs = dialogs;

        UserName = string.Empty;
        UserRole = string.Empty;
        UserInitials = string.Empty;
        PageTitle = string.Empty;
        PageSubtitle = string.Empty;
        Clock = string.Empty;
        FooterStatus = string.Empty;

        BuildNavigation();
        _navigator.Navigated += OnNavigated;
    }

    public ObservableCollection<NavGroupViewModel> NavGroups { get; } = new();

    [ObservableProperty]
    public partial string UserName { get; set; }

    [ObservableProperty]
    public partial string UserRole { get; set; }

    [ObservableProperty]
    public partial string UserInitials { get; set; }

    [ObservableProperty]
    public partial string PageTitle { get; set; }

    [ObservableProperty]
    public partial string PageSubtitle { get; set; }

    [ObservableProperty]
    public partial string Clock { get; set; }

    [ObservableProperty]
    public partial string FooterStatus { get; set; }

    public string AppVersion => $"Payroll MS v{AppInfo.Current.VersionString}";

    /// <summary>
    /// FR-002: a destination the role cannot use is never rendered. The section
    /// views re-check on load, so this is presentation, not the security gate.
    /// </summary>
    private void BuildNavigation()
    {
        NavGroups.Clear();

        var permitted = AppSections.All
            .Where(s => Session.Has(s.RequiredPermission))
            .ToList();

        foreach (var group in permitted.Select(s => s.Group).Distinct())
        {
            var items = permitted
                .Where(s => s.Group == group)
                .Select(s => new NavItemViewModel(s, _navigator, Session));

            NavGroups.Add(new NavGroupViewModel(group, items));
        }

        SetActive(_navigator.Current);
    }

    [RelayCommand]
    private Task SignOutAsync() => RunAsync(async () =>
    {
        // NFR-022
        if (!await _dialogs.ConfirmAsync("Sign out", "Are you sure you want to sign out?", "Sign out", "Stay"))
            return;

        await Session.SignOutAsync();
    });

    /// <summary>FR-004.</summary>
    [RelayCommand]
    private void ChangePassword() => Session.RequestPasswordChange();

    private void OnNavigated(object? sender, AppSection section) => SetActive(section);

    private void SetActive(AppSection section)
    {
        foreach (var item in NavGroups.SelectMany(g => g.Items))
            item.IsActive = item.Section == section;

        var info = AppSections.Get(section);
        PageTitle = info.Title;
        PageSubtitle = info.Description;
    }

    public void Start()
    {
        var user = Session.CurrentUser;
        if (user is not null)
        {
            UserName = string.IsNullOrWhiteSpace(user.FullName) ? user.Username : user.FullName;
            UserRole = user.RoleDisplayName;
            UserInitials = Initials(UserName);
        }

        FooterStatus = $"Signed in as {UserName} · {UserRole}";

        Tick();

        _clock = Application.Current?.Dispatcher.CreateTimer();
        if (_clock is not null)
        {
            _clock.Interval = TimeSpan.FromSeconds(1);
            _clock.Tick += (_, _) => Tick();
            _clock.Start();
        }
    }

    private void Tick() => Clock = DateTime.Now.ToString("ddd, dd MMM yyyy · h:mm:ss tt");

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
        };
    }

    public void Dispose()
    {
        _clock?.Stop();
        _clock = null;
        _navigator.Navigated -= OnNavigated;
    }
}
