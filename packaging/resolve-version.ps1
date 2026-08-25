<#
.SYNOPSIS
    Resolves the version used by local and CI packaging.

.DESCRIPTION
    An explicit -Version wins. Otherwise an exact v* tag on HEAD is used, with the project file as
    the normal development-build source and as the fallback for archives without Git metadata.
#>
[CmdletBinding()]
param(
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$resolved = $Version.Trim()

if (-not $resolved) {
    $tag = & git -C $root describe --tags --exact-match --match 'v[0-9]*' HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $tag) {
        $resolved = ([string]$tag).Trim() -replace '^[vV]', ''
    }
}

if (-not $resolved) {
    $project = Join-Path $root 'src\ImageViewer\ImageViewer.csproj'
    $text = [System.IO.File]::ReadAllText($project, [System.Text.Encoding]::UTF8)
    $match = [System.Text.RegularExpressions.Regex]::Match(
        $text, '<Version>([^<]+)</Version>')
    if ($match.Success) {
        $resolved = $match.Groups[1].Value.Trim()
    }
}

if ($resolved -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    throw "Version '$resolved' is not a valid package version."
}

Write-Output $resolved
