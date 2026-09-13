#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Facility mod 全自动操控测试台（纯标准库 + Pillow，无需 pyautogui / pywin32）

能力（全部可远程、无人工干预）：
  * 进程：启动游戏（唯一合法通道 steam://rungameid/1455910）/ 强杀 / 探测 8765 HTTP 就绪
  * 存档：列出存档目录、进最新存档、轮询 inSave & saveLoads
  * 截屏：窗口 PrintWindow（后台可取，不抢前台）+ 全屏 GDI 兜底
  * 输入：鼠标移动/点击/拖拽、键盘按键、文本输入（中文走剪贴板 Ctrl+V、
        ASCII 走 WM_CHAR）——PostMessage 后台投递优先，SendInput 前台兜底
  * 观测：帧计数器（判定游戏是否真在渲染/响应）、日志文件按偏移增量读取、
         cfg 读取、8765 API 只读端点

CLI 用法（给 AI 会话直接调用）：
  python _tools/autotest.py launch                 # 启动游戏并等待 mod 就绪
  python _tools/autotest.py load [存档名]           # 进档（缺省=最新档），等待 inSave
  python _tools/autotest.py shot out.png           # 截游戏窗口
  python _tools/autotest.py click X Y              # 在游戏窗口客户区坐标点击
  python _tools/autotest.py key F10                 # 按键（名见 KEY 表）
  python _tools/autotest.py text "中文"             # 输入文本
  python _tools/autotest.py state                   # 打印 8765 状态
  python _tools/autotest.py log [N]                 # 打印 BepInEx 日志最后 N 行
  python _tools/autotest.py quit                    # 优雅退出
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

# ---------------- 常量 ----------------
GAME_EXE = "Territory.exe"
GAME_TITLE = "Territory"
STEAM_URL = "steam://rungameid/1455910"
GAME_DIR = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Territory")
SAVE_ROOT = Path(r"C:\Users\c\AppData\LocalLow\Looming\Territory\TerritoryArchive\UserData")
BEPINEX_LOG = GAME_DIR / "BepInEx" / "LogOutput.log"
PLAYER_LOG = Path(r"C:\Users\c\AppData\LocalLow\Looming\Territory\Player.log")
CFG = GAME_DIR / "BepInEx" / "config" / "claude.facility.cfg"
API = "http://localhost:8765"

user32 = ctypes.WinDLL("user32", use_last_error=True)
gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

# 控制台按 UTF-8 输出（中文标题/日志在 GBK 控制台会抛 UnicodeEncodeError 打断 ctypes 回调）
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

# ---------------- Win32 基础 ----------------

def find_window(title_substr: str = GAME_TITLE):
    """按标题找游戏主窗口。

    ⚠ 实测坑：**BepInEx 控制台窗口的标题里也含 "Territory"**
    （`选择 BepInEx 6.0.0-be.752+… - Territory`），会被标题匹配误选，
    导致所有点击/截图打到黑框控制台上。所以这里显式排除控制台与常见非游戏窗口。
    """
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def _cb(hwnd, _lparam):
        n = user32.GetWindowTextLengthW(hwnd)
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            title = buf.value
            if title_substr.lower() in title.lower() and user32.IsWindowVisible(hwnd):
                if _is_console_like(title):
                    return True
                found.append((hwnd, title))
        return True

    user32.EnumWindows(_cb, 0)
    return found[0] if found else None


def _is_console_like(title: str) -> bool:
    """BepInEx 控制台 / 日志窗口：标题里带 BepInEx。"""
    return "bepinex" in title.lower()


def find_windows(title_substr: str = GAME_TITLE):
    """列出所有标题匹配的可见窗口（调试用；find_window 取其中第一个非控制台窗口）。"""
    out = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def _cb(hwnd, _lparam):
        n = user32.GetWindowTextLengthW(hwnd)
        if n > 0:
            buf = ctypes.create_unicode_buffer(n + 1)
            user32.GetWindowTextW(hwnd, buf, n + 1)
            if title_substr.lower() in buf.value.lower() and user32.IsWindowVisible(hwnd):
                w, h = client_size(hwnd)
                out.append((hwnd, buf.value, w, h, _is_console_like(buf.value)))
        return True

    user32.EnumWindows(_cb, 0)
    return out


def client_size(hwnd):
    r = wt.RECT()
    user32.GetClientRect(hwnd, ctypes.byref(r))
    return r.right - r.left, r.bottom - r.top


def client_to_screen(hwnd, x, y):
    p = wt.POINT(x, y)
    user32.ClientToScreen(hwnd, ctypes.byref(p))
    return p.x, p.y


def is_foreground(hwnd):
    return user32.GetForegroundWindow() == hwnd


