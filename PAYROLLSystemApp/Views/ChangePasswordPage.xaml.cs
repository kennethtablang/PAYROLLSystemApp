using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views;

public partial class ChangePasswordPage : ContentPage
{
    public ChangePasswordPage(ChangePasswordViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = viewModel;
    }

    /// <summary>The hardware back button must not skip a forced change.</summary>
    protected override bool OnBackButtonPressed() => true;
}
