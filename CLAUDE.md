# Image Viewer — handover

**Canonical handover document.** `AGENTS.md` is a thin pointer to this file; keep the content here.

A fast, plain Windows image viewer. Opens essentially any image format, starts as quickly as WPF
allows, and walks a folder with **Space** or the **mouse wheel**. Built 2026-08-13/14.

- **Stack:** C# / .NET 10 (`net10.0-windows`), WPF, x64.
- **Version:** 0.1.0
- **Repo layout:** `src/ImageViewer` (app), `tests/ImageViewer.SelfTest` (checks + benchmarks),
  `packaging` (icon generator, publish scripts, Inno Setup script).
- **Not a git repo yet.** No commits have been made; `git init` when you want history.

---

## Build, run, test

```bash
dotnet build "src/ImageViewer/ImageViewer.csproj"
```

```bash
dotnet run --project "tests/ImageViewer.SelfTest/ImageViewer.SelfTest.csproj" -- <test-image-folder>
```

```bash
dotnet run --project "tests/ImageViewer.SelfTest/ImageViewer.SelfTest.csproj" -- --make-corpus <folder>
```

```bash
dotnet run --project "tests/ImageViewer.SelfTest/ImageViewer.SelfTest.csproj" -- --assembly-check <folder>
```

```bash
pwsh -File packaging/build-portable.ps1
```

```bash
pwsh -File packaging/build-installer.ps1
```

`--make-corpus` writes PSD/TGA/EXR/JP2/QOI/SVG/animated-GIF test files using Magick.NET; run it once
before the main suite so the fallback tiers are actually exercised. `--assembly-check` **must** run
as its own process — see "The one architectural invariant" below.

118 checks currently pass. Inno Setup 6 is **not installed on this machine**; `build-installer.ps1`
detects that and tells you how to get it (`winget install JRSoftware.InnoSetup`).

---

## The one architectural invariant

The app references ImageSharp, SkiaSharp and Magick.NET — roughly **55 MB of decoders**. Startup
speed depends entirely on the CLR never loading them unless an exotic file is actually opened.

That works because **every reference to those libraries lives inside a method body**, never in a
field, constructor, or method signature. The CLR loads an assembly when it JITs a method that
mentions it, so a fields-and-signatures-free design keeps them cold. `MagickDecoder`,
`ImageSharpDecoder` and `SvgDecoder` also mark their entry points `MethodImpl(NoInlining)`.

**This is one careless edit away from breaking.** `--assembly-check` decodes 48 everyday images in a
fresh process and asserts none of those assemblies loaded. Run it after touching anything in
`src/ImageViewer/Imaging`.

---

## Decoder tiers

Format is identified from the file's **bytes**, not its extension, so a PNG named `.jpg` still opens.
The extension is only consulted when content cannot decide (gzipped SVG, camera RAW).

| Tier | Component | Covers |
|---|---|---|
| 1 | **WIC** (built into Windows) | JPEG, PNG, GIF, BMP, TIFF, ICO, DDS, JXR + HEIC/WebP/JPEG-XL/AVIF/RAW where the Windows codec is installed |
| 2 | **ImageSharp 2.1.13** | JPEG, PNG, GIF, BMP, TIFF, WebP, TGA, PNM — works with no Windows codecs at all |
| 3 | **Svg.Skia** | SVG, SVGZ (rasterised to viewport size) |
| 4 | **Magick.NET Q8** | PSD, XCF, EXR, JP2, PCX, HDR, QOI and a few hundred more |

**ImageSharp is pinned to 2.1.13 deliberately.** Version 3.0 moved from Apache-2.0 to the Six Labors
Split License; building against 4.x emits a "no license found" warning and needs a commercial
licence for most use. 2.1.13 is the last Apache-2.0 release and covers everything this tier needs.
**Do not "upgrade" it** without a licensing decision.

---

## Rotate and save — read before changing

`Ctrl+S` writes the on-screen rotation/flip back to the file.

**`JpegBitmapEncoder.Rotation` is NOT lossless**, despite its widespread reputation as a block
transform. Measured 2026-08-14: rotating a JPEG through a full turn changed **301,079 of 3,145,728
pixel bytes** and grew the file from 16,026 to 18,886 bytes. It silently re-encodes. The plan had
assumed it was lossless; the verification step caught it.

What ships instead: **`JpegOrientationWriter` patches the EXIF orientation bytes directly**, leaving
the compressed scan data untouched. Genuinely zero-loss, effectively instant at any image size, and
verified by rotating twelve times with byte-identical pixels and a constant file size. A JPEG with no
EXIF block gets a minimal 36-byte APP1 segment inserted on first save; after that the file size never
changes again.

- Trade-off: pixels stay as stored and the tag says how to present them. Every current browser,
  Explorer, Windows Photos and the major editors honour it; software that ignores EXIF will not.
- `Ctrl+Shift+S` forces a physical re-encode for that case (quality 100, still lossy — confirmed
  168,014 bytes changed over a full turn).
