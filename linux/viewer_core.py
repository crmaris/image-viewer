"""CPU-only Linux image decoding/view geometry. Source files are read-only."""
from dataclasses import dataclass
import io
import gzip
import math
import os
from pathlib import Path
import re
import signal
import subprocess
import tempfile
import threading
import time
import warnings
import xml.etree.ElementTree as ET
from PIL import Image, ImageCms, ImageOps

# Ubuntu 22.04 ships Pillow 9.0; the named enum groups appeared in 9.1.
TRANSPOSE = getattr(Image, 'Transpose', Image)
TRANSFORM = getattr(Image, 'Transform', Image)
RESAMPLING = getattr(Image, 'Resampling', Image)

# Only in-process Pillow plugins; EPS/WMF and their implicit helpers stay excluded.
FORMATS = {'JPEG', 'PNG', 'GIF', 'BMP', 'DIB', 'TIFF', 'WEBP', 'ICO', 'PPM', 'TGA', 'PCX',
           'QOI', 'JPEG2000', 'DDS', 'PSD', 'SGI', 'XBM', 'XPM', 'DCX', 'ICNS', 'MPO',
           'SUN', 'MSP', 'FLI', 'FITS', 'PIXAR', 'IM'}
RAW_EXTENSIONS = {'.dng', '.cr2', '.cr3', '.crw', '.nef', '.nrw', '.arw', '.srf', '.sr2',
                  '.raf', '.orf', '.rw2', '.pef', '.srw', '.3fr', '.erf', '.kdc', '.dcr', '.mos', '.mrw'}
MAX_INPUT_BYTES = 256 * 1024 * 1024
MAX_HELPER_PIXELS = 40_000_000
HELPER_SECONDS = 30


def pillow_formats():
    Image.init()
    return [fmt for fmt in Image.ID if fmt in FORMATS and fmt in Image.OPEN]


def extra_format(path):
    """Sniff additional formats without invoking an interpreter or external tool."""
    path = Path(path)
    with path.open('rb') as stream:
        head = stream.read(4096)
    if head.startswith(b'\x1f\x8b'):
        try:
            with gzip.open(path, 'rb') as stream:
                if re.search(rb'<(?:[\w.-]+:)?svg(?:\s|>)', stream.read(4096)):
                    return 'SVGZ'
        except (OSError, EOFError):
            return None
    if head.startswith(b'\x76\x2f\x31\x01'):
        return 'EXR'
    if head.startswith((b'#?RADIANCE', b'#?RGBE')):
        return 'HDR'
    if head.startswith(b'gimp xcf '):
        return 'XCF'
    if head.startswith(b'8BPS\x00\x02'):
        return 'PSB'
    if head.startswith(b'8BPS\x00\x01'):
        return 'PSD'
    if head.startswith(b'qoif'):
        return 'QOI'
    if head.startswith(b'II\xbc\x01'):
        return 'JXR'
    if head.startswith((b'\xff\x0a', b'\x00\x00\x00\x0cJXL \x0d\x0a\x87\x0a')):
        return 'JXL'
    if len(head) >= 16 and head[4:8] == b'ftyp':
        brands = {head[8:12]} | {head[i:i + 4] for i in range(16, min(len(head), int.from_bytes(head[:4], 'big')), 4)}
        if brands & {b'avif', b'avis'}:
            return 'AVIF'
        if brands & {b'heic', b'heix', b'hevc', b'hevx', b'heim', b'heis', b'mif1', b'msf1'}:
            return 'HEIC'
        if b'crx ' in brands:
            return 'RAW'
    stripped = head.lstrip(b'\xef\xbb\xbf \t\r\n')
    if stripped.startswith(b'<') and re.search(rb'<(?:[\w.-]+:)?svg(?:\s|>)', head):
        return 'SVG'
    raw_header = head.startswith((b'II*\x00', b'MM\x00*', b'IIRO', b'IIRS', b'FUJIFILMCCD-RAW',
                                  b'\x00MRM', b'II\x1a\x00HEAPCCDR'))
    if head[8:12] == b'CR\x02\x00' or (path.suffix.lower() in RAW_EXTENSIONS and raw_header):
        return 'RAW'
    if head.startswith((b'II*\x00', b'MM\x00*')):
        # Pillow cannot open every CFA photometric type, so inspect the bounded
        # classic TIFF directory directly for DNGVersion, without decoding pixels.
        order = 'little' if head[:2] == b'II' else 'big'
        offset = int.from_bytes(head[4:8], order)
        if 8 <= offset <= min(path.stat().st_size - 2, 16 * 1024 * 1024):
            with path.open('rb') as stream:
                stream.seek(offset)
                count = int.from_bytes(stream.read(2), order)
                if count <= 4096:
                    directory = stream.read(count * 12)
                    if len(directory) == count * 12 and any(int.from_bytes(directory[i:i + 2], order) == 50706 for i in range(0, len(directory), 12)):
                        return 'RAW'
    return None


