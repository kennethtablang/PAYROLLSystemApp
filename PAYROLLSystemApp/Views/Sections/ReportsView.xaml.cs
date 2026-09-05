using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>
/// Section 2.8 — reporting (FR-080 – FR-085). A list of reports on the left, the
/// selected one rendered on the right, and two export buttons.
///
/// <para>No modal layer: everything on this screen is read-only apart from the
/// export, which reports where the file went rather than asking anything.</para>
/// </summary>
public partial class ReportsView : ContentView, ISectionView
{
    private readonly ReportsViewModel _viewModel;

    public ReportsView(ReportsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public Task OnShownAsync() => _viewModel.LoadAsync();
}
