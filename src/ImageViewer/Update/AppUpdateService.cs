using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace ImageViewer.Update;

/// <summary>
/// Checks the project's GitHub Releases for a newer build and, on request, installs it.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately quiet. The check runs in the background well after the window is up, never blocks
/// anything, fails silently if the network is unavailable, and is throttled so a viewer opened
/// fifty times a day does not make fifty API calls. Nothing is downloaded and nothing is executed
/// without the user explicitly asking for it.
/// </para>
/// <para>
/// Downloads are restricted to GitHub hosts for the configured repository and must carry a valid
/// SHA-256 asset digest. The bytes are verified before the installer receives its final name.
/// </para>
/// </remarks>
public sealed class AppUpdateService
{
    // Changing these moves where updates come from; they are the only place the repo is named.
    public const string RepositoryOwner = "crmaris";
    public const string RepositoryName = "image-viewer";

    public static string ReleasesPageUrl => $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases";

    private static readonly Uri ApiUri =
        new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");

    /// <summary>Hosts a release asset may legitimately be served from.</summary>
    private static readonly string[] AllowedDownloadHosts =
        ["github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>At most one check per day, however often the viewer is launched.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// Created on first use, not at class initialisation.
    /// </summary>
    /// <remarks>
    /// Lazy for two reasons. It must not be built before <see cref="CurrentVersion"/> - static
    /// fields initialise in declaration order, and an eager <c>= CreateClient()</c> here read a
    /// null version and threw a TypeInitializationException. It also means a session that never
    /// checks for updates never allocates an HttpClient at all.
    /// </remarks>
    private static readonly Lazy<HttpClient> HttpLazy = new(CreateClient);

    private static HttpClient Http => HttpLazy.Value;

    /// <summary>The running build's version.</summary>
    public static Version CurrentVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

    private static string StateFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ImageViewer", "update-check.txt");

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // Generous enough for a slow link, short enough that a hung request cannot linger.
            Timeout = TimeSpan.FromSeconds(30),
        };

        // GitHub rejects API requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ImageViewer", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }

    /// <summary>True if enough time has passed since the last check.</summary>
    public static bool ShouldCheckNow()
    {
        try
        {
            if (!File.Exists(StateFile)) return true;

            var text = File.ReadAllText(StateFile).Trim();
            return !DateTimeOffset.TryParse(text, out var last) ||
                   DateTimeOffset.UtcNow - last >= CheckInterval;
        }
        catch
        {
            return true;   // an unreadable timestamp should not disable updates
        }
    }

