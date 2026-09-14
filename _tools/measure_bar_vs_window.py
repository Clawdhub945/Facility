#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""量「下拉框选择条」与「建筑窗口」的实际像素宽度之比。

用户反复反馈「下拉框超出窗口」，需要客观数据：
  * 窗口左右边界在哪
  * 选择条（那条浅色横条）左右边界在哪
  * 两者比值 = 是否超出、超出多少

判据：
  * 窗口：深色面板（游戏窗口底色）
  * 选择条：浅色横条（我们自绘条的底色，比窗口亮很多）

用法： python _tools/measure_bar_vs_window.py <截图>
"""
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
from PIL import Image  # noqa: E402


def row_profile(img, y):
    """返回该行的 (x, R,G,B) 列表"""
    px = img.convert("RGB").load()
    return [(x, *px[x, y]) for x in range(img.width)]


def longest_run(img, pred):
    """全图找出满足 pred 的最长水平连续段，返回 (y, x0, x1)"""
    px = img.convert("RGB").load()
    best = (0, -1, -1)
    for y in range(img.height):
        x = 0
        while x < img.width:
            if pred(px[x, y]):
                x0 = x
                while x < img.width and pred(px[x, y]):
                    x += 1
                if x - x0 > best[2] - best[1]:
                    best = (y, x0, x - 1)
            else:
                x += 1
    return best


def is_light_bar(p):
    """选择条的浅色底（明显比窗口底色亮、且偏灰米色）"""
    r, g, b = p
    return r > 120 and g > 110 and b > 95 and abs(r - g) < 60 and (r - b) < 90


def is_dark_panel(p):
    """游戏窗口面板的深色底"""
    r, g, b = p
    return r < 90 and g < 90 and b < 100


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 1
    p = Path(sys.argv[1])
    img = Image.open(p)
    print(f"图 {p.name} {img.size}")

    by, bx0, bx1 = longest_run(img, is_light_bar)
    if bx1 < 0:
        print("没找到浅色选择条")
    else:
        print(f"浅色选择条: y={by}  x={bx0}..{bx1}  宽={bx1 - bx0 + 1}px")

    # 在该行上下找窗口面板的左右边界
    vals = row_profile(img, by) if bx1 >= 0 else []
    if vals:
        left = next((x for x, *c in vals if is_dark_panel(c)), None)
        right = next((x for x, *c in reversed(vals) if is_dark_panel(c)), None)
        print(f"同行深色面板范围: x={left}..{right}" if left is not None else "同行没找到深色面板")

    # 全图找最宽的深色面板（窗口本体）
    py, px0, px1 = longest_run(img, is_dark_panel)
    if px1 > 0:
        print(f"最宽深色面板: y={py}  x={px0}..{px1}  宽={px1 - px0 + 1}px")
    if bx1 > 0 and px1 > 0:
        print()
        print(f"选择条 / 最宽面板 = {(bx1 - bx0 + 1) / (px1 - px0 + 1):.2f}")
        if bx0 < px0 or bx1 > px1:
            print(f"  → **超出面板**：左侧多 {max(0, px0 - bx0)}px，右侧多 {max(0, bx1 - px1)}px")
        else:
            print("  → 在面板内")
    return 0


if __name__ == "__main__":
    sys.exit(main())
