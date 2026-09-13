#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""打开建造菜单并选中「超级生产所」，然后验证放置幽灵是否出现。

每一步都用**像素判据**确认成功，不靠感觉：
  * 建造菜单开着吗 → 客户区固定位置是不是菜单底色
  * 选中建筑了吗 → 光标移到两处空地，两帧差异是否出现「移动的幽灵」

用法： python _tools/place_super.py
"""
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"

# 实测客户区坐标（1440×900）
BUILD_BTN = (521, 878)          # 底部工具栏「建造」
FOOD_TAB = (382, 601)           # 建造菜单左侧分类「食品」
SUPER_ICON = (545, 570)         # 食品页最后一个图标 = 超级生产所
MENU_BG = (58, 48, 44)          # 建造菜单面板底色（实测约 (57,48,47)）
TOL = 14


def pixel(img, x, y):
    return img.convert("RGB").getpixel((x, y))


def near(p, ref, tol=TOL):
    return all(abs(p[i] - ref[i]) <= tol for i in range(3))


def menu_open(img):
    """建造菜单开着时，菜单面板会盖住这块区域（客户区 400,460~900,640 附近）。"""
    hits = sum(1 for (x, y) in ((400, 470), (420, 560), (900, 470), (900, 620))
               if near(pixel(img, x, y), MENU_BG, 20))
    return hits >= 2


def main():
    hwnd = A.find_window()[0]
    A.focus_window(hwnd)
    time.sleep(0.5)

    for attempt in range(1, 6):
        img = A.client_grab(hwnd, SHOTS / "_place_probe.png")
        if not menu_open(img):
            A.click_client(hwnd, *BUILD_BTN, settle=1.5)
            img = A.client_grab(hwnd, SHOTS / "_place_probe.png")
            if not menu_open(img):
                print(f"  尝试{attempt}: 建造菜单没打开（面板底色不匹配）")
                continue
        print(f"  尝试{attempt}: 建造菜单已打开")
        # 选食品分类 + 超级生产所
        A.click_client(hwnd, *FOOD_TAB, settle=1.0)
        A.click_client(hwnd, *SUPER_ICON, settle=1.5)

        # 验证幽灵：光标在两处空地，看有没有「跟着走的东西」
        A.move_cursor(*A.client_to_screen(hwnd, 620, 300))
        time.sleep(1.4)
        a = A.client_grab(hwnd, SHOTS / "place_a.png")
        A.move_cursor(*A.client_to_screen(hwnd, 780, 360))
        time.sleep(1.4)
        b = A.client_grab(hwnd, SHOTS / "place_b.png")

        from PIL import ImageChops
        d = ImageChops.difference(a.convert("RGB"), b.convert("RGB")).convert("L")
        hist = d.histogram()
        pct = 100.0 * sum(hist[30:]) / sum(hist)
        print(f"  光标移动引起的画面变化: {pct:.2f}%  bbox={d.getbbox()}")
        if pct > 0.2:
            print("  [OK] 已进入放置模式（有跟随光标的幽灵）")
            # 顺便查幽灵区域有没有红砖色
            bbox = d.getbbox()
            from check_texture import count_brick
            region = (max(0, bbox[0] - 20), max(0, bbox[1] - 20),
                      min(b.width, bbox[2] + 20), min(b.height, bbox[3] + 20))
            n = count_brick(b, region)
            print(f"  幽灵区域红砖像素: {n} → "
                  + ("[OK] 是我们的红砖厂房" if n > 30 else "[NO] 不是红砖外观"))
            return 0 if n > 30 else 2
    print("反复尝试仍没进入放置模式")
    return 1


if __name__ == "__main__":
    sys.exit(main())
