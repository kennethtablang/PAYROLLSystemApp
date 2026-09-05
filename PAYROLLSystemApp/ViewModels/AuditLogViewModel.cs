using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>FR-092: the audit trail, readable by administrators only.</summary>
public sealed partial class AuditLogViewModel : BaseViewModel
{
    private readonly IAuditService _audit;
    private List<AuditEntry> _all = new();

    public AuditLogViewModel(IAuditService audit, ISessionService session)
        : base(session)
    {
        _audit = audit;
        Title = "Audit log";
        SearchText = string.Empty;
        ResultSummary = string.Empty;
    }

    public ObservableCollection<AuditEntry> Entries { get; } = new();

    [ObservableProperty]
    public partial string SearchText { get; set; }

    [ObservableProperty]
    public partial string ResultSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(LoadEntriesAsync);

    public async Task LoadAsync()
    {
        // Defence in depth: the sidebar hides this section, but the data load
        // must refuse independently (FR-002).
        if (!await Session.RequireAsync(Permission.ViewAuditLog, "open the audit log"))
        {
            IsDenied = true;
            Entries.Clear();
            return;
        }

        IsDenied = false;
        await RunAsync(LoadEntriesAsync);
    }

    private async Task LoadEntriesAsync()
    {
        ClearMessages();
        _all = (await _audit.GetRecentAsync(500)).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term = (SearchText ?? string.Empty).Trim();

        var filtered = string.IsNullOrEmpty(term)
            ? _all
            : _all.Where(e =>
                e.Action.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.Actor.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.Details.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        Entries.Clear();
        foreach (var entry in filtered)
            Entries.Add(entry);

        ResultSummary = _all.Count == 0
            ? "No audit entries yet."
            : filtered.Count == _all.Count
                ? $"{_all.Count} entries"
                : $"{filtered.Count} of {_all.Count} entries";
    }
}
