using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>A request already written, as the list shows it.</summary>
public sealed class SupportRequestRow
{
    public SupportRequestRow(SupportRequest request)
    {
        Request = request;
    }

    public SupportRequest Request { get; }

    public string Reference => Request.Reference;

    public string Title => Request.Title;

    public string Detail =>
        $"{KindLabel(Request.Kind)} · {Request.Priority} · {Request.Screen} · " +
        $"{Request.CreatedLocal:MMM d, yyyy h:mm tt} · v{Request.AppVersion} · {Request.CreatedBy}";

    public bool HasFile => File.Exists(Request.PackagePath);

    public static string KindLabel(SupportRequestKind kind) => kind switch
    {
        SupportRequestKind.Problem => "Problem",
        SupportRequestKind.Change => "Change request",
        _ => "Question"
    };
}

/// <summary>
/// Help &amp; Updates: installing a newer version published by the developer,
/// and writing a problem report or change request to send them.
/// </summary>
public sealed partial class HelpViewModel : BaseViewModel, IDisposable
{
    private const string OtherScreen = "Other / not sure";

    private readonly IUpdateService _updates;
    private readonly ISupportRequestService _requests;
    private readonly IDataManagementService _data;
    private readonly IDialogService _dialogs;
    private readonly IAuditService _audit;

    private CancellationTokenSource? _download;

    public HelpViewModel(
        ISessionService session,
        IUpdateService updates,
        ISupportRequestService requests,
        IDataManagementService data,
        IDialogService dialogs,
        IAuditService audit)
        : base(session)
    {
        _updates = updates;
        _requests = requests;
        _data = data;
        _dialogs = dialogs;
        _audit = audit;
        Title = "Help & Updates";

        UpdateStatus = string.Empty;
        UpdateTitle = string.Empty;
        UpdateNotes = string.Empty;
        UpdateMeta = string.Empty;
        DownloadText = string.Empty;
        RequestTitle = string.Empty;
        RequestDetails = string.Empty;
        AttachmentsText = string.Empty;

        KindOptions =
        [
            new EnumOption((int)SupportRequestKind.Problem, "Something is wrong (a problem)"),
            new EnumOption((int)SupportRequestKind.Change, "Change something or add something new"),
            new EnumOption((int)SupportRequestKind.Question, "A question")
        ];

        PriorityOptions =
        [
            new EnumOption((int)SupportRequestPriority.Urgent, "Urgent — we cannot finish payroll"),
            new EnumOption((int)SupportRequestPriority.Normal, "Normal"),
            new EnumOption((int)SupportRequestPriority.Low, "Low — whenever possible")
        ];

        ScreenOptions = AppSections.All.Select(s => s.Title).Append(OtherScreen).ToList();

        SelectedKind = KindOptions[0];
        SelectedPriority = PriorityOptions[1];
        SelectedScreen = OtherScreen;

        _updates.AvailableChanged += OnAvailableChanged;
        ShowAvailable();
    }

    // ============================================================ updates

    public string CurrentVersion => $"Version {AppVersion.Display}";

    /// <summary>Installing backs the database up first, so it needs the backup permission.</summary>
    public bool CanInstall => Session.Has(Permission.ManageBackups);

