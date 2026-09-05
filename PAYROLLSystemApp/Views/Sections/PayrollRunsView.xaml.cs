using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>
/// Section 2.6 — payroll processing (FR-050 – FR-063): creating runs, computing
/// them, adjusting them, and the loans they collect against.
/// </summary>
public partial class PayrollRunsView : ContentView, ISectionView, IModalOwner
{
    private readonly PayrollRunsViewModel _viewModel;

    public PayrollRunsView(PayrollRunsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public Task OnShownAsync() => _viewModel.LoadAsync();

    /// <summary>
    /// Hands the dialog layer to the page so it can be shown above the whole
    /// window. The binding context is pinned explicitly, because once the layer
    /// is reparented it would otherwise inherit the page's view model.
    /// </summary>
    public View DetachModalLayer()
    {
        RootGrid.Children.Remove(ModalLayer);
        ModalLayer.BindingContext = _viewModel;
        return ModalLayer;
    }
}
