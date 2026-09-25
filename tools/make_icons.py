"""Generates the ribbon icons (32px + 16px PNG) into Icons/. Run: python tools/make_icons.py
Flat style: rounded square in the panel colour, white glyph. Glyphs are drawn as shapes or short text."""
import math, os
from PIL import Image, ImageDraw, ImageFont

OUT = os.path.join(os.path.dirname(__file__), '..', 'Icons')
os.makedirs(OUT, exist_ok=True)
S = 128            # draw big, downscale for crisp edges
FONT = 'C:/Windows/Fonts/segoeuib.ttf'

COL = {
    'setup': (84, 110, 138),      # slate
    'mech': (222, 120, 40),       # orange
    'plumb': (36, 120, 200),      # blue
    'plan': (120, 80, 180),       # purple
    'riser': (40, 150, 100),      # green
    'check': (200, 60, 60),       # red
    'doc': (30, 150, 160),        # teal
}
W = (255, 255, 255)

def base(color):
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((4, 4, S - 4, S - 4), radius=26, fill=color)
    return im, d

def text(d, s, size=64, y=None):
    f = ImageFont.truetype(FONT, size)
    bb = d.textbbox((0, 0), s, font=f)
    w, h = bb[2] - bb[0], bb[3] - bb[1]
    x = (S - w) / 2 - bb[0]
    yy = (S - h) / 2 - bb[1] if y is None else y - bb[1]
    d.text((x, yy), s, font=f, fill=W)

def line(d, pts, w=10):
    d.line(pts, fill=W, width=w, joint='curve')

def arrow_down(d, x, y0, y1, w=10, head=18):
    line(d, [(x, y0), (x, y1)], w)
    d.polygon([(x - head, y1 - head), (x + head, y1 - head), (x, y1 + 4)], fill=W)

def arrow_up(d, x, y0, y1, w=10, head=18):
    line(d, [(x, y0), (x, y1)], w)
    d.polygon([(x - head, y1 + head), (x + head, y1 + head), (x, y1 - 4)], fill=W)

def ring(d, cx, cy, r, w=10):
    d.ellipse((cx - r, cy - r, cx + r, cy + r), outline=W, width=w)

def save(im, name):
    for px in (32, 16):
        im.resize((px, px), Image.LANCZOS).save(os.path.join(OUT, f'{name}{px}.png'))

icons = {}

# ---------------- Setup
def workflow():
    im, d = base(COL['setup'])
    for i, y in enumerate((36, 64, 92)):
        line(d, [(24, y), (44, y + 14), (58, y - 16)], 8) if i < 2 else ring(d, 40, y, 10, 8)
        line(d, [(72, y), (104, y)], 10)
    return im
icons['Workflow'] = workflow

def project_setup():
    im, d = base(COL['setup'])
    for y, k in ((40, 50), (64, 84), (88, 62)):
        line(d, [(24, y), (104, y)], 8)
        d.ellipse((k - 11, y - 11, k + 11, y + 11), fill=W)
    return im
icons['ProjectSetup'] = project_setup

def reload_rules():
    im, d = base(COL['setup'])
    d.arc((28, 28, 100, 100), start=300, end=240, fill=W, width=12)
    d.polygon([(96, 22), (112, 54), (78, 50)], fill=W)
    return im
icons['ReloadRules'] = reload_rules

def edit_rules():
    im, d = base(COL['setup'])
    d.polygon([(34, 94), (82, 32), (100, 46), (52, 108)], fill=W)
    d.polygon([(28, 112), (34, 94), (52, 108)], fill=W)
    return im
icons['EditRules'] = edit_rules

def map_families():
    im, d = base(COL['setup'])
    d.rectangle((22, 30, 58, 62), outline=W, width=8)
    d.rectangle((70, 66, 106, 98), outline=W, width=8)
    line(d, [(58, 46), (88, 46), (88, 66)], 8)
    return im
icons['MapFamilies'] = map_families

def test_families():
    im, d = base(COL['setup'])
    d.rectangle((26, 40, 74, 88), outline=W, width=8)
    line(d, [(90, 46), (90, 82)], 10); line(d, [(72, 64), (108, 64)], 10)
    return im
icons['TestFamilies'] = test_families

def adopt():
    # existing sleeve (ring) getting the add-in's stamp (tick)
    im, d = base(COL['setup'])
    ring(d, 50, 64, 26, 9)
    line(d, [(74, 74), (88, 90), (112, 46)], 10)
    return im
icons['Adopt'] = adopt

def export_key():
    # sleeve (ring) exported to a sheet (arrow out)
    im, d = base(COL['setup'])
    ring(d, 44, 64, 22, 9)
    line(d, [(72, 64), (110, 64)], 10)
    d.polygon([(112, 64), (94, 48), (94, 80)], fill=W)
    return im
icons['ExportKey'] = export_key

def auto_run():
    # drawing sheet feeding a sleeve (ring): sheet with a play arrow
    im, d = base(COL['plan'])
    d.rectangle((22, 26, 70, 102), outline=W, width=8)
    d.polygon([(80, 40), (112, 64), (80, 88)], fill=W)
    line(d, [(32, 48), (60, 48)], 6)
    line(d, [(32, 64), (60, 64)], 6)
    line(d, [(32, 80), (52, 80)], 6)
    return im
icons['AutoRun'] = auto_run

# ---------------- Place – Mechanical
def exhaust():
    im, d = base(COL['mech'])
    d.rectangle((26, 26, 102, 102), outline=W, width=8)
    arrow_up(d, 64, 92, 44)
    return im
