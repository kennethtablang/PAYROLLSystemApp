using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>
/// FR-058. The approver's side of a payroll run: review, approve or return, and
/// post. A separate section from Payroll Runs because it is a separate
/// permission held by a different person.
/// </summary>
public partial class ApprovalsView : ContentView, ISectionView, IModalOwner
{
    private readonly ApprovalsViewModel _viewModel;

    public ApprovalsView(ApprovalsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public Task OnShownAsync() => _viewModel.LoadAsync();

    public View DetachModalLayer()
    {
        RootGrid.Children.Remove(ModalLayer);
        ModalLayer.BindingContext = _viewModel;
        return ModalLayer;
    }
}
