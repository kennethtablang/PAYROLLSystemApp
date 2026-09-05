using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

public partial class AuditLogView : ContentView, ISectionView
{
    private readonly AuditLogViewModel _viewModel;

    public AuditLogView(AuditLogViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public Task OnShownAsync() => _viewModel.LoadAsync();
}
