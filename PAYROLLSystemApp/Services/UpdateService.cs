using System.Diagnostics;
using System.Net.Http.Headers;
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

public interface IUpdateService
{
    /// <summary>The newest release found by the last successful check, if newer than this copy.</summary>
    UpdateInfo? Available { get; }

    /// <summary>Raised when <see cref="Available"/> changes, from any thread.</summary>
    event EventHandler? AvailableChanged;

    Task<UpdateCheckResult> CheckAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Downloads the installer and checks it against the published SHA-256.
    /// Returns the verified file, or throws <see cref="UpdateException"/>.
    /// </summary>
    Task<string> DownloadAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken cancellation = default);

    /// <summary>Starts the verified installer in silent mode. The caller then closes the app.</summary>
    void StartInstaller(string installerPath);
}

public sealed class UpdateException(string message) : Exception(message);

/// <summary>
/// Updates come from the project's GitHub Releases: the developer publishes a
/// release carrying <c>PayrollSystemSetup-x.y.exe</c> and its <c>.sha256</c>, and
/// every installed copy sees it on the next sign-in.
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
public sealed class UpdateService : IUpdateService
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

    private static readonly HttpClient Http = CreateClient();

    public UpdateInfo? Available { get; private set; }

    public event EventHandler? AvailableChanged;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var response = await Http.GetAsync(LatestRelease, cancellation).ConfigureAwait(false);

            // No release published yet.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Found(null, "You have the latest version.");

            if (!response.IsSuccessStatusCode)
                return new(false, null, $"The update server answered {(int)response.StatusCode}. Try again later.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellation).ConfigureAwait(false);

            var update = Read(json.RootElement);

            if (update is null || update.Version <= AppVersion.Current)
                return Found(null, $"You have the latest version ({AppVersion.Display}).");

            return Found(update, $"Version {update.VersionDisplay} is available.");
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

    private UpdateCheckResult Found(UpdateInfo? update, string message)
    {
        if (Available?.Version != update?.Version)
        {
            Available = update;
            AvailableChanged?.Invoke(this, EventArgs.Empty);
        }

        return new(true, update, message);
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

    public async Task<string> DownloadAsync(
        UpdateInfo update, IProgress<double>? progress, CancellationToken cancellation = default)
    {
        if (update.ChecksumUrl is null || update.SignatureUrl is null)
            throw new UpdateException(
                "This release is not signed, so it cannot be verified and was not installed. Ask the developer to republish it.");

        var expected = await ReadSignedChecksumAsync(update.ChecksumUrl, update.SignatureUrl, cancellation)
            .ConfigureAwait(false);

        var folder = Path.Combine(Path.GetTempPath(), "PayrollMS-Update");
        Directory.CreateDirectory(folder);

        // Only the file name from the release is used, never a path.
        var target = Path.Combine(folder, Path.GetFileName(update.InstallerName));

        try
        {
            using var response = await Http.GetAsync(
                update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"The download failed ({(int)response.StatusCode}). Try again later.");

            var total = response.Content.Headers.ContentLength ?? update.InstallerSize;

            using var sha = SHA256.Create();

            await using (var source = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false))
            await using (var file = File.Create(target))
            {
                var buffer = new byte[81920];
                long read = 0;
                int count;

                while ((count = await source.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
                    sha.TransformBlock(buffer, 0, count, null, 0);

                    read += count;
                    if (total > 0)
                        progress?.Report((double)read / total);
                }

                sha.TransformFinalBlock([], 0, 0);
            }

            var actual = Convert.ToHexString(sha.Hash!);

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(target);
                throw new UpdateException(
                    "The downloaded file does not match the published checksum and was deleted. Nothing was installed.");
            }

            return target;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            TryDelete(target);
            Debug.WriteLine($"[UpdateService] {ex}");
            throw new UpdateException("The download was interrupted. Check the internet connection and try again.");
        }
        catch (OperationCanceledException)
        {
            TryDelete(target);
            throw;
        }
    }

    private static async Task<string> ReadSignedChecksumAsync(Uri checksumUrl, Uri signatureUrl, CancellationToken cancellation)
    {
        byte[] checksum;
        string signatureText;

        try
        {
            checksum = await Http.GetByteArrayAsync(checksumUrl, cancellation).ConfigureAwait(false);
            signatureText = await Http.GetStringAsync(signatureUrl, cancellation).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw new UpdateException("Could not reach the update server. Check the internet connection.");
        }

        if (!IsSignedByDeveloper(checksum, signatureText))
            throw new UpdateException(
                "This release's signature does not match the developer's key. It was not installed. Tell the developer straight away.");

        var text = System.Text.Encoding.UTF8.GetString(checksum);

        // "HASH  file.exe" (sha256sum style) or the bare hash.
        var hash = text.Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new UpdateException("The release's checksum file could not be read.");

        return hash;
    }

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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // GitHub's API refuses requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PayrollMS", AppVersion.Display));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }
}
