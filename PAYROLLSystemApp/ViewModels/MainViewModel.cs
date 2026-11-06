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

    /// <summary>A Segoe MDL2 Assets glyph; see the IconFont resource.</summary>
    public string Icon => Section switch
    {
        AppSection.Dashboard => "\uE80F",       // Home
        AppSection.Employees => "\uE716",       // People
        AppSection.Organization => "\uE821",    // Work
        AppSection.Detachments => "\uE707",     // MapPin
        AppSection.Attendance => "\uE823",      // Recent
        AppSection.Timesheets => "\uE7C3",      // Page
        AppSection.Leave => "\uE787",           // Calendar
        AppSection.PayrollSetup => "\uE8EF",    // Calculator
        AppSection.PayrollRuns => "\uE8C7",     // PaymentCard
        AppSection.Approvals => "\uE8FB",       // Accept
        AppSection.Payslips => "\uE8A5",        // Document
        AppSection.Reports => "\uE9D2",         // AreaChart
        AppSection.Users => "\uE77B",           // Contact
        AppSection.AuditLog => "\uE81C",        // History
        AppSection.DataManagement => "\uE74E",  // Save
        AppSection.Settings => "\uE713",        // Settings
        AppSection.Help => "\uE897",            // Help
        _ => string.Empty
    };

    public AppSection Section => Info.Section;

    public string Badge { get; }

    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    /// <summary>The colours that show it live in MainPage.xaml, so they follow the theme.</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Settings → compact sidebar: the icon alone, with the title as a tooltip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsLabel))]
    public partial bool IsCompact { get; set; }

    public bool ShowsLabel => !IsCompact;

    public bool ShowsBadge => HasBadge && !IsCompact;

    partial void OnIsCompactChanged(bool value) => OnPropertyChanged(nameof(ShowsBadge));
}

public sealed partial class NavGroupViewModel : ObservableObject
{
    public NavGroupViewModel(string name, IEnumerable<NavItemViewModel> items)
    {
        Name = name;
        Items = new ObservableCollection<NavItemViewModel>(items);
    }

    public string Name { get; }

    public ObservableCollection<NavItemViewModel> Items { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsName))]
    public partial bool IsCompact { get; set; }

    public bool ShowsName => !IsCompact;
}

/// <summary>
/// Drives the application chrome: sidebar, top bar and footer. The section
/// content itself is swapped by <see cref="MainPage"/>.
/// </summary>
public sealed partial class MainViewModel : BaseViewModel, IDisposable
{
    private readonly IAppNavigator _navigator;
    private readonly IDialogService _dialogs;
    private readonly IUserPreferences _preferences;
    private readonly IUpdateService _updates;
    private readonly IUpdateInstaller _installer;
    private IDispatcherTimer? _clock;

