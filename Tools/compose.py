"""
Builds the labelled collages, detail crops and comparison images used by README.md from a showcase run.

Usage:  python compose.py <run_dir> <out_dir>
Reads   <run_dir>/results.json and the PNGs next to it (plus <run_dir>/ext/*.png desktop captures).
Writes  <out_dir>/*.jpg|png and <out_dir>/metrics.json
"""
import json
import os
import sys
import math
from PIL import Image, ImageDraw, ImageFont
import numpy as np

RUN = sys.argv[1]
OUT = sys.argv[2]
os.makedirs(OUT, exist_ok=True)
R = json.load(open(os.path.join(RUN, "results.json"), encoding="utf-8-sig"))
E = {e["id"]: e for e in R["entries"]}

BG = (24, 14, 10)
INK = (247, 232, 212)
MUTED = (184, 158, 136)
ACC = (242, 166, 90)
BAD = (235, 110, 90)


def font(size, bold=False):
    for name in (("segoeuib.ttf" if bold else "segoeui.ttf"), "arialbd.ttf" if bold else "arial.ttf"):
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def load(id_):
    e = E.get(id_)
    if not e or not e.get("file"):
        return None
    p = os.path.join(RUN, e["file"])
    return Image.open(p).convert("RGB") if os.path.exists(p) else None


