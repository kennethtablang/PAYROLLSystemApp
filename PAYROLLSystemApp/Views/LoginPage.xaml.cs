using PAYROLLSystemApp.ViewModels;

namespace PAYROLLSystemApp.Views;

public partial class LoginPage : ContentPage
{
    private readonly LoginViewModel _viewModel;

    public LoginPage(LoginViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    /// <summary>Collapse the brand panel when the window is too narrow for it.</summary>
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        BrandPanel.IsVisible = width >= 820;
    }

    /// <summary>The hardware back button must not bypass the sign-in screen.</summary>
    protected override bool OnBackButtonPressed() => true;
}
