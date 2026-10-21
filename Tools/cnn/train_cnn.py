"""Train the RescueBot pixel segmenter (road / crossable debris / other) and export it to ONNX.

Data: Dataset/{train,val}/img + lbl produced by `unity command rb_dump_dataset` (160x90 rover camera images, ground-truth classes).
Usage:  py -3.14 Tools/cnn/train_cnn.py [--epochs 40] [--out Assets/RescueBot/Models/rescue_seg.onnx]
"""
import argparse, glob, os, time
import numpy as np
from PIL import Image
import torch, torch.nn as nn, torch.nn.functional as F

ap = argparse.ArgumentParser()
ap.add_argument('--data', default='Dataset')
ap.add_argument('--epochs', type=int, default=40)
ap.add_argument('--bs', type=int, default=64)
ap.add_argument('--lr', type=float, default=3e-3)
ap.add_argument('--width', type=int, default=24)
ap.add_argument('--out', default='Assets/RescueBot/Resources/rescue_seg.onnx')
ap.add_argument('--aug', type=int, default=1, help='1 = photometric augmentation (brightness/contrast/colour/noise)')
args = ap.parse_args()
dev = 'cuda' if torch.cuda.is_available() else 'cpu'
torch.manual_seed(0); np.random.seed(0)
H, W, PAD = 90, 160, 3          # the network sees 96 rows (3 replicated rows top and bottom); the divisor is 8

def load(split):
    xs, ys = [], []
    for p in sorted(glob.glob(os.path.join(args.data, split, 'img', '*.png'))):
        xs.append(np.array(Image.open(p).convert('RGB')))
        q = os.path.join(args.data, split, 'lbl', os.path.basename(p))
        ys.append(np.array(Image.open(q).convert('RGB'))[:, :, 0])
    return torch.from_numpy(np.stack(xs)).permute(0, 3, 1, 2).contiguous(), torch.from_numpy(np.stack(ys)).long()

t0 = time.time()
Xtr, Ytr = load('train'); Xva, Yva = load('val')
print(f'device={dev} train={len(Xtr)} val={len(Xva)} load={time.time()-t0:.0f}s')
cnt = torch.bincount(Ytr.flatten(), minlength=3).float(); print('train class share', (cnt / cnt.sum()).tolist())
cw = (cnt.sum() / (3 * cnt)).clamp(max=6.0); cw = cw / cw.mean(); print('class weights', cw.tolist())

def cbr(i, o): return nn.Sequential(nn.Conv2d(i, o, 3, padding=1, bias=False), nn.BatchNorm2d(o), nn.ReLU(inplace=True))

class Net(nn.Module):
    """Small U-Net: 3 scales (1, 1/2, 1/4). Input: RGB float 0..1, N x 3 x 96 x 160. Output: logits N x 3 x 96 x 160."""
    def __init__(s, w=24):
        super().__init__()
        s.e1 = nn.Sequential(cbr(3, w), cbr(w, w))
        s.e2 = nn.Sequential(cbr(w, 2 * w), cbr(2 * w, 2 * w))
        s.e3 = nn.Sequential(cbr(2 * w, 3 * w), cbr(3 * w, 3 * w), cbr(3 * w, 3 * w))
        s.d2 = nn.Sequential(cbr(5 * w, 2 * w), cbr(2 * w, 2 * w))
        s.d1 = nn.Sequential(cbr(3 * w, w), cbr(w, w))
        s.head = nn.Conv2d(w, 3, 1)
    def forward(s, x):
        x = (x - 0.5) * 4.0
        a = s.e1(x)
        b = s.e2(F.max_pool2d(a, 2))
        c = s.e3(F.max_pool2d(b, 2))
        c = F.interpolate(c, scale_factor=2.0, mode='nearest')
        b = s.d2(torch.cat([b, c], 1))
        b = F.interpolate(b, scale_factor=2.0, mode='nearest')
        a = s.d1(torch.cat([a, b], 1))
        return s.head(a)

def pad_in(x):                    # N x 3 x 90 x 160 -> N x 3 x 96 x 160, replicate rows
    return F.pad(x, (0, 0, PAD, PAD), mode='replicate')

def augment(x):                   # x float 0..1 on the GPU
    n = x.shape[0]
    g = 1.0 + (torch.rand(n, 1, 1, 1, device=x.device) - 0.5) * 0.7          # brightness
    c = 1.0 + (torch.rand(n, 1, 1, 1, device=x.device) - 0.5) * 0.5          # contrast
    t = 1.0 + (torch.rand(n, 3, 1, 1, device=x.device) - 0.5) * 0.18         # colour cast
    m = x.mean((1, 2, 3), keepdim=True)
    x = ((x - m) * c + m) * g * t
    x = x + torch.randn_like(x) * (torch.rand(n, 1, 1, 1, device=x.device) * 0.04)
    return x.clamp(0, 1)

