[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('AllUsers','CurrentUser')][string]$Mode,
    [string]$AppExe,
    [switch]$RunUpdate,
    [switch]$Remove,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = if ($Mode -eq 'AllUsers') { 'ImageViewer-AutoUpdate-AllUsers' } else { "ImageViewer-AutoUpdate-$sid" }
$cache = if ($Mode -eq 'AllUsers') { Join-Path $env:ProgramData 'ImageViewer\Updates' } else { Join-Path $env:LOCALAPPDATA 'ImageViewer\Updates' }
$taskArguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $PSCommandPath +
    '" -Mode ' + $Mode + ' -RunUpdate -AppExe "' + $AppExe + '"'
$taskHost = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if ($PlanOnly) {
    [pscustomobject]@{TaskName=$taskName; Execute=$taskHost; Arguments=$taskArguments; Cache=$cache;
        Principal=if($Mode -eq 'AllUsers'){'SYSTEM'}else{$sid};
        RunLevel=if($Mode -eq 'AllUsers'){'Highest'}else{'Limited'}; IntervalHours=1}
    return
}
if ($Remove) {
    Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false
    return
}
$AppExe = (Resolve-Path -LiteralPath $AppExe).Path
if ([IO.Path]::GetFileName($AppExe) -ne 'ImageViewer.exe') { throw 'Unexpected updater executable.' }
if ((Get-Item -LiteralPath $AppExe).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe updater executable.' }
foreach ($directory in @((Split-Path $cache), $cache)) {
    if ((Test-Path -LiteralPath $directory) -and
        ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Unsafe updater cache ancestor.'
    }
}
if ($Mode -eq 'AllUsers') {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'All-users setup requires elevation.' }
    # A SYSTEM task must never execute code from a directory writable by standard users.
    $writers = 'S-1-1-0','S-1-5-11','S-1-5-32-545'
    # Ask the ACL for SIDs directly: package/service identities need not have
    # a resolvable account name on this Windows installation.
    $rules = (Get-Acl -LiteralPath (Split-Path $AppExe)).GetAccessRules(
        $true,$true,[Security.Principal.SecurityIdentifier])
    foreach ($rule in $rules) {
        if ($rule.AccessControlType -eq 'Allow' -and
            $rule.IdentityReference.Value -in $writers -and
            (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write) -or
             ([long]$rule.FileSystemRights -band 0x50000000))) {
            throw 'Refusing a SYSTEM updater for a directory writable by standard users.'
        }
    }
}
if ($RunUpdate) {
    $pendingPath = Join-Path $cache 'pending-update.json'
    try {
        if ($Mode -eq 'CurrentUser') {
            New-Item -ItemType Directory -Path $cache -Force | Out-Null
            if ((Get-Item -LiteralPath $cache).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Unsafe per-user updater cache.'
            }
        }
        if (-not (Test-Path -LiteralPath $cache)) { throw 'Missing protected updater cache.' }
        if (Test-Path -LiteralPath $pendingPath) { [IO.File]::Delete($pendingPath) }
        $backend = Start-Process -FilePath $AppExe -ArgumentList '--auto-update' -WindowStyle Hidden -Wait -PassThru
        if ($backend.ExitCode -ne 0) { throw "Update preparation failed: $($backend.ExitCode)" }
        if (-not (Test-Path -LiteralPath $pendingPath)) { exit 0 }
        $pending = Get-Content -LiteralPath $pendingPath -Raw | ConvertFrom-Json
        $installer = [IO.Path]::GetFullPath($pending.InstallerPath)
        if ($pending.Mode -ne $Mode -or $pending.Version -notmatch '^\d+\.\d+\.\d+$' -or
            $installer -ne (Join-Path $cache "ImageViewer-$($pending.Version)-setup.exe") -or
            $pending.Digest -notmatch '^sha256:[0-9a-fA-F]{64}$') { throw 'Invalid prepared update.' }
        $expected = @("/$(if($Mode -eq 'AllUsers'){'ALLUSERS'}else{'CURRENTUSER'})", '/VERYSILENT',
            '/SUPPRESSMSGBOXES','/NORESTART','/NOCLOSEAPPLICATIONS','/NORESTARTAPPLICATIONS')
        if (($pending.Arguments -join '|') -ne ($expected -join '|')) { throw 'Unexpected installer arguments.' }
        # Recheck for a viewer opened during the download. A process we cannot inspect defers.
        foreach ($viewer in Get-Process -Name ImageViewer -ErrorAction SilentlyContinue) {
            try { $path = $viewer.Path } catch { exit 0 }
            if (-not $path -or $path -eq $AppExe) { exit 0 }
        }
        $stream = [IO.File]::Open($installer,'Open','Read','Read')
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() }
            finally { $sha.Dispose() }
            if ($actual -ne $pending.Digest.Substring(7).ToLowerInvariant()) { throw 'Installer changed after download.' }
            $setup = Start-Process -FilePath $installer -ArgumentList ([string[]]$pending.Arguments) -WindowStyle Hidden -PassThru
        } finally { $stream.Dispose() }
        $setup.WaitForExit()
        [IO.File]::WriteAllText((Join-Path $cache 'last-update.txt'),
            "$(Get-Date -Format o) version=$($pending.Version) exit=$($setup.ExitCode)")
        [IO.File]::Delete($pendingPath)
        if ($setup.ExitCode -notin @(0,3010)) { throw "Automatic installation failed: $($setup.ExitCode)" }
        exit 0
    } catch {
        if (Test-Path -LiteralPath $cache) {
            [IO.File]::WriteAllText((Join-Path $cache 'last-error.txt'), $_.ToString())
        } else { Write-Error $_ }
        exit 1
    }
}
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if ((Get-Item -LiteralPath $cache).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unsafe updater cache.' }
if ($Mode -eq 'AllUsers') {
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach ($identity in 'S-1-5-18','S-1-5-32-544') {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($identity)), 'FullControl',
            'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    # Protect the parent too: DeleteChild on an unprotected parent could replace
    # an otherwise protected cache directory before the SYSTEM task opens it.
    Set-Acl -LiteralPath (Split-Path $cache) -AclObject $acl
    Set-Acl -LiteralPath $cache -AclObject $acl
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $logon = New-ScheduledTaskTrigger -AtStartup
} else {
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $sid -LogonType Interactive -RunLevel Limited
    $logon = New-ScheduledTaskTrigger -AtLogOn -User $sid
}
$hourly = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Hours 1)
$action = New-ScheduledTaskAction -Execute $taskHost -Argument $taskArguments
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($logon,$hourly) `
    -Principal $taskPrincipal -Settings $settings -Description 'Install verified Image Viewer updates while the viewer is closed.' -Force | Out-Null
