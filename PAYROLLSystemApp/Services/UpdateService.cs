using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace PAYROLLSystemApp.Services;

/// <summary>The version this copy of the app was built as, e.g. 1.1.</summary>
public static class AppVersion
{
    /// <summary>
    /// Read from the build's informational version ("1.1+&lt;commit&gt;") rather
    /// than <c>AppInfo.VersionString</c>, whose meaning differs between a packaged
    /// and an unpackaged Windows app.
    /// </summary>
    public static Version Current { get; } = Read();

    public static string Display => Format(Current);

    public static string Format(Version version) =>
        version.Build > 0 ? $"{version.Major}.{version.Minor}.{version.Build}" : $"{version.Major}.{version.Minor}";

    /// <summary>"v1.1", "1.1" and "1.1.0.0" all compare as the same version.</summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var clean = text.Trim().TrimStart('v', 'V');
        var plus = clean.IndexOfAny(['+', '-', ' ']);
        if (plus >= 0)
            clean = clean[..plus];

        // Version.TryParse needs at least major.minor.
        if (!clean.Contains('.'))
            clean += ".0";

        return Version.TryParse(clean, out var parsed) ? Normalise(parsed) : null;
    }

    private static Version Normalise(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static Version Read()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (Parse(informational) is { } fromBuild)
            return fromBuild;

        return Normalise(assembly.GetName().Version ?? new Version(1, 0));
    }
}

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Title,
    string Notes,
    DateTime PublishedLocal,
    string InstallerName,
    Uri InstallerUrl,
    long InstallerSize,
    Uri? ChecksumUrl,
    Uri? SignatureUrl)
{
    public string VersionDisplay => AppVersion.Format(Version);

    public string PublishedDisplay => PublishedLocal.ToString("MMM d, yyyy");

    public string SizeDisplay => $"{InstallerSize / 1024d / 1024d:0.#} MB";
}

public sealed record UpdateCheckResult(bool Succeeded, UpdateInfo? Update, string Message)
{
    public bool IsAvailable => Update is not null;
}

/// <summary>Where the newest release is on its way onto this PC.</summary>
public enum UpdateStage
{
    /// <summary>Nothing newer is published, or no check has succeeded yet.</summary>
    UpToDate,

    /// <summary>A newer release was found and is waiting for someone to download it.</summary>
    Available,

    /// <summary>The installer is being downloaded.</summary>
    Downloading,

    /// <summary>The installer is downloaded, verified and waiting for Install.</summary>
    Ready
}

public interface IUpdateService
{
    /// <summary>The newest release found by the last successful check, if newer than this copy.</summary>
    UpdateInfo? Available { get; }

    UpdateStage Stage { get; }

    /// <summary>0 to 1 while <see cref="Stage"/> is <see cref="UpdateStage.Downloading"/>.</summary>
    double DownloadProgress { get; }

    /// <summary>Why the last download failed; cleared when a new one starts.</summary>
    string? DownloadError { get; }

    /// <summary>The last check's outcome, including "no internet".</summary>
    UpdateCheckResult? LastCheck { get; }

    DateTime? LastCheckedAt { get; }

    /// <summary>
    /// Raised, from any thread, whenever anything above changes: a release is
    /// found, a download moves on, finishes or fails, or a check completes.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Starts checking GitHub in the background: now, every half hour while
    /// online, every few minutes while offline, and shortly after the PC
    /// reconnects. Safe to call more than once.
    /// </summary>
    void StartMonitoring();

    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Downloads <see cref="Available"/> and checks it against the signed
    /// SHA-256. Never throws: success moves to <see cref="UpdateStage.Ready"/>,
    /// failure sets <see cref="DownloadError"/> and returns to
    /// <see cref="UpdateStage.Available"/>. Calling it while a download is
    /// running joins that download.
    /// </summary>
    Task<bool> DownloadAsync();

    void CancelDownload();

