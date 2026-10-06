using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>Installing updates, and writing problem reports and change requests.</summary>
public partial class HelpView : ContentView, ISectionView
{
    private readonly HelpViewModel _viewModel;

    public HelpView(HelpViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;

        // The update service outlives this view; let go of its event.
        Unloaded += (_, _) => _viewModel.Dispose();
    }

    public Task OnShownAsync() => _viewModel.LoadAsync();
}
