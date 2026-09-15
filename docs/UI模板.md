# 通用 UI 模板（任意尺寸建筑可复用）

> 用户要求：「无论是 2×2、3×3，还是 N×N，UI 来一个全新模板，
> 可以被后续多个建筑来自定义 UI 里的内容」
>
> 本文记录**可行性验证结论 + 模板设计 + 使用方式 + 踩过的坑**。

---

## 一、可行性验证（必须先做的那一步）

**问题**：早前为了给下拉框做"金色描边"，在运行时**新增了一个 GameObject**
并调 `SetAsFirstSibling`，结果**游戏进档后静默退出**
（连 `BepInEx/ErrorLog.log` 都没写）。那次加的是带 `SpriteRenderer` 的物体。

**所以做模板前必须先验证：纯 uGUI 能不能在运行时构建。**

`UiProbe.cs` 做了这个最小实验（详细模式下开窗自动跑一次）：
```
UI 实验 ① 新建 GameObject 成功
UI 实验 ② SetParent 成功
UI 实验 ③ RectTransform 配置成功
UI 实验 ④ Image 底板成功
UI 实验 ⑤ TextMeshProUGUI 成功
```
实测**游戏稳定**（构建后持续运行 24 秒以上无异常）。

### 结论
| 操作 | 可行性 |
|---|---|
| `new GameObject` + `SetParent` 到窗口 | ✅ 可行 |
| `RectTransform` + 手工定位 | ✅ 可行 |
| `Image`（纯色底板 / 进度条） | ✅ 可行 |
| `TextMeshProUGUI` | ✅ 可行 |
| `ScrollRect` + `Mask` | ✅ 可行 |
| **带 `SpriteRenderer` 的物体** | ❌ **不要做**（曾导致游戏静默退出） |

---

## 二、游戏 UI 控件调研（决定模板能提供哪些"行"）

### 有的控件
| 类别 | 控件 |
|---|---|
| 文本 | `TextMeshProUGUI`、`IconText`、`MagicIconText` |
| 图标+文字 | `IconTextNum`、`IconName`、`IconTextTextWithCheck` |
| 物品显示 | `StuffIconNum`、`IconNum`、`IconNameNum` |
| 数值调整 | `NumAdjust`（带 ▲▼） |
| 勾选 | `MyCheckBox`、`MyCheckBoxGroup` |
| 下拉 | `MyDropdown`、`TMP_Dropdown`、`StuffIconDropdown` |
| 进度 | `MyProgressBar`、`MyProgressBarOnMap` |
| 列表/网格 | `GridLayoutGroup`、`ScrollRect`、`Mask`、`LayoutElement` |
| 按钮 | `Button` |

### **没有**的控件（影响模板布局方式）
* `VerticalLayoutGroup` / `HorizontalLayoutGroup` —— **没有**
* `ContentSizeFitter` —— **没有**

→ **所以竖排列表必须自己算坐标**（`anchoredPosition` 自上而下累加）。
模板就是这么做的。

---

## 三、模板设计

### 数据模型（`UiLayout.cs`）—— 建筑只描述"显示什么"
```csharp
Ui = new UiLayout {
    Rows = {
        new UiLabelRow    { Label = "配方", Text = "铁锭 ×2 + 煤 ×1 → 钢 ×1" },
        new UiItemRow     { Label = "配方材料", Source = UiItemSource.RecipeRequirement },
        new UiItemRow     { Label = "库存",     Source = UiItemSource.BuildingBag },
        new UiStatusRow   { Label = "状态",     Source = UiStatusSource.SmelterBlockReason,
                            OkText = "已冶炼（今日）", FailText = "未开工" },
        new UiProgressRow { Label = "燃料余量", Source = UiProgressSource.FuelRatio },
    }
}
```

### 已实现的行类型
| 行类型 | 内容 | 数据来源 |
|---|---|---|
| `UiLabelRow` | 纯文字（可选次要色） | 静态文字 |
| `UiItemRow` | **图标 + 数量**，自动换行 | 建筑仓库 / 配方需求 |
| `UiStatusRow` | 状态文字，**绿=正常 / 红=异常** | `SmelterConsumer.BlockReason` |
| `UiProgressRow` | **自绘进度条** + 百分比 | 燃料余量 / 今日是否开工 / 固定值 |

### 渲染器（`UiTemplate.cs`）
* 面板：深色底（`0.15,0.14,0.13,0.94`）+ 暖白文字 —— 照游戏自己的面板取色
* 结构：`root（ScrollRect）→ viewport（Mask）→ content（自上而下排行）`
* **按状态签名刷新**：每帧被调用，但只有"内容真的变了"才重排
  （签名 = 各行的数据摘要；避免每帧重建 → 性能与闪烁）
* 物品图标：`物品id → D.Ins.stuff_dic → stuff_img（如 ui_603010）→ SpriteManager.Get()`
  *取不到就画灰色占位块，不空白*

### 与占地尺寸无关
布局全部用 `anchoredPosition` 手工计算，不依赖任何"建筑尺寸"参数 ——
所以 2×2 / 3×3 / N×N 共用同一套模板（已在 2×2 与 3×3 两座高炉上同时启用验证）。

---

## 四、踩过的坑

| 坑 | 现象 | 解法 |
|---|---|---|
| 窗口 `rect.height` 是布局前的初始值 | 日志出现「面板 526×**-96**」（负高度） | 取下限保护 + `Mathf.Abs` 兜底 |
| 热键 F12 收不到 | `keybd_event` 发 F12 游戏无反应（F10/F8 正常） | 改成"详细模式下开窗自动跑一次"，不依赖热键 |
| 诊断日志每帧刷 | 窗口开着时 `Apply` 每帧调用 | 按窗口实例 ID 去重（`_diagDone`） |
| 面板不挡点击 | 窗口底层的东西会被点到 | 底板 `Image.raycastTarget = true` |
| 数量文字看不清 | 叠在图标上 | 加 `Shadow` 描边 |

---

## 五、给新建筑配 UI 的步骤

```csharp
// BuildingSpec.cs 里那条规格加一个 Ui
Ui = new UiLayout {
    Rows = { /* 按需组合上面四种行 */ }
}
```
**不需要改任何 UI 代码** —— 这是这套模板存在的意义。

需要新的"行类型"时（例如数值调节、勾选、按钮），
在 `UiLayout.cs` 加一个 `UiRow` 子类，在 `UiTemplate.RenderRow` 里加一个 `case`。
