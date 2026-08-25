namespace ImageViewer.Update;

/// <summary>A release newer than the running build.</summary>
/// <param name="Version">Version parsed from the release tag.</param>
/// <param name="TagName">The raw tag, e.g. "v0.2.0".</param>
/// <param name="InstallerUrl">Direct download for the setup executable, if the release has one.</param>
/// <param name="InstallerName">File name of that asset.</param>
/// <param name="InstallerSizeBytes">Expected size, checked after download.</param>
/// <param name="InstallerDigest">GitHub's SHA-256 asset digest, checked after download.</param>
/// <param name="ReleasePageUrl">Human-readable release page, used when there is no installer asset.</param>
/// <param name="Notes">Release notes body, trimmed for display.</param>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string? InstallerUrl,
    string? InstallerName,
    long InstallerSizeBytes,
    string? InstallerDigest,
    string ReleasePageUrl,
    string? Notes)
{
    /// <summary>
    /// True when the update can be installed in place.
    /// </summary>
    /// <remarks>
    /// A release with no setup asset - or a portable copy running from a folder the user manages
    /// themselves - is offered as a link rather than an automatic install.
    /// </remarks>
    public bool CanInstallAutomatically =>
        !string.IsNullOrEmpty(InstallerUrl) &&
        AppUpdateService.IsSafeInstallerName(InstallerName) &&
        AppUpdateService.IsAllowedDigest(InstallerDigest);
}
