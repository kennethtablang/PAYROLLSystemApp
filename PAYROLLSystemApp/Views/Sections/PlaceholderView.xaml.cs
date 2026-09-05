using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.Views.Sections;

/// <summary>Stands in for a module that later requirement sections will build.</summary>
public partial class PlaceholderView : ContentView
{
    public PlaceholderView(SectionInfo info)
    {
        InitializeComponent();

        TitleLabel.Text = info.Title;
        DescriptionLabel.Text = info.Description;
        PlannedLabel.Text = string.IsNullOrEmpty(info.RequirementRef)
            ? "Not yet implemented."
            : $"Planned for {info.RequirementRef} of the requirements specification.";
    }
}
