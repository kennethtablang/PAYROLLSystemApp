using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>
/// Section 2.5 — the payroll configuration (FR-040 – FR-045): the pay calendar,
/// the earning and deduction components, the DOLE premium matrix, the statutory
/// tables and the company profile.
/// </summary>
public partial class PayrollSetupView : ContentView, ISectionView, IModalOwner
{
    private readonly PayrollSetupViewModel _viewModel;

    public PayrollSetupView(PayrollSetupViewModel viewModel)
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
