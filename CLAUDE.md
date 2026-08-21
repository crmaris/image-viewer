# Image Viewer — handover

**Canonical handover document.** `AGENTS.md` is a thin pointer to this file; keep the content here.

A fast, plain Windows image viewer. Opens essentially any image format, starts as quickly as WPF
allows, and walks a folder with **Space** or the **mouse wheel**. Built 2026-08-13/14.

- **Stack:** C# / .NET 10 (`net10.0-windows`), WPF, x64.
- **Version:** 0.2.0 (released and installed all-users at `C:\Program Files\Image Viewer`,
  with the CLI on the machine PATH)
- **Repo layout:** `src/ImageViewer` (app), `tests/ImageViewer.SelfTest` (checks + benchmarks),
  `packaging` (icon generator, publish scripts, Inno Setup script).
- **Public repo:** <https://github.com/crmaris/image-viewer> (MIT). `main` is the default branch.

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

218 checks currently pass. **Inno Setup 6 is installed** and `build-installer.ps1` produces a
58.5 MB installer locally in about 40 seconds.

It was long recorded here as "not installed", which was wrong: winget puts Inno Setup under
`%LOCALAPPDATA%\Programs\Inno Setup 6`, and the script only looked in the two Program Files
locations. The project carried that as an environment fact for a week when it was a one-line
bug in the search list.

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

Measured on this machine 2026-08-21 with `imageviewer info`, which reports the tier that answered:

| Tier | Answers for |
|---|---|
| WIC | png jpg gif bmp tif **webp** — the WebP and JPEG-XL codecs and the Microsoft Raw Image Decoder are all present here, so RAW does *not* fall through |
| ImageSharp | pgm ppm tga |
| Svg.Skia | svg svgz |
| Magick.NET | exr hdr jp2 pcx psd qoi |

**ImageSharp is pinned to 2.1.13 deliberately.** Version 3.0 moved from Apache-2.0 to the Six Labors
Split License; building against 4.x emits a "no license found" warning and needs a commercial
licence for most use. 2.1.13 is the last Apache-2.0 release and covers everything this tier needs.
**Do not "upgrade" it** without a licensing decision.

---

## Colour management — do NOT "add" it, it is already there

**Measured 2026-08-14 through the viewer's own decode path.** The obvious change here — wrap the
decode in a `ColorConvertedBitmap` to sRGB — is a **regression**, not a fix.

WIC already applies an embedded ICC profile by itself whenever it decodes a frame into a straight
RGB format. Numbers, for a colour inside AdobeRGB but outside sRGB:

| | stored in file | reaches the app | after a further "fix" |
|---|---|---|---|
| AdobeRGB JPEG | (100,181,89) | **(0,183,81)** — already converted | (0,185,71) — **double-converted** |
| AdobeRGB TIFF | (100,180,90) | **(0,182,82)** — already converted | (0,184,72) — **double-converted** |
| sRGB JPEG | (100,181,89) | (100,181,89) — untouched | (100,181,89) — no-op |

The red channel clipping to 0 is the giveaway that a real gamut conversion happened. Converting a
second time oversaturates every photograph, which is what the third column is.

**The one genuine gap is palettised frames.** When WIC keeps a frame in its native indexed format
there is no format conversion, so there is no colour transform either, and an AdobeRGB PNG-8 arrives
untouched at (100,180,90) and renders as though it were sRGB. `WicDecoder.ConvertIndexedToSrgb`
fixes exactly that case and nothing else, gated on `BitmapSource.Palette is not null` so it costs a
reference comparison on every other image. Note `ColorConvertedBitmap` **rejects an indexed source**
("Pixel format not supported"), hence the `FormatConvertedBitmap` to Bgra32 first.

Not done, deliberately: **no transform to the monitor's own ICC profile.** Everything is normalised
to sRGB, which is right for the overwhelming majority of displays but not for a wide-gamut one —
on such a monitor images will render slightly oversaturated, as they do in every non-managed
application. Doing it properly needs the display profile, re-conversion when the window moves
between monitors, and a way to keep it off the fast path; none of that is justified yet.

