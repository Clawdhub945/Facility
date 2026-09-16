#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""打印 MySpriteRenderer 的真实调用范例（看游戏自己怎么用）。

用法： python _tools/probe_usage.py [关键词]
"""
import json
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
DB = Path(r"C:\AI\游戏向量库\code_meta.json")


def show(d, name, maxlen=2600):
    for x in d:
        f = x.get("file", "").split("\\")[-1]
        if f == name:
            print("=" * 30, name, "=" * 30)
            c = x.get("content", "")
            print(c[:maxlen])
            print()
            return True
    print(f"（未找到 {name}）")
    return False


def main():
    kw = sys.argv[1] if len(sys.argv) > 1 else "FacilityInitBodySp"
    d = json.loads(DB.read_text(encoding="utf-8"))
    show(d, kw + ".c" if not kw.endswith(".c") else kw)


if __name__ == "__main__":
    main()