def frame_count(hwnd):
    """读窗口标题里的帧号（部分 Unity 构建会写）；没有则返回 None。"""
    n = user32.GetWindowTextLengthW(hwnd)
    buf = ctypes.create_unicode_buffer(n + 1)
    user32.GetWindowTextW(hwnd, buf, n + 1)
    m = re.search(r"(\d+)\s*$", buf.value)
    return int(m.group(1)) if m else None


# ---------------- 截屏 ----------------

def grab_window(hwnd, path: Path):
    """PrintWindow 抓窗口位图（后台可取）。返回 (w,h,非空像素比例)。"""
    from PIL import Image
    w, h = client_size(hwnd)
    if w <= 0 or h <= 0:
        return None
    hdc = user32.GetDC(hwnd)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)

    PW_RENDERFULLCONTENT = 0x00000002
    ok = user32.PrintWindow(hwnd, mem, PW_RENDERFULLCONTENT)
    if not ok:
        ok = user32.PrintWindow(hwnd, mem, 0)

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                    ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                    ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h          # 负数 = 自上而下
    bi.biPlanes = 1
    bi.biBitCount = 32
    bi.biCompression = 0      # BI_RGB
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)

    img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB")
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path)

    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(hwnd, hdc)

    px = img.getdata()
    nonblack = sum(1 for p in px if p[0] + p[1] + p[2] > 24)
    return w, h, nonblack / float(w * h)


def grab_screen(path: Path, box=None):
    """全屏 GDI 抓取（要求窗口可见）。box=(l,t,r,b) 可选。"""
    from PIL import Image
    if box is None:
        sw, sh = user32.GetSystemMetrics(0), user32.GetSystemMetrics(1)
        box = (0, 0, sw, sh)
    l, t, r, b = box
    w, h = r - l, b - t
    hdc = user32.GetDC(0)
    mem = gdi32.CreateCompatibleDC(hdc)
    bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
    gdi32.SelectObject(mem, bmp)
    gdi32.BitBlt(mem, 0, 0, w, h, hdc, l, t, 0x00CC0020)  # SRCCOPY

    class BITMAPINFOHEADER(ctypes.Structure):
        _fields_ = [("biSize", wt.DWORD), ("biWidth", wt.LONG), ("biHeight", wt.LONG),
                    ("biPlanes", wt.WORD), ("biBitCount", wt.WORD), ("biCompression", wt.DWORD),
                    ("biSizeImage", wt.DWORD), ("biXPelsPerMeter", wt.LONG),
                    ("biYPelsPerMeter", wt.LONG), ("biClrUsed", wt.DWORD), ("biClrImportant", wt.DWORD)]

    bi = BITMAPINFOHEADER()
    bi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
    bi.biWidth = w
    bi.biHeight = -h
    bi.biPlanes = 1
    bi.biBitCount = 32
    buf = ctypes.create_string_buffer(w * h * 4)
    gdi32.GetDIBits(mem, bmp, 0, h, buf, ctypes.byref(bi), 0)
    img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1).convert("RGB")
    path.parent.mkdir(parents=True, exist_ok=True)
    img.save(path)
    gdi32.DeleteObject(bmp)
    gdi32.DeleteDC(mem)
    user32.ReleaseDC(0, hdc)
    return w, h


# ---------------- 输入 ----------------

WM_MOUSEMOVE = 0x0200
WM_LBUTTONDOWN = 0x0201
WM_LBUTTONUP = 0x0202
WM_RBUTTONDOWN = 0x0204
WM_RBUTTONUP = 0x0205
WM_CHAR = 0x0102
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
MK_LBUTTON = 0x0001
MK_RBUTTON = 0x0002

INPUT_MOUSE = 0
INPUT_KEYBOARD = 1
KEYEVENTF_KEYUP = 0x0002
KEYEVENTF_UNICODE = 0x0004
MOUSEEVENTF_MOVE = 0x0001
MOUSEEVENTF_ABSOLUTE = 0x8000
MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
MOUSEEVENTF_RIGHTDOWN = 0x0008
MOUSEEVENTF_RIGHTUP = 0x0010


