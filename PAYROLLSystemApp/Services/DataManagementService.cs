using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;
using SQLite;

namespace PAYROLLSystemApp.Services;

/// <summary>One backup file on disk, and whether it can actually be restored.</summary>
public sealed record BackupInfo(
    string FilePath,
    DateTime TakenLocal,
    long SizeBytes,
    bool IsAutomatic,
    bool IsReadable,
    string Contents)
{
    public string FileName => Path.GetFileName(FilePath);

    public string TakenDisplay => TakenLocal.ToString("ddd, dd MMM yyyy HH:mm");

    public string SizeDisplay => SizeBytes < 1024 * 1024
        ? $"{SizeBytes / 1024d:0.#} KB"
        : $"{SizeBytes / (1024d * 1024d):0.##} MB";

    public string KindDisplay => IsAutomatic ? "Automatic" : "Manual";

    /// <summary>What the file is worth restoring: the row counts, or why it is not.</summary>
    public string StatusDisplay => IsReadable ? Contents : $"Unreadable — {Contents}";
}

/// <summary>Outcome of a backup, restore or archive operation.</summary>
public sealed record DataOperationResult(bool Succeeded, string Message, string Detail = "")
{
    public static DataOperationResult Fail(string message) => new(false, message);

    public static DataOperationResult Ok(string message, string detail = "") => new(true, message, detail);
}

/// <summary>
/// FR-094. One payroll year as the archive screen sees it: how much is in it,
/// whether it has been written out, and whether it may be removed.
/// </summary>
public sealed record PayrollYear(
    int Year,
    int RunCount,
    int PayslipCount,
    decimal TotalNet,
    bool HasOpenRuns,
    ArchiveRecord? Archive,
    bool IsWithinRetention)
{
    public bool IsArchived => Archive is not null;

    public bool IsPurged => Archive?.IsPurged == true;

    /// <summary>
    /// NFR-007, NFR-037. A year may only be removed once it has been written
    /// out, every run in it is settled, and it is older than the retention
    /// window.
    /// </summary>
    public bool CanPurge => IsArchived && !IsPurged && !HasOpenRuns && !IsWithinRetention;

    public string SummaryDisplay =>
        $"{RunCount} run(s) · {PayslipCount} payslip(s) · net {PayrollRounding.Format(TotalNet)}";

    public string StatusDisplay =>
        IsPurged ? "Archived and removed from the live database"
        : IsArchived ? "Archived"
        : "Not archived";

    /// <summary>Why the year cannot be purged, in the words the screen shows.</summary>
    public string PurgeBlockedReason =>
        IsPurged ? "This year has already been removed."
        : !IsArchived ? "Archive the year first — nothing is removed that has not been written out."
        : HasOpenRuns ? "This year still has runs that are not posted or cancelled."
        : IsWithinRetention ? "This year is inside the retention window and stays online (NFR-007)."
        : string.Empty;
}

public interface IDataManagementService
{
    // --------------------------------------------------------- FR-093

    Task<BackupSettings> GetSettingsAsync();

    Task<DataOperationResult> SaveSettingsAsync(BackupSettings settings, User performedBy);

    Task<IReadOnlyList<BackupInfo>> GetBackupsAsync();

    Task<DataOperationResult> CreateBackupAsync(User performedBy, bool automatic = false);

    /// <summary>Takes an automatic backup if the interval says one is due, and otherwise does nothing.</summary>
    Task<DataOperationResult?> RunDueBackupAsync(User performedBy);

    Task<DataOperationResult> RestoreAsync(string filePath, User performedBy);

    Task<DataOperationResult> DeleteBackupAsync(string filePath, User performedBy);

    // --------------------------------------------------------- FR-094

    Task<IReadOnlyList<PayrollYear>> GetYearsAsync();

    Task<DataOperationResult> ArchiveYearAsync(int year, User performedBy);

    Task<DataOperationResult> PurgeYearAsync(int year, User performedBy);
}

