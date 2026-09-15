"""Rasterise assets/winmux.svg into assets/winmux.ico.

The SVG is the source of truth for the mark; this only converts it. Run it after changing the SVG
and commit both, so the icon inside the executable can never disagree with the drawing:

    py assets/build-icon.py

Rendering is done by headless Chrome or Edge — a real browser engine, which every Windows machine
with either browser already has. The obvious Python routes were tried first and all of them end at
a native cairo that Windows does not ship: cairosvg and rlPyCairo both require it, and svglib's
renderPM path fails on rounded rectangles even once cairo is present. A browser needs installing
nothing and renders the SVG exactly as a browser would.

Pillow packs the result into a multi-resolution .ico. It renders once, large, and downsamples with
Lanczos for every size, because rendering each size separately lets the small ones drift from the
shape of the big ones.
"""

import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SVG = os.path.join(HERE, "winmux.svg")
ICO = os.path.join(HERE, "winmux.ico")
PNG = os.path.join(HERE, "winmux-256.png")

# Every size Windows asks for: 16 in list views, 32 on the desktop and in Alt+Tab, 256 for the
# large-icon view. The in-between sizes stop Windows downscaling badly on its own.
SIZES = [16, 24, 32, 48, 64, 128, 256]

# Render far above the largest size so the downsample does the antialiasing.
RENDER = 1024

BROWSERS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    os.path.expandvars(r"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]


def find_browser() -> str | None:
    for path in BROWSERS:
        if os.path.isfile(path):
            return path
    return shutil.which("chrome") or shutil.which("msedge")


def render(browser: str, out_png: str) -> None:
    """Render the SVG on a transparent background at RENDER x RENDER."""
    # A wrapper page rather than the SVG directly: it pins the size and kills the default margin,
    # so the mark fills the frame instead of sitting in a white letterbox.
    with open(SVG, "r", encoding="utf-8") as handle:
        svg = handle.read()

    html = (
        "<!doctype html><meta charset='utf-8'>"
        "<style>html,body{margin:0;padding:0;background:transparent}"
        f"svg{{display:block;width:{RENDER}px;height:{RENDER}px}}</style>"
        + svg
    )

    with tempfile.TemporaryDirectory() as work:
        page = os.path.join(work, "icon.html")
        with open(page, "w", encoding="utf-8") as handle:
            handle.write(html)

        subprocess.run(
            [
                browser,
                "--headless",
                "--disable-gpu",
                "--hide-scrollbars",
                "--default-background-color=00000000",   # transparent, not white
                f"--screenshot={out_png}",
                f"--window-size={RENDER},{RENDER}",
                f"--user-data-dir={os.path.join(work, 'profile')}",
                "file:///" + page.replace("\\", "/"),
            ],
            check=True,
            capture_output=True,
            timeout=120,
        )


def main() -> int:
    browser = find_browser()
    if browser is None:
        print("no Chrome or Edge found to render with", file=sys.stderr)
        return 1

    with tempfile.TemporaryDirectory() as work:
        raw = os.path.join(work, "raw.png")
        render(browser, raw)
        if not os.path.isfile(raw):
            print("the browser produced no image", file=sys.stderr)
            return 1

        with Image.open(raw) as opened:
            master = opened.convert("RGBA")

        master.resize((256, 256), Image.LANCZOS).save(PNG)
        master.save(ICO, format="ICO", sizes=[(size, size) for size in SIZES])

    print(f"rendered with {os.path.basename(browser)}")
    print(f"wrote {ICO} ({', '.join(str(s) for s in SIZES)})")
    print(f"wrote {PNG}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
