#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从官方数据表生成 Facility mod 的 Def 文件（多建筑）。

用法:
    python _tools/make_defs.py [--deploy]
    python _tools/make_defs.py --appearance 制造台 --deploy   # 只影响旧建筑 105040

产物（写到 Defs/，--deploy 时复制到 C:\\TerritoryModTest\\Defs）：

| 表 | 内容 |
|---|---|
| stuff.json | 两座建筑的 stuff 行（含外观 prefab/图标） |
| build.json | 两座建筑的 build 行（尺寸/门位/菜单分类） |
| tech.json | 建造菜单可见性（txt_id 分类段 + tech_id 解锁条件） |
| career.json | 工人系统（缺了窗口就没有工人控件） |
| blueprint.json | 产品行（缺了窗口会 KeyNotFoundException 炸绑定） |
| tech_tree.json | 新科技节点（超级生产所挂在自己新建的、无前置的科技点下） |

⚠ **新增表（tech_tree.json）必须重启游戏**才生效：建造菜单/科技树 UI 是进程启动时装配的。
"""

import json
import shutil
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
EXTRA_DATA = Path(r"C:\AI\领地部分源码(AI注释)\ExtraData")

# ============ 建筑定义 ============
# 旧建筑（保留）：综合生产所，外观可换，免科技，食品分类
MOD_ID = 105040
NAME = "综合生产所"
DESC = "安排工人后每天稳定产出原木、石料等资源。人数可调，产出清单见 BepInEx/config/claude.facility.cfg。"
TEMPLATE_ID = 105006  # 采集营地：原生工人系统（人数可调）+ 自带 bag

# 新建筑：超级生产所，自定义外观（mod 自带贴图），挂在自己新建的科技节点下
SUPER_ID = 105050
SUPER_NAME = "超级生产所"
SUPER_DESC = ("由红砖厂房改建的大型生产设施。安排工人后每天稳定产出资源，"
              "产出清单与人数上限见 BepInEx/config/claude.facility.cfg。")
# 自定义外观：这个 prefab 名**游戏本身没有**，由 mod 的 DLL 在运行时提供
# （`CustomSprite.cs` 读 Textures/super_factory.png → 造 Sprite →
#   克隆 workbench prefab 换图 → Harmony 拦 PrefabManager.GetPrefab 返回克隆）。
# 试过的两条官方贴图通道都不行：action="add" 会 NullReferenceException，
# action="replace" 洋红探针实测不生效（详见 make_textures.py 顶部注释）。
SUPER_PREFAB = "workbench"
SUPER_IMG = f"ui_{SUPER_ID}"           # 自定义 UI 图标名（DLL 用 SpriteManager.AddSpriteToDic 注册，实测有效）
SUPER_IMG_ON_MAP = "workbench_0"       # 场景/小地图贴图：用游戏已有的名字，保证一定能解析出模型
# 科技：mod 通道加不了新科技树（tech_tree.json 不被读取），按用户要求**取消科技门槛**
SUPER_TECH_ID = 0
SUPER_TXT_ID = 300                    # 建造菜单分类段号（与旧建筑同用「食品」段）

TEST_DIR = Path(r"C:\TerritoryModTest")
DEF_DEPLOY_DIR = TEST_DIR / "Defs"
TEX_DEPLOY_DIR = TEST_DIR / "Textures"

# 默认产出（每人每日）：必须与 Plugin.cs 里 cfg「每人每日产出」默认值一致
DEFAULT_PRODUCTS = [(604001, 10, "原木"), (605001, 10, "石料")]
# 窗口「额外产品候选」：必须与 Plugin.cs 的 cfg 默认值一致
DEFAULT_EXTRA_CANDIDATES = [(616001, "铁矿"), (616002, "秘银矿"), (612001, "红宝石")]
# formula_id 槽位基数：官方惯例 formula_id = product_id*100 + 序号；
# 105040 用 40 起、105050 用 50 起，跟官方 206 行都不冲突，也便于一眼认出是本 mod 的行。
FORMULA_SLOT_BASE = {MOD_ID: 40, SUPER_ID: 50}

# 建筑外观预设（只对旧建筑 105040 生效；新建筑用 mod 自带贴图）：
# stuff.json 的 prefab / stuff_img / stuff_img_on_map 都换成目标建筑的资源名。
# 资源名必须是游戏已有资源（官方 139 座建筑的 prefab 都是裸名，贴图是 ui_<id> 与 <prefab>_0）。
APPEARANCE_PRESETS = {
    "采集营地": ("gatherers_hut", "ui_105006", "gatherers_hut_0", "0.4.0 之前的外观（3×2 原型）"),
    "磨坊":     ("mill2",         "ui_105032", "mill2_0",         "4×4 磨坊：风车造型，最好认"),
    "交易台":   ("trading_desk",  "ui_103004", "trading_desk_0",  "3×3 交易台：尺寸最贴"),
    "制造台":   ("workbench",     "ui_105010", "workbench_0",     "3×1 制造台：工坊感"),
    "熔炉":     ("furnace",       "ui_105028", "furnace_0",       "2×2 熔炉"),
}
DEFAULT_APPEARANCE = "制造台"


def load(table: str):
    return json.loads((EXTRA_DATA / f"{table}.json").read_text(encoding="utf-8"))


def make_blueprints(bp_table, stuff_type_by_id, facility_id, products):
    """按产品清单生成 blueprint 行。

    ⚠ blueprint 行（产品蓝图表）是产品记录/数据键的总开关：
    `FacilityHuntingCabin.GetProductDataKeyList(stuff_id)` 查蓝图字典[stuff_id]，
    缺行 → KeyNotFoundException，异常沿 WindowWorkFacility.SetInfo 一路炸断——
    窗口全部退化为预制体占位值（工人数 99/99、库存空、假产量记录），
    且 FacilityWork.IsReachLimit 在主任务循环 NpcTaskHelper.Tick 里反复抛（0.2.0 实测）。

    产品的**来源行**（决定 type/days/教育加成等字段）优先复用官方同产品行，
    官方没有该产品行时（如铁矿 616001 只在 105013 有）退回石料行当模板。
    """
    def template(product_id: int) -> dict:
        for r in bp_table:
            if r.get("product_id") == product_id:
                return r
        for r in bp_table:
            if r.get("product_id") == 605001:   # 石料行：普通日产物，最中性
                return r
        raise RuntimeError("blueprint 表里找不到可用模板行")

    rows = []
    for i, (pid, per) in enumerate(products):
        if pid not in stuff_type_by_id:
            raise RuntimeError(f"物品 {pid} 不在官方 stuff.json 里，先确认 id 再生成 Def")
        row = dict(template(pid))
        row["formula_id"] = pid * 100 + FORMULA_SLOT_BASE[facility_id] + i
        row["facility_id"] = facility_id
        row["product_id"] = pid
        row["output_count"] = per
        row["disable"] = 0
        row["need_research"] = 0
        rows.append(row)
    return rows


def products_for(facility_id):
    """产品清单 = 默认产出 + 额外产品候选，去重后依次排（决定 formula 槽位）。"""
    ordered, seen = [], set()
    for pid, per, _name in DEFAULT_PRODUCTS:
        if pid not in seen:
            seen.add(pid)
            ordered.append((pid, per))
    for pid, _name in DEFAULT_EXTRA_CANDIDATES:
        if pid not in seen:
            seen.add(pid)
            ordered.append((pid, 10))   # 额外产品每日数量默认 10
    return ordered


def build_all(appearance: str = DEFAULT_APPEARANCE):
    stuff = load("stuff")
    build = load("build")
    career = load("career")
    bp_table = load("blueprint")
    stuff_type_by_id = {r.get("stuff_id"): r.get("stuff_type") for r in stuff}

    if appearance not in APPEARANCE_PRESETS:
        raise SystemExit(f"未知外观预设「{appearance}」，可选：{'、'.join(APPEARANCE_PRESETS)}")
    prefab, img, img_on_map, _note = APPEARANCE_PRESETS[appearance]

    src_stuff = next(r for r in stuff if r.get("stuff_id") == TEMPLATE_ID)
    src_build = next(r for r in build if r.get("id") == TEMPLATE_ID)
    # ⚠ career 行（职业表）是工人系统的总开关：Facility.GetOriginalWorkPosCount 查
    # D.career_dic_with_facility_id_as_key[stuff_id]，缺行=工位数 0 → 建筑窗口不显示
    # 工人控件、永远分不到人（0.1.0 实测）。
    # ⚠ is_main_facility 必须置 0：每个职业组（同 data_id）只允许一个主设施
    #   （主=105006 采集营地），重复主设施会把人手 DB 弄乱（0.2.0 实测窗口 99/99 不可交互）。
    src_career = next(r for r in career if r.get("facility_id") == TEMPLATE_ID)

    def stuff_row(sid, name, desc, icon, icon_map, model):
        r = dict(src_stuff)
        r.update({
            "stuff_id": sid,
            "stuff_namezh-CN": name,
            "desczh-CN": desc,
            "prefab": model,          # 场景里的模型/贴图名
            "stuff_img": icon,        # UI 图标
            "stuff_img_on_map": icon_map,
        })
        return r

    def build_row(sid, model, guide):
        r = dict(src_build)
        r.update({
            "id": sid,
            "cellw": 3,
            "cellh": 3,
            "door_way": 1,            # 3×3 门位掩码（1234 是 3×2 的）
            "res_range": 0,           # 关掉采集营地的野生菜生成——产出全走 DLL，数值精确
            "res_range_anchor": 0,
            "guide_info_zh-CN": guide,
        })
        return r

    def career_row(sid):
        r = dict(src_career)
        r.update({"facility_id": sid, "manpower_limit": 4, "is_main_facility": 0})
        return r

    guide_common = ("安排工人后，每个工人每天稳定产出资源\n"
                    "# 产出内容与数量在 BepInEx/config/claude.facility.cfg 配置\n"
                    "# 工人越多，每日产出越多；产出会自动入库")

    stuff_rows = [
        stuff_row(MOD_ID, NAME, DESC, img, img_on_map, prefab),
        stuff_row(SUPER_ID, SUPER_NAME, SUPER_DESC, SUPER_IMG, SUPER_IMG_ON_MAP, SUPER_PREFAB),
    ]
    build_rows = [
        build_row(MOD_ID, prefab, guide_common),
        build_row(SUPER_ID, SUPER_PREFAB, guide_common),
    ]
    # tech 行的 txt_id 是「分类段号」必须与 menu_group 配对——
    # 100=初始(0) 200=住所(1) 300=食品(2) 3700=制造(3) 1500=物流(4) 600=路桥(7)。
    # 用错段号建筑不会出现在目标分类的建造菜单里（0.1.0 实测）。
    # 新建筑按用户要求**免科技**（tech_id=0），开局即可建造。
    tech_rows = [
        {"txt_id": SUPER_TXT_ID, "tech_id": 0, "facility_id": MOD_ID, "seed_id": "", "event_id": ""},
        {"txt_id": SUPER_TXT_ID, "tech_id": SUPER_TECH_ID, "facility_id": SUPER_ID,
         "seed_id": "", "event_id": "",
         "tech_desc_zh-CN": "建造超级生产所：红砖厂房，产量与综合生产所一致"},
    ]
    career_rows = [career_row(MOD_ID), career_row(SUPER_ID)]

    blueprint_rows = (make_blueprints(bp_table, stuff_type_by_id, MOD_ID, products_for(MOD_ID)) +
                      make_blueprints(bp_table, stuff_type_by_id, SUPER_ID, products_for(SUPER_ID)))

    # 科技树表**不要写**：游戏 mod 通道不读 tech_tree.json（实测写进去也不会进树）。
    # 保留空表只为了让旧部署目录里的同名文件被清空，避免残留旧内容。
    tech_tree_rows: list = []

    return (stuff_rows, build_rows, tech_rows, career_rows, blueprint_rows, tech_tree_rows)


def main():
    argv = sys.argv[1:]
    appearance = DEFAULT_APPEARANCE
    if "--appearance" in argv:
        i = argv.index("--appearance")
        if i + 1 >= len(argv):
            raise SystemExit("--appearance 后面要跟外观名")
        appearance = argv[i + 1]

    stuff_rows, build_rows, tech_rows, career_rows, bp_rows, tree_rows = build_all(appearance)
    print(f"外观(仅 {MOD_ID}): {appearance} → prefab={stuff_rows[0]['prefab']}")
    print(f"新建科技节点: tech_id={SUPER_TECH_ID}（无前置，排官方 {902002} 之前）")

    out_dir = REPO / "Defs"
    out_dir.mkdir(exist_ok=True)
    files = {
        "stuff.json": stuff_rows,
        "build.json": build_rows,
        "tech.json": tech_rows,
        "career.json": career_rows,
        "blueprint.json": bp_rows,
        "tech_tree.json": tree_rows,
    }
    for name, rows in files.items():
        (out_dir / name).write_text(
            json.dumps(rows, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8", newline="\n")
    print("generated:", ", ".join(files))

    if "--deploy" in argv:
        DEF_DEPLOY_DIR.mkdir(parents=True, exist_ok=True)
        for name in files:
            shutil.copy2(out_dir / name, DEF_DEPLOY_DIR / name)
        print(f"deployed: {DEF_DEPLOY_DIR}")
        tex_src = out_dir / "Textures"
        if tex_src.exists():
            TEX_DEPLOY_DIR.mkdir(parents=True, exist_ok=True)
            # 先同步删除：部署目录里多余的文件必须清掉。
            # 实测踩坑：`workbench_0.png`（覆盖原版制造台贴图）与洋红探针图残留过一次，
            # 会让游戏里所有制造台都变成我们的图/纯色块。
            keep = {f.name for f in tex_src.iterdir()}
            for old in TEX_DEPLOY_DIR.iterdir():
                if old.name not in keep:
                    old.unlink()
                    print(f"  删除部署目录里的多余贴图: {old.name}")
            for f in tex_src.iterdir():
                shutil.copy2(f, TEX_DEPLOY_DIR / f.name)
            print(f"deployed Textures: {TEX_DEPLOY_DIR}")


if __name__ == "__main__":
    main()
