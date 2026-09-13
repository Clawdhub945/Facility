#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""点一下额外产品选择条，验证「窗口点击 → 写 cfg」链路。

用法： python _tools/click_selector.py [x] [y]
缺省坐标 (355,368) = 综合生产所窗口里「额外产品：…」选择条的中心（1440×1080 客户区）。
"""
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"


def main():
    x = int(sys.argv[1]) if len(sys.argv) > 2 else 355
    y = int(sys.argv[2]) if len(sys.argv) > 2 else 368
    win = A.find_window()
    if not win:
        print("找不到游戏窗口")
        return 2
    hwnd = win[0]
    A.focus_window(hwnd)
    time.sleep(0.6)

    before = A.read_cfg().get("额外产品")
    sx, sy = A.client_to_screen(hwnd, x, y)
    A.move_cursor(sx, sy)
    time.sleep(0.25)
    A.mouse_button("left", True)
    time.sleep(0.08)
    A.mouse_button("left", False)
    time.sleep(1.2)
    after = A.read_cfg().get("额外产品")

    ok = before != after
    print("点击 client({}, {})".format(x, y))
    print("额外产品: {} -> {}  {}".format(before, after, "生效" if ok else "没变"))
    A.client_grab(hwnd, SHOTS / "fac_click2.png")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
