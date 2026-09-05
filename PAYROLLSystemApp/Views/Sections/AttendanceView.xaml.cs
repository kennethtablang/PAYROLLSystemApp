using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>
/// Section 2.3 — daily time records, overtime approval, the cut-off summary and
/// the holiday calendar (FR-020 – FR-027).
/// </summary>
public partial class AttendanceView : ContentView, ISectionView, IModalOwner
{
    private readonly AttendanceViewModel _viewModel;

    public AttendanceView(AttendanceViewModel viewModel)
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
