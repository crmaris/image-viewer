using System.Diagnostics;
using System.IO;
using ImageViewer.Update;

namespace ImageViewer.SelfTest;

/// <summary>
/// Exercises the updater against the real GitHub release, end to end.
/// </summary>
/// <remarks>
/// <para>
/// The offline checks cover tag parsing and URL validation, but they cannot prove the updater
/// actually finds a real release, picks the right asset out of it, and downloads a file that
/// arrives intact. This does, by talking to the live API.
/// </para>
/// <para>
/// Run it against a deliberately old build so there is something to find:
/// <c>dotnet run --project tests/ImageViewer.SelfTest -p:Version=0.0.1 -- --check-update</c>
/// Add <c>--download</c> to fetch the installer as well, which is the only way to exercise the
/// size verification and the partial-file handling.
/// </para>
/// <para>
/// Not part of the normal suite: it needs the network, and a test that fails when the wifi drops is
/// worse than no test.
/// </para>
/// </remarks>
public static class LiveUpdateCheck
{
    public static async Task<int> RunAsync(bool download)
    {
        var passed = 0;
        var failed = 0;

        void Check(string description, bool condition, string? detail = null)
        {
            if (condition)
            {
                passed++;
                Console.WriteLine($"    PASS  {description}");
            }
            else
            {
                failed++;
                Console.WriteLine($"    FAIL  {description}" + (detail is null ? "" : $"  ({detail})"));
            }
        }

        Console.WriteLine("Live update check");
        Console.WriteLine("-----------------");
        Console.WriteLine($"  running as version {AppUpdateService.CurrentVersion.ToString(3)}");
        Console.WriteLine($"  querying {AppUpdateService.RepositoryOwner}/{AppUpdateService.RepositoryName}");
        Console.WriteLine();

        var service = new AppUpdateService();

        var stopwatch = Stopwatch.StartNew();
        var update = await service.CheckAsync(CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();

        Console.WriteLine($"    check took {stopwatch.ElapsedMilliseconds} ms");

        if (update is null)
        {
            // Correct when running at or above the latest release; run with -p:Version=0.0.1 to
            // force a hit.
            Console.WriteLine();
            Console.WriteLine("    No update found. That is correct if this build is current.");
            Console.WriteLine("    To exercise the discovery path, rebuild with -p:Version=0.0.1");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"    found      {update.TagName}  ->  version {update.Version}");
        Console.WriteLine($"    asset      {update.InstallerName}");
        Console.WriteLine($"    size       {update.InstallerSizeBytes / (1024.0 * 1024):N1} MB");
        Console.WriteLine($"    url        {update.InstallerUrl}");
        Console.WriteLine($"    page       {update.ReleasePageUrl}");
        Console.WriteLine();

        Check("the release is newer than the running build",
            update.Version > AppUpdateService.CurrentVersion);

        Check("an installer asset was identified",
            update.CanInstallAutomatically, "no *setup*.exe in the release");

        Check("the download URL passes the host allow-list",
            update.InstallerUrl is not null && AppUpdateService.IsAllowedDownload(update.InstallerUrl));

        Check("the asset's size was reported", update.InstallerSizeBytes > 0);

        if (download && update.CanInstallAutomatically)
        {
            Console.WriteLine();
            Console.WriteLine("    downloading...");

            var lastReported = -1;
            var progress = new Progress<double>(fraction =>
            {
                var percent = (int)(fraction * 100);
                if (percent / 10 == lastReported / 10) return;
                lastReported = percent;
                Console.WriteLine($"      {percent,3}%");
            });

            try
            {
                var timer = Stopwatch.StartNew();
                var path = await service
                    .DownloadInstallerAsync(update, progress, CancellationToken.None)
                    .ConfigureAwait(false);
                timer.Stop();

                var info = new FileInfo(path);
                Console.WriteLine($"      saved to {path}");
                Console.WriteLine($"      {info.Length:N0} bytes in {timer.Elapsed.TotalSeconds:N1}s");

                Check("the downloaded file exists", info.Exists);
                Check("the downloaded size matches what the release declared",
                    info.Length == update.InstallerSizeBytes,
                    $"{info.Length:N0} vs {update.InstallerSizeBytes:N0}");

                // A truncated download must never be left behind wearing the final name.
                Check("no partial file was left behind",
                    !File.Exists(path + ".part"));

                // Windows executables start with "MZ"; anything else means an error page was saved.
                var header = new byte[2];
                using (var stream = File.OpenRead(path)) stream.ReadExactly(header);
                Check("the download is a real Windows executable, not an error page",
                    header[0] == 0x4D && header[1] == 0x5A,
                    $"starts with {header[0]:X2} {header[1]:X2}");

                Console.WriteLine();
                Console.WriteLine("    NOTE: the installer was downloaded but deliberately NOT run.");

                try { File.Delete(path); Console.WriteLine("    Cleaned up the download."); }
                catch { /* best effort */ }
            }
            catch (Exception ex)
            {
                Check("the download completed", false, ex.Message);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
