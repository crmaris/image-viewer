# Image Viewer for Linux

The native Linux companion uses Python 3.10+, Tk and Pillow. It browses a folder,
zooms from full-resolution pixels, rotates the view, plays GIF/WebP animations,
opens TIFF pages, and saves an explicit rendered PNG copy. Image originals are
never overwritten. Linux uses separate packages and launchers; the Windows
application and its updater continue to use the Windows releases.

## Install on Ubuntu or Debian

Download `image-viewer-linux_0.2.6_all.deb` from the Linux release, then run:

```sh
sudo apt install ./image-viewer-linux_0.2.6_all.deb
imageviewer-linux
```

The package installs `imageviewer-linux`, `imageviewer-linux-cli`, an application
menu entry, and Open With registration. It does not change default MIME handlers.
Python, Tk and Pillow dependencies are supplied by your distribution. This package
contains no compiled CPU-specific code; the same package can be used on x86-64
and ARM64 systems with those dependencies. Tested configurations are recorded
in the release notes and project handover. Remove with `sudo apt remove image-viewer-linux`.

## Portable archive or source checkout

On Ubuntu/Debian, install dependencies once:

```sh
sudo apt install python3-tk python3-pil python3-pil.imagetk
```

Extract `ImageViewer-0.2.6-linux.tar.gz` and run `./imageviewer-linux` from the
extracted folder. To open a file, pass its path as the first argument:

```sh
./imageviewer-linux /path/to/photo.jpg
```

From a source checkout, use `python3 linux/viewer.py`. Other distributions need
matching Python Tk and Pillow packages, including ImageTk and ImageCms. The GUI
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

Portable copies check after startup and hourly while running. A verified archive
replaces the application folder atomically, with the previous folder retained
alongside it. The running viewer continues; the next launch uses the update.
Keep your photos outside the portable application folder: unknown user files
cause an update to be deferred rather than moved. Source checkouts never update
themselves. Offline checks fail quietly and retry on the next interval.

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

Content is identified rather than trusting the extension. Allowed Pillow decoders
cover JPEG, PNG, GIF, BMP, DIB, TIFF, WebP, ICO, PPM/PGM/PNM, TGA, PCX, QOI,
JPEG 2000 and DDS. Availability depends on the distribution's Pillow build; QOI
is absent from Ubuntu 22.04's Pillow 9.0.1. SVG, EXR, JXR, HEIC, JPEG-XL, RAW
and other Windows fallback formats are not supported. EPS/WMF and external-helper
decoders are rejected. Unsupported/corrupt input produces a visible error.

EXIF orientation and embedded ICC-to-sRGB conversion are applied once per decoded
frame. Alpha is preserved, and CMYK is transformed through its embedded profile.
Invalid profiles produce a visible warning and an sRGB fallback. Tk receives
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
sh linux/checks.sh
```

Outputs: `.deb`, portable `.tar.gz`, and `SHA256SUMS` in `build/linux`. The builder
accepts `--output` and `--version`; staging stays inside the project and is removed
when the build finishes.

Core fixtures verify source preservation, colour handling, frame/page decoding,
natural ordering, cursor anchoring, extreme zoom and exclusive copy saving. GUI
checks run the actual Tk app in an isolated X11 display and exercise browsing,
zoom, pan, fullscreen, animation, copy saving, compact layout and stale result
rejection. They do not prove every physical desktop, compositor, scale or distro.
The GUI checks require test-only `xvfb`, `xauth`, and `openbox` packages; they are
not application dependencies. Openbox runs only inside the isolated test display.

Decoder publication checks generation, path and frame before/after decoding and
again on the Tk thread. Render generations reject stale tiles and errors. Folder
scanning runs separately after the first image appears. A bounded viewport tile
comes from full decoded pixels, never a giant bitmap at 64x zoom. Source changes
during decoding abort publication.

MIT; see the included LICENSE or the repository's root license.
