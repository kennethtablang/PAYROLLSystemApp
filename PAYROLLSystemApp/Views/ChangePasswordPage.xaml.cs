using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views;

public partial class ChangePasswordPage : ContentPage
{
    public ChangePasswordPage(ChangePasswordViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    /// <summary>Cancel is the way back to the workspace; the hardware back button is ignored.</summary>
    protected override bool OnBackButtonPressed() => true;
}
