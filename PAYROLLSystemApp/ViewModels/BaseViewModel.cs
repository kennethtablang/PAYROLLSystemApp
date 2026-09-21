using CommunityToolkit.Mvvm.ComponentModel;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    protected BaseViewModel(ISessionService session)
    {
        Session = session;

        // Partial properties cannot carry initialisers, so the empty-string
        // defaults are established here.
        Title = string.Empty;
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
    }

    protected ISessionService Session { get; }

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    public partial bool IsBusy { get; set; }

    public bool IsNotBusy => !IsBusy;

    /// <summary>Inline, non-blocking feedback shown on the page itself (NFR-023).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusMessage { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusMessage);

    protected void ClearMessages()
    {
        ErrorMessage = string.Empty;
        StatusMessage = string.Empty;
    }

    protected void ShowError(string message)
    {
        StatusMessage = string.Empty;
        ErrorMessage = message;
    }

    protected void ShowStatus(string message)
    {
        ErrorMessage = string.Empty;
        StatusMessage = message;
    }

    /// <summary>
    /// Runs work with a busy guard and re-entrancy protection (NFR-026).
    /// </summary>
    protected async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy)
            return;

        IsBusy = true;

        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            ShowError("Something went wrong. Please try again.");
            System.Diagnostics.Debug.WriteLine($"[{GetType().Name}] {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Called by the page when it becomes visible.</summary>
    public virtual Task OnAppearingAsync()
    {
        return Task.CompletedTask;
    }
}
