#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""侦察脚本：汇总新建建筑所需的全部信息（id 冲突、菜单分类、科技树可用 id 段）。

用法： python _tools/recon_new_building.py
"""
import collections
import json
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
EXTRA = Path(r"C:\AI\领地部分源码(AI注释)\ExtraData")
WS = Path(r"C:\Program Files (x86)\Steam\steamapps\workshop\content\1455910")
REPO = Path(__file__).resolve().parent.parent


def load(p):
    return json.loads(Path(p).read_text(encoding="utf-8"))


def all_ids(table, key):
    ids = {r.get(key) for r in load(EXTRA / f"{table}.json")}
    for mod in WS.iterdir():
        f = mod / "Defs" / f"{table}.json"
        if f.exists():
            try:
                ids |= {r.get(key) for r in load(f)}
            except Exception:
                pass
    f = REPO / "Defs" / f"{table}.json"
    if f.exists():
        try:
            ids |= {r.get(key) for r in load(f)}
        except Exception:
            pass
    return {i for i in ids if isinstance(i, int)}


def main():
    print("=== 105050 是否已被占用 ===")
    for table, key in (("stuff", "stuff_id"), ("build", "id"), ("tech", "facility_id"),
                       ("career", "facility_id"), ("blueprint", "facility_id")):
        used = 105050 in all_ids(table, key)
        print(f"  {table}.{key}: {'❌ 已占用' if used else '✔ 空闲'}")

    print("\n=== 官方 tech.json 的菜单分类（txt_id → facility 数）===")
    tech = load(EXTRA / "tech.json")
    cnt = collections.Counter(r.get("txt_id") for r in tech)
    for k in sorted(cnt):
        print(f"  txt_id={k}  {cnt[k]} 个设施")

    print("\n=== txt_id 300（食品）分类下的设施 ===")
    stuff = {r["stuff_id"]: r.get("stuff_namezh-CN") for r in load(EXTRA / "stuff.json")}
    for r in tech:
        if r.get("txt_id") == 300:
            print(f"  facility={r.get('facility_id')} {stuff.get(r.get('facility_id'))} "
                  f"tech_id={r.get('tech_id')}")

    print("\n=== 科技树 id 占用情况 ===")
    tree = load(EXTRA / "tech_tree.json")
    tids = sorted(r["tech_id"] for r in tree)
    print(f"  官方 {len(tids)} 个节点，范围 {tids[0]}..{tids[-1]}")
    for mod in WS.iterdir():
        f = mod / "Defs" / "tech_tree.json"
        if f.exists():
            print(f"  工坊 {mod.name} 也有 tech_tree.json")
    print("  9 亿段（900000+）是否被用:", [t for t in tids if t >= 900000][:10])

    print("\n=== 造一个 900000 段候选 id 的冲突检查 ===")
    for cand in (909050, 909051, 910050):
        print(f"  {cand}: {'已用' if cand in tids else '空闲'}")

    print("\n=== 现有 105040 的 Def 行（新建筑照此复制改 id）===")
    for t in ("stuff", "build", "tech", "career", "blueprint"):
        rows = load(REPO / "Defs" / f"{t}.json")
        print(f"  {t}.json: {len(rows)} 行")
        for r in rows[:1]:
            print("    ", json.dumps(r, ensure_ascii=False)[:200])


if __name__ == "__main__":
    main()
