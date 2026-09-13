#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用同一张截图里的已知建筑（综合生产所 workbench）当标尺，反推每格像素，
再算我们贴图需要多大（即 ppu 该设多少）。

用法： python _tools/calc_scale_from_ref.py <截图> [参考建筑宽格数] [参考建筑高格数]
默认参考：综合生产所 workbench（建模贴图本体为 3×1 格的小台子）
"""
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
from PIL import Image  # noqa: E402


def cluster(img, pred, gap=14, step=2):
    w, h = img.size
    px = img.convert("RGB").load()
    pts = [(x, y) for y in range(0, h, step) for x in range(0, w, step) if pred(px[x, y])]
    if not pts:
        return []
    cs = []
    for (x, y) in pts:
        for c in cs:
            if c["x0"] - gap <= x <= c["x1"] + gap and c["y0"] - gap <= y <= c["y1"] + gap:
                c["x0"] = min(c["x0"], x); c["y0"] = min(c["y0"], y)
                c["x1"] = max(c["x1"], x); c["y1"] = max(c["y1"], y); c["n"] += 1
                break
        else:
            cs.append({"x0": x, "y0": y, "x1": x, "y1": y, "n": 1})
    cs.sort(key=lambda c: -c["n"])
    return cs


def is_wood_blue(p):
    """workbench 的木质台身：棕黄 + 桌面浅蓝（取宽泛的“亮暖色”判据）"""
    r, g, b = p
    return r > 120 and g > 95 and b < 140 and r - b > 25


def is_purple(p):
    r, g, b = p
    return b > 80 and b - g > 30 and r - g > 10


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    img = Image.open(Path(sys.argv[1]))
    ref_w = int(sys.argv[2]) if len(sys.argv) > 2 else 3
    ref_h = int(sys.argv[3]) if len(sys.argv) > 3 else 1
    print(f"图 {Path(sys.argv[1]).name} {img.size}；参考建筑本体假设 {ref_w}×{ref_h} 格")

    refs = [c for c in cluster(img, is_wood_blue) if c["n"] > 60]
    print(f"\n找到 {len(refs)} 个「木质台身」候选（可能是综合生产所/workbench）:")
    cell_est = []
    for c in refs[:6]:
        w = c["x1"] - c["x0"] + 1
        h = c["y1"] - c["y0"] + 1
        cw, ch = w / ref_w, h / ref_h
        print(f"  bbox=({c['x0']},{c['y0']})-({c['x1']},{c['y1']})  {w}×{h}px  像素数={c['n']}"
              f"  → 每格≈{cw:.1f}×{ch:.1f}px")
        cell_est.append(ch)
    if not cell_est:
        print("没找到参考建筑，换个阈值或截图")
        return 1

    cell = sum(cell_est) / len(cell_est)
    print(f"\n估计每格 ≈ {cell:.1f}px（建筑渲染缩放）")

    pur = [c for c in cluster(img, is_purple) if c["n"] > 40]
    if pur:
        c = pur[0]
        w = c["x1"] - c["x0"] + 1
        h = c["y1"] - c["y0"] + 1
        print(f"我们贴图渲染为 {w}×{h}px = {w / cell:.2f} × {h / cell:.2f} 格（目标 3.00 × 2.00）")
        print(f"→ 需要放大：宽 ×{3 * cell / w:.3f}，高 ×{2 * cell / h:.3f}")
        avg = ((3 * cell / w) + (2 * cell / h)) / 2
        print(f"→ 等价做法：把 ppu 从 64 改成 {64 / avg:.1f}")
    else:
        print("没找到紫色区域（可能这张图里没有我们的建筑）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
