using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PAYROLLSystemApp.Models;
using PAYROLLSystemApp.Services;

namespace PAYROLLSystemApp.ViewModels;

/// <summary>One backup file as the list renders it.</summary>
public sealed class BackupRow
{
    public BackupRow(BackupInfo info) => Info = info;

    public BackupInfo Info { get; }

    public string FileName => Info.FileName;

    public string TakenDisplay => Info.TakenDisplay;

    public string Detail => $"{Info.KindDisplay} · {Info.SizeDisplay} · {Info.StatusDisplay}";

    public bool IsRestorable => Info.IsReadable;

    public Color StatusColor => Info.IsReadable
        ? Color.FromArgb("#6B7085")
        : Color.FromArgb("#B91C1C");
}

/// <summary>One payroll year as the archive list renders it.</summary>
public sealed class ArchiveYearRow
{
    public ArchiveYearRow(PayrollYear year) => Year = year;

    public PayrollYear Year { get; }

    public int Value => Year.Year;

    public string Title => Year.Year.ToString();

    public string Summary => Year.SummaryDisplay;

    public string Status => Year.StatusDisplay;

    public bool CanArchive => !Year.IsPurged && Year.RunCount > 0 && !Year.HasOpenRuns;

    public bool CanPurge => Year.CanPurge;

    /// <summary>Shown instead of the purge button, so the refusal explains itself.</summary>
    public string BlockedReason => Year.PurgeBlockedReason;

    public bool HasBlockedReason => !string.IsNullOrWhiteSpace(BlockedReason);

    public Color StatusColor => Year.IsPurged
        ? Color.FromArgb("#B45309")
        : Year.IsArchived
            ? Color.FromArgb("#15803D")
            : Color.FromArgb("#6B7085");
}

/// <summary>
/// Section 2.9 — backup, restore and archival (FR-093, FR-094).
///
/// <para><b>Two destructive actions on one screen</b>, and both are confirmed
/// twice over: the dialog names what will be replaced or removed, and the
/// service refuses anything it cannot verify first (NFR-022). Restoring signs
/// the user out, so the screen says so before it is started rather than leaving
/// them to discover it.</para>
/// </summary>
public sealed partial class DataManagementViewModel : BaseViewModel
{
    private readonly IDataManagementService _data;
    private readonly IDialogService _dialogs;

    public DataManagementViewModel(
        IDataManagementService data, IDialogService dialogs, ISessionService session)
        : base(session)
    {
        _data = data;
        _dialogs = dialogs;

        Title = "Backup & archive";
        PolicySummary = string.Empty;
        BackupSummary = string.Empty;
        ArchiveSummary = string.Empty;
        LastResultDetail = string.Empty;
        BackupFolder = DataManagementService.BackupFolder();
        ArchiveFolder = DataManagementService.ArchiveFolder();
    }

    public ObservableCollection<BackupRow> Backups { get; } = new();

    public ObservableCollection<ArchiveYearRow> Years { get; } = new();

