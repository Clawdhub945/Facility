#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从反编译语料库里提取某个类的字段访问，找出「配方下拉」到底挂在哪个组件上。

用法： python _tools/probe_fields.py WindowFurnace
"""
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
DB = Path(r"C:\AI\游戏向量库\code_meta.json")


def main():
    kw = sys.argv[1] if len(sys.argv) > 1 else "WindowFurnace"
    d = json.loads(DB.read_text(encoding="utf-8"))
    per_file = defaultdict(set)
    allf = set()
    for x in d:
        f = x.get("file", "").split("\\")[-1]
        if kw.lower() not in f.lower():
            continue
        c = x.get("content", "")
        for m in re.findall(r"fields\.([a-zA-Z_][a-zA-Z0-9_]*)", c):
            per_file[f].add(m)
            allf.add(m)
    print(f"=== {kw} 相关文件 {len(per_file)} 个 ===")
    for f in sorted(per_file):
        print(f"\n--- {f} ---")
        print("   ", ", ".join(sorted(per_file[f])))
    print(f"\n=== 全部字段（{len(allf)} 个）===")
    print("   ", ", ".join(sorted(allf)))
    # 挑出可能和「配方/下拉」有关的
    hit = [f for f in allf if any(t in f.lower() for t in
           ("formula", "drop", "dp_", "blueprint", "list", "option", "mode", "select"))]
    print(f"\n=== 疑似「配方/下拉」相关（{len(hit)} 个）===")
    print("   ", ", ".join(sorted(hit)))


if __name__ == "__main__":
    main()
