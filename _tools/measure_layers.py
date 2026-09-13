#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""量出「超级生产所」那两层贴图的真实尺寸（单位：格）。

用途：用户反馈「上面一层 2×3 正常，背后一层大了一圈」，
需要**客观数据**判断到底是哪一层、大多少，而不是靠肉眼估。

做法：在给定位图里按颜色聚类找两块区域 ——
  * 红砖色（游戏里 gatherers_hut 等建筑的红棕色调）
  * 紫色（我们自己贴图的主色）
游戏一格 = 64 像素，所以 (像素宽 / 64) 就是格数。

用法： python _tools/measure_layers.py <截图> [格子像素=64]
"""
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
from PIL import Image  # noqa: E402

CELL = 64


def bbox_of(img, pred, step=1):
    w, h = img.size
    px = img.convert("RGB").load()
    xs, ys = [], []
    for y in range(0, h, step):
        for x in range(0, w, step):
            if pred(px[x, y]):
                xs.append(x)
                ys.append(y)
    if not xs:
        return None
    return min(xs), min(ys), max(xs), max(ys)


def biggest_cluster(img, pred, gap=12):
    """按连通性（用坐标网格粗聚类）找最大的那一簇，避免零散像素干扰"""
    w, h = img.size
    px = img.convert("RGB").load()
    pts = [(x, y) for y in range(0, h, 2) for x in range(0, w, 2) if pred(px[x, y])]
    if not pts:
        return None
    clusters = []
    for (x, y) in pts:
        for c in clusters:
            if c["x0"] - gap <= x <= c["x1"] + gap and c["y0"] - gap <= y <= c["y1"] + gap:
                c["x0"] = min(c["x0"], x); c["y0"] = min(c["y0"], y)
                c["x1"] = max(c["x1"], x); c["y1"] = max(c["y1"], y)
                c["n"] += 1
                break
        else:
            clusters.append({"x0": x, "y0": y, "x1": x, "y1": y, "n": 1})
    clusters.sort(key=lambda c: -(c["x1"] - c["x0"]) * (c["y1"] - c["y0"]))
    return clusters[0] if clusters else None


def report(name, box, cell=CELL):
    if not box:
        print(f"  {name}: 没找到")
        return
    x0, y0, x1, y1 = box["x0"], box["y0"], box["x1"], box["y1"]
    w, h = x1 - x0 + 1, y1 - y0 + 1
    print(f"  {name}: bbox=({x0},{y0})-({x1},{y1})  {w}×{h}px  = {w/cell:.2f} × {h/cell:.2f} 格")


def is_red_brick(p):
    """红砖：R 明显高于 G/B，且整体偏暗到中亮（游戏建筑红棕色）"""
    r, g, b = p
    return r > 70 and r - g > 25 and r - b > 35 and g < 120


def is_purple(p):
    """我们的图主色：紫（R、B 高，G 低）"""
    r, g, b = p
    return b > 90 and r > 60 and b - g > 35 and r - g > 20


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    p = Path(sys.argv[1])
    cell = int(sys.argv[2]) if len(sys.argv) > 2 else CELL
    img = Image.open(p)
    print(f"图: {p.name} {img.size}   假设一格 = {cell}px")
    print("最大连通区域：")
    report("红砖层(可能是游戏原图)", biggest_cluster(img, is_red_brick), cell)
    report("紫色层(我们的图)  ", biggest_cluster(img, is_purple), cell)
    # 全图范围也报一下，便于判断是否被 UI 干扰
    print("全图范围（可能含 UI）：")
    report("红砖", bbox_of(img, is_red_brick), cell)
    report("紫色", bbox_of(img, is_purple), cell)
    return 0


if __name__ == "__main__":
    sys.exit(main())
