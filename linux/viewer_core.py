"""CPU-only Linux image decoding/view geometry. Source files are read-only."""
from dataclasses import dataclass
import io
import math
import os
from pathlib import Path
import re
import tempfile
import threading
import warnings
from PIL import Image, ImageCms, ImageOps

# Ubuntu 22.04 ships Pillow 9.0; the named enum groups appeared in 9.1.
TRANSPOSE = getattr(Image, 'Transpose', Image)
TRANSFORM = getattr(Image, 'Transform', Image)
RESAMPLING = getattr(Image, 'Resampling', Image)

# No EPS/WMF/SVG/external-helper fallback. Actual Pillow build support still varies.
FORMATS = {'JPEG', 'PNG', 'GIF', 'BMP', 'DIB', 'TIFF', 'WEBP', 'ICO', 'PPM', 'TGA', 'PCX', 'QOI', 'JPEG2000', 'DDS'}


class Superseded(RuntimeError):
    pass


@dataclass(frozen=True)
class Ticket:
    generation: int
    path: str
    frame: int


class GenerationGate:
    def __init__(self):
        self.lock = threading.Lock()
        self.current = Ticket(0, '', 0)

    def request(self, path, frame=0):
        with self.lock:
            self.current = Ticket(self.current.generation + 1, str(Path(path).resolve()), frame)
            return self.current

    def accepts(self, ticket):
        with self.lock:
            return ticket == self.current

    def latest(self):
        with self.lock:
            return self.current


@dataclass
class Decoded:
    ticket: Ticket
    image: Image.Image
    format: str
    frames: int
    duration_ms: int
    animated: bool
    warning: str = ''


def normalize(frame):
    """Apply EXIF orientation and ICC once while preserving alpha/CMYK semantics."""
    frame = ImageOps.exif_transpose(frame)
    alpha = (frame.convert('RGBA').getchannel('A') if 'A' in frame.getbands() or 'transparency' in frame.info
             else Image.new('L', frame.size, 255))
    warning = ''
    profile = frame.info.get('icc_profile')
    if profile:
        try:
            source = ImageCms.ImageCmsProfile(io.BytesIO(profile))
            destination = ImageCms.createProfile('sRGB')
            # Never preconvert CMYK to RGB before applying its CMYK profile.
            cms_input = frame if frame.mode in ('RGB', 'CMYK', 'LAB', 'L') else frame.convert('RGB')
            output = ImageCms.profileToProfile(cms_input, source, destination, outputMode='RGB').convert('RGBA')
        except (OSError, ValueError, TypeError, ImageCms.PyCMSError):
            output = frame.convert('RGBA')
            warning = 'Invalid or unsupported colour profile; displayed with sRGB fallback.'
    else:
        if frame.mode == 'LAB':
            output = ImageCms.profileToProfile(frame, ImageCms.createProfile('LAB'), ImageCms.createProfile('sRGB'), outputMode='RGB').convert('RGBA')
        else:
            output = frame.convert('RGBA')
    output.putalpha(alpha)
    output.info.clear()
    output.info['icc_profile'] = ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes()
    return output, warning


def decode(ticket, gate=None):
    if gate and not gate.accepts(ticket):
        raise Superseded()
    path = Path(ticket.path)
    before = path.stat()
    with warnings.catch_warnings():
        warnings.simplefilter('error', Image.DecompressionBombWarning)
        with Image.open(path) as source:
            if source.format not in FORMATS:
                raise ValueError(f'{source.format or "Unknown"} is not supported by this Linux port.')
            frames = getattr(source, 'n_frames', 1)
            if not 0 <= ticket.frame < frames:
                raise ValueError('Image frame/page is out of range.')
            source.seek(ticket.frame)
            duration = max(20, min(60000, int(source.info.get('duration', 100) or 100)))
            frame = source.copy()
            normalized, warning = normalize(frame)
            fmt = source.format
    after = path.stat()
    if (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns) != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns):
        raise ValueError('The image changed while opening. Open it again.')
    if gate and not gate.accepts(ticket):
        raise Superseded()
    return Decoded(ticket, normalized, fmt, frames, duration, fmt in ('GIF', 'WEBP') and frames > 1, warning)


