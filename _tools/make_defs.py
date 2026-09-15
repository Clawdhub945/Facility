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
# ⚠ **必须用原生 3×2 的 prefab**：workbench（制造台）是 **3×1** 的，
# 拿它当 3×2 建筑的基础会导致「模型只画 1×3、占地却是 3×2」（用户实测反馈）。
# gatherers_hut（采集营地）原生就是 3×2、class_name 也是 FacilityGatherersHut、
# 同样有工人系统 —— 和超级生产所完全同款，是最合适的骨架。
SUPER_PREFAB = "gatherers_hut"
SUPER_IMG = f"ui_{SUPER_ID}"           # 自定义 UI 图标名（DLL 用 SpriteManager.AddSpriteToDic 注册，实测有效）
SUPER_IMG_ON_MAP = "super_factory_0"     # 场景贴图：我们自己新增的（textures.xml action="add"），\u65b9\u5411 0
# 科技：mod 通道加不了新科技树（tech_tree.json 不被读取），按用户要求**取消科技门槛**
SUPER_TECH_ID = 0
SUPER_TXT_ID = 300                    # 建造菜单分类段号（与旧建筑同用「食品」段）

# ⚠ 超级生产所的窗口预制体**必须写进生成器**：
#   它决定用哪套原生 UI。手改 TerritoryModTest/Defs/build.json 是不行的 ——
#   下次 make_defs.py --deploy 会把 window_prefab 重置回默认（踩过：
#   结果超级生产所又变回「自绘选择条」，那条白底看不清的横条）。
#   用 window_blacksmith 是为了拿到游戏**原生下拉** dp_blueprint。
SUPER_WINDOW = "window_blacksmith"

# ---------------------------------------------------------------------------
# 实验建筑 105051：3×3 高炉兼容性实验
#
# 目的：验证「3×3 占地 + 熔炉机制（FacilityFurnace）+ 熔炉窗口」能不能共存。
# 背景：熔炉(105028) 原生是 **2×2**、矿井(105013) 是 **3×3**；
#       熔炉的 `CreateMaterialsPosList` 会按格子摆材料位置，**对占地可能有硬假设**。
#       若这个组合能跑，工业 mod 的「高炉烧煤（煤=燃料）」就能做成 3×3。
#
# 骨架：`mine`（3×3），类：`FacilityFurnace`，窗口：`window_furnace`
# ---------------------------------------------------------------------------
FURNACE3_ID = 105051
FURNACE3_NAME = "三乘三高炉实验"
FURNACE3_DESC = "兼容性实验建筑：3×3 占地 + 熔炉机制。用于验证尺寸是否兼容。"
# ⚠ 骨架 prefab **自带建造地形限制**：mine（矿井）只能建在山体上（用户实测）。
#   限制来自预制体本身（build.json 里没有地形字段），不是数据能改的。
#   换成 trading_desk（交易台）—— 原生 3×3、平地上可建、无限制字段。
FURNACE3_PREFAB = "trading_desk"     # 3×3 骨架（交易台）
FURNACE3_CLASS = "FacilityFurnace"    # 熔炉机制（燃料 + 生产计划）
FURNACE3_WINDOW = "window_furnace"
FURNACE3_IMG = "ui_103004"   # 暂借交易台图标（保证菜单里不是白块）
FURNACE3_IMG_ON_MAP = "trading_desk_0"
FURNACE3_CELL = 3

