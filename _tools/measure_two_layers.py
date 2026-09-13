#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""量「超级生产所」两层贴图的相对尺寸，算出我们贴图该放大多少。

依据：用户实测截图里，上层（我们的紫图）比建筑占地小、下层（红色）大一圈。
红层是游戏按 3×2 占地画的**原模型**，所以它 ≈ 3×2 格；
把紫层和红层一比，就知道该放大多少。

用法： python _tools/measure_two_layers.py <截图>
"""
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
from PIL import Image  # noqa: E402


def bbox(img, pred, region=None, step=1):
    w, h = img.size
    l, t, r, b = region or (0, 0, w, h)
    px = img.convert("RGB").load()
    xs, ys = [], []
    for y in range(t, b, step):
        for x in range(l, r, step):
            if pred(px[x, y]):
                xs.append(x); ys.append(y)
    if not xs:
        return None
    return min(xs), min(ys), max(xs), max(ys)


def is_purple(p):
    r, g, b = p
    return b > 85 and b - g > 35 and r - g > 15 and r > 55


def is_dark_red(p):
    """暗红砖（游戏原模型那层）：R 明显大于 G/B，整体偏暗"""
    r, g, b = p
    return r > 55 and r - g > 22 and r - b > 28 and g < 95


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    img = Image.open(Path(sys.argv[1]))
    print(f"图 {Path(sys.argv[1]).name} {img.size}")

    # 只看建筑附近（避开左侧资源栏/底部工具栏/右侧小地图）
    w, h = img.size
    region = (int(w * 0.05), 0, int(w * 0.95), int(h * 0.75))

    pb = bbox(img, is_purple, region)
    rb = bbox(img, is_dark_red, region)

    def show(name, box):
        if not box:
            print(f"  {name}: 没找到")
            return None
        x0, y0, x1, y1 = box
        pw, ph = x1 - x0 + 1, y1 - y0 + 1
        print(f"  {name}: bbox=({x0},{y0})-({x1},{y1})  {pw}×{ph}px  宽高比 {pw / ph:.2f}")
        return pw, ph

    print("两层：")
    p = show("紫色层（我们的图）", pb)
    r = show("红色层（游戏原模型）", rb)

    if p and r:
        print("\n按「红层 = 建筑本体（比例视为正确）」换算：")
        print(f"  紫/红 宽度比 = {p[0] / r[0]:.3f}   高度比 = {p[1] / r[1]:.3f}")
        print(f"  → 我们贴图需要放大 ≈ ×{r[0] / p[0]:.2f}（宽） ×{r[1] / p[1]:.2f}（高）")
        print(f"  → 等价：ppu 从 64 改为 {64 * p[0] / r[0]:.1f}（按宽度算）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
