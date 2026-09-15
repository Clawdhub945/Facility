using System.Collections.Generic;

namespace FacilityMod;

/// <summary>
/// **通用 UI 模板的数据模型** —— 建筑只描述"要显示什么"，模板负责怎么画。
///
/// ## 设计目标（用户要求）
/// 「无论是 2×2、3×3 还是 N×N，UI 来一个全新模板，可以被后续多个建筑来自定义 UI 里的内容」
/// → 所以这里**只描述内容**，不含任何布局/尺寸/坐标；
///   布局由 <see cref="UiTemplate"/> 统一负责（任意占地尺寸都适用）。
///
/// ## 加一座新建筑的 UI 要做什么
/// 在它的 <see cref="BuildingSpec.Ui"/> 里写一份 <see cref="UiLayout"/>，例如：
/// <code>
/// Ui = new UiLayout {
///     Rows = {
///         new UiLabelRow  { Label = "配方", Text = "铁锭×2 + 煤×1 → 钢×1" },
///         new UiItemRow   { Label = "库存", Source = UiItemSource.BuildingBag },
///         new UiStatusRow { Label = "状态", Source = UiStatusSource.SmelterBlockReason,
///                           OkText = "运行中", FailText = "未开工" },
///         new UiProgressRow { Label = "燃料余量", Source = UiProgressSource.FuelRatio },
///     }
/// }
/// </code>
/// **不需要改任何 UI 代码** —— 这是这套模板存在的意义。
/// </summary>
internal sealed class UiLayout
{
    /// <summary>窗口标题（留空则用建筑名）</summary>
    internal string Title { get; init; } = "";

    /// <summary>按顺序渲染的行</summary>
    internal List<UiRow> Rows { get; init; } = new();
}

/// <summary>一行的基类：只有"标题"是共通的</summary>
internal abstract class UiRow
{
    /// <summary>行标题（留空 = 不显示标题，内容占满整行）</summary>
    internal string Label { get; init; } = "";

    /// <summary>本行渲染后的高度（由模板按行类型决定，这里给默认值）</summary>
    internal virtual float Height => 22f;
}

/// <summary>标签行：一行纯文字（配方说明、标题、备注…）</summary>
internal sealed class UiLabelRow : UiRow
{
    /// <summary>要显示的文字</summary>
    internal string Text { get; init; } = "";

    /// <summary>文字是否用"次要"颜色（适合备注）</summary>
    internal bool Secondary { get; init; }

    internal override float Height => 22f;
}

/// <summary>物品行的数据来源</summary>
internal enum UiItemSource
{
    /// <summary>建筑自己的仓库（`Facility.bag`）里现有的物品</summary>
    BuildingBag = 0,

    /// <summary>某份冶炼配方的**需求**（输入材料 + 燃料），用于显示"需要什么"</summary>
    RecipeRequirement = 1,
}

/// <summary>
/// 物品行：**图标 + 数量** 的列表（横向排列，自动换行）。
/// 工业 UI 最核心的一行 —— 显示库存、显示配方材料都靠它。
/// </summary>
internal sealed class UiItemRow : UiRow
{
    internal UiItemSource Source { get; init; } = UiItemSource.BuildingBag;

    /// <summary>最多显示几个（超出的用 "+N" 提示）</summary>
    internal int MaxItems { get; init; } = 12;

    /// <summary>
    /// **不显示**的物品 id（例如配方行里不想重复列出燃料）。
    /// </summary>
    internal int[] ExcludeIds { get; init; } = System.Array.Empty<int>();

    internal override float Height => 62f;   // 标题 + 一行图标
}

/// <summary>状态行的数据来源</summary>
internal enum UiStatusSource
{
    /// <summary>`SmelterConsumer.BlockReason(guid)` —— 空串表示正常</summary>
    SmelterBlockReason = 0,

    /// <summary>固定文字（少见，便于调试）</summary>
    Static = 1,
}

/// <summary>
/// 状态行：一行状态文字，**用颜色区分正常/异常**（绿 / 红）。
/// 用于"运行中 / 缺料 / 缺燃料"这类反馈。
/// </summary>
internal sealed class UiStatusRow : UiRow
{
    internal UiStatusSource Source { get; init; } = UiStatusSource.SmelterBlockReason;

    /// <summary>正常时显示的文字</summary>
    internal string OkText { get; init; } = "正常";

    /// <summary>异常时的前缀（实际文字 = 前缀 + 具体原因）</summary>
    internal string FailText { get; init; } = "未开工";

    /// <summary>Source = Static 时显示的文字</summary>
    internal string StaticText { get; init; } = "";

    internal override float Height => 22f;
}

/// <summary>进度行的数据来源</summary>
internal enum UiProgressSource
{
    /// <summary>不需要进度条（配合 Static 用固定比例，调试用）</summary>
    Static = 0,

    /// <summary>燃料余量 / 每日消耗（0~1，封顶 1）</summary>
    FuelRatio = 1,

    /// <summary>今日是否已开工（1 = 已开工，0 = 未开工）</summary>
    WorkedToday = 2,
}

/// <summary>进度行：自绘进度条（游戏没有 VerticalLayoutGroup，位置由模板算）</summary>
internal sealed class UiProgressRow : UiRow
{
    internal UiProgressSource Source { get; init; } = UiProgressSource.Static;

    /// <summary>Source = Static 时的固定比例（0~1）</summary>
    internal float StaticRatio { get; init; }

    /// <summary>进度条右侧的附加文字（如 "50/50"）</summary>
    internal bool ShowValueText { get; init; } = true;

    internal override float Height => 24f;
}
