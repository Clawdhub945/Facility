#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""把插件里的诊断日志统一改成「只有详细模式才输出」。

规则（用户明确要求：默认必须安静）：
  * `Plugin.LogInfo(...)`    → `Plugin.LogV(...)`      （纯信息，只有详细模式要）
  * `Plugin.LogWarning(...)` → `Plugin.LogV(...)`      （诊断性警告，同上）
  * `Plugin.LogError(...)`   → 保持不动               （真错误必须看得见）
  * `Plugin.cs` 里保留两条启动横幅（已加载 / 补丁挂载完成），其余 LogInfo 也降级

用法： python _tools/quiet_logs.py [--dry]
"""
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
# Plugin.cs 里这两条保留为 LogInfo（启动横幅，只在启动时各一条）
KEEP_INFO = ("已加载！", "补丁挂载完成")


def main():
    dry = "--dry" in sys.argv
    total = 0
    for cs in sorted(REPO.glob("*.cs")):
        src = cs.read_text(encoding="utf-8")
        out_lines = []
        changed = 0
        for line in src.splitlines(keepends=True):
            new = line
            if "LogInfo(" in line or "LogWarning(" in line:
                if not any(k in line for k in KEEP_INFO):
                    new = line.replace("Plugin.LogInfo(", "Plugin.LogV(") \
                              .replace("Plugin.LogWarning(", "Plugin.LogV(") \
                              .replace("LogInfo(", "LogV(") \
                              .replace("LogWarning(", "LogV(")
            if new != line:
                changed += 1
            out_lines.append(new)
        if changed and not dry:
            cs.write_text("".join(out_lines), encoding="utf-8", newline="\n")
        if changed:
            print(f"{cs.name}: 降级 {changed} 行")
            total += changed
    print(f"共 {total} 行{'（dry-run，未改文件）' if dry else ''}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
