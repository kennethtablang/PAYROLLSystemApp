using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>
/// Section 2.9 — backup, restore and archival (FR-093, FR-094).
///
/// <para>Opening the screen is also what checks whether an automatic backup is
/// due, which is the whole of the "scheduled" in FR-093: a desktop application
/// has nothing running when it is closed.</para>
/// </summary>
public partial class DataManagementView : ContentView, ISectionView
{
    private readonly DataManagementViewModel _viewModel;

    public DataManagementView(DataManagementViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    public Task OnShownAsync() => _viewModel.LoadAsync();
}