    /// <summary>
    /// The downloaded installer, hashed again because it may have sat in the
    /// temp folder for hours. Throws <see cref="UpdateException"/> (and goes
    /// back to <see cref="UpdateStage.Available"/>) if it changed or vanished.
    /// </summary>
    Task<string> GetVerifiedInstallerAsync();

    /// <summary>Starts the verified installer in silent mode. The caller then closes the app.</summary>
    void StartInstaller(string installerPath);
}

public sealed class UpdateException(string message, bool isConnectionProblem = false) : Exception(message)
{
    /// <summary>The internet dropped, as opposed to a bad or unsigned release.</summary>
    public bool IsConnectionProblem { get; } = isConnectionProblem;
}

/// <summary>
/// Updates come from the project's GitHub Releases: the developer publishes a
/// release carrying <c>PayrollSystemSetup-x.y.exe</c> and its <c>.sha256</c>, and
/// every installed copy that is online notices it within half an hour (sooner
/// after reconnecting). Nothing is downloaded until someone chooses Download,
/// and nothing is installed until an administrator chooses Install.
///
/// <para><b>Why a checksum alone is not enough.</b> The checksum sits in the
/// same release as the installer, so anyone able to publish a release could
/// publish a matching pair. The checksum file is therefore signed with a private
/// key that never leaves the developer's PC (<c>tools/release-signing.cs</c>),
/// and only a signature that verifies against <see cref="ReleasePublicKey"/>
/// lets an installer run. Control of the GitHub repository alone cannot push
/// code onto the company's PC.</para>
///
/// <para><b>Why the app downloads the installer itself.</b> A browser, Messenger
/// or Outlook marks what it saves as "from the internet", and that mark is what
/// makes Windows SmartScreen stop an unsigned installer with "Windows protected
/// your PC". A file the app writes with <see cref="HttpClient"/> carries no such
/// mark, so after the first install, updates run without that prompt. The
/// SHA-256 check is what stands in for the browser's caution: a file that does
/// not match what was published is deleted, never run.</para>
/// </summary>
public sealed partial class UpdateService : IUpdateService
{
    /// <summary>The public repository releases are published to.</summary>
    public const string Repository = "kennethtablang/PAYROLLSystemApp";

