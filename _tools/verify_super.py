#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""全自动验证「超级生产所」：

  1. 重启游戏 + 进档（关系统代理）
  2. 打开建造菜单 → 食品 → 选中「超级生产所」（多候选点重试，以日志/像素为准）
  3. 在空地放下建筑（左键）
  4. 用重扫后的实体列表确认 105050 真的建出来了
  5. 相机居中到它，截图，用「红砖色指纹」判断自定义外观是否渲染出来

用法： python _tools/verify_super.py
"""
import json
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402
from check_texture import count_brick  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"
BUILD_BTN = (521, 878)
FOOD_TAB = (382, 601)
SUPER_ICON = (545, 570)


def entities():
    raw = urllib.request.urlopen(A.API + "/api/editor/entities", timeout=120).read()
    return json.loads(raw.decode("utf-8", "replace"))


def rescan():
    A.api_post("/api/editor/scan", None, timeout=200)
    for _ in range(30):
        st = A.api_state() or {}
        if not st.get("scanning"):
            return True
        time.sleep(3)
    return False


def supers():
    return [e for e in entities() if e.get("stuffId") == 105050]


def main():
    hwnd = A.find_window()[0]
    print("=== 1. 重启 + 进档 ===")
    A.kill_game()
    time.sleep(6)
    st = A.proxy_off_for_launch()
    A.launch_game(wait_ready=False)
    if A.wait_api(240) is None:
        A.restore_system_proxy(st)
        print("游戏没起来")
        return 1
    ok, s, dt = A.load_save(settle=14)
    A.restore_system_proxy(st)
    print(f"  进档 ok={ok} {dt:.1f}s")
    if not ok:
        return 1
    time.sleep(10)
    rescan()
    print(f"  当前 105050 座数: {len(supers())}")

    print("=== 2. 建造菜单 → 食品 → 超级生产所 ===")
    A.focus_window(hwnd)
    time.sleep(1.0)
    A.click_client(hwnd, *BUILD_BTN, settle=2.0)
    img = A.client_grab(hwnd, SHOTS / "super_menu.png")
    A.click_client(hwnd, *FOOD_TAB, settle=1.2)
    A.click_client(hwnd, *SUPER_ICON, settle=1.6)
    img2 = A.client_grab(hwnd, SHOTS / "super_picked.png")

    # 判断有没有进入放置模式：光标在两处空地，看有没有跟随的幽灵
    from PIL import ImageChops
    A.move_cursor(*A.client_to_screen(hwnd, 640, 320)); time.sleep(1.4)
    a = A.client_grab(hwnd, SHOTS / "_sup_a.png")
    A.move_cursor(*A.client_to_screen(hwnd, 780, 380)); time.sleep(1.4)
    b = A.client_grab(hwnd, SHOTS / "_sup_b.png")
    d = ImageChops.difference(a.convert("RGB"), b.convert("RGB")).convert("L")
    pct = 100.0 * sum(d.histogram()[30:]) / sum(d.histogram())
    print(f"  放置幽灵检测: {pct:.2f}% 变化  bbox={d.getbbox()}")
    if pct <= 0.2:
        print("  [NO] 没进入放置模式 —— 建造菜单/图标坐标需要人工确认（已存 super_menu.png）")
        return 2
    print("  [OK] 已进入放置模式")

    print("=== 3. 放下建筑 ===")
    A.move_cursor(*A.client_to_screen(hwnd, 700, 350)); time.sleep(1.0)
    A.mouse_button("left", True); time.sleep(0.1); A.mouse_button("left", False)
    time.sleep(2.5)
    A.client_grab(hwnd, SHOTS / "super_placed.png")

    print("=== 4. 重扫确认 105050 出现 ===")
    before = len(supers())
    rescan()
    now = supers()
    print(f"  105050 座数: {before} → {len(now)}  {[e['guid'] for e in now]}")
    if not now:
        print("  [NO] 没建出来（可能被占位/资源不足挡住，看 super_placed.png）")
        return 3
    print("  [OK] 超级生产所已存在于场景")

    print("=== 5. 居中截图 + 红砖指纹 ===")
    A.api_post("/api/editor/locate", {"ptrHash": now[0]["ptrHash"]}, timeout=30)
    time.sleep(2.2)
    A.focus_window(hwnd); time.sleep(0.8)
    im = A.client_grab(hwnd, SHOTS / "super_world.png")
    n = count_brick(im, (0, 0, int(im.width * 0.62), int(im.height * 0.65)))
    print(f"  世界区红砖像素: {n}")
    print("  " + ("[OK] 自定义红砖外观已渲染" if n > 60 else "[NO] 没看到红砖外观（仍是原版贴图）"))
    return 0 if n > 60 else 4


if __name__ == "__main__":
    sys.exit(main())
