#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把「超级生产所」放下去并确认建出来。

思路（不靠人眼看图，靠**结果**判断）：
  1. 重进档（清掉帝国地图/暂停菜单等弹窗）
  2. 开建造菜单 → 点「食品」
  3. 在食品页的图标格子里**逐个候选点点击**，每点一次就移动鼠标：
     若出现「跟随光标的幽灵」说明进入了放置模式（选中成功）
  4. 左键放下 → 重扫实体 → 看有没有 105050

用法： python _tools/place_and_verify.py
"""
import json
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"
BUILD_BTN = (521, 878)
# 建造菜单（客户区，1440×900 实测）：分类列表在左，图标格子在右下
FOOD_TAB = (382, 601)
# 图标格子的候选点（食品页 3 行 × 6 列，按实测布局铺开）
ICON_CANDIDATES = [(x, y) for y in (566, 664, 762) for x in (838, 974, 1110, 1246, 1382)]


def entities():
    raw = urllib.request.urlopen(A.API + "/api/editor/entities", timeout=120).read()
    return json.loads(raw.decode("utf-8", "replace"))


def supers():
    return [e for e in entities() if e.get("stuffId") == 105050]


def rescan(timeout=300):
    """重扫实体。⚠ 这台机器实体 2 万多，扫描偶尔会超时（HTTP 408），
    所以失败不当致命错误——后面用缓存数据继续判断，避免整条流程中断。"""
    try:
        A.api_post("/api/editor/scan", None, timeout=timeout)
    except Exception as ex:
        print(f"  （重扫请求失败，改用缓存: {type(ex).__name__}）")
        return False
    for _ in range(40):
        if not (A.api_state() or {}).get("scanning"):
            return True
        time.sleep(3)
    return False


def main():
    hwnd = A.find_window()[0]
    print("== 重进档清弹窗 ==")
    ok, _, dt = A.load_save(settle=10)
    print(f"  reload={ok} {dt:.1f}s")
    if not ok:
        return 1
    time.sleep(12)
    rescan()
    print(f"  开始前 105050 座数: {len(supers())}")

    A.focus_window(hwnd)
    time.sleep(1.0)
    A.close_overlays(hwnd)
    time.sleep(0.8)

    print("== 开建造菜单 ==")
    A.click_client(hwnd, *BUILD_BTN, settle=1.8)
    A.click_client(hwnd, *FOOD_TAB, settle=1.2)

    print("== 逐个候选点找「超级生产所」 ==")
    from PIL import ImageChops
    for i, (x, y) in enumerate(ICON_CANDIDATES, 1):
        A.click_client(hwnd, x, y, settle=1.0)
        A.move_cursor(*A.client_to_screen(hwnd, 640, 300)); time.sleep(1.1)
        a = A.client_grab(hwnd, SHOTS / "_pv_a.png")
        A.move_cursor(*A.client_to_screen(hwnd, 820, 400)); time.sleep(1.1)
        b = A.client_grab(hwnd, SHOTS / "_pv_b.png")
        d = ImageChops.difference(a.convert("RGB"), b.convert("RGB")).convert("L")
        pct = 100.0 * sum(d.histogram()[30:]) / sum(d.histogram())
        if pct > 0.25:
            print(f"  候选 {i} ({x},{y}) 出现跟随光标的幽灵（{pct:.2f}%）→ 已进入放置模式")
            A.client_grab(hwnd, SHOTS / "place_mode.png")
            print("== 试着放在几个空地位置 ==")
            # 同一处可能被占（"不能放置在这儿"），多试几个点；每次放完重扫实体确认
            for (px, py) in ((640, 300), (820, 400), (500, 250), (900, 260), (620, 430)):
                A.move_cursor(*A.client_to_screen(hwnd, px, py))
                time.sleep(0.9)
                A.mouse_button("left", True); time.sleep(0.1); A.mouse_button("left", False)
                time.sleep(2.5)
                A.client_grab(hwnd, SHOTS / "placed.png")
                rescan()
                now = supers()
                if now:
                    print(f"  在 ({px},{py}) 放置成功：105050 座数 {len(now)}  {[e['guid'] for e in now]}")
                    print("  [OK] 超级生产所已经建出来")
                    A.api_post("/api/editor/locate", {"ptrHash": now[0]["ptrHash"]}, timeout=30)
                    time.sleep(2.0)
                    A.client_grab(hwnd, SHOTS / "super_built.png")
                    return 0
                print(f"  ({px},{py}) 没放下，换下一个位置")
                # 放失败后可能退出放置模式，重新点一次图标
                A.click_client(hwnd, x, y, settle=0.9)
            print("  [NO] 试了 5 个位置都没建出来")
            return 2
    print("  所有候选点都没进入放置模式（说明点到的都不是那个图标）")
    return 3


if __name__ == "__main__":
    sys.exit(main())
