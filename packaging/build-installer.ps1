<#
.SYNOPSIS
    Publishes Image Viewer and builds the Inno Setup installer.

.DESCRIPTION
    Runs build-portable.ps1 first (which also regenerates the icon), then compiles
    packaging/ImageViewer.iss into build/ImageViewer-<version>-setup.exe.

    Requires Inno Setup 6, which is NOT bundled: https://jrsoftware.org/isdl.php
    Or via winget:  winget install JRSoftware.InnoSetup

.PARAMETER SkipPublish
    Reuse the existing build/portable output instead of republishing.

.EXAMPLE
    pwsh -File packaging/build-installer.ps1
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$script = Join-Path $PSScriptRoot 'ImageViewer.iss'
$publishDir = Join-Path $root "build\portable\$Runtime"
$outputDir = Join-Path $root 'build'

# Locate the compiler before doing any expensive work.
# winget installs Inno Setup per-user by default, which puts it under LOCALAPPDATA rather than
# either Program Files. Omitting that path made this script report Inno Setup as missing on a
# machine where it was installed and working, and the project carried "Inno Setup is not installed
# here" as a fact for a week because of it.
$candidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    Write-Host "Inno Setup 6 was not found." -ForegroundColor Yellow
    Write-Host "Looked in:"
    $candidates | ForEach-Object { Write-Host "  $_" }
    Write-Host ""
    Write-Host "Install it with:" -ForegroundColor Cyan
    Write-Host "  winget install JRSoftware.InnoSetup"
    Write-Host "or download from https://jrsoftware.org/isdl.php, then run this script again."
    Write-Host ""
    Write-Host "The portable build does not need Inno Setup - use build-portable.ps1 for that."
    exit 1
}

Write-Host "Inno Setup: $iscc" -ForegroundColor DarkGray

if (-not $SkipPublish) {
    & (Join-Path $PSScriptRoot 'build-portable.ps1') -Runtime $Runtime -SkipZip
    if ($LASTEXITCODE -ne 0) { throw "Publish step failed." }
}

if (-not (Test-Path (Join-Path $publishDir 'ImageViewer.exe'))) {
    throw "Nothing published at $publishDir. Run without -SkipPublish."
}

Write-Host "`nCompiling installer..." -ForegroundColor Cyan

& $iscc `
    "/DSourceDir=$publishDir" `
    "/DOutputDir=$outputDir" `
    $script

if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$setup = Get-ChildItem $outputDir -Filter 'ImageViewer-*-setup.exe' |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($setup) {
    Write-Host ("`nInstaller: {0} ({1:N1} MB)" -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
    Write-Host "It installs per-user by default, so no elevation prompt appears."
    Write-Host "File associations are additive: Image Viewer is added to the 'Open with' list."
    Write-Host "To make it the default, use Open with > Choose another app, or Settings > Default apps."
}