# ---------------------------------------------------------------------------
# 对照实验 105052：**原生配置的熔炉**（骨架/类/窗口三者一致）
#
# 目的：定位「高炉窗口能显示但控件不能交互」到底是哪一层的问题。
# 做法：完全照抄原生熔炉(105028) 的三要素：prefab=`furnace`、
#       class=`FacilityFurnace`、window=`window_furnace`、2×2。
#
# 与 105051 的**唯一差别**就是骨架 prefab（`furnace` vs `trading_desk`）：
#   * 若 105052 交互正常、105051 不正常 → 证实「prefab 上的组件必须与 class_name 一致」
#     （`furnace` 预制体自带 FacilityFurnace，而 `trading_desk` 自带 FacilityTradingDesk）
#   * 若两者都不正常 → 问题在别处（例如窗口依赖 2×2 的其他假设）
#
# 结论会决定工业 mod 的高炉走哪条路：3×3 借骨架，还是就用 2×2 原生熔炉。
# ---------------------------------------------------------------------------
FURNACE_NATIVE_ID = 105052
FURNACE_NATIVE_NAME = "熔炉对照实验"
FURNACE_NATIVE_DESC = "对照实验：完全照抄原生熔炉的骨架/类/窗口，用于定位交互问题。"
FURNACE_NATIVE_PREFAB = "furnace"
FURNACE_NATIVE_CLASS = "FacilityFurnace"
FURNACE_NATIVE_WINDOW = "window_furnace"
FURNACE_NATIVE_IMG = "ui_105028"        # 直接用原生熔炉图标
FURNACE_NATIVE_IMG_ON_MAP = "furnace_0"
FURNACE_NATIVE_CELL = 2

# ---------------------------------------------------------------------------
# 工业 mod 内容
# ---------------------------------------------------------------------------

# 新物品「钢」：type 6（资源）/ sub 603（金属），与铁锭 603001 同段
#   prefab 用通用的 stuff_on_map（**不需要自己做世界模型**，实测铁锭等也是这样）
#   stuff_img / stuff_img_on_map 指向我们自己画的图标（由 make_industry_icons.py 生成）
STEEL_ID = 603010
STEEL_NAME = "钢"
STEEL_DESC = "高炉冶炼出的钢材。比铁更坚硬，可制作钢制工具。"
STEEL_IMG = "ui_603010"
STEEL_IMG_ON_MAP = "603010"

# 新物品「钢制工具」：type 4（物品）/ sub 403（工具），效率加成高于优质工具(3.0)
STEEL_TOOL_ID = 403010
STEEL_TOOL_NAME = "钢制工具"
STEEL_TOOL_DESC = "钢制工具，比优质工具更耐用高效。"
STEEL_TOOL_IMG = "ui_403010"
STEEL_TOOL_IMG_ON_MAP = "403010"
STEEL_TOOL_EFFECT = 4.0            # 效率加成（普通2.0 / 优质3.0）
STEEL_TOOL_PRICE = 40

# 新建筑「高炉」：**必须用原生熔炉骨架**（urnace，2×2）——
# 实测：window_furnace 的控件（配方下拉、燃料设置）依赖 prefab 上的 FacilityFurnace 组件，
# 借别尺寸/别类型的骨架会导致「能显示但不能交互」。
# 煤作为**燃料**（煤的 remark=「燃料系数」、effect_value=5.0）。
BLAST_ID = 105053
BLAST_NAME = "高炉"
BLAST_DESC = ("冶炼钢材的工业设施。以煤为燃料，把铁锭炼成钢。"
              "\n# 配方：铁锭 → 钢；燃料：煤（在窗口里设置燃料类型）")
BLAST_PREFAB = "furnace"
BLAST_CLASS = "FacilityFurnace"
BLAST_WINDOW = "window_furnace"
BLAST_IMG = "ui_105053"           # 自己画的高炉图标
BLAST_IMG_ON_MAP = "furnace_0"     # 世界外观沿用熔炉（骨架决定）
BLAST_CELL = 2

# 高炉配方：铁锭×2 → 钢×1（days=2 表示更快）
BLAST_RECIPE = {
    "product_id": STEEL_ID,
    "output_count": 1,
    "days": 2.0,
    "materials": [(603001, 2)],       # 铁锭 ×2
}

