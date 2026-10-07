"""Additional decoder fixtures; all images are synthetic and originals are hashed."""
import gzip
import hashlib
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import threading
import time
import unittest
from unittest.mock import patch
from PIL import Image, TiffImagePlugin
from viewer_core import FORMATS, GenerationGate, Superseded, decode, extra_format, run_helper, scan_folder


def make_dng(path):
    tags = TiffImagePlugin.ImageFileDirectory_v2()
    tags[262], tags[271], tags[272] = 32803, 'ImageViewer', 'Synthetic DNG'
    tags[33421], tags[33422] = (2, 2), bytes((0, 1, 1, 2))
    tags[50706], tags[50707], tags[50708] = bytes((1, 4, 0, 0)), bytes((1, 1, 0, 0)), 'ImageViewer Synthetic DNG'
    tags[50714], tags[50717] = 0, 1023
    tags.tagtype[50721] = 10
    tags[50721] = tuple(TiffImagePlugin.IFDRational(v) for v in (1, 0, 0, 0, 1, 0, 0, 0, 1))
    tags[50728], tags[50778] = (1, 1, 1), 21
    Image.frombytes('I;16', (128, 96), struct.pack('<H', 500) * (128 * 96)).save(path, format='TIFF', tiffinfo=tags)


def make_xcf(path):
    # Run-length encoded one-layer GIMP XCF v001, 4x3 solid green.
    data = bytearray(b'gimp xcf v001\0' + struct.pack('>III', 4, 3, 0))
    data.extend(struct.pack('>II', 17, 1) + b'\1' + struct.pack('>II', 0, 0))
    layer_pointer = len(data)
    data.extend(b'\0' * 12)  # layer pointer, layer terminator, channel terminator
    def point(offset):
        struct.pack_into('>I', data, offset, len(data))
    point(layer_pointer)
    data.extend(struct.pack('>IIII', 4, 3, 0, 6) + b'layer\0' + struct.pack('>III', 6, 4, 255) + struct.pack('>II', 0, 0))
    hierarchy_pointer = len(data)
    data.extend(b'\0' * 8)  # hierarchy pointer, no mask
    point(hierarchy_pointer)
    data.extend(struct.pack('>III', 4, 3, 3))
    level_pointer = len(data)
    data.extend(b'\0' * 8)
    point(level_pointer)
    data.extend(struct.pack('>II', 4, 3))
    tile_pointer = len(data)
    data.extend(b'\0' * 8)
    point(tile_pointer)
    data.extend(bytes((11, 32, 11, 180, 11, 80)))
    path.write_bytes(data)


class FormatChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.folder = Path(self.temp.name)
        self.image = Image.new('RGB', (128, 96), (32, 180, 80))

    def tearDown(self):
        self.temp.cleanup()

    def open_unchanged(self, path, fmt, size=(128, 96)):
        before = hashlib.sha256(path.read_bytes()).digest()
        result = decode(GenerationGate().request(path))
        self.assertEqual(result.format, fmt)
        self.assertEqual(result.image.size, size)
        self.assertEqual(hashlib.sha256(path.read_bytes()).digest(), before)
        self.assertEqual(result.image.mode, 'RGBA')
        return result

    def convert(self, fmt):
        tool = '/usr/bin/magick' if Path('/usr/bin/magick').is_file() else '/usr/bin/convert'
        if not Path(tool).is_file():
            self.skipTest('Install imagemagick for decoder integration fixtures.')
        source, path = self.folder / 'source.png', self.folder / 'image.wrong'
        self.image.save(source)
        subprocess.run([tool, str(source), f'{fmt}:{path}'], check=True, stdout=subprocess.DEVNULL,
                       stderr=subprocess.DEVNULL, timeout=30, env={**os.environ, 'OMP_NUM_THREADS': '1'})
        return path

    def test_heic_and_avif_content_sniff_colour_and_original(self):
        for fmt in ('HEIC', 'AVIF'):
            with self.subTest(fmt=fmt):
                result = self.open_unchanged(self.convert(fmt), fmt)
                for actual, expected in zip(result.image.getpixel((40, 40))[:3], (32, 180, 80)):
                    self.assertLessEqual(abs(actual - expected), 5)

    def test_exr_and_hdr_are_rendered_with_visible_precision_warning(self):
        for fmt in ('EXR', 'HDR'):
            with self.subTest(fmt=fmt):
                result = self.open_unchanged(self.convert(fmt), fmt)
                self.assertIn('8-bit', result.warning)
                self.assertEqual(result.image.getchannel('A').getextrema(), (255, 255))

    def test_psd_psb_composite_not_one_based_layer_seek(self):
        for fmt in ('PSD', 'PSB'):
            with self.subTest(fmt=fmt):
                result = self.open_unchanged(self.convert(fmt), fmt)
                self.assertEqual(result.image.getpixel((40, 40)), (32, 180, 80, 255))
                self.assertEqual(result.frames, 1)

    def test_svg_and_svgz_transparency_internal_reference_and_renamed_file(self):
        svg = b'<svg xmlns="http://www.w3.org/2000/svg" width="128" height="96"><defs><rect id="r" x="20" y="20" width="50" height="50" fill="#20b450"/></defs><use href="#r"/></svg>'
        for fmt, contents in (('SVG', svg), ('SVGZ', gzip.compress(svg))):
            path = self.folder / (fmt + '.wrong')
            path.write_bytes(contents)
            result = self.open_unchanged(path, fmt)
            self.assertEqual(result.image.getpixel((40, 40)), (32, 180, 80, 255))
            self.assertEqual(result.image.getpixel((0, 0))[3], 0)

    def test_svg_external_content_entities_and_bomb_dimensions_fail_closed(self):
        for content in ('<!DOCTYPE svg [<!ENTITY x SYSTEM "file:///etc/passwd">]><svg>&x;</svg>',
                        '<svg width="128" height="96"><image href="file:///etc/passwd"/></svg>',
                        '<svg width="128" height="96"><style>@import "https://example.com/a.css";</style></svg>',
                        '<svg width="999999999" height="999999999"/>'):
            path = self.folder / 'bad.svg'
            path.write_text(content)
            with patch('viewer_core.run_helper') as helper:
                with self.assertRaises(ValueError):
                    decode(GenerationGate().request(path))
                helper.assert_not_called()

    def test_xcf_first_layer(self):
        path = self.folder / 'image.wrong'
        make_xcf(path)
        result = self.open_unchanged(path, 'XCF', (4, 3))
        self.assertEqual(result.image.getpixel((0, 0)), (32, 180, 80, 255))

    def test_raw_dng_renamed_content_and_source_preservation(self):
        path = self.folder / 'camera.wrong'
        make_dng(path)
        result = self.open_unchanged(path, 'RAW')
        self.assertIn('Camera RAW', result.warning)
        r, g, b, a = result.image.getpixel((40, 40))
        self.assertEqual(a, 255)
        self.assertTrue(0 < min(r, g, b) < 255)

    def test_jpeg_xr_rgb(self):
        if not Path('/usr/bin/JxrEncApp').is_file():
            self.skipTest('Install libjxr-tools.')
        source, path = self.folder / 'input.bmp', self.folder / 'renamed.wrong'
        self.image.save(source)
        subprocess.run(['/usr/bin/JxrEncApp', '-i', str(source), '-o', str(path)], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
        result = self.open_unchanged(path, 'JXR')
        self.assertEqual(result.image.getpixel((40, 40)), (32, 180, 80, 255))

    def test_jpeg_xl_actual_lossless_roundtrip(self):
        if not Path('/usr/bin/cjxl').is_file():
            self.skipTest('JPEG XL tools are not packaged on Ubuntu 22.04.')
        source, path = self.folder / 'input.png', self.folder / 'renamed.wrong'
        self.image.save(source)
        subprocess.run(['/usr/bin/cjxl', str(source), str(path), '-d', '0', '-e', '1'], check=True,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=30)
        result = self.open_unchanged(path, 'JXL')
        self.assertEqual(result.image.getpixel((40, 40)), (32, 180, 80, 255))

    def test_qoi_all_chunk_types_alpha_and_invalid_runs(self):
        # RGBA, RGB, DIFF (+1,+1,+1), LUMA (+2,+2,+2), INDEX, RUN.
        first, second = (10, 20, 30, 70), (40, 50, 60, 70)
        index = (10 * 3 + 20 * 5 + 30 * 7 + 70 * 11) % 64
        chunks = bytes((255, *first, 254, *second[:3], 0x7f, 0xa2, 0x88, index, 0xc1))
        path = self.folder / 'qoi.wrong'
        header = b'qoif' + struct.pack('>II', 7, 1) + b'\x04\0'
        path.write_bytes(header + chunks + b'\0' * 7 + b'\x01')
        result = self.open_unchanged(path, 'QOI', (7, 1))
        self.assertEqual(list(result.image.getdata()), [first, second, (41, 51, 61, 70), (43, 53, 63, 70), first, first, first])
        path.write_bytes(header + b'\xfd' + b'\0' * 7 + b'\x01')
        with self.assertRaises(ValueError):
            decode(GenerationGate().request(path))

    def test_added_pillow_formats_and_content_folder_navigation(self):
        for fmt in ('SGI', 'XBM', 'MSP', 'ICNS'):
            path = self.folder / (fmt + '.wrong')
            image = self.image.convert('1') if fmt in ('XBM', 'MSP') else self.image.resize((128, 128)) if fmt == 'ICNS' else self.image
            image.save(path, format=fmt)
            result = decode(GenerationGate().request(path))
            self.assertEqual(result.format, fmt)
            self.assertTrue(result.image.width > 0)
        svg = self.folder / 'vector.wrong'
        svg.write_text('<svg width="128" height="96"/>')
        self.assertEqual(len(scan_folder(self.folder)), 5)

    def test_corrupt_extra_missing_decoder_and_postscript_never_run_implicit_helper(self):
        path = self.folder / 'malicious.svg'
        path.write_bytes(b'%!PS-Adobe-3.0\nshowpage')
        self.assertNotIn('EPS', FORMATS)
        with patch('viewer_core.run_helper') as helper:
            with self.assertRaises(OSError):
                decode(GenerationGate().request(path))
            helper.assert_not_called()
        path.write_bytes(b'\xff\x0a\x00broken')
        with patch('viewer_core.decoder_tool', side_effect=ValueError('Install libjxl-tools')):
            with self.assertRaisesRegex(ValueError, 'libjxl-tools'):
                decode(GenerationGate().request(path))

    def test_helper_timeout_and_supersession_reap_the_child(self):
        for cancel in (False, True):
            gate = GenerationGate()
            ticket = gate.request(self.folder / 'fake')
            pid = self.folder / 'pid'
            pid.unlink(missing_ok=True)
            command = ['/usr/bin/python3', '-c', 'import os,time; open("pid","w").write(str(os.getpid())); time.sleep(5)']
            timer = threading.Timer(0.15, lambda: gate.request(self.folder / 'new')) if cancel else None
            if timer:
                timer.start()
            try:
                with patch('viewer_core.HELPER_SECONDS', 0.3):
                    with self.assertRaises(Superseded if cancel else ValueError):
                        run_helper(command, self.folder, gate, ticket)
                with self.assertRaises(ProcessLookupError):
                    os.kill(int(pid.read_text()), 0)
            finally:
                if timer:
                    timer.join()


if __name__ == '__main__':
    unittest.main()
