"""
HUD ghosting comparison sheet.

Usage:  python ghosting.py <run_dir> <out_file.jpg>

For each HUD type of the stress test it crops the HUD out of
  - the in-engine screenshot taken with FSR only (the reference: no generated frames),
  - three desktop captures with frame generation on,
  - three desktop captures with frame generation + HUD-less input.
Desktop captures are used for frame generation because generated frames only exist in the swapchain.
Frames are shown in capture order; a generated frame next to a rendered one is what exposes ghosting (a doubled or smeared ball,
marker, dial or ticker text).
"""
import os
import sys
from PIL import Image, ImageDraw, ImageFont

RUN, OUT = sys.argv[1], sys.argv[2]
BG, INK, MUTED, ACC = (24, 14, 10), (247, 232, 212), (184, 158, 136), (242, 166, 90)
KINDS = [
    ("OverlayUIToolkit", "Overlay UI (UI Toolkit)", (0, 780, 740, 1070)),
    ("ScreenSpaceCamera", "Screen-space camera UI", (0, 780, 740, 1070)),
    ("WorldSpace", "World-space UI", (60, 670, 960, 1070)),
]


def font(size, bold=False):
    for n in (("segoeuib.ttf" if bold else "segoeui.ttf"), "arial.ttf"):
        try:
            return ImageFont.truetype(n, size)
        except OSError:
            pass
    return ImageFont.load_default()


def crop(path, box, w):
    if not os.path.exists(path):
        return Image.new("RGB", (w, int(w * (box[3] - box[1]) / (box[2] - box[0]))), (60, 30, 30))
    im = Image.open(path).convert("RGB").crop(box)
    return im.resize((w, int(w * im.height / im.width)), Image.LANCZOS)


tw = 420
cols = [("FSR only (reference)", None, 1), ("FSR + frame generation", "fsr_fg", 3), ("FSR + frame generation + HUD-less", "fsr_fg_hudless", 3)]
# pick captures spread over the sequence so consecutive picks are different frames
picks = [1, 3, 5]
rows = []
for kid, kname, box in KINDS:
    h = int(tw * (box[3] - box[1]) / (box[2] - box[0]))
    rows.append((kid, kname, box, h))

W = 200 + sum(c[2] for c in cols) * (tw + 4) + 20
H = 110 + sum(r[3] + 8 for r in rows)
C = Image.new("RGB", (W, H), BG)
d = ImageDraw.Draw(C)
d.text((16, 14), "HUD ghosting test: animated HUD under FSR and frame generation", font=font(26, True), fill=INK)
d.text((16, 50), "Crops of the HUD stress panel. Frame-generation columns are desktop captures (they include generated frames); three different frames per column.", font=font(14), fill=MUTED)
x = 200
for label, tag, n in cols:
    d.text((x + 6, 82), label, font=font(16, True), fill=ACC)
    x += n * (tw + 4)

y = 110
for kid, kname, box, h in rows:
    d.text((10, y + h // 2 - 20), kname, font=font(16, True), fill=INK)
    x = 200
    for label, tag, n in cols:
        for i in range(n):
            if tag is None:
                p = os.path.join(RUN, f"hud_{kid}_fsr.png")
            else:
                p = os.path.join(RUN, "ext", f"hud_{kid}_{tag}_desktop_{picks[i]:02d}.png")
            C.paste(crop(p, box, tw), (x, y))
            x += tw + 4
    y += h + 8
C.save(OUT, quality=92)
print("wrote", OUT)