# 制造台配方：原木×10 + 钢×3 → 钢制工具×1（用户指定）
#   ⚠ 这条写的是**原版制造台**(105010) 的表 → 跨 mod 写同一张表，有冲突风险（用户已确认接受）。
WORKBENCH_ID = 105010
STEEL_TOOL_RECIPE = {
    "product_id": STEEL_TOOL_ID,
    "output_count": 1,
    "days": 10.0,
    "materials": [(604001, 10), (STEEL_ID, 3)],   # 原木×10 + 钢×3
}

# ---------------------------------------------------------------------------
# 3×3 高炉（自研炉）
#
# ## 为什么自研
# 对照实验结论：`window_furnace` 的控件依赖 **prefab 上的 FacilityFurnace 组件**，
# 而原生熔炉骨架是 **2×2** —— 借 3×3 骨架配 FacilityFurnace 会导致
# 「窗口能显示但控件不可交互」（配方下拉空、燃料按钮点不动）。
# 用户要 3×3，所以燃料/配方/产出逻辑由本 mod 的 DLL 自己实现
# （见 `SmelterConsumer.cs`）。
#
# ## 骨架选型
# 用 `trading_desk`（交易台，3×3，无地形限制）：它带 FacilityTradingDesk 组件，
# 但**我们不需要它的功能** —— 只要能放、能存东西。
# 冶炼由我们自己的 DLL 做（换日结算），所以**不与它的组件冲突**。
# 窗口用 `window_gatherers_hut`（有工人/存储界面，我们最熟）。
# ---------------------------------------------------------------------------
BLAST3_ID = 105054
BLAST3_NAME = "高炉"
BLAST3_DESC = ("工业高炉（3×3）。以煤为燃料，把铁锭冶炼成钢。"
               "\n# 把铁锭与煤放进建筑仓库，每个游戏日自动冶炼一次"
               "\n# 配方与燃料消耗由本 mod 配置")
BLAST3_PREFAB = "trading_desk"      # 3×3 骨架（无地形限制）
BLAST3_CLASS = "FacilityTradingDesk"
BLAST3_WINDOW = "window_gatherers_hut"
BLAST3_IMG = "ui_105054"
BLAST3_IMG_ON_MAP = "trading_desk_0"
BLAST3_CELL = 3

# 自研冶炼配方：铁锭×2 + 煤×1 → 钢×1（每游戏日一炉）
# ⚠ 这份配方与游戏的 blueprint.json **无关** —— 是 SmelterConsumer 自己的规格，
#   所以 3×3 也能自由定义（不受"配方 UI 按 facility_id 取行"的限制）。
BLAST3_SMELT_INPUTS = [(603001, 2)]      # 铁锭 ×2
BLAST3_SMELT_FUEL = (601001, 1)          # 煤 ×1
BLAST3_SMELT_OUTPUT = (603010, 1)        # 钢 ×1

TEST_DIR = Path(r"C:\TerritoryModTest")
DEF_DEPLOY_DIR = TEST_DIR / "Defs"
TEX_DEPLOY_DIR = TEST_DIR / "Textures"

# 默认产出（每人每日）：必须与 Plugin.cs 里 cfg「每人每日产出」默认值一致
DEFAULT_PRODUCTS = [(604001, 10, "原木"), (605001, 10, "石料")]
# 窗口「额外产品候选」：必须与 Plugin.cs 的 cfg 默认值一致
DEFAULT_EXTRA_CANDIDATES = [(616001, "铁矿"), (616002, "秘银矿"), (612001, "红宝石")]
# formula_id 槽位基数：官方惯例 formula_id = product_id*100 + 序号；
# 105040 用 40 起、105050 用 50 起，跟官方 206 行都不冲突，也便于一眼认出是本 mod 的行。
FORMULA_SLOT_BASE = {MOD_ID: 40, SUPER_ID: 50, FURNACE3_ID: 51, FURNACE_NATIVE_ID: 52, BLAST_ID: 53, BLAST3_ID: 54}

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


