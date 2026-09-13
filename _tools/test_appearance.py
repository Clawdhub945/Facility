#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""逐个试建筑外观预设：能不能进档、模型有没有变。

对每个预设：生成 Defs → 重启游戏 → 读档 → 如果进得了档，就把它和「采集营地」基准图对比像素。

用法： python _tools/test_appearance.py [预设名 ...]      # 缺省=全部
"""
import hashlib
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
PRESETS = ["采集营地", "磨坊", "交易台", "制造台", "熔炉"]


def deploy(appearance):
    r = subprocess.run([sys.executable, str(REPO / "_tools" / "make_defs.py"),
                        "--appearance", appearance, "--deploy"],
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    return r.returncode == 0, (r.stdout or "").strip().splitlines()[:1]


def entities():
    raw = urllib.request.urlopen(A.API + "/api/editor/entities", timeout=120).read()
    return json.loads(raw.decode("utf-8", "replace"))


def shot_of_facility(hwnd, tag):
    """把相机居中到第一座综合生产所，截图存盘，返回 (图, 设施列表)。

    ⚠ 定位后**必须再清一次弹窗**：`locate` 只是移相机，屏幕上可能还压着
    帝国地图/帝国面板（实测第一版截到的全是羊皮纸地图，看不到建筑）。
    """
    facs = [e for e in entities() if e.get("stuffId") == 105040]
    if not facs:
        return None, []
    A.api_post("/api/editor/locate", {"ptrHash": facs[0]["ptrHash"]}, timeout=30)
    time.sleep(2.0)
    A.close_overlays(hwnd)
    time.sleep(0.5)
    A.focus_window(hwnd)
    time.sleep(0.6)
    return A.client_grab(hwnd, SHOTS / f"appearance_{tag}.png"), facs


def main():
    presets = sys.argv[1:] or PRESETS
    results = {}
    for tag in presets:
        print(f"\n=== 外观预设：{tag} ===")
        ok, out = deploy(tag)
        print("  部署:", ok, out)
        A.kill_game()
        time.sleep(4)
        st = A.proxy_off_for_launch()
        A.launch_game(wait_ready=False)
        api = A.wait_api(240)
        if api is None:
            A.restore_system_proxy(st)
            results[tag] = "游戏没起来"
            print("  → 游戏没起来")
            continue
        loaded, state, dt = A.load_save()
        A.restore_system_proxy(st)
        if not loaded:
            results[tag] = "卡在加载中（进不了档）"
            print(f"  → 进档失败（{dt:.0f}s），判定该 prefab 不被接受")
            continue
        print(f"  → 进档成功（{dt:.1f}s）")
        time.sleep(8)
        A.api_post("/api/editor/scan", None, timeout=180)
        time.sleep(6)
        hwnd = A.find_window()[0]
        img, facs = shot_of_facility(hwnd, tag)
        if img is None:
            results[tag] = "进档了但找不到 105040 建筑"
            print("  → 找不到建筑")
            continue
        results[tag] = f"OK，{len(facs)} 座，goName={facs[0].get('goName')}"
        print(f"  → {results[tag]}")

    # 汇总 + 与基准图比像素
    print("\n=== 汇总 ===")
    base = SHOTS / f"appearance_{presets[0]}.png"
    for tag in presets:
        r = results.get(tag, "-")
        diff = ""
        p = SHOTS / f"appearance_{tag}.png"
        if base.exists() and p.exists() and tag != presets[0]:
            try:
                from PIL import Image
                a = Image.open(base).convert("RGB")
                b = Image.open(p).convert("RGB")
                if a.size == b.size:
                    from PIL import ImageChops
                    d = ImageChops.difference(a, b).convert("L").histogram()
                    diff = f"  与基准差异 {100.0 * sum(d[30:]) / sum(d):.2f}%"
            except Exception as e:
                diff = f"  (比对失败 {e})"
        print(f"  {tag}: {r}{diff}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