net = Net(args.width).to(dev)
print('params', sum(p.numel() for p in net.parameters()))
opt = torch.optim.AdamW(net.parameters(), lr=args.lr, weight_decay=1e-4)
steps = args.epochs * ((len(Xtr) + args.bs - 1) // args.bs)
sched = torch.optim.lr_scheduler.OneCycleLR(opt, max_lr=args.lr, total_steps=steps, pct_start=0.15)
Xg, Yg = Xtr.to(dev), Ytr.to(dev); cwd = cw.to(dev)

def evaluate(X, Y, fn=None):
    net.eval(); conf = torch.zeros(3, 3, dtype=torch.long)
    with torch.no_grad():
        for i in range(0, len(X), 256):
            xb = X[i:i + 256]
            if fn is None:
                p = net(pad_in(xb.to(dev).float() / 255))[:, :, PAD:PAD + H].argmax(1).cpu()
            else:
                p = fn(xb)
            y = Y[i:i + 256]
            conf += torch.bincount((y * 3 + p).flatten(), minlength=9).view(3, 3)
    iou = [(conf[k, k] / (conf[k].sum() + conf[:, k].sum() - conf[k, k]).clamp(min=1)).item() for k in range(3)]
    return (conf.diag().sum() / conf.sum()).item(), iou, conf

def classical(xb):                # the hand-written colour rule (Perception.ClassOf) on the same pixels
    x = xb.float(); mx = x.max(1).values; mn = x.min(1).values
    v = mx / 255; s = torch.where(mx > 0, (mx - mn) / mx.clamp(min=1), torch.zeros_like(mx))
    r, g, b = x[:, 0], x[:, 1], x[:, 2]; d = (mx - mn).clamp(min=1e-6)
    hue = torch.where(mx == r, 60 * (((g - b) / d) % 6), torch.where(mx == g, 60 * ((b - r) / d + 2), 60 * ((r - g) / d + 4)))
    hue = torch.where(hue < 0, hue + 360, hue)
    road = (s < 0.13) & (v > 0.07) & (v < 0.62)
    cross = (~road) & (s > 0.55) & (v > 0.2) & (hue >= 33) & (hue <= 70)
    return torch.where(road, 0, torch.where(cross, 1, 2)).long()

ca, ciou, _ = evaluate(Xva, Yva, classical)
print(f'CLASSICAL on val: acc={ca:.4f} IoU road/cross/other={[round(i, 3) for i in ciou]}')

t0 = time.time()
for ep in range(args.epochs):
    net.train(); perm = torch.randperm(len(Xg), device=dev); tot = 0.0
    for i in range(0, len(Xg), args.bs):
        idx = perm[i:i + args.bs]
        x = Xg[idx].float() / 255; y = Yg[idx]
        if torch.rand(1).item() < 0.5: x = x.flip(3); y = y.flip(2)
        if args.aug: x = augment(x)
        out = net(pad_in(x))[:, :, PAD:PAD + H]
        loss = F.cross_entropy(out, y, weight=cwd)
        opt.zero_grad(set_to_none=True); loss.backward(); opt.step(); sched.step(); tot += loss.item() * len(idx)
    if ep % 5 == 4 or ep == args.epochs - 1:
        a, iou, _ = evaluate(Xva, Yva)
        print(f'ep {ep+1:3d} loss={tot/len(Xg):.4f} val acc={a:.4f} IoU={[round(i, 3) for i in iou]}  ({time.time()-t0:.0f}s)', flush=True)

a, iou, conf = evaluate(Xva, Yva)
print('FINAL val acc', round(a, 4), 'IoU road/cross/other', [round(i, 3) for i in iou]); print('confusion rows=truth cols=pred\n', conf)
# robustness: evaluate both under a lighting change the colour rule was never tuned for
def dim(X): return (X.float() * 0.55 * torch.tensor([1.0, 0.95, 1.1]).view(1, 3, 1, 1)).clamp(0, 255).to(torch.uint8)
Xd = dim(Xva)
a1, i1, _ = evaluate(Xd, Yva, classical); a2, i2, _ = evaluate(Xd, Yva)
print(f'DARK (x0.55, bluish) classical acc={a1:.4f} IoU={[round(i, 3) for i in i1]} | CNN acc={a2:.4f} IoU={[round(i, 3) for i in i2]}')

os.makedirs(os.path.dirname(args.out) or '.', exist_ok=True)
net.eval().cpu()
dummy = torch.rand(1, 3, H + 2 * PAD, W)
try:
    torch.onnx.export(net, dummy, args.out, input_names=['image'], output_names=['logits'], opset_version=17, dynamo=False)
except TypeError:
    torch.onnx.export(net, dummy, args.out, input_names=['image'], output_names=['logits'], opset_version=17)
print('exported', args.out, os.path.getsize(args.out) // 1024, 'KB')
try:
    import onnx; onnx.checker.check_model(onnx.load(args.out)); print('onnx check ok')
except Exception as e:
    print('onnx check skipped:', e)
torch.save(net.state_dict(), os.path.join('Tools', 'cnn', 'rescue_seg.pt'))

# the real test: the standard RescueBot scene lighting (what the simulator uses when nothing is randomised)
if os.path.isdir(os.path.join(args.data, 'val_std')):
    net.to(dev)
    Xs, Ys = load('val_std')
    a_c, i_c, _ = evaluate(Xs, Ys, classical); a_n, i_n, _ = evaluate(Xs, Ys)
    print(f'STANDARD LIGHTING val_std: classical acc={a_c:.4f} IoU={[round(i, 3) for i in i_c]} | CNN acc={a_n:.4f} IoU={[round(i, 3) for i in i_n]}')
