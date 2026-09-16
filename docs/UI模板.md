# 通用 UI 模板（v2）—— 独立 Canvas + Image 方案

> 用户要求：「无论是 2×2、3×3，还是 N×N，UI 来一个全新模板，
> 可以被后续多个建筑来自定义 UI 里的内容」

---

## 一、最终结论（三次试错的收敛）

### ✅ 正确做法：**独立 `ScreenSpaceOverlay` Canvas + `UnityEngine.UI.Image`**

```csharp
// UiKit.EnsureRoot()
var go = new GameObject("facility_uikit");
go.layer = 5;                                   // UI 层
var canvas = go.AddComponent<Canvas>();
canvas.renderMode = RenderMode.ScreenSpaceOverlay;
canvas.overrideSorting = true;
canvas.sortingOrder = 20000;                    // 压过游戏 UI
go.AddComponent<GraphicRaycaster>();

// 控件：Image（面板/色块/图标）+ TextMeshProUGUI（文字）
// 尺寸/位置一律显式给出：anchorMin=anchorMax=(0.5,0.5) + sizeDelta + anchoredPosition
```

**实测结果**（自检日志 + 截图双重确认）：
```
Canvas: rect=1440x900  renderMode=ScreenSpaceOverlay  order=20000  layer=5
面板: size=(420.00, 220.00)  pos=(0.00, 0.00)      <- 尺寸终于正确
图标 ui_603010 / ui_403010 / ui_105054: 全部"贴图有"
色块像素统计: red=1709  green=1858  blue=1615       <- 三色块全部渲染
```
截图可见：深色面板 + 红绿蓝色块 + 中文文字 + 三个游戏图标与标签。

---

## 二、试错过程（**不要再走这两条路**）

### ❌ 路线 1：标准 uGUI 挂在游戏窗口内部
* 窗口根 `window_gatherers_hut` 的 `rect` 是 **550×0**
* 游戏自己的子控件（`title_area`/`content_area`/…）也全是 `550×0`
* 原因：游戏 UI 是**自绘批渲染**（`batch_sprite_renderer_*`）画的，**uGUI 布局从不运行**
* 后果：
  * `rect` 永远是 0（UnityExplorer 里点 Apply 也无反应、重开数值恢复原值）
  * `anchorMin=0/anchorMax=1` 的拉伸子物体 -> 高度 0
  * 拿父 rect 推算尺寸 -> 也是 0
* 现象：物体存在、坐标正确、`activeInHierarchy=true`，**但一个像素都不画**

### ❌ 路线 2：`SpriteRenderer` + `MySpriteRenderer`
* **能画出来**（用户截图确认屏幕上出现了绿块）
* 但**一打开建筑窗口就被盖住** —— 与游戏 UI 不在同一渲染体系
* 附带发现：
  * `MySpriteRenderer` **不是 MonoBehaviour**（`AddComponent` 返回 null、组件列表里永远不出现）
    —— 它是普通 C# 包装对象，内部持有 `render_handler` 参与批渲染
  * 挂上它之后 `sr.enabled` 会变成 `False`（它自己负责画，关掉被包装的渲染器避免重复绘制）
  * `new GameObject()` 默认在 `Default`(0) 层，而游戏 UI 相机的 `cullingMask` 只渲染 `UI`(5) 层
    -> **必须 `go.layer = 5`**，否则被直接剔除
  * 画布父级 `ui_canvas.localScale = 0.01`：写世界 `z=-1` 会变成局部 `z=-100`

### ✅ 路线 3（采纳）：独立 Canvas + Image
由 UnityExplorer 实测游戏自己的 UI 元素组件得出：
```
icon_num
  Components: RectTransform, CanvasRenderer, IconNum, UnityEngine.UI.Image
  Layer: UI
```
-> 游戏 UI 用的是 **`CanvasRenderer` + `Image`**，照抄即可。

---

## 三、控件清单（`UiKit.cs`）

| 方法 | 作用 |
|---|---|
| `EnsureRoot()` | 建独立 Overlay Canvas（`sortingOrder=20000`，layer=5） |
| `AddPanel(parent, name, color, pos, size, anchor?)` | 面板/色块（`Image`） |
| `AddText(parent, name, text, pos, size, fontSize, align, color, anchor?)` | 文字（`TextMeshProUGUI`，支持中文） |
| `AddIcon(parent, name, spriteName, pos, size, anchor?)` | 图标（`Image` + 游戏贴图，如 `ui_603010`） |
| `ResolveSprite(name)` | 从游戏贴图表取精灵（`SpriteManager.Get`） |

**关键约定**：锚点默认 `(0.5,0.5)` = 屏幕中心；`sizeDelta`/`anchoredPosition` **必须显式给出**
—— 不依赖任何父容器布局，这是"能用"的根本原因。

---

## 四、模板渲染器（`UiTemplate.cs`）

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

### 行类型
| 行 | 内容 | 默认高度 |
|---|---|---|
| `UiLabelRow` | 纯文字（可次要色） | 22 |
| `UiItemRow` | **图标 + 数量**（自动换行，放不下提示 +N） | 62 |
| `UiStatusRow` | 状态文字（**绿=正常 / 红=异常**） | 22 |
| `UiProgressRow` | **进度条** + 百分比 | 24 |

### 渲染要点
* 游戏**没有** `VerticalLayoutGroup` / `HorizontalLayoutGroup`（实测）-> 行位置**手工累加**
* 面板高度**自适应内容**（`sizeDelta` 按行高总和算）
* **按状态签名刷新**：每帧被调用，但内容没变不重建（性能 + 防闪烁）
* 行宽固定 360px，**与建筑占地尺寸无关** -> 2×2 / 3×3 / N×N 共用一套模板

---

## 五、给新建筑配 UI

在 `BuildingSpec.cs` 里给那条规格加一个 `Ui` 即可，**不需要改任何 UI 代码**。

需要新"行类型"时：在 `UiLayout.cs` 加一个 `UiRow` 子类，
在 `UiTemplate.RenderRow` 里加一个 `case`。

---

## 六、诊断工具

* `SpriteUiSelfTest` —— 进档后（详细模式开启时）自动跑一次，验证 UiKit 是否正常：
  画面板 + 三色块 + 文字 + 三个游戏图标，日志打 `UI 自检` 前缀
* `UiKit.EnsureRoot()` 的日志会打出 `rect` / `renderMode` / `sortingOrder` / `layer`
* `SpriteUi.cs`（`SpriteRenderer` 路线）保留但**不是主路线**，
  其层/深度/坐标换算的踩坑记录在注释里，供以后参考

---

## 七、坐标换算备忘（若将来需要世界坐标方案）

```
Canvas: ui_canvas  renderMode=ScreenSpaceCamera  scaleFactor=0.9
  canvas rect = 1600×1000   localScale = 0.01
  -> 世界尺寸 = 16×10
  -> anchor(0,0) + anchoredPosition(0,0) = 屏幕左下角
把"屏幕像素点 (X, Y)"换算成 anchoredPosition：aPos ≈ (X/0.9, -(Screen.height-Y)/0.9)
```
