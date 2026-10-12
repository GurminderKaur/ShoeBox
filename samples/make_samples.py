"""Generate clearly-labelled SAMPLE receipts (fictional businesses) for testing Shoebox.

Run:  python samples/make_samples.py   (needs Pillow)
Writes JPGs into samples/receipts/ and the expected values into samples/expected.json.
"""
import json, math, os, random
from PIL import Image, ImageDraw, ImageFilter, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "receipts")
os.makedirs(OUT, exist_ok=True)
random.seed(7)

def font(size, bold=False):
    for p in ["/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf" if bold else "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
              "C:/Windows/Fonts/consolab.ttf" if bold else "C:/Windows/Fonts/consola.ttf"]:
        if os.path.exists(p):
            return ImageFont.truetype(p, size)
    return ImageFont.load_default()

RECEIPTS = [
    dict(file="01_fuel.jpg", vendor="BOW VALLEY FUEL CO.", addr=["123 Example Trail NW", "Calgary, AB"], date="2026-10-03", datestr="10/03/2026 07:42",
         lines=[("REGULAR UNLEADED 42.5L @ 1.454", 61.80)], gst=3.09, other=0, tip=0, cat="9281", items_hint="gasoline"),
    dict(file="02_coffee.jpg", vendor="Chinook Coffee House", addr=["45 Sample St SW", "Calgary, AB"], date="2026-10-06", datestr="Oct 6, 2026 9:15 AM",
         lines=[("Latte 16oz", 5.75), ("Latte 16oz", 5.75), ("Blueberry Muffin", 2.75)], gst=0.71, other=0, tip=2.00, cat="8523", items_hint="coffee"),
    dict(file="03_office.jpg", vendor="PRAIRIE OFFICE SUPPLY", addr=["9 Demo Blvd", "Edmonton, AB"], date="2026-09-18", datestr="2026-09-18 14:03",
         lines=[("INK CARTRIDGE BLK XL", 54.99), ("COPY PAPER 5000 SH", 34.98)], gst=4.50, other=0, tip=0, cat="8811", items_hint="ink, paper"),
    dict(file="04_courier.jpg", vendor="Foothills Courier Ltd.", addr=["700 Test Rd SE", "Calgary, AB"], date="2026-09-29", datestr="29 SEP 2026",
         lines=[("Same-day delivery - 2 pkgs", 25.00)], gst=1.25, other=0, tip=0, cat="9275", items_hint="delivery"),
    dict(file="05_bc_print.jpg", vendor="Granville Print Shop", addr=["1 Placeholder Way", "Vancouver, BC"], date="2026-08-14", datestr="Aug 14 2026",
         lines=[("Business cards x500", 100.00)], gst=5.00, other=7.00, tip=0, cat="8521", items_hint="business cards", note="out of province: PST"),
    dict(file="06_groceries.jpg", vendor="KENSINGTON MARKET GROCERY", addr=["88 Fictional Ave NW", "Calgary, AB"], date="2026-10-08", datestr="2026/10/08",
         lines=[("MILK 4L", 6.49), ("BREAD", 3.99), ("APPLES 2.1KG", 8.42), ("CHICKEN BREAST", 18.50)], gst=0.00, other=0, tip=0, cat="personal", items_hint="groceries"),
]

def draw_receipt(spec):
    W = 560
    sub = round(sum(p for _, p in spec["lines"]), 2)
    total = round(sub + spec["gst"] + spec["other"] + spec["tip"], 2)
    spec["subtotal"], spec["total"] = sub, total
    rows = []
    rows += [("C", spec["vendor"], 30, True)] + [("C", a, 20, False) for a in spec["addr"]] + [("C", "GST# 00000 0000 RT0001", 18, False), ("-",)]
    rows += [("L", spec["datestr"], 20, False), ("-",)]
    rows += [("LR", n, f"{p:.2f}") for n, p in spec["lines"]]
    rows += [("-",), ("LR", "SUBTOTAL", f"{sub:.2f}")]
    if spec["gst"] or spec["other"] == 0:
        rows += [("LR", "GST 5%", f"{spec['gst']:.2f}")]
    if spec["other"]:
        rows += [("LR", "PST 7%", f"{spec['other']:.2f}")]
    if spec["tip"]:
        rows += [("LR", "TIP", f"{spec['tip']:.2f}")]
    rows += [("LRB", "TOTAL", f"${total:.2f}"), ("L", "VISA ****1234   APPROVED", 18, False), ("-",),
             ("C", "Thank you!", 20, False), ("C", "*** SAMPLE RECEIPT - FOR TESTING ***", 16, True), ("C", "fictional business", 16, False)]

    H = 70 + sum(36 if r[0] != "-" else 22 for r in rows)
    img = Image.new("L", (W, H), 250)
    d = ImageDraw.Draw(img)
    y = 30
    f_body, f_bold = font(22), font(24, True)
    for r in rows:
        if r[0] == "-":
            d.line([(30, y + 10), (W - 30, y + 10)], fill=120, width=2); y += 22; continue
        if r[0] == "C":
            f = font(r[2], r[3]); w = d.textlength(r[1], font=f); d.text(((W - w) / 2, y), r[1], font=f, fill=25)
        elif r[0] == "L":
            d.text((30, y), r[1], font=font(r[2], r[3]), fill=25)
        else:
            f = f_bold if r[0] == "LRB" else f_body
            d.text((30, y), r[1], font=f, fill=25)
            w = d.textlength(r[2], font=f); d.text((W - 30 - w, y), r[2], font=f, fill=25)
        y += 36

    # Make it look photographed: thermal fade, slight blur, rotation, background, perspective-free noise.
    img = img.filter(ImageFilter.GaussianBlur(0.6))
    noise = Image.effect_noise((W, H), 12).convert("L")
    img = Image.blend(img, noise, 0.06)
    angle = random.uniform(-3, 3)
    bg = Image.new("RGB", (W + 160, H + 160), (96, 84, 72))
    paper = img.convert("RGB").rotate(angle, expand=True, fillcolor=(96, 84, 72))
    bg.paste(paper, (80 - (paper.width - W) // 2, 80 - (paper.height - H) // 2))
    return bg

expected = []
for s in RECEIPTS:
    im = draw_receipt(s)
    im.save(os.path.join(OUT, s["file"]), quality=88)
    expected.append({k: s[k] for k in ["file", "vendor", "date", "subtotal", "gst", "other", "tip", "total", "cat"]})
json.dump(expected, open(os.path.join(HERE, "expected.json"), "w"), indent=2)
print("wrote", len(expected), "sample receipts to", OUT)
