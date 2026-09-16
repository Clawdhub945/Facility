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

## 五、[重要] 运行时 uGUI 的两个必须条件

这两条是反复失败后测出来的，缺一个就看不到面板：

### 1. 必须挂独立 Canvas + overrideSorting
ar cv = root.AddComponent<Canvas>(); cv.overrideSorting = true; cv.sortingOrder = 30000;
外加 GraphicRaycaster。

**原因**：游戏的 UI 是**自绘批渲染**（atch_sprite_renderer_*）画的，
我们的面板是标准 uGUI —— **两套渲染体系**。
只改同级顺序（SetAsLastSibling）**压不住**游戏自绘 UI。
实测：面板物体确实挂在窗口下（UnityExplorer 能看到 acility_ui_template），
但屏幕上被游戏 UI 完全盖住；加独立 Canvas 后才显示。

### 2. 不能用拉伸写法，必须给明确尺寸
* 无效：nchorMin=0 / anchorMax=1 → 窗口根 
ect 是 **550×0**，拉伸子物体高度为 0
* 可行：**中心锚点 + 明确 sizeDelta**（nchorMin=anchorMax=(0.5,0.5)，sizeDelta=(260,150)）

**原因**：窗口根 
ect 是 550×0（它只是容器，实际尺寸由自绘系统决定），
游戏自己的子控件也全是 550×0 —— **不存在窗口可视矩形**可当参照，
这就是用锚点推算位置全部失败的根本原因。


### ⭐⭐⭐ 4. 根因：窗口根的 rect 是 550x0（一切定位失败的源头）

实测证据（`DumpCoordSys` + 标定日志）：
```
window_gatherers_hut  pos=(0,0)  rect=550x0      <- 高度 0！
title_area / content_area / product_record / tips_root  size=550x0
标定红块...尺寸 260x0                            <- 连红块也变成 0 高
```

**后果**：
* 任何 `anchorMin=0 / anchorMax=1` 的**拉伸**子物体 -> **高度 0** -> 看不见
* 任何拿"父 rect"推算尺寸的写法 -> 也是 0
* 游戏自己的 UI 元素同样是 550x0（它们是**自绘批渲染**画的，不依赖 uGUI 布局）
  -> **不存在"窗口可视矩形"可以当参照**，这解释了为什么"用锚点推算位置"全部失败

**唯一正确的做法**：
1. 面板根：**中心锚点 + 明确 sizeDelta**（`anchorMin=anchorMax=(0.5,0.5)`、`sizeDelta=(260,150)`）
2. 子物体：可以安全地用拉伸填满**已给明确尺寸的父物体**
3. 再加**独立 Canvas（overrideSorting）** 让 uGUI 画在游戏自绘 UI 之上

**anchoredPosition 与 屏幕像素 的实测对应**（用于标定）：
```
(0,   0)   -> 屏幕 (0, 900)   = 左下角
(400,-400) -> 屏幕 (4, 904)
-> 屏幕x 约 0.9 x X ；屏幕y 约 900 + 0.1 x Y（Y 系数很小，所以要大幅调整）
```



### ⭐⭐⭐ 5. 【最终结论】游戏窗口的 uGUI 布局**不运行** -> 标准 uGUI 量不出尺寸

UnityExplorer 实测（用户提供）：
```
facility_ui_template
  RectTransform.sizeDelta      = 260, 150      <- 设置是生效的
  RectTransform.rect           = -130, 0, 260, 0   <- 但实际高度 0
  RectTransform.offsetMin     = 0, -267
  RectTransform.offsetMax     = 260, -267       <- 上下偏移相同 -> 高度算成 0
```
而且用户实测：点 `Inspect` 重新打开后**数值恢复原值**、点 `Apply` **没有任何反应**
-> **`rect` 不是实时值，而是布局系统在布局时写入的缓存**；
   这个窗口的 uGUI **布局从不运行**（游戏用自绘批渲染画 UI），
   所以 `rect` 永远是初始的 0。

**含义**：
* 用 `rect` 判断尺寸/位置**不可靠**（读到的永远是 0）
* 用 `offsetMin/offsetMax`（拉伸）算尺寸**也不可靠**（父的 rect 是 0）
* 标准 uGUI 在这个窗口里**没有可用的布局信息**

**可行的两条路**：
| 路 | 做法 | 代价 |
|---|---|---|
| **A. 用游戏自绘系统** | 用 `MySpriteRenderer`（游戏画 UI 用的就是它）创建面板/图标 | 需要逆向它的用法，工作量大但最"原生" |
| **B. 复用现有控件** | 不改布局：改写窗口里已有的文本（`txt_*`）、往已有的物品格里填图标（`icon_num*`）、改已有进度/数值控件 | 零风险、立即可用；布局自由度低 |

**推荐 B 打底 + A 逐步替换**：先用 B 让内容立刻可见，再逐步用 A 做自定义布局。

### 6. 定位用 cfg 标定（热生效）


`ini
[UI模板]
横偏移 = 0     # 正数 = 向右
纵偏移 = 0     # 正数 = 向下
`
UiProbe 画的**红块与面板同参数** —— 看到红块在哪，面板就在哪。

---

## 六、坐标换算（已测得）
`
Canvas: ui_canvas  ScreenSpaceCamera  scaleFactor=0.9   rect=1600x1000
  → UI 坐标 × 0.9 = 屏幕像素
  → anchor(0,0)+(0,0) = 屏幕左下角
换算：aPos ≈ (X / 0.9, -(Screen.height - Y) / 0.9)
`

---

## 七、踩过的坑

| 坑 | 现象 | 解法 |
|---|---|---|
| 窗口 `rect.height` 是布局前的初始值 | 日志出现「面板 526×**-96**」（负高度） | 取下限保护 + `Mathf.Abs` 兜底 |
| 热键 F12 收不到 | `keybd_event` 发 F12 游戏无反应（F10/F8 正常） | 改成"详细模式下开窗自动跑一次"，不依赖热键 |
| 诊断日志每帧刷 | 窗口开着时 `Apply` 每帧调用 | 按窗口实例 ID 去重（`_diagDone`） |
| 面板不挡点击 | 窗口底层的东西会被点到 | 底板 `Image.raycastTarget = true` |
| 数量文字看不清 | 叠在图标上 | 加 `Shadow` 描边 |

---

## 八、给新建筑配 UI 的步骤

```csharp
// BuildingSpec.cs 里那条规格加一个 Ui
Ui = new UiLayout {
    Rows = { /* 按需组合上面四种行 */ }
}
```
**不需要改任何 UI 代码** —— 这是这套模板存在的意义。

需要新的"行类型"时（例如数值调节、勾选、按钮），
在 `UiLayout.cs` 加一个 `UiRow` 子类，在 `UiTemplate.RenderRow` 里加一个 `case`。
