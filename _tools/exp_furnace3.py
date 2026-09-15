#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""实验：放置「三乘三高炉实验」(105051) 并验证窗口能否正常打开。

背景：该建筑 = 3×3 占地 + FacilityFurnace 机制 + window_furnace 窗口。
熔炉原生是 2×2，其 CreateMaterialsPosList 按格子摆材料位置，
**对占地可能有硬假设** —— 本脚本就是来验这个组合能不能跑。

流程：
  1. 进档 → 打开建造菜单 → 「制造」分类 → 找到实验建筑图标 → 点选进放置模式
  2. 在几处空地尝试放置
  3. F10 开窗（走工厂方法，避免鼠标标定）
  4. 检查进程是否存活 + 日志有无异常

用法： python _tools/exp_furnace3.py
"""
import ctypes
import json
import sys
import time
import urllib.request
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402

SAVE = "2026-09-13_23_c9798b4a01eb4ba795638e19eb5bc488"
TARGET = 105051
SHOTS = Path(__file__).resolve().parent / "_shots"


def key(vk, wait=1.6):
    ctypes.windll.user32.keybd_event(vk, 0, 0, 0)
    time.sleep(0.08)
    ctypes.windll.user32.keybd_event(vk, 0, 2, 0)
    time.sleep(wait)


def scan():
    try:
        A.api_post("/api/editor/scan", None, timeout=280)
        for _ in range(40):
            if not (A.api_state() or {}).get("scanning"):
                break
            time.sleep(3)
    except Exception as e:
        print(f"  （重扫失败，用缓存: {type(e).__name__}）")
    d = json.loads(urllib.request.urlopen(A.API + "/api/editor/entities", timeout=120)
                   .read().decode("utf-8", "replace"))
    return {e["guid"]: e for e in d if e.get("stuffId") == TARGET}


def main():
    hwnd = A.find_window()[0]
    A.load_save(SAVE, settle=10)
    time.sleep(10)
    A.focus_window(hwnd)
    time.sleep(0.8)
    A.close_overlays(hwnd)
    time.sleep(0.6)

    before = scan()
    print(f"放置前场上 {TARGET} 座数: {len(before)}")

    # 建造菜单 → 制造分类（第三项）
    A.click_client(hwnd, 521, 878, settle=1.6)      # 建造
    A.click_client(hwnd, 382, 664, settle=1.2)      # 制造（菜单第三行）
    print("已开建造菜单并切到「制造」")

    # 逐个候选图标点，靠「跟随光标的幽灵」判断是否进了放置模式
    cands = [(x, y) for y in (566, 664, 762) for x in (838, 974, 1110, 1246, 1382)]
    placed_mode = False
    for i, (x, y) in enumerate(cands, 1):
        A.click_client(hwnd, x, y, settle=0.8)
        A.move_cursor(*A.client_to_screen(hwnd, 700, 340))
        time.sleep(0.9)
        img = A.client_grab(hwnd, SHOTS / "_f3probe.png")
        # 放置模式会出现绿色方向箭头
        from measure_grid_scale import arrow_columns
        if len(arrow_columns(img, (300, 100, 1200, 700))) >= 2:
            print(f"  候选 {i} ({x},{y}) 进入放置模式")
            placed_mode = True
            break
    if not placed_mode:
        print("没能进入放置模式（可能没找到实验建筑图标）")
        return 1

    # 试着放几处
    w, h = A.client_size(hwnd)
    spots = [(640, 300), (820, 400), (500, 250), (900, 260), (620, 430), (700, 500)]
    for (cx, cy) in spots:
        A.click_client(hwnd, cx, cy, settle=1.8)
        time.sleep(1.2)
        now = scan()
        if len(now) > len(before):
            print(f"  在 ({cx},{cy}) 放置成功：{TARGET} 座数 {len(now)}")
            break
        print(f"  ({cx},{cy}) 没放下")
    else:
        print("几个位置都没放下去")
        return 1

    # F10 开窗
    key(0x79, wait=3.0)
    A.client_grab(hwnd, SHOTS / "furnace3_window.png")
    print("已按 F10 尝试开窗，截图 furnace3_window.png")

    time.sleep(2)
    pids = A.game_pids()
    print(f"游戏进程: {pids}")
    return 0 if pids else 2


if __name__ == "__main__":
    sys.exit(main())
