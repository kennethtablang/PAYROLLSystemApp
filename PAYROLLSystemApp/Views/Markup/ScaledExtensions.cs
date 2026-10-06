using Microsoft.Maui.Controls.Xaml;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.Views.Markup;

/// <summary>
/// A font size that follows Settings → Text size: <c>FontSize="{markup:Font 12.5}"</c>.
/// Every font size in the XAML goes through this, so the whole interface grows
/// together rather than only the text that happens to use a theme style.
/// </summary>
[ContentProperty(nameof(Size))]
[AcceptEmptyServiceProvider]
public sealed class FontExtension : IMarkupExtension<double>
{
    public double Size { get; set; }

    public double ProvideValue(IServiceProvider serviceProvider) => TextScale.Apply(Size);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}

/// <summary>
/// A height that has to grow with the text inside it — a field, a button, the
/// top bar — or larger text would be clipped: <c>HeightRequest="{markup:Scaled 34}"</c>.
/// </summary>
[ContentProperty(nameof(Size))]
[AcceptEmptyServiceProvider]
public sealed class ScaledExtension : IMarkupExtension<double>
{
    public double Size { get; set; }

    public double ProvideValue(IServiceProvider serviceProvider) => Math.Round(Size * TextScale.Factor);

    object IMarkupExtension.ProvideValue(IServiceProvider serviceProvider) => ProvideValue(serviceProvider);
}
