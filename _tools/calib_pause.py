#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""标定暂停菜单按钮的真实像素位置（排查自动化点错坐标用的）。

用法： python _tools/calib_pause.py [截图路径]
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402


def main():
    p = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent / "_shots" / "_verify_scan.png"
    from PIL import Image
    img = Image.open(p)
    w, h = img.size
    print(f"图: {p.name} {w}x{h}")
    rgb = img.convert("RGB")

    # 全图找金色像素的行分布（不限 x 范围）
    rows = {}
    for y in range(0, h):
        n = sum(1 for x in range(0, w, 2) if A._is_gold(rgb.getpixel((x, y))))
        if n:
            rows[y] = n
    if not rows:
        print("整图没有金色像素")
        return 1
    top = sorted(rows.items(), key=lambda kv: -kv[1])[:25]
    print("金色最多的行（y, 计数）:", sorted(top))
    ys = sorted(rows)
    print(f"金色行范围: {ys[0]}..{ys[-1]}")
    # 逐行打印前若干行的金色 x 范围
    shown = 0
    for y in ys:
        xs = [x for x in range(0, w) if A._is_gold(rgb.getpixel((x, y)))]
        if len(xs) >= 20:
            print(f"  y={y:4d} 金色 {len(xs):4d}  x {min(xs)}..{max(xs)}")
            shown += 1
            if shown >= 40:
                break
    return 0


if __name__ == "__main__":
    sys.exit(main())
