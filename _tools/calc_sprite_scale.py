#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""进入放置模式 → 量出「1 格 = 多少屏幕像素」→ 换算我们贴图的真实格数 → 算出缩放系数。

为什么这么做：用户反馈「上面一层（我们的图）比建筑占地小、背后那层大一圈」，
但游戏内部的渲染缩放未知，直接看像素没法判断。放置模式里游戏会按格画绿色方向箭头，
箭头中心间距就是 1 格，于是可以把它当标尺。

用法： python _tools/calc_sprite_scale.py
"""
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402
from measure_by_grid import green_runs, purple_bbox  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"
BUILD_BTN = (521, 878)
FOOD_TAB = (382, 601)
ICON_CANDIDATES = [(x, y) for y in (566, 664, 762) for x in (838, 974, 1110, 1246, 1382)]

SAVE = "2026-09-13_23_c9798b4a01eb4ba795638e19eb5bc488"


def main():
    hwnd = A.find_window()[0]
    A.load_save(SAVE, settle=10)
    time.sleep(10)
    A.focus_window(hwnd)
    time.sleep(0.8)
    A.close_overlays(hwnd)
    time.sleep(0.6)

    # 进放置模式：选超级生产所
    A.click_client(hwnd, *BUILD_BTN, settle=1.6)
    A.click_client(hwnd, *FOOD_TAB, settle=1.2)
    for i, (x, y) in enumerate(ICON_CANDIDATES, 1):
        A.click_client(hwnd, x, y, settle=0.9)
        A.move_cursor(*A.client_to_screen(hwnd, 700, 350))
        time.sleep(1.2)
        img = A.client_grab(hwnd, SHOTS / "_scale_probe.png")
        runs = green_runs(img)
        if runs:
            rows = {}
            for (yy, x0, x1) in runs:
                rows.setdefault(yy, []).append((x0, x1))
            best_y = max(rows, key=lambda yy: len(rows[yy]))
            centers = sorted((a + b) // 2 for (a, b) in rows[best_y])
            diffs = [centers[k + 1] - centers[k] for k in range(len(centers) - 1)]
            diffs = [d for d in diffs if d > 8]
            if len(diffs) >= 2:
                cell = sum(diffs) / len(diffs)
                print(f"候选 {i} ({x},{y}) 进入放置模式，绿色箭头间距 = {diffs} → 1 格 ≈ {cell:.1f}px")
                box = purple_bbox(img)
                if box:
                    pw, ph = box[2] - box[0] + 1, box[3] - box[1] + 1
                    print(f"  我们贴图渲染尺寸 = {pw}×{ph}px = {pw / cell:.2f} × {ph / cell:.2f} 格")
                    print(f"  目标 = 3.00 × 2.00 格")
                    print(f"  → 建议缩放：宽 ×{3 * cell / pw:.3f}，高 ×{2 * cell / ph:.3f}")
                    print(f"  → 建议 ppu：64 ÷ 平均缩放 = {64 / ((3 * cell / pw + 2 * cell / ph) / 2):.1f}")
                return 0
    print("没进到放置模式或没量到格线")
    return 1


if __name__ == "__main__":
    sys.exit(main())
