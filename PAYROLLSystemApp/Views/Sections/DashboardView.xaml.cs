using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

public partial class DashboardView : ContentView, ISectionView
{
    private readonly DashboardViewModel _viewModel;

    public DashboardView(DashboardViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public Task OnShownAsync() => _viewModel.LoadAsync();
}