def caption(im, lines, size=15, pad=8, color=INK, bg=(0, 0, 0, 170), where="bottom"):
    """Draws a translucent caption box with one or more lines onto a copy of im."""
    im = im.convert("RGBA")
    ov = Image.new("RGBA", im.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(ov)
    f = font(size, True)
    fs = font(size - 2)
    h = pad * 2 + sum((size + 4) for _ in lines)
    y0 = im.height - h if where == "bottom" else 0
    d.rectangle([0, y0, im.width, y0 + h], fill=bg)
    y = y0 + pad
    for i, ln in enumerate(lines):
        d.text((pad, y), ln, font=f if i == 0 else fs, fill=color if i == 0 else (225, 210, 190))
        y += size + 4
    return Image.alpha_composite(im, ov).convert("RGB")


def psnr(a, b):
    a = np.asarray(a, dtype=np.float64)
    b = np.asarray(b, dtype=np.float64)
    if a.shape != b.shape:
        return float("nan")
    mse = np.mean((a - b) ** 2)
    return 99.0 if mse == 0 else 10 * math.log10(255.0 ** 2 / mse)


def label_for(e, short=False):
    prov = e.get("providerInUse") or ""
    req = e.get("requestedProvider", "")
    q = e.get("quality", "")
    res = f'{e.get("renderRes","")}→{e.get("displayRes","")}'
    fps = e.get("avgFps", 0)
    return prov, req, q, res, fps


def tile(id_, w, lines):
    im = load(id_)
    if im is None:
        im = Image.new("RGB", (w, int(w * 9 / 16)), (60, 30, 30))
        ImageDraw.Draw(im).text((10, 10), "missing: " + id_, fill=INK, font=font(14))
        return im
    im = im.resize((w, int(w * im.height / im.width)), Image.LANCZOS)
    return caption(im, lines, size=15)


metrics = {}


def matrix(pname, title):
    quals = ["Native AA", "Quality", "Balanced", "Performance", "Ultra Performance"]
    gens = [(4, "FSR 4 requested"), (3, "FSR 3.1"), (2, "FSR 2.3")]
    tw = 560
    th = int(tw * 9 / 16)
    ref = load(f"matrix_{pname}_native_taa")
    left, top = 150, 120
    W = left + tw * 5 + 6 * 5
    H = top + (th + 6) * 3 + 6 + 40
    C = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(C)
    d.text((24, 22), title, font=font(30, True), fill=INK)
    d.text((24, 62), "Oasis scene, 1920x1080 output, camera static, 100+ frames of temporal convergence before capture.  PSNR is measured against native resolution with TAA.",
           font=font(15), fill=MUTED)
    for qi, q in enumerate(quals):
        d.text((left + qi * (tw + 6) + 8, top - 30), q, font=font(20, True), fill=ACC)
    for gi, (g, gname) in enumerate(gens):
        y = top + gi * (th + 6)
        d.text((14, y + th // 2 - 24), gname.split(" ")[0] + " " + gname.split(" ")[1], font=font(20, True), fill=INK)
        d.text((14, y + th // 2 + 2), gname.split(" ", 2)[2] if len(gname.split(" ")) > 2 else "", font=font(14), fill=MUTED)
        for qi in range(5):
            id_ = f"matrix_{pname}_fsr{g}_{qi}"
            e = E.get(id_, {})
            prov, req, q, res, fps = label_for(e)
            im = load(id_)
            p = psnr(im, ref) if (im is not None and ref is not None) else float("nan")
            metrics[id_] = {"providerInUse": prov, "render": e.get("renderRes"), "display": e.get("displayRes"), "fps": round(fps, 1), "psnrVsNative": None if math.isnan(p) else round(p, 2)}
            lines = [f"{('FSR ' + prov) if prov else req}  |  {res}", f"{fps:.0f} fps  |  PSNR {p:.1f} dB" if not math.isnan(p) else f"{fps:.0f} fps"]
            C.paste(tile(id_, tw, lines), (left + qi * (tw + 6), y))
    C.save(os.path.join(OUT, f"matrix_{pname}.jpg"), quality=90)

    # native baseline + legacy upscalers strip
    ids = [(f"matrix_{pname}_native_taa", "Native (TAA)")]
    for k, sc in [("STP", 67), ("STP", 50), ("FSR1Spatial", 67), ("FSR1Spatial", 50), ("Bilinear", 50)]:
        ids.append((f"matrix_{pname}_legacy_{k}_{sc}", {"STP": "Unity STP", "FSR1Spatial": "FSR 1.0 (spatial)", "Bilinear": "Bilinear"}[k] + f" @ {sc}%"))
    tw2 = 620
    th2 = int(tw2 * 9 / 16)
    S = Image.new("RGB", (tw2 * 3 + 12, th2 * 2 + 6), BG)
    for i, (id_, name) in enumerate(ids):
        e = E.get(id_, {})
        im = load(id_)
        p = psnr(im, ref) if (im is not None and ref is not None and id_ != ids[0][0]) else float("nan")
        metrics[id_] = {"fps": round(e.get("avgFps", 0), 1), "render": e.get("renderRes"), "psnrVsNative": None if math.isnan(p) else round(p, 2)}
        lines = [name, f'{e.get("renderRes","")}→{e.get("displayRes","")}  |  {e.get("avgFps",0):.0f} fps' + (f"  |  PSNR {p:.1f} dB" if not math.isnan(p) else "")]
        S.paste(tile(id_, tw2, lines), ((i % 3) * (tw2 + 6), (i // 3) * (th2 + 6)))
    S.save(os.path.join(OUT, f"legacy_{pname}.jpg"), quality=90)


def detail(pname, box, title, scale=4):
    """Zoomed crops of the same region across configurations (nearest neighbour so pixels stay honest)."""
    sets = [
        ("Native (TAA)", f"matrix_{pname}_native_taa"),
        ("FSR 3.1 Quality", f"matrix_{pname}_fsr3_1"),
        ("FSR 3.1 Balanced", f"matrix_{pname}_fsr3_2"),
        ("FSR 3.1 Performance", f"matrix_{pname}_fsr3_3"),
        ("FSR 3.1 Ultra Perf.", f"matrix_{pname}_fsr3_4"),
        ("FSR 2.3 Performance", f"matrix_{pname}_fsr2_3"),
        ("FSR 2.3 Ultra Perf.", f"matrix_{pname}_fsr2_4"),
        ("STP @ 50%", f"matrix_{pname}_legacy_STP_50"),
        ("FSR 1.0 @ 50%", f"matrix_{pname}_legacy_FSR1Spatial_50"),
        ("Bilinear @ 50%", f"matrix_{pname}_legacy_Bilinear_50"),
    ]
    x, y, w, h = box
    cw, ch = w * scale // 2, h * scale // 2
    cols = 5
    rows = 2
    Cn = Image.new("RGB", (cols * (cw + 6), 70 + rows * (ch + 6)), BG)
    ImageDraw.Draw(Cn).text((14, 16), title, font=font(26, True), fill=INK)
    ImageDraw.Draw(Cn).text((14, 48), f"crop {w}x{h} px at ({x},{y}), shown {scale / 2:.0f}x", font=font(14), fill=MUTED)
    for i, (name, id_) in enumerate(sets):
        im = load(id_)
        if im is None:
            continue
        c = im.crop((x, y, x + w, y + h)).resize((cw, ch), Image.NEAREST)
        c = caption(c, [name], size=17)
        Cn.paste(c, ((i % cols) * (cw + 6), 70 + (i // cols) * (ch + 6)))
    Cn.save(os.path.join(OUT, f"detail_{pname}.jpg"), quality=92)


def wipe(a_id, b_id, name_a, name_b, out_name, frac=0.5):
    a, b = load(a_id), load(b_id)
    if a is None or b is None:
        return
    x = int(a.width * frac)
    im = a.copy()
    im.paste(b.crop((x, 0, b.width, b.height)), (x, 0))
    d = ImageDraw.Draw(im)
    d.rectangle([x - 2, 0, x + 2, im.height], fill=ACC)
    im = caption(im, [f"{name_a}   |   {name_b}"], size=26, where="top")
    im.resize((im.width * 3 // 4, im.height * 3 // 4), Image.LANCZOS).save(os.path.join(OUT, out_name), quality=92)


def split_screen():
    ids = [("split_fsr3_q1", "Quality"), ("split_fsr3_q3", "Performance"), ("split_fsr3_q4", "Ultra Performance"), ("split_stp_067", "STP 67% (control)")]
    tw = 940
    th = int(tw * 9 / 16)
    Cn = Image.new("RGB", (tw * 2 + 6, th * 2 + 6), BG)
    for i, (id_, name) in enumerate(ids):
        e = E.get(id_, {})
        lines = [f"Split-screen, FSR 3.1 {name}" if "fsr3" in id_ else f"Split-screen, {name}", (e.get("notes") or "")[:140]]
        Cn.paste(tile(id_, tw, lines), ((i % 2) * (tw + 6), (i // 2) * (th + 6)))
    Cn.save(os.path.join(OUT, "splitscreen.jpg"), quality=90)


def hud_grid():
    kinds = [("OverlayUIToolkit", "Overlay UI (UI Toolkit)"), ("ScreenSpaceCamera", "Screen-space camera UI"), ("WorldSpace", "World-space UI")]
    tags = [("fsr", "FSR only"), ("fsr_fg", "FSR + frame gen"), ("fsr_fg_hudless", "FSR + frame gen + HUD-less")]
    # crop the HUD region from desktop captures if present, otherwise from internal screenshots
    cw, ch = 700, 300
    Cn = Image.new("RGB", (3 * (cw + 6) + 190, 80 + 3 * (ch + 6)), BG)
    d = ImageDraw.Draw(Cn)
    d.text((14, 16), "HUD ghosting test: animated HUD under FSR and frame generation", font=font(26, True), fill=INK)
    for ti, (t, tn) in enumerate(tags):
        d.text((190 + ti * (cw + 6) + 8, 54), tn, font=font(18, True), fill=ACC)
    for ki, (k, kn) in enumerate(kinds):
        y = 80 + ki * (ch + 6)
        d.text((10, y + ch // 2 - 20), kn, font=font(16, True), fill=INK)
        for ti, (t, tn) in enumerate(tags):
            id_ = f"hud_{k}_{t}"
            im = load(id_)
            if im is None:
                continue
            # HUD sits bottom-left (overlay) - take that region
            crop = im.crop((0, im.height - ch - 20, cw, im.height - 20))
            Cn.paste(crop, (190 + ti * (cw + 6), y))
    Cn.save(os.path.join(OUT, "hud_grid.jpg"), quality=92)


if __name__ == "__main__":
    for pname, title in (("vista", "FSR quality matrix: sunset vista"), ("foliage", "FSR quality matrix: foliage close-up")):
        if f"matrix_{pname}_native_taa" in E:
            matrix(pname, title)
    json.dump(metrics, open(os.path.join(OUT, "metrics.json"), "w"), indent=1)
    print("composed ->", OUT)