    // ======================================================= gate / state

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGranted))]
    public partial bool IsDenied { get; set; }

    public bool IsGranted => !IsDenied;

    [ObservableProperty]
    public partial string BackupFolder { get; set; }

    [ObservableProperty]
    public partial string ArchiveFolder { get; set; }

    [ObservableProperty]
    public partial string PolicySummary { get; set; }

    [ObservableProperty]
    public partial string BackupSummary { get; set; }

    [ObservableProperty]
    public partial string ArchiveSummary { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultDetail))]
    public partial string LastResultDetail { get; set; }

    public bool HasResultDetail => !string.IsNullOrWhiteSpace(LastResultDetail);

    // ------------------------------------------------------- the policy

    [ObservableProperty]
    public partial bool IsAutomaticEnabled { get; set; }

    [ObservableProperty]
    public partial string IntervalDays { get; set; } = "1";

    [ObservableProperty]
    public partial string KeepCount { get; set; } = "14";

    [ObservableProperty]
    public partial string RetentionYears { get; set; } = "5";

    // =========================================================== loading

    public async Task LoadAsync()
    {
        if (!await Session.RequireAsync(Permission.ManageBackups, "open backup and archive"))
        {
            IsDenied = true;
            Backups.Clear();
            Years.Clear();
            return;
        }

        IsDenied = false;

        await RunAsync(async () =>
        {
            // FR-093. The "schedule" is checked here rather than run by a timer:
            // a desktop application is not running at 2 a.m., and a backup that
            // silently never happens is worse than one taken on first use.
            if (Session.CurrentUser is { } user)
            {
                var automatic = await _data.RunDueBackupAsync(user);

                if (automatic is { Succeeded: true })
                    ShowStatus($"An automatic backup was due. {automatic.Message}");
                else if (automatic is { Succeeded: false })
                    ShowError(automatic.Message);
            }

            await ReloadAsync();
        });
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        var settings = await _data.GetSettingsAsync();

        IsAutomaticEnabled = settings.IsAutomaticEnabled;
        IntervalDays = settings.IntervalDays.ToString();
        KeepCount = settings.KeepCount.ToString();
        RetentionYears = settings.RetentionYears.ToString();

        PolicySummary = settings.IsAutomaticEnabled
            ? $"Backing up {settings.IntervalDisplay}, keeping the last {settings.KeepCount} automatic copies. " +
              $"Last backup: {settings.LastBackupDisplay}."
            : $"Automatic backup is off. Last backup: {settings.LastBackupDisplay}.";

        var backups = await _data.GetBackupsAsync();

        Backups.Clear();
        foreach (var backup in backups)
            Backups.Add(new BackupRow(backup));

        var unreadable = backups.Count(b => !b.IsReadable);

        BackupSummary = backups.Count == 0
            ? "No backups yet."
            : $"{backups.Count} backup(s)" +
              (unreadable > 0 ? $" · {unreadable} cannot be opened" : " · all verified");

        var years = await _data.GetYearsAsync();

        Years.Clear();
        foreach (var year in years)
            Years.Add(new ArchiveYearRow(year));

        ArchiveSummary = years.Count == 0
            ? "No payroll has been run yet."
            : $"{years.Count} payroll year(s) · {years.Count(y => y.IsArchived)} archived · " +
              $"{years.Count(y => y.IsPurged)} removed from the live database";
    }

    // ====================================================== FR-093 policy

    [RelayCommand]
    private Task SavePolicyAsync() => RunAsync(async () =>
    {
        var user = Session.CurrentUser;
        if (user is null)
            return;

        if (!int.TryParse(IntervalDays, out var interval) ||
            !int.TryParse(KeepCount, out var keep) ||
            !int.TryParse(RetentionYears, out var retention))
        {
            ShowError("Interval, keep count and retention must all be whole numbers.");
            return;
        }

        var settings = await _data.GetSettingsAsync();

        settings.IsAutomaticEnabled = IsAutomaticEnabled;
        settings.IntervalDays = interval;
        settings.KeepCount = keep;
        settings.RetentionYears = retention;

        var result = await _data.SaveSettingsAsync(settings, user);

        if (!result.Succeeded)
        {
            ShowError(result.Message);
            return;
        }

        ShowStatus(result.Message);
        await ReloadAsync();
    });

    // ====================================================== FR-093 backup

    [RelayCommand]
    private Task BackUpNowAsync() => RunAsync(async () =>
    {
        var user = Session.CurrentUser;
        if (user is null)
            return;

        var result = await _data.CreateBackupAsync(user);

        Report(result);
        await ReloadAsync();
    });

    [RelayCommand]
    private Task RestoreAsync(BackupRow? row) => RunAsync(async () =>
    {
        var user = Session.CurrentUser;
        if (user is null || row is null)
            return;

        if (!row.IsRestorable)
        {
            ShowError($"{row.FileName} cannot be opened, so it cannot be restored.");
            return;
        }

        // NFR-022. The most destructive thing this application can do, so the
        // dialog says exactly what is being replaced and what it will cost.
        var confirmed = await _dialogs.ConfirmAsync(
            "Restore this backup?",
            $"Every employee, payslip and audit entry in the live database will be replaced by " +
            $"{row.FileName}, taken {row.TakenDisplay} and holding {row.Info.Contents}.\n\n" +
            "The current database is backed up first, and you will be signed out.",
            "Replace the database", "Cancel");

        if (!confirmed)
            return;

        var result = await _data.RestoreAsync(row.Info.FilePath, user);

        // A successful restore has already signed the session out, so there is
        // no screen left to report onto.
        if (!result.Succeeded)
        {
            ShowError(result.Message);
            await ReloadAsync();
        }
    });

    [RelayCommand]
    private Task DeleteBackupAsync(BackupRow? row) => RunAsync(async () =>
    {
        var user = Session.CurrentUser;
        if (user is null || row is null)
            return;

        var confirmed = await _dialogs.ConfirmAsync(
            "Delete this backup?",
            $"{row.FileName} will be deleted from disk. This cannot be undone.",
            "Delete", "Keep it");

        if (!confirmed)
            return;

        Report(await _data.DeleteBackupAsync(row.Info.FilePath, user));
        await ReloadAsync();
    });

    // ===================================================== FR-094 archive

    [RelayCommand]
    private Task ArchiveYearAsync(ArchiveYearRow? row) => RunAsync(async () =>
    {
        var user = Session.CurrentUser;
        if (user is null || row is null)
            return;

        var confirmed = await _dialogs.ConfirmAsync(
            $"Archive {row.Title}?",
            $"{row.Summary} will be written to {ArchiveFolder}\\{row.Title} as CSV files. " +
            "Nothing is removed from the live database by archiving.",
            "Write the archive", "Cancel");

        if (!confirmed)
            return;

        Report(await _data.ArchiveYearAsync(row.Value, user));
        await ReloadAsync();
    });

    [RelayCommand]
    private Task PurgeYearAsync(ArchiveYearRow? row) => RunAsync(async () =>
    {
        var user = Session.CurrentUser;
        if (user is null || row is null)
            return;

        // NFR-022, NFR-037. Typing the year is deliberate friction: this removes
        // posted payroll, and a mis-click should not be able to do it.
        var typed = await _dialogs.PromptAsync(
            $"Remove {row.Title} from the live database?",
            $"{row.Summary} will be deleted. The archive stays on disk and the whole database is " +
            $"backed up first.\n\nType {row.Title} to confirm.",
            "Remove", "Cancel", placeholder: row.Title);

        if (typed?.Trim() != row.Title)
        {
            if (typed is not null)
                ShowError("The year did not match, so nothing was removed.");

            return;
        }

        Report(await _data.PurgeYearAsync(row.Value, user));
        await ReloadAsync();
    });

    // =====================================================================

    private void Report(DataOperationResult result)
    {
        LastResultDetail = result.Detail;

        if (result.Succeeded)
            ShowStatus(result.Message);
        else
            ShowError(result.Message);
    }
}
