# Makes the Taskbar Glance icon: the tile of Claude's app icon (shape, dark base, glow) in blue (Claude Taskbar's tile)
# with a white sun and cloud.
# Input: claude.exe's 256 px icon saved as PNG (only its outline is used).
# Run from the repo root: python assets/make_icon.py claude.png src/app.ico 215 ; python assets/make_icon.py claude.png assets/icon.png 215
import sys, math, colorsys
from PIL import Image, ImageDraw, ImageFilter, ImageChops

ref = Image.open(sys.argv[1]).convert('RGBA'); out = sys.argv[2]; hue = float(sys.argv[3]) / 360
S = 4; W = 256 * S
mask = ref.split()[3].resize((W, W), Image.LANCZOS)

def tint(rgb):  # move a dark brown from Claude's tile to the target hue, keeping saturation and value
    h, s, v = colorsys.rgb_to_hsv(*(c / 255 for c in rgb)); r, g, b = colorsys.hsv_to_rgb(hue, s, v)
    return (round(r * 255), round(g * 255), round(b * 255))

base, glow = tint((19, 11, 5)), tint((84, 46, 31))
bg = Image.new('RGBA', (W, W), base + (255,))
gl = Image.new('RGBA', (W, W), (0, 0, 0, 0))
ImageDraw.Draw(gl).ellipse([int(W*0.0), int(W*0.58), int(W*1.0), int(W*1.35)], fill=glow + (255,))
bg = Image.alpha_composite(bg, gl.filter(ImageFilter.GaussianBlur(W * 0.12)))
im = Image.new('RGBA', (W, W), (0, 0, 0, 0)); im.paste(bg, (0, 0), mask)

GLYPH = (255, 255, 255, 255)
SCALE = 0.86  # glyph size relative to the drawing below, shrunk around its centre
sun = Image.new('L', (W, W), 0); d = ImageDraw.Draw(sun)
DX, DY = -0.035, 0.05
cx, cy, sr = W*(0.42+DX), W*(0.40+DY), W*0.115
d.ellipse([cx-sr, cy-sr, cx+sr, cy+sr], fill=255)
for k in (3, 4, 5, 6, 7):  # only the rays that clear the cloud (left, top, top right)
    a = math.radians(k * 45)
    d.line([cx + math.cos(a)*sr*1.45, cy + math.sin(a)*sr*1.45, cx + math.cos(a)*sr*1.95, cy + math.sin(a)*sr*1.95],
           fill=255, width=int(W*0.04))
cloud = Image.new('L', (W, W), 0); c = ImageDraw.Draw(cloud)
for (x, y, r) in ((0.43, 0.63, 0.105), (0.575, 0.545, 0.145), (0.715, 0.63, 0.105)):
    x, y = x + DX, y + DY
    c.ellipse([int(W*(x-r)), int(W*(y-r)), int(W*(x+r)), int(W*(y+r))], fill=255)
c.rounded_rectangle([int(W*(0.325+DX)), int(W*(0.60+DY)), int(W*(0.82+DX)), int(W*(0.735+DY))], radius=int(W*0.067), fill=255)
gap = cloud.filter(ImageFilter.MaxFilter((int(W*0.03) // 2) * 2 + 1))
sun = ImageChops.subtract(sun, gap)          # sun hidden behind the cloud, with a thin gap around it
glyph = ImageChops.lighter(sun, cloud)
bb = glyph.getbbox(); gx, gy = (bb[0] + bb[2]) / 2, (bb[1] + bb[3]) / 2
small = glyph.resize((round(W * SCALE), round(W * SCALE)), Image.LANCZOS)
glyph = Image.new('L', (W, W), 0)
glyph.paste(small, (round(W / 2 - gx * SCALE), round(W / 2 - gy * SCALE)))  # centred on the tile
# depth like the starburst on Claude's icon: about 15% darker from the top of the glyph to its bottom, and an inner
# shadow (the inside darker than the edges) that grows over the bottom half; the shade leans toward the tile's blue
bb = glyph.getbbox()
inner = ImageChops.multiply(glyph.filter(ImageFilter.GaussianBlur(W * 0.03)), glyph)  # ~255 deep inside, lower at edges
shade = Image.new('L', (W, W), 0); sp = shade.load(); ip = inner.load(); gp = glyph.load()
SHADOW = (190, 205, 228)  # what full shade looks like: white pulled toward the tile
for y in range(bb[1], bb[3]):
    t = (y - bb[1]) / max(1, bb[3] - bb[1] - 1)          # 0 top .. 1 bottom
    lower = max(0.0, (t - 0.45) / 0.55)                    # inner shadow only in the bottom half
    for x in range(bb[0], bb[2]):
        if gp[x, y]: sp[x, y] = round(255 * min(1.0, 0.42 * t + 0.55 * lower * ip[x, y] / 255))
fill = Image.composite(Image.new('RGBA', (W, W), SHADOW + (255,)), Image.new('RGBA', (W, W), GLYPH), shade)
im.paste(fill, (0, 0), ImageChops.multiply(glyph, mask))
im = im.resize((256, 256), Image.LANCZOS)
if out.endswith('.ico'): im.save(out, sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])
else: im.save(out)
