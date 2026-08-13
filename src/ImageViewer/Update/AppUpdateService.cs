using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

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
/// Downloads are restricted to GitHub hosts for the configured repository, so a tampered or
/// unexpected API response cannot redirect the updater to an arbitrary server.
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

            var (url, name, size) = FindInstallerAsset(root);

            var page = root.TryGetProperty("html_url", out var html)
                ? html.GetString() ?? ReleasesPageUrl
                : ReleasesPageUrl;

            var notes = root.TryGetProperty("body", out var body) ? body.GetString() : null;

            return new UpdateInfo(version, tag!, url, name, size, page, Trim(notes));
        }
        catch
        {
            // No network, rate limited, repository not published yet - all non-events for a viewer.
            return null;
        }
    }

    /// <summary>Picks the setup executable from a release's assets.</summary>
    private static (string? Url, string? Name, long Size) FindInstallerAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind != JsonValueKind.Array)
        {
            return (null, null, 0);
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (name is null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is null || !IsAllowedDownload(url)) continue;

            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
            return (url, name, size);
        }

        return (null, null, 0);
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

    /// <summary>
    /// Downloads the installer to a temporary folder.
    /// </summary>
    /// <returns>Path to the downloaded file.</returns>
    public async Task<string> DownloadInstallerAsync(
        UpdateInfo update, IProgress<double>? progress, CancellationToken ct)
    {
        if (update.InstallerUrl is null || !IsAllowedDownload(update.InstallerUrl))
            throw new InvalidOperationException("This release has no installer that can be downloaded safely.");

        var folder = Path.Combine(Path.GetTempPath(), "ImageViewerUpdate");
        Directory.CreateDirectory(folder);

        var target = Path.Combine(folder, update.InstallerName ?? "ImageViewer-setup.exe");

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

        if (File.Exists(target)) File.Delete(target);
        File.Move(partial, target);

        return target;
    }

    /// <summary>
    /// Launches the downloaded installer and asks the application to exit.
    /// </summary>
    /// <remarks>
    /// The installer cannot replace files the running viewer holds open, so the caller must shut
    /// down immediately after this returns.
    /// </remarks>
    public static void LaunchInstaller(string installerPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = true,
        });
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
