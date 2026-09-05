namespace PAYROLLSystemApp.Views.Controls;

/// <summary>
/// Draws the focus ring on the field frame.
///
/// <see cref="Handlers.FieldChrome"/> strips the platform's own border, which
/// also removes its focus indicator, so the frame has to show focus itself —
/// otherwise a keyboard user cannot tell which field they are in (NFR-023).
/// Applied by the <c>FieldFrame</c> style, so every field gets it.
/// </summary>
public static class FieldFrame
{
    private static readonly Color FocusFallback = Color.FromArgb("#4338CA");
    private static readonly Color RestLightFallback = Color.FromArgb("#CFD2DE");
    private static readonly Color RestDarkFallback = Color.FromArgb("#3D3D4A");

    public static readonly BindableProperty HighlightOnFocusProperty =
        BindableProperty.CreateAttached(
            "HighlightOnFocus",
            typeof(bool),
            typeof(FieldFrame),
            false,
            propertyChanged: OnHighlightOnFocusChanged);

    public static bool GetHighlightOnFocus(BindableObject target) =>
        (bool)target.GetValue(HighlightOnFocusProperty);

    public static void SetHighlightOnFocus(BindableObject target, bool value) =>
        target.SetValue(HighlightOnFocusProperty, value);

    private static void OnHighlightOnFocusChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not Border border || newValue is not bool enabled)
            return;

        border.Loaded -= OnBorderLoaded;
        border.Unloaded -= OnBorderUnloaded;

        if (!enabled)
            return;

        border.Loaded += OnBorderLoaded;
        border.Unloaded += OnBorderUnloaded;

        // Loaded has already fired when the style is applied late.
        if (border.Handler is not null)
            Subscribe(border);
    }

    private static void OnBorderLoaded(object? sender, EventArgs e)
    {
        if (sender is Border border)
            Subscribe(border);
    }

    private static void OnBorderUnloaded(object? sender, EventArgs e)
    {
        if (sender is Border border)
            Unsubscribe(border);
    }

    private static void Subscribe(Border border)
    {
        if (border.Content is not VisualElement content)
            return;

        // Idempotent: Loaded can fire more than once for a recycled view.
        content.Focused -= OnFocused;
        content.Unfocused -= OnUnfocused;

        content.Focused += OnFocused;
        content.Unfocused += OnUnfocused;
    }

    private static void Unsubscribe(Border border)
    {
        if (border.Content is not VisualElement content)
            return;

        content.Focused -= OnFocused;
        content.Unfocused -= OnUnfocused;
    }

    private static void OnFocused(object? sender, FocusEventArgs e) =>
        Apply(sender, focused: true);

    private static void OnUnfocused(object? sender, FocusEventArgs e) =>
        Apply(sender, focused: false);

    private static void Apply(object? sender, bool focused)
    {
        if (sender is not VisualElement { Parent: Border border })
            return;

        if (focused)
        {
            border.Stroke = Resource("Brand", FocusFallback);
            border.StrokeThickness = 2;
            return;
        }

        border.StrokeThickness = 1;
        border.SetAppThemeColor(
            Border.StrokeProperty,
            Resource("BorderStrongLight", RestLightFallback),
            Resource("BorderStrongDark", RestDarkFallback));
    }

    private static Color Resource(string key, Color fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var value) == true && value is Color color
            ? color
            : fallback;
}