There is deliberately **no colour-management setting**. WIC's behaviour is not ours to switch off,
so a toggle by that name could only control the palettised correction — a name promising far more
than it delivered.

## Command line

`Cli/` holds a full command-line interface over the same engine the window uses: `info`, `identify`,
`list`, `formats`, `convert`, `resize`, `thumb`, `rotate`, `flip`, `version`, `help`. Exit codes are
0 / 1 / 2 for success / failure / bad usage.

- **The commands are a second face on the existing engine, not a parallel implementation.**
  `rotate` and `flip` call `ImageSaver`, so a JPEG rotated from a script is exactly as lossless as
  one rotated with Ctrl+S. `list` calls `FolderScanner`, so the printed order is the order Space
  walks. `info` calls `DecoderChain` and reports which tier answered.
- **`ConsoleHost.Prepare` is what makes any of it visible.** This is a `WinExe`, which is why no
  console flashes when an image is double-clicked, and it therefore starts with no console at all:
  `Console.WriteLine` goes nowhere. It attaches to the parent process's console and then **rebinds
  `Console.Out`** — .NET caches the first resolution, which in a windowed process is a null writer,
  so attaching without rebinding still prints nothing.
- **A shell does not wait for a `WinExe`,** so the prompt returns before the output appears.
  Cosmetic interactively, invisible when redirected. Fixing it properly needs a second, console-
  subsystem executable; not worth a second binary in the installer.
- **`CommandLine.IsCommand` runs before `SingleInstance.TryHandOff`.** A command must execute in
  *this* process and print to the console that invoked it. Handing it to an already-open window
  would produce no output and a meaningless exit code.
- **A bare word that is not a verb and is not on disk is treated as a mistyped command**, not a
  file. Otherwise `imageviewer conver a.jpg b.png` opens a window saying "conver" was not found —
  useless interactively and silent from a script. The cheap tests come first so a real launch, whose
  path contains a colon, never pays for a disk probe.
- **`--` forces everything after it to be a path**, for a file genuinely called `info`.
- `Arguments` binds `--flag value` in a second pass, because only the list of value-taking flags can
  tell that pattern from a boolean flag followed by a file name. Getting this wrong silently eats
  the first file after every boolean flag.
- **Unknown options are rejected rather than ignored.** A mistyped `--quality` would otherwise write
  a whole batch at the default and give no hint it had happened.
- `ImageWriter` uses WPF's encoders for the common targets and Magick.NET for anything else, behind
  `NoInlining` for the same reason the decoder tiers do. Alpha is composited onto white for JPEG and
  GIF, which cannot carry it.
- `InternalsVisibleTo` in the csproj lets the self-test drive the parser directly.

### PATH, and the flag that would delete it

The installer offers an **unchecked** `addtopath` task. It is written declaratively with `{olddata}`
and `preservestringtype` rather than read-modify-write, because Inno's `RegQueryStringValue` returns
a REG_EXPAND_SZ value already expanded — writing it back would bake `%SystemRoot%` into a literal
path and quietly change what the environment means.

**Never put `uninsdeletevalue` on a PATH entry.** It does not remove what was added; it deletes the
entire `Path` value on uninstall. Removal is done in `RemoveFromPath`, which strips only the exact
segment and **refuses to act at all if the value still contains a `%`** — leaving one stale entry is
much better than destroying the indirection. The self-test asserts all of this, because Inno Setup
is not installed here and CI only compiles the script rather than running it.

## Settings

`Settings/AppSettings.cs`, stored as flat `key=value` text at `%APPDATA%\ImageViewer\settings.txt`.
Persists window bounds, maximised/fullscreen state, slideshow interval, the info and filmstrip
toggles, and which executable path the "Open with" registration was last written for.

