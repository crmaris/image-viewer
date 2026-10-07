#!/usr/bin/env python3
"""Native Linux viewer: display edits only, originals are never overwritten."""
import argparse
import copy
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import queue
import tkinter as tk
from tkinter import filedialog, font, ttk
from PIL import ImageTk
from viewer_core import GenerationGate, Superseded, View, decode, render_tile, rotate_view, save_png_copy, scan_folder

VERSION = Path(__file__).with_name('VERSION').read_text().strip()


class Viewer:
    def __init__(self, root, path=None):
        self.root = root
        self.gate, self.folder_gate = GenerationGate(), GenerationGate()
        self.worker = ThreadPoolExecutor(max_workers=1)
        self.scanner = ThreadPoolExecutor(max_workers=1)
        self.pending = queue.Queue()
        self.current = None
        self.files = []
        self.selected_path = ''
        self.turns = 0
        self.render_generation = 0
        self.render_job = None
        self.animation_job = None
        self.closed = False
        self.update_executor = None
        self.update_job = None
        self.folder = None
        self.rotated_cache = None
        self.photo = None
        self.view = View()
        root.title('Image Viewer · Linux')
        width = min(1280, max(320, root.winfo_screenwidth() - 80))
        height = min(880, max(320, root.winfo_screenheight() - 100))
        root.geometry(f'{width}x{height}')
        root.minsize(min(680, width), min(480, height))
        for name in ['TkDefaultFont', 'TkTextFont', 'TkMenuFont']:
            font.nametofont(name).configure(size=12)
        style = ttk.Style(root)
        style.theme_use('clam')
        style.configure('.', font=('Sans', 12))
        bar = ttk.Frame(root, padding=8)
        bar.pack(fill='x')
        for text, action in [('Open…', self.open_dialog), ('Previous', lambda: self.navigate(-1)),
                             ('Next', lambda: self.navigate(1)), ('Fit', self.fit), ('1:1', self.actual)]:
            ttk.Button(bar, text=text, command=action).pack(side='left', padx=3)
        second = ttk.Frame(root, padding=(8, 0, 8, 8))
        second.pack(fill='x')
        ttk.Button(second, text='Rotate view', command=self.rotate).pack(side='left', padx=3)
        ttk.Button(second, text='Save PNG copy…', command=self.save_copy).pack(side='left', padx=3)
        self.playing = tk.BooleanVar(value=True)
        ttk.Checkbutton(second, text='Play animation', variable=self.playing, command=self.toggle_animation).pack(side='left', padx=10)
        self.page_label = tk.StringVar(value='')
        ttk.Label(second, textvariable=self.page_label).pack(side='left', padx=10)
        pages = ttk.Frame(root, padding=(8, 0, 8, 8))
        pages.pack(fill='x')
        ttk.Button(pages, text='Previous frame/page', command=lambda: self.frame(-1)).pack(side='left', padx=3)
        ttk.Button(pages, text='Next frame/page', command=lambda: self.frame(1)).pack(side='left', padx=3)
        self.canvas = tk.Canvas(root, background='#151b23', highlightthickness=0)
        self.canvas.pack(fill='both', expand=True)
        self.status = tk.StringVar(value='Open an image. Space/wheel browse · Ctrl+wheel zoom · Drag to pan · R rotate view')
        self.status_label = ttk.Label(root, textvariable=self.status, padding=8, wraplength=width - 24)
        self.status_label.pack(fill='x')
        self.canvas.bind('<Configure>', self.resize)
        self.canvas.bind('<ButtonPress-1>', self.pan_start)
        self.canvas.bind('<B1-Motion>', self.pan)
        self.canvas.bind('<MouseWheel>', self.wheel)
        self.canvas.bind('<Button-4>', self.wheel)
        self.canvas.bind('<Button-5>', self.wheel)
        self.canvas.bind('<Control-MouseWheel>', self.zoom)
        self.canvas.bind('<Control-Button-4>', self.zoom)
        self.canvas.bind('<Control-Button-5>', self.zoom)
        root.bind('<space>', lambda _: self.navigate(1))
        root.bind('<Right>', lambda _: self.navigate(1))
        root.bind('<Left>', lambda _: self.navigate(-1))
        root.bind('<BackSpace>', lambda _: self.navigate(-1))
        root.bind('<Next>', lambda _: self.navigate(1))
        root.bind('<Prior>', lambda _: self.navigate(-1))
        root.bind('<Home>', lambda _: self.end(False))
        root.bind('<End>', lambda _: self.end(True))
        root.bind('r', lambda _: self.rotate())
        root.bind('f', lambda _: self.fit())
        root.bind('0', lambda _: self.fit())
        root.bind('1', lambda _: self.actual())
        root.bind('<F11>', lambda _: self.fullscreen())
        root.bind('<Escape>', lambda _: self.escape())
        root.bind('<Control-s>', lambda _: self.save_copy())
        root.bind('<Control-o>', lambda _: self.open_dialog())
        root.protocol('WM_DELETE_WINDOW', self.close)
        self.drain_job = root.after(30, self.drain)
        root.update_idletasks()
        root.deiconify()
        marker = Path(__file__).with_name('INSTALL_KIND')
        if marker.is_file() and marker.read_text().strip() == 'portable':
            self.update_executor = ThreadPoolExecutor(max_workers=1)
            self.update_job = root.after(4000, self.check_updates)
        if path:
            root.after(50, lambda: self.open(path))

    def open_dialog(self):
        path = filedialog.askopenfilename(parent=self.root, title='Open image', filetypes=[('Images or renamed image files', '*')])
        if path:
            self.open(path)

    def submit(self, kind, ticket, action, extra=None, executor=None):
        def run():
            try:
                self.pending.put((kind, ticket, extra, action(), None))
            except Superseded:
                pass
            except Exception as error:
                self.pending.put((kind, ticket, extra, None, error))
        (executor or self.worker).submit(run)

    def cancel_animation(self):
        if self.animation_job is not None:
            self.root.after_cancel(self.animation_job)
            self.animation_job = None

    def open(self, path):
        try:
            resolved = str(Path(path).resolve(strict=True))
            if not Path(resolved).is_file():
                raise ValueError('Choose an image file.')
        except (OSError, ValueError) as error:
            self.show_error(error)
            return
        self.cancel_animation()
        self.selected_path = resolved
        self.current = None
        self.photo = None
        self.turns = 0
        self.canvas.delete('all')
        self.status.set(f'Opening {Path(resolved).name}…')
        ticket = self.gate.request(resolved)
        self.submit('decode', ticket, lambda: decode(ticket, self.gate), extra='new')

    def navigate(self, delta):
        if self.selected_path not in self.files or not self.files:
            return
        index = self.files.index(self.selected_path)
        self.open(self.files[(index + delta) % len(self.files)])

    def end(self, last):
        if self.files:
            self.open(self.files[-1 if last else 0])

    def fullscreen(self):
        self.root.attributes('-fullscreen', not self.root.attributes('-fullscreen'))

    def escape(self):
        if self.root.attributes('-fullscreen'):
            self.root.attributes('-fullscreen', False)
        else:
            self.close()

    def frame(self, delta, automatic=False):
        if not self.current or self.current.frames < 2:
            return
        old = self.current
        if not automatic:
            self.playing.set(False)
        self.cancel_animation()
        requested = self.gate.latest()
        start = requested.frame if requested.path == old.ticket.path else old.ticket.frame
        ticket = self.gate.request(old.ticket.path, (start + delta) % old.frames)
        self.submit('decode', ticket, lambda: decode(ticket, self.gate), extra='frame')

    def toggle_animation(self):
        self.cancel_animation()
        self.animate()

    def animate(self):
        if self.current and self.current.animated and self.playing.get() and self.animation_job is None:
            self.animation_job = self.root.after(self.current.duration_ms, self.next_animated_frame)

    def next_animated_frame(self):
        self.animation_job = None
        self.frame(1, automatic=True)

    def decoded(self, result, kind):
        # This gate is checked AGAIN on Tk's publication thread, not just worker.
        if not self.gate.accepts(result.ticket) or result.ticket.path != self.selected_path:
            return
        self.current = result
        size = (result.image.height, result.image.width) if self.turns % 2 else result.image.size
        if kind == 'new' or self.view.mode == 'fit':
            self.view.fit(size)
        self.page_label.set(f'{"Frame" if result.animated else "Page"} {result.ticket.frame + 1}/{result.frames}' if result.frames > 1 else '')
        self.root.title(f'{Path(result.ticket.path).name} · Image Viewer Linux')
        self.update_status()
        self.draw_later()
        self.animate()
        folder = Path(result.ticket.path).parent
        if self.folder != folder or (kind == 'new' and result.ticket.path not in self.files):
            self.folder = folder
            self.files = [result.ticket.path]
            ticket = self.folder_gate.request(folder)
            self.submit('folder', ticket, lambda: scan_folder(folder, lambda: not self.folder_gate.accepts(ticket)), executor=self.scanner)

    def resized_image_size(self):
        size = self.current.image.size
        return size[::-1] if self.turns % 2 else size

    def fit(self):
        if self.current:
            self.view.fit(self.resized_image_size())
            self.draw_later()

    def actual(self):
        self.view.actual()
        self.draw_later()

    def rotate(self):
        if self.current:
            self.turns = (self.turns + 1) % 4
            self.view.fit(self.resized_image_size())
            self.draw_later()

    def resize(self, event):
        self.view.width, self.view.height = max(1, event.width), max(1, event.height)
        self.status_label.configure(wraplength=max(200, event.width - 24))
        if self.current and self.view.mode == 'fit':
            self.view.fit(self.resized_image_size())
        self.draw_later()

    def wheel(self, event):
        self.navigate(-1 if getattr(event, 'num', None) == 4 or getattr(event, 'delta', 0) > 0 else 1)
        return 'break'

    def zoom(self, event):
        if self.current:
            factor = 1.15 if getattr(event, 'num', None) == 4 or getattr(event, 'delta', 0) > 0 else 1 / 1.15
            self.view.zoom_at(factor, (event.x, event.y), self.resized_image_size())
            self.draw_later()
        return 'break'

    def pan_start(self, event):
        self.drag = (event.x, event.y, self.view.pan_x, self.view.pan_y)

    def pan(self, event):
        if self.current and hasattr(self, 'drag'):
            x, y, px, py = self.drag
            self.view.pan_x, self.view.pan_y = px + event.x - x, py + event.y - y
            self.view.mode = 'free'
            self.draw_later()

    def draw_later(self):
        self.render_generation += 1
        if self.render_job is not None:
            self.root.after_cancel(self.render_job)
        self.render_job = self.root.after(20, self.draw)

    def draw(self):
        self.render_job = None
        if self.current is None:
            return
        current, turns = self.current, self.turns
        view, generation = copy.copy(self.view), self.render_generation
        def render():
            if not self.gate.accepts(current.ticket) or generation != self.render_generation:
                raise Superseded()
            cached = self.rotated_cache
            if cached is None or cached[0] is not current.image or cached[1] != turns:
                cached = (current.image, turns, rotate_view(current.image, turns))
                self.rotated_cache = cached
            return render_tile(cached[2], view)
        self.submit('render', current.ticket, render, extra=generation)

    def update_status(self):
        if self.current:
            item = self.current
            warning = ' · ' + item.warning if item.warning else ''
            self.status.set(f'{Path(item.ticket.path).name} · {item.image.width}×{item.image.height} · {item.format} · {self.view.zoom * 100:.1f}% · rotation {self.turns * 90}° (view only){warning}')

    def save_copy(self):
        if not self.current:
            return
        item, turns = self.current, self.turns
        path = filedialog.asksaveasfilename(parent=self.root, title='Save rendered PNG copy (original unchanged)',
                                          initialdir=Path(item.ticket.path).parent, initialfile=Path(item.ticket.path).stem + '-copy.png',
                                          defaultextension='.png', filetypes=[('Rendered PNG copy', '*.png')])
        if path:
            self.submit('save', item.ticket, lambda: save_png_copy(item.ticket.path, rotate_view(item.image, turns), path))

    def drain(self):
        if self.closed:
            return
        if self.drain_job is not None:
            self.root.after_cancel(self.drain_job)
            self.drain_job = None
        for _ in range(12):
            try:
                kind, ticket, extra, value, error = self.pending.get_nowait()
            except queue.Empty:
                break
            gate = self.folder_gate if kind == 'folder' else self.gate
            if not gate.accepts(ticket):
                continue
            if kind != 'folder' and ticket.path != self.selected_path:
                continue
            if kind == 'render' and extra != self.render_generation:
                continue
            if error:
                self.show_error(error)
            elif kind == 'decode':
                self.decoded(value, extra)
            elif kind == 'folder':
                self.files = value or [self.selected_path]
            elif kind == 'render' and extra == self.render_generation:
                self.canvas.delete('all')
                if value:
                    tile, position = value
                    self.photo = ImageTk.PhotoImage(tile)
                    self.canvas.create_image(*position, image=self.photo, anchor='nw')
                self.update_status()
            elif kind == 'save':
                self.status.set(f'PNG copy saved: {Path(value).name}. Original unchanged.')
        if not self.closed:
            self.drain_job = self.root.after(30, self.drain)

    def show_error(self, error):
        self.status.set('Could not complete the image action: ' + str(error)[:300])

    def check_updates(self):
        def update():
            try:
                from update_service import run_update
                run_update()
            except Exception:
                pass  # an unavailable update server must not interrupt image viewing
        if not self.closed:
            self.update_executor.submit(update)
            self.update_job = self.root.after(3600000, self.check_updates)

    def close(self):
        if self.closed:
            return
        self.closed = True
        if self.update_job is not None:
            self.root.after_cancel(self.update_job)
        if self.update_executor is not None:
            self.update_executor.shutdown(wait=False, cancel_futures=True)
        self.cancel_animation()
        if self.drain_job is not None:
            self.root.after_cancel(self.drain_job)
        if self.render_job is not None:
            self.root.after_cancel(self.render_job)
        self.current = self.rotated_cache = self.photo = None
        self.gate.request(self.selected_path or '.', -1)
        self.folder_gate.request('.', -1)
        self.worker.shutdown(wait=False, cancel_futures=True)
        self.scanner.shutdown(wait=False, cancel_futures=True)
        self.root.destroy()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', action='version', version=f'Image Viewer Linux {VERSION}')
    parser.add_argument('image', nargs='?', type=Path)
    args = parser.parse_args()
    try:
        root = tk.Tk(className='ImageViewerLinux')
    except tk.TclError as error:
        parser.exit(1, f'Cannot open the Linux desktop display: {error}\nUse viewer_cli.py for headless image inspection.\n')
    Viewer(root, args.image)
    root.mainloop()


if __name__ == '__main__':
    main()