- PNG/BMP/TIFF always re-encode, which is lossless for those formats.
- Writes go via a temp file plus `File.Replace`, so a crash cannot destroy the original.

`Orientation` (in `Editing/`) composes the file's existing EXIF transform with the user's edit into a
single transform. This is not optional bookkeeping: **mirroring and rotation do not commute**, so
naively adding rotations produces the wrong result for any flipped image.

---

## Measured performance (2026-08-14, this machine)

| Metric | Planned | **Measured** |
|---|---|---|
| Cold start → first frame | < 300 ms | **~1.0–1.2 s** |
| — of which, empty WPF window | — | **~945 ms** |
| Warm start (single-instance handoff) | < 50 ms | **~110–150 ms** |
| 24 MP JPEG, decode-to-fit | < 150 ms | **~20 ms** |
| 24 MP JPEG, full resolution | — | **~26 ms** |
| Embedded thumbnail (first pixels) | < 30 ms | **~0.7 ms** (≈30× faster than full decode) |
| Cached navigation | < 16 ms | **~0.02 ms** |
| Folder scan + natural sort, 31 files | — | **0.3 ms** |

**The 300 ms cold-start budget was not achievable and was wrong to promise.** A minimal WPF app —
empty window, no XAML, no image code — takes ~945 ms to first frame on this machine, so that is the
floor. Image Viewer adds only ~100 ms on top of it. Verified not to be fixable by publish settings:

- ReadyToRun **is** applied (RTR signature present; DLL 209 KB vs 119 KB IL-only).
- Self-contained vs framework-dependent: no measurable difference.
- SSD vs HDD: no measurable difference (see the disk note below).
- Render tier is **2** (full hardware acceleration), so it is not a software-rendering fallback.

The design's answer to WPF's startup cost is the **single-instance named pipe**: the first launch
pays it once, and every subsequent double-click hands the path to the running process. If cold start
ever needs to drop further, the remaining lever is a NativeAOT launcher stub that does the
mutex-and-pipe check before any WPF type is touched (~20 ms instead of ~150 ms). Not built — it adds
a second executable and deployment complexity that has not been justified.

### Benchmark note

**`E:` is the 8 TB WDC spinning disk; `C:` is the ADATA SSD.** The project lives on `E:`. This was
checked during benchmarking and turned out not to matter, but any future disk-sensitive measurement
should account for it.

Startup instrumentation is built in and costs nothing unless enabled: set
`IMAGEVIEWER_STARTUP_LOG` to a file path and the app appends per-stage timings
(`main`, `solo`, `startup`, `ctor`, `shown`, `rendered`), measured from `Process.StartTime`.

---

## How the speed is actually achieved

- **Single-instance named pipe.** `SingleInstance.TryHandOff` runs in `Main` *before any WPF type is
  referenced*, so a second launch never initialises a UI framework it is about to discard.
- **Embedded thumbnail first.** JPEG/HEIC/RAW previews reach the screen ~30× sooner than the full
  decode, which then replaces them. The thumbnail reports the *full* frame's dimensions so the swap
  produces no visible jump.
- **Decode-to-fit.** `DecodePixelWidth`/`Height` sized to the viewport. `DecodedImage.PixelWidth`
  always reports the *original* size so zoom percentages and the info overlay stay truthful.
- **Prefetch ring + LRU cache.** Three ahead, one behind, bounded by bytes (25% of RAM, capped at
  1.5 GB) rather than item count. A cached navigation paints synchronously inside the input event —
  no `await`, so no dropped frame.
- **Wheel coalescing.** A 70 ms debounce absorbs a fast spin; it only costs anything on a cache miss.
- **No XAML.** The window is built in code; no `App.xaml`, no resource dictionary, no BAML parsing.
- **Lazy overlays.** The info panel, filmstrip, toast and even the status `TextBlock` are created on
  first use — building a `TextBlock` is what first touches WPF's text/font stack, worth ~150 ms.

---

## Controls

| Input | Action |
|---|---|
| **Space** / → / PgDn / **wheel down** | Next image |
| Backspace / ← / PgUp / **wheel up** | Previous |
| Home / End | First / last |
| **Ctrl + wheel** | Zoom at cursor |
| Left-drag | Pan |
| Ctrl+← / Ctrl+→ | Rotate 90° CCW / CW |
| H / V | Flip horizontal / vertical |
| Ctrl+S / Ctrl+Shift+S | Save (lossless) / save re-encoded |
| `0` `1` `+` `-` | Fit / 100% / zoom in / out |
| F11, double-click | Fullscreen |
| Del / Shift+Del | Recycle Bin / permanent (confirmed) |
| Ctrl+C / Ctrl+Shift+C | Copy image / copy path |
| F2 · E · I · T · S | Rename · show in Explorer · info · filmstrip · slideshow |
| `,` `.` during slideshow | Slower / faster |
| Esc | Stop slideshow, else leave fullscreen, else close |

