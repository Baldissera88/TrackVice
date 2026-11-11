# Renders icons.py and writes TrackVice.ico and docs/images/logo.png.
# Needs Python with Pillow and rsvg-convert (librsvg). Run from the repository root: python3 art/build.py
import os
import subprocess
import sys

from PIL import Image

here = os.path.dirname(os.path.abspath(__file__))
root = os.path.dirname(here)
sys.path.insert(0, here)
import icons

out = os.path.join(here, 'png')
os.makedirs(out, exist_ok=True)
sizes = [16, 20, 24, 32, 40, 48, 64, 256, 512]
for s in sizes:
    svg = os.path.join(out, 'app-%d.svg' % s)
    with open(svg, 'w') as f:
        f.write(icons.app(s))
    subprocess.check_call(['rsvg-convert', '-w', str(s), '-h', str(s), '-o', os.path.join(out, 'app-%d.png' % s), svg])

ico = [256, 64, 48, 40, 32, 24, 20, 16]
images = [Image.open(os.path.join(out, 'app-%d.png' % s)).convert('RGBA') for s in ico]
images[0].save(os.path.join(root, 'TrackVice.ico'), format='ICO', sizes=[im.size for im in images], append_images=images[1:])
os.makedirs(os.path.join(root, 'docs', 'images'), exist_ok=True)
Image.open(os.path.join(out, 'app-512.png')).save(os.path.join(root, 'docs', 'images', 'logo.png'))
print('Wrote TrackVice.ico and docs/images/logo.png')
