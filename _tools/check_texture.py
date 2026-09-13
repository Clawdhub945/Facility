#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证自定义外观是否真的渲染出来：在世界截图里找「红砖色」像素。

我的红砖厂房主色是 (150,62,48)，而游戏世界里已有的红/棕色调是
木墙(约 138,119,70 偏黄)、地砖(45,54,80 偏蓝)、树(绿)——红砖色（R 明显高、G/B 低且 G≈B/1.3）
在场景里几乎是唯一的，所以可以拿它当客观指纹。

用法： python _tools/check_texture.py [截图路径]
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

BRICK = (150, 62, 48)
TOL = 26


def count_brick(img, region=None):
    w, h = img.size
    l, t, r, b = region or (0, 0, w, h)
    px = img.convert("RGB").load()
    n = 0
    for y in range(t, b, 2):
        for x in range(l, r, 2):
            rr, gg, bb = px[x, y]
            if (abs(rr - BRICK[0]) <= TOL and abs(gg - BRICK[1]) <= TOL
                    and abs(bb - BRICK[2]) <= TOL):
                n += 1
    return n


def main():
    from PIL import Image
    if len(sys.argv) > 1:
        p = Path(sys.argv[1])
    else:
        p = Path(__file__).resolve().parent / "_shots" / "ghost3.png"
    img = Image.open(p)
    total = count_brick(img)
    # 世界区域（排除左侧资源栏与右侧小地图/日志）
    w, h = img.size
    world = count_brick(img, (int(w * 0.04), 0, int(w * 0.58), int(h * 0.62)))
    print(f"{p.name}: 红砖像素 全图={total}  世界区={world}")
    print("结论:", "[OK] 发现自定义红砖外观" if world > 40 else "[NO] 没发现红砖外观")
    return 0


if __name__ == "__main__":
    sys.exit(main())
