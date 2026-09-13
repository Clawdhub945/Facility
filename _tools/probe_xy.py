#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""坐标微调工具：把鼠标移到给定客户区坐标，截图并画出十字准星，供人工/AI 校准。

用法： python _tools/probe_xy.py X Y [输出名]
"""
import ctypes
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    x, y = int(sys.argv[1]), int(sys.argv[2])
    name = sys.argv[3] if len(sys.argv) > 3 else f"probe_{x}_{y}"
    win = A.find_window()
    if not win:
        print("找不到游戏窗口")
        return 2
    hwnd = win[0]
    A.focus_window(hwnd)
    time.sleep(0.5)
    sx, sy = A.client_to_screen(hwnd, x, y)
    A.move_cursor(sx, sy)
    time.sleep(0.8)
    p = A.wt.POINT()
    A.user32.GetCursorPos(ctypes.byref(p))
    print(f"目标 client=({x},{y}) -> screen=({sx},{sy})；实际光标=({p.x},{p.y})")

    img = A.client_grab(hwnd, SHOTS / f"{name}.png")
    # 画准星（红 40px）
    from PIL import ImageDraw
    d = ImageDraw.Draw(img)
    d.line([(x - 25, y), (x + 25, y)], fill=(255, 0, 0), width=2)
    d.line([(x, y - 25), (x, y + 25)], fill=(255, 0, 0), width=2)
    img.save(SHOTS / f"{name}_cross.png")
    # 再存一张 4 倍放大的局部
    img.crop((max(0, x - 160), max(0, y - 80), x + 160, y + 80)).resize((1280, 640)).save(
        SHOTS / f"{name}_zoom.png")
    print("saved:", SHOTS / f"{name}_cross.png")
    return 0


if __name__ == "__main__":
    sys.exit(main())