- **Not JSON.** `JsonSerializer`'s first call builds reflection metadata for the type — tens of
  milliseconds, against a cold start where an empty WPF window is already most of a second. Hand
  parsing measures **0.033 ms** per load and adds no assembly to the startup path. The self-test
  asserts it stays under 1 ms so nobody quietly swaps a serialiser back in.
- **Every number is written with `InvariantCulture`.** `InvariantGlobalization` is off in this
  project (Greek filenames), so on a decimal-comma locale a culture-sensitive writer would emit
  `7,5` and then fail to read it back. There is a check for this.
- **`RestoreBounds`, not `Left`/`Top`/`Width`/`Height`.** While maximised or fullscreen the latter
  describe the screen-filling rectangle, not the size to return to.
- **A saved position is validated against the current virtual desktop.** A window remembered on a
  monitor that has since been unplugged would otherwise restore into empty space — running,
  focusable, and impossible to drag back.
- Overlays are restored at `ApplicationIdle` **after** the first frame. Reopening the info panel is
  what first touches WPF's text stack (~150 ms); that must not land back on the startup path.
- `AppSettings.Load(path)`/`Save(path)` overloads exist so the self-test can use scratch files.
  `Environment.GetFolderPath` asks the shell for the real roaming folder and **ignores the `APPDATA`
  environment variable**, so redirecting it does nothing — a test written that way silently reads
  the developer's own settings.

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

## Auto-update

`Update/AppUpdateService.cs` polls the public repo's `releases/latest` endpoint, at most once a day
(timestamp in `%APPDATA%\ImageViewer\update-check.txt`). The check is scheduled from
`ContentRendered` on a 4-second idle-priority timer, so it is **never on the startup path**, and it
fails silently when offline. If a newer release exists the user gets a toast; **Ctrl+U** then asks
for confirmation before anything is downloaded or run.

Rules that must not be relaxed:

- **Download hosts are allow-listed** (`github.com`, `objects.githubusercontent.com`, and two
  siblings), HTTPS only. The updater fetches and then *executes* a file, so the destination is never
  taken on trust from the API response. The self-test proves lookalike domains, plain HTTP and
  `file://` are all rejected.
- **The downloaded size is checked** against the asset's declared size before the file is run; a
  partial download is deleted rather than executed.
- Prereleases are skipped. A release with no `*setup*.exe` asset opens the release page instead.
- `HttpClient` is `Lazy` — not just tidiness. It was originally an eager static initialiser declared
  *before* `CurrentVersion`, read a null version, and threw `TypeInitializationException`. Static
  fields initialise in declaration order.

`RepositoryOwner`/`RepositoryName` in `AppUpdateService` are the only place the repo is named.

### The install-mode trap (fixed 2026-08-14)

`LaunchInstaller` used to run the installer with **no arguments**. Setup is built
`PrivilegesRequired=lowest`, so left to itself it asks whether to install per-user or for all users
— and answering that wrong does not fail loudly, it installs a **second copy**. An all-users
installation in `C:\Program Files` would gain a per-user duplicate in `%LOCALAPPDATA%` while the
original stayed put, still registered, still owning every file association.

It now reads Inno Setup's own uninstall key to decide (`DetectInstallMode`: present in HKLM →
all-users, HKCU → per-user, absent → portable, let Setup ask) and passes the matching
`/ALLUSERS` or `/CURRENTUSER`. Registry, not the executable's path — this application has already
been installed once into a folder that did not match its registration.

**That switch is inert without `PrivilegesRequiredOverridesAllowed=commandline dialog` in the
`.iss`.** With only `dialog`, Inno silently ignores `/ALLUSERS` and `/CURRENTUSER`. The self-test
checks the directive, because the C# side would otherwise pass all its own tests and still do
nothing. (This also explains the earlier silent install: the `/ALLUSERS` passed by hand was almost
certainly ignored, and the install reached HKLM because the process was already elevated.)

`LaunchInstaller` now also returns the started `Process`, throws `FileNotFoundException` for a
missing file, and converts a declined UAC prompt (`Win32Exception` 1223) into
`OperationCanceledException` — which `MainWindow` catches separately so the window is **not** closed
out from under a user who said no. `Close()` only happens after the launch has actually succeeded.

