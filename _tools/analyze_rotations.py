#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""分析用户提供的 4 张建筑图（img/1_0..1_3.png）之间的旋转/镜像关系。

用途：确定哪张图对应游戏的哪个朝向（rotation 0..3），避免建筑转个方向贴图就不对。

用法： python _tools/analyze_rotations.py
"""
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
from PIL import Image  # noqa: E402

SRC = Path(__file__).resolve().parent.parent / "img"


def diff_pixels(a, b, step=2):
    if a.size != b.size:
        return -1
    pa, pb = a.convert("RGBA").load(), b.convert("RGBA").load()
    n = 0
    for y in range(0, a.height, step):
        for x in range(0, a.width, step):
            if pa[x, y] != pb[x, y]:
                n += 1
    return n


def main():
    ims = []
    for i in range(4):
        p = SRC / f"1_{i}.png"
        if not p.exists():
            print(f"缺少 {p}")
            return 1
        im = Image.open(p).convert("RGBA")
        ims.append(im)
        orient = "横向(宽>高)" if im.width > im.height else "竖向(高>宽)"
        print(f"1_{i}.png  {im.size}  {orient}")

    print("\n=== 相互关系（差异像素数，0 = 完全相同）===")
    transforms = {
        "原图": lambda im: im,
        "水平翻转": lambda im: im.transpose(Image.FLIP_LEFT_RIGHT),
        "垂直翻转": lambda im: im.transpose(Image.FLIP_TOP_BOTTOM),
        "旋转180": lambda im: im.transpose(Image.ROTATE_180),
    }
    for i in range(4):
        for j in range(4):
            if i == j:
                continue
            for tname, tf in transforms.items():
                d = diff_pixels(ims[i], tf(ims[j]))
                if 0 <= d <= 40:
                    print(f"  1_{i} ≈ 1_{j} 的{tname}（差异 {d}）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
