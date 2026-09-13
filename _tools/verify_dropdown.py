#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证下拉框：反复尝试直到建筑窗口真的开出来（以日志里的「选择条屏幕坐标」为准），
然后点标题条展开、截图、再点某一项，最后打印结果。

用法： python _tools/verify_dropdown.py
"""
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402
from find_bench import find_bench_clusters  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"


def entities():
    raw = urllib.request.urlopen(A.API + "/api/editor/entities", timeout=120).read()
    return json.loads(raw.decode("utf-8", "replace"))


def wait_bar_pos(watcher, timeout=4.0):
    """等日志里出现选择条坐标（= 建筑窗口真的开了）。"""
    t0 = time.time()
    pos = None
    while time.time() - t0 < timeout:
        for l in watcher.poll():
            m = re.search(r"选择条屏幕坐标=(-?\d+),(-?\d+)", l)
            if m:
                pos = (int(m.group(1)), int(m.group(2)))
        if pos:
            return pos
        time.sleep(0.4)
    return pos


def main():
    hwnd = A.find_window()[0]
    watcher = A.LogWatcher(A.BEPINEX_LOG, from_end=True)
    facs = sorted([e for e in entities() if e.get("stuffId") == 105040],
                  key=lambda e: e["guid"])
    if not facs:
        print("场景里没有 105040")
        return 1
    print("综合生产所:", [f["guid"] for f in facs])

    pos = None
    for attempt in range(1, 9):
        f = facs[(attempt - 1) % len(facs)]
        A.close_overlays(hwnd)
        A.api_post("/api/editor/locate", {"ptrHash": f["ptrHash"]}, timeout=30)
        time.sleep(1.8)
        A.focus_window(hwnd)
        time.sleep(0.5)
        img = A.client_grab(hwnd, SHOTS / "_verify_scan.png")
        # 先试「相机居中后建筑就在屏幕中心」——`locate` 的原意就是这个（实测常有效）
        w, h = A.client_size(hwnd)
        tries = [(w // 2, int(h * 0.45))]
        # 不行再试白色台面簇（村里的床/桌子也是白的，所以放后面）
        tries += [(int(c["cx"]), int(c["cy"])) for c in find_bench_clusters(img)[:5]]
        print(f"  尝试 {attempt}：建筑 {f['guid']}，{len(tries)} 个候选点")
        for (cx, cy) in tries:
            watcher.poll()
            A.click_client(hwnd, cx, cy, settle=1.4)
            pos = wait_bar_pos(watcher, timeout=2.5)
            if pos:
                print(f"    点中 ({cx},{cy}) → 下拉框坐标 {pos}")
                break
        if pos:
            break
    if not pos:
        print("窗口一直没开出来")
        return 1

    print("\n[1] 点标题条展开")
    cfg0 = A.read_cfg().get("额外产品")
    watcher.poll()
    A.click_client(hwnd, pos[0], pos[1], settle=1.3)
    logs = [l.split("] ", 1)[-1] for l in watcher.poll([r"下拉框"])]
    A.client_grab(hwnd, SHOTS / "verify_dropdown_open.png")
    print("  日志:", logs)
    print(f"  cfg: {cfg0} -> {A.read_cfg().get('额外产品')}（展开不该改配置）")

    print("\n[2] 点第 2 行选项（Row_616001 铁矿）")
    row_y = pos[1] + 28 + 1 + 2 + 24 + 12
    A.click_client(hwnd, pos[0], row_y, settle=1.3)
    logs = [l.split("] ", 1)[-1] for l in watcher.poll([r"下拉框"])]
    cfg1 = A.read_cfg().get("额外产品")
    A.client_grab(hwnd, SHOTS / "verify_dropdown_selected.png")
    print("  日志:", logs)
    print(f"  cfg: {cfg1}")

    print("\n[3] F8 钩子（展开 + 选下一项）")
    A.focus_window(hwnd)
    time.sleep(0.4)
    A.key_press(hwnd, "F8", foreground=True)
    time.sleep(1.6)
    logs = [l.split("] ", 1)[-1] for l in watcher.poll([r"测试点击下拉框"])]
    cfg2 = A.read_cfg().get("额外产品")
    print("  日志:", logs)
    print(f"  cfg: {cfg2}")

    ok = (cfg1 != cfg0) and (cfg2 != cfg1)
    print("\n=== 结论 ===")
    print(f"  展开 → 选项点击 → F8 钩子：{cfg0} → {cfg1} → {cfg2}")
    print("  " + ("全部生效 ✔" if ok else "有不生效的步骤 ✘"))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
