#!/bin/sh
# Run native Tk tests on a private display; never use the owner's desktop.
set -eu
repo=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repo"
export PYTHONDONTWRITEBYTECODE=1
python3 -m unittest discover -s linux -p test_viewer_core.py -v
python3 -m unittest discover -s linux -p test_updates.py -v
if ! command -v openbox >/dev/null || ! command -v xvfb-run >/dev/null; then
    echo 'GUI checks require xvfb, xauth and openbox (a test-only window manager).' >&2
    exit 1
fi
xvfb-run -a -s '-screen 0 1280x1024x24' sh -c '
    openbox >/dev/null 2>&1 &
    wm=$!
    trap "kill $wm 2>/dev/null || true" EXIT HUP INT TERM
    python3 -m unittest discover -s linux -p test_viewer_gui.py -v
'