def clone_facility_blueprints(bp_table, src_facility_id, dst_facility_id, seen_formula_ids=None):
    """把**某个原生建筑的全部配方**克隆给我们的建筑。

    ## 用途
    熔炉系建筑（`FacilityFurnace`）的配方 UI 读的是 `blueprint.json` 里
    `facility_id == 自己` 的那些行。新建一座熔炉（id 105052）时，
    游戏**不会**自动继承原生熔炉(105028) 的配方 —— 实测：窗口里
    「工作类型」有值但**配方下拉是空的**。
    所以要显式克隆一份，把 `facility_id` 换成我们的 id。

    ## formula_id 的处理
    官方惯例是 `formula_id = product_id * 100 + 序号`。克隆时保持 product_id 不变，
    只换末尾的序号（用 `FORMULA_SLOT_BASE[dst]` 起递增），保证：
      * 不同的 (产品, 材料) 组合仍有不同 formula_id
      * 不占用原生行的 formula_id（避免冲突）
    同一 (product_id, 材料组合) 视为同一配方，**去重**。

    ## ⚠ 为什么不去改原生行
    改 `facility_id=105028` 的行等于**改原版熔炉**（跨 mod 写同一张表 → 冲突风险），
    而且会让原生熔炉的配方也变成我们的。所以只**新增**属于我们 id 的行。
    """
    if seen_formula_ids is None:
        seen_formula_ids = set()

    mat_keys = [("_material_1", "_n1"), ("_material_2", "_n2"),
                ("_material_3", "_n3"), ("_material_4", "_n4")]
    rows = []
    dedup = set()
    slot = 1
    for src in bp_table:
        if src.get("facility_id") != src_facility_id:
            continue
        sig = (src.get("product_id"),) + tuple(src.get(k) for k, _ in mat_keys)
        if sig in dedup:
            continue
        dedup.add(sig)

        row = dict(src)
        # 找一个没被占用的 formula_id（保持 product_id 前缀，只换末尾序号）
        pid = src.get("product_id") or 0
        while True:
            fid = pid * 100 + slot
            slot += 1
            if fid not in seen_formula_ids:
                break
        seen_formula_ids.add(fid)

        row["formula_id"] = fid
        row["facility_id"] = dst_facility_id
        row["disable"] = 0
        row["need_research"] = 0        # 免科技：新建筑开局即可用
        rows.append(row)
    return rows


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

    def item_row(src_id, sid, name, desc, icon, icon_map, **overrides):
        """造一条**物品**行（steel/工具这类非建筑物品）。

        ⚠ 必须照抄**同类型原生物品**当模板：物品行的字段很多
        （price/weight/effect_value/remark/stuff_type/stuff_sub_type/
        can_carry_one_by_one_anytime/incineration_result/…），
        少一个都可能在 UI 或搬运逻辑里出问题。
        所以调用方传入 src_id（如铁锭 603001 / 普通工具 403001）作模板。
        """
        tpl = next((r for r in stuff if r.get("stuff_id") == src_id), None)
        if tpl is None:
            raise RuntimeError(f"物品模板 {src_id} 不在官方 stuff 表里")
        r = dict(tpl)
        r.update({
            "stuff_id": sid,
            "stuff_namezh-CN": name,
            "desczh-CN": desc,
            "stuff_img": icon,
            "stuff_img_on_map": icon_map,
            "prefab": "stuff_on_map",     # 通用世界模型（原生物品也这样）
        })
        r.update(overrides)
        return r

    def build_row(sid, model, guide, cellw=3, cellh=3, door_way=1):
        """⚠ `cellw/cellh` 必须与所用 prefab 匹配（用户要求超级生产所改成 2×2）。
        `door_way` 是门位掩码：1 = 只正面开门（官方 2×2 建筑熔炉/酒桶用的就是 1）。"""
        r = dict(src_build)
        r.update({
            "id": sid,
            "cellw": cellw,
            "cellh": cellh,
            "door_way": door_way,
            "res_range": 0,           # 关掉采集营地的野生菜生成——产出全走 DLL，数值精确
            "res_range_anchor": 0,
            "guide_info_zh-CN": guide,
        })
        return r

    def career_row(sid):
        r = dict(src_career)
        r.update({"facility_id": sid, "manpower_limit": 4, "is_main_facility": 0})
        return r

    def super_row(guide):
        """超级生产所：3×2 + 原生下拉窗口（window_blacksmith）。
        ⚠ 窗口必须在生成器里指定，否则每次 --deploy 会被重置回 window_gatherers_hut。"""
        r = build_row(SUPER_ID, SUPER_PREFAB, guide, 3, 2, door_way=1234)
        r["window_prefab"] = SUPER_WINDOW
        return r

    def furnace3_build_row(guide):
        """实验建筑：3×3 + 熔炉机制 + 熔炉窗口。
        必须显式覆盖 `class_name` / `window_prefab`，否则会继承采集营地那一套。"""
        r = build_row(FURNACE3_ID, FURNACE3_PREFAB, guide, FURNACE3_CELL, FURNACE3_CELL)
        r.update({
            "class_name": FURNACE3_CLASS,
            "window_prefab": FURNACE3_WINDOW,
            "have_worker": 1,          # 熔炉是有人工作的
            "must_have_worker": 1,
            "has_bag": "1",            # 熔炉自带仓库（放原料/燃料）
            "res_range": 0,            # 不做采集范围
            "res_range_anchor": 0,
            "menu_group": 3,          # ⚠ 必须与 tech 的 txt_id=3700（制造段）配对
        })
        return r

    def furnace_native_build_row(guide):
        """对照实验：完全照抄原生熔炉的三要素（prefab/class/window 一致），2×2。"""
        r = build_row(FURNACE_NATIVE_ID, FURNACE_NATIVE_PREFAB, guide,
                      FURNACE_NATIVE_CELL, FURNACE_NATIVE_CELL)
        r.update({
            "class_name": FURNACE_NATIVE_CLASS,
            "window_prefab": FURNACE_NATIVE_WINDOW,
            "have_worker": 1,
            "must_have_worker": 1,
            "has_bag": "1",
            "res_range": 0,
            "res_range_anchor": 0,
            "menu_group": 3,
        })
        return r

    def blast3_build_row(guide):
        """3×3 自研高炉：不依赖游戏的熔炉机制（那套要求 2×2 原生骨架）。

        骨架用 `trading_desk`（3×3、无地形限制）；
        冶炼由本 mod 的 `SmelterConsumer` 在换日时结算，
        所以**不需要**给它的 blueprint 写设施配方行。
        """
        r = build_row(BLAST3_ID, BLAST3_PREFAB, guide, BLAST3_CELL, BLAST3_CELL)
        r.update({
            "class_name": BLAST3_CLASS,
            "window_prefab": BLAST3_WINDOW,
            "have_worker": 1,
            "must_have_worker": 0,          # 自研炉不强制工人（换日自动结算）
            "has_bag": "1",                 # 自带仓库：放铁锭与煤
            "res_range": 0,
            "res_range_anchor": 0,
            "menu_group": 3,                # 制造段（与 tech.txt_id=3700 配对）
        })
        return r

    def blast_furnace_build_row(guide):
        """高炉：**原生熔炉骨架**（2×2）+ FacilityFurnace + window_furnace。

        实测结论：熔炉窗口的配方下拉 / 燃料设置依赖 prefab 上的 `FacilityFurnace` 组件，
        借别尺寸或别类型的骨架会导致「能显示但不能交互」（对照实验 105051 vs 105052）。
        """
        r = build_row(BLAST_ID, BLAST_PREFAB, guide, BLAST_CELL, BLAST_CELL)
        r.update({
            "class_name": BLAST_CLASS,
            "window_prefab": BLAST_WINDOW,
            "have_worker": 1,
            "must_have_worker": 1,
            "has_bag": "1",
            "res_range": 0,
            "res_range_anchor": 0,
            "menu_group": 3,          # 制造段（与 tech.txt_id=3700 配对）
        })
        return r

    guide_common = ("安排工人后，每个工人每天稳定产出资源\n"
                    "# 产出内容与数量在 BepInEx/config/claude.facility.cfg 配置\n"
                    "# 工人越多，每日产出越多；产出会自动入库")

    stuff_rows = [
        # 综合生产所：**保持原样**（外观 = 用户选的预设，默认制造台 workbench）
        stuff_row(MOD_ID, NAME, DESC, img, img_on_map, prefab),
        # 超级生产所：自定义外观（prefab 借用 workbench，贴图由 DLL 运行时换成我们自己的）
        stuff_row(SUPER_ID, SUPER_NAME, SUPER_DESC, SUPER_IMG, SUPER_IMG_ON_MAP, SUPER_PREFAB),
        # 实验：3×3 高炉（骨架 trading_desk、机制 FacilityFurnace、窗口 window_furnace）
        stuff_row(FURNACE3_ID, FURNACE3_NAME, FURNACE3_DESC,
                  FURNACE3_IMG, FURNACE3_IMG_ON_MAP, FURNACE3_PREFAB),
        # 对照：原生配置熔炉（骨架 furnace、机制 FacilityFurnace、窗口 window_furnace，2×2）
        stuff_row(FURNACE_NATIVE_ID, FURNACE_NATIVE_NAME, FURNACE_NATIVE_DESC,
                  FURNACE_NATIVE_IMG, FURNACE_NATIVE_IMG_ON_MAP, FURNACE_NATIVE_PREFAB),
        # ---- 工业 mod 内容 ----
        # 新物品：钢（type6/sub603 金属）、钢制工具（type4/sub403 工具）
        # ⚠ 物品的世界模型统一用通用 stuff_on_map（铁锭等原生物品也这样），
        #   所以只需提供图标 sprite（make_industry_icons.py 生成）
        item_row(603001, STEEL_ID, STEEL_NAME, STEEL_DESC,
                 STEEL_IMG, STEEL_IMG_ON_MAP),                     # 模板=铁锭（金属类）
        item_row(403001, STEEL_TOOL_ID, STEEL_TOOL_NAME, STEEL_TOOL_DESC,
                 STEEL_TOOL_IMG, STEEL_TOOL_IMG_ON_MAP,            # 模板=普通工具（工具类）
                 effect_value=STEEL_TOOL_EFFECT, price=STEEL_TOOL_PRICE,
                 remark="效率加成"),
        # 新建筑：高炉（原生熔炉骨架，2×2）
        stuff_row(BLAST_ID, BLAST_NAME, BLAST_DESC,
                  BLAST_IMG, BLAST_IMG_ON_MAP, BLAST_PREFAB),
        # 新建筑：3×3 高炉（自研炉，走本 mod 的冶炼逻辑）
        stuff_row(BLAST3_ID, BLAST3_NAME, BLAST3_DESC,
                  BLAST3_IMG, BLAST3_IMG_ON_MAP, BLAST3_PREFAB),
    ]
    build_rows = [
        build_row(MOD_ID, prefab, guide_common, 3, 3),
        # 超级生产所：3×2（用户要求），door_way=1234 与采集营地同款；窗口换成自带原生下拉的
        super_row(guide_common),
        # 实验建筑：3×3；class/window 用熔炉那一套（要覆盖 build_row 的默认值）
        furnace3_build_row(guide_common),
        furnace_native_build_row(guide_common),
        blast_furnace_build_row(guide_common),
        blast3_build_row(guide_common),
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
        # 实验建筑：放在「制造」段（3700）便于和制造台一起找；免科技
        {"txt_id": 3700, "tech_id": 0, "facility_id": FURNACE3_ID,
         "seed_id": "", "event_id": "",
         "tech_desc_zh-CN": "建造三乘三高炉实验"},
        {"txt_id": 3700, "tech_id": 0, "facility_id": FURNACE_NATIVE_ID,
         "seed_id": "", "event_id": "",
         "tech_desc_zh-CN": "建造熔炉对照实验"},
        {"txt_id": 3700, "tech_id": 0, "facility_id": BLAST_ID,
         "seed_id": "", "event_id": "",
         "tech_desc_zh-CN": "建造高炉：以煤为燃料把铁锭炼成钢"},
        {"txt_id": 3700, "tech_id": 0, "facility_id": BLAST3_ID,
         "seed_id": "", "event_id": "",
         "tech_desc_zh-CN": "建造高炉（3×3）：以煤为燃料冶炼钢材"},
    ]
    career_rows = [career_row(MOD_ID), career_row(SUPER_ID),
                   career_row(FURNACE3_ID), career_row(FURNACE_NATIVE_ID),
                   career_row(BLAST_ID), career_row(BLAST3_ID)]

    blueprint_rows = (make_blueprints(bp_table, stuff_type_by_id, MOD_ID, products_for(MOD_ID)) +
                      make_blueprints(bp_table, stuff_type_by_id, SUPER_ID, products_for(SUPER_ID)))

    # ---- 工业 mod 配方 ----
    def recipe_row(facility_id, spec, slot):
        """按配方规格造一行 blueprint。

        ⚠ blueprint 行同时承担两个职责：
          1. **产品数据键**（GetProductDataKeyList 查它，缺行会让窗口 SetInfo 炸断）
          2. **配方定义**（_material_N/_nN 是消耗材料、product_id/output_count 是产出）
        所以新物品（钢/钢制工具）**必须有**至少一行 blueprint，否则它做出来也没处记账。
        """
        pid = spec["product_id"]
        # 模板优先用**同产品**的官方行；没有就用该设施已有的行；再退回石料行
        tpl = None
        for r in bp_table:
            if r.get("product_id") == pid:
                tpl = r
                break
        if tpl is None:
            tpl = next((r for r in bp_table if r.get("facility_id") == facility_id), None)
        if tpl is None:
            tpl = next(r for r in bp_table if r.get("product_id") == 605001)

        row = dict(tpl)
        row["formula_id"] = pid * 100 + slot
        row["facility_id"] = facility_id
        row["product_id"] = pid
        row["output_count"] = spec["output_count"]
        row["days"] = spec["days"]
        row["disable"] = 0
        row["need_research"] = 0
        # 材料（最多 3 组，游戏表结构如此）
        mats = list(spec["materials"])[:3]
        for i in (1, 2, 3):
            if i <= len(mats):
                row[f"_material_{i}"] = mats[i - 1][0]
                row[f"_n{i}"] = mats[i - 1][1]
            else:
                row[f"_material_{i}"] = ""
                row[f"_n{i}"] = ""
        return row

    # 高炉：铁锭 → 钢
    blueprint_rows.append(recipe_row(BLAST_ID, BLAST_RECIPE, 1))
    # 制造台：原木 + 钢 → 钢制工具
    #   ⚠ 写的是**原版制造台**(105010) 的表 —— 跨 mod 写同一张表，有冲突风险（用户已确认接受）
    blueprint_rows.append(recipe_row(WORKBENCH_ID, STEEL_TOOL_RECIPE, 1))
    print(f"  工业配方: 高炉炼钢 + 制造台造钢制工具")

    # 熔炉系建筑：克隆**原生熔炉(105028) 的全部配方**给它自己。
    # 原因：配方 UI 按 facility_id 取行，新建筑不会自动继承 → 实测窗口里配方下拉是空的。
    # ⚠ 只**新增**属于我们 id 的行，**不动**原生 105028 的行（跨 mod 改原版表有冲突风险）。
    used_fids = {r.get("formula_id") for r in blueprint_rows}
    used_fids |= {r.get("formula_id") for r in bp_table}
    for fid_own in (FURNACE_NATIVE_ID, FURNACE3_ID):
        cloned = clone_facility_blueprints(bp_table, 105028, fid_own, used_fids)
        blueprint_rows += cloned
        print(f"  熔炉配方克隆 → {fid_own}: {len(cloned)} 条（源 105028）")

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
