using Microsoft.Extensions.DependencyInjection;
using PAYROLLSystemApp.Services;
using PAYROLLSystemApp.ViewModels;
using PAYROLLSystemApp.Views.Sections;

namespace PAYROLLSystemApp.Views;

/// <summary>A section that wants to load or refresh when it becomes visible.</summary>
public interface ISectionView
{
    Task OnShownAsync();
}

/// <summary>
/// A section whose dialogs must float above the whole window rather than only
/// the content region.
/// </summary>
public interface IModalOwner
{
    View DetachModalLayer();
}

/// <summary>
/// The application chrome. Sections are swapped into the content region rather
/// than pushed as pages, which keeps the sidebar, top bar and footer fixed.
/// </summary>
public partial class MainPage : ContentPage
{
    private readonly MainViewModel _viewModel;
    private readonly IServiceProvider _services;
    private readonly IAppNavigator _navigator;
    private readonly ISessionService _session;
    private View? _modalLayer;

    public MainPage(
        MainViewModel viewModel,
        IServiceProvider services,
        IAppNavigator navigator,
        ISessionService session)
    {
        InitializeComponent();

        BindingContext = _viewModel = viewModel;
        _services = services;
        _navigator = navigator;
        _session = session;

        _navigator.Navigated += OnNavigated;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        _viewModel.Start();
        ShowSection(_navigator.Current);
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();

        _navigator.Navigated -= OnNavigated;
        _viewModel.Dispose();
    }

    private async void OnNavigated(object? sender, AppSection section) =>
        await ShowSectionAsync(section);

    private async void ShowSection(AppSection section) => await ShowSectionAsync(section);

    private async Task ShowSectionAsync(AppSection section)
    {
        _session.Touch();

        var view = Resolve(section);

        // Dialogs are hoisted out of the section and added directly to the page
        // root, so the scrim dims the sidebar and top bar too.
        //
        // The layer is added and removed rather than left in place and hidden:
        // a permanent full-window element swallows every click, and marking it
        // InputTransparent is not a way out, because on Windows that cascades
        // to its children and kills the dialog's own buttons.
        if (_modalLayer is not null)
        {
            RootLayer.Children.Remove(_modalLayer);
            _modalLayer = null;
        }

        if (view is IModalOwner owner)
        {
            _modalLayer = owner.DetachModalLayer();
            RootLayer.Children.Add(_modalLayer);
        }

        SectionHost.Content = view;

        if (view is ISectionView loadable)
            await loadable.OnShownAsync();
    }

    private View Resolve(AppSection section) => section switch
    {
        AppSection.Dashboard => _services.GetRequiredService<DashboardView>(),
        AppSection.Employees => _services.GetRequiredService<EmployeesView>(),
        AppSection.Organization => _services.GetRequiredService<OrganizationView>(),
        AppSection.Attendance => _services.GetRequiredService<AttendanceView>(),
        AppSection.Leave => _services.GetRequiredService<LeaveView>(),
        AppSection.PayrollSetup => _services.GetRequiredService<PayrollSetupView>(),
        AppSection.PayrollRuns => _services.GetRequiredService<PayrollRunsView>(),
        AppSection.Approvals => _services.GetRequiredService<ApprovalsView>(),
        AppSection.Payslips => _services.GetRequiredService<PayslipsView>(),
        AppSection.Reports => _services.GetRequiredService<ReportsView>(),
        AppSection.Users => _services.GetRequiredService<UserManagementView>(),
        AppSection.AuditLog => _services.GetRequiredService<AuditLogView>(),
        AppSection.DataManagement => _services.GetRequiredService<DataManagementView>(),
        _ => new PlaceholderView(AppSections.Get(section))
    };
}
