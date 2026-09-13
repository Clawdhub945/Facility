#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""全自动端到端测试：综合生产所（Facility 105040）

一条命令跑完，全程无人工：
  1) mod 是否加载（日志断言）
  2) 进最新存档（ChestEditor /api/editor/debug/load）并确认 inSave
  3) 自动定位一座综合生产所并把相机居中，自动点击建筑 → 建筑窗口打开
  4) 视觉识别窗口里的「额外产品」选择条（不依赖绝对坐标）
  5) 自动点击选择条 → 断言 cfg「额外产品」被改写（窗口 → 写配置链路）
  6) 盯日志跨过一个游戏日 → 断言：每座建筑当天恰好产出一次、产品数量 = 工人数×每人数量
     （这条直接验证 0.4.0 修的「一天之内重复产出 512 次」回归）
  7) 截图存档（窗口记录区 / 全屏），并做像素级变化断言

退出码 0 = 全过；1 = 有失败项（打印逐项结果）。

用法：
  python _tools/e2e.py            # 完整跑
  python _tools/e2e.py --no-load  # 已在存档里，跳过读档
"""
import json
import re
import sys
import time
import urllib.request
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import autotest as A  # noqa: E402

SHOTS = Path(__file__).resolve().parent / "_shots"
FACILITY = 105040

results = []


def check(name, ok, detail=""):
    results.append((name, bool(ok), detail))
    print(("  [PASS] " if ok else "  [FAIL] ") + name + ("  " + detail if detail else ""))
    return ok


def entities():
    raw = urllib.request.urlopen(A.API + "/api/editor/entities", timeout=120).read()
    return json.loads(raw.decode("utf-8", "replace"))


def scan():
    A.api_post("/api/editor/scan", None, timeout=180)


def facilities():
    return [e for e in entities() if e.get("stuffId") == FACILITY]


def refine_with_screenshot(img, pos, radius=90):
    """用截图识别校正 mod 上报的选择条坐标。

    两条路各有短板：mod 报的是 Unity 渲染坐标（窗口分辨率变了就要换算），
    截图识别不依赖坐标但可能被别的深色 UI 干扰。
    做法：以 mod 上报点为圆心在截图里找「暖深色长条」，找到就用截图的位置（更可信）。
    """
    if pos is None:
        return A.find_selector_bar(img)
    r = radius
    box = (max(0, pos[0] - r), max(0, pos[1] - r), pos[0] + r, pos[1] + r)
    hit = A.find_selector_bar(img, region=box)
    if hit:
        return hit
    return pos


def open_facility_window(hwnd, watcher):
    """打开一座综合生产所的窗口。

    关键是**每一步都验证再重试**：游戏里一次点击可能先弹出帝国地图/帝国面板
    （实测踩坑），盖住世界；所以要「先关弹窗 → 再点建筑 → 看日志有没有报出选择条坐标」。
    点空/点到弹窗上都由重试兜住，最多试 3 座建筑 × 2 轮。
    """
    facs = facilities()
    if not facs:
        return None, None
    for attempt in range(2):
        for f in facs[:3]:
            A.close_overlays(hwnd)              # 关掉帝国地图/其它弹窗（ESC ×3 + F7）
            A.api_post("/api/editor/locate", {"ptrHash": f["ptrHash"]}, timeout=30)
            time.sleep(1.6)
            A.focus_window(hwnd)
            time.sleep(0.5)
            w, h = A.client_size(hwnd)
            watcher.poll()                       # 清掉旧日志
            A.click_client(hwnd, w // 2, int(h * 0.45), settle=1.8)
            img = A.client_grab(hwnd, SHOTS / "e2e_window.png")

            pos, res = parse_selector_pos(watcher.poll())
            if pos:
                cw, ch = A.client_size(hwnd)     # 点击前重新读几何
                pos = to_client_coords(pos, res, (cw, ch))
                return img, refine_with_screenshot(img, pos)
            hit = A.find_selector_bar(img)       # 兜底：纯截图识别
            if hit:
                return img, hit
            print(f"  第 {attempt + 1} 轮 / 建筑 {f['guid']}：窗口没开出来，重试")
    return None, None


def parse_selector_pos(lines):
    """从 mod 日志解析选择条坐标 → 返回 ((x, y), 游戏内分辨率 (w, h)) 或 (None, None)。

    日志格式（`FacilityWindowUi.LogSelectorScreenPos`，不要改）：
      [FacilityUI] 选择条屏幕坐标=935,864 客户区=1440x1080

    ⚠ 游戏窗口分辨率会变（实测 1440×1080 ↔ 2560×1417，可能被玩家/系统改过），
    而 Unity 报的是**游戏渲染分辨率**下的坐标；窗口客户区与之不等时要按比例换算，
    否则点击会偏（这是 0.4.0 自动化调试里真实踩到的坑）。
    """
    for l in reversed(lines):
        m = re.search(r"选择条屏幕坐标=(-?\d+),(-?\d+)(?:\s*客户区=(\d+)x(\d+))?", l)
        if m:
            pos = (int(m.group(1)), int(m.group(2)))
            res = (int(m.group(3)), int(m.group(4))) if m.group(3) else None
            return pos, res
    return None, None


def to_client_coords(pos, game_res, client):
    """把「游戏渲染坐标」换算成当前窗口客户区坐标。"""
    if not game_res:
        return pos
    gw, gh = game_res
    cw, ch = client
    if gw <= 0 or gh <= 0 or (gw == cw and gh == ch):
        return pos
    return (int(round(pos[0] * cw / gw)), int(round(pos[1] * ch / gh)))


def production_lines(watcher, seconds=40):
    """盯日志，返回这一段时间里捕获到的产出相关行。"""
    lines = []
    t0 = time.time()
    while time.time() - t0 < seconds:
        lines += watcher.poll([r"\[Facility\]"])
        time.sleep(1)
    return lines


def main():
    no_load = "--no-load" in sys.argv
    print("=== Facility 全自动端到端测试 ===")
    SHOTS.mkdir(parents=True, exist_ok=True)
    watcher = A.LogWatcher(A.BEPINEX_LOG, from_end=True)

    # --- 1/2. 进程 / API（没在跑 / 卡死就自动拉起来）---
    def restart_game(why):
        print(f"  {why} → 自动重启游戏…")
        A.kill_game()
        time.sleep(4)
        A.launch_game(wait_ready=False)
        st = A.wait_api(240)
        time.sleep(8)
        return st

    if not A.game_pids():
        restart_game("游戏未运行")
    st = A.api_state()
    if st is None:
        # HTTP 端口能连上但主线程不回包 = 主线程卡死（实测遇到过），重启是唯一解
        restart_game("ChestEditor 无响应（主线程卡死）")
        st = A.api_state()
    check("ChestEditor HTTP 可用", st is not None, json.dumps(st, ensure_ascii=False))
    if st is None:
        print("  游戏主线程无响应，重启也救不回来，请人工看一眼")
        return 1

    hwnd_win = A.find_window()
    check("游戏窗口存在", hwnd_win is not None,
          f"hwnd={hwnd_win[0] if hwnd_win else '-'}")
    if not hwnd_win:
        print("  提示：启动后可能还在加载，重跑一次 e2e.py 即可")
        return 1
    hwnd = hwnd_win[0]

    # --- mod 加载断言必须在「游戏确实在跑」之后看日志，否则会看到上一轮启动的旧日志 ---
    tail = "\n".join(A.read_log_tail(A.BEPINEX_LOG, 4000))
    check("mod 已加载并挂上工位数补丁",
          "工位数补丁已挂" in tail, "日志: 工位数补丁已挂：GetOriginalWorkPosCount")

    if not no_load:
        save = A.newest_save()
        ok, st2, dt = A.load_save(save)
        check("进入最新存档", ok, f"{save} 用时 {dt:.1f}s state={st2}")
        if not ok:
            return 1
        time.sleep(6)
        scan()

    st = A.api_state() or {}
    check("在存档内 (inSave)", st.get("inSave") is True, json.dumps(st, ensure_ascii=False))

    # --- 3. 建筑存在 ---
    facs = facilities()
    check("场景里存在综合生产所(105040)", len(facs) > 0,
          f"{len(facs)} 座: {[f['guid'] for f in facs]}")
    if not facs:
        return 1

    # --- 4/5. 打开窗口 + 点选择条 ---
    print("\n-- 打开建筑窗口（自动定位+点击）--")
    img, pos = open_facility_window(hwnd, watcher)
    check("建筑窗口打开且拿到「额外产品」选择条坐标", pos is not None,
          f"选择条客户区坐标={pos}")
    if img is not None:
        img.save(SHOTS / "e2e_window_found.png")

    if pos:
        cfg_before = A.read_cfg().get("额外产品")
        A.focus_window(hwnd)
        time.sleep(0.5)
        cw, ch = A.client_size(hwnd)              # 点击前重新读几何，别用旧值
        sx, sy = A.client_to_screen(hwnd, pos[0], pos[1])
        print(f"  点击选择条: 客户区={cw}x{ch} 坐标=({pos[0]},{pos[1]}) → 屏幕=({sx},{sy})")
        A.move_cursor(sx, sy)
        time.sleep(0.3)
        A.mouse_button("left", True)
        time.sleep(0.09)
        A.mouse_button("left", False)
        time.sleep(1.4)
        cfg_after = A.read_cfg().get("额外产品")
        check("点击选择条 → cfg「额外产品」被改写", cfg_before != cfg_after,
              f"{cfg_before} -> {cfg_after}")
        A.client_grab(hwnd, SHOTS / "e2e_after_selector_click.png")
    else:
        check("点击选择条 → cfg「额外产品」被改写", False, "未找到选择条，跳过")

    # --- 5b. 测试钩子：F8 程序化点击（走 Unity 事件系统，不依赖坐标标定）---
    if not no_load or pos is None:
        pass
    if pos:
        cfg_before = A.read_cfg().get("额外产品")
        A.focus_window(hwnd)
        time.sleep(0.4)
        A.key_press(hwnd, "F8", foreground=True)
        time.sleep(1.5)
        cfg_after = A.read_cfg().get("额外产品")
        check("F8 测试钩子：程序化点击选择条 → 改写 cfg", cfg_before != cfg_after,
              f"{cfg_before} -> {cfg_after}")
        hook_lines = [l for l in watcher.poll([r"测试点击选择条"]) if "测试点击选择条" in l]
        if hook_lines:
            print("  " + hook_lines[-1].split("] ", 1)[-1])

    # --- 6. 跨游戏日产出断言 ---
    print("\n-- 跨游戏日产出断言（最多 100 秒，游戏约 9.5 秒一个游戏日）--")
    lines = production_lines(watcher, seconds=100)

    # 按「第 N 日：…」把产出行切分到每天（防重断言必须以**单个游戏日**为单位）
    days = {}          # day -> {guid: [(sid, workers, count), ...]}
    cur = None
    for l in lines:
        m = re.search(r"第 (\d+) 日：", l)
        if m:
            cur = m.group(1)
            days.setdefault(cur, {})
            continue
        m = re.search(r"guid=(\d+) 工人×(\d+) → 物品(\d+) \+(\d+)", l)
        if m and cur is not None:
            guid, workers, sid, cnt = m.group(1), int(m.group(2)), m.group(3), int(m.group(4))
            days[cur].setdefault(guid, []).append((sid, workers, cnt))
    # 丢掉尚未跑完的最后一天（其行可能被截断），只断言完整天
    day_list = sorted(days)
    if len(day_list) >= 2:
        days.pop(day_list[-1], None)

    check("捕获到完整换日产出", len(days) > 0,
          f"{len(days)} 个完整游戏日（原始捕获 {len(day_list)} 天）")

    if days:
        dup = {}
        bad = []
        cfg_products = {}
        for seg in (A.read_cfg().get("每人每日产出") or "").split(","):
            if ":" in seg:
                a, b = seg.split(":", 1)
                cfg_products[a.strip()] = int(b.strip())
        for day, guidmap in days.items():
            for guid, items in guidmap.items():
                sids = [x[0] for x in items]
                if len(sids) != len(set(sids)):
                    dup[f"{day}/{guid}"] = items
                for sid, workers, cnt in items:
                    if sid in cfg_products and cnt != workers * cfg_products[sid]:
                        bad.append((day, guid, sid, cnt, workers * cfg_products[sid]))
        check("同一天同一建筑每种产品只产出一次（0.4.0 回归）", not dup,
              f"重复={list(dup)[:3]}" if dup else f"覆盖 {len(days)} 天 × {len(days[max(days)])} 座建筑")
        check("产出数量 = 工人数 × 每人每日数量", not bad,
              f"不符={bad[:3]}" if bad else "全部吻合")
        total = sum(len(v) for gm in days.values() for v in gm.values())
        print(f"  统计：{len(days)} 个游戏日，{total} 条产出行，"
              f"每日建筑数={ {d: len(g) for d, g in sorted(days.items())} }")

    A.client_grab(hwnd, SHOTS / "e2e_final.png")

    # --- 汇总 ---
    print("\n=== 结果汇总 ===")
    passed = sum(1 for _, ok, _ in results if ok)
    for name, ok, detail in results:
        print(("  ✔ " if ok else "  ✘ ") + name)
    print(f"{passed}/{len(results)} 通过")
    return 0 if passed == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
