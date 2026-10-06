using System.Windows.Input;

namespace PAYROLLSystemApp.Views.Controls;

/// <summary>
/// A web-style modal dialog: scrim plus a centred card with header, scrolling
/// body and a footer of actions.
///
/// MAUI has no built-in dialog control, so this sits as the last child of a
/// page's root Grid and covers it when open. Add / update / delete flows use
/// it so the surrounding list stays visible for context, and a destructive
/// confirmation is always an explicit second step (NFR-022).
/// </summary>
public partial class ModalHost : ContentView
{
    public static readonly BindableProperty IsOpenProperty = BindableProperty.Create(
        nameof(IsOpen), typeof(bool), typeof(ModalHost), false);

    public static readonly BindableProperty TitleProperty = BindableProperty.Create(
        nameof(Title), typeof(string), typeof(ModalHost), string.Empty);

    public static readonly BindableProperty SubtitleProperty = BindableProperty.Create(
        nameof(Subtitle), typeof(string), typeof(ModalHost), string.Empty,
        propertyChanged: (b, _, _) => ((ModalHost)b).OnPropertyChanged(nameof(HasSubtitle)));

    /// <summary>The caller-supplied dialog body.</summary>
    public static readonly BindableProperty BodyProperty = BindableProperty.Create(
        nameof(Body), typeof(View), typeof(ModalHost), null);

    public static readonly BindableProperty PrimaryTextProperty = BindableProperty.Create(
        nameof(PrimaryText), typeof(string), typeof(ModalHost), "Save");

    public static readonly BindableProperty PrimaryCommandProperty = BindableProperty.Create(
        nameof(PrimaryCommand), typeof(ICommand), typeof(ModalHost), null);

    public static readonly BindableProperty IsPrimaryEnabledProperty = BindableProperty.Create(
        nameof(IsPrimaryEnabled), typeof(bool), typeof(ModalHost), true);

    public static readonly BindableProperty SecondaryTextProperty = BindableProperty.Create(
        nameof(SecondaryText), typeof(string), typeof(ModalHost), "Cancel");

    public static readonly BindableProperty SecondaryCommandProperty = BindableProperty.Create(
        nameof(SecondaryCommand), typeof(ICommand), typeof(ModalHost), null);

    /// <summary>Renders the confirming action in the danger tone.</summary>
    public static readonly BindableProperty IsDestructiveProperty = BindableProperty.Create(
        nameof(IsDestructive), typeof(bool), typeof(ModalHost), false,
        propertyChanged: (b, _, _) => ((ModalHost)b).OnPropertyChanged(nameof(IsNotDestructive)));

    /// <summary>False for a dialog the user must resolve, e.g. one whose outcome must be acknowledged.</summary>
    public static readonly BindableProperty IsDismissibleProperty = BindableProperty.Create(
        nameof(IsDismissible), typeof(bool), typeof(ModalHost), true);

    public static readonly BindableProperty DismissCommandProperty = BindableProperty.Create(
        nameof(DismissCommand), typeof(ICommand), typeof(ModalHost), null);

    public static readonly BindableProperty ErrorTextProperty = BindableProperty.Create(
        nameof(ErrorText), typeof(string), typeof(ModalHost), string.Empty,
        propertyChanged: (b, _, _) => ((ModalHost)b).OnPropertyChanged(nameof(HasError)));

    public static readonly BindableProperty ModalWidthProperty = BindableProperty.Create(
        nameof(ModalWidth), typeof(double), typeof(ModalHost), 420d);

    public ModalHost() => InitializeComponent();

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public View? Body
    {
        get => (View?)GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public string PrimaryText
    {
        get => (string)GetValue(PrimaryTextProperty);
        set => SetValue(PrimaryTextProperty, value);
    }

    public ICommand? PrimaryCommand
    {
        get => (ICommand?)GetValue(PrimaryCommandProperty);
        set => SetValue(PrimaryCommandProperty, value);
    }

    public bool IsPrimaryEnabled
    {
        get => (bool)GetValue(IsPrimaryEnabledProperty);
        set => SetValue(IsPrimaryEnabledProperty, value);
    }

    public string SecondaryText
    {
        get => (string)GetValue(SecondaryTextProperty);
        set => SetValue(SecondaryTextProperty, value);
    }

    public ICommand? SecondaryCommand
    {
        get => (ICommand?)GetValue(SecondaryCommandProperty);
        set => SetValue(SecondaryCommandProperty, value);
    }

    public bool IsDestructive
    {
        get => (bool)GetValue(IsDestructiveProperty);
        set => SetValue(IsDestructiveProperty, value);
    }

    public bool IsDismissible
    {
        get => (bool)GetValue(IsDismissibleProperty);
        set => SetValue(IsDismissibleProperty, value);
    }

    public ICommand? DismissCommand
    {
        get => (ICommand?)GetValue(DismissCommandProperty);
        set => SetValue(DismissCommandProperty, value);
    }

    public string ErrorText
    {
        get => (string)GetValue(ErrorTextProperty);
        set => SetValue(ErrorTextProperty, value);
    }

    public double ModalWidth
    {
        get => (double)GetValue(ModalWidthProperty);
        set => SetValue(ModalWidthProperty, value);
    }

    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool IsNotDestructive => !IsDestructive;
}
