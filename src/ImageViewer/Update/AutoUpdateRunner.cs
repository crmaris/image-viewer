using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace ImageViewer.Update;

/// <summary>Headless scheduled updater; never closes the owner's active viewer.</summary>
internal static class AutoUpdateRunner
{
    private const string UninstallKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{7C3F1A62-9E4D-4B8A-9F21-2D6B5E0C4A17}_is1";

    internal static AppUpdateService.InstallMode OwnInstallMode()
    {
        foreach (var (hive, mode) in new[] {
                     (Registry.LocalMachine, AppUpdateService.InstallMode.AllUsers),
                     (Registry.CurrentUser, AppUpdateService.InstallMode.CurrentUser) })
        {
            using var key = hive.OpenSubKey(UninstallKey);
            if (key?.GetValue("InstallLocation") is string location &&
                SameDirectory(location, AppContext.BaseDirectory))
                return mode;
        }
        return AppUpdateService.InstallMode.Unknown;
    }

    internal static bool SameDirectory(string first, string second) =>
        string.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar),
                      Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar),
                      StringComparison.OrdinalIgnoreCase);

    private static bool ViewerIsOpen()
    {
        foreach (var process in Process.GetProcessesByName("ImageViewer"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (process.MainModule?.FileName is string path &&
                        SameDirectory(Path.GetDirectoryName(path)!, AppContext.BaseDirectory))
                        return true;
                }
                catch { return true; } // unreadable process state is a reason to defer
            }
        }
        return false;
    }

    internal static async Task<int> RunAsync()
    {
        string? cache = null;
        try
        {
            var mode = OwnInstallMode();
            if (mode == AppUpdateService.InstallMode.Unknown) return 0;
            using var identity = WindowsIdentity.GetCurrent();
            if (mode == AppUpdateService.InstallMode.AllUsers && !identity.IsSystem) return 0;
            if (ViewerIsOpen()) return 0;
            cache = Path.Combine(Environment.GetFolderPath(mode == AppUpdateService.InstallMode.AllUsers
                ? Environment.SpecialFolder.CommonApplicationData : Environment.SpecialFolder.LocalApplicationData),
                "ImageViewer", "Updates");
            // The installer creates the SYSTEM cache with a protected ACL. Never create an
            // arbitrary administrator-writable directory during an unattended update.
            if (!Directory.Exists(cache) || (File.GetAttributes(cache) & FileAttributes.ReparsePoint) != 0)
                return 1;
            using var mutex = new Mutex(false, mode == AppUpdateService.InstallMode.AllUsers
                ? "Global\\ImageViewerAutomaticUpdate" : "Local\\ImageViewerAutomaticUpdate");
            if (!mutex.WaitOne(0)) return 0;
            try
            {
                var service = new AppUpdateService();
                var update = await service.CheckAsync(CancellationToken.None);
                if (update is null || !update.CanInstallAutomatically) return 0;
                // The task must never execute an asset from another repository or channel.
                var expected = $"https://github.com/crmaris/image-viewer/releases/download/{update.TagName}/{update.InstallerName}";
                if (update.TagName != $"v{update.Version.ToString(3)}" ||
                    update.InstallerName != $"ImageViewer-{update.Version.ToString(3)}-setup.exe" ||
                    update.InstallerUrl != expected) return 1;
                var installer = await service.DownloadInstallerAsync(update, null, CancellationToken.None, cache);
                if (ViewerIsOpen()) return 0;
                // Keep the currently installed EXE and launch helper for diagnosis/rollback.
                var old = Path.Combine(cache, "previous");
                Directory.CreateDirectory(old);
                foreach (var file in new[] { "ImageViewer.exe", "ImageViewer.dll", "windows-auto-update.ps1" })
                {
                    var path = Path.Combine(AppContext.BaseDirectory, file);
                    if (File.Exists(path)) File.Copy(path, Path.Combine(old, file), true);
                }
                // The scheduled PowerShell host waits for this process to exit before launching
                // Setup. Waiting here would hold the very EXE/DLL that Setup must replace.
                var pending = JsonSerializer.Serialize(new
                {
                    InstallerPath = installer, Digest = update.InstallerDigest,
                    Version = update.Version.ToString(3), Mode = mode.ToString(),
                    Arguments = AppUpdateService.BuildInstallerArguments(mode)
                        .Concat(AppUpdateService.BuildUnattendedArguments()).ToArray(),
                });
                var target = Path.Combine(cache, "pending-update.json");
                File.WriteAllText(target + ".tmp", pending);
                File.Move(target + ".tmp", target, overwrite: true);
                return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception error)
        {
            if (cache is not null)
            {
                try { File.WriteAllText(Path.Combine(cache, "last-error.txt"), error.ToString()); }
                catch { }
            }
            return 1;
        }
    }
}
