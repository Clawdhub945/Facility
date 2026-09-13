#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""生成「超级生产所」的自定义外观贴图（红砖工厂 + 顶部烟囱 + 正面大门）。

产物（写到 Defs/Textures/，随后部署到 C:\\TerritoryModTest\\Textures\\）：

    super_factory.png    192×192  场景里的建筑本体（3 格 × 64px）
    ui_105050.png         64×64   UI/建造菜单图标

## ⚠ 这两张图是**由 DLL 在运行时加载**的，不走游戏的贴图通道

试过两条官方通道，都不行（2026-09-13 实测）：
  1. `textures.xml` + `action="add"` + 新 prefab 名 →
     放置建筑时 `NullReferenceException`（游戏按名字找不到预制体）
  2. `textures.xml` + `action="replace"` 覆盖 `workbench_0` →
     **洋红探针实测没生效**：世界里 0 个洋红像素（只有工具栏 14 个）

现在的做法（`CustomSprite.cs`）：DLL 在启动时
  * 从插件目录的 `Textures/` 读这两个 PNG
  * `ImageConversion.LoadImage` → `Texture2D` → `Sprite.Create`
  * 克隆一个游戏已有的建筑 prefab（`workbench`）并换掉它的 SpriteRenderer.sprite
  * Harmony 拦 `PrefabManager.GetPrefab("super_factory")` 返回这个克隆

好处：**完全不用游戏美术资源**，改图只要换 PNG 重启游戏，不用重编译 DLL。

