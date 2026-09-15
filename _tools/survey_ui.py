#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""调研：游戏里有哪些 UI 控件类型（用于设计「通用 UI 模板」的内容模型）。

从两个来源统计：
  1. 反编译语料库 `code_meta.json` —— 看游戏自己用了哪些控件类
  2. interop 程序集 —— 看这些控件类有哪些字段/方法（能填什么内容）

用法： python _tools/survey_ui.py
"""
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
DB = Path(r"C:\AI\游戏向量库\code_meta.json")
INTEROP = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Territory"
               r"\BepInEx\interop\Assembly-CSharp.dll")

# 关注的控件族（游戏自己的 + Unity/TMP 的）
FAMILIES = [
    "MyCheckBox", "MyToggle", "MySlider", "MyButton", "MyInput",
    "MyDropdown", "MyScroll", "MyProgress", "MyIcon", "MyText",
    "IconText", "IconName", "IconNum", "NumAdjust", "Panel",
    "ProgressBar", "Slider", "Scrollbar", "InputField", "TMP_InputField",
    "TMP_Dropdown", "TMP_Text", "Image", "Button", "Toggle", "ScrollRect",
    "GridView", "ListView", "List_", "Tab", "Tooltip", "Tip",
]


def main():
    d = json.loads(DB.read_text(encoding="utf-8"))
    blob = " ".join(x.get("content", "") for x in d)

    print("=" * 60)
    print("一、游戏自定义控件类（从语料库的方法签名里提取）")
    print("=" * 60)
    # 方法签名形如  Xxx__Method(...)，从中取类名
    classes = Counter()
    for x in d:
        f = x.get("file", "").split("\\")[-1]
        m = re.match(r"([A-Z][A-Za-z0-9_]+?)\.c$", f)
        if m:
            classes[m.group(1)] += 1
    hits = defaultdict(list)
    for fam in FAMILIES:
        for cls, n in classes.items():
            if fam.lower() in cls.lower():
                hits[fam].append((cls, n))
    for fam in FAMILIES:
        if not hits[fam]:
            continue
        items = sorted(set(hits[fam]), key=lambda x: -x[1])[:6]
        print(f"\n[{fam}]")
        for cls, n in items:
            print(f"    {cls}  ({n} 处)")

    print("\n" + "=" * 60)
    print("二、这些控件类在 interop 里的成员（能填什么内容）")
    print("=" * 60)
    raw = INTEROP.read_bytes()
    names = set(m.decode() for m in re.findall(rb"[A-Za-z_][A-Za-z0-9_]{2,120}", raw))
    for cls in ["MyCheckBox", "IconTextNum", "IconNameNumStockAdjust", "NumAdjust",
                "ProgressBar", "MyProgressBar", "IconNum", "PanelStuffStockAdjust"]:
        fs = sorted(n.replace("NativeFieldInfoPtr_", "")
                    for n in names
                    if n.startswith("NativeFieldInfoPtr_") and n[21:].startswith(cls + "_")
                    or n == f"NativeFieldInfoPtr_{cls}")
        # 上面的前缀匹配不准，改用「字段名里含控件名」的方式
        print(f"\n[{cls}] interop 字段命中: {len([n for n in names if cls in n])} 个")


if __name__ == "__main__":
    main()