icons['Exhaust'] = exhaust

def chute():
    im, d = base(COL['mech'])
    d.rectangle((26, 26, 102, 102), outline=W, width=8)
    text(d, 'GC', 44)
    return im
icons['GarbageChute'] = chute

def dryer():
    im, d = base(COL['mech'])
    ring(d, 64, 64, 40, 8)
    text(d, 'DE', 40)
    return im
icons['DryerExhaust'] = dryer

def damper():
    im, d = base(COL['mech'])
    d.rectangle((26, 26, 102, 102), outline=W, width=8)
    for y in (46, 64, 82):
        line(d, [(38, y + 8), (90, y - 8)], 7)
    return im
icons['Damper'] = damper

def snowflake(d, cx=64, cy=64, r=38):
    for k in range(3):
        a = math.radians(k * 60)
        line(d, [(cx - r * math.cos(a), cy - r * math.sin(a)), (cx + r * math.cos(a), cy + r * math.sin(a))], 8)

def refrigeration():
    im, d = base(COL['mech'])
    snowflake(d)
    return im
icons['Refrigeration'] = refrigeration

# ---------------- Place – Plumbing / FP
def drop(d, cx, cy, r):
    d.polygon([(cx, cy - r * 1.6), (cx + r, cy), (cx - r, cy)], fill=W)
    d.ellipse((cx - r, cy - r * 0.6, cx + r, cy + r * 0.8), fill=W)

def storm():
    im, d = base(COL['plumb'])
    ring(d, 64, 64, 42, 8)
    drop(d, 64, 66, 16)
    return im
icons['Storm'] = storm

def area_drain():
    im, d = base(COL['plumb'])
    ring(d, 40, 64, 24, 8); ring(d, 88, 64, 24, 8)
    return im
icons['AreaDrain'] = area_drain

def condensate():
    im, d = base(COL['plumb'])
    ring(d, 64, 64, 42, 8)
    text(d, '3"', 40)
    return im
icons['Condensate'] = condensate

def standpipe():
    im, d = base(COL['plumb'])
    ring(d, 64, 64, 42, 8)
    text(d, 'SP', 38)
    return im
icons['Standpipe'] = standpipe

def bathtub():
    im, d = base(COL['plumb'])
    d.rounded_rectangle((22, 56, 106, 96), radius=16, fill=W)
    d.rounded_rectangle((34, 66, 94, 88), radius=10, fill=COL['plumb'])
    line(d, [(34, 56), (34, 34), (52, 34)], 8)
    return im
icons['Bathtub'] = bathtub

# ---------------- Plan
def electrical():
    im, d = base(COL['plan'])
    d.polygon([(70, 18), (36, 70), (62, 70), (54, 110), (94, 54), (68, 54)], fill=W)
    return im
icons['Electrical'] = electrical

def refrig_plan():
    im, d = base(COL['plan'])
    snowflake(d, 46, 64, 26)
    for y in (40, 64, 88):
        line(d, [(84, y), (108, y)], 8)
    return im
icons['RefrigPlan'] = refrig_plan

def roof():
    im, d = base(COL['plan'])
    d.polygon([(64, 24), (112, 68), (98, 68), (98, 104), (30, 104), (30, 68), (16, 68)], fill=W)
    d.rectangle((54, 74, 74, 104), fill=COL['plan'])
    return im
icons['Roof'] = roof

# ---------------- Risers
def propagate():
    im, d = base(COL['riser'])
    for y in (30, 58, 86):
        line(d, [(22, y), (78, y)], 8)
    arrow_down(d, 98, 26, 96)
    return im
icons['Propagate'] = propagate

def riser_manager():
    im, d = base(COL['riser'])
    for y in (32, 64, 96):
        line(d, [(22, y), (106, y)], 7)
    line(d, [(64, 18), (64, 110)], 12)
    return im
icons['RiserManager'] = riser_manager

# ---------------- Check
def final_check():
    im, d = base(COL['check'])
    d.polygon([(64, 16), (108, 32), (104, 78), (64, 112), (24, 78), (20, 32)], fill=W)
    line(d, [(42, 64), (58, 80), (88, 46)], 11)
    d2 = ImageDraw.Draw(im)
    d2.line([(42, 64), (58, 80), (88, 46)], fill=COL['check'], width=11, joint='curve')
    return im
icons['FinalCheck'] = final_check

# ---------------- Document
def schedule():
    im, d = base(COL['doc'])
    d.rectangle((22, 26, 106, 102), outline=W, width=7)
    for y in (50, 76): line(d, [(22, y), (106, y)], 6)
    line(d, [(56, 26), (56, 102)], 6)
    return im
icons['Schedule'] = schedule

def tag():
    im, d = base(COL['doc'])
    d.polygon([(24, 24), (76, 24), (108, 64), (76, 104), (24, 104)], fill=W)
    d.ellipse((36, 54, 56, 74), fill=COL['doc'])
    return im
icons['Tag'] = tag

def sync():
    im, d = base(COL['doc'])
    d.arc((28, 28, 100, 100), start=200, end=340, fill=W, width=12)
    d.arc((28, 28, 100, 100), start=20, end=160, fill=W, width=12)
    d.polygon([(100, 40), (108, 70), (80, 58)], fill=W)
    d.polygon([(28, 88), (20, 58), (48, 70)], fill=W)
    return im
icons['SyncParams'] = sync

for name, fn in icons.items():
    save(fn(), name)
print(f'{len(icons)} icons -> {os.path.abspath(OUT)}')