尺寸依据：官方 mod 教程里 3×3 建筑示例 `kingdom_treasure_box_0.png` = 192×192
（3 格 × 64px），建筑图 anchor 用 `0,0`（左下角）。
"""
import sys
from pathlib import Path

from PIL import Image, ImageDraw

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "Defs" / "Textures"

# 文件名必须与 CustomSprite.cs 里的 SpriteNamePrefix / IconSpriteName 一致
SPRITE_PREFIX = "super_factory"     # 4 个朝向：super_factory_0..3
ICON_PNG = "ui_105050.png"

# 原图是按 32 像素/格画的，游戏要 64 像素/格 → 统一放大 2 倍
TEXTURE_UPSCALE = 2

# ---------------- 调色板（像素风，刻意压住颜色数量） ----------------
BRICK = (150, 62, 48)          # 红砖主体
BRICK_D = (118, 46, 36)        # 砖缝/暗部
BRICK_L = (176, 82, 62)        # 砖面高光
MORTAR = (196, 176, 158)       # 灰浆
ROOF = (92, 78, 70)            # 屋顶
ROOF_L = (116, 100, 90)
ROOF_D = (68, 56, 50)
CHIMNEY = (104, 62, 52)        # 烟囱砖（比墙深一点）
CHIMNEY_D = (78, 44, 38)
CHIMNEY_TOP = (58, 54, 56)
DOOR = (92, 62, 38)            # 木门
DOOR_L = (122, 86, 54)
DOOR_D = (66, 42, 26)
FRAME = (208, 190, 168)        # 门框/窗框（浅石）
WINDOW = (108, 152, 168)       # 玻璃
WINDOW_L = (150, 196, 208)
SHADOW = (26, 30, 44)          # 地面投影（和地砖暗部接近）
SIGN = (226, 186, 84)          # 金色招牌
SIGN_D = (168, 130, 48)
SMOKE = (178, 178, 186)


def brick_fill(d: ImageDraw.ImageDraw, box, mortar_rows=6, mortar_cols=5, seed=0):
    """在 box 里画红砖墙：底色 + 灰浆缝 + 随机深浅砖块。"""
    x0, y0, x1, y1 = box
    d.rectangle(box, fill=BRICK)
    w, h = x1 - x0, y1 - y0
    # 水平灰浆缝
    for i in range(1, mortar_rows):
        y = y0 + h * i // mortar_rows
        d.line([(x0, y), (x1 - 1, y)], fill=MORTAR)
    # 竖直砖缝（错缝）
    for i in range(mortar_rows):
        y_top = y0 + h * i // mortar_rows
        y_bot = y0 + h * (i + 1) // mortar_rows
        offset = (w // mortar_cols // 2) if i % 2 else 0
        for j in range(mortar_cols + 1):
            x = x0 + offset + w * j // mortar_cols
            if x0 < x < x1 - 1:
                d.line([(x, y_top + 1), (x, y_bot - 1)], fill=MORTAR)
    # 砖面明暗：按格子伪随机点几个高光/暗部，做出砖块质感
    rnd = (seed * 1103515245 + 12345) & 0x7FFFFFFF
    for i in range(mortar_rows):
        y_top = y0 + h * i // mortar_rows + 1
        y_bot = y0 + h * (i + 1) // mortar_rows - 1
        if y_bot <= y_top:
            continue
        for j in range(mortar_cols):
            rnd = (rnd * 1103515245 + 12345) & 0x7FFFFFFF
            cx = x0 + w * j // mortar_cols + 2
            cy = y_top
            cw = max(1, w // mortar_cols - 4)
            ch = max(1, y_bot - y_top)
            if cw < 2 or ch < 2:
                continue
            tone = rnd % 5
            if tone == 0:
                d.rectangle([cx, cy, cx + cw - 1, cy + ch - 1], fill=BRICK_L)
            elif tone == 1:
                d.rectangle([cx, cy, cx + cw - 1, cy + ch - 1], fill=BRICK_D)


def build_body(W=192, H=192):
    """192×192 的建筑本体：45° 俯视的方形红砖厂房。

    绘制顺序（决定了遮挡关系，改之前先想清楚）：
      地面投影 → 墙左右侧厚度 → **烟囱（先画，让屋顶压住它的底部，形成「从屋顶后方升起」）**
      → 屋顶 → 檐口 → 主体砖墙 → 门窗 → 门口踏步
    """
    img = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # --- 地面投影 ---
    d.ellipse([24, H - 28, W - 24, H - 4], fill=SHADOW)

    # --- 建筑几何（改坐标就改这几个常量）---
    rx0, ry0, rx1, ry1 = 20, 34, 172, 76          # 屋顶
    wx0, wy0, wx1, wy1 = 34, 76, 158, 166         # 正面墙
    cx0, cy0, cx1, cy1 = 56, 6, 90, 60            # 烟囱（底部被屋顶压住）

    # --- 侧墙厚度（先画，左右各露一条）---
    d.polygon([(wx0, wy0), (wx0 - 12, wy0 + 10), (wx0 - 12, wy1 + 6), (wx0, wy1)], fill=BRICK_D)
    d.polygon([(wx1, wy0), (wx1 + 12, wy0 + 10), (wx1 + 12, wy1 + 6), (wx1, wy1)], fill=BRICK_D)

    # --- 烟囱：先画砖体 + 顶盖 + 烟，稍后屋顶会压住底部 ---
    brick_fill(d, (cx0, cy0, cx1, cy1), mortar_rows=5, mortar_cols=2, seed=91)
    mask = Image.new("L", (W, H), 0)
    ImageDraw.Draw(mask).rectangle([cx0, cy0, cx1, cy1], fill=255)
    img.paste(Image.composite(Image.new("RGBA", (W, H), CHIMNEY + (255,)), img, mask), (0, 0))
    d = ImageDraw.Draw(img)
    for i in range(1, 5):
        y = cy0 + (cy1 - cy0) * i // 5
        d.line([(cx0, y), (cx1, y)], fill=MORTAR)
    d.line([((cx0 + cx1) // 2, cy0 + 3), ((cx0 + cx1) // 2, cy1 - 3)], fill=MORTAR)
    d.rectangle([cx0 - 4, cy0 - 5, cx1 + 4, cy0 + 3], fill=CHIMNEY_TOP)
    d.rectangle([cx0 - 4, cy0 - 5, cx1 + 4, cy0 - 2], fill=(84, 80, 82))
    # 烟（往上飘，两团）
    d.ellipse([cx0 + 4, cy0 - 20, cx0 + 18, cy0 - 6], fill=SMOKE + (140,))
    d.ellipse([cx0 + 14, cy0 - 32, cx0 + 30, cy0 - 16], fill=SMOKE + (100,))

    # --- 屋顶（压住烟囱底部）---
    d.rectangle([rx0, ry0, rx1, ry1], fill=ROOF)
    d.rectangle([rx0, ry0, rx1, ry0 + 6], fill=ROOF_L)
    for i in range(1, 4):
        y = ry0 + 10 + (ry1 - ry0 - 14) * i // 4
        d.line([(rx0 + 3, y), (rx1 - 3, y)], fill=ROOF_D)
    d.rectangle([rx0, ry1 - 3, rx1, ry1], fill=ROOF_D)

    # --- 檐口（浅石线，分开屋顶与墙）---
    d.rectangle([wx0 - 14, wy0 - 6, wx1 + 14, wy0 + 1], fill=FRAME)
    d.rectangle([wx0 - 14, wy0 + 1, wx1 + 14, wy0 + 3], fill=(168, 152, 132))

    # --- 正面砖墙 ---
    brick_fill(d, (wx0, wy0 + 3, wx1, wy1), mortar_rows=7, mortar_cols=4, seed=17)

    # --- 两扇窗 ---
    for x0 in (48, 126):
        d.rectangle([x0 - 2, 92, x0 + 22, 116], fill=FRAME)
        d.rectangle([x0, 94, x0 + 20, 114], fill=WINDOW)
        d.rectangle([x0, 94, x0 + 20, 98], fill=WINDOW_L)
        d.line([(x0 + 10, 94), (x0 + 10, 114)], fill=FRAME)
        d.line([(x0, 104), (x0 + 20, 104)], fill=FRAME)

    # --- 正面大门（居中）---
    dx0, dx1, dy0, dy1 = 80, 116, 116, 166
    d.rectangle([dx0 - 7, dy0 - 9, dx1 + 7, dy1], fill=FRAME)
    d.rectangle([dx0 - 4, dy0 - 6, dx1 + 4, dy1], fill=(178, 162, 142))
    d.rectangle([dx0, dy0, dx1, dy1], fill=DOOR)
    for x in (dx0 + 9, dx0 + 18, dx0 + 27):
        d.line([(x, dy0 + 3), (x, dy1 - 3)], fill=DOOR_D)
    d.rectangle([dx0, dy0, dx1, dy0 + 4], fill=DOOR_L)
    d.rectangle([dx1 - 10, dy0 + 20, dx1 - 6, dy0 + 27], fill=SIGN)
    # 门口踏步
    d.rectangle([dx0 - 11, dy1, dx1 + 11, dy1 + 6], fill=(176, 160, 142))
    d.line([(dx0 - 11, dy1 + 6), (dx1 + 11, dy1 + 6)], fill=(150, 136, 120))

    return img


def build_icon(size=64):
    """UI / 小地图图标：同款工厂的简化缩略图。"""
    body = build_body(192, 192)
    bbox = body.getbbox() or (0, 0, 192, 192)
    crop = body.crop(bbox)
    w, h = crop.size
    scale = min(size / w, size / h)
    nw, nh = max(1, int(w * scale)), max(1, int(h * scale))
    small = crop.resize((nw, nh), Image.LANCZOS)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(small, ((size - nw) // 2, (size - nh) // 2), small)
    return out


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    # 清掉历史策略留下的文件
    for old in ("workbench_0.png", "ui_105010.png", "textures.xml", "super_factory.png"):
        p = OUT / old
        if p.exists():
            p.unlink()
            print("清理旧文件:", old)

    src = REPO / "img"
    if not src.exists():
        raise SystemExit(f"缺少素材目录 {src}（放 4 张 3×2 建筑图：1_0..1_3.png）")

    # 用户提供的 4 张图 = 建筑的 4 个朝向（1_0..1_3）。
    #
    # ⚠ 必须放大 2 倍：游戏是 **64 像素/格**（官方教程里 3×3 建筑示例 = 192×192）。
    # 用户的图是 32 像素/格画的（横向 96×73、竖向 64×118），
    # 直接用的话 3×2 的建筑只画出 1.5×1.1 格 —— 用户实测反馈「贴图大小只有 1.5×1」就是这个原因。
    # 放大到 192×146（3×64 × 2×64 再按原图比例）后，用 ppu=64 正好铺满 3×2 占地格。
    # 用 NEAREST 放大保住像素风的硬边，不要用 LANCZOS（会糊）。
    for i in range(4):
        s = src / f"1_{i}.png"
        if not s.exists():
            raise SystemExit(f"缺少素材 {s}")
        im = Image.open(s).convert('RGBA')
        im2 = im.resize((im.width * TEXTURE_UPSCALE, im.height * TEXTURE_UPSCALE), Image.NEAREST)
        im2.save(OUT / f"{SPRITE_PREFIX}_{i}.png")
        print(f"生成: {SPRITE_PREFIX}_{i}.png ← img/1_{i}.png  {im.size} → {im2.size}（放大 {TEXTURE_UPSCALE}×）")
    Image.open(src / "1_0.png").convert("RGBA").resize((64, 64), Image.LANCZOS).save(OUT / ICON_PNG)
    print(f"生成: {ICON_PNG} ← img/1_0.png（缩到 64×64 当菜单图标）")

    # ⚠ 必须 action="add"：我们的贴图名游戏本来没有，add 才是「新增图片」。
    # 之前用 replace 覆盖游戏已有名字，实测不生效（洋红探针世界里 0 个洋红像素）。
    # 建筑图 anchor 用 "0,0"（左下角对齐占地格），这是官方 mod 教程里建筑图的写法。
    lines = ['<ModImages version="1">', '']
    for i in range(4):
        lines += [f'  <Image', f'      file="{SPRITE_PREFIX}_{i}.png"',
                  f'      action="add"', f'      anchor="0,0"', f'      />', '']
    lines += ['  <Image', f'      file="{ICON_PNG}"',
              '      action="add"', '      anchor="0.5,0.5"', '      />', '', '</ModImages>', '']
    (OUT / "textures.xml").write_text("\n".join(lines), encoding="utf-8", newline="\n")
    print("生成: textures.xml（4 个朝向 + 1 个图标，全部 action=add）")

    for f in sorted(OUT.iterdir()):
        print("  ", f.name, f.stat().st_size, "字节")
    print("输出目录:", OUT)


if __name__ == "__main__":
    main()
