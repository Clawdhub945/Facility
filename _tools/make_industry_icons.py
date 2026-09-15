"""用 Pillow 画工业 mod 需要的图标（32×32，游戏一格 = 32 像素）。

产物（输出到 Defs/Textures/）：
  * ui_603010.png —— 钢锭的 UI 图标（银白金属锭，比铁锭更亮更冷）
  * 603010.png    —— 钢锭在世界里的小图标
  * ui_403010.png —— 钢制工具的 UI 图标（带钢蓝刃口的工具）
  * 403010.png    —— 钢制工具在世界里的小图标
  * ui_105053.png —— 高炉的建造菜单图标（红砖炉体 + 烟囱 + 火光）

风格参考游戏自带图标：像素风、有明显描边、单色为主 + 高光。

用法： python _tools/make_industry_icons.py [--deploy]
"""
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
from PIL import Image, ImageDraw  # noqa: E402

REPO = Path(__file__).resolve().parent.parent
OUT = REPO / "Defs" / "Textures"
DEPLOY = Path(r"C:\TerritoryModTest\Textures")

SIZE = 32  # 游戏一格 = 32 像素；UI 图标也用 32×32


def ingot(body, light, dark, edge):
    """画一个金属锭（梯形块 + 高光 + 描边）"""
    im = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    # 梯形锭身
    d.polygon([(6, 22), (10, 12), (22, 12), (26, 22)], fill=body, outline=edge)
    # 顶面高光
    d.polygon([(10, 12), (22, 12), (20, 15), (12, 15)], fill=light)
    # 侧面暗部
    d.polygon([(6, 22), (10, 15), (12, 15), (8, 22)], fill=dark)
    return im


def tool(body, blade, edge, handle):
    """画一把工具（木柄 + 金属头部）"""
    im = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    # 木柄（斜）
    d.line([(10, 26), (20, 14)], fill=handle, width=4)
    d.line([(10, 26), (20, 14)], fill=edge, width=1)
    # 金属头
    d.polygon([(18, 8), (26, 10), (24, 16), (17, 13)], fill=body, outline=edge)
    # 刃口（更亮）
    d.line([(18, 8), (26, 10)], fill=blade, width=2)
    return im


def blast_furnace():
    """画高炉图标：红砖炉体 + 烟囱 + 炉口火光"""
    im = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    brick = (150, 62, 48, 255)
    brick_d = (110, 44, 34, 255)
    edge = (58, 26, 20, 255)
    fire = (245, 176, 66, 255)
    # 炉体
    d.rectangle([4, 14, 27, 28], fill=brick, outline=edge)
    # 砖缝
    for y in (19, 24):
        d.line([(4, y), (27, y)], fill=brick_d, width=1)
    # 烟囱
    d.rectangle([19, 4, 24, 14], fill=brick_d, outline=edge)
    # 炉口火光
    d.rectangle([9, 20, 16, 27], fill=(40, 22, 18, 255), outline=edge)
    d.polygon([(10, 27), (15, 27), (13, 22)], fill=fire)
    return im


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    made = []

    steel = ingot(body=(178, 186, 196, 255), light=(228, 234, 240, 255),
                   dark=(120, 130, 142, 255), edge=(60, 68, 78, 255))
    steel.save(OUT / "ui_603010.png"); made.append("ui_603010.png")
    steel.save(OUT / "603010.png"); made.append("603010.png")

    st = tool(body=(150, 162, 176, 255), blade=(226, 234, 244, 255),
              edge=(52, 60, 70, 255), handle=(150, 106, 62, 255))
    st.save(OUT / "ui_403010.png"); made.append("ui_403010.png")
    st.save(OUT / "403010.png"); made.append("403010.png")

    bf = blast_furnace()
    bf.save(OUT / "ui_105053.png"); made.append("ui_105053.png")

    for m in made:
        p = OUT / m
        print(f"  生成 {m}  {p.stat().st_size} 字节  {Image.open(p).size}")

    # ⚠⚠ **必须把新图写进 textures.xml**，否则游戏不认这些 sprite：
    #   症状一：建造菜单里图标是**白块**
    #   症状二：物品格（制造台配方产物）里图标**透明**
    #   原因：`stuff_img` / `stuff_img_on_map` 只是 sprite **名字**，
    #         真正的贴图要由 `Textures/textures.xml` 的 `<Image action="add">` 注册进游戏贴图表。
    #   这个坑踩过：只把 png 拷进 Textures 目录、忘了更新清单 → 图标不显示。
    update_textures_xml(made)

    argv = sys.argv[1:]
    if "--deploy" in argv:
        DEPLOY.mkdir(parents=True, exist_ok=True)
        for m in made:
            (DEPLOY / m).write_bytes((OUT / m).read_bytes())
        # 清单也要一起部署
        (DEPLOY / "textures.xml").write_bytes((OUT / "textures.xml").read_bytes())
        print(f"已部署到 {DEPLOY}（含 textures.xml）")


def update_textures_xml(new_files):
    """把新图**合并**进 Defs/Textures/textures.xml（保持已有条目，不覆盖别的脚本的产出）。

    条目约定：
      * 建筑/世界图（铺在地上的）→ `anchor="0,0"`（左下角对齐，官方教程对建筑图的写法）
      * UI 图标（居中显示）      → `anchor="0.5,0.5"`
    这里按文件名判断：`ui_*` 走居中，其余走左下角。
    """
    import re
    xml_path = OUT / "textures.xml"
    text = xml_path.read_text(encoding="utf-8") if xml_path.exists() else '<ModImages version="1">\n</ModImages>\n'

    added = []
    for f in new_files:
        if re.search(rf'file="{re.escape(f)}"', text):
            continue
        anchor = "0.5,0.5" if f.startswith("ui_") else "0,0"
        entry = (f'\n  <Image\n      file="{f}"\n'
                 f'      action="add"\n      anchor="{anchor}"\n      />\n')
        text = text.replace("</ModImages>", entry + "</ModImages>")
        added.append(f)

    xml_path.write_text(text, encoding="utf-8", newline="\n")
    if added:
        print(f"  textures.xml 新增 {len(added)} 条: {', '.join(added)}")
    else:
        print("  textures.xml 已包含全部图标，无需改动")


if __name__ == "__main__":
    main()
