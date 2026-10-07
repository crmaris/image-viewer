[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('AllUsers','CurrentUser')][string]$Mode,
    [string]$AppExe,
    [switch]$Remove,
    [switch]$PlanOnly
)
$ErrorActionPreference = 'Stop'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$taskName = if ($Mode -eq 'AllUsers') { 'ImageViewer-AutoUpdate-AllUsers' } else { "ImageViewer-AutoUpdate-$sid" }
$cache = if ($Mode -eq 'AllUsers') { Join-Path $env:ProgramData 'ImageViewer\Updates' } else { Join-Path $env:LOCALAPPDATA 'ImageViewer\Updates' }
if ($PlanOnly) {
    [pscustomobject]@{TaskName=$taskName; Execute=$AppExe; Arguments='--auto-update'; Cache=$cache;
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
    foreach ($rule in (Get-Acl -LiteralPath (Split-Path $AppExe)).Access) {
        if ($rule.AccessControlType -eq 'Allow' -and
            $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -in $writers -and
            ($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Write)) {
            throw 'Refusing a SYSTEM updater for a directory writable by standard users.'
        }
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
$action = New-ScheduledTaskAction -Execute $AppExe -Argument '--auto-update'
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 30) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($logon,$hourly) `
    -Principal $taskPrincipal -Settings $settings -Description 'Install verified Image Viewer updates while the viewer is closed.' -Force | Out-Null
