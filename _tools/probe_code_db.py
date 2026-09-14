#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""在反编译语料库里检索指定类/方法的实现，并抽取其中的字符串常量线索。

用法：
    python _tools/probe_code_db.py                      # 概览
    python _tools/probe_code_db.py --find WindowWorkshop # 列出相关文件
    python _tools/probe_code_db.py --show WindowWorkshopInitWorkshopOptionData
    python _tools/probe_code_db.py --strings WindowWorkshop  # 该文件里的可读字符串
"""
import argparse
import json
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")

DB = Path(r"C:\AI\游戏向量库\code_meta.json")


def load():
    return json.loads(DB.read_text(encoding="utf-8"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--find", help="列出文件名含该关键字的条目")
    ap.add_argument("--show", help="打印文件名含该关键字的完整实现")
    ap.add_argument("--strings", help="打印该关键字相关文件里的可读字符串常量")
    ap.add_argument("--limit", type=int, default=4000)
    a = ap.parse_args()

    d = load()
    if a.find:
        n = 0
        for x in d:
            f = x.get("file", "")
            if a.find.lower() in f.lower():
                print("  ", f.split("\\")[-1])
                n += 1
        print(f"共 {n} 个")
        return

    if a.show:
        for x in d:
            f = x.get("file", "")
            if a.show.lower() in f.lower():
                print("=" * 25, f.split("\\")[-1], "=" * 25)
                print(x.get("content", "")[: a.limit])
                print()
        return

    if a.strings:
        pat = re.compile(r'"([^"\\]{2,60})"')
        seen = set()
        for x in d:
            f = x.get("file", "")
            if a.strings.lower() not in f.lower():
                continue
            for s in pat.findall(x.get("content", "")):
                if s not in seen:
                    seen.add(s)
        for s in sorted(seen):
            print("  ", repr(s))
        print(f"共 {len(seen)} 个字符串")
        return

    print(f"语料库条目 {len(d)}；用 --find / --show / --strings 查询")


if __name__ == "__main__":
    main()
