#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""查 MySpriteRenderer.Init 的参数、以及游戏里的真实调用范例。

用法： python _tools/probe_mysprite2.py
"""
import json
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
DB = Path(r"C:\AI\游戏向量库\code_meta.json")


def main():
    d = json.loads(DB.read_text(encoding="utf-8"))

    print("=" * 70)
    print("一、Init 的签名（决定怎么创建）")
    print("=" * 70)
    for x in d:
        f = x.get("file", "").split("\\")[-1]
        if not f.startswith("MySpriteRendererInit"):
            continue
        c = x.get("content", "")
        sig = c[:c.find("{")] if "{" in c else c[:400]
        print(f"\n--- {f} ---")
        print(" ".join(sig.split())[:500])
        fields = sorted(set(re.findall(r"fields[.\->]+([a-zA-Z_][a-zA-Z0-9_]*)", c)))
        print("  访问字段:", ", ".join(fields[:20]))

    print("\n" + "=" * 70)
    print("二、游戏里的真实调用范例（谁调了 MySpriteRenderer.Create/Init）")
    print("=" * 70)
    callers = []
    for x in d:
        c = x.get("content", "")
        if "MySpriteRenderer__Create" not in c and "MySpriteRenderer__Init" not in c:
            continue
        f = x.get("file", "").split("\\")[-1]
        if f.startswith("MySpriteRenderer"):
            continue
        callers.append(f)
    seen = []
    for f in callers:
        if f in seen:
            continue
        seen.append(f)
    print(f"共 {len(seen)} 个调用方，列出前 25 个：")
    for f in seen[:25]:
        print("   ", f)


if __name__ == "__main__":
    main()