def decoder_tool(name, package):
    # Use distribution tools, never a same-named executable in the image folder/PATH.
    tool = Path('/usr/bin') / name
    if not tool.is_file() or not os.access(tool, os.X_OK):
        raise ValueError(f'This format needs the distribution package {package}. Install it with your package manager.')
    return str(tool)


def run_helper(command, folder, gate=None, ticket=None, payload=None, output=None):
    """Bound helper CPU, memory, output and wall time without fork hooks in Tk threads."""
    limiter = decoder_tool('prlimit', 'util-linux')
    command = [limiter, '--as=2147483648', '--cpu=25', f'--fsize={MAX_INPUT_BYTES}', '--nofile=64', '--'] + command
    env = {'PATH': '/usr/bin:/bin', 'HOME': str(folder), 'TMPDIR': str(folder),
           'MAGICK_CONFIGURE_PATH': str(folder), 'MAGICK_TEMPORARY_PATH': str(folder),
           'OMP_NUM_THREADS': '1', 'MAGICK_THREAD_LIMIT': '1', 'LC_ALL': 'C'}
    with (folder / 'errors.txt').open('wb+') as errors, (folder / 'stdin').open('wb+') as source:
        if payload is not None:
            source.write(payload)
            source.seek(0)
        with (output.open('wb') if output else open(os.devnull, 'wb')) as stdout:
            process = subprocess.Popen(command, cwd=folder, env=env, stdin=source, stdout=stdout,
                                       stderr=errors, start_new_session=True)
            deadline = time.monotonic() + HELPER_SECONDS
            try:
                while process.poll() is None:
                    if gate and not gate.accepts(ticket):
                        raise Superseded()
                    if time.monotonic() > deadline:
                        raise ValueError('Image decoder exceeded its time limit.')
                    time.sleep(0.03)
            finally:
                if process.poll() is None:
                    try:
                        os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                process.wait()
            if process.returncode:
                raise ValueError('The image could not be decoded, or exceeded decoder resource limits.')


def svg_dimensions(data):
    # UTF-8 XML only, without entities, scripts or links to other files/web pages.
    if len(data) > 8 * 1024 * 1024:
        raise ValueError('SVG exceeds the 8 MiB input limit.')
    text = data.decode('utf-8-sig')
    if re.search(r'<!\s*(?:DOCTYPE|ENTITY)', text, re.I):
        raise ValueError('SVG document types and entities are not supported.')
    try:
        root = ET.fromstring(text)
    except ET.ParseError as error:
        raise ValueError('Invalid SVG XML.') from error
    if root.tag not in ('svg', '{http://www.w3.org/2000/svg}svg'):
        raise ValueError('Invalid SVG root.')
    for element in root.iter():
        if element.tag.rsplit('}', 1)[-1] in ('script', 'include'):
            raise ValueError('SVG scripts and external includes are not supported.')
        for name, value in element.attrib.items():
            if name.rsplit('}', 1)[-1] in ('href', 'base') and not value.startswith('#'):
                raise ValueError('SVG external references are not supported.')
        for value in list(element.attrib.values()) + ([element.text] if element.text else []):
            if re.search(r'@import', value, re.I) or any(not url.strip(' \t\r\n\"\'').startswith('#')
                                                       for url in re.findall(r'url\s*\((.*?)\)', value, re.I | re.S)):
                raise ValueError('SVG external references are not supported.')
    viewbox = [float(v) for v in re.split(r'[ ,\s]+', root.get('viewBox', '').strip()) if v]
    def length(value, fallback):
        match = re.fullmatch(r'\s*(\d+(?:\.\d*)?|\.\d+)(px|in|cm|mm|pt|pc)?\s*', value or '')
        return float(match[1]) * {'px': 1, 'in': 96, 'cm': 96 / 2.54, 'mm': 96 / 25.4, 'pt': 96 / 72, 'pc': 16, None: 1}[match[2]] if match else fallback
    width = length(root.get('width'), viewbox[2] if len(viewbox) == 4 else 300)
    height = length(root.get('height'), viewbox[3] if len(viewbox) == 4 else 150)
    if not all(math.isfinite(v) and 0 < v <= 8192 for v in (width, height)) or width * height > MAX_HELPER_PIXELS:
        raise ValueError('SVG dimensions exceed the decoder limit.')
    return math.ceil(width), math.ceil(height)


