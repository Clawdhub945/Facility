#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""抓一张「干净」的游戏世界截图：反复清弹窗 + 连拍，挑出建筑可见的那一帧。

背景：本机并存一个「帝国」类 mod，会周期性弹出帝国地图/面板盖住世界，
      单张截图经常只剩羊皮纸地图，看不到建筑本体。

用法： python _tools/shot_world.py <标签> [张数]
"""
import sys
import time
import urllib.request
import json
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"


def busy_ratio(img):
    """深色 UI 面板占比（羊皮纸地图是亮色、游戏世界偏暗）。"""
    px = img.convert("L").resize((320, 200)).getdata()
    total = len(px)
    dark = sum(1 for v in px if v < 90)
    papyrus = sum(1 for v in px if v > 150)
    return dark / total, papyrus / total


def main():
    tag = sys.argv[1] if len(sys.argv) > 1 else "world"
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 8
    hwnd = A.find_window()[0]
    A.close_overlays(hwnd)
    raw = urllib.request.urlopen(A.API + "/api/editor/entities", timeout=60).read()
    facs = [e for e in json.loads(raw.decode("utf-8", "replace")) if e.get("stuffId") == 105040]
    if not facs:
        print("场景里没有 105040")
        return 1
    A.api_post("/api/editor/locate", {"ptrHash": facs[0]["ptrHash"]}, timeout=30)
    time.sleep(1.8)
    A.focus_window(hwnd)

    best = None
    for i in range(n):
        A.close_overlays(hwnd, times=1, settle=0.4)
        time.sleep(0.6)
        p = SHOTS / f"_{tag}_cand{i}.png"
        img = A.client_grab(hwnd, p)
        dark, papyrus = busy_ratio(img)
        score = dark - papyrus            # 越大越像「正常世界」
        print(f"  第{i + 1}张: 深色 {dark:.2f} 羊皮纸 {papyrus:.2f} 分数 {score:.2f}")
        if best is None or score > best[0]:
            best = (score, img, f"{tag}_cand{i}")
    out = SHOTS / f"{tag}.png"
    best[1].save(out)
    for i in range(n):
        q = SHOTS / f"_{tag}_cand{i}.png"
        if q.exists():
            q.unlink()
    print(f"  选中 {best[2]} → {out}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
