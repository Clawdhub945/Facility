#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用游戏自己的「格线/放置指示箭头」当标尺，量出建筑贴图的真实格数。

原理：放置建筑时游戏会画出占地格的绿色箭头/边框，箭头间距 = 1 格。
      先测出「1 格 = 多少屏幕像素」，再用同一张图量我们贴图的像素尺寸，即可换算成格数。
      这样绕开了「游戏内部渲染缩放未知」的问题。

用法： python _tools/measure_by_grid.py <截图>
"""
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
from PIL import Image  # noqa: E402


def green_runs(img):
    """找出绿色指示色（放置箭头/边框）的水平连续段，返回 [(y, x_start, x_end), …]"""
    w, h = img.size
    px = img.convert("RGB").load()
    runs = []
    for y in range(h):
        x = 0
        while x < w:
            if is_grid_green(px[x, y]):
                x0 = x
                while x < w and is_grid_green(px[x, y]):
                    x += 1
                if x - x0 >= 6:
                    runs.append((y, x0, x - 1))
            else:
                x += 1
    return runs


def is_grid_green(p):
    """放置指示色：亮绿（实测偏 #00FF66 ~ #33FF33 一类）"""
    r, g, b = p
    return g > 170 and g - r > 70 and g - b > 60


def purple_bbox(img):
    w, h = img.size
    px = img.convert("RGB").load()
    xs, ys = [], []
    for y in range(h):
        for x in range(w):
            r, g, b = px[x, y]
            if b > 80 and b - g > 30 and r - g > 10:      # 紫色调
                xs.append(x); ys.append(y)
    if not xs:
        return None
    return min(xs), min(ys), max(xs), max(ys)


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    img = Image.open(Path(sys.argv[1]))
    print(f"图: {Path(sys.argv[1]).name} {img.size}")

    runs = green_runs(img)
    # 按 y 分行，找相邻行的「箭头簇」，用它们的 x 中心差估计格宽
    rows = {}
    for (y, x0, x1) in runs:
        rows.setdefault(y, []).append((x0, x1))
    # 取绿色像素最多的一行做参考
    best_y = max(rows, key=lambda y: len(rows[y])) if rows else None
    print(f"绿色指示行数: {len(rows)}；像素最多的一行 y={best_y}")
    cell_px = None
    if best_y is not None:
        segs = sorted(rows[best_y])
        centers = [(a + b) // 2 for (a, b) in segs]
        if len(centers) >= 2:
            diffs = [centers[i + 1] - centers[i] for i in range(len(centers) - 1)]
            diffs = [d for d in diffs if d > 8]
            if diffs:
                cell_px = sum(diffs) / len(diffs)
                print(f"  相邻绿色指示中心间距（≈1 格）= {diffs} → 均值 {cell_px:.1f}px")

    box = purple_bbox(img)
    if box:
        x0, y0, x1, y1 = box
        w, h = x1 - x0 + 1, y1 - y0 + 1
        print(f"紫色（我们的贴图）bbox={box}  {w}×{h}px")
        if cell_px:
            print(f"  → 尺寸 = {w / cell_px:.2f} × {h / cell_px:.2f} 格（按 {cell_px:.1f}px/格 换算）")
        else:
            print("  （没测到格宽，无法换算）")
    else:
        print("没找到紫色区域")
    return 0


if __name__ == "__main__":
    sys.exit(main())
