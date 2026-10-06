using System.IO.Compression;
using System.Text;
using PAYROLLSystemApp.Data;
using PAYROLLSystemApp.Models;

namespace PAYROLLSystemApp.Services;

public sealed record SupportRequestDraft(
    SupportRequestKind Kind,
    SupportRequestPriority Priority,
    string Title,
    string Details,
    string Screen,
    IReadOnlyList<string> Attachments,
    bool IncludeDatabase);

public sealed record SupportRequestResult(bool Succeeded, string Message, SupportRequest? Request = null);

public interface ISupportRequestService
{
    /// <summary>Documents\Payroll MS\Requests.</summary>
    string Folder { get; }

    Task<IReadOnlyList<SupportRequest>> GetRecentAsync(int take = 50);

    /// <summary>Writes the request into a zip ready to send, and records it.</summary>
    Task<SupportRequestResult> CreateAsync(SupportRequestDraft draft, User performedBy);
}

/// <summary>
/// The company's side of the revision workflow: a problem or change request is
/// written in the app, which packs it into one zip file with everything the
/// developer would otherwise have to ask for — version, screen, Windows
/// version, account role, the files they attached and, when asked for, a
/// verified copy of the database. The zip is sent by whatever the company
/// already uses (Messenger, e-mail, Drive); the app never sends anything itself.
/// </summary>
public sealed class SupportRequestService : ISupportRequestService
{
    /// <summary>Messenger and Gmail both stop at 25 MB.</summary>
    public const long SendLimitBytes = 25L * 1024 * 1024;

    private readonly PayrollDatabase _database;
    private readonly IDataManagementService _data;
    private readonly IAuditService _audit;

    public SupportRequestService(PayrollDatabase database, IDataManagementService data, IAuditService audit)
    {
        _database = database;
        _data = data;
        _audit = audit;
    }

    public string Folder =>
        Path.Combine(Path.GetDirectoryName(ReportService.ExportFolder())!, "Requests");

    public async Task<IReadOnlyList<SupportRequest>> GetRecentAsync(int take = 50)
    {
        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        return await connection.Table<SupportRequest>()
            .OrderByDescending(r => r.CreatedUtc)
            .Take(take)
            .ToListAsync()
            .ConfigureAwait(false);
    }

    public async Task<SupportRequestResult> CreateAsync(SupportRequestDraft draft, User performedBy)
    {
        var title = draft.Title.Trim();
        var details = draft.Details.Trim();

        if (title.Length == 0)
            return new(false, "Give the request a short title.");

        if (details.Length < 15)
            return new(false, "Describe what happened or what you need in a sentence or two.");

        if (title.Length > 150)
            return new(false, "Keep the title under 150 characters; put the rest in the details.");

        if (details.Length > 4000)
            return new(false, "The details are too long. Attach a document instead.");

        var missing = draft.Attachments.Where(a => !File.Exists(a)).ToList();
        if (missing.Count > 0)
            return new(false, $"This attached file is no longer there: {Path.GetFileName(missing[0])}");

        // A database copy is the whole payroll; only someone who may take
        // backups may hand one out.
        if (draft.IncludeDatabase && !performedBy.Can(Permission.ManageBackups))
            return new(false, "Only an administrator can include the database.");

        var connection = await _database.GetConnectionAsync().ConfigureAwait(false);

        var request = new SupportRequest
        {
            Reference = await NextReferenceAsync(connection).ConfigureAwait(false),
            Kind = draft.Kind,
            Priority = draft.Priority,
            Title = title,
            Details = details,
            Screen = draft.Screen,
            AppVersion = AppVersion.Display,
            CreatedUtc = DateTime.UtcNow,
            CreatedBy = string.IsNullOrWhiteSpace(performedBy.FullName) ? performedBy.Username : performedBy.FullName
        };

        string? databaseCopy = null;

        if (draft.IncludeDatabase)
        {
            // Taken as a normal, verified, audited backup and packed from there,
            // rather than copying the live file mid-write.
            var backup = await _data.CreateBackupAsync(performedBy).ConfigureAwait(false);
            if (!backup.Succeeded)
                return new(false, $"The database copy could not be made: {backup.Message}");

            databaseCopy = (await _data.GetBackupsAsync().ConfigureAwait(false))
                .Where(b => b.IsReadable)
                .OrderByDescending(b => b.TakenLocal)
                .Select(b => b.FilePath)
                .FirstOrDefault();
        }

        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"{request.Reference}.zip");

