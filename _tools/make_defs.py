#!/usr/bin/env python3
# 从官方数据表生成 Facility（综合生产所 105040）的 Def 文件
# 用法: python _tools/make_defs.py [--deploy]
# 行拷贝自官方 stuff.json/build.json 的 105006(采集营地)，只改需求字段。
#
# 部署目标：C:\TerritoryModTest\Defs\（游戏本地测试目录，启动时镜像同步进 plugins——
# 直接放 plugins 下自建文件夹会被游戏删掉）。JianZhu(101007) 已走创意工坊订阅，
# 本地测试通道里现在只有本 mod 的 105040 行，无需合并。
import json
import shutil
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
EXTRA_DATA = Path(r"C:\AI\领地部分源码(AI注释)\ExtraData")

MOD_ID = 105040
NAME = "综合生产所"
DESC = "安排工人后每天稳定产出原木、石料等资源。人数可调，产出清单见 BepInEx/config/claude.facility.cfg。"
TEMPLATE_ID = 105006  # 采集营地：原生工人系统（人数可调）+ 自带 bag，营地外观
TEST_DIR = Path(r"C:\TerritoryModTest")
DEF_DEPLOY_DIR = TEST_DIR / "Defs"

# 默认产出（每人每日）：必须与 Plugin.cs 里 cfg「每人每日产出」默认值一致
DEFAULT_PRODUCTS = [(604001, 10, "原木"), (605001, 10, "石料")]
# 窗口「额外产品候选」：必须与 Plugin.cs 的 cfg 默认值一致。
# 这些产品也要有 blueprint 行，否则窗口产量记录区不会给它们开条目。
DEFAULT_EXTRA_CANDIDATES = [(616001, "铁矿"), (616002, "秘银矿"), (612001, "红宝石")]
# formula_id 槽位基数：官方惯例 formula_id = product_id*100 + 序号，
# 序号在该设施内唯一即可（实测 105006 用 00/01/02/03/04 对应 5 个产品）。
# 105040 用 40 起的槽位，跟官方现有 206 行完全不冲突，也便于一眼认出是本 mod 的行。
FORMULA_SLOT_BASE = 40


def load(table: str):
    return json.loads((EXTRA_DATA / f"{table}.json").read_text(encoding="utf-8"))


