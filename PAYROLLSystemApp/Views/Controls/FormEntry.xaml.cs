using System.Windows.Input;

namespace PAYROLLSystemApp.Views.Controls;

/// <summary>
/// Label + compact bordered input + inline error, in one control.
///
/// MAUI's Entry exposes no Padding, so the field frame is a Border wrapping a
/// chrome-less Entry. Wrapping that here keeps every form in the app to the
/// same metrics and error placement (NFR-023, NFR-024).
/// </summary>
public partial class FormEntry : ContentView
{
    public static readonly BindableProperty LabelProperty = BindableProperty.Create(
        nameof(Label), typeof(string), typeof(FormEntry), string.Empty,
        propertyChanged: (b, _, _) => ((FormEntry)b).OnPropertyChanged(nameof(HasLabel)));

    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(FormEntry), string.Empty, BindingMode.TwoWay);

    public static readonly BindableProperty PlaceholderProperty = BindableProperty.Create(
        nameof(Placeholder), typeof(string), typeof(FormEntry), string.Empty);

    public static readonly BindableProperty IsPasswordProperty = BindableProperty.Create(
        nameof(IsPassword), typeof(bool), typeof(FormEntry), false);

    public static readonly BindableProperty ErrorTextProperty = BindableProperty.Create(
        nameof(ErrorText), typeof(string), typeof(FormEntry), string.Empty,
        propertyChanged: (b, _, _) => ((FormEntry)b).OnPropertyChanged(nameof(HasError)));

    public static readonly BindableProperty IsFieldEnabledProperty = BindableProperty.Create(
        nameof(IsFieldEnabled), typeof(bool), typeof(FormEntry), true);

    public static readonly BindableProperty ReturnCommandProperty = BindableProperty.Create(
        nameof(ReturnCommand), typeof(ICommand), typeof(FormEntry), null);

    public FormEntry() => InitializeComponent();

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Placeholder
    {
        get => (string)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public bool IsPassword
    {
        get => (bool)GetValue(IsPasswordProperty);
        set => SetValue(IsPasswordProperty, value);
    }

    public string ErrorText
    {
        get => (string)GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    public bool IsFieldEnabled
    {
        get => (bool)GetValue(IsFieldEnabledProperty);
        set => SetValue(IsFieldEnabledProperty, value);
    }

    public ICommand? ReturnCommand
    {
        get => (ICommand?)GetValue(ReturnCommandProperty);
        set => SetValue(ReturnCommandProperty, value);
    }

    public bool HasLabel => !string.IsNullOrWhiteSpace(Label);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public void FocusField() => Field.Focus();
}
