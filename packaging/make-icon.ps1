<#
.SYNOPSIS
    Generates src/ImageViewer/app.ico, the application icon.

.DESCRIPTION
    Draws the icon as vectors at each target size rather than rendering once and downscaling, so
    the 16px version stays crisp instead of turning to mush. Packs the results into a proper
    multi-resolution .ico: uncompressed 32-bit DIBs for the sizes Explorer picks most often, and
    PNG compression for 128/256 where the DIB would be needlessly large.

    Design: a rounded-square blue-to-violet gradient carrying a white sun-and-mountains
    silhouette. Deliberately a bold silhouette with no fine detail or thin strokes - at 16px only
    the shape survives, and anything more delicate would smear.

.EXAMPLE
    pwsh -File packaging/make-icon.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputIcon = (Join-Path $PSScriptRoot "..\src\ImageViewer\app.ico"),
    [string]$PreviewPng = (Join-Path $PSScriptRoot "icon-preview.png")
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Explorer, the taskbar, Alt-Tab and the title bar all pick different sizes; supply them all so
# Windows never has to rescale and blur one.
$Sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

# Sizes at or below this are stored as DIBs for maximum shell compatibility; above it, PNG.
$PngThreshold = 128

function New-RoundedRectPath {
    param([float]$x, [float]$y, [float]$w, [float]$h, [float]$r)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)                      # top-left
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)            # top-right
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)    # bottom-right
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)             # bottom-left
    $path.CloseFigure()
    return $path
}

function New-ContentBitmap {
    <#
        Draws the artwork on a plain square with no rounding. The rounded silhouette is applied
        afterwards by New-IconBitmap; keeping the two steps separate is what lets the corners be
        antialiased (see the note there).
    #>
    param([int]$s)

    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    # Diagonal blue -> violet. Distinctive enough not to be mistaken for a stock Windows icon.
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        [System.Drawing.Point]::new(0, 0),
        [System.Drawing.Point]::new($s, $s),
        [System.Drawing.Color]::FromArgb(255, 0x2E, 0x7B, 0xFF),
        [System.Drawing.Color]::FromArgb(255, 0x8A, 0x3F, 0xFC))
    $g.FillRectangle($grad, 0, 0, $s, $s)
    $grad.Dispose()

    # Both peaks run past the bottom of the canvas and are trimmed by the rounded silhouette, so
    # they sit on the icon's own edge. An earlier version ended them partway up with a separate
    # baseline bar, which read as a stray line with the peaks floating above it.
    $overshoot = [float]($s * 1.06)

    # Back peak: a touch translucent so it reads as further away without muddying 16px.
    $back = New-Object System.Drawing.Drawing2D.GraphicsPath
    $back.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($s * 0.46, $overshoot)
        [System.Drawing.PointF]::new($s * 0.695, $s * 0.505)
        [System.Drawing.PointF]::new($s * 0.925, $overshoot)
    ))
    $backBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(238, 255, 255, 255))
    $g.FillPath($backBrush, $back)
    $backBrush.Dispose(); $back.Dispose()

    # Sun, sitting clear of both peaks.
    $sunR = [float]($s * 0.105)
    $sunX = [float]($s * 0.715 - $sunR)
    $sunY = [float]($s * 0.275 - $sunR)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillEllipse($white, $sunX, $sunY, $sunR * 2, $sunR * 2)

    # Front peak: solid white and the dominant shape at every size.
    $front = New-Object System.Drawing.Drawing2D.GraphicsPath
    $front.AddPolygon([System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($s * 0.075, $overshoot)
        [System.Drawing.PointF]::new($s * 0.365, $s * 0.385)
        [System.Drawing.PointF]::new($s * 0.655, $overshoot)
    ))
    $g.FillPath($white, $front)
    $front.Dispose()
    $white.Dispose()

    $g.Dispose()
    return $bmp
}

function New-IconBitmap {
    param([int]$s)

    # Render the artwork square, then paint it through the rounded path with a texture brush.
    #
    # The obvious approach - Graphics.SetClip(roundedPath) - does not work: clipping uses a
    # hard-edged region, so the mountains ran to the bottom corners with visible jaggies against
    # the smoothly antialiased background. Filling a path with a TextureBrush antialiases the
    # silhouette itself, so the corners stay clean at every size.
    $content = New-ContentBitmap -s $s

    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # A hair of inset so the antialiased corner pixels are not clipped by the bitmap edge.
    $pad = [float]($s * 0.02)
    $box = [float]($s - 2 * $pad)
    $radius = [float]($s * 0.215)

    $shape = New-RoundedRectPath -x $pad -y $pad -w $box -h $box -r $radius
    $texture = New-Object System.Drawing.TextureBrush($content)
    $g.FillPath($texture, $shape)

    $texture.Dispose(); $shape.Dispose(); $g.Dispose(); $content.Dispose()
    return $bmp
}

