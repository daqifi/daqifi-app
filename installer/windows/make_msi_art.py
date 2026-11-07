"""Regenerates the MSI wizard art in this folder from daqifi-design-tokens.

    python3 installer/windows/make_msi_art.py <path to daqifi-design-tokens> installer/windows

Generated at daqifi-design-tokens e3009eb. Sources, all under that repo's assets/:
  app-icons/master/glyph-traces-transparent-1024.png  - banner mark
  logos/logo-primary-light.png                         - welcome/finish logo
  app-icons/master/mark-only-1024.png                  - the colour-wave band

Sizes are the WiX ones: banner 493x58, dialog background 493x312. The wizard draws its own
text over both (titles at the left of the banner, body text right of x=164 on the dialog),
so the art stays out of those areas. The finish page's "Launch" checkbox paints an opaque
box that cannot be styled without forking the dialog templates, which is why the waves
fade out before x=164 rather than running full width.
"""
import sys
from PIL import Image
T = sys.argv[1]; out = sys.argv[2]
glyph = Image.open(f'{T}/assets/app-icons/master/glyph-traces-transparent-1024.png').convert('RGBA')
glyph = glyph.crop(glyph.getbbox())
logo = Image.open(f'{T}/assets/logos/logo-primary-light.png').convert('RGBA')
logo = logo.crop(logo.getbbox())
mark = Image.open(f'{T}/assets/app-icons/master/mark-only-1024.png').convert('RGB')
waves = mark.crop((0, 600, 1024, 1024))  # the colour-wave band under the traces

def fit(im, w=None, h=None):
    if w and not h: h = round(im.height * w / im.width)
    if h and not w: w = round(im.width * h / im.height)
    return im.resize((w, h), Image.LANCZOS)

# Top banner, 493x58: WiX draws the dialog title over the left; keep it white, mark on the right.
b = Image.new('RGB', (493, 58), 'white')
g = fit(glyph, h=26)
b.paste(g, (493 - g.width - 14, (58 - g.height) // 2), g)
b.save(f'{out}/msi-top-banner.bmp')

# Welcome/finish background, 493x312: text sits right of x=164, so art stays in the left panel.
d = Image.new('RGB', (493, 312), 'white')
l = fit(logo, w=124)
d.paste(l, ((164 - l.width) // 2, 34), l)

# The wave band stays inside the left panel and fades out before x=164: the finish dialog's
# "Launch" checkbox paints an opaque box over the right side, so nothing can sit behind it.
band = fit(waves, h=130).crop((0, 0, 164, 130))
fade = Image.new('L', band.size, 255)
for x in range(110, 164):
    a = round(255 * (164 - x) / 54)
    for y in range(band.height):
        fade.putpixel((x, y), a)
d.paste(band, (0, 312 - band.height), fade)
d.save(f'{out}/msi-dialog-background.bmp')
print(b.size, d.size)
