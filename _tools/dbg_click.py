#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""单点联调：开建筑窗口 → 点下拉框标题条 → 点选项 → 读 cfg。逐步打印。

用法： python _tools/dbg_click.py [facility_index]
"""
import json
import re
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

    print("\n[1] 清理挡屏面板 + locate")
    A.close_overlays(hwnd)
    print("   ", A.api_post("/api/editor/locate", {"ptrHash": f["ptrHash"]}, timeout=30))
    time.sleep(1.8)

    print("[2] 点建筑开窗")
    A.focus_window(hwnd)
    time.sleep(0.6)
    w, h = A.client_size(hwnd)
    A.click_client(hwnd, w // 2, int(h * 0.45), settle=1.8)
    A.client_grab(hwnd, SHOTS / "dbg_open.png")

    lines = watcher.poll()
    pos = None
    for l in reversed(lines):
        m = re.search(r"选择条屏幕坐标=(-?\d+),(-?\d+)", l)
        if m:
            pos = (int(m.group(1)), int(m.group(2)))
            break
    print("   下拉框标题条坐标:", pos, "客户区:", A.client_size(hwnd))
    if not pos:
        print("   没找到下拉框——窗口没开出来")
        return 1

    print("\n[3] 点标题条（应展开列表，cfg 不变）")
    cfg0 = A.read_cfg().get("额外产品")
    watcher.poll()
    A.click_client(hwnd, pos[0], pos[1], settle=1.2)
    A.client_grab(hwnd, SHOTS / "dbg_dropdown.png")
    for l in watcher.poll([r"下拉框"]):
        print("   日志:", l.split("] ", 1)[-1])
    print("   cfg:", cfg0, "->", A.read_cfg().get("额外产品"))

    print("\n[4] 点第 1 行选项（Row_0 = 无）")
    row_y = pos[1] + 28 + 1 + 2 + 12
    A.click_client(hwnd, pos[0], row_y, settle=1.3)
    A.client_grab(hwnd, SHOTS / "dbg_option.png")
    for l in watcher.poll([r"下拉框"]):
        print("   日志:", l.split("] ", 1)[-1])
    print("   cfg:", A.read_cfg().get("额外产品"))

    print("\n[5] F8 钩子（展开+选下一项）")
    A.focus_window(hwnd)
    time.sleep(0.4)
    A.key_press(hwnd, "F8", foreground=True)
    time.sleep(1.5)
    for l in watcher.poll([r"测试点击下拉框"]):
        print("   日志:", l.split("] ", 1)[-1])
    print("   cfg:", A.read_cfg().get("额外产品"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