**Both assumptions behind that design were verified for real on 2026-08-21**, using a throwaway
per-user install in a non-default folder so nothing on this machine was at risk:

1. *The mode switch is honoured.* Setup run with `/CURRENTUSER` logged
   `Administrative install mode: No`, which is only possible because
   `PrivilegesRequiredOverridesAllowed` includes `commandline`. With `dialog` alone the switch is
   discarded in silence.
2. *Omitting `/DIR` is correct.* Setup was re-run with the mode switch **and nothing else**, exactly
   what `LaunchInstaller` passes. It found the existing non-default directory from its own AppId,
   upgraded in place — a sentinel file dropped there beforehand survived — and did **not** fork a
   second copy into the default `%LOCALAPPDATA%\Programs\Image Viewer`. It also logged
   `Detected previous administrative 64-bit install? Yes`, so it is aware of the other copy and
   still respected the mode it was told.

What that leaves untested is only `Process.Start` handing those arguments over from a live Ctrl+U,
and the argument construction itself is covered by the suite. The unknowable part is closed.

**Releasing:** push a tag and `.github/workflows/release.yml` does the rest — it stamps the version
into the csproj (so the shipped binary reports the same number the updater compares), runs the full
self-test *and* the assembly-load check, builds the portable zip and the installer, and attaches
both to the release.

```bash
git tag v0.2.0 && git push origin v0.2.0
```

The workflow ran clean on its first attempt for v0.1.0 (2m19s), including the Chocolatey install of
Inno Setup and the installer build.

### Verifying the updater against a real release

`--check-update` talks to the live API. Build as an artificially old version so there is something
to discover, and add `--download` to exercise the size verification and partial-file handling:

```bash
dotnet run --project tests/ImageViewer.SelfTest -p:Version=0.0.1 -- --check-update --download
```

Kept out of the normal suite deliberately: a test that fails when the network drops is worse than no
test. Note that running it writes the throttle timestamp, so the app will skip its next daily check.

---

## Package size

The portable build is ~180 MB (80 MB zipped). It was 317 MB before two fixes, both in the csproj:

- **Native symbol files are deleted after publish.** SkiaSharp and HarfBuzz ship `.pdb` files for
  their native libraries and the SDK copies them into the output — 84 MB and 22 MB respectively, for
  files never read at runtime.
- **ReadyToRun is applied only to the application.** Precompiling the fallback decoders added ~25 MB
  (ImageSharp alone went 2 MB → 30 MB) to speed up code that by design only runs for exotic files
  already on the slow path. Excluding them also measurably *improved* startup, since there is less
  to load.

---

## Windows shell integration — hard-won, read before touching associations

Getting an app into Windows 11's "Open with" involves **four separate mechanisms**. Having some of
them right and the rest missing produces an app that is correctly registered and still invisible,
which is exactly what happened here across several rounds of "fixed it" / "no it isn't".

| Key | What it actually controls |
|---|---|
| `Classes\<ext>\OpenWithProgids` | Puts the ProgID in the **Choose another app** dialog |
| `Classes\Applications\<exe>\SupportedTypes` | The shell reads this when building the Open With list |
| `Software\<App>\Capabilities` + `RegisteredApplications` | Lists the app **by name in Settings → Default apps** |
| `Explorer\FileExts\<ext>\OpenWithList` | The **compact flyout** — an ordinary per-user MRU |
| `Explorer\FileExts\<ext>\UserChoice` | The **default handler**. Hash-protected; user only |

Things that cost real time to learn:

- **`SHAssocEnumHandlers` reads a different list from the compact flyout.** It returned "Image
  Viewer, recommended" the whole time the flyout showed nothing. Verifying with it proves the
  association layer is sound; it does *not* prove the user will see the app. To check the flyout,
  read `FileExts\<ext>\OpenWithList`.