def build_rows():
    stuff = load("stuff")
    build = load("build")
    career = load("career")

    src_stuff = next(r for r in stuff if r.get("stuff_id") == TEMPLATE_ID)
    new_stuff = dict(src_stuff)
    new_stuff["stuff_id"] = MOD_ID
    new_stuff["stuff_namezh-CN"] = NAME
    new_stuff["desczh-CN"] = DESC
    # prefab/stuff_img/stuff_img_on_map 随采集营地原样（游戏自带贴图，无需 mod 自带资源）

    src_build = next(r for r in build if r.get("id") == TEMPLATE_ID)
    new_build = dict(src_build)
    new_build["id"] = MOD_ID
    new_build["cellw"] = 3
    new_build["cellh"] = 3
    new_build["door_way"] = 1          # 随官方 3×3 矿井的门位掩码（1234 是 3×2 的）
    new_build["res_range"] = 0         # 关掉采集营地的野生菜生成——产出全走 DLL，数值精确
    new_build["res_range_anchor"] = 0
    new_build["guide_info_zh-CN"] = (
        "安排工人后，每个工人每天稳定产出资源\n"
        "# 产出内容与数量在 BepInEx/config/claude.facility.cfg 配置\n"
        "# 工人越多，每日产出越多；产出会自动入库"
    )

    # tech 行：tech_id=0 免科技；⚠ txt_id 是「分类段号」必须与 menu_group 配对——
    # 100=初始(0) 200=住所(1) 300=食品(2) 3700=制造(3) 1500=物流(4) 600=路桥(7)。
    # 用错段号（如 200）建筑不会出现在目标分类的建造菜单里（0.1.0 实测）。
    new_tech = {"txt_id": 300, "tech_id": 0, "facility_id": MOD_ID, "seed_id": "", "event_id": ""}

    # ⚠ career 行（职业表）是工人系统的总开关：Facility.GetOriginalWorkPosCount 查
    # D.career_dic_with_facility_id_as_key[stuff_id]，缺行=工位数 0 → 建筑窗口不显示
    # 工人控件、永远分不到人（0.1.0 实测窗口只有通用库存/产量模板）。
    # 拷采集营地行：npc_type=8 采集者（与采集营地共用同一职业池，官方多设施共用职业是常态），
    # 图标/帽子贴图沿用游戏自带 career_105006_m/f；manpower_limit 4（3×3 比 3×2 多 1 人）。
    # ⚠ is_main_facility 必须置 0：每个职业组（同 data_id）只允许一个主设施
    #   （主=105006 采集营地），重复主设施会把人手 DB 弄乱（0.2.0 实测窗口 99/99 不可交互）。
    src_career = next(r for r in career if r.get("facility_id") == TEMPLATE_ID)
    new_career = dict(src_career)
    new_career["facility_id"] = MOD_ID
    new_career["manpower_limit"] = 4
    new_career["is_main_facility"] = 0

    # ⚠ blueprint 行（产品蓝图表）是产品记录/数据键的总开关：
    # FacilityHuntingCabin.GetProductDataKeyList(stuff_id) 查蓝图字典[stuff_id]，
    # 缺行 → KeyNotFoundException，异常沿 WindowWorkFacility.SetInfo 一路炸断——
    # 窗口全部退化为预制体占位值（工人数 99/99、库存空、假产量记录），
    # 且 FacilityWork.IsReachLimit 在主任务循环 NpcTaskHelper.Tick 里反复抛（0.2.0 实测）。
    # 产品的**来源行**（决定 type/days/教育加成等字段）优先复用官方同产品行，
    # 官方没有该产品行时（如铁矿 616001 只在 105013 有）退回石料行当模板。
    bp_table = load("blueprint")
    stuff_table = load("stuff")
    stuff_type_by_id = {r.get("stuff_id"): r.get("stuff_type") for r in stuff_table}

    def blueprint_template(product_id: int) -> dict:
        for r in bp_table:
            if r.get("product_id") == product_id:
                return r
        for r in bp_table:
            if r.get("product_id") == 605001:   # 石料行：普通日产物，最中性
                return r
        raise RuntimeError("blueprint 表里找不到可用模板行")

    def make_blueprint(product_id: int, slot: int, output_count: int) -> dict:
        if product_id not in stuff_type_by_id:
            raise RuntimeError(f"物品 {product_id} 不在官方 stuff.json 里，先确认 id 再生成 Def")
        row = dict(blueprint_template(product_id))
        row["formula_id"] = product_id * 100 + slot
        row["facility_id"] = MOD_ID
        row["product_id"] = product_id
        row["output_count"] = output_count
        row["disable"] = 0
        row["need_research"] = 0
        return row

    # 产品清单 = 默认产出 + 额外产品候选，去重后依次分配 formula 槽位。
    # 额外产品（铁矿/秘银/红宝石等）也要有行：窗口「今年/去年产量」记录区按蓝图键开条目，
    # 缺行则 DLL 记进去的产量在窗口里看不到（0.4.0 修的正是这个）。
    ordered: list[tuple[int, int]] = []
    seen: set[int] = set()
    for pid, per, _name in DEFAULT_PRODUCTS:
        if pid not in seen:
            seen.add(pid)
            ordered.append((pid, per))
    for pid, _name in DEFAULT_EXTRA_CANDIDATES:
        if pid not in seen:
            seen.add(pid)
            ordered.append((pid, 10))   # 额外产品每日数量默认 10（cfg「额外产品每日数量」）

    blueprints = [make_blueprint(pid, FORMULA_SLOT_BASE + i, per)
                  for i, (pid, per) in enumerate(ordered)]

    return new_stuff, new_build, new_tech, new_career, blueprints


def main():
    new_stuff, new_build, new_tech, new_career, new_blueprints = build_rows()

    out_dir = REPO / "Defs"
    out_dir.mkdir(exist_ok=True)
    (out_dir / "stuff.json").write_text(
        json.dumps([new_stuff], ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    (out_dir / "build.json").write_text(
        json.dumps([new_build], ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    (out_dir / "tech.json").write_text(
        json.dumps([new_tech], ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    (out_dir / "career.json").write_text(
        json.dumps([new_career], ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    (out_dir / "blueprint.json").write_text(
        json.dumps(new_blueprints, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"generated: {out_dir}\\stuff.json, build.json, tech.json, career.json, blueprint.json")

    if "--deploy" in sys.argv:
        DEF_DEPLOY_DIR.mkdir(parents=True, exist_ok=True)
        for f in ("stuff.json", "build.json", "tech.json", "career.json", "blueprint.json"):
            shutil.copy2(out_dir / f, DEF_DEPLOY_DIR / f)
        print(f"deployed: {DEF_DEPLOY_DIR}")


if __name__ == "__main__":
    main()
