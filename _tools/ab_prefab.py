#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""A/B 对比两种建筑外观：同一建筑、同一缩放，紧贴裁剪出模型区域，看像素差。

用法： python _tools/ab_prefab.py 采集营地 制造台
"""
import json
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402

REPO = Path(__file__).resolve().parent.parent
SHOTS = Path(__file__).resolve().parent / "_shots"
# 建筑固定在屏幕中心（locate 就是居中），裁一块紧贴它的区域
CROP = (int(1440 * 0.5) - 150, int(900 * 0.45) - 110, int(1440 * 0.5) + 150, int(900 * 0.45) + 110)


def entities():
    raw = urllib.request.urlopen(A.API + "/api/editor/entities", timeout=120).read()
    return json.loads(raw.decode("utf-8", "replace"))


def run_one(appearance, guid_filter=None):
    print(f"\n=== {appearance} ===")
    subprocess.run([sys.executable, str(REPO / "_tools" / "make_defs.py"),
                    "--appearance", appearance, "--deploy"],
                   capture_output=True, text=True, encoding="utf-8", errors="replace")
    A.kill_game()
    time.sleep(5)
    st = A.proxy_off_for_launch()
    A.launch_game(wait_ready=False)
    if A.wait_api(240) is None:
        A.restore_system_proxy(st)
        return None, None
    ok, _, dt = A.load_save(settle=12)
    A.restore_system_proxy(st)
    if not ok:
        print("  进档失败")
        return None, None
    time.sleep(8)
    A.api_post("/api/editor/scan", None, timeout=180)
    time.sleep(6)
    hwnd = A.find_window()[0]
    facs = [e for e in entities() if e.get("stuffId") == 105040]
    if guid_filter:
        facs = [f for f in facs if f["guid"] == guid_filter] or facs
    facs.sort(key=lambda f: f["guid"])
    f = facs[0]
    A.api_post("/api/editor/locate", {"ptrHash": f["ptrHash"]}, timeout=30)
    time.sleep(2.2)
    # 反复 ESC 关掉可能弹出的帝国地图/暂停菜单，再点一下空白地面关掉菜单
    A.focus_window(hwnd)
    for _ in range(3):
        A.key_press(hwnd, "ESC", foreground=True)
        time.sleep(0.5)
    A.click_client(hwnd, 120, 700, settle=1.0)
    A.api_post("/api/editor/locate", {"ptrHash": f["ptrHash"]}, timeout=30)
    time.sleep(2.0)
    img = A.client_grab(hwnd, SHOTS / f"ab_{appearance}.png")
    crop = img.crop(CROP)
    crop.resize((crop.width * 3, crop.height * 3)).save(SHOTS / f"ab_{appearance}_zoom.png")
    print(f"  guid={f['guid']} goName={f.get('goName')} 裁剪区={CROP}")
    return crop, f


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 1
    a, b = sys.argv[1], sys.argv[2]
    ca, fa = run_one(a)
    cb, fb = run_one(b)
    if ca is None or cb is None:
        print("有一步没跑通")
        return 1
    from PIL import ImageChops
    d = ImageChops.difference(ca.convert("RGB"), cb.convert("RGB")).convert("L").histogram()
    pct = 100.0 * sum(d[30:]) / sum(d)
    print(f"\n=== 结论 ===")
    print(f"  {a}: guid={fa['guid']} goName={fa.get('goName')}")
    print(f"  {b}: guid={fb['guid']} goName={fb.get('goName')}")
    print(f"  建筑区域像素差异: {pct:.2f}%  （>2% 基本可判定模型变了）")
    canvas = __import__("PIL.Image", fromlist=["Image"]).new("RGB", (ca.width * 3 * 2 + 20, ca.height * 3), (25, 25, 25))
    canvas.paste(ca.resize((ca.width * 3, ca.height * 3)), (5, 0))
    canvas.paste(cb.resize((cb.width * 3, cb.height * 3)), (ca.width * 3 + 15, 0))
    canvas.save(SHOTS / "ab_side_by_side.png")
    print("  对比图: _tools/_shots/ab_side_by_side.png")
    return 0


if __name__ == "__main__":
    sys.exit(main())
