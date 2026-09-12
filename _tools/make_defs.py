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
    # 产品直接声明 原木/石料（604001/605001，护林营地/采石场同款先例），
    # 这样窗口的今年/去年产量记录会真实累加 DLL 的产出。
    # formula_id 沿用「product_id*100+序号」官方惯例，取 40 槽位（对应 105040）防冲突。
    src_bp_wood = next(r for r in load("blueprint") if r.get("formula_id") == 60400100)
    new_bp_wood = dict(src_bp_wood)
    new_bp_wood["formula_id"] = 60404000
    new_bp_wood["facility_id"] = MOD_ID
    new_bp_wood["output_count"] = 10

    src_bp_stone = next(r for r in load("blueprint") if r.get("formula_id") == 60500100)
    new_bp_stone = dict(src_bp_stone)
    new_bp_stone["formula_id"] = 60504000
    new_bp_stone["facility_id"] = MOD_ID
    new_bp_stone["output_count"] = 10

    return new_stuff, new_build, new_tech, new_career, [new_bp_wood, new_bp_stone]


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
