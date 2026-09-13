#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在客户区截图里找「白色台面」簇（综合生产所/制造台的台面），返回候选中心点。

用法（库）： from find_bench import find_bench_clusters; find_bench_clusters(img)
"""
from pathlib import Path


def find_bench_clusters(img, region=None, thresh=150, cluster_dist=40, min_px=60):
    """返回按像素数降序的簇列表 [{'cx','cy','n','bbox'}, …]。

    判据：R/G/B 都 > thresh 的像素（游戏里的台面/桌面是亮白色，
    地板砖约 (45,54,80)、墙约 (33,38,58)，区分度很高）。
    排除区域默认去掉顶部资源栏、左侧面板、右下小地图/底部工具栏。
    """
    w, h = img.size
    l, t, r, b = region or (int(w * 0.20), int(h * 0.13), int(w * 0.70), int(h * 0.62))
    px = img.convert("RGB").load()

    pts = [(x, y) for y in range(t, b) for x in range(l, r)
           if px[x, y][0] > thresh and px[x, y][1] > thresh and px[x, y][2] > thresh]

    clusters = []
    for (x, y) in pts:
        for c in clusters:
            if abs(c["cx"] - x) < cluster_dist and abs(c["cy"] - y) < cluster_dist:
                c["pts"].append((x, y))
                c["cx"] = sum(p[0] for p in c["pts"]) / len(c["pts"])
                c["cy"] = sum(p[1] for p in c["pts"]) / len(c["pts"])
                break
        else:
            clusters.append({"pts": [(x, y)], "cx": x, "cy": y})

    out = []
    for c in clusters:
        if len(c["pts"]) < min_px:
            continue
        xs = [p[0] for p in c["pts"]]
        ys = [p[1] for p in c["pts"]]
        out.append({"cx": c["cx"], "cy": c["cy"], "n": len(c["pts"]),
                    "bbox": (min(xs), min(ys), max(xs), max(ys))})
    out.sort(key=lambda c: -c["n"])
    return out


if __name__ == "__main__":
    import sys
    from PIL import Image
    p = Path(sys.argv[1])
    img = Image.open(p)
    for c in find_bench_clusters(img)[:8]:
        print(f"簇 {c['n']:4d}px  中心 ({c['cx']:.0f},{c['cy']:.0f})  bbox={c['bbox']}")