def is_supported(path):
    try:
        with warnings.catch_warnings():
            warnings.simplefilter('error', Image.DecompressionBombWarning)
            with Image.open(path) as image:
                return image.format in FORMATS
    except (OSError, ValueError, Image.DecompressionBombError, Image.DecompressionBombWarning):
        return False


def natural_key(path):
    return tuple((1, int(part)) if part.isdigit() else (0, part.casefold()) for part in re.split(r'(\d+)', Path(path).name))


def scan_folder(folder, cancelled=lambda: False):
    found = []
    for path in Path(folder).iterdir():
        if cancelled():
            raise Superseded()
        if path.is_file() and not path.name.startswith('.') and is_supported(path):
            found.append(str(path.resolve()))
    return sorted(found, key=lambda p: (natural_key(p), p))


@dataclass
class View:
    width: int = 1000
    height: int = 700
    zoom: float = 1
    pan_x: float = 0
    pan_y: float = 0
    mode: str = 'fit'

    def fit(self, image_size):
        self.mode = 'fit'
        self.zoom = min(1, max(1, self.width) / image_size[0], max(1, self.height) / image_size[1])
        self.pan_x = self.pan_y = 0

    def actual(self):
        self.mode, self.zoom, self.pan_x, self.pan_y = 'actual', 1, 0, 0

    def origin(self, image_size):
        position = ((self.width - image_size[0] * self.zoom) / 2 + self.pan_x,
                    (self.height - image_size[1] * self.zoom) / 2 + self.pan_y)
        # Fit-small/1:1 aligns image pixels to device raster pixels, avoiding
        # a half-pixel blur and an extra fringe row for odd-sized images.
        return tuple(round(value) for value in position) if self.zoom == 1 and self.mode in ('fit', 'actual') else position

    def zoom_at(self, factor, cursor, image_size):
        if not math.isfinite(factor) or factor <= 0:
            raise ValueError('Invalid zoom factor')
        old = self.zoom
        new = min(64, max(0.00001, old * factor))
        origin_x, origin_y = self.origin(image_size)
        world_x, world_y = (cursor[0] - origin_x) / old, (cursor[1] - origin_y) / old
        self.zoom, self.mode = new, 'free'
        self.pan_x = cursor[0] - world_x * new - (self.width - image_size[0] * new) / 2
        self.pan_y = cursor[1] - world_y * new - (self.height - image_size[1] * new) / 2


def rotate_view(image, quarter_turns):
    turns = quarter_turns % 4
    return image if not turns else image.transpose({1: TRANSPOSE.ROTATE_270, 2: TRANSPOSE.ROTATE_180, 3: TRANSPOSE.ROTATE_90}[turns])


def render_tile(image, view):
    """Bounded viewport raster from full-resolution pixels; never giant zoom bitmap."""
    ox, oy = view.origin(image.size)
    left, top = max(0, math.floor(ox)), max(0, math.floor(oy))
    right = min(view.width, math.ceil(ox + image.width * view.zoom))
    bottom = min(view.height, math.ceil(oy + image.height * view.zoom))
    if right <= left or bottom <= top:
        return None
    extent = ((left - ox) / view.zoom, (top - oy) / view.zoom,
              (right - ox) / view.zoom, (bottom - oy) / view.zoom)
    tile = image.transform((right - left, bottom - top), TRANSFORM.EXTENT, extent,
                           resample=RESAMPLING.BICUBIC)
    return tile, (left, top)


def save_png_copy(source_path, displayed_image, destination):
    """Explicit lossless PNG copy, atomically published without replacing any file."""
    source, target = Path(source_path).resolve(), Path(destination).absolute()
    if target.resolve() == source or target.suffix.lower() != '.png':
        raise ValueError('Choose a new PNG copy path; originals are never overwritten.')
    fd, temporary = tempfile.mkstemp(prefix='.image-viewer-', suffix='.png', dir=target.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            displayed_image.save(stream, format='PNG', icc_profile=displayed_image.info.get('icc_profile'))
            stream.flush()
            os.fsync(stream.fileno())
        # link is exclusive; a concurrent file/symlink at target causes failure.
        os.link(temporary, target)
    finally:
        os.unlink(temporary)
    return target
