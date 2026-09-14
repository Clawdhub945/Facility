#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在反编译语料库里查 window 预制体名，以及哪些窗口类带 StuffIconDropdown。

用法： python _tools/probe_code_db.py [关键字]
"""
import json
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

DB = Path(r"C:\AI\游戏向量库\code_meta.json")


def main():
    kw = sys.argv[1] if len(sys.argv) > 1 else None
    d = json.loads(DB.read_text(encoding="utf-8"))
    print(f"语料库条目 {len(d)}")

    blob = " ".join(x.get("content", "") for x in d)
    win = sorted(set(re.findall(r"(window_[a-z0-9_]{2,40})", blob)))
    print(f"\nwindow_* 名称 {len(win)} 个:")
    for w in win:
        print("  ", w)

    if kw:
        print(f"\n=== 含「{kw}」的文件 ===")
        for x in d:
            f = x.get("file", "")
            if kw.lower() in f.lower():
                print("  ", f.split("\\")[-1])


if __name__ == "__main__":
    main()
