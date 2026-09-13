#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""单点联调：开建筑窗口 → 截图 → F8 程序化点击 → 读 cfg。逐步打印，便于定位卡在哪一步。

用法： python _tools/dbg_click.py [facility_index]
"""
import json
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"


def main():
    idx = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    win = A.find_window()
    if not win:
        print("找不到游戏窗口")
        return 2
    hwnd = win[0]
    print("窗口:", win, "客户区:", A.client_size(hwnd))

    raw = urllib.request.urlopen(A.API + "/api/editor/entities", timeout=60).read()
    facs = [e for e in json.loads(raw.decode("utf-8", "replace")) if e.get("stuffId") == 105040]
    print("综合生产所:", [f["guid"] for f in facs])
    f = facs[idx]

    watcher = A.LogWatcher(A.BEPINEX_LOG, from_end=True)

    print("\n[1] 清理挡屏面板（UnityExplorer F7 / 游戏内弹窗 ESC）")
    A.close_overlays(hwnd)
    print("   ", A.api_post("/api/editor/locate", {"ptrHash": f["ptrHash"]}, timeout=30))
    time.sleep(1.8)

    print("[2] 前台 + 点击建筑")
    A.focus_window(hwnd)
    time.sleep(0.7)
    w, h = A.client_size(hwnd)
    sx, sy = A.client_to_screen(hwnd, w // 2, int(h * 0.45))
    A.move_cursor(sx, sy)
    time.sleep(0.3)
    A.mouse_button("left", True)
    time.sleep(0.09)
    A.mouse_button("left", False)
    time.sleep(1.8)

    img = A.client_grab(hwnd, SHOTS / "dbg_open.png")
    print("[3] 截图已存 dbg_open.png；日志里的选择条坐标:")
    lines = watcher.poll()
    pos, res = None, None
    for l in lines:
        if "选择条屏幕坐标" in l:
            print("   ", l.split("] ", 1)[-1])
    import re
    for l in reversed(lines):
        m = re.search(r"选择条屏幕坐标=(-?\d+),(-?\d+)(?:\s*客户区=(\d+)x(\d+))?", l)
        if m:
            pos = (int(m.group(1)), int(m.group(2)))
            res = (int(m.group(3)), int(m.group(4))) if m.group(3) else None
            break
    print("   解析:", pos, "游戏分辨率:", res, "窗口客户区:", A.client_size(hwnd))

    print("\n[4] F8 程序化点击（走 Unity 事件系统）")
    cfg_before = A.read_cfg().get("额外产品")
    A.focus_window(hwnd)
    time.sleep(0.4)
    A.key_press(hwnd, "F8", foreground=True)
    time.sleep(1.6)
    cfg_after = A.read_cfg().get("额外产品")
    print(f"   cfg 额外产品: {cfg_before} -> {cfg_after}")
    for l in watcher.poll([r"测试点击", r"FacilityUI"]):
        print("   日志:", l.split("] ", 1)[-1])

    print("\n[5] 真鼠标点击选择条位置")
    if pos:
        cw, ch = A.client_size(hwnd)
        # 若游戏分辨率与客户区不一致，按比例换算
        cx, cy = pos
        if res and res[0] > 0 and (res[0] != cw or res[1] != ch):
            cx = int(round(pos[0] * cw / res[0]))
            cy = int(round(pos[1] * ch / res[1]))
            print(f"   换算: {pos} → ({cx},{cy})")
        sx, sy = A.client_to_screen(hwnd, cx, cy)
        print(f"   移动光标到屏幕 ({sx},{sy})")
        A.move_cursor(sx, sy)
        time.sleep(0.35)
        A.mouse_button("left", True)
        time.sleep(0.09)
        A.mouse_button("left", False)
        time.sleep(1.5)
        print(f"   cfg 额外产品: {cfg_after} -> {A.read_cfg().get('额外产品')}")
        A.client_grab(hwnd, SHOTS / "dbg_after_click.png")
    return 0


if __name__ == "__main__":
    sys.exit(main())