function Get-DibBytes {
    <# 32bpp BITMAPINFOHEADER + bottom-up BGRA pixels + an empty AND mask, per the ICO spec. #>
    param([System.Drawing.Bitmap]$bmp)

    $w = $bmp.Width; $h = $bmp.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $w, $h)
    $locked = $bmp.LockBits($rect,
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

    $stride = $locked.Stride
    $pixels = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    $bmp.UnlockBits($locked)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)

    # BITMAPINFOHEADER. Height is doubled because the structure describes the colour bitmap and
    # the AND mask stacked together, even when the mask is unused.
    $bw.Write([uint32]40)
    $bw.Write([int32]$w)
    $bw.Write([int32]($h * 2))
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]0)          # BI_RGB
    $bw.Write([uint32]($w * $h * 4))
    $bw.Write([int32]0); $bw.Write([int32]0)
    $bw.Write([uint32]0); $bw.Write([uint32]0)

    # Colour data, bottom-up.
    for ($row = $h - 1; $row -ge 0; $row--) {
        $bw.Write($pixels, $row * $stride, $w * 4)
    }

    # AND mask: all zeros, since the 32-bit alpha channel already carries transparency.
    $maskStride = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $bw.Write((New-Object byte[] ($maskStride * $h)))

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return $bytes
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$bmp)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return $bytes
}

# ---------------------------------------------------------------- build

Write-Host "Rendering $($Sizes.Count) sizes..." -ForegroundColor Cyan

$entries = @()
foreach ($size in $Sizes) {
    $bmp = New-IconBitmap -s $size
    $data = if ($size -ge $PngThreshold) { Get-PngBytes $bmp } else { Get-DibBytes $bmp }
    $entries += [pscustomobject]@{ Size = $size; Data = $data; Png = ($size -ge $PngThreshold) }

    if ($size -eq 256) {
        $bmp.Save($PreviewPng, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
    Write-Host ("  {0,3}x{1,-3} {2,7:N0} bytes  {3}" -f $size, $size, $data.Length, $(if ($size -ge $PngThreshold) { 'PNG' } else { 'DIB' }))
}

$dir = Split-Path $OutputIcon -Parent
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$fs = [System.IO.File]::Create($OutputIcon)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    # ICONDIR
    $bw.Write([uint16]0)                     # reserved
    $bw.Write([uint16]1)                     # type: 1 = icon
    $bw.Write([uint16]$entries.Count)

    # ICONDIRENTRY table. Image data follows immediately after it.
    $offset = 6 + (16 * $entries.Count)
    foreach ($e in $entries) {
        # 256 is encoded as 0 in a single byte.
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $bw.Write([byte]$dim)                # width
        $bw.Write([byte]$dim)                # height
        $bw.Write([byte]0)                   # palette size (0 = no palette)
        $bw.Write([byte]0)                   # reserved
        $bw.Write([uint16]1)                 # colour planes
        $bw.Write([uint16]32)                # bits per pixel
        $bw.Write([uint32]$e.Data.Length)
        $bw.Write([uint32]$offset)
        $offset += $e.Data.Length
    }

    # The [byte[]] cast is load-bearing: reading the array back off a pscustomobject hands
    # BinaryWriter a PSObject, which resolves to a single-value Write overload and silently
    # emits one byte per entry instead of the image.
    foreach ($e in $entries) { $bw.Write([byte[]]$e.Data) }
}
finally {
    $bw.Dispose(); $fs.Dispose()
}

# Guard against exactly the failure above recurring unnoticed.
$expected = 6 + (16 * $entries.Count) + (($entries | Measure-Object -Property { $_.Data.Length } -Sum).Sum)
$actual = (Get-Item $OutputIcon).Length
if ($actual -ne $expected) {
    Write-Host "SIZE MISMATCH: wrote $actual bytes, expected $expected" -ForegroundColor Red
    exit 1
}

$icoInfo = Get-Item $OutputIcon
Write-Host "`nWrote $($icoInfo.FullName)  ($('{0:N0}' -f $icoInfo.Length) bytes, $($entries.Count) sizes)" -ForegroundColor Green
Write-Host "Preview: $PreviewPng"

# Round-trip the result so a malformed container is caught here rather than at build time.
try {
    $check = New-Object System.Drawing.Icon($OutputIcon, 32, 32)
    Write-Host "Verified: Windows loads the 32x32 entry ($($check.Width)x$($check.Height))" -ForegroundColor Green
    $check.Dispose()
}
catch {
    Write-Host "VERIFY FAILED: $_" -ForegroundColor Red
    exit 1
}