- **`OpenWithList` is a plain MRU and is safe to write** — it is exactly what Windows writes when
  you pick an app manually. Adding `ImageViewer.exe` and putting its letter first in `MRUList` puts
  the app at the top of the flyout. It survives an Explorer restart.
- **`UserChoice` must never be written.** It carries a validated hash; forging it makes Windows
  discard the association entirely. `IAssocHandler::MakeDefault` returns **S_OK and does nothing**
  on Windows 11 — measured. Setting the default is reserved to the user, permanently.
- **`ChangesAssociations=yes` is required** in `[Setup]`, or Inno Setup never calls
  `SHChangeNotify(SHCNE_ASSOCCHANGED)` and Explorer serves stale cached associations.
- An **explorer.exe restart** is the reliable way to make new registrations show up.

### The app registers itself now (2026-08-14)

`Files/OpenWithRegistration.cs` writes the `OpenWithList` MRU on first run, from
`MainWindow.RegisterWithShellOnce`, deferred to `ApplicationIdle` so it is never on the startup path.

**It lives in the application rather than the installer on purpose.** The list is per-user, and an
all-users install runs elevated — an installer writing `HKCU` would populate the *administrator's*
hive, not the hive of whoever ends up using the viewer. Doing it in the app also covers the portable
build, which has no installer at all.

- **Appends to the MRU, does not promote to the front.** Being absent was the bug; jumping ahead of
  an application the user has actually been choosing — a RAW editor for `.cr2`, say — would be
  presumptuous. On an extension with no list yet (the common case) appending still makes the viewer
  the first entry. Note this differs from the hand-written script used on this machine, which *did*
  promote to front.
- **Keyed on the executable path** (`openWithRegisteredFor` in settings), so it re-runs if the app
  moves or is reinstalled elsewhere, but never nags if the user removes the entry.
- **The application/ProgID keys are only written when nothing usable is already registered**
  (`HasWorkingRegistration`, read through `HKEY_CLASSES_ROOT` so it sees both hives). This is not
  tidiness: `HKCU\Software\Classes` **shadows** HKLM in the merged view, so writing them
  unconditionally makes whichever copy ran last hijack every association — during development, a
  build sitting in `bin\Debug`. Verified after this guard: launching the debug build left
  `HKCU\...\ImageViewer.Image` absent and `.jpg` still resolving to `C:\Program Files`.
- `UserChoice` is still never touched.

Ruled out by measurement, do not re-investigate: HKLM/HKCR shadowing, `NoOpenWith` policies,
packaged-app precedence, `%LOCALAPPDATA%` being deprioritised (VS Code lives there and appears
fine), and unsigned-binary gating (Smart App Control is off).

### Silent-install trap

