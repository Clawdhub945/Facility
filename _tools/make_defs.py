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
    new_build["can_exceed_worker_max_limit"] = 1  # 人数上限可突破（矿井同款），配合人数可调
    new_build["guide_info_zh-CN"] = (
        "安排工人后，每个工人每天稳定产出资源\n"
        "# 产出内容与数量在 BepInEx/config/claude.facility.cfg 配置\n"
        "# 工人越多，每日产出越多；产出会自动入库"
    )

    # tech 行：tech_id=0 免科技；⚠ txt_id 是「分类段号」必须与 menu_group 配对——
    # 100=初始(0) 200=住所(1) 300=食品(2) 3700=制造(3) 1500=物流(4) 600=路桥(7)。
    # 用错段号（如 200）建筑不会出现在目标分类的建造菜单里（0.1.0 实测）。
    new_tech = {"txt_id": 300, "tech_id": 0, "facility_id": MOD_ID, "seed_id": "", "event_id": ""}
    return new_stuff, new_build, new_tech


def main():
    new_stuff, new_build, new_tech = build_rows()

    out_dir = REPO / "Defs"
    out_dir.mkdir(exist_ok=True)
    (out_dir / "stuff.json").write_text(
        json.dumps([new_stuff], ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    (out_dir / "build.json").write_text(
        json.dumps([new_build], ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    (out_dir / "tech.json").write_text(
        json.dumps([new_tech], ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"generated: {out_dir}\\stuff.json, build.json, tech.json")

    if "--deploy" in sys.argv:
        DEF_DEPLOY_DIR.mkdir(parents=True, exist_ok=True)
        for f in ("stuff.json", "build.json", "tech.json"):
            shutil.copy2(out_dir / f, DEF_DEPLOY_DIR / f)
        print(f"deployed: {DEF_DEPLOY_DIR}")


if __name__ == "__main__":
    main()
