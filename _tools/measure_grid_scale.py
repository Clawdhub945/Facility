#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用放置模式里的**绿色方向箭头**当标尺，量出 1 格 = 多少屏幕像素，
再算我们的贴图渲染出来是多少格、需要缩放多少。

绿色箭头是游戏按格画的，箭头中心间距就是 1 格 —— 这是最权威的标尺，
不受场景杂色影响（判据要求「明亮的纯绿」，游戏世界里的植物绿偏暗偏黄）。

用法： python _tools/measure_grid_scale.py <截图>
"""
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
from PIL import Image  # noqa: E402


def is_arrow_green(p):
    """放置箭头色：明亮纯绿（实测接近 #22DD44 / #33FF33）"""
    r, g, b = p
    return g > 150 and g - r > 55 and g - b > 45


def arrow_columns(img, region):
    """返回箭头在 x 方向的中心列表（按列投影找簇）"""
    w, h = img.size
    l, t, r, b = region
    px = img.convert("RGB").load()
    cols = {}
    for y in range(t, b):
        for x in range(l, r):
            if is_arrow_green(px[x, y]):
                cols[x] = cols.get(x, 0) + 1
    if not cols:
        return []
    xs = sorted(cols)
    groups, cur = [], [xs[0]]
    for x in xs[1:]:
        if x - cur[-1] <= 6:
            cur.append(x)
        else:
            groups.append(cur); cur = [x]
    groups.append(cur)
    return [int(sum(g) / len(g)) for g in groups if len(g) >= 3]


def purple_bbox(img, region):
    w, h = img.size
    l, t, r, b = region
    px = img.convert("RGB").load()
    xs, ys = [], []
    for y in range(t, b):
        for x in range(l, r):
            rr, gg, bb = px[x, y]
            if bb > 85 and bb - gg > 35 and rr - gg > 15 and rr > 55:
                xs.append(x); ys.append(y)
    if not xs:
        return None
    return min(xs), min(ys), max(xs), max(ys)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    p = Path(sys.argv[1])
    img = Image.open(p)
    w, h = img.size
    region = (0, 0, w, h)
    print(f"图 {p.name} {img.size}")

    centers = arrow_columns(img, region)
    print(f"检测到 {len(centers)} 个绿色箭头列: {centers[:14]}")
    if len(centers) >= 2:
        diffs = [centers[i + 1] - centers[i] for i in range(len(centers) - 1)]
        # 同一格的箭头可能被拆成多列，取最小重复间距作为 1 格
        diffs = [d for d in diffs if d >= 8]
        if diffs:
            cell = min(diffs)
            print(f"  相邻箭头中心间距 = {diffs}")
            print(f"  → 1 格 ≈ {cell}px（取最小间距，避免跨格）")
            box = purple_bbox(img, region)
            if box:
                pw, ph = box[2] - box[0] + 1, box[3] - box[1] + 1
                print(f"  紫色层 {pw}×{ph}px = {pw / cell:.2f} × {ph / cell:.2f} 格（目标 3×2）")
                print(f"  → 需要放大：宽 ×{3 * cell / pw:.2f}，高 ×{2 * cell / ph:.2f}")
                avg = ((3 * cell / pw) + (2 * cell / ph)) / 2
                print(f"  → 等价：ppu 64 → {64 / avg:.1f}")
            return 0
    print("没量到箭头间距（这张图可能不在放置模式）")
    return 1


if __name__ == "__main__":
    sys.exit(main())