/// <summary>
/// Section 2.9 (FR-093, FR-094). Backing up the database, restoring it, and
/// writing a closed payroll year out to files.
///
/// <para><b>A backup is a copy of the file, not an export.</b> SQLite is one
/// file; copying it captures every table, index and row exactly, including the
/// audit log, and restores in one move. Rebuilding the database from exported
/// rows would be a second implementation of the schema that could drift from the
/// first, and the drift would only be discovered on the day somebody needed the
/// restore to work.</para>
///
/// <para><b>Every copy is verified before it is offered.</b> The file is opened
/// read-only and asked for its integrity and its row counts, so an unreadable
/// backup is discovered when it is taken rather than when it is needed. Backups
/// that fail are listed and named as unreadable rather than hidden.</para>
///
/// <para><b>Restoring signs you out.</b> The account holding the session came
/// out of the database that is being replaced; in the restored copy it may not
/// exist, or may hold a different role. Carrying the session across would be
/// carrying an authorisation that the restored data never granted.</para>
/// </summary>
public sealed class DataManagementService : IDataManagementService
{
    private const string BackupExtension = ".db3";
    private const string AutomaticMarker = "auto";
    private const string ManualMarker = "manual";

    private readonly PayrollDatabase _database;
    private readonly ISessionService _session;
    private readonly IAuditService _audit;

    public DataManagementService(
        PayrollDatabase database, ISessionService session, IAuditService audit)
    {
        _database = database;
        _session = session;
        _audit = audit;
    }

    // =====================================================================
    // Where the files live
    // =====================================================================

    /// <summary>
    /// Backups go beside the exports, under the user's Documents folder where
    /// the platform has one. Deliberately <b>not</b> next to the live database:
    /// the application's data directory is what an uninstall removes.
    /// </summary>
    public static string BackupFolder() => Folder("Backups");

    public static string ArchiveFolder() => Folder("Archive");

    private static string Folder(string name)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var root = string.IsNullOrWhiteSpace(documents) || !Directory.Exists(documents)
            ? FileSystem.AppDataDirectory
            : documents;