    public MainViewModel(
        ISessionService session,
        IAppNavigator navigator,
        IDialogService dialogs,
        IUserPreferences preferences,
        IUpdateService updates,
        IUpdateInstaller installer)
        : base(session)
    {
        _navigator = navigator;
        _dialogs = dialogs;
        _preferences = preferences;
        _updates = updates;
        _installer = installer;

        UserName = string.Empty;
        UserRole = string.Empty;
        UserInitials = string.Empty;
        PageTitle = string.Empty;
        PageSubtitle = string.Empty;
        Clock = string.Empty;
        FooterStatus = string.Empty;
        UpdateBadge = string.Empty;
        BannerText = string.Empty;
        BannerDetail = string.Empty;

        BuildNavigation();
        ApplyCompact(_preferences.CompactSidebar);

        _navigator.Navigated += OnNavigated;
        _preferences.Changed += OnPreferenceChanged;
        _updates.Changed += OnUpdatesChanged;
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

    public string AppVersion => $"Payroll MS v{Services.AppVersion.Display}";

    // ===================================================== update notice

    /// <summary>"Update 1.2 available" in the top bar; empty when up to date.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    public partial string UpdateBadge { get; set; }

    public bool HasUpdate => !string.IsNullOrEmpty(UpdateBadge);

    /// <summary>
    /// The strip under the top bar that announces a release the moment the
    /// background check finds it, then follows it through download to Install.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowsBanner { get; set; }

    [ObservableProperty]
    public partial string BannerText { get; set; }

    [ObservableProperty]
    public partial string BannerDetail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerShowsDownload))]
    [NotifyPropertyChangedFor(nameof(BannerIsDownloading))]
    [NotifyPropertyChangedFor(nameof(BannerShowsInstall))]
    [NotifyPropertyChangedFor(nameof(BannerShowsLater))]
    public partial UpdateStage BannerStage { get; set; }

    [ObservableProperty]
    public partial double BannerProgress { get; set; }

    public bool BannerShowsDownload => BannerStage == UpdateStage.Available;

    public bool BannerIsDownloading => BannerStage == UpdateStage.Downloading;

    public bool BannerShowsInstall => BannerStage == UpdateStage.Ready && _installer.CanInstall;

    public bool BannerShowsLater => BannerStage != UpdateStage.Downloading;

    /// <summary>
    /// "Later" hides the banner for this version at this stage until the next
    /// sign-in. When it moves on (finishes downloading) it shows again.
    /// </summary>
    private (Version Version, UpdateStage Stage)? _dismissed;

    [RelayCommand]
    private void OpenUpdates() => _navigator.NavigateTo(AppSection.Help);

    [RelayCommand]
    private void DownloadUpdate() => _ = _updates.DownloadAsync();

    [RelayCommand]
    private void CancelUpdateDownload() => _updates.CancelDownload();

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (await _installer.InstallAsync() is { } problem)
        {
            BannerDetail = problem;
            ShowUpdateNotice();
        }
    }

    [RelayCommand]
    private void DismissUpdateBanner()
    {
        if (_updates.Available is { } update)
            _dismissed = (update.Version, _updates.Stage);

        ShowsBanner = false;
    }

    private void OnUpdatesChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(ShowUpdateNotice);

    private void ShowUpdateNotice()
    {
        var update = _updates.Available;
        var stage = update is null ? UpdateStage.UpToDate : _updates.Stage;

        UpdateBadge = (update, stage) switch
        {
            (null, _) => string.Empty,
            (_, UpdateStage.Downloading) => $"Downloading {update.VersionDisplay}… {_updates.DownloadProgress:P0}",
            (_, UpdateStage.Ready) => $"Update {update.VersionDisplay} ready to install",
            _ => $"Update {update.VersionDisplay} available"
        };

        if (update is null)
        {
            ShowsBanner = false;
            return;
        }

        var stageChanged = BannerStage != stage;
        BannerStage = stage;
        BannerProgress = _updates.DownloadProgress;

        BannerText = stage switch
        {
            UpdateStage.Downloading => $"Downloading version {update.VersionDisplay}… {_updates.DownloadProgress:P0}",
            UpdateStage.Ready => $"Version {update.VersionDisplay} is downloaded and ready to install.",
            _ => $"A new version is available: {update.Title} ({update.VersionDisplay})."
        };

        if (stage == UpdateStage.Available && _updates.DownloadError is { } error)
            BannerDetail = error;
        else if (stage == UpdateStage.Ready && !_installer.CanInstall)
            BannerDetail = "An administrator can install it from Help & Updates.";
        else if (stage == UpdateStage.Available)
            BannerDetail = $"{update.SizeDisplay}. Download it now; nothing is installed until you choose Install.";
        else if (stageChanged || stage == UpdateStage.Downloading)
            BannerDetail = stage == UpdateStage.Ready
                ? "The app backs up the database before installing and reopens by itself."
                : "You can keep working while it downloads.";

        ShowsBanner = _dismissed != (update.Version, stage);
    }

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
        // Settings → Confirm before signing out (on unless switched off).
        if (_preferences.ConfirmSignOut &&
            !await _dialogs.ConfirmAsync("Sign out", "Are you sure you want to sign out?", "Sign out", "Stay"))
            return;

        await Session.SignOutAsync();
    });

    private void OnNavigated(object? sender, AppSection section) => SetActive(section);

    // ===================================================== compact sidebar

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpanded))]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    public partial bool IsCompact { get; set; }

    public bool IsExpanded => !IsCompact;

    /// <summary>Grows with the text size, or longer titles would be cut off.</summary>
    public double SidebarWidth => Math.Round((IsCompact ? 56 : 228) * TextScale.Factor);

    private void OnPreferenceChanged(object? sender, string name)
    {
        if (name == nameof(IUserPreferences.CompactSidebar))
            MainThread.BeginInvokeOnMainThread(() => ApplyCompact(_preferences.CompactSidebar));
    }

    private void ApplyCompact(bool compact)
    {
        IsCompact = compact;

        foreach (var group in NavGroups)
        {
            group.IsCompact = compact;

            foreach (var item in group.Items)
                item.IsCompact = compact;
        }
    }

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

        // Checks now, then every half hour while online and soon after the PC
        // reconnects. No internet simply means no banner until there is.
        ShowUpdateNotice();
        _updates.StartMonitoring();

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
        _preferences.Changed -= OnPreferenceChanged;
        _updates.Changed -= OnUpdatesChanged;
    }
}
