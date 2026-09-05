namespace PAYROLLSystemApp.Services;

public interface IDialogService
{
    Task AlertAsync(string title, string message, string cancel = "OK");

    /// <summary>NFR-022: destructive actions must be explicitly confirmed.</summary>
    Task<bool> ConfirmAsync(string title, string message, string accept = "Yes", string cancel = "Cancel");

    Task<string?> PromptAsync(string title, string message, string accept = "OK", string cancel = "Cancel",
        string? placeholder = null, string? initialValue = null);

    Task<string?> ActionSheetAsync(string title, string cancel, params string[] options);
}

/// <summary>Thin wrapper over page dialogs so view models remain testable.</summary>
public sealed class DialogService : IDialogService
{
    private static Page? CurrentPage =>
        Application.Current?.Windows.FirstOrDefault()?.Page;

    public Task AlertAsync(string title, string message, string cancel = "OK") =>
        MainThread.InvokeOnMainThreadAsync(() =>
            CurrentPage?.DisplayAlertAsync(title, message, cancel) ?? Task.CompletedTask);

    public Task<bool> ConfirmAsync(string title, string message, string accept = "Yes", string cancel = "Cancel") =>
        MainThread.InvokeOnMainThreadAsync(() =>
            CurrentPage?.DisplayAlertAsync(title, message, accept, cancel) ?? Task.FromResult(false));

    public Task<string?> PromptAsync(string title, string message, string accept = "OK",
        string cancel = "Cancel", string? placeholder = null, string? initialValue = null) =>
        MainThread.InvokeOnMainThreadAsync(() =>
            CurrentPage?.DisplayPromptAsync(title, message, accept, cancel, placeholder,
                initialValue: initialValue ?? string.Empty)
            ?? Task.FromResult<string?>(null))!;

    public Task<string?> ActionSheetAsync(string title, string cancel, params string[] options) =>
        MainThread.InvokeOnMainThreadAsync(() =>
            CurrentPage?.DisplayActionSheetAsync(title, cancel, null, options)
            ?? Task.FromResult<string?>(null))!;
}
