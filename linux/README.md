# Image Viewer for Linux

The native Linux companion uses Python 3.10+, Tk, Pillow and distribution decoder tools. It browses a folder,
zooms from full-resolution pixels, rotates the view, plays GIF/WebP animations,
opens TIFF pages, and saves an explicit rendered PNG copy. Image originals are
never overwritten. Linux uses separate packages and launchers; the Windows
application and its updater continue to use the Windows releases.

## Install on Ubuntu or Debian

Download `image-viewer-linux_0.2.7_all.deb` from the [Linux release](https://github.com/crmaris/image-viewer/releases/tag/linux-v0.2.7), then run:

```sh
sudo apt install ./image-viewer-linux_0.2.7_all.deb
imageviewer-linux
```

The package installs `imageviewer-linux`, `imageviewer-linux-cli`, an application
menu entry, and Open With registration. It does not change default MIME handlers.
Python, Tk, Pillow and recommended decoder tools are supplied by your distribution. This package
contains no compiled CPU-specific code; the same package can be used on x86-64
and ARM64 systems with those dependencies. Tested configurations are recorded
in the release notes and project handover. Remove with `sudo apt remove image-viewer-linux`.

## Portable archive or source checkout

On Ubuntu/Debian, install dependencies once:

```sh
sudo apt install python3-tk python3-pil python3-pil.imagetk imagemagick librsvg2-bin libraw-bin libjxr-tools
# JPEG XL, on Ubuntu 24.04+/Debian 12+:
sudo apt install libjxl-tools
# Ubuntu 24.04+ also packages HEIC decoding separately:
sudo apt install libheif-plugin-libde265
```

Extract `ImageViewer-0.2.7-linux.tar.gz` and run `./imageviewer-linux` from the
extracted folder. To open a file, pass its path as the first argument:

```sh
./imageviewer-linux /path/to/photo.jpg
```

From a source checkout, use `python3 linux/viewer.py`. Other distributions need
matching Python Tk and Pillow packages, including ImageTk and ImageCms, plus
matching ImageMagick, librsvg, LibRaw, jxrlib and JPEG XL tools. Helpers also need
`prlimit` from util-linux, normally installed with the operating system. The GUI
needs X11 or a desktop supporting XWayland. The CLI works without a display.

## Fully automatic updates

The Debian package enables `image-viewer-linux-update.timer` on systemd-based
systems. It checks hourly, downloads only immutable Linux releases from this
repository, verifies the size and GitHub SHA-256 digest, retains a reinstallable
copy of the current package, and installs the new package without confirmation.
Failures are recorded in the system journal; a failed installation attempts to
reinstall the rollback package. Check with:

```sh
systemctl status image-viewer-linux-update.timer
journalctl -u image-viewer-linux-update.service
```

The system timer also installs missing distribution decoder packages using APT's
normal signed repositories, without removing packages. This lets 0.2.6's dpkg-only
updater install 0.2.7 first; on the next timer run the new updater supplies the
extra codecs. A normal `apt install` of the new package supplies them immediately.
Offline/locked package-manager attempts fail and retry later. JPEG XL is installed
only when available in configured distribution repositories; repositories are
never changed to obtain a codec. Portable/source copies never request root access
or install system packages; install their dependencies once as shown above.

Portable copies check after startup and hourly while running. A verified archive
replaces the application folder atomically, with the previous folder retained
alongside it. The running viewer continues; the next launch uses the update.
Keep your photos outside the portable application folder: unknown user files
cause an update to be deferred rather than moved. Source checkouts never update
themselves. Offline checks fail quietly and retry on the next interval.

Portable automatic updates need a filesystem that enforces Linux ownership and
permissions for their private update state. Windows-mounted NTFS under WSL may
expose mode 0777 despite chmod; the updater refuses that state. Use a native Linux
filesystem (for example your Linux home directory) for automatic portable updates.

## Controls

| Input | Action |
|---|---|
| Space / Right / Page Down / wheel down | Next image |
| Left / Backspace / Page Up / wheel up | Previous image |
| Home / End | First / last image |
| Ctrl+wheel | Zoom at the cursor |
| Left-drag | Pan |
| F / 0 / Fit | Fit, without enlarging small images |
| 1 / 1:1 | One source pixel per Tk raster pixel |
| R / Rotate view | Rotate the displayed image clockwise |
| F11 | Toggle fullscreen |
| Escape | Leave fullscreen, otherwise close |
| Ctrl+O | Open a file |
| Ctrl+S | Save a new rendered PNG copy |
| Frame/page buttons | Browse GIF/WebP frames or TIFF pages |

Uncheck **Play animation** to pause. Manual frame selection also pauses playback.

## Formats and colour

The first Linux release used only a small Pillow decoder set. Version 0.2.7 adds
decoder layers while retaining the native GUI and automatic updates.

| Decoder | Formats |
|---|---|
| Pillow, in process | JPEG, PNG/APNG, GIF, BMP/DIB, TIFF, WebP, ICO, PPM/PGM/PNM, TGA, PCX, JPEG 2000, DDS, SGI, XBM/XPM, DCX, ICNS, MPO, Sun raster, MSP, FLI/FLC, FITS, Pixar, IM |
| Built-in QOI reader | QOI, including Ubuntu 22.04 with older Pillow |
| ImageMagick | HEIC/HEIF, AVIF, EXR, Radiance HDR, PSD/PSB composite, XCF first layer |
| librsvg | SVG/SVGZ, including transparency and internal references |
| LibRaw | Camera RAW supported by the installed LibRaw version: DNG, CR2/CR3/CRW, NEF/NRW, ARW/SRF/SR2, RAF, ORF, RW2, PEF, SRW and others |
| jxrlib | JPEG XR, rendered as 8-bit RGB; alpha is unavailable and a warning is shown |
| JPEG XL tools | JPEG XL on Ubuntu 24.04+/Debian 12+ with `libjxl-tools`; not packaged on Ubuntu 22.04 |

Content is sniffed for ordinary images and additional formats, so renamed images
still open. Some TIFF-based camera RAW containers also use their camera extension
as a decoder hint. Actual camera/model and compression support depends on your
distribution's decoder versions. The fixtures use synthetic DNG; they do not
qualify every camera. Extra codecs display the composite/first image (XCF's first
layer), not editable layers or HEIF/JXL animation.

Tools run only when a matching file is opened. Missing tools produce a specific
package-install message. Helpers use private input copies, fixed distribution
executable paths, and CPU/memory/output/time limits. ImageMagick uses explicit
raster coders with delegates/filters disabled. SVG is passed through stdin without
a base URL and rejects scripts, entities and external file/web references. SVG
must be UTF-8 and is limited to 8192 pixels per side. Other helper input/output is
limited to 256 MiB and decoded images to 40 megapixels. EPS, PostScript, PDF and WMF
implicit-helper paths remain excluded. Unsupported/corrupt/oversized input produces
a visible error.

EXIF orientation and embedded ICC-to-sRGB conversion are applied once per decoded
frame. Alpha is preserved where the selected decoder supplies it (JPEG XR is RGB
only), and CMYK is transformed through its embedded profile. Invalid Pillow
profiles produce a warning and sRGB fallback; failed helper colour conversions
produce an error. RAW uses camera white balance with no automatic exposure lift;
EXR/HDR displays a precision/clipping warning. Tk receives
sRGB pixels; monitor-profile conversion and colour parity with Windows WIC have
not been established. Decompression-bomb warnings are rejected.

Copy saving produces an 8-bit rendered sRGB PNG. It preserves neither RAW/HDR
precision nor all original metadata. View rotation never recompresses JPEGs.
Exclusive hard-link publication prevents overwriting an existing file or symlink,
including a collision during encoding. Filesystems without hard-link support
produce a safe save failure.

## Headless inspection

```sh
imageviewer-linux-cli photo.jpg
imageviewer-linux-cli photo.jpg --render preview.png --width 1000 --height 700
imageviewer-linux-cli pages.tif --frame 1 --rotate 90
```

For the portable/source version, use `python3 viewer_cli.py`. JSON output reports
detected format, normalized dimensions, frame count and colour warning. Optional
`--zoom` specifies raster pixels per image pixel. A viewport render contains image
pixels only, not a GUI screenshot. Copies never replace existing output. Failures
exit with code 1, and invalid arguments with code 2.

## Build and validate

On Linux, install `desktop-file-utils` and use your distribution's `dpkg-deb`:

```sh
python3 linux/build-package.py
python3 -B -m unittest discover -s linux -p test_viewer_core.py -v
python3 -B -m unittest discover -s linux -p test_formats.py -v
sh linux/checks.sh
```

Outputs: `.deb`, portable `.tar.gz`, and `SHA256SUMS` in `build/linux`. The builder
accepts `--output` and `--version`; staging stays inside the project and is removed
when the build finishes.

Core and format fixtures verify source preservation, colour handling, frame/page decoding,
natural ordering, cursor anchoring, extreme zoom and exclusive copy saving. GUI
checks run the actual Tk app in an isolated X11 display and exercise browsing,
zoom, pan, fullscreen, animation, copy saving, compact layout and stale result
rejection. They do not prove every physical desktop, compositor, scale or distro.
Format fixtures create real HEIC/AVIF, EXR/HDR, PSD/PSB, SVG/SVGZ, XCF, RAW DNG,
JPEG XR, QOI and JPEG XL files, and test cancellation and forbidden content.
JPEG XL's roundtrip is skipped on distributions without its encoder/decoder tools.
The GUI checks require test-only `xvfb`, `xauth`, and `openbox` packages; they are
not application dependencies. Openbox runs only inside the isolated test display.

Decoder publication checks generation, path and frame before/after decoding and
again on the Tk thread. Render generations reject stale tiles and errors. Folder
scanning runs separately after the first image appears. A bounded viewport tile
comes from full decoded pixels, never a giant bitmap at 64x zoom. Source changes
during decoding abort publication.

Application code: MIT; see the included LICENSE or repository root license.
Decoder tools/libraries are separate distribution packages under their own licenses
(ImageMagick, LGPL librsvg, LGPL/CDDL LibRaw, BSD jxrlib and BSD JPEG XL).
