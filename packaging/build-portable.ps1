<#
.SYNOPSIS
    Publishes the portable build of Image Viewer and zips it.

.DESCRIPTION
    Produces a self-contained, ReadyToRun folder build. Deliberately NOT a single-file publish:
    bundling the native dependencies means .NET extracts them to a temp directory on first run,
    which costs roughly a second the first time - unacceptable for an application whose entire
    point is starting instantly. A zipped folder has no such cost.

.PARAMETER Runtime
    Target RID. win-x64 unless you are building for ARM.

.PARAMETER SkipZip
    Leave the published folder without packing it, for quick local testing.

.EXAMPLE
    pwsh -File packaging/build-portable.ps1
#>
[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release',
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\ImageViewer\ImageViewer.csproj'
$outDir = Join-Path $root "build\portable\$Runtime"
$zipPath = Join-Path $root "build\ImageViewer-portable-$Runtime.zip"

if (-not (Test-Path $project)) { throw "Project not found: $project" }

# Regenerate the icon so a tweak to make-icon.ps1 can never ship stale.
& (Join-Path $PSScriptRoot 'make-icon.ps1') | Out-Null

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

Write-Host "Publishing $Configuration / $Runtime ..." -ForegroundColor Cyan

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishReadyToRun=true `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en `
    -o $outDir `
    --nologo `
    -v minimal

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $outDir 'ImageViewer.exe'
if (-not (Test-Path $exe)) { throw "Publish completed but $exe is missing." }

$size = (Get-ChildItem $outDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
Write-Host ("`nPublished to {0}" -f $outDir) -ForegroundColor Green
Write-Host ("  {0:N0} files, {1:N1} MB" -f (Get-ChildItem $outDir -Recurse -File).Count, ($size / 1MB))

if (-not $SkipZip) {
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host ("  Zip: {0} ({1:N1} MB)" -f $zipPath, ((Get-Item $zipPath).Length / 1MB)) -ForegroundColor Green
}

Write-Host "`nRun it with:`n  $exe" -ForegroundColor Cyan
