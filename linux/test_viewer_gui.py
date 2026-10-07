"""Real Tk integration checks; run under an isolated X11 display with xvfb-run."""
import hashlib
from pathlib import Path
import tempfile
import time
import tkinter as tk
import unittest
from unittest.mock import patch
from PIL import Image
from viewer import Viewer


class LinuxGui(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.folder = Path(self.temp.name)
        self.first = self.folder / 'image2.png'
        self.second = self.folder / 'image10.jpg'
        Image.new('RGB', (1200, 800), '#e03540').save(self.first)
        Image.new('RGB', (600, 900), '#247bd1').save(self.second)
        self.hashes = {p: hashlib.sha256(p.read_bytes()).hexdigest() for p in (self.first, self.second)}
        self.root = tk.Tk()
        self.errors = []
        self.root.report_callback_exception = lambda *args: self.errors.append(args)
        self.viewer = Viewer(self.root, self.first)
        self.pump(lambda: self.viewer.photo is not None and len(self.viewer.files) == 2)
        self.pump(lambda: self.root.winfo_viewable())

    def pump(self, condition, seconds=5):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            self.root.update()
            if condition():
                return
            time.sleep(0.01)
        self.fail('Timed out waiting for the Linux GUI.')

    def tearDown(self):
        self.viewer.close()
        self.viewer.worker.shutdown(wait=True)
        self.viewer.scanner.shutdown(wait=True)
        self.assertFalse(self.errors)
        for path, expected in self.hashes.items():
            self.assertEqual(hashlib.sha256(path.read_bytes()).hexdigest(), expected)
        self.temp.cleanup()

    def test_keyboard_browse_rotate_fit_and_real_photo(self):
        self.assertGreater(self.viewer.photo.width(), 0)
        self.assertLessEqual(self.viewer.photo.width(), self.viewer.canvas.winfo_width())
        self.root.focus_force()
        self.root.event_generate('<Right>')
        self.pump(lambda: self.viewer.current is not None and self.viewer.current.ticket.path == str(self.second))
        self.root.event_generate('r')
        self.assertEqual(self.viewer.turns, 1)
        self.root.event_generate('1')
        self.assertEqual(self.viewer.view.zoom, 1)
        self.root.event_generate('f')
        self.assertEqual(self.viewer.view.mode, 'fit')
        self.pump(lambda: self.viewer.photo is not None)

    def test_zoom_pan_fullscreen_and_escape(self):
        canvas = self.viewer.canvas
        initial = self.viewer.view.zoom
        canvas.event_generate('<Control-Button-4>', x=200, y=150)
        self.assertGreater(self.viewer.view.zoom, initial)
        before = self.viewer.view.pan_x, self.viewer.view.pan_y
        canvas.event_generate('<ButtonPress-1>', x=100, y=100)
        canvas.event_generate('<B1-Motion>', x=140, y=130)
        self.assertAlmostEqual(self.viewer.view.pan_x - before[0], 40)
        self.assertAlmostEqual(self.viewer.view.pan_y - before[1], 30)
        self.root.focus_force()
        self.root.event_generate('<F11>')
        self.pump(lambda: self.root.attributes('-fullscreen'))
        self.root.event_generate('<Escape>')
        self.pump(lambda: not self.root.attributes('-fullscreen'))

    def test_late_render_error_and_decode_never_replace_new_content(self):
        ticket = self.viewer.current.ticket
        self.viewer.pending.put(('render', ticket, self.viewer.render_generation - 1, None, ValueError('stale render')))
        self.viewer.drain()
        self.assertNotIn('stale render', self.viewer.status.get())
        self.viewer.open(self.first)
        self.viewer.open(self.second)
        self.pump(lambda: self.viewer.current is not None and self.viewer.current.ticket.path == str(self.second))
        self.assertEqual(self.viewer.current.format, 'JPEG')

    def test_copy_save_and_unsupported_file(self):
        destination = self.folder / 'copy.png'
        with patch('viewer.filedialog.asksaveasfilename', return_value=str(destination)):
            self.viewer.save_copy()
        self.pump(lambda: 'PNG copy saved' in self.viewer.status.get())
        self.assertTrue(destination.is_file())
        invalid = self.folder / 'broken.jpg'
        invalid.write_text('This is not an image.')
        self.viewer.open(invalid)
        self.pump(lambda: 'Could not complete' in self.viewer.status.get())
        self.assertIsNone(self.viewer.current)

    def test_animation_pages_and_compact_layout(self):
        gif = self.folder / 'animation.gif'
        Image.new('RGB', (30, 20), 'red').save(gif, save_all=True,
            append_images=[Image.new('RGB', (30, 20), 'blue')], duration=70, loop=0)
        self.viewer.open(gif)
        self.pump(lambda: self.viewer.current is not None and self.viewer.current.animated)
        first = self.viewer.current.ticket.generation
        self.pump(lambda: self.viewer.current.ticket.generation > first)
        self.viewer.playing.set(False)
        self.viewer.toggle_animation()
        self.assertIsNone(self.viewer.animation_job)
        self.root.geometry('720x560')
        self.root.update()
        self.assertGreater(self.viewer.canvas.winfo_height(), 200)
        for row in self.root.winfo_children():
            for widget in row.winfo_children():
                self.assertLessEqual(widget.winfo_x() + widget.winfo_width(), row.winfo_width())


if __name__ == '__main__':
    unittest.main()
