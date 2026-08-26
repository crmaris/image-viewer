<div align="center">

<img src="packaging/icon-preview.png" width="120" alt="Image Viewer icon">

# Image Viewer

**A plain, fast image viewer for Windows.**
Opens essentially any image format. Browse a folder with Space or the mouse wheel.

</div>

---

## Why

Windows Photos is slow to launch and slow to page through a folder. This is the opposite: it opens
what you double-click, gets out of the way, and moves between images in a single frame.

## What it opens

JPEG, PNG, GIF, BMP, TIFF, ICO, DDS, JXR, WebP, HEIC/HEIF, AVIF, JPEG-XL, SVG/SVGZ, PSD, TGA, EXR,
JPEG 2000, QOI, PCX, PNM, Radiance HDR, and camera RAW from Canon, Nikon, Sony, Fujifilm, Olympus,
Panasonic, Pentax, Leica, Hasselblad, Phase One, Sigma and others.

Format is detected from the file's **bytes**, not its extension — a PNG named `.jpg` still opens.

## Controls

| Input | Action |
|---|---|
| **Space** / → / PgDn / **wheel down** | Next image |
| Backspace / ← / PgUp / **wheel up** | Previous |
| Home / End | First / last |
| **Ctrl + wheel** | Zoom at cursor |
| Left-drag | Pan |
| Right-click image | Rotate left/right or save rotation |
| Ctrl+← / Ctrl+→ | Rotate 90° |
| H / V | Flip horizontal / vertical |
| Ctrl+S | Save rotation (lossless for JPEG) |
| Ctrl+Shift+S | Save with pixels physically rotated |
| `0` `1` `+` `-` | Fit / 100% / zoom in / out |
| F11, double-click | Fullscreen |
| Del / Shift+Del | Recycle Bin / delete permanently |
| Ctrl+C / Ctrl+Shift+C | Copy image / copy path |
| F2 | Rename |
| E · I · T · S | Show in Explorer · info · filmstrip · slideshow |
| `,` `.` during slideshow | Slower / faster |
| Ctrl+U | Install update, if one is available |
| Esc | Stop slideshow, leave fullscreen, or close |

The wheel navigates rather than zooms; zoom is on Ctrl+wheel.

## Command line

The same executable is a command-line tool. Tick **Add to PATH** during installation and
`imageviewer` works from any shell; otherwise call it by its full path.

```
imageviewer info <file>...              dimensions, format, decoder and EXIF
imageviewer identify <file>...          detect the real format from the file's bytes
imageviewer list <folder>               list images in viewing order
imageviewer formats                     every readable and writable format
imageviewer convert <in> <out>          convert between formats
imageviewer resize <in> <out> --width N resize, preserving aspect ratio
imageviewer thumb <in> <out> --size N   write a thumbnail
imageviewer rotate <file> --cw          rotate in place, losslessly for JPEG
imageviewer flip <file> --horizontal    mirror in place, losslessly for JPEG
```

Every command takes several inputs, expands wildcards and folders itself, and writes batches with
`--out-dir`. `imageviewer help <command>` prints the options for one. Exit codes are `0` for
success, `1` for a failure and `2` for bad usage.

```bash
imageviewer resize photos --out-dir web --width 1600 --quality 85
```

```bash
imageviewer identify photos --mismatched-only
```

```bash
imageviewer rotate *.jpg --cw
```

`rotate` and `flip` go through the same EXIF writer as **Ctrl+S** in the window, so repeating them
on a JPEG never re-compresses it. `resize` and `thumb` scale during decoding, so a 24 MP photograph
is never fully decoded just to be thrown away, and `--embedded` pulls the camera's own preview when
one is present.

The viewer also takes launch options: `--fullscreen`, and `--slideshow[=SECONDS]`.

Because the executable is a Windows GUI application, a shell does not wait for it — the prompt comes
back before the output prints. Redirecting to a file or a pipe behaves normally, which is the case
that matters for scripting.

## Speed

Measured on a Ryzen workstation, Windows 11:

| | |
|---|---|
| 24 MP JPEG, decode to fit window | **~20 ms** |
| First pixels (embedded thumbnail) | **~0.7 ms** |
| Navigate to a prefetched image | **~0.02 ms** |
| Open a second image from Explorer | **~110–150 ms** |
| Cold start | ~1.0–1.2 s |

Cold start is dominated by WPF itself — an empty WPF window measures ~945 ms on the same machine, so
that is the floor rather than anything this application does. It matters little in practice: a
single-instance pipe means the first launch pays it once and every image after that reuses the
running process.

How the rest is achieved:

- **Embedded thumbnails first.** JPEG/HEIC/RAW previews reach the screen ~30× sooner than a full
  decode, then get replaced silently.
- **Decode to display size.** A 6000 px photo on a 1440p monitor decodes ~3× faster and uses ~5×
  less memory than decoding full-size and scaling down.
- **Prefetch ring.** Neighbouring images are decoded before you ask for them, so Space paints in one
  frame.
- **Tiered decoders.** Windows' own codecs handle almost everything; ~55 MB of fallback libraries
  cover the rest and are never loaded unless an exotic file is actually opened. A test asserts this.

## Rotation is genuinely lossless

Rotating a JPEG rewrites its EXIF orientation and leaves the compressed data untouched — no
re-encoding, no generational quality loss, instant at any size.

The usual approach, `JpegBitmapEncoder.Rotation`, is widely believed to be a lossless block
transform. It is not: measured here, a full 360° turn changed 301,079 of 3,145,728 pixel bytes and
grew the file by 18%. If you need pixels physically rotated (for software that ignores EXIF),
Ctrl+Shift+S does that instead, with the re-compression that implies.

## Install

Download the setup from [Releases](https://github.com/crmaris/image-viewer/releases). It installs
per-user, so there is no elevation prompt, and adds Image Viewer to the "Open with" list without
hijacking your existing defaults — set it as default yourself via *Open with → Choose another app*.

A portable zip is also published; unzip it anywhere and run `ImageViewer.exe`.

The app checks for updates once a day in the background and tells you if one is available. Nothing
is downloaded or run without you saying so.

## Build

Requires the .NET 10 SDK.

```bash
dotnet build src/ImageViewer/ImageViewer.csproj
```

```bash
pwsh -File packaging/build-portable.ps1
```

The installer additionally needs [Inno Setup 6](https://jrsoftware.org/isdl.php):

```bash
pwsh -File packaging/build-installer.ps1
```

### Tests

```bash
dotnet run --project tests/ImageViewer.SelfTest -- --make-corpus testimages
```

```bash
dotnet run --project tests/ImageViewer.SelfTest -- testimages
```

The first command generates test files in the exotic formats so the fallback decoders are actually
exercised. There is also `--assembly-check`, which must run as its own process and verifies the heavy
decoder libraries stay unloaded during ordinary viewing.

## Licence

MIT — see [LICENSE](LICENSE). Contributor notes are in [CLAUDE.md](CLAUDE.md).
