using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

public partial class UserManagementView : ContentView, ISectionView, IModalOwner
{
    private readonly UserManagementViewModel _viewModel;

    public UserManagementView(UserManagementViewModel viewModel)
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
