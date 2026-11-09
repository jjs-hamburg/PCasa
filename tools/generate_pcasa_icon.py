"""Build Windows ICO from a pixel-sampled copy of the user-approved PCasa logo.

The compressed pixel sample below was made from the supplied artwork, rather
than drawing or synthesizing a different house/pi design.
"""
from pathlib import Path
from PIL import Image
import base64
import zlib

DATA = """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"""
SIZE = 64
COLORS = 64

def render():
    raw = zlib.decompress(base64.b64decode(DATA))
    palette_bytes = COLORS * 3
    count = SIZE * SIZE
    if len(raw) != palette_bytes + 2 * count:
        raise ValueError("Approved logo pixel payload is invalid")
    palette = raw[:palette_bytes]
    indices = raw[palette_bytes:palette_bytes + count]
    alpha = raw[palette_bytes + count:]
    rgb = Image.frombytes("P", (SIZE, SIZE), indices)
    rgb.putpalette(palette)
    rgba = rgb.convert("RGBA")
    rgba.putalpha(Image.frombytes("L", (SIZE, SIZE), alpha))
    return rgba

if __name__ == "__main__":
    destination = Path(__file__).resolve().parents[1] / "src" / "PCasa.App" / "Assets" / "PCasa.ico"
    destination.parent.mkdir(parents=True, exist_ok=True)
    render().save(destination, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64)])
    print(f"Generated approved PCasa logo: {destination}")
