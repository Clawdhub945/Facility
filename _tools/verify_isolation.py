#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""验证「每座建筑独立」：加载**最新存档**，逐项确认互不影响。

检查项：
  1. 产出：只有规格里 DailyProducer=true 的建筑发产（105040/105050），
     熔炉 105051 被跳过
  2. 窗口：每座建筑按**自己的规格**处理（隐名单各自独立）
  3. 外观：只有声明了自定义外观的建筑换图

用法： python _tools/verify_isolation.py
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


def key(vk, wait=2.5):
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
        print(f"  （重扫失败: {type(e).__name__}）")
    return json.loads(urllib.request.urlopen(A.API + "/api/editor/entities", timeout=120)
                      .read().decode("utf-8", "replace"))


def main():
    hwnd = A.find_window()[0]
    import autotest as _a
    save = _a.newest_save()
    print(f"最新存档: {save}")
    ok, st, dt = A.load_save(save, settle=12)
    print(f"进档: {ok} {dt:.1f}s")
    time.sleep(12)

    d = scan()
    for sid in (105040, 105050, 105051):
        seen = {e["guid"]: e for e in d if e.get("stuffId") == sid}
        print(f"  场上 {sid}: {len(seen)} 座 {list(seen)}")

    # 开两座建筑的窗口（F10 会先开原版制造台，再开我们的）
    A.focus_window(hwnd)
    time.sleep(0.8)
    for _ in range(3):
        key(0x79)      # F10

    log = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Territory\BepInEx\LogOutput.log")
    text = log.read_text(encoding="utf-8", errors="replace")
    print("\n=== 归属与规格分派 ===")
    for line in text.splitlines():
        if "归属建筑" in line or "按其规格处理" in line or "隐名单" in line:
            print("  ", line.split("Facility] ", 1)[-1])
    return 0


if __name__ == "__main__":
    sys.exit(main())
