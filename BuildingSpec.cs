using System;
using System.Collections.Generic;

namespace FacilityMod;

/// <summary>
/// 一座自定义建筑的**完整独立规格**。
///
/// ## 为什么要有这个（架构重构的原因）
/// 早期版本把三座建筑的行为混在几条共用路径里：
///   * 窗口补丁一张大网网住所有窗口类 → 改下拉框，高炉也跟着变
///   * `FacilityWindowUi.Apply()` 一套逻辑处理所有建筑 → 隐藏名单互相污染
///   * 产出循环用全局判据筛建筑 → 改「高炉不产木石」，别的建筑也停摆
///
/// 用户明确要求：**每种建筑必须独立存在**。于是把「一座建筑是什么、要做什么」
/// 全部收进这个规格对象，所有代码路径都**按规格分派**，不再有跨建筑共享的行为开关。
///
/// ## 加一座新建筑要改哪里
/// 1. 在 <see cref="Buildings"/> 里加一条规格
/// 2. `_tools/make_defs.py` 里加对应的 Def 表行
/// 除此之外**不需要动任何逻辑代码** —— 这是这套规格存在的意义。
/// </summary>
internal sealed class BuildingSpec
{
    /// <summary>设施 id（与 Defs 表一致）</summary>
    internal int StuffId { get; init; }

    /// <summary>显示名（日志用）</summary>
    internal string Name { get; init; } = "";

    /// <summary>这个建筑的窗口预制体名（`window_gatherers_hut` / `window_blacksmith` / `window_furnace` …）</summary>
    internal string WindowPrefab { get; init; } = "";

    /// <summary>
    /// 是否参与本 mod 的「每日产出」逻辑（工人数 × cfg 产出表）。
    /// **只有日常产出型建筑才为 true**；熔炉那种走游戏自己机制的不参与。
    /// </summary>
    internal bool DailyProducer { get; init; }

    /// <summary>
    /// 窗布里要**隐藏**的控件名（**只对这座建筑的窗口生效**）。
    /// ⚠ 绝不能放跨窗口重名的通用名（`icon_num` / `res_grid` / `my_progress_make` …）——
    /// 也别放 `fomula_item_main`（原生下拉的父容器，隐藏它下拉就没了）。
    /// </summary>
    internal string[] HideControls { get; init; } = Array.Empty<string>();

    /// <summary>是否往窗口的**原生下拉**里填「额外产品」候选（只有带 `dp_blueprint` 的窗口才需要）</summary>
    internal bool FillExtraProductDropdown { get; init; }

    /// <summary>是否改写窗口文案（标题/说明/森林覆盖率等）</summary>
    internal bool RewriteWindowTexts { get; init; } = true;

    /// <summary>
    /// **自研冶炼规格**（null = 这座建筑不做冶炼）。
    /// 见 <see cref="SmelterConsumer"/>：每游戏日检查原料与燃料，够就消耗并产出。
    /// 用途：3×3 高炉这种「原生机制做不出」的建筑。
    /// </summary>
    internal SmeltingRecipe? Smelting { get; init; }

    /// <summary>自定义外观（贴图前缀）；null = 用原版外观</summary>
    internal string? CustomSpritePrefix { get; init; }
}

/// <summary>本 mod 全部建筑的规格表（**唯一事实来源**）</summary>
internal static class Buildings
{
    /// <summary>综合生产所：日常产出型，原生窗口，不改外观</summary>
    internal static readonly BuildingSpec Producer = new()
    {
        StuffId = Plugin.FacilityId,                 // 105040
        Name = "综合生产所",
        WindowPrefab = "window_gatherers_hut",
        DailyProducer = true,
        FillExtraProductDropdown = false,            // 该窗口没有原生下拉
        HideControls = Array.Empty<string>(),        // 采集营地窗口本来就是我们要的样子
        RewriteWindowTexts = true,
    };

    /// <summary>超级生产所：日常产出型 + 自定义外观 + 借 window_blacksmith 拿原生下拉</summary>
    internal static readonly BuildingSpec SuperProducer = new()
    {
        StuffId = Plugin.SuperFacilityId,            // 105050
        Name = "超级生产所",
        WindowPrefab = "window_blacksmith",
        DailyProducer = true,
        FillExtraProductDropdown = true,
        // ⚠ 只隐藏「工坊配方」专有的东西；**不能有** fomula_item_main（下拉父容器）
        HideControls = new[]
        {
            "formula_item_alternative", "fomula_item_alternative",
            "auto_make_product_of_materials",
            "material_settings", "material_settings_grid", "material_settings_panel",
            "tmp_product", "btn_add_alternative",
            "icon_num", "icon_num_1", "icon_num_2", "icon_num_3", "icon_num_4", "icon_num_5",
        },
        RewriteWindowTexts = true,
        CustomSpritePrefix = "super_factory",
    };

    /// <summary>
    /// 三乘三高炉实验：走**游戏自己的熔炉机制**（燃料 + 生产计划），
    /// 所以**不参与**本 mod 的每日产出；窗口**一个控件都不隐藏**、不改文案。
    /// </summary>
    internal static readonly BuildingSpec Furnace3Test = new()
    {
        StuffId = Plugin.Furnace3TestId,             // 105051
        Name = "三乘三高炉实验",
        WindowPrefab = "window_furnace",
        DailyProducer = false,                       // ← 关键：不参与本 mod 产出
        FillExtraProductDropdown = false,
        HideControls = Array.Empty<string>(),        // ← 关键：熔炉窗口一个都不隐藏
        RewriteWindowTexts = false,                  // ← 关键：不碰它的文案
        CustomSpritePrefix = null,
    };

