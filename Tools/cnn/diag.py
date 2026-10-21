import glob, os, numpy as np
from PIL import Image
files = sorted(glob.glob('Dataset/val/img/*.png'))[::7]
conf = np.zeros((3, 3), np.int64); worst = []
def classical(x):
    x = x.astype(np.float32); mx = x.max(2); mn = x.min(2)
    v = mx / 255; s = np.where(mx > 0, (mx - mn) / np.maximum(mx, 1), 0)
    r, g, b = x[..., 0], x[..., 1], x[..., 2]; d = np.maximum(mx - mn, 1e-6)
    hue = np.where(mx == r, 60 * (((g - b) / d) % 6), np.where(mx == g, 60 * ((b - r) / d + 2), 60 * ((r - g) / d + 4)))
    hue = np.where(hue < 0, hue + 360, hue)
    road = (s < 0.13) & (v > 0.07) & (v < 0.62)
    cross = (~road) & (s > 0.55) & (v > 0.2) & (hue >= 33) & (hue <= 70)
    return np.where(road, 0, np.where(cross, 1, 2))
for p in files:
    x = np.array(Image.open(p).convert('RGB')); y = np.array(Image.open(p.replace('img', 'lbl')).convert('RGB'))[..., 0]
    c = classical(x)
    for a in range(3):
        for b in range(3): conf[a, b] += ((y == a) & (c == b)).sum()
    worst.append(((y != c).sum(), p, x, y, c))
print('rows=truth cols=classical\n', conf)
worst.sort(key=lambda t: -t[0])
pal = np.array([[60, 60, 72], [255, 200, 0], [160, 70, 60]], np.uint8)
os.makedirs('Screenshots', exist_ok=True)
for k in range(4):
    n, p, x, y, c = worst[k]
    print(p, n)
    diff = np.where((y != c)[..., None], np.array([255, 0, 255], np.uint8), pal[c] // 2)
    im = np.concatenate([x, pal[y], pal[c], diff.astype(np.uint8)], 1)
    Image.fromarray(im).resize((im.shape[1] * 2, im.shape[0] * 2), Image.NEAREST).save(f'Screenshots/diag_{k}.png')
