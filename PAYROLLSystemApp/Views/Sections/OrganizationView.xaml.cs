using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>
/// FR-012 reference data — the departments and positions employee records are
/// assigned to.
/// </summary>
public partial class OrganizationView : ContentView, ISectionView, IModalOwner
{
    private readonly OrganizationViewModel _viewModel;

    public OrganizationView(OrganizationViewModel viewModel)
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
