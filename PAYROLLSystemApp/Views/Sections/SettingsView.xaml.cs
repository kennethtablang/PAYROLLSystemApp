using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>Appearance, the signed-in account, printing and the start-up screen.</summary>
public partial class SettingsView : ContentView, ISectionView
{
    private readonly SettingsViewModel _viewModel;

    public SettingsView(SettingsViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public Task OnShownAsync() => _viewModel.LoadAsync();
}
