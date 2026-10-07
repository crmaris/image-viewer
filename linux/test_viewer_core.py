"""Meaningful offline image fixtures. No Tk root, native window or HALO access."""
import hashlib
import io
from pathlib import Path
import tempfile
import unittest
from PIL import Image, ImageCms
from viewer_core import GenerationGate, Superseded, Ticket, View, decode, normalize, render_tile, rotate_view, save_png_copy, scan_folder


class ImageFixtures(unittest.TestCase):
    def test_cursor_anchor_survives_zoom(self):
        size, cursor = (1600, 1200), (130, 170)
        view = View(600, 400, zoom=0.5, mode='free')
        ox, oy = view.origin(size)
        before = ((cursor[0] - ox) / view.zoom, (cursor[1] - oy) / view.zoom)
        view.zoom_at(1.15, cursor, size)
        ox, oy = view.origin(size)
        after = ((cursor[0] - ox) / view.zoom, (cursor[1] - oy) / view.zoom)
        self.assertAlmostEqual(before[0], after[0])
        self.assertAlmostEqual(before[1], after[1])

    def test_full_resolution_zoom_tiles_cover_viewport_without_huge_bitmap(self):
        image = Image.new('RGBA', (6000, 4000), '#ff0000')
        view = View(600, 400)
        view.fit(image.size)
        for _ in range(3):
            view.zoom_at(1.15, (300, 200), image.size)
        tile, position = render_tile(image, view)
        self.assertEqual(tile.size, (600, 400))
        self.assertEqual(position, (0, 0))
        self.assertEqual(tile.getchannel('A').getextrema(), (255, 255))
        view.zoom = 64
        tile, _ = render_tile(image, view)
        self.assertEqual(tile.size, (600, 400))

    def test_fit_small_image_never_enlarges_and_one_to_one_is_real_raster(self):
        view = View(1000, 700)
        view.fit((10, 5))
        self.assertEqual(view.zoom, 1)
        view.actual()
        tile, _ = render_tile(Image.new('RGBA', (10, 5)), view)
        self.assertEqual(tile.size, (10, 5))

    def test_exif_rotation_and_display_rotation_preserve_original_jpeg_bytes(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'original.jpg'
            source = Image.new('RGB', (12, 8), '#4477aa')
            exif = Image.Exif()
            exif[274] = 6
            source.save(path, exif=exif)
            before = hashlib.sha256(path.read_bytes()).hexdigest()
            gate = GenerationGate()
            result = decode(gate.request(path), gate)
            self.assertEqual(result.image.size, (8, 12))
            rotated = rotate_view(result.image, 1)
            self.assertEqual(rotated.size, (12, 8))
            self.assertEqual(result.image.size, (8, 12))
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), before)

    def test_bad_icc_falls_back_with_warning_and_good_srgb_not_double_changed(self):
        image = Image.new('RGBA', (4, 4), (100, 181, 89, 70))
        image.info['icc_profile'] = b'not an ICC profile'
        converted, warning = normalize(image)
        self.assertIn('profile', warning)
        self.assertEqual(converted.getpixel((0, 0)), (100, 181, 89, 70))
        converted_again, warning = normalize(converted)
        self.assertFalse(warning)
        self.assertEqual(converted_again.getpixel((0, 0)), converted.getpixel((0, 0)))

    def test_superseded_generation_same_path_or_different_frame_not_publishable(self):
        gate = GenerationGate()
        one = gate.request('same.png', 0)
        two = gate.request('same.png', 1)
        self.assertFalse(gate.accepts(one))
        self.assertTrue(gate.accepts(two))
        self.assertFalse(gate.accepts(Ticket(two.generation, 'wrong.png', 1)))
        with self.assertRaises(Superseded):
            decode(one, gate)

    def test_png_copy_collision_never_overwrites_original_or_existing_copy(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'source.png'
            Image.new('RGBA', (5, 5), 'red').save(path)
            result = decode(GenerationGate().request(path))
            before = path.read_bytes()
            with self.assertRaises(ValueError):
                save_png_copy(path, result.image, path)
            target = Path(folder) / 'copy.png'
            save_png_copy(path, result.image, target)
            target_bytes = target.read_bytes()
            with self.assertRaises(FileExistsError):
                save_png_copy(path, result.image, target)
            self.assertEqual(target.read_bytes(), target_bytes)
            self.assertEqual(path.read_bytes(), before)
            self.assertFalse(list(Path(folder).glob('.image-viewer-*')))

    def test_byte_sniffing_natural_folder_order_and_multi_page_tiff(self):
        with tempfile.TemporaryDirectory() as folder:
            for name in ['img10.jpg', 'img2.wrong']:
                Image.new('RGB', (3, 2), 'blue').save(Path(folder) / name, format='PNG')
            self.assertEqual([Path(p).name for p in scan_folder(folder)], ['img2.wrong', 'img10.jpg'])
            path = Path(folder) / 'pages.tif'
            Image.new('RGB', (3, 2), 'red').save(path, save_all=True, append_images=[Image.new('RGB', (5, 4), 'blue')])
            gate = GenerationGate()
            result = decode(gate.request(path, 1), gate)
            self.assertEqual(result.frames, 2)
            self.assertEqual(result.image.size, (5, 4))
            self.assertEqual(result.image.getpixel((0, 0)), (0, 0, 255, 255))

    def test_animated_gif_frames_are_composited_and_report_timing(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'animated.gif'
            first = Image.new('RGBA', (8, 8), 'red')
            second = Image.new('RGBA', (8, 8), 'blue')
            first.save(path, save_all=True, append_images=[second], duration=[40, 70], loop=0, disposal=2)
            gate = GenerationGate()
            result = decode(gate.request(path, 1), gate)
            self.assertTrue(result.animated)
            self.assertEqual(result.frames, 2)
            self.assertEqual(result.duration_ms, 70)
            self.assertEqual(result.image.getpixel((0, 0)), (0, 0, 255, 255))


if __name__ == '__main__':
    unittest.main()
