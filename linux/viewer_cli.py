#!/usr/bin/env python3
"""Headless decoder/viewport checks. A render is image-pixel evidence, not GUI proof."""
import argparse
import json
import sys
from pathlib import Path
from viewer_core import GenerationGate, View, decode, render_tile, rotate_view, save_png_copy


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('image', type=Path)
    parser.add_argument('--frame', type=int, default=0)
    parser.add_argument('--rotate', type=int, choices=[0, 90, 180, 270], default=0)
    parser.add_argument('--render', type=Path)
    parser.add_argument('--width', type=int, default=1000)
    parser.add_argument('--height', type=int, default=700)
    parser.add_argument('--zoom', type=float, help='Explicit viewport pixels per image pixel; omitted=fit')
    args = parser.parse_args()
    if not 1 <= args.width <= 8192 or not 1 <= args.height <= 8192:
        parser.error('Viewport dimensions must be 1–8192 pixels.')
    gate = GenerationGate()
    result = decode(gate.request(args.image, args.frame), gate)
    image = rotate_view(result.image, args.rotate // 90)
    output = {'scope': 'CPU decoder and viewport raster only; no native GUI/hardware proof',
              'filename': args.image.name, 'detected_format': result.format,
              'normalized_width': result.image.width, 'normalized_height': result.image.height,
              'display_width': image.width, 'display_height': image.height,
              'frames': result.frames, 'frame_index': args.frame,
              'animated': result.animated, 'duration_ms': result.duration_ms,
              'colour_warning': result.warning}
    if args.render:
        view = View(args.width, args.height)
        view.fit(image.size)
        if args.zoom is not None:
            view.zoom_at(args.zoom / view.zoom, (args.width / 2, args.height / 2), image.size)
        tile = render_tile(image, view)
        if tile is None:
            raise ValueError('No pixels intersect the requested viewport.')
        save_png_copy(result.ticket.path, tile[0], args.render)
        output.update(render_filename=args.render.name, render_width=tile[0].width,
                      render_height=tile[0].height, canvas_position=tile[1], zoom=view.zoom)
    print(json.dumps(output))
    return 0


if __name__ == '__main__':
    try:
        raise SystemExit(main())
    except (OSError, ValueError) as error:
        print(f'Image Viewer: {error}', file=sys.stderr)
        raise SystemExit(1)
