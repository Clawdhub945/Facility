#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""逆向 MySpriteRenderer（游戏自绘 UI 用的批渲染组件）。

目标：搞清怎么用它创建/配置一个位图元素（面板底、图标、文字），
以便我们的 UI 模板走"游戏自己的渲染系统"这条路（标准 uGUI 在这个游戏里量不出尺寸）。

用法： python _tools/probe_mysprite.py
"""
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
DB = Path(r"C:\AI\游戏向量库\code_meta.json")

# 关注的方法名（创建/初始化/设置）
KEY_METHODS = [
    "MySpriteRenderer__Create", "MySpriteRenderer__Init", "MySpriteRenderer__SetInfo",
    "MySpriteRenderer__InitBody", "MySpriteRenderer__InitPart",
    "MySpriteRenderer__SetFixSortingOrderByParent", "MySpriteRenderer__OnPositionChange",
    "MySpriteRenderer__get_sprite_id", "MySpriteRenderer__set_sprite_id",
    "MySpriteRenderer__ctor", "SpriteManager__Get", "SpriteManager__TryGetSpriteId",
    "SpriteManager__AddSpriteToDic",
]


def main():
    d = json.loads(DB.read_text(encoding="utf-8"))

    print("=" * 70)
    print("一、MySpriteRenderer 各方法的实现（找创建入口）")
    print("=" * 70)
    shown = set()
    for x in d:
        f = x.get("file", "").split("\\")[-1]
        base = f[:-2] if f.endswith(".c") else f
        if base in shown:
            continue
        if not base.startswith("MySpriteRenderer"):
            continue
        shown.add(base)
        c = x.get("content", "")
        # 只打印方法签名 + 关键字段访问
        sig = c[:c.find("{")] if "{" in c else c[:200]
        fields = sorted(set(re.findall(r"fields[.\->]+([a-zA-Z_][a-zA-Z0-9_]*)", c)))
        calls = sorted(set(re.findall(r"\b(MySpriteRenderer__[A-Za-z_]+|SpriteManager__[A-Za-z_]+)", c)))
        print(f"\n--- {base} ({len(c)} 字节) ---")
        print("  签名:", " ".join(sig.split())[:150])
        if fields:
            print("  访问字段:", ", ".join(fields[:14]))
        if calls:
            print("  调用:", ", ".join(calls[:10]))

    print("\n" + "=" * 70)
    print("二、Create 重载（哪个最省事）")
    print("=" * 70)
    for name in ("Create", "Init"):
        for x in d:
            f = x.get("file", "").split("\\")[-1]
            if f != f"MySpriteRenderer{name}.c" and f != f"MySpriteRenderer__{name}.c":
                continue
            c = x.get("content", "")
            sig = c[:c.find("{")] if "{" in c else c[:300]
            print(f"\n--- {f} ---")
            print(" ".join(sig.split())[:400])


if __name__ == "__main__":
    main()