`/DIR=C:\Program Files\Image Viewer` passed as an **array element** to `Start-Process` splits at the
space and Inno Setup receives `/DIR=C:\Program` — which silently installs 183 MB into `C:\Program\`
and returns exit code 0. Pass the whole command line as a **single quoted string**:

```powershell
$args = '/VERYSILENT /ALLUSERS /DIR="C:\Program Files\Image Viewer" /TASKS=associate'
```

A stray `C:\Program` folder is worth cleaning up beyond this app: it breaks other installers that
reference `C:\Program Files` unquoted.

### When `dotnet build` hangs forever, check TEMP first

Cost most of a session on 2026-08-14. Every build hung indefinitely — not slowly, *indefinitely* —
including `dotnet new console` + `dotnet build` on a throwaway project on a different drive, so it
was clearly machine-wide rather than this repository. `dotnet --version` answered in 201 ms, restore
reported "all projects are up-to-date", and then nothing.

**The cause was an inaccessible default `TEMP` directory.** MSBuild writes to `%TEMP%\MSBuildTemp`
and stalls there with no diagnostic. Redirecting it makes builds complete in ~4 seconds:

```powershell
$env:TEMP = 'E:\All projects\Image Viewer\src\ImageViewer\obj\codex-temp'
$env:TMP  = $env:TEMP
dotnet build src/ImageViewer/ImageViewer.csproj --no-restore --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false
```

The 28 idle `dotnet.exe ... /nodemode:1` worker processes visible at the time were a **symptom**,
not the cause — an early reading blamed them and was wrong. Nothing needed killing. `obj/` is
gitignored, so a temp directory under it is a safe target.

### Launching the app to test it hijacks whatever session is already open

The single-instance pipe means a test launch does not start a new process: it hands the path to the
running instance, which then jumps to that image and pulls itself to the front. During this
session that interrupted the owner mid-folder, at `IMG_9418.PNG [333/411]`, and made a first-run
registration test silently measure nothing at all — the launched process had exited immediately
without ever reaching `RegisterWithShellOnce`.

**Check for a running `ImageViewer.exe` before any GUI test, and read its window title.** CLI
commands are safe: `CommandLine.IsCommand` short-circuits ahead of `SingleInstance.TryHandOff`, so
`imageviewer info ...` never touches an open window. That ordering is verified by the self-test.

### Benchmarking caveat

Startup measured 26–58 s at one point and ~1 s an hour later, on the same binaries. The machine was
running Codex, ChatGPT and a dozen agent processes at 46–60% CPU with 3.7 GB free of 31 GB. **Check
system load before trusting any startup number**, and re-measure when the machine is quiet.

---

## Session log

### 2026-08-21 — the install-mode design verified against real Setup runs
Proved the two assumptions `LaunchInstaller` rests on, without waiting for a release after v0.2.0
and without touching the real installation. Full detail in "The install-mode trap" above.

- Installed per-user into `...\Temp\iv modetest` — a **non-default** folder, which is what makes
  the test meaningful — then re-ran Setup with `/CURRENTUSER` and nothing else.
- It upgraded in place (a sentinel file planted beforehand survived), did not fork into
  `%LOCALAPPDATA%\Programs`, and logged `Administrative install mode: No`, proving the
  `commandline` override is live.
- Uninstalling removed everything Setup had installed and correctly left the sentinel alone; the
  folder and the HKCU uninstall key both went. The real all-users 0.2.0 install, its associations
  and its PATH entry were all confirmed untouched afterwards.

### 2026-08-21 — v0.2.0 released and installed; Open With finally observed
Cut v0.2.0, installed it over the v0.1.1 all-users install, and closed three more pending items.

- **The version-stamping fix is proven.** v0.1.1 shipped an asset named `ImageViewer-0.1.0-setup.exe`
  whose Add/Remove entry read 0.1.0 while the binary reported 0.1.1. v0.2.0 ships
  `ImageViewer-0.2.0-setup.exe`, and Add/Remove and the binary now agree at 0.2.0.
- **The live updater was verified against the real release**: `--check-update --download` found
  v0.2.0, passed the host allow-list, and downloaded 61,308,793 bytes matching the declared size,
  confirmed to be a real PE rather than an error page.
- **The Open With first-run registration has now been observed**, which had never happened before.
  With the viewer closed and the entries cleared: `.qoi` had no list at all and the app took slot
  `a` as the only entry; `.tga` already held three ACDSee/other entries and the app took the first
  free letter `d` and **appended** it, `acb` becoming `acbd`, leaving the existing order untouched.
  That is the documented intent, and it differs from the old hand-written script, which produced
  `dacb` — the viewer jumping to the front of somebody else's list.
- **The PATH task works on the machine hive too.** HKLM PATH went from 1284 to 1314 characters,
  exactly the 29-character folder plus one separator, type preserved, original prefix intact.
  `imageviewer` resolves to `C:\Program Files\Image Viewer\ImageViewer.exe` from a fresh shell.
- No duplicate install: no HKCU uninstall key, nothing under `%LOCALAPPDATA%\Programs`, and `.jpg`
  still resolves to the Program Files copy. The `HasWorkingRegistration` guard held — no HKCU
  ProgID shadow was written.

**The install-mode switch is still unproven, and this upgrade could not prove it.** The fix lives in
the v0.2.0 *client*, so it governs 0.2.0 onward; v0.1.1's updater passes no arguments. This upgrade
was therefore installed directly with `/ALLUSERS` rather than routed through Ctrl+U, which would
have shown the "all users / just me" dialog — the exact fork the fix exists to prevent.

### 2026-08-21 — closing out the pending list
Verified locally what had only ever been compiled in CI.

- **Inno Setup was installed all along.** `build-installer.ps1` searched only the two Program Files
  paths; winget installs per-user under `%LOCALAPPDATA%\Programs`. Added that path. The installer
  now builds locally, 58.5 MB in ~40 s.
- **The PATH task is verified end to end.** Installed per-user into a folder *with a space in it*,
  confirmed PATH grew by exactly one semicolon plus the folder, that the value type and the whole
  existing string were preserved, and that `imageviewer` then resolved from a fresh shell. Uninstall
  restored PATH **byte-identically**. One side effect found and documented: removal rewrites the
  value as REG_EXPAND_SZ, so a REG_SZ user PATH is normalised. Harmless, because the guard means we
  only write when the value holds no `%` at all.
- **The uninstall [Code] path ran for real**, including `RemoveFromPath`. Only the wizard's own UI
  clicks remain unexercised.
- **The decoder tiers were measured** rather than assumed; see the table above. WebP is handled by
  WIC here, and the Raw Image Decoder is installed.
- **The Open With clean-machine test could not be run** and was reverted. Clearing the `.qoi` and
  `.tga` MRU entries and launching the app measured nothing, because the launch handed off to the
  owner's live session; the registry entries were restored byte-for-byte from a backup. See the
  handoff caveat above.
- Note the installed copy on this machine is still **v0.1.1**, which predates settings, the colour
  fix, the shell registration and the CLI. None of that reaches the owner until a release is cut.

### 2026-08-14 — full command-line interface
Added `Cli/` (11 commands), `LaunchOptions` for `--fullscreen` / `--slideshow`, and an installer
task that puts the folder on PATH. 218 checks pass; the assembly-load invariant still holds and
cold start is unchanged at ~1.05-1.18 s.

- The section above records the traps: the console attach, the handoff ordering, the mistyped-verb
  rule, and the `uninsdeletevalue` flag that would delete the user's entire PATH.
- Verified end to end against the built executable: a full turn of `rotate --cw` grew `sample.jpg`
  by exactly the documented 36-byte EXIF header and then left it byte-identical, and two
  `flip --horizontal` runs returned it to the same hash.
- `thumb --embedded` originally returned the camera's preview at whatever size it was stored,
  ignoring `--size`. It now fits the requested box.
- Not tested here: the PATH task itself. Inno Setup is not installed on this machine, so CI compiles
  the script but nothing runs it. Watch the first real install that ticks the box.

### 2026-08-14 — the four pending items closed
Owner asked for all four outstanding items in one go. 170 checks pass; assembly-load invariant holds.

- **Colour management: measured, and the obvious fix was a regression.** WIC already converts
  embedded ICC profiles; adding a transform would double-convert every photo. Only palettised
  frames genuinely needed fixing. Full numbers in the section above. The measurement was delegated
  to Codex, which also caught two bugs in the probe itself — Q16 values passed to the Q8
  `MagickColor` API, and an "sRGB" control file that had silently lost its ICC profile, which would
  have made the sRGB-is-a-no-op conclusion meaningless.
- **Settings persist** (`Settings/AppSettings.cs`). Verified end to end: wrote 140,90 900×640 into
  the file, launched, window came up at exactly 140,90 900×640.
- **The app registers itself in the Open With flyout** on first run, with a guard that stops a
  `bin\Debug` build hijacking the installed copy's associations.
- **`LaunchInstaller` runs for real in the suite** (stub executable, verified pid and exit code),
  and the install-mode trap above was found and fixed while testing it.
- **`--probe-color` was a throwaway mode and has been removed**; its findings became permanent
  regression checks in `tests/ImageViewer.SelfTest/FeatureChecks.cs`.

### 2026-08-14 — v0.1.1, installed, shell integration fixed
- **Released v0.1.1** and installed it all-users to `C:\Program Files\Image Viewer`. The first
  install went per-user to `%LOCALAPPDATA%\Programs` without asking, which was the wrong default
  for a machine-wide tool.
- **Fixed Open With properly** — see the section above. `ChangesAssociations`, `SupportedTypes`,
  Capabilities/`RegisteredApplications` in the installer; `OpenWithList` MRU written directly on
  this machine. Double-clicking a JPEG now opens Image Viewer in ~1s, verified end to end.
- **Fixed release versioning**: the workflow stamped only the csproj, so v0.1.1 shipped an asset
  named `ImageViewer-0.1.0-setup.exe` reporting 0.1.0. It now stamps `ImageViewer.iss` too.
- **Added `.github/workflows/ci.yml`**, which compiles the installer on every push. The `.iss` now
  contains a Pascal `[Code]` section that nothing else validates.
- A six-agent audit confirmed nothing else was wrong; its conclusions are folded into the section
  above so the reasoning is not lost.

### 2026-08-14 — First release, updater verified live
- **Released v0.1.0.** The workflow passed on its first run (2m19s), producing
  `ImageViewer-0.1.0-setup.exe` (58.4 MB) and `ImageViewer-portable-win-x64.zip` (80.2 MB).
- **Fixed a corpus bug that would have broken CI immediately.** `--make-corpus` only generated the
  exotic formats; the everyday images came from throwaway scripts that were never committed, so the
  suite only passed on the machine it was written on. `Corpus.Generate` now produces all 31 files.
- **Verified the updater against the real release** with the new `--check-update --download` mode:
  found v0.1.0 in 1.26s, selected the right asset, and downloaded 61,271,768 bytes intact.

### 2026-08-14 — Auto-update, public repo, package size
- Added `AppUpdateService` + `UpdateInfo` and the Ctrl+U flow. Owner asked for auto-update mid-build.
- **Published to <https://github.com/crmaris/image-viewer> as a public MIT repo** at the owner's
  request. Added `README.md`, `LICENSE`, `.gitignore`, `.gitattributes` and the release workflow.
  Scanned for secrets before publishing; nothing sensitive is in the tree.
- Fixed a `TypeInitializationException` from static field ordering in `AppUpdateService`.
- **Cut the portable build from 317 MB to 180 MB** — see "Package size" above.
- Note: the repo uses **GitHub Releases directly**, not the `private-repo-autoupdate` pattern from
  the global skills. That pattern exists for apps whose *source* is private; this one is public, so
  the asset belongs on its own Releases page.

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

- **Ctrl+U has still never driven a real upgrade**, though the risky part of it is now proven.
  Setup's own behaviour under a bare mode switch was verified directly (see the install-mode
  section): it honours the switch and reuses the recorded install directory without `/DIR`. What has
  not happened is the v0.2.0 client invoking that from a live update, which is `Process.Start` with
  arguments the suite already checks. The first release after 0.2.0 will show it.
- **The interactive uninstall wizard is unexercised.** Its `[Code]` path, PATH removal included, has
  been run for real; only the clicking has not.
- **No transform to the monitor's ICC profile.** Everything normalises to sRGB. Correct for ordinary
  displays, slightly oversaturated on a wide-gamut one. See the colour-management section. Offered
  and declined 2026-08-21.
- **NativeAOT launcher stub** for a ~20 ms handoff: designed, deliberately not built. It would add a
  second executable to the installer and the release pipeline for a saving nobody has asked for.
  Offered and declined 2026-08-21.
- **HEIC is still untested here.** WebP and RAW are confirmed handled by WIC on this machine; no
  HEIC sample was available, and Magick.NET cannot write one without a libheif delegate.
