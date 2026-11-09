"""Generate a multi-resolution PCasa Windows icon: house with a smaller pi.
The generated ICO is used by the EXE and Inno Setup; no external image is fetched.
"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

DEST = Path(__file__).resolve().parents[1] / "src" / "PCasa.App" / "Assets" / "PCasa.ico"
FONT_CANDIDATES = [
    Path("C:/Windows/Fonts/arial.ttf"),
    Path("C:/Windows/Fonts/times.ttf"),
    Path("/usr/share/fonts/truetype/dejavu/DejaVuSerif.ttf"),
]

def render(size=256):
    scale = 4
    s = size * scale
    im = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    def p(x, y): return (round(x*s), round(y*s))
    # The house is visibly dominant; the pi is intentionally smaller.
    blue, dark, white = "#187bc2", "#14578a", "#f7fbff"
    d.polygon([p(.16,.46), p(.5,.16), p(.84,.46), p(.77,.52), p(.5,.28), p(.23,.52)], fill=blue)
    d.rounded_rectangle([*p(.25,.45), *p(.75,.85)], radius=int(.035*s), fill=blue)
    d.rectangle([*p(.33,.51), *p(.67,.79)], fill=white)
    d.polygon([p(.33,.51), p(.5,.36), p(.67,.51)], fill=white)
    font_path = next((str(f) for f in FONT_CANDIDATES if f.exists()), None)
    font = ImageFont.truetype(font_path, int(.29*s)) if font_path else ImageFont.load_default()
    d.text(p(.50,.62), "π", font=font, anchor="mm", fill=dark)
    return im.resize((size,size), Image.Resampling.LANCZOS)

if __name__ == "__main__":
    DEST.parent.mkdir(parents=True, exist_ok=True)
    image = render()
    image.save(DEST, format="ICO", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
    print(f"Generated {DEST} ({DEST.stat().st_size} bytes)")