class MOUSEINPUT(ctypes.Structure):
    _fields_ = [("dx", wt.LONG), ("dy", wt.LONG), ("mouseData", wt.DWORD),
                ("dwFlags", wt.DWORD), ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(wt.ULONG))]


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [("wVk", wt.WORD), ("wScan", wt.WORD), ("dwFlags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.POINTER(wt.ULONG))]


class HARDWAREINPUT(ctypes.Structure):
    _fields_ = [("uMsg", wt.DWORD), ("wParamL", wt.WORD), ("wParamH", wt.WORD)]


class _INPUTunion(ctypes.Union):
    _fields_ = [("mi", MOUSEINPUT), ("ki", KEYBDINPUT), ("hi", HARDWAREINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [("type", wt.DWORD), ("u", _INPUTunion)]


def _send_inputs(inputs):
    n = len(inputs)
    arr = (INPUT * n)(*inputs)
    return user32.SendInput(n, ctypes.byref(arr), ctypes.sizeof(INPUT))


def move_cursor(x, y):
    """移动光标。

    ⚠ 实测坑（本机 Win11 + Territory）：**SendInput 的绝对坐标移动会被系统吞掉**
    （SendInput 返回 1、光标纹丝不动），而 `SetCursorPos` 与 `mouse_event` 相对移动都正常。
    所以统一走 SetCursorPos + mouse_event，不用 SendInput 绝对坐标。
    """
    user32.SetCursorPos(int(x), int(y))


def mouse_button(button="left", down=True):
    """mouse_event 发按下/抬起（比 SendInput 绝对坐标可靠）。"""
    left = button == "left"
    flag = (MOUSEEVENTF_LEFTDOWN if left else MOUSEEVENTF_RIGHTDOWN) if down \
        else (MOUSEEVENTF_LEFTUP if left else MOUSEEVENTF_RIGHTUP)
    user32.mouse_event(flag, 0, 0, 0, 0)


def click_foreground(x, y, button="left"):
    """真点击（SetCursorPos + mouse_event，需要窗口在前台）。"""
    move_cursor(x, y)
    time.sleep(0.08)
    mouse_button(button, True)
    time.sleep(0.06)
    mouse_button(button, False)


def post_click(hwnd, x, y, button="left"):
    """PostMessage 后台点击（不需要前台，不移动真实光标）。"""
    lp = (y << 16) | (x & 0xFFFF)
    dn = WM_LBUTTONDOWN if button == "left" else WM_RBUTTONDOWN
    up = WM_LBUTTONUP if button == "left" else WM_RBUTTONUP
    mk = MK_LBUTTON if button == "left" else MK_RBUTTON
    user32.PostMessageW(hwnd, WM_MOUSEMOVE, 0, lp)
    time.sleep(0.03)
    user32.PostMessageW(hwnd, dn, mk, lp)
    time.sleep(0.06)
    user32.PostMessageW(hwnd, up, 0, lp)


def post_move(hwnd, x, y):
    lp = (y << 16) | (x & 0xFFFF)
    user32.PostMessageW(hwnd, WM_MOUSEMOVE, 0, lp)


VK = {
    "ESC": 0x1B, "ENTER": 0x0D, "SPACE": 0x20, "TAB": 0x09, "BACK": 0x08,
    "F1": 0x70, "F2": 0x71, "F3": 0x72, "F4": 0x73, "F5": 0x74, "F6": 0x75,
    "F7": 0x76, "F8": 0x77, "F9": 0x78, "F10": 0x79, "F11": 0x7A, "F12": 0x7B,
    "LEFT": 0x25, "UP": 0x26, "RIGHT": 0x27, "DOWN": 0x28,
    "CTRL": 0x11, "SHIFT": 0x10, "ALT": 0x12,
}


def key_press(hwnd, key, foreground=False):
    """按键：默认 PostMessage 后台投递；foreground=True 走 SendInput。"""
    name = key.upper()
    vk = VK.get(name)
    if vk is None and len(key) == 1:
        vk = ord(key.upper())
    if vk is None:
        raise ValueError(f"未知按键: {key}")
    if foreground:
        _send_inputs([
            INPUT(type=INPUT_KEYBOARD, u=_INPUTunion(ki=KEYBDINPUT(vk, 0, 0, 0, None))),
            INPUT(type=INPUT_KEYBOARD, u=_INPUTunion(ki=KEYBDINPUT(vk, 0, KEYEVENTF_KEYUP, 0, None))),
        ])
    else:
        user32.PostMessageW(hwnd, WM_KEYDOWN, vk, 0)
        time.sleep(0.03)
        user32.PostMessageW(hwnd, WM_KEYUP, vk, 0)


def type_ascii(hwnd, s):
    for ch in s:
        user32.PostMessageW(hwnd, WM_CHAR, ord(ch), 0)
        time.sleep(0.02)


def set_clipboard_text(s: str):
    """写系统剪贴板（CF_UNICODETEXT），返回原内容以便还原。"""
    CF_UNICODETEXT = 13
    GMEM_MOVEABLE = 0x0002
    old = get_clipboard_text()
    if not user32.OpenClipboard(0):
        raise RuntimeError("OpenClipboard 失败")
    try:
        user32.EmptyClipboard()
        data = s.encode("utf-16-le") + b"\x00\x00"
        h = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
        ptr = kernel32.GlobalLock(h)
        ctypes.memmove(ptr, data, len(data))
        kernel32.GlobalUnlock(h)
        user32.SetClipboardData(CF_UNICODETEXT, h)
    finally:
        user32.CloseClipboard()
    return old


def get_clipboard_text():
    CF_UNICODETEXT = 13
    if not user32.OpenClipboard(0):
        return None
    try:
        h = user32.GetClipboardData(CF_UNICODETEXT)
        if not h:
            return None
        ptr = kernel32.GlobalLock(h)
        try:
            return ctypes.wstring_at(ptr)
        finally:
            kernel32.GlobalUnlock(h)
    finally:
        user32.CloseClipboard()


def paste_text(hwnd, s, foreground=False):
    """输入任意文本：中文用剪贴板 + Ctrl+V（游戏窗口必须前台），ASCII 用 WM_CHAR。"""
    if all(ord(c) < 128 for c in s):
        type_ascii(hwnd, s)
        return "ascii-wmchar"
    old = set_clipboard_text(s)
    try:
        time.sleep(0.1)
        if foreground:
            _send_inputs([
                INPUT(type=INPUT_KEYBOARD, u=_INPUTunion(ki=KEYBDINPUT(0x11, 0, 0, 0, None))),   # Ctrl down
                INPUT(type=INPUT_KEYBOARD, u=_INPUTunion(ki=KEYBDINPUT(0x56, 0, 0, 0, None))),   # V down
                INPUT(type=INPUT_KEYBOARD, u=_INPUTunion(ki=KEYBDINPUT(0x56, 0, KEYEVENTF_KEYUP, 0, None))),
                INPUT(type=INPUT_KEYBOARD, u=_INPUTunion(ki=KEYBDINPUT(0x11, 0, KEYEVENTF_KEYUP, 0, None))),
            ])
        else:
            user32.PostMessageW(hwnd, WM_KEYDOWN, 0x11, 0)
            user32.PostMessageW(hwnd, WM_KEYDOWN, 0x56, 0)
            user32.PostMessageW(hwnd, WM_KEYUP, 0x56, 0)
            user32.PostMessageW(hwnd, WM_KEYUP, 0x11, 0)
        time.sleep(0.15)
    finally:
        if old is not None:
            try:
                set_clipboard_text(old)
            except Exception:
                pass
    return "clipboard-ctrlcv"


def focus_window(hwnd):
    """把游戏窗口切到前台（截全屏/真点击需要）。"""
    user32.ShowWindow(hwnd, 9)   # SW_RESTORE
    return bool(user32.SetForegroundWindow(hwnd))


def explorer_open():
    """UnityExplorer 面板是否开着（标题含 UnityExplorer；它盖在游戏上面会挡点击）。"""
    return any("unityexplorer" in t.lower() for _, t, _, _, _ in find_windows("UnityExplorer"))


def close_overlays(hwnd, rounds=1, settle=0.6):
    """把挡在游戏上的面板清掉——**只关 UnityExplorer，绝不按 ESC**。

    ⚠⚠ 这是本项目自动化最贵的一次教训（真实把一局游戏点退了）：
    ESC 在本游戏是**暂停菜单**开关，菜单里第 6 个按钮就是「退出」。
    脚本只要「盲按 ESC + 随后往屏幕中间点一下」，就可能点到「退出」，
    游戏进入「正在结束游戏。。。」永久卡住，后面所有断言全废
    （日志实证：某轮 e2e 跑到一半游戏自己在退出）。
    而「暂停菜单检测」在这台机器上极不可靠——我试了三版像素判据，
    第 1 版 60+ 张世界截图全误报，第 3 版又把「参数设置」窗口误判成暂停菜单
    （两者都是 6 个金色描边按钮，只是宽度不同）。**结论：不要按 ESC，从源头避免。**

    那帝国地图之类的弹窗怎么关？不用 ESC——直接点建筑本身会把建筑窗口开到前面来；
    实在被挡住，`open_facility_window` 会重试（换建筑/换角度）。
    """
    if explorer_open():
        focus_window(hwnd)
        time.sleep(0.3)
        key_press(hwnd, "F7", foreground=True)
        time.sleep(settle)
    focus_window(hwnd)
    time.sleep(0.2)


def find_pause_button(hwnd, img=None):
    """找暂停菜单里最上面那个按钮（「继续游戏」），返回其中心 (x, y) 或 None。

    实测标定（1440×900）：暂停菜单是**竖直 6 个金色描边按钮**（继续游戏/设置/帮助/
    读档/保存/退出），描边金色约 RGB(168,128,56)（暗）+ 高光更亮，按钮宽约 310–360px、
    高约 40px、竖直间距约 14px。
    ⚠ 不要用固定比例坐标：菜单位置会变（实测同一机器上出现过 (512,178) 与 (512,242)），
    用错坐标就会点到隔壁的**「退出」**，直接把游戏点退（血泪教训）。
    判据用「至少 3 个等宽金色长条、竖直等距排列」——单个金像素在游戏世界里也有，
    但三条等宽等距的只有暂停菜单。
    """
    try:
        from PIL import Image
        if img is None:
            tmp = Path(tempfile.gettempdir()) / "_dsh_pause_probe.png"
            grab_screen(tmp)
            p = wt.POINT(0, 0)
            user32.ClientToScreen(hwnd, ctypes.byref(p))
            w, h = client_size(hwnd)
            img = Image.open(tmp).crop((p.x, p.y, p.x + w, p.y + h))
            tmp.unlink(missing_ok=True)
        w, h = img.size
        rgb = img.convert("RGB")
        x0, x1 = int(w * 0.38), int(w * 0.62)

        def row_gold(y):
            xs = [x for x in range(x0, x1) if _is_gold(rgb.getpixel((x, y)))]
            if len(xs) < 8:
                return None
            return min(xs), max(xs)

        hits = []          # [(y, minx, maxx), …]
        for y in range(int(h * 0.05), int(h * 0.85)):
            r = row_gold(y)
            if r and r[1] - r[0] >= 150:      # 按钮描边是「一整条横边」
                hits.append((y, r[0], r[1]))
        if len(hits) < 3:
            return None

        # 连续行分组成条带（按钮的上/下描边各算一条）
        bands, cur = [], [hits[0]]
        for t in hits[1:]:
            if t[0] - cur[-1][0] <= 4:
                cur.append(t)
            else:
                bands.append(cur)
                cur = [t]
        bands.append(cur)
        # 按钮特征：左右边缘 x 基本固定（实测 1440×900 下约 612 与 828，宽约 216）
        cand = []
        for b in bands:
            lo = min(t[1] for t in b)
            hi = max(t[2] for t in b)
            cand.append((b[0][0], b[-1][0], lo, hi))
        if len(cand) < 3:
            return None
        base = cand[0]
        aligned = [c for c in cand
                   if abs(c[2] - base[2]) <= 12 and abs(c[3] - base[3]) <= 12
                   and abs((c[3] - c[2]) - (base[3] - base[2])) <= 16]
        # 暂停菜单有 6 个同宽同位的按钮（上下描边 → 至少 5 条带）；世界里的木墙凑不出来
        if len(aligned) < 5:
            return None
        top = aligned[0]
        # 第一条带是「继续游戏」按钮的上描边？→ 按钮中心在下一条带附近；
        # 稳妥取「前两条带的中点」作为按钮中心 y。
        second = aligned[1] if len(aligned) > 1 else top
        cy = (top[0] + second[1]) // 2
        return (top[2] + top[3]) // 2, cy
    except Exception:
        return None


def _is_gold(p):
    r, g, b = p
    return r > 110 and g > 80 and b < 130 and r - b > 45 and r >= g > b


def pause_menu_open(hwnd, img=None):
    """是否停在暂停菜单（能找到金色按钮即算）。"""
    return find_pause_button(hwnd, img=img) is not None


def click_continue(hwnd):
    """点暂停菜单的「继续游戏」按钮（先定位再点；定位不到就不点，避免误触退出）。"""
    pos = find_pause_button(hwnd)
    if pos is None:
        return False
    click_client(hwnd, pos[0], pos[1], settle=1.0)
    return True


def click_client(hwnd, x, y, settle=1.2, foreground=True):
    """在客户区坐标点一下（真鼠标）。返回是否成功派发。"""
    sx, sy = client_to_screen(hwnd, x, y)
    if foreground:
        focus_window(hwnd)
        time.sleep(0.25)
    move_cursor(sx, sy)
    time.sleep(0.25)
    mouse_button("left", True)
    time.sleep(0.09)
    mouse_button("left", False)
    time.sleep(settle)
    return True


def drag(hwnd, x1, y1, x2, y2, button="right", steps=10, settle=0.7):
    """在客户区坐标上做一次真实拖拽（游戏里右键/中键拖拽 = 平移相机）。

    实测确认：这条路径能可靠验证「输入是否真的到达游戏」——
    拖拽前后全屏像素差异应 > 5%，否则说明点击没进去。
    """
    sx1, sy1 = client_to_screen(hwnd, x1, y1)
    sx2, sy2 = client_to_screen(hwnd, x2, y2)
    move_cursor(sx1, sy1)
    time.sleep(0.2)
    if button == "middle":
        user32.mouse_event(0x0020, 0, 0, 0, 0)      # MIDDLEDOWN
    else:
        mouse_button(button, True)
    time.sleep(0.15)
    for i in range(1, steps + 1):
        move_cursor(sx1 + (sx2 - sx1) * i // steps, sy1 + (sy2 - sy1) * i // steps)
        time.sleep(0.04)
    time.sleep(0.2)
    if button == "middle":
        user32.mouse_event(0x0040, 0, 0, 0, 0)      # MIDDLEUP
    else:
        mouse_button(button, False)
    time.sleep(settle)


def client_grab(hwnd, path: Path):
    """全屏抓取后按客户区裁切——像素与客户区 1:1，坐标映射可靠。

    ⚠ 不要用 grab_window（PrintWindow）来标定坐标：实测它和全屏裁切的结果**不一致**
    （PrintWindow 抓到的帧与客户区有偏移），据此标定的坐标会点空。
    """
    from PIL import Image
    tmp = path.with_name("_" + path.name)
    path.parent.mkdir(parents=True, exist_ok=True)
    grab_screen(tmp)
    im = Image.open(tmp)
    p = wt.POINT(0, 0)
    user32.ClientToScreen(hwnd, ctypes.byref(p))
    w, h = client_size(hwnd)
    crop = im.crop((p.x, p.y, p.x + w, p.y + h))
    crop.save(path)
    try:
        tmp.unlink()
    except Exception:
        pass
    return crop


def image_diff_pct(a, b, box=None):
    """两张 PIL 图的显著差异像素百分比（用于「点击有没有生效」的客观判断）。"""
    from PIL import ImageChops
    x = a if box is None else a.crop(box)
    y = b if box is None else b.crop(box)
    d = ImageChops.difference(x.convert("RGB"), y.convert("RGB")).convert("L")
    h = d.histogram()
    return 100.0 * sum(h[30:]) / max(1, sum(h))


def find_selector_bar(img, region=None):
    """在客户区截图里找「额外产品」选择条，返回其中心 (x, y) 或 None。

    选择条是本 mod 用原生 uGUI 注入的深色实心长条（240×28，Image.color=0.18/0.17/0.15）。
    落地像素是**偏暖的深色**（约 (51,50,62)：R>=G>B，明显区别于冷色地砖 (45,54,80) B 最大）。
    判据：某行里「暖深色」像素**总宽度** >= 120（允许被文字笔画打断，最多容忍 6 连续非暖像素）。
    不依赖绝对坐标 → 建筑窗口随相机移动也能找到。
    """
    w, h = img.size
    l, t, r, b = region or (0, int(h * 0.10), int(w * 0.80), int(h * 0.85))
    px = img.convert("RGB").load()

    def warm_dark(p):
        rr, gg, bb = p
        return rr < 100 and gg < 100 and bb < 100 and rr >= bb and gg >= bb - 3

    best = None      # (y, start, end, 暖像素数)
    for y in range(t, b):
        start = None
        gap = 0
        warm = 0
        for x in range(l, r):
            if warm_dark(px[x, y]):
                if start is None:
                    start = x
                warm += 1
                gap = 0
            elif start is not None:
                gap += 1
                if gap > 6:
                    if warm >= 120 and (best is None or warm > best[3]):
                        best = (y, start, x - gap, warm)
                    start = None
                    gap = 0
                    warm = 0
        if start is not None and warm >= 120 and (best is None or warm > best[3]):
            best = (y, start, r - 1, warm)
    if best is None:
        return None
    y, xs, xe, _ = best
    return (xs + xe) // 2, y


# ---------------- 进程 / API / 日志 ----------------

def game_pids():
    out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {GAME_EXE}", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True, errors="replace").stdout
    return [int(m.group(1)) for m in re.finditer(r'"%s","(\d+)"' % re.escape(GAME_EXE), out)]


# ---------------- 系统代理开关（进游戏前必须关掉）----------------

_IE_SETTINGS = r"HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings"


def system_proxy_state():
    """读系统代理设置 → (ProxyEnable, ProxyServer)。"""
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"$p=Get-ItemProperty '{_IE_SETTINGS}'; Write-Output \"$($p.ProxyEnable)|$($p.ProxyServer)\""],
        capture_output=True, text=True, errors="replace").stdout.strip()
    en, _, server = out.partition("|")
    return (en.strip() == "1"), server.strip()


def set_system_proxy(enable: bool):
    subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         f"Set-ItemProperty '{_IE_SETTINGS}' -Name ProxyEnable -Value {(1 if enable else 0)}"],
        capture_output=True)


def proxy_off_for_launch():
    """关掉系统代理，返回原状态以便还原。

    ⚠ 这是进游戏前的必需步骤（实测踩坑，见 docs/开发交接.md §11）：
    开着系统代理（本机 FlClash 127.0.0.1:7890）时，游戏的 Steamworks UGC 查询会因为
    **TLS 证书校验失败**（Player.log: `Curl error 35: Cert verify failed`）拿不到创意工坊订阅，
    工坊 mod 的贴图没进 sprite atlas → `MainScene.Start` 里
    `BatchSpriteRendererHelper..ctor` 抛 NullReferenceException → 卡在「加载中，请稍候」永远进不去。
    """
    was_enabled, server = system_proxy_state()
    if was_enabled:
        set_system_proxy(False)
        time.sleep(3)
    return was_enabled, server


def restore_system_proxy(state):
    was_enabled, _ = state
    if was_enabled:
        set_system_proxy(True)
        time.sleep(1)


def kill_game():
    for pid in game_pids():
        subprocess.run(["taskkill", "/PID", str(pid), "/F"], capture_output=True)
    return True


def launch_game(wait_ready=True, timeout=180):
    """启动游戏。⚠ 唯一可用通道是 steam:// 协议（直接跑 Territory.exe 会因 Steam 校验秒退）。

    ⚠ 必须用干净环境启动（Start-Process 从 explorer 走，不继承当前 shell 的 env）：
    实测把 `https_proxy/http_proxy` 带进游戏后，Steamworks 的 UGC 查询会因**证书校验失败**
    （curl error 35）拿不到创意工坊订阅，工坊 mod 的贴图没注册 → `MainScene.Start` 里
    `BatchSpriteRendererHelper..ctor` 抛 NullReferenceException → 进去一直卡在「加载中，请稍候」。
    （这个坑是 AI 自己 push 代码时设了代理留下的，排查记录见 docs/开发交接.md §11。）
    """
    if not game_pids():
        env = {k: v for k, v in os.environ.items()
               if k.lower() not in ("http_proxy", "https_proxy", "all_proxy", "no_proxy")}
        subprocess.Popen(["cmd", "/c", "start", "", STEAM_URL], shell=False, env=env)
    if wait_ready:
        return wait_api(timeout)
    return True


def wait_api(timeout=180, interval=3):
    t0 = time.time()
    while time.time() - t0 < timeout:
        try:
            s = api_state()
            if s is not None:
                return s
        except Exception:
            pass
        time.sleep(interval)
    return None


def api_get(path, timeout=15):
    req = urllib.request.Request(API + path, method="GET")
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8", "replace"))


def api_post(path, payload=None, timeout=60):
    data = json.dumps(payload or {}).encode("utf-8")
    req = urllib.request.Request(API + path, data=data, method="POST",
                                headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.loads(r.read().decode("utf-8", "replace"))


def api_state():
    try:
        return api_get("/api/editor/state", timeout=5)
    except Exception:
        return None


def newest_save():
    dirs = [d for d in SAVE_ROOT.iterdir() if d.is_dir()]
    dirs.sort(key=lambda d: d.stat().st_mtime, reverse=True)
    return dirs[0].name if dirs else None


def load_save(name=None, timeout=240, expect_saveLoads=None, settle=10):
    """进档并等到 inSave:true 且 saveLoads 增加。返回 (ok, state, 用时秒)。

    ⚠ `settle` 是给游戏留的启动缓冲：UI 一就绪就立刻喊读档，会撞上
    `MainScene.Start()` 的贴图图集（sprite atlas）初始化竞态 →
    `BatchSpriteRendererHelper..ctor` 抛 NullReferenceException → 永远卡「加载中」。
    实测踩坑：早先有一轮 e2e 在 `wait_api` 后多等了 8 秒就顺利进档，
    而「一就绪就 load」连续失败 5 次（5 个外观预设想各试一遍全挂）。
    """
    if settle > 0:
        time.sleep(settle)
    name = name or newest_save()
    t0 = time.time()
    before = api_state() or {}
    r = api_post("/api/editor/debug/load", {"dir": name}, timeout=30)
    base = expect_saveLoads if expect_saveLoads is not None else before.get("saveLoads", 0)
    while time.time() - t0 < timeout:
        s = api_state()
        if s and s.get("inSave") and s.get("saveLoads", 0) > base and not s.get("loading"):
            return True, s, time.time() - t0
        time.sleep(3)
    return False, api_state(), time.time() - t0


def quit_game(timeout=60):
    try:
        api_post("/api/editor/debug/quit", timeout=10)
    except Exception:
        pass
    t0 = time.time()
    while time.time() - t0 < timeout:
        if not game_pids():
            return True
        time.sleep(2)
    return not game_pids()


def read_log_tail(path: Path, n=80):
    if not path.exists():
        return []
    with path.open("r", encoding="utf-8", errors="replace") as f:
        return f.read().splitlines()[-n:]


class LogWatcher:
    """增量读日志：记录偏移，每次 poll() 只取新增行并按关键字筛。"""

    def __init__(self, path: Path, from_end=True):
        self.path = path
        self.offset = path.stat().st_size if (from_end and path.exists()) else 0

    def poll(self, patterns=None):
        if not self.path.exists():
            return []
        size = self.path.stat().st_size
        if size < self.offset:      # 日志被轮转/截断
            self.offset = 0
        with self.path.open("r", encoding="utf-8", errors="replace") as f:
            f.seek(self.offset)
            new = f.read()
            self.offset = f.tell()
        lines = [l for l in new.splitlines() if l.strip()]
        if patterns:
            rx = re.compile("|".join(patterns))
            lines = [l for l in lines if rx.search(l)]
        return lines


def read_cfg():
    out = {}
    if not CFG.exists():
        return out
    for line in CFG.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or line.startswith("[") or "=" not in line:
            continue
        k, v = line.split("=", 1)
        out[k.strip()] = v.strip()
    return out


def parse_candidates(raw):
    """解析 cfg「额外产品候选」→ [(id, 名称), …]。"""
    out = []
    for seg in (raw or "").replace("，", ",").replace("；", ";").split(","):
        for s in seg.split(";"):
            s = s.strip()
            if ":" not in s:
                continue
            a, _, b = s.partition(":")
            try:
                out.append((int(a.strip()), b.strip()))
            except ValueError:
                continue
    return out


# ---------------- CLI ----------------

def main(argv):
    if len(argv) < 2:
        print(__doc__)
        return 1
    cmd = argv[1]
    shots = Path(__file__).resolve().parent / "_shots"

    if cmd == "launch":
        print("启动游戏…")
        kill_game()
        time.sleep(3)
        os.startfile(STEAM_URL)
        s = wait_api(180)
        print("API 就绪:", s)
        return 0 if s else 2

    if cmd == "load":
        name = argv[2] if len(argv) > 2 else None
        ok, s, dt = load_save(name)
        print(f"进档 {name or newest_save()} -> ok={ok} {dt:.1f}s state={s}")
        return 0 if ok else 2

    if cmd == "state":
        print(json.dumps(api_state(), ensure_ascii=False))
        return 0

    if cmd == "shot":
        out = Path(argv[2]) if len(argv) > 2 else shots / "shot.png"
        win = find_window()
        if not win:
            print("找不到游戏窗口")
            return 2
        hwnd, title = win
        info = grab_window(hwnd, out)
        print(f"窗口 '{title}' hwnd={hwnd} client={client_size(hwnd)} -> {out} stat={info}")
        return 0

    if cmd == "shotfull":
        out = Path(argv[2]) if len(argv) > 2 else shots / "full.png"
        print("全屏 ->", out, grab_screen(out))
        return 0

    if cmd == "click":
        x, y = int(argv[2]), int(argv[3])
        mode = argv[4] if len(argv) > 4 else "post"
        win = find_window()
        hwnd = win[0]
        if mode == "fg":
            focus_window(hwnd)
            time.sleep(0.3)
            sx, sy = client_to_screen(hwnd, x, y)
            click_foreground(sx, sy)
            print(f"前台点击 client=({x},{y}) screen=({sx},{sy})")
        else:
            post_click(hwnd, x, y)
            print(f"后台点击 client=({x},{y})")
        return 0

    if cmd == "key":
        hwnd = find_window()[0]
        key_press(hwnd, argv[2], foreground=(len(argv) > 3 and argv[3] == "fg"))
        print("按键", argv[2])
        return 0

    if cmd == "text":
        hwnd = find_window()[0]
        print("输入方式:", paste_text(hwnd, argv[2], foreground=(len(argv) > 3 and argv[3] == "fg")))
        return 0

    if cmd == "log":
        n = int(argv[2]) if len(argv) > 2 else 60
        for l in read_log_tail(BEPINEX_LOG, n):
            print(l)
        return 0

    if cmd == "cfg":
        print(json.dumps(read_cfg(), ensure_ascii=False, indent=2))
        return 0

    if cmd == "quit":
        print("退出:", quit_game())
        return 0

    if cmd == "windows":
        @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
        def _cb(hwnd, _l):
            if user32.IsWindowVisible(hwnd):
                n = user32.GetWindowTextLengthW(hwnd)
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                if buf.value.strip():
                    print(f"hwnd={hwnd} title='{buf.value}' rect={client_size(hwnd)}")
            return True
        user32.EnumWindows(_cb, 0)
        return 0

    print("未知命令:", cmd)
    print(__doc__)
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv))