    [ObservableProperty]
    public partial string UpdateStatus { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsInstall))]
    [NotifyPropertyChangedFor(nameof(ShowsAskAdmin))]
    public partial bool HasUpdate { get; set; }

    public bool ShowsInstall => HasUpdate && CanInstall;

    public bool ShowsAskAdmin => HasUpdate && !CanInstall;

    [ObservableProperty]
    public partial string UpdateTitle { get; set; }

    [ObservableProperty]
    public partial string UpdateMeta { get; set; }

    [ObservableProperty]
    public partial string UpdateNotes { get; set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    [ObservableProperty]
    public partial string DownloadText { get; set; }

    private void OnAvailableChanged(object? sender, EventArgs e) =>
        MainThread.BeginInvokeOnMainThread(ShowAvailable);

    private void ShowAvailable()
    {
        var update = _updates.Available;
        HasUpdate = update is not null;

        if (update is null)
            return;

        UpdateTitle = update.Title;
        UpdateMeta = $"Version {update.VersionDisplay} · published {update.PublishedDisplay} · {update.SizeDisplay}";
        UpdateNotes = string.IsNullOrWhiteSpace(update.Notes) ? "No notes were written for this version." : Plain(update.Notes);
    }

    [RelayCommand]
    private Task CheckForUpdatesAsync() => RunAsync(async () =>
    {
        ClearMessages();
        UpdateStatus = "Checking…";

        var result = await _updates.CheckAsync();

        UpdateStatus = result.Message;
        ShowAvailable();
    });

    [RelayCommand]
    private Task InstallUpdateAsync() => RunAsync(async () =>
    {
        ClearMessages();

        var user = Session.CurrentUser;
        var update = _updates.Available;

        if (user is null || update is null)
            return;

        if (!CanInstall)
        {
            ShowError("Only an administrator can install updates.");
            return;
        }

        if (!await _dialogs.ConfirmAsync(
                $"Install version {update.VersionDisplay}",
                "The app will back up the database, close, install the new version and open again. " +
                "It takes a minute or two.\n\nMake sure no one else is using the app and that nothing is half-saved.",
                "Back up and install", "Not now"))
            return;

        // 1. Download and verify before touching anything.
        string installer;
        _download = new CancellationTokenSource();
        IsDownloading = true;
        DownloadProgress = 0;
        DownloadText = "Downloading…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                DownloadProgress = p;
                DownloadText = $"Downloading… {p:P0}";
            });

            installer = await _updates.DownloadAsync(update, progress, _download.Token);
        }
        catch (UpdateException ex)
        {
            ShowError(ex.Message);
            return;
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Download cancelled. Nothing was installed.");
            return;
        }
        finally
        {
            IsDownloading = false;
            _download?.Dispose();
            _download = null;
        }

        // 2. A backup the new version can be rolled back to.
        DownloadText = "Backing up the database…";
        var backup = await _data.CreateBackupAsync(user);

        if (!backup.Succeeded)
        {
            ShowError($"The update was not installed because the backup failed: {backup.Message}");
            return;
        }

        await _audit.WriteAsync(AuditActions.UpdateStarted, "Application", null, true,
            $"Updating from {AppVersion.Display} to {update.VersionDisplay}. {backup.Message}",
            user.Username, user.Id);

        // 3. Hand over to the installer, which reopens the app when it is done.
        try
        {
            _updates.StartInstaller(installer);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HelpViewModel] {ex}");
            ShowError("The installer could not be started. If Windows asked for permission, choose Yes and try again.");
            return;
        }

        Application.Current?.Quit();
    });

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    /// <summary>Release notes are written in Markdown; shown here as plain text.</summary>
    private static string Plain(string markdown) =>
        string.Join('\n', markdown.Replace("\r", string.Empty).Split('\n')
            .Select(line => line.TrimStart('#', ' ').Replace("**", string.Empty).Replace("`", string.Empty))
            .Select(line => line.StartsWith("- ") || line.StartsWith("* ") ? "•  " + line[2..] : line))
        .Trim();

    // ============================================================ requests

    public IReadOnlyList<EnumOption> KindOptions { get; }

    public IReadOnlyList<EnumOption> PriorityOptions { get; }

    public IReadOnlyList<string> ScreenOptions { get; }

    [ObservableProperty]
    public partial EnumOption SelectedKind { get; set; }

    [ObservableProperty]
    public partial EnumOption SelectedPriority { get; set; }

    [ObservableProperty]
    public partial string SelectedScreen { get; set; }

    [ObservableProperty]
    public partial string RequestTitle { get; set; }

    [ObservableProperty]
    public partial string RequestDetails { get; set; }

    [ObservableProperty]
    public partial bool IncludeDatabase { get; set; }

    /// <summary>A database copy is the whole payroll; only the backup holder may hand one out.</summary>
    public bool CanIncludeDatabase => Session.Has(Permission.ManageBackups);

    private readonly List<string> _attachments = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachments))]
    public partial string AttachmentsText { get; set; }

    public bool HasAttachments => _attachments.Count > 0;

    public ObservableCollection<SupportRequestRow> Requests { get; } = [];

    [ObservableProperty]
    public partial bool HasRequests { get; set; }

    [RelayCommand]
    private async Task AddAttachmentsAsync()
    {
        ClearMessages();

        try
        {
            var picked = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = "Choose screenshots or files that show the problem"
            });

            foreach (var file in picked ?? [])
            {
                if (file?.FullPath is { Length: > 0 } path && !_attachments.Contains(path, StringComparer.OrdinalIgnoreCase))
                    _attachments.Add(path);
            }
        }
        catch (Exception ex)
        {
            ShowError("The file could not be added: " + ex.Message);
        }

        ShowAttachments();
    }

    [RelayCommand]
    private void ClearAttachments()
    {
        _attachments.Clear();
        ShowAttachments();
    }

    private void ShowAttachments() =>
        AttachmentsText = string.Join(", ", _attachments.Select(Path.GetFileName));

    [RelayCommand]
    private Task SaveRequestAsync() => RunAsync(async () =>
    {
        ClearMessages();

        var user = Session.CurrentUser;
        if (user is null)
            return;

        var result = await _requests.CreateAsync(new SupportRequestDraft(
            SelectedKind.As<SupportRequestKind>(),
            SelectedPriority.As<SupportRequestPriority>(),
            RequestTitle,
            RequestDetails,
            SelectedScreen,
            _attachments.ToList(),
            IncludeDatabase && CanIncludeDatabase), user);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        RequestTitle = string.Empty;
        RequestDetails = string.Empty;
        IncludeDatabase = false;
        _attachments.Clear();
        ShowAttachments();

        ShowStatus(result.Message);
        await LoadRequestsAsync();

        if (result.Request is { } saved)
            Reveal(saved.PackagePath);
    });

    [RelayCommand]
    private void ShowFile(SupportRequestRow? row)
    {
        ClearMessages();

        if (row is null)
            return;

        if (!row.HasFile)
        {
            ShowError($"{row.Reference}.zip is no longer in {_requests.Folder}.");
            return;
        }

        Reveal(row.Request.PackagePath);
    }

    [RelayCommand]
    private void OpenRequestsFolder()
    {
        ClearMessages();
        Directory.CreateDirectory(_requests.Folder);
        Reveal(_requests.Folder);
    }

    /// <summary>Opens Explorer with the file selected, ready to drag into Messenger or e-mail.</summary>
    private void Reveal(string path)
    {
        try
        {
#if WINDOWS
            var arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"";
            System.Diagnostics.Process.Start("explorer.exe", arguments);
#endif
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HelpViewModel] {ex}");
            ShowError($"The folder could not be opened. The file is {path}");
        }
    }

    private async Task LoadRequestsAsync()
    {
        var rows = await _requests.GetRecentAsync();

        Requests.Clear();
        foreach (var request in rows)
            Requests.Add(new SupportRequestRow(request));

        HasRequests = Requests.Count > 0;
    }

    // ============================================================ load

    public Task LoadAsync() => RunAsync(async () =>
    {
        await LoadRequestsAsync();

        // The sign-in check may have already found one; otherwise look now.
        if (_updates.Available is null)
        {
            var result = await _updates.CheckAsync();
            UpdateStatus = result.Message;
        }
        else
        {
            UpdateStatus = $"Version {_updates.Available.VersionDisplay} is available.";
        }

        ShowAvailable();
    });

    public void Dispose()
    {
        _updates.AvailableChanged -= OnAvailableChanged;
        _download?.Cancel();
    }
}