    /// <summary>
    /// Verifies release checksums. The matching private key is at
    /// %APPDATA%\PayrollMS-Release\signing-key.pem on the developer's PC.
    /// Replacing it means installing the next version by hand once.
    /// </summary>
    private const string ReleasePublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExZGhMkwyJm/ul0ncCYVe71iQR2r3
        /T+8Axle5kL4iTPCco86H05axIIy1Ci1w7a2LpJOAqP3t7XlPve8akJzAg==
        -----END PUBLIC KEY-----
        """;

    private static readonly Uri LatestRelease = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>
    /// No byte for this long means the connection died without saying so. The
    /// HttpClient timeout stops applying once the headers arrive, so without
    /// this a silently dropped line would leave the download stuck forever.
    /// </summary>
    private readonly TimeSpan _stallTimeout;

    private readonly HttpClient Http;

    public UpdateService()
        : this(new HttpClientHandler(), TimeSpan.FromSeconds(60))
    {
    }

    /// <summary>For tests: a fake network and a shorter stall limit.</summary>
    internal UpdateService(HttpMessageHandler handler, TimeSpan stallTimeout)
    {
        Http = CreateClient(handler);
        _stallTimeout = stallTimeout;
    }

    /// <summary>How often a connected PC looks for a new release.</summary>
    private static readonly TimeSpan OnlineInterval = TimeSpan.FromMinutes(30);

    /// <summary>How often a PC whose last check failed (usually: no internet) tries again.</summary>
    private static readonly TimeSpan OfflineInterval = TimeSpan.FromMinutes(5);

    /// <summary>Windows reports the network before DNS answers; give it a moment.</summary>
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);

    private bool _monitoring;
    private Task<bool>? _download;
    private CancellationTokenSource? _downloadCancel;
    private int _reportedPercent = -1;

    /// <summary>A download someone started was cut off by the connection; finish it once back online.</summary>
    private bool _resumeWhenOnline;

    private string? _readyPath;
    private string? _readyHash;

    public UpdateInfo? Available { get; private set; }

    public UpdateStage Stage { get; private set; }

    public double DownloadProgress { get; private set; }

    public string? DownloadError { get; private set; }

    public UpdateCheckResult? LastCheck { get; private set; }

    public DateTime? LastCheckedAt { get; private set; }

    public event EventHandler? Changed;

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // ============================================================ background checks

    public void StartMonitoring()
    {
        lock (_lock)
        {
            if (_monitoring)
            {
                // Already running (a second sign-in): check now, as a sign-in always has.
                Wake();
                return;
            }

            _monitoring = true;
        }

        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        _ = Task.Run(MonitorAsync);
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken.
        }
    }

    /// <summary>Runs for the life of the app; the process ending is what stops it.</summary>
    private async Task MonitorAsync()
    {
        while (true)
        {
            var succeeded = false;

            try
            {
                succeeded = (await CheckAsync().ConfigureAwait(false)).Succeeded;

                // Someone already chose to download this and the connection
                // dropped half-way. Pick it up again without asking twice.
                bool resume;
                lock (_lock)
                    resume = succeeded && _resumeWhenOnline && Stage == UpdateStage.Available;

                if (resume)
                    _ = DownloadAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UpdateService] background check: {ex}");
            }

            // Sleep until the interval passes or the PC reconnects, whichever is first.
            await _wake.WaitAsync(succeeded ? OnlineInterval : OfflineInterval).ConfigureAwait(false);
        }
    }

    private async void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable)
            return;

        await Task.Delay(ReconnectDelay).ConfigureAwait(false);
        Wake();
    }

    // ============================================================ checking

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellation = default)
    {
        // The background loop and "Check now" share one request at a time.
        await _checkGate.WaitAsync(cancellation).ConfigureAwait(false);

        try
        {
            var result = await QueryAsync(cancellation).ConfigureAwait(false);
            Record(result);
            return result;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    private async Task<UpdateCheckResult> QueryAsync(CancellationToken cancellation)
    {
        try
        {
            using var response = await Http.GetAsync(LatestRelease, cancellation).ConfigureAwait(false);

            // No release published yet.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new(true, null, "You have the latest version.");

            if (!response.IsSuccessStatusCode)
                return new(false, null, $"The update server answered {(int)response.StatusCode}. Try again later.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);

            var update = Read(json.RootElement);

            if (update is null || update.Version <= AppVersion.Current)
                return new(true, null, $"You have the latest version ({AppVersion.Display}).");

            return new(true, update, $"Version {update.VersionDisplay} is available.");
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return new(false, null, "The update server did not answer in time. Check the internet connection.");
        }
        catch (HttpRequestException)
        {
            return new(false, null, "Could not reach the update server. Check the internet connection.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Debug.WriteLine($"[UpdateService] {ex}");
            return new(false, null, "The update information could not be read.");
        }
    }

    private void Record(UpdateCheckResult result)
    {
        lock (_lock)
        {
            LastCheck = result;
            LastCheckedAt = DateTime.Now;

            // A failed check (offline) changes nothing, and a running download
            // keeps the release it started with.
            if (result.Succeeded && Stage != UpdateStage.Downloading && result.Update?.Version != Available?.Version)
            {
                // A newer release than the one downloaded, or the release was
                // withdrawn: whatever was downloaded is no longer what to install.
                Available = result.Update;
                Stage = Available is null ? UpdateStage.UpToDate : UpdateStage.Available;
                DownloadError = null;
                DownloadProgress = 0;
                _resumeWhenOnline = false;
                _readyPath = null;
                _readyHash = null;
            }
        }

        RaiseChanged();
    }

    private static UpdateInfo? Read(JsonElement release)
    {
        var version = AppVersion.Parse(release.GetProperty("tag_name").GetString());
        if (version is null)
            return null;

        JsonElement? installer = null;
        Uri? checksum = null;
        Uri? signature = null;

        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? string.Empty;

            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                installer = asset;
            else if (name.EndsWith(".exe.sha256", StringComparison.OrdinalIgnoreCase))
                checksum = GitHubUrl(asset);
            else if (name.EndsWith(".exe.sha256.sig", StringComparison.OrdinalIgnoreCase))
                signature = GitHubUrl(asset);
        }

        // A release without an installer (notes only) is not an update.
        if (installer is not { } exe)
            return null;

        var published = release.TryGetProperty("published_at", out var at) && at.TryGetDateTime(out var utc)
            ? utc.ToLocalTime()
            : DateTime.Now;

        return new UpdateInfo(
            version,
            release.GetProperty("name").GetString() is { Length: > 0 } title ? title : $"Version {AppVersion.Format(version)}",
            release.GetProperty("body").GetString() ?? string.Empty,
            published,
            exe.GetProperty("name").GetString()!,
            GitHubUrl(exe) ?? throw new InvalidOperationException("The installer link is not on github.com."),
            exe.GetProperty("size").GetInt64(),
            checksum,
            signature);
    }

    /// <summary>An asset's download link, accepted only over https from github.com.</summary>
    private static Uri? GitHubUrl(JsonElement asset)
    {
        var text = asset.GetProperty("browser_download_url").GetString();

        return Uri.TryCreate(text, UriKind.Absolute, out var url) &&
               url.Scheme == Uri.UriSchemeHttps &&
               string.Equals(url.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? url
            : null;
    }

    // ============================================================ downloading

    public Task<bool> DownloadAsync()
    {
        Task<bool> download;

        lock (_lock)
        {
            if (_download is { IsCompleted: false } running)
                return running;

            if (Stage == UpdateStage.Ready)
                return Task.FromResult(true);

            if (Available is not { } update)
                return Task.FromResult(false);

            var cancel = new CancellationTokenSource();
            _downloadCancel = cancel;
            Stage = UpdateStage.Downloading;
            DownloadProgress = 0;
            DownloadError = null;
            _resumeWhenOnline = false;
            _reportedPercent = -1;

            download = _download = Task.Run(() => RunDownloadAsync(update, cancel));
        }

        RaiseChanged();
        return download;
    }

    public void CancelDownload()
    {
        lock (_lock)
            _downloadCancel?.Cancel();
    }

    private async Task<bool> RunDownloadAsync(UpdateInfo update, CancellationTokenSource cancel)
    {
        try
        {
            var (path, hash) = await FetchInstallerAsync(update, cancel.Token).ConfigureAwait(false);

            lock (_lock)
            {
                _readyPath = path;
                _readyHash = hash;
                DownloadProgress = 1;
                Stage = UpdateStage.Ready;
            }

            RaiseChanged();
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return Failed(null, resume: false);
        }
        catch (UpdateException ex)
        {
            return Failed(ex.Message, ex.IsConnectionProblem);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UpdateService] {ex}");
            return Failed("The update could not be downloaded. Try again later.", resume: false);
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_downloadCancel, cancel))
                    _downloadCancel = null;
            }

            cancel.Dispose();
        }
    }

    private bool Failed(string? message, bool resume)
    {
        lock (_lock)
        {
            Stage = Available is null ? UpdateStage.UpToDate : UpdateStage.Available;
            DownloadProgress = 0;
            DownloadError = message;
            _resumeWhenOnline = resume;
        }

        RaiseChanged();
        return false;
    }

    /// <summary>Raises <see cref="Changed"/> once per whole percent, not once per 80 KB.</summary>
    private void ReportProgress(double fraction)
    {
        var percent = (int)(fraction * 100);

        lock (_lock)
        {
            if (percent == _reportedPercent)
                return;

            _reportedPercent = percent;
            DownloadProgress = fraction;
        }

        RaiseChanged();
    }

    /// <summary>The verified installer and its hash, or throws <see cref="UpdateException"/>.</summary>
    private async Task<(string Path, string Hash)> FetchInstallerAsync(UpdateInfo update, CancellationToken cancellation)
    {
        if (update.ChecksumUrl is null || update.SignatureUrl is null)
            throw new UpdateException(
                "This release is not signed, so it cannot be verified and was not installed. Ask the developer to republish it.");

        var expected = await ReadSignedChecksumAsync(update, cancellation).ConfigureAwait(false);

        var folder = Path.Combine(Path.GetTempPath(), "PayrollMS-Update");
        Directory.CreateDirectory(folder);

        // Only the file name from the release is used, never a path.
        var target = Path.Combine(folder, Path.GetFileName(update.InstallerName));

        // Downloaded in an earlier session and never installed: no need to fetch it again.
        if (File.Exists(target) &&
            string.Equals(await HashFileAsync(target, cancellation).ConfigureAwait(false), expected, StringComparison.OrdinalIgnoreCase))
            return (target, expected);

        // What has arrived so far. Kept when the connection drops, so a slow
        // line picks up where it stopped instead of starting the 60 MB again.
        var partial = target + ".partial";

        try
        {
            await DownloadToPartialAsync(update, partial, cancellation).ConfigureAwait(false);

            var actual = await HashFileAsync(partial, cancellation).ConfigureAwait(false);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new UpdateException(
                    "The downloaded file does not match the published checksum and was deleted. Nothing was installed.");
            }

            File.Move(partial, target, overwrite: true);
            return (target, expected);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException ||
                                   (ex is OperationCanceledException && !cancellation.IsCancellationRequested))
        {
            // Includes the HttpClient timeout and the stall limit, which both
            // surface as a cancellation nobody asked for. The partial file stays.
            Debug.WriteLine($"[UpdateService] {ex}");
            throw new UpdateException(
                "The download was interrupted. It will carry on from where it stopped once the internet connection is back.",
                isConnectionProblem: true);
        }
        catch (OperationCanceledException)
        {
            // Cancel means "not now": start clean next time.
            TryDelete(partial);
            throw;
        }
    }

    /// <summary>Fills <paramref name="partial"/> with the whole installer, resuming if part of it is there.</summary>
    private async Task DownloadToPartialAsync(UpdateInfo update, string partial, CancellationToken cancellation)
    {
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, update.InstallerUrl);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);

        using var response = await Http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);

        // Asked for the rest of a file that is already complete (or the release
        // was replaced by a smaller one): start over.
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            File.Delete(partial);
            await DownloadToPartialAsync(update, partial, cancellation).ConfigureAwait(false);
            return;
        }

        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"The download failed ({(int)response.StatusCode}). Try again later.");

        // 206: the server is sending the rest. 200: it ignored the range and
        // is sending everything, so what was kept is thrown away.
        var resuming = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resuming)
            have = 0;

        var total = have + (response.Content.Headers.ContentLength ?? Math.Max(0, update.InstallerSize - have));

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellation);

        await using var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        await using var file = new FileStream(partial, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write);

        var buffer = new byte[81920];
        var read = have;
        int count;

        if (total > 0)
            ReportProgress(Math.Min(1, (double)read / total));

        while (true)
        {
            stall.CancelAfter(_stallTimeout);
            count = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            if (count == 0)
                break;

            await file.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);

            read += count;
            if (total > 0)
                ReportProgress(Math.Min(1, (double)read / total));
        }

        // A connection that closes early ends the stream without an error.
        if (total > 0 && read < total)
            throw new IOException($"The connection closed after {read} of {total} bytes.");
    }

    public async Task<string> GetVerifiedInstallerAsync()
    {
        string? path;
        string? hash;

        lock (_lock)
        {
            path = Stage == UpdateStage.Ready ? _readyPath : null;
            hash = _readyHash;
        }

        if (path is null || hash is null)
            throw new UpdateException("The update has not been downloaded yet.");

        string? actual = null;

        try
        {
            if (File.Exists(path))
                actual = await HashFileAsync(path, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }

        if (string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase))
            return path;

        lock (_lock)
        {
            _readyPath = null;
            _readyHash = null;

            if (Stage == UpdateStage.Ready)
                Stage = UpdateStage.Available;
        }

        RaiseChanged();
        throw new UpdateException(
            "The downloaded installer was changed or deleted after it was downloaded, so it was not run. Download it again.");
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellation)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation).ConfigureAwait(false));
    }

    /// <summary>
    /// The installer hash from the release's signed checksum file.
    ///
    /// <para>The signed file also names the installer, and that name carries the
    /// version it was built as. It must match the release's tag and be newer than
    /// this copy. Otherwise an old installer that was genuinely signed could be
    /// re-uploaded under a higher tag and "update" the company back to it.</para>
    /// </summary>
    private async Task<string> ReadSignedChecksumAsync(UpdateInfo update, CancellationToken cancellation)
    {
        byte[] checksum;
        string signatureText;

        try
        {
            checksum = await Http.GetByteArrayAsync(update.ChecksumUrl!, cancellation).ConfigureAwait(false);
            signatureText = await Http.GetStringAsync(update.SignatureUrl!, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException ||
                                   (ex is OperationCanceledException && !cancellation.IsCancellationRequested))
        {
            throw new UpdateException(
                "Could not reach the update server. It will try again once the internet connection is back.",
                isConnectionProblem: true);
        }

        if (!IsSignedByDeveloper(checksum, signatureText))
            throw new UpdateException(
                "This release's signature does not match the developer's key. It was not installed. Tell the developer straight away.");

        return CheckSignedContents(System.Text.Encoding.UTF8.GetString(checksum), update);
    }

    /// <summary>"HASH  PayrollSystemSetup-x.y.exe", as build-installer.ps1 writes it.</summary>
    private static string CheckSignedContents(string text, UpdateInfo update)
    {
        var parts = text.Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2 || parts[0].Length != 64 || !parts[0].All(Uri.IsHexDigit))
            throw new UpdateException("The release's checksum file could not be read.");

        var signedName = parts[1];
        var match = SignedInstallerName().Match(signedName);
        var signedVersion = match.Success ? AppVersion.Parse(match.Groups[1].Value) : null;

        if (signedVersion is null ||
            signedVersion != update.Version ||
            signedVersion <= AppVersion.Current ||
            !string.Equals(signedName, update.InstallerName, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateException(
                $"The signed installer ({signedName}) does not match release {update.VersionDisplay}. " +
                "It was not installed. Tell the developer straight away.");
        }

        return parts[0];
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^PayrollSystemSetup-(\d+(?:\.\d+){1,3})\.exe$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SignedInstallerName();

    private static bool IsSignedByDeveloper(byte[] data, string signatureBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(ReleasePublicKey);
            return key.VerifyData(data, Convert.FromBase64String(signatureBase64.Trim()), HashAlgorithmName.SHA256);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public void StartInstaller(string installerPath)
    {
        // /SILENT shows only a progress bar; the installer closes this app if it
        // is still running and reopens it when done (see installer/PayrollSystem.iss).
        // Shell execute so Windows can ask for an administrator when the app
        // was installed for all users.
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        // Covers reaching the server and getting headers; the body is guarded
        // by the stall limit instead, because a slow line may need hours.
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };

        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PayrollMS", AppVersion.Display));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }
}