Wheel navigates rather than zooms, as requested; zoom is on Ctrl+wheel.

---

## Gotchas found the hard way

- **`System.IO` is not in this project's implicit usings** despite `ImplicitUsings=enable`. Add
  `using System.IO;` explicitly.
- **`TransformedBitmap` rejects a `TransformGroup`.** Chain two `TransformedBitmap`s instead; they
  are evaluated lazily so it costs nothing.
- **`ContentRendered` never fires on a window whose `Content` is null.** This silently broke the
  startup benchmark before the baseline app was given a `Grid`.
- **WIC does not apply EXIF orientation.** Without `WicDecoder.ApplyOrientation`, every rotated photo
  shows sideways.
- **A superseded decode can still finish.** Native decoders do not stop on a cancellation request, so
  both the success and failure paths check `IsStillCurrent(path)` before publishing anything. Missing
  this made a healthy GIF report as "unreadable" because a corrupt file's decode landed late.
- **TGA has no magic number.** Its header collides with ICO/CUR (`00 00 02 00`); the image-count
  field at offset 4 is what separates them.
- **PowerShell:** `BinaryWriter.Write` on an array read back off a `pscustomobject` resolves to a
  single-value overload and silently writes one byte. Cast `[byte[]]` — this produced a 159-byte
  "icon" before the size assertion was added to `make-icon.ps1`.

---

## Icon

`packaging/make-icon.ps1` generates `src/ImageViewer/app.ico` — nine sizes (16–256), DIB for the
small ones Explorer favours, PNG for 128/256. Vectors are drawn at each size rather than downscaled
from one render, so 16 px stays crisp. **Do not hand-edit the .ico**; change the script and re-run.
`build-portable.ps1` regenerates it automatically so a stale icon cannot ship.

The rounded silhouette is applied with a `TextureBrush` fill rather than `Graphics.SetClip`, because
clipping uses a hard-edged region and left visibly jagged bottom corners.

---

## Installer

`packaging/ImageViewer.iss`. Per-user by default (no elevation prompt), all-users optional.

File associations are **additive** — each extension gets an `OpenWithProgids` entry, putting Image
Viewer in the "Open with" list without seizing the default handler. Windows 10/11 block installers
from silently changing defaults anyway. The user chooses via *Open with → Choose another app* or
*Settings → Default apps*.

The 55 associated extensions are duplicated between the `.iss` and
`SupportedFormats.AssociatableExtensions`; they cannot share a definition across languages, so the
self-test parses the `.iss` and fails if the two lists drift.

---

## Session log

### 2026-08-14 — Phases 3–5: decoder tiers, image functions, packaging
- Added tiers 2–4 (ImageSharp / Svg.Skia / Magick.NET) behind `DecoderChain`, plus `FormatSniffer`
  (content-based identification with extension fallback) and `GifAnimator` (WPF does not animate
  GIFs; frames are pre-composited with disposal-method handling).
- **Downgraded ImageSharp 4.1.0 → 2.1.13** on discovering the licence change. Owner decision needed
  if 3.x+ features are ever wanted.
- **Discovered `JpegBitmapEncoder.Rotation` is not lossless** and replaced it with direct EXIF-byte
  patching. See "Rotate and save" above.
- Added `Orientation` (D4 composition), `ImageSaver`, `ShellOps`, `ExifSummary`, and the
  `InfoOverlay` / `Toast` / `Filmstrip` / `RenameDialog` UI.
- Fixed two state bugs found by the GUI smoke test: the title reporting stale dimensions or a false
  "unreadable" during load, and a late-failing decode clobbering a newer image.
- Wrote the Inno Setup script, `build-portable.ps1`, `build-installer.ps1`, and this document.

### 2026-08-13 — Phases 1–2 and icon
- Skeleton, WIC decoder, single-instance pipe, view transform, folder scanner with natural sort.
- Speed layer: LRU cache, prefetcher, preview-first decode, wheel coalescing, R2R publish.
- **Established that the 300 ms cold-start budget was unachievable** by measuring a minimal WPF app
  at ~945 ms. Budget revised; see the table above.
- Generated the app icon.

---

## Pending / not done

- **No git history.** `git init` and an initial commit would be worthwhile.
- **Installer never compiled** — Inno Setup is not installed here. The `.iss` is written and its
  association list is test-verified, but the setup executable has not been produced or run.
- **Portable zip built and verified; installer not.**
- **NativeAOT launcher stub** for a ~20 ms handoff: designed, deliberately not built.
- **Settings are not persisted.** `_slideshowSeconds`, the info/filmstrip toggles and window size
  reset each launch. `Settings/AppSettings.cs` was planned and not written.
- **No colour-management pass.** WIC applies embedded profiles by default; wide-gamut behaviour has
  not been checked, which may matter for published review images.
- **HEIC/WebP/RAW rely on this machine's installed Windows codecs.** Tier 2 covers WebP on a clean
  machine; HEIC and RAW would fall through to Magick.NET, which is untested for those here.