        return Path.Combine(root, "Payroll MS", name);
    }

    // =====================================================================
    // FR-093 — settings
    // =====================================================================

    public async Task<BackupSettings> GetSettingsAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var settings = await connection.Table<BackupSettings>()
            .Where(s => s.Id == BackupSettings.SingletonId)
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        if (settings is not null)
            return settings;

        settings = new BackupSettings();
        await connection.InsertAsync(settings).ConfigureAwait(false);

        return settings;
    }

    public async Task<DataOperationResult> SaveSettingsAsync(BackupSettings settings, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageBackups))
            return await RefuseAsync(performedBy, "change the backup policy").ConfigureAwait(false);

        if (settings.IntervalDays is < 1 or > 90)
            return DataOperationResult.Fail("The interval must be between 1 and 90 days.");

        if (settings.KeepCount is < 1 or > 200)
            return DataOperationResult.Fail("Keep between 1 and 200 automatic backups.");

        if (settings.RetentionYears is < 1 or > 20)
            return DataOperationResult.Fail("Retention must be between 1 and 20 years.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        settings.Id = BackupSettings.SingletonId;
        settings.UpdatedUtc = DateTime.UtcNow;

        await connection.InsertOrReplaceAsync(settings).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.BackupSettingsUpdated, nameof(BackupSettings),
            settings.Id, true,
            $"Automatic backup {(settings.IsAutomaticEnabled ? "on" : "off")}, " +
            $"{settings.IntervalDisplay}, keeping {settings.KeepCount}, " +
            $"retaining {settings.RetentionYears} year(s) online.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return DataOperationResult.Ok("Backup policy saved.");
    }

    // =====================================================================
    // FR-093 — backup
    // =====================================================================

    public Task<IReadOnlyList<BackupInfo>> GetBackupsAsync() => Task.Run(() =>
    {
        var folder = BackupFolder();

        if (!Directory.Exists(folder))
            return (IReadOnlyList<BackupInfo>)Array.Empty<BackupInfo>();

        return Directory.GetFiles(folder, $"*{BackupExtension}")
            .Select(Describe)
            .OrderByDescending(b => b.TakenLocal)
            .ToList();
    });

    public async Task<DataOperationResult> CreateBackupAsync(User performedBy, bool automatic = false)
    {
        if (!performedBy.Can(Permission.ManageBackups))
            return await RefuseAsync(performedBy, "back the database up").ConfigureAwait(false);

        try
        {
            var folder = BackupFolder();
            Directory.CreateDirectory(folder);

            var marker = automatic ? AutomaticMarker : ManualMarker;
            var path = Path.Combine(folder,
                $"payroll_{DateTime.Now:yyyy-MM-dd_HHmmss}_{marker}{BackupExtension}");

            await CopyDatabaseAsync(path).ConfigureAwait(false);

            var info = Describe(path);

            if (!info.IsReadable)
            {
                // A backup that cannot be opened is worse than none, because it
                // is counted as protection that is not there.
                File.Delete(path);

                return DataOperationResult.Fail(
                    $"The copy could not be verified and was discarded: {info.Contents}");
            }

            var settings = await GetSettingsAsync().ConfigureAwait(false);
            settings.LastBackupUtc = DateTime.UtcNow;

            var connection = await _database.GetConnectionAsync().ConfigureAwait(false);
            await connection.InsertOrReplaceAsync(settings).ConfigureAwait(false);

            var swept = Sweep(settings.KeepCount);

            await _audit.WriteAsync(AuditActions.BackupCreated, "Backup", null, true,
                $"{(automatic ? "Automatic" : "Manual")} backup {Path.GetFileName(path)} " +
                $"({info.SizeDisplay}); {info.Contents}." +
                (swept > 0 ? $" {swept} older automatic backup(s) removed by the keep-{settings.KeepCount} policy." : ""),
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return DataOperationResult.Ok(
                $"Backed up to {Path.GetFileName(path)}.", $"{info.Contents} · {info.SizeDisplay}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataManagementService] {ex}");

            return DataOperationResult.Fail(
                "The backup could not be written. Check that the destination folder is available.");
        }
    }

    public async Task<DataOperationResult?> RunDueBackupAsync(User performedBy)
    {
        if (!performedBy.Can(Permission.ManageBackups))
            return null;

        var settings = await GetSettingsAsync().ConfigureAwait(false);

        return settings.IsDue
            ? await CreateBackupAsync(performedBy, automatic: true).ConfigureAwait(false)
            : null;
    }

    /// <summary>
    /// Copies the live database, with its write-ahead log folded in first.
    ///
    /// <para><c>wal_checkpoint(TRUNCATE)</c> moves everything sitting in the WAL
    /// into the main file. Without it the copy is missing whatever had not been
    /// checkpointed — which on a quiet system is nothing, and on a busy one is
    /// the last few minutes of payroll.</para>
    /// </summary>
    private async Task CopyDatabaseAsync(string destination)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        try
        {
            await connection.ExecuteScalarAsync<string>("PRAGMA wal_checkpoint(TRUNCATE)")
                .ConfigureAwait(false);
        }
        catch (SQLiteException)
        {
            // A database in the default journal mode has no WAL to fold in.
        }

        File.Copy(_database.DatabasePath, destination, overwrite: true);
    }

    /// <summary>
    /// Opens a backup read-only and asks it what it holds.
    ///
    /// <para>Row counts rather than a bare integrity check, because the question
    /// somebody actually has in front of this list is "how much is in it" — a
    /// structurally perfect copy of an empty database restores just as
    /// destructively as a corrupt one.</para>
    /// </summary>
    private static BackupInfo Describe(string path)
    {
        var file = new FileInfo(path);
        var automatic = Path.GetFileNameWithoutExtension(path).EndsWith(AutomaticMarker, StringComparison.Ordinal);
        var taken = file.Exists ? file.CreationTime : DateTime.Now;

        try
        {
            using var connection = new SQLiteConnection(path, SQLiteOpenFlags.ReadOnly);

            var integrity = connection.ExecuteScalar<string>("PRAGMA integrity_check");

            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
                return new BackupInfo(path, taken, file.Length, automatic, false, integrity ?? "corrupt");

            var employees = Count(connection, "employees");
            var payslips = Count(connection, "payslips");
            var runs = Count(connection, "payroll_runs");

            return new BackupInfo(path, taken, file.Length, automatic, true,
                $"{employees} employee(s), {runs} run(s), {payslips} payslip(s)");
        }
        catch (Exception ex)
        {
            return new BackupInfo(path, taken, file.Length, automatic, false, ex.GetType().Name);
        }
    }

    private static int Count(SQLiteConnection connection, string table)
    {
        try
        {
            return connection.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table}");
        }
        catch (SQLiteException)
        {
            // A backup taken before that table existed. Not a fault in the copy.
            return 0;
        }
    }

    /// <summary>
    /// Removes automatic backups past the keep count, oldest first.
    ///
    /// <para><b>Manual backups are never swept.</b> Somebody took those
    /// deliberately, usually immediately before doing something they were
    /// nervous about, and a retention policy quietly deleting the one copy
    /// somebody made on purpose is exactly the wrong behaviour.</para>
    /// </summary>
    private static int Sweep(int keep)
    {
        var folder = BackupFolder();

        if (!Directory.Exists(folder))
            return 0;

        var automatic = Directory.GetFiles(folder, $"*{AutomaticMarker}{BackupExtension}")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTime)
            .Skip(Math.Max(1, keep))
            .ToList();

        var removed = 0;

        foreach (var file in automatic)
        {
            try
            {
                file.Delete();
                removed++;
            }
            catch (IOException)
            {
                // Locked or already gone. Not worth failing the backup over.
            }
        }

        return removed;
    }

    public async Task<DataOperationResult> DeleteBackupAsync(string filePath, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageBackups))
            return await RefuseAsync(performedBy, "delete a backup").ConfigureAwait(false);

        if (!IsInsideBackupFolder(filePath))
            return DataOperationResult.Fail("That file is not one of this system's backups.");

        try
        {
            File.Delete(filePath);

            await _audit.WriteAsync(AuditActions.BackupDeleted, "Backup", null, true,
                $"Deleted backup {Path.GetFileName(filePath)}.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return DataOperationResult.Ok($"Deleted {Path.GetFileName(filePath)}.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataManagementService] {ex}");

            return DataOperationResult.Fail("The file could not be deleted.");
        }
    }

    // =====================================================================
    // FR-093 — restore
    // =====================================================================

    public async Task<DataOperationResult> RestoreAsync(string filePath, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageBackups))
            return await RefuseAsync(performedBy, "restore the database").ConfigureAwait(false);

        if (!IsInsideBackupFolder(filePath) || !File.Exists(filePath))
            return DataOperationResult.Fail("That backup is no longer where it was saved.");

        var info = Describe(filePath);

        if (!info.IsReadable)
            return DataOperationResult.Fail($"That backup cannot be opened: {info.Contents}");

        // The state about to be replaced is worth keeping. A restore of the
        // wrong file is otherwise unrecoverable, and it is the single most
        // destructive thing this application can do.
        var safety = await CreateBackupAsync(performedBy, automatic: true).ConfigureAwait(false);

        if (!safety.Succeeded)
        {
            return DataOperationResult.Fail(
                "The current database could not be backed up first, so the restore was not attempted. " +
                safety.Message);
        }

        // Written to the outgoing database, so the copy just taken carries the
        // record of why it was taken.
        await _audit.WriteAsync(AuditActions.BackupRestored, "Backup", null, true,
            $"About to restore {Path.GetFileName(filePath)} ({info.Contents}).",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        try
        {
            await _database.CloseAsync().ConfigureAwait(false);

            File.Copy(filePath, _database.DatabasePath, overwrite: true);

            // Any write-ahead log beside the live file belongs to the database
            // that was just replaced. Left in place it would be replayed over
            // the restored one.
            foreach (var sidecar in new[] { "-wal", "-shm" })
            {
                var stale = _database.DatabasePath + sidecar;

                if (File.Exists(stale))
                    File.Delete(stale);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataManagementService] {ex}");

            return DataOperationResult.Fail(
                "The database could not be replaced. Nothing was changed; the file may be in use.");
        }

        // Reopening runs schema creation and seeding, so a backup taken by an
        // older release gains any tables added since.
        await _database.GetConnectionAsync().ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.BackupRestored, "Backup", null, true,
            $"Restored {Path.GetFileName(filePath)} taken {info.TakenDisplay} ({info.Contents}). " +
            $"The database it replaced was backed up first.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        await _session.SignOutAsync(SessionEndReason.DatabaseRestored).ConfigureAwait(false);

        return DataOperationResult.Ok(
            $"Restored {Path.GetFileName(filePath)}.", info.Contents);
    }

    private static bool IsInsideBackupFolder(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var folder = Path.GetFullPath(BackupFolder());
        var file = Path.GetFullPath(filePath);

        return file.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
               Path.GetExtension(file).Equals(BackupExtension, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    // FR-094 — archival
    // =====================================================================

    public async Task<IReadOnlyList<PayrollYear>> GetYearsAsync()
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var runs = await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false);
        var payslips = await connection.Table<Payslip>().ToListAsync().ConfigureAwait(false);
        var archives = await connection.Table<ArchiveRecord>().ToListAsync().ConfigureAwait(false);
        var settings = await GetSettingsAsync().ConfigureAwait(false);

        var oldestOnline = DateTime.Today.Year - Math.Max(1, settings.RetentionYears) + 1;

        var byYear = runs.GroupBy(r => r.PeriodYear).ToList();

        // A year with an archive but no runs left is a purged year, and it still
        // belongs on the list — otherwise the record of what was removed
        // disappears along with the rows.
        foreach (var year in archives.Select(a => a.Year).Distinct())
        {
            if (byYear.All(g => g.Key != year))
                byYear.Add(new List<PayrollRun>().GroupBy(_ => year).First());
        }

        return byYear
            .Select(group =>
            {
                var ids = group.Select(r => r.Id).ToHashSet();
                var slips = payslips.Where(p => ids.Contains(p.PayrollRunId)).ToList();

                return new PayrollYear(
                    Year: group.Key,
                    RunCount: group.Count(),
                    PayslipCount: slips.Count,
                    TotalNet: slips.Sum(p => p.NetPay),
                    HasOpenRuns: group.Any(r => !r.IsPosted && !r.IsCancelled),
                    Archive: archives.Where(a => a.Year == group.Key)
                        .OrderByDescending(a => a.CreatedUtc)
                        .FirstOrDefault(),
                    IsWithinRetention: group.Key >= oldestOnline);
            })
            .OrderByDescending(y => y.Year)
            .ToList();
    }

    /// <summary>
    /// FR-094. Writes a year's payroll out as files that outlive the database.
    ///
    /// <para><b>CSV, not a database copy.</b> An archive is read years later, by
    /// somebody answering a question from an auditor or a former employee, quite
    /// possibly without this application. A folder of readable tables serves
    /// that; a <c>.db3</c> requires the thing that wrote it.</para>
    /// </summary>
    public async Task<DataOperationResult> ArchiveYearAsync(int year, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageBackups))
            return await RefuseAsync(performedBy, "archive a payroll year").ConfigureAwait(false);

        var years = await GetYearsAsync().ConfigureAwait(false);
        var target = years.FirstOrDefault(y => y.Year == year);

        if (target is null || target.RunCount == 0)
            return DataOperationResult.Fail($"There is no payroll in {year} to archive.");

        if (target.HasOpenRuns)
        {
            return DataOperationResult.Fail(
                $"{year} still has runs that are neither posted nor cancelled. " +
                "Settle them before archiving, or the archive records a figure that later changes.");
        }

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var runs = (await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false))
            .Where(r => r.PeriodYear == year)
            .OrderBy(r => r.PayDate)
            .ToList();

        var runIds = runs.Select(r => r.Id).ToHashSet();

        var payslips = (await connection.Table<Payslip>().ToListAsync().ConfigureAwait(false))
            .Where(p => runIds.Contains(p.PayrollRunId))
            .OrderBy(p => p.PayDate).ThenBy(p => p.EmployeeName)
            .ToList();

        var payslipIds = payslips.Select(p => p.Id).ToHashSet();

        var lines = (await connection.Table<PayslipLine>().ToListAsync().ConfigureAwait(false))
            .Where(l => payslipIds.Contains(l.PayslipId))
            .OrderBy(l => l.PayslipId).ThenBy(l => l.Sequence)
            .ToList();

        var adjustments = (await connection.Table<PayrollAdjustment>().ToListAsync().ConfigureAwait(false))
            .Where(a => runIds.Contains(a.PayrollRunId))
            .ToList();

        try
        {
            var folder = Path.Combine(ArchiveFolder(), year.ToString());
            Directory.CreateDirectory(folder);

            await WriteTableAsync(folder, "payroll-runs", runs).ConfigureAwait(false);
            await WriteTableAsync(folder, "payslips", payslips).ConfigureAwait(false);
            await WriteTableAsync(folder, "payslip-lines", lines).ConfigureAwait(false);
            await WriteTableAsync(folder, "adjustments", adjustments).ConfigureAwait(false);

            var record = new ArchiveRecord
            {
                Year = year,
                FolderPath = folder,
                RunCount = runs.Count,
                PayslipCount = payslips.Count,
                LineCount = lines.Count,
                TotalNet = payslips.Sum(p => p.NetPay),
                CreatedBy = performedBy.Username,
                CreatedUtc = DateTime.UtcNow
            };

            await WriteManifestAsync(folder, record).ConfigureAwait(false);

            await connection.InsertAsync(record).ConfigureAwait(false);

            await _audit.WriteAsync(AuditActions.YearArchived, nameof(ArchiveRecord), record.Id, true,
                $"Archived {year} to {folder}: {record.SummaryDisplay}.",
                performedBy.Username, performedBy.Id).ConfigureAwait(false);

            return DataOperationResult.Ok(
                $"{year} archived to {folder}.",
                $"{record.SummaryDisplay} · {lines.Count} payslip line(s)");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DataManagementService] {ex}");

            return DataOperationResult.Fail(
                "The archive could not be written, so nothing was recorded as archived.");
        }
    }

    /// <summary>
    /// FR-094. Removes a year's payroll rows from the live database.
    ///
    /// <para><b>The archive is checked again first.</b> The receipt says the
    /// files were written; this asks whether they are still there. An archive
    /// somebody has since moved or deleted is not an archive, and discovering
    /// that after the purge is discovering it too late.</para>
    ///
    /// <para><b>Only payroll is removed.</b> Employees, attendance and the audit
    /// log stay: the first two are read by later years, and the third is
    /// append-only by FR-092.</para>
    /// </summary>
    public async Task<DataOperationResult> PurgeYearAsync(int year, User performedBy)
    {
        if (!performedBy.Can(Permission.ManageBackups))
            return await RefuseAsync(performedBy, "remove an archived payroll year").ConfigureAwait(false);

        var years = await GetYearsAsync().ConfigureAwait(false);
        var target = years.FirstOrDefault(y => y.Year == year);

        if (target is null)
            return DataOperationResult.Fail($"There is no payroll in {year}.");

        if (!target.CanPurge)
            return DataOperationResult.Fail(target.PurgeBlockedReason);

        var archive = target.Archive!;

        if (!Directory.Exists(archive.FolderPath) ||
            Directory.GetFiles(archive.FolderPath, "*.csv").Length == 0)
        {
            return DataOperationResult.Fail(
                $"The archive folder for {year} is no longer at {archive.FolderPath}. " +
                "Archive the year again before removing it.");
        }

        // Belt and braces: the rows are about to stop existing, so the whole
        // database is copied first regardless of when the last backup was.
        var safety = await CreateBackupAsync(performedBy, automatic: true).ConfigureAwait(false);

        if (!safety.Succeeded)
            return DataOperationResult.Fail("The database could not be backed up first, so nothing was removed.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var runs = (await connection.Table<PayrollRun>().ToListAsync().ConfigureAwait(false))
            .Where(r => r.PeriodYear == year)
            .ToList();

        var runIds = runs.Select(r => r.Id).ToHashSet();

        var payslips = (await connection.Table<Payslip>().ToListAsync().ConfigureAwait(false))
            .Where(p => runIds.Contains(p.PayrollRunId))
            .ToList();

        var payslipIds = payslips.Select(p => p.Id).ToHashSet();

        var lines = (await connection.Table<PayslipLine>().ToListAsync().ConfigureAwait(false))
            .Where(l => payslipIds.Contains(l.PayslipId))
            .ToList();

        var adjustments = (await connection.Table<PayrollAdjustment>().ToListAsync().ConfigureAwait(false))
            .Where(a => runIds.Contains(a.PayrollRunId))
            .ToList();

        await connection.RunInTransactionAsync(transaction =>
        {
            foreach (var line in lines)
                transaction.Delete(line);

            foreach (var payslip in payslips)
                transaction.Delete(payslip);

            foreach (var adjustment in adjustments)
                transaction.Delete(adjustment);

            foreach (var run in runs)
                transaction.Delete(run);

            archive.IsPurged = true;
            archive.PurgedUtc = DateTime.UtcNow;
            transaction.Update(archive);
        }).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.YearPurged, nameof(ArchiveRecord), archive.Id, true,
            $"Removed {year} from the live database: {runs.Count} run(s), {payslips.Count} payslip(s), " +
            $"{lines.Count} line(s). The archive remains at {archive.FolderPath}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return DataOperationResult.Ok(
            $"{year} removed from the live database.",
            $"{runs.Count} run(s) and {payslips.Count} payslip(s). The archive is at {archive.FolderPath}.");
    }

    // =====================================================================

    /// <summary>
    /// Writes any table of rows as CSV, reading the columns off the type.
    ///
    /// <para>Reflection rather than a hand-written writer per table: an archive
    /// that silently omits a column added to <c>Payslip</c> later is an archive
    /// that is missing the very thing somebody eventually asks about.</para>
    /// </summary>
    private static async Task WriteTableAsync<T>(string folder, string name, IReadOnlyList<T> rows)
    {
        var properties = typeof(T)
            .GetProperties()
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Where(p => p.GetCustomAttributes(typeof(IgnoreAttribute), inherit: true).Length == 0)
            .ToList();

        var builder = new System.Text.StringBuilder();

        builder.AppendLine(string.Join(',', properties.Select(p => Escape(p.Name))));

        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', properties.Select(p =>
                Escape(p.GetValue(row) switch
                {
                    null => string.Empty,
                    DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss"),
                    decimal money => money.ToString("0.####"),
                    bool flag => flag ? "true" : "false",
                    var value => value.ToString() ?? string.Empty
                }))));
        }

        var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(builder.ToString());

        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);

        await File.WriteAllBytesAsync(Path.Combine(folder, $"{name}.csv"), bytes).ConfigureAwait(false);
    }

    private static async Task WriteManifestAsync(string folder, ArchiveRecord record)
    {
        var text = $"""
            Payroll archive — {record.Year}

            Written        {DateTime.Now:dd MMM yyyy HH:mm} by {record.CreatedBy}
            Payroll runs   {record.RunCount}
            Payslips       {record.PayslipCount}
            Payslip lines  {record.LineCount}
            Total net pay  {record.TotalNet:N2} PHP

            Files
              payroll-runs.csv    one row per payroll run, with its stored totals
              payslips.csv        one row per employee per run, as it was paid
              payslip-lines.csv   the itemisation behind each payslip, keyed by PayslipId
              adjustments.csv     one-off earnings and deductions applied to a run

            Every figure was snapshotted when the run was calculated, so this archive is
            what was actually paid rather than what the same inputs would produce today.
            Amounts are in Philippine pesos. Files are UTF-8 with a byte-order mark.
            """;

        await File.WriteAllTextAsync(Path.Combine(folder, "README.txt"), text).ConfigureAwait(false);
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        return value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private async Task<DataOperationResult> RefuseAsync(User performedBy, string attempted)
    {
        await _audit.WriteAsync(AuditActions.AccessDenied, "Backup", null, false,
            $"Role {performedBy.RoleDisplayName} is not permitted to {attempted}.",
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        return DataOperationResult.Fail($"You do not have permission to {attempted}.");
    }
}