    private static void RecordCheck()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // Failing to record simply means checking again next launch.
        }
    }

    /// <summary>
    /// Asks GitHub for the latest release.
    /// </summary>
    /// <returns>The release if it is newer than the running build, otherwise null.</returns>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(ApiUri, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            RecordCheck();

            var root = document.RootElement;

            // Drafts are invisible to unauthenticated callers; prereleases are skipped on purpose.
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) return null;

            var version = ParseVersion(tag);
            if (version is null || version <= CurrentVersion) return null;

            var (url, name, size, digest) = FindInstallerAsset(root);

            var page = root.TryGetProperty("html_url", out var html)
                ? html.GetString() ?? ReleasesPageUrl
                : ReleasesPageUrl;

            var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;

            return new UpdateInfo(version, tag!, url, name, size, digest, page, Trim(notes));
        }
        catch
        {
            // No network, rate limited, repository not published yet - all non-events for a viewer.
            return null;
        }
    }

    /// <summary>Picks the setup executable from a release's assets.</summary>
    private static (string? Url, string? Name, long Size, string? Digest) FindInstallerAsset(
        JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null, 0, null);
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!IsSafeInstallerName(name) ||
                !name!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is null || !IsAllowedDownload(url)) continue;

            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            if (!IsAllowedDigest(digest)) continue;

            return (url, name, size, digest);
        }

        return (null, null, 0, null);
    }

    /// <summary>
    /// Rejects any download URL that is not an HTTPS GitHub address.
    /// </summary>
    /// <remarks>
    /// The updater fetches and then runs an executable, so the destination must never be taken on
    /// trust from the API response alone. Public so the self-test can prove the rejections hold.
    /// </remarks>
    public static bool IsAllowedDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedDownloadHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Rejects path components and invalid characters in a release asset name.</summary>
    public static bool IsSafeInstallerName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name == Path.GetFileName(name) &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>True only for a complete SHA-256 digest in GitHub's asset-digest format.</summary>
    public static bool IsAllowedDigest(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var hex = digest.AsSpan(prefix.Length);
        if (hex.Length != 64) return false;

        foreach (var character in hex)
        {
            if (!Uri.IsHexDigit(character)) return false;
        }

        return true;
    }

    /// <summary>Hashes a file and compares it with a validated GitHub SHA-256 digest.</summary>
    internal static bool HasExpectedDigest(string path, string? expectedDigest)
    {
        using var stream = File.OpenRead(path);
        return HasExpectedDigest(stream, expectedDigest);
    }

    private static bool HasExpectedDigest(Stream stream, string? expectedDigest)
    {
        if (!IsAllowedDigest(expectedDigest)) return false;

        var expected = Convert.FromHexString(expectedDigest!["sha256:".Length..]);
        var actual = SHA256.HashData(stream);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Downloads the installer to a temporary folder.
    /// </summary>
    /// <returns>Path to the downloaded file.</returns>
    public async Task<string> DownloadInstallerAsync(
        UpdateInfo update, IProgress<double>? progress, CancellationToken ct)
    {
        if (update.InstallerUrl is null ||
            !IsAllowedDownload(update.InstallerUrl) ||
            !IsSafeInstallerName(update.InstallerName) ||
            !IsAllowedDigest(update.InstallerDigest))
            throw new InvalidOperationException("This release has no installer that can be downloaded safely.");

        var folder = Path.Combine(Path.GetTempPath(), "ImageViewerUpdate");
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, update.InstallerName!);

        // A partial file left by an interrupted attempt must never be executed.
        var partial = target + ".part";
        if (File.Exists(partial)) File.Delete(partial);

        using (var response = await Http
                   .GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? update.InstallerSizeBytes;

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var destination = new FileStream(
                partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

            var buffer = new byte[1 << 16];
            long written = 0;
            int read;

            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;

                if (total > 0) progress?.Report(Math.Clamp(written / (double)total, 0, 1));
            }

            await destination.FlushAsync(ct).ConfigureAwait(false);
        }

        // Size check: a truncated download would otherwise be handed to the shell as an executable.
        var actual = new FileInfo(partial).Length;
        if (update.InstallerSizeBytes > 0 && actual != update.InstallerSizeBytes)
        {
            File.Delete(partial);
            throw new IOException(
                $"The download is incomplete ({actual:N0} of {update.InstallerSizeBytes:N0} bytes).");
        }

        if (!HasExpectedDigest(partial, update.InstallerDigest))
        {
            File.Delete(partial);
            throw new InvalidDataException(
                "The downloaded installer's SHA-256 digest does not match the published release asset.");
        }

        if (File.Exists(target)) File.Delete(target);
        File.Move(partial, target);

        return target;
    }

    /// <summary>
    /// How the running copy was installed, which the update has to match.
    /// </summary>
    public enum InstallMode
    {
        /// <summary>No installer record found - a portable copy. Let Setup ask.</summary>
        Unknown,

        /// <summary>Installed for this user only, under the user's profile.</summary>
        CurrentUser,

        /// <summary>Installed for all users, normally under Program Files.</summary>
        AllUsers,
    }

    /// <summary>Inno Setup's uninstall key for this application's AppId.</summary>
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7C3F1A62-9E4D-4B8A-9F21-2D6B5E0C4A17}_is1";

    /// <summary>
    /// Works out whether the running copy was installed for all users or just this one.
    /// </summary>
    /// <remarks>
    /// Read from the registry rather than guessed from the executable's path. The path is only a
    /// hint - this application was, at one point, installed into a folder that did not match its
    /// registration at all - whereas the uninstall key records what Setup actually did, and which
    /// hive it is in is precisely the answer needed.
    /// </remarks>
    public static InstallMode DetectInstallMode()
    {
        try
        {
            using (var machine = Registry.LocalMachine.OpenSubKey(UninstallKey))
            {
                if (machine is not null) return InstallMode.AllUsers;
            }

            using (var user = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                if (user is not null) return InstallMode.CurrentUser;
            }
        }
        catch
        {
            // An unreadable hive is no reason to refuse the update; Setup can ask instead.
        }

        return InstallMode.Unknown;
    }

    /// <summary>
    /// Builds the command line handed to the downloaded installer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Getting this wrong does not fail loudly - it installs a <em>second</em> copy alongside the
    /// first. Setup is marked <c>PrivilegesRequired=lowest</c>, so left to itself it offers a
    /// choice, and an all-users installation updated by a per-user run lands in
    /// <c>%LOCALAPPDATA%</c> while the original stays in Program Files, still registered, still
    /// launched by every file association. Pinning the mode to the one already in use is what
    /// prevents that.
    /// </para>
    /// <para>
    /// No <c>/DIR</c> is passed. Setup recognises its own AppId and reuses the recorded install
    /// path automatically, so supplying the directory adds nothing but a quoting hazard - a path
    /// containing a space that reaches Setup unquoted installs silently into the wrong folder and
    /// still reports success, which has happened here once already.
    /// </para>
    /// <para>
    /// Returned as a list rather than a joined string so the runtime does the quoting.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> BuildInstallerArguments(InstallMode mode) => mode switch
    {
        InstallMode.AllUsers => ["/ALLUSERS"],
        InstallMode.CurrentUser => ["/CURRENTUSER"],
        _ => [],
    };

    /// <summary>
    /// Launches the downloaded installer and asks the application to exit.
    /// </summary>
    /// <remarks>
    /// The installer cannot replace files the running viewer holds open, so the caller must shut
    /// down immediately after this returns. Failure to start is raised rather than swallowed: the
    /// caller has already told the user the update is being applied, so silently doing nothing
    /// would be the worst possible outcome. Declining the elevation prompt lands here too, which is
    /// why the message distinguishes it.
    /// </remarks>
    /// <returns>The started process, or null if the shell handed the file to a running instance.</returns>
    public static Process? LaunchInstaller(string installerPath, string expectedDigest) =>
        LaunchInstaller(installerPath, expectedDigest, DetectInstallMode());

    /// <inheritdoc cref="LaunchInstaller(string, string)"/>
    public static Process? LaunchInstaller(
        string installerPath, string expectedDigest, InstallMode mode)
    {
        if (!File.Exists(installerPath))
            throw new FileNotFoundException("The downloaded installer is no longer there.", installerPath);

        // Keep the verified file read-locked until Process.Start has opened it. Without this lock,
        // another process could replace the bytes between the digest check and execution.
        using var installerLock = new FileStream(
            installerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!HasExpectedDigest(installerLock, expectedDigest))
            throw new InvalidDataException(
                "The installer changed after download and will not be executed.");

        var start = new ProcessStartInfo
        {
            FileName = installerPath,
            // Required for the installer's own elevation prompt to appear at all.
            UseShellExecute = true,
        };

        foreach (var argument in BuildInstallerArguments(mode))
            start.ArgumentList.Add(argument);

        try
        {
            return Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user dismissed the UAC prompt. Not a fault, but the caller must
            // know the installer is not running so it does not close the window underneath them.
            throw new OperationCanceledException(
                "The update was cancelled at the Windows permission prompt.", ex);
        }
    }

    /// <summary>Opens the release page in the default browser.</summary>
    public static void OpenReleasePage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;

        Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
    }

    /// <summary>
    /// Reads a version out of a release tag, tolerating a leading "v".
    /// </summary>
    public static Version? ParseVersion(string tag)
    {
        var cleaned = tag.TrimStart('v', 'V').Trim();

        // Strip any pre-release suffix such as "1.2.0-beta".
        var dash = cleaned.IndexOf('-');
        if (dash > 0) cleaned = cleaned[..dash];

        if (!Version.TryParse(cleaned, out var parsed)) return null;

        // Normalise so "0.2" and "0.2.0.0" compare equal rather than as different versions.
        return new Version(
            parsed.Major,
            parsed.Minor,
            Math.Max(parsed.Build, 0),
            Math.Max(parsed.Revision, 0));
    }

    private static string? Trim(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;

        var text = notes.Trim();
        return text.Length <= 300 ? text : text[..300].TrimEnd() + "...";
    }
}