def decode_qoi(path, gate, ticket):
    """QOI 1.0 reader for distributions predating Pillow's QOI plugin.

    Based on the public specification: https://qoiformat.org/qoi-specification.pdf
    """
    with path.open('rb') as stream:
        data = stream.read(MAX_INPUT_BYTES + 1)
    if len(data) > MAX_INPUT_BYTES or len(data) < 22 or data[-8:] != b'\x00' * 7 + b'\x01':
        raise ValueError('Invalid or oversized QOI file.')
    width, height = int.from_bytes(data[4:8], 'big'), int.from_bytes(data[8:12], 'big')
    if not width or not height or width * height > MAX_HELPER_PIXELS or data[12] not in (3, 4) or data[13] not in (0, 1):
        raise ValueError('Invalid QOI header or dimensions exceed the decoder limit.')
    pixels, cache = bytearray(width * height * 4), [(0, 0, 0, 0)] * 64
    colour, offset, written = (0, 0, 0, 255), 14, 0
    deadline = time.monotonic() + HELPER_SECONDS
    while written < width * height:
        if gate and not gate.accepts(ticket):
            raise Superseded()
        if time.monotonic() > deadline or offset >= len(data) - 8:
            raise ValueError('Truncated QOI file or decoder time limit exceeded.')
        op, run = data[offset], 1
        offset += 1
        r, g, b, a = colour
        extra = 3 if op == 254 else 4 if op == 255 else 1 if op & 192 == 128 else 0
        if offset + extra > len(data) - 8:
            raise ValueError('Truncated QOI colour chunk.')
        if op == 254:
            r, g, b = data[offset:offset + 3]
        elif op == 255:
            r, g, b, a = data[offset:offset + 4]
        elif op & 192 == 0:
            r, g, b, a = cache[op]
        elif op & 192 == 64:
            r, g, b = (r + ((op >> 4) & 3) - 2) % 256, (g + ((op >> 2) & 3) - 2) % 256, (b + (op & 3) - 2) % 256
        elif op & 192 == 128:
            dg, other = (op & 63) - 32, data[offset]
            r, g, b = (r + dg + (other >> 4) - 8) % 256, (g + dg) % 256, (b + dg + (other & 15) - 8) % 256
        else:
            run = (op & 63) + 1
        offset += extra
        if written + run > width * height:
            raise ValueError('QOI run exceeds the image dimensions.')
        colour = (r, g, b, a)
        cache[(r * 3 + g * 5 + b * 7 + a * 11) % 64] = colour
        pixels[written * 4:(written + run) * 4] = bytes(colour) * run
        written += run
    if offset != len(data) - 8:
        raise ValueError('Unexpected data after QOI pixels.')
    image = Image.frombytes('RGBA', (width, height), bytes(pixels))
    if data[13] == 1:
        # A linear QOI is displayed in sRGB; alpha remains linear and unchanged.
        curve = [round(255 * (12.92 * (v / 255) if v / 255 <= 0.0031308 else 1.055 * (v / 255) ** (1 / 2.4) - 0.055)) for v in range(256)]
        image = image.point(curve * 3 + list(range(256)))
    image, warning = normalize(image)
    return image, 1, 100, warning