        try
        {
            await Task.Run(() => WritePackage(path, request, draft, databaseCopy, performedBy)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"[SupportRequestService] {ex}");
            return new(false, "The request file could not be written. Close any attached file that is open and try again.");
        }

        request.PackagePath = path;
        await connection.InsertAsync(request).ConfigureAwait(false);

        await _audit.WriteAsync(AuditActions.SupportRequestCreated, "SupportRequest", request.Id, true,
            $"{request.Reference} ({request.Kind}, {request.Priority}): {Truncate(title, 120)}" +
            (draft.IncludeDatabase ? " — database copy included." : string.Empty),
            performedBy.Username, performedBy.Id).ConfigureAwait(false);

        var size = new FileInfo(path).Length;
        var message = $"{request.Reference} saved. Send the file {request.Reference}.zip to the developer.";

        if (size > SendLimitBytes)
            message += $" It is {size / 1024d / 1024d:0} MB, too big for Messenger or e-mail — upload it to Google Drive instead.";

        return new(true, message, request);
    }

    private static async Task<string> NextReferenceAsync(SQLite.SQLiteAsyncConnection connection)
    {
        var stem = $"REQ-{DateTime.Now:yyMMdd-HHmm}";
        var reference = stem;

        // Two requests in the same minute get a letter.
        for (var suffix = 'B';
             await connection.Table<SupportRequest>().Where(r => r.Reference == reference).CountAsync().ConfigureAwait(false) > 0;
             suffix++)
        {
            reference = $"{stem}{suffix}";
        }

        return reference;
    }

    private static void WritePackage(
        string path, SupportRequest request, SupportRequestDraft draft, string? databaseCopy, User user)
    {
        if (File.Exists(path))
            File.Delete(path);

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        var summary = zip.CreateEntry("REQUEST.txt");
        using (var writer = new StreamWriter(summary.Open(), new UTF8Encoding(true)))
            writer.Write(Describe(request, draft, user));

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in draft.Attachments)
        {
            var name = Path.GetFileName(file);
            var unique = name;

            for (var n = 2; !used.Add(unique); n++)
                unique = $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}";

            zip.CreateEntryFromFile(file, $"attachments/{unique}", CompressionLevel.Optimal);
        }

        if (databaseCopy is not null)
            zip.CreateEntryFromFile(databaseCopy, $"database/{Path.GetFileName(databaseCopy)}", CompressionLevel.Optimal);
    }

    private static string Describe(SupportRequest request, SupportRequestDraft draft, User user)
    {
        var kind = request.Kind switch
        {
            SupportRequestKind.Problem => "Problem (something is wrong)",
            SupportRequestKind.Change => "Change request (new or different behaviour)",
            _ => "Question"
        };

        var text = new StringBuilder();
        text.AppendLine($"Reference : {request.Reference}");
        text.AppendLine($"Type      : {kind}");
        text.AppendLine($"Priority  : {request.Priority}");
        text.AppendLine($"Screen    : {request.Screen}");
        text.AppendLine($"Title     : {request.Title}");
        text.AppendLine();
        text.AppendLine("Details");
        text.AppendLine("-------");
        text.AppendLine(request.Details);
        text.AppendLine();
        text.AppendLine("Sent from");
        text.AppendLine("---------");
        text.AppendLine($"Written by  : {request.CreatedBy} ({user.RoleDisplayName})");
        text.AppendLine($"Written at  : {request.CreatedLocal:yyyy-MM-dd HH:mm}");
        text.AppendLine($"App version : {request.AppVersion}");
        text.AppendLine($"Windows     : {Environment.OSVersion.VersionString}");
        text.AppendLine($"Computer    : {Environment.MachineName}");
        text.AppendLine($"Attachments : {(draft.Attachments.Count == 0 ? "none" : string.Join(", ", draft.Attachments.Select(Path.GetFileName)))}");
        text.AppendLine($"Database    : {(draft.IncludeDatabase ? "included (contains payroll data - keep private)" : "not included")}");

        return text.ToString();
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
