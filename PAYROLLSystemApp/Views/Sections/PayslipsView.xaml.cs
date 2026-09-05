using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>
/// Section 2.7 — payslips (FR-070 – FR-074). Serves both a payroll officer
/// reviewing everyone's and an employee reading their own; the narrowing is
/// done by the service, not here.
///
/// <para>No modal layer: everything on this screen is read-only apart from the
/// export, which reports where the file went rather than asking anything.</para>
/// </summary>
public partial class PayslipsView : ContentView, ISectionView
{
    private readonly PayslipsViewModel _viewModel;

    public PayslipsView(PayslipsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public Task OnShownAsync() => _viewModel.LoadAsync();
}
