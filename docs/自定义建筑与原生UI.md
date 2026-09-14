# 自定义建筑 + 原生 UI 框架（Facility mod）

> 这份文档解决的核心问题：**如何给新建筑用上游戏原生的 UI 控件，而不是自绘一套"看起来像"的。**
> 结论是 2026-09-14 通过受控实验定下来的，前面走过三轮弯路，都记在最后一节。

---

## 一、核心结论（一句话）

> **建筑的窗口外观由 `build.json` 的 `window_prefab` 决定，和 `class_name` 可以不一致。**
> 把 `window_prefab` 指向一个**自带原生控件**的窗口预制体，就能直接拿到游戏的原生 UI；
> 数值/选项由我们自己的 DLL 填进去。

实测证据（`window_prefab` 从 `window_gatherers_hut` 换成 `window_blacksmith`）：
- 游戏**不崩溃**，进档正常
- 窗口变成**原生样式**，且自带原生下拉 `dp_blueprint`（`TMP_Dropdown`）
- 只是数值是制造台的默认值 —— 因为游戏按制造台的配方表填的

---

## 二、三步接入法

### 第 1 步：选一个"带你要的控件"的窗口预制体

| 想要的原生控件 | 窗口预制体 | 字段名 | 出处 |
|---|---|---|---|
| **下拉选择**（带图标） | 见下方说明 | `dp_blueprint` | `WindowWorkshop`（制造台） |
| **下拉选择**（图标式） | 作物田/牧场等 | `drop_down`（`StuffIconDropdown`） | `WindowCropField` 等 |
| **数值 ±** | 多数窗口 | `num_adjust` | 通用 |
| **今年/去年产量** | 多数窗口 | `txt_this_year` / `txt_last_year` | `product_record` 下 |

> `window_blacksmith` 的 `dp_blueprint` 是**标准 `TMP_Dropdown`**，
> 层级为 `ItemIcon / Label / Arrow / Template{Viewport{Content{Item}}}`（UnityExplorer 快照确认），
> 尺寸 420×32、anchor/pivot 都是 0.5。**标准层级意味着可以直接用标准 API 操作。**

### 第 2 步：build.json 换窗口 + DLL 填数据

```jsonc
// Defs/build.json —— 建筑 105050 时
{
  "id": 105050,
  "class_name": "FacilityGatherersHut",      // 决定 3D 模型组件，不必等于窗口类型
  "window_prefab": "window_blacksmith",       // ★ 换成自带原生下拉的窗口
  "cellw": 3, "cellh": 2
}
```

DLL 侧（见 `NativeUi.cs` / `NativeDropdown.cs`）：
```csharp
// ① 按字段名取控件（通用助手，任何窗口都能用）
var dd = NativeUi.Find<TMP_Dropdown>(window, "dp_blueprint");

// ② 用标准 API 填我们的数据
var opts = new Il2CppSystem.Collections.Generic.List<TMP_Dropdown.OptionData>();
foreach (var label in labels) opts.Add(new TMP_Dropdown.OptionData(label));
dd.ClearOptions();
dd.AddOptions(opts);              // ⚠ IL2CPP 不生成 AddOptions(List<string>) 重载
dd.SetValueWithoutNotify(sel);
dd.RefreshShownValue();

// ③ 接回调写配置
dd.onValueChanged.AddListener(
    (UnityEngine.Events.UnityAction<int>)(idx => Plugin.SetExtraProduct(idOf(idx))));
```

### 第 3 步：隐藏该窗口里对本建筑无意义的控件

```csharp
NativeUi.HideAll(window,
    "formula_item_main", "formula_item_alternative",   // 制造台配方
    "auto_make_product_of_materials", "material_settings",
    "material_settings_grid", "tmp_product",
    "res_grid", "my_progress_make",
    "num_adjust_of_materials_access_range");            // 材料取用范围
```

---

## 三、IL2CPP 特有的三个坑（都踩过）

| 坑 | 现象 | 解法 |
|---|---|---|
| **lambda 不能隐式转委托** | `CS1660: 无法将 lambda 表达式转换为 UnityAction<int>` | 显式 `new`：`(UnityAction<int>)(idx => …)` |
| **`AddOptions(List<string>)` 不存在** | `CS1503: 无法从 List<string> 转换` | 自己构造 `List<TMP_Dropdown.OptionData>` |
| **控件提取顺序** | 原生下拉填不上，日志一行都没有 | `Apply()` 里**先处理原生下拉**，再处理只存在于某些窗口的参照控件（如 `txt_forest_coverage_rate`，`window_blacksmith` 里没有，会把后面的代码全 `return` 掉） |

---

## 四、无人值守测试钩子（热键）

| 键 | 作用 | 说明 |
|---|---|---|
| **F8** | 操作原生下拉（读值 → 切下一项 → 校验 cfg 被写入） | 不依赖 OS 鼠标标定 |
| **F10** | 打开本 mod 建筑的窗口（调 `Facility.ShowWindow()`） | OS 点击标定太脆（分辨率会变），直接调游戏方法最稳 |

实测日志形态（可作为回归断言）：
```
[FacilityUI] F10 打开窗口尝试：guid=27249342 sid=105050
[FacilityUI] 原生下拉已填充 4 项，当前=2（dp_blueprint）
[FacilityUI] F10 调用 ShowWindow() 成功
[FacilityUI] 原生下拉：共 4 项，当前 2，切到 3
[FacilityUI] 原生下拉选中索引 3 → 额外产品 612001
cfg: 额外产品 = 612001
```

---

## 五、走过的弯路（别再走）

1. **自绘选择条 + 自建列表**（三轮样式调整）
   用户反复反馈「不像游戏内 UI / 长度超出」。根因不是样式没调好，而是**控件根本不在窗口预制体里**。
   → 教训：先确认"这个控件游戏自己有没有"，再去调样式。
2. **`sizeDelta` 当成宽度改**：锚点是从别的控件抄来的、会横向拉伸时，
   `sizeDelta.x` 只是「相对父容器的增量」，**改多少都没用**。
   → 宽度要用 `rect.width`（真实像素宽）。
3. **运行时新增 GameObject 改样式**：为了做"金色描边"加了个 Frame 子物体 + `SetAsFirstSibling`，
   **游戏进档后静默退出**（连 `BepInEx/ErrorLog.log` 都没写）。
   → 规矩：样式只用「改颜色/改尺寸」，不要运行时增删物体或改层级。
4. **挂游戏业务方法做重活**：挂了 `Facility.DoAfterMoveFacility` 的 Postfix，
   直接 `AccessViolationException` 崩游戏。
   → 规矩：跨帧对象操作只在主线程循环（`FacilityComponent.Update`）里做。

---

## 六、下一步可做（框架化）

- [ ] 把「控件映射表」做成 JSON（`Defs/ui_mapping.json`），
      新建筑只写「用哪个窗口、填哪个控件、隐藏哪些」，不必改 DLL
- [ ] 把 `NativeUi` 的候选来源从 `Plugin.ParseExtraCandidates()` 抽象成接口，
      支持每座建筑独立配置
- [ ] 清理 `FacilityWindowUi` 里已废弃的自绘路径（`EnsureSelector` / `SyncListPosition` 等）
