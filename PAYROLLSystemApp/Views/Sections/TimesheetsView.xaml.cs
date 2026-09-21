using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>
/// The client's cut-off sheet, keyed as it is written — banded by detachment
/// code, seven figures against each name, every figure pricing itself as it is
/// typed.
/// </summary>
public partial class TimesheetsView : ContentView, ISectionView, IModalOwner
{
    private readonly TimesheetsViewModel _viewModel;

    public TimesheetsView(TimesheetsViewModel viewModel)
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