    /// <summary>
    /// 熔炉对照实验 105052：**完全照抄原生熔炉的三要素**
    /// （prefab=urnace、class=FacilityFurnace、window=window_furnace、2×2）。
    ///
    /// 用途：与 105051（3×3 借 	rading_desk 骨架）做**严格对照** ——
    /// 两者类与窗口相同、只有骨架 prefab 不同，用来定位
    /// 「窗口能显示但控件不能交互」是不是「prefab 组件与 class_name 不一致」造成的。
    ///
    /// 与熔炉同理：走游戏自己的机制，**不参与**本 mod 产出、**不碰**窗口。
    /// </summary>
    internal static readonly BuildingSpec FurnaceNativeTest = new()
    {
        StuffId = Plugin.FurnaceNativeTestId,        // 105052
        Name = "熔炉对照实验",
        WindowPrefab = "window_furnace",
        DailyProducer = false,
        FillExtraProductDropdown = false,
        HideControls = Array.Empty<string>(),
        RewriteWindowTexts = false,
        CustomSpritePrefix = null,
    };

    /// <summary>
    /// 高炉 105053（工业 mod 第一座建筑）：**原生熔炉骨架**（`furnace`，2×2）
    /// + `FacilityFurnace` + `window_furnace`。
    ///
    /// 与熔炉同理：走游戏自己的**燃料 + 生产计划**机制，
    /// **不参与**本 mod 的每日产出、**不碰**窗口
    /// （配方下拉与「设置燃料类型」都是原生的 —— 实测只要骨架正确它们就能用）。
    ///
    /// 配方（铁锭×2 → 钢×1）写在 `blueprint.json` 的 `facility_id=105053`；
    /// 燃料用煤（煤的 remark=「燃料系数」、effect_value=5.0）。
    /// </summary>
    internal static readonly BuildingSpec BlastFurnace = new()
    {
        StuffId = Plugin.BlastFurnaceId,             // 105053
        Name = "高炉",
        WindowPrefab = "window_furnace",
        DailyProducer = false,
        FillExtraProductDropdown = false,
        HideControls = Array.Empty<string>(),
        RewriteWindowTexts = false,
        CustomSpritePrefix = null,
    };

    /// <summary>
    /// **3×3 高炉 105054（自研炉）** —— 工业 mod 的主力建筑。
    ///
    /// ## 为什么不复用游戏熔炉机制
    /// `window_furnace` 的控件依赖 prefab 上的 `FacilityFurnace` 组件，
    /// 而原生熔炉骨架是 **2×2**（对照实验结论：借 3×3 骨架配 `FacilityFurnace`
    /// 会导致「窗口能显示但控件不可交互」）。用户要求 3×3，
    /// 所以燃料 + 配方 + 产出由本 mod 的 <see cref="SmelterConsumer"/> 自己实现。
    ///
    /// ## 工作方式
    /// 骨架用 `trading_desk`（3×3、无地形限制、自带仓库），窗口借用
    /// `window_gatherers_hut`（有工人/存储界面）。玩家把**铁锭与煤放进仓库**，
    /// 每游戏日由 `SmelterConsumer` 结算一次：够料就扣除并产出钢。
    /// </summary>
    internal static readonly BuildingSpec BlastFurnace3 = new()
    {
        StuffId = Plugin.BlastFurnace3Id,            // 105054
        Name = "高炉(3×3)",
        WindowPrefab = "window_gatherers_hut",
        DailyProducer = false,                       // 不做「工人×产出表」那套
        FillExtraProductDropdown = false,
        HideControls = Array.Empty<string>(),
        // ⚠ 不改窗口文案：我们借的是采集营地窗口，它的文案改写逻辑是针对
        //   「工人每日产出」写的；高炉是冶炼，写上去会词不达意。
        //   （真正该显示的是「配方/燃料/进度」，那需要专门做 UI，后续再说）
        RewriteWindowTexts = false,
        CustomSpritePrefix = null,
        // ★ 自研冶炼规格：铁锭×2 + 煤×1 → 钢×1（每游戏日一炉）
        Smelting = new SmeltingRecipe
        {
            Inputs = new System.Collections.Generic.List<(int id, int n)> { (603001, 2) },
            FuelId = 601001,          // 煤
            FuelPerBatch = 1,
            OutputId = 603010,        // 钢
            OutputCount = 1,
        },
    };

    internal static readonly BuildingSpec[] All =
        { Producer, SuperProducer, Furnace3Test, FurnaceNativeTest, BlastFurnace, BlastFurnace3 };

    /// <summary>本 mod 全部设施 id</summary>
    internal static int[] AllIds()
    {
        var ids = new int[All.Length];
        for (int i = 0; i < All.Length; i++) ids[i] = All[i].StuffId;
        return ids;
    }

    /// <summary>按设施 id 取规格（找不到返回 null）</summary>
    internal static BuildingSpec? ByStuffId(int stuffId)
    {
        foreach (var b in All)
            if (b.StuffId == stuffId) return b;
        return null;
    }

    /// <summary>
    /// 按**窗口预制体名**取规格。
    /// 用途：窗口事件里只知道 GameObject 名（`window_furnace`），据此找到归属建筑。
    /// ⚠ 多座建筑**可以共用同一个窗口预制体**（例如将来两座都借 `window_blacksmith`）——
    /// 那种情况返回第一个；调用方应再结合 `facility_guid` 精确判定。
    /// </summary>
    internal static BuildingSpec? ByWindowPrefab(string windowName)
    {
        if (string.IsNullOrEmpty(windowName)) return null;
        foreach (var b in All)
            if (b.WindowPrefab == windowName) return b;
        return null;
    }
}
