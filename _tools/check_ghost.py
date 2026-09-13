#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""判断「放置建筑的幽灵预览」是否出现，并检查它是不是我们的红砖外观。

做法（不依赖人眼看图）：
  1. 把光标放在空地 A，截图；再放到空地 B，截图
  2. 两图求差 → 差异区域 = 跟着光标移动的东西（建筑幽灵 / 地块高亮）
  3. 在差异区域的 bbox 里数「红砖色 (150,62,48)」像素：
     有 → 幽灵是我们的红砖厂房；没有 → 幽灵是别的（或根本没进放置模式）

用法： python _tools/check_ghost.py
"""
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402
from check_texture import count_brick, BRICK, TOL  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"
AX, AY = 620, 320          # 空地 A（客户区坐标）
BX, BY = 760, 380          # 空地 B


def main():
    from PIL import ImageChops
    hwnd = A.find_window()[0]
    A.focus_window(hwnd)
    time.sleep(0.5)
    A.move_cursor(*A.client_to_screen(hwnd, AX, AY))
    time.sleep(1.6)
    a = A.client_grab(hwnd, SHOTS / "ghost_a.png")
    A.move_cursor(*A.client_to_screen(hwnd, BX, BY))
    time.sleep(1.6)
    b = A.client_grab(hwnd, SHOTS / "ghost_b.png")

    d = ImageChops.difference(a.convert("RGB"), b.convert("RGB")).convert("L")
    bbox = d.getbbox()
    hist = d.histogram()
    pct = 100.0 * sum(hist[30:]) / sum(hist)
    print(f"两帧差异: {pct:.2f}%  差异区域 bbox={bbox}")

    if not bbox or pct < 0.2:
        print("结论: 没有跟着光标移动的东西 → 可能没进入放置模式（或光标位置是空的）")
        return 1

    # 在差异区域里找红砖色
    region = (max(0, bbox[0] - 20), max(0, bbox[1] - 20),
              min(a.width, bbox[2] + 20), min(a.height, bbox[3] + 20))
    n_brick = count_brick(b, region)
    print(f"差异区域内红砖像素: {n_brick}（区域 {region}）")
    if n_brick > 30:
        print("结论: [OK] 放置幽灵就是我们的红砖厂房 —— 自定义外观生效")
        return 0
    print("结论: [NO] 有幽灵但不是红砖外观 —— 贴图覆盖可能没生效")
    return 1


if __name__ == "__main__":
    sys.exit(main())