def decode_extra(path, fmt, ticket, gate):
    if ticket.frame != 0:
        raise ValueError('This decoder displays the composite/first image only.')
    if fmt == 'QOI':
        return decode_qoi(path, gate, ticket)
    with tempfile.TemporaryDirectory(prefix='imageviewer-decode-') as temporary:
        folder = Path(temporary)
        source, output = folder / 'input', folder / 'output.png'
        with path.open('rb') as original, source.open('xb') as copy:
            total = 0
            while chunk := original.read(128 * 1024):
                total += len(chunk)
                if total > MAX_INPUT_BYTES:
                    raise ValueError('Image exceeds the 256 MiB helper input limit.')
                if gate and not gate.accepts(ticket):
                    raise Superseded()
                copy.write(chunk)
        warning = ''
        if fmt in ('SVG', 'SVGZ'):
            with (gzip.open(source, 'rb') if fmt == 'SVGZ' else source.open('rb')) as stream:
                data = stream.read(8 * 1024 * 1024 + 1)
            width, height = svg_dimensions(data)
            run_helper([decoder_tool('rsvg-convert', 'librsvg2-bin'), '--format=png', '--keep-aspect-ratio',
                        '--width', str(width), '--height', str(height), '--output', str(output)], folder, gate, ticket, payload=data)
        elif fmt == 'JXL':
            run_helper([decoder_tool('djxl', 'libjxl-tools (Ubuntu 24.04+/Debian 12+)'), str(source), str(output)], folder, gate, ticket)
        elif fmt == 'JXR':
            # jxrlib selects its input codec by suffix; the original is sniffed,
            # then copied under this trusted suffix (never renamed in place).
            jxr_source = folder / 'input.jxr'
            source.rename(jxr_source)
            output = folder / 'output.tif'
            run_helper([decoder_tool('JxrDecApp', 'libjxr-tools'), '-i', str(jxr_source), '-o', str(output), '-c', '9'], folder, gate, ticket)
            warning = 'JPEG XR rendered to 8-bit RGB; alpha is not available in this decoder.'
        elif fmt == 'RAW':
            output = folder / 'output.ppm'
            run_helper([decoder_tool('dcraw_emu', 'libraw-bin'), '-w', '-W', '-q', '3', '-o', '1', '-Z', '-', str(source)],
                       folder, gate, ticket, output=output)
            warning = 'Camera RAW rendered to 8-bit sRGB with camera white balance; original RAW preserved.'
        else:
            # Explicit raster coder, private fixed input name, and no delegates or filters.
            coder = {'AVIF': 'HEIC', 'PSB': 'PSD'}.get(fmt, fmt)
            if coder not in {'HEIC', 'EXR', 'HDR', 'XCF', 'PSD'}:
                raise ValueError('No permitted fallback decoder for this image.')
            (folder / 'policy.xml').write_text(
                '<policymap><policy domain="delegate" rights="none" pattern="*"/>'
                '<policy domain="filter" rights="none" pattern="*"/>'
                '<policy domain="coder" rights="none" pattern="*"/>'
                f'<policy domain="coder" rights="read" pattern="{coder}"/>'
                '<policy domain="coder" rights="write" pattern="PNG"/>'
                '<policy domain="resource" name="memory" value="256MiB"/>'
                '<policy domain="resource" name="map" value="256MiB"/>'
                '<policy domain="resource" name="disk" value="256MiB"/>'
                '<policy domain="resource" name="width" value="16KP"/>'
                '<policy domain="resource" name="height" value="16KP"/>'
                '<policy domain="resource" name="time" value="25"/>'
                '<policy domain="path" rights="none" pattern="@*"/></policymap>')
            profile = folder / 'srgb.icc'
            profile.write_bytes(ImageCms.ImageCmsProfile(ImageCms.createProfile('sRGB')).tobytes())
            tool = '/usr/bin/magick' if Path('/usr/bin/magick').is_file() else decoder_tool('convert', 'imagemagick')
            colour = ['-colorspace', 'sRGB'] if fmt in ('EXR', 'HDR') else []
            run_helper([tool, '-limit', 'thread', '1', f'{coder}:{source}[0]', '-auto-orient', *colour,
                        '-profile', str(profile), '-colorspace', 'sRGB', '-depth', '8', f'PNG:{output}'], folder, gate, ticket)
            if fmt in ('EXR', 'HDR'):
                warning = 'HDR rendered to 8-bit sRGB; highlights can clip. Original HDR preserved.'
            elif fmt == 'XCF':
                warning = 'XCF first layer only; layers are not composited.'
        if not output.is_file() or not 0 < output.stat().st_size <= MAX_INPUT_BYTES:
            raise ValueError('Decoder produced no usable image or exceeded its output limit.')
        with Image.open(output, formats=['PNG', 'PPM', 'TIFF']) as image:
            if image.width * image.height > MAX_HELPER_PIXELS:
                raise ValueError('Decoded image exceeds the 40 megapixel helper limit.')
            normalized, colour_warning = normalize(image.copy())
        return normalized, 1, 100, ' '.join(filter(None, (warning, colour_warning)))


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
    fmt = extra_format(path)
    with warnings.catch_warnings():
        warnings.simplefilter('error', Image.DecompressionBombWarning)
        if fmt:
            normalized, frames, duration, warning = decode_extra(path, fmt, ticket, gate)
        else:
            with Image.open(path, formats=pillow_formats()) as source:
                # Pillow exposes PSD layers with one-based seek indices; the
                # initially loaded image is the composite, not a page/layer.
                frames = 1 if source.format == 'PSD' else max(1, getattr(source, 'n_frames', 1))
                if not 0 <= ticket.frame < frames:
                    raise ValueError('Image frame/page is out of range.')
                if ticket.frame:
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
    return Decoded(ticket, normalized, fmt, frames, duration, fmt in ('GIF', 'WEBP', 'FLI', 'PNG') and frames > 1, warning)


def is_supported(path):
    try:
        with warnings.catch_warnings():
            warnings.simplefilter('error', Image.DecompressionBombWarning)
            if extra_format(path):
                return True
            with Image.open(path, formats=pillow_formats()) as image:
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
