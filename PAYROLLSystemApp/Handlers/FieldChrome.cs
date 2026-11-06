using Microsoft.Maui.Handlers;

namespace PAYROLLSystemApp.Handlers;

/// <summary>
/// Removes the platform's own border and background from text inputs.
///
/// The app draws its own field frame (the <c>FieldFrame</c> Border style), and
/// every platform also draws one inside it — on Windows a square-cornered
/// TextBox border that does not follow the frame's rounded corners, on Android
/// the material underline. Setting BackgroundColor from XAML is not enough
/// because the chrome is applied by the native control's own visual states, so
/// it has to be stripped on the platform view.
/// </summary>
public static class FieldChrome
{
    public static void Register()
    {
        EntryHandler.Mapper.AppendToMapping(nameof(FieldChrome), (handler, _) => Strip(handler.PlatformView));
        EditorHandler.Mapper.AppendToMapping(nameof(FieldChrome), (handler, _) => Strip(handler.PlatformView));
        PickerHandler.Mapper.AppendToMapping(nameof(FieldChrome), (handler, _) => Strip(handler.PlatformView));
        DatePickerHandler.Mapper.AppendToMapping(nameof(FieldChrome), (handler, _) => Strip(handler.PlatformView));
        TimePickerHandler.Mapper.AppendToMapping(nameof(FieldChrome), (handler, _) => Strip(handler.PlatformView));
    }

#if WINDOWS
    private static void Strip(Microsoft.UI.Xaml.FrameworkElement platformView)
    {
        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var none = new Microsoft.UI.Xaml.Thickness(0);

        // WinUI picks the border and fill from these resource keys per visual
        // state, so overriding the properties alone leaves the focused and
        // pointer-over states drawing chrome again.
        void Neutralise(Microsoft.UI.Xaml.ResourceDictionary resources, string prefix)
        {
            foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled", "Pressed", "Unfocused" })
            {
                resources[$"{prefix}Background{state}"] = transparent;
                resources[$"{prefix}BorderBrush{state}"] = transparent;
            }
        }

        switch (platformView)
        {
            case Microsoft.UI.Xaml.Controls.TextBox textBox:
                textBox.BorderThickness = none;
                textBox.Padding = none;
                textBox.MinHeight = 0;
                textBox.VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
                textBox.Background = transparent;
                textBox.Resources["TextControlBorderThemeThickness"] = none;
                textBox.Resources["TextControlBorderThemeThicknessFocused"] = none;
                Neutralise(textBox.Resources, "TextControl");
                break;

            // Without this the date picker drew its own bordered box inside the
            // frame, only part of the frame's width, and taller than the frame,
            // so the field looked split in two and its text was cut. Properties
            // only, as for ComboBox: overriding these pickers' brushes through
            // Resources crashes WinUI at start-up (0xc000027b in combase).
            case Microsoft.UI.Xaml.Controls.CalendarDatePicker datePicker:
                datePicker.BorderThickness = none;
                datePicker.Background = transparent;
                datePicker.Padding = none;
                datePicker.MinHeight = 0;
                datePicker.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
                datePicker.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
                break;

            case Microsoft.UI.Xaml.Controls.TimePicker timePicker:
                timePicker.BorderThickness = none;
                timePicker.Background = transparent;
                timePicker.Padding = none;
                timePicker.MinHeight = 0;
                timePicker.MinWidth = 0;
                timePicker.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
                timePicker.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
                break;

            // ComboBox is deliberately handled with properties only. Overriding
            // its brushes through Resources the way TextBox allows crashes
            // WinUI inside combase when the template is realised.
            case Microsoft.UI.Xaml.Controls.ComboBox comboBox:
                comboBox.BorderThickness = none;
                comboBox.Background = transparent;
                comboBox.MinHeight = 0;
                // WinUI's default ComboBox padding is asymmetric (more at the
                // bottom), which pushes the selected text down until the field
                // frame clips it.
                comboBox.Padding = none;
                comboBox.VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
                comboBox.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
                break;
        }
    }
#elif ANDROID
    private static void Strip(Android.Views.View platformView)
    {
        // Clears the material underline and its focus accent.
        platformView.Background = null;
        platformView.SetPadding(0, 0, 0, 0);
    }
#elif IOS || MACCATALYST
    private static void Strip(UIKit.UIView platformView)
    {
        switch (platformView)
        {
            case UIKit.UITextField textField:
                textField.BorderStyle = UIKit.UITextBorderStyle.None;
                textField.Layer.BorderWidth = 0;
                break;

            case UIKit.UITextView textView:
                textView.Layer.BorderWidth = 0;
                textView.TextContainerInset = UIKit.UIEdgeInsets.Zero;
                break;
        }
    }
#else
    private static void Strip(object platformView)
    {
        // No chrome to remove on other platforms.
    }
#endif
}
