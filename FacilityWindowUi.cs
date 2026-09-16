using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FacilityMod;

/// <summary>
/// 综合生产所的窗口 UI 定制（由 FacilityWindowUiPatch 在窗口 SetInfo/刷新后调用）：
/// 1. 改写「附近森林覆盖率」文案（本建筑产出与森林无关）→ 改为产出说明
/// 2. 在文案下方注入一个**下拉框**选择「额外产品」：点标题条展开候选列表，
///    点某一项即选中并写入 cfg（换日产出时并入每人产出，热生效）。
///
/// 为什么不是 Unity 原生 `Dropdown`：那个控件在本游戏里点不开（控件可见、射线也命中它自己，
/// 就是弹不出列表，游戏疑用自定义输入派发）。所以这里用「标题按钮 + 自建选项面板」自己实现下拉，
/// 并且**每个可点对象都同时挂 Button.onClick 与 EventTrigger(PointerClick)**——
/// 两条输入路径都覆盖到，避免再被游戏的输入派发方式坑一次。
/// </summary>
internal static class FacilityWindowUi
{
    private const string SelectorName = "FacilityExtraProductSelector";
    private const string DropdownListName = "FacilityExtraProductDropdown";

    // ---- 尺寸 ----
    // ⚠ 只改这几个常量是**安全**的；但**不要**在这里新增/删除 GameObject 或改层级
    // （`SetAsFirstSibling` 之类）——踩过坑：加「描边」子物体那版直接让游戏在进档后静默退出，
    // 连 ErrorLog 都没写。样式尽量用「改颜色/改尺寸」这种低风险手段。
    private const float RowHeight = 24f;
    private const float BarHeight = 26f;
    /// <summary>标题条宽度。用户反馈「下拉框超出太长了」——收窄到与窗口内产品行相当。</summary>
    private const float BarWidth = 340f;

    // 文字色：游戏 UI 的暖白（原来用 0.92/0.90/0.86 偏冷）
    private static readonly Color TextColor = new Color(0.92f, 0.88f, 0.78f, 1f);
    // 行底色/当前项高亮：沿用原来已验证可用的值，只微调更贴近游戏面板
    private static readonly Color RowColor = new Color(0.22f, 0.21f, 0.19f, 1f);
    private static readonly Color RowCurColor = new Color(0.34f, 0.31f, 0.24f, 1f);
    private static readonly Color PanelColor = new Color(0.18f, 0.17f, 0.15f, 0.97f);
    private static readonly Color ListColor = new Color(0.13f, 0.12f, 0.11f, 0.99f);

    private static Font? _font;

    /// <summary>当前展开的窗口实例（换窗口时自动收起上一个）。</summary>
    private static GameObject? _openListOwner;

    /// <summary>
    /// 同一帧内的重复点击要去重。
    /// 实测原因：同一个对象上同时挂了 `Button.onClick` 与 `EventTrigger(PointerClick)`，
    /// `ExecuteEvents.Execute(go, …, pointerClickHandler)` 会**两条都派发**，
    /// 于是「展开」被连按两次变成「展开又收起」（日志实证：展开→收起→展开→收起）。
    /// 用「最后一次处理点击的帧号」去重，既修掉程序化派发的双发，也不影响真人点击
    /// （真人两次点击不可能落在同一帧）。
    /// </summary>
    private static int _lastClickFrame = -1;
    private static GameObject? _lastClickTarget;

    private static bool ShouldHandleClick(GameObject target)
    {
        int frame = Time.frameCount;
        if (frame == _lastClickFrame && target == _lastClickTarget) return false;
        _lastClickFrame = frame;
        _lastClickTarget = target;
        return true;
    }

    private static Font UiFont
    {
        get
        {
            if (_font == null)
            {
                try { _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                catch { }
                if (_font == null)
                    try { _font = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                    catch { }
            }
            return _font;
        }
    }


    /// <summary>
    /// 窗口事件总入口 —— **只负责分派**：先判定这个窗口属于哪座建筑，
    /// 再**只执行那座建筑的规格**（`BuildingSpec`）。
    ///
    /// ## 为什么改成这样（架构重构）
    /// 早期版本所有建筑共用一套 `Apply` 逻辑 + 一套隐藏名单 + 一个全局开关，
    /// 结果「改下拉框 → 高炉也跟着变」「改高炉 → 另两座也不产了」。
    /// 用户明确要求**每种建筑独立存在**，所以：
    ///   * 归属判定 → 拿到 `BuildingSpec`
    ///   * 之后每个动作都先看**该建筑自己的规格**（要不要填下拉 / 隐藏什么 / 改不改文案）
    ///   * 任何一座建筑的规格变化，**不会**影响其他建筑
    /// </summary>
    internal static void Apply(GameObject window)
    {
        if (window == null) return;

        int guid = ReadWindowFacilityGuid(window);
        BuildingSpec? spec = guid != 0 ? SpecByGuid(guid) : null;

        if (Plugin.VerboseEntry?.Value == true)
        {
            try
            {
                string key = window.name + "|" + guid + "|" + (spec?.Name ?? "原版");
                if (key != _lastApplyKey)
                {
                    _lastApplyKey = key;
                    Plugin.LogV($"[FacilityUI] Apply 窗口 {window.name} facility_guid={guid}" +
                                $" → {(spec == null ? "原版的，不改" : $"建筑「{spec.Name}」，按其规格处理")}");
                }
            }
            catch { }
        }
        // 诊断去重：同一个窗口只打一次
        // （窗口开着时 `Apply` 被刷新方法**每帧**调用，不去重会把日志刷爆）
        if (Plugin.VerboseEntry?.Value == true && _diagDone.Add(window.GetInstanceID()))
        {
            DumpBoundFacility(window);
            DumpFurnaceRecipeDropdown(window);
            DumpAllDropdowns(window);
        }

        // ⚠ 标定红块要**每次**检查（不在上面的去重块里）——
        //   窗口关掉时红块会跟着销毁，只建一次的话再开窗就没了。
        UiProbe.AutoEnsure(window);

        // 不是本 mod 的建筑 → 一律不碰（fail-safe）
        if (spec == null) return;

        // 记录「当前打开的本 mod 窗口」—— UI 构建实验（F12）与将来的 UI 模板
        // 都需要一个挂载点；这里顺手记下来比事后扫场景可靠。
        try
        {
            if (window.activeInHierarchy)
            {
                _currentWindow = window;
                _currentSpec = spec;
                _currentFacility = FindFacilityByGuid(guid);
            }
        }
        catch { }

        // ★ 通用 UI 模板：建筑在自己规格里写了 UiLayout 才渲染。
        //   与占地尺寸无关（2×2 / 3×3 / N×N 同一套模板），多座建筑各自独立。
        if (spec.Ui != null)
        {
            try { UiTemplate.Render(spec, _currentFacility); }
            catch (Exception ex) { Plugin.LogV($"[FacilityUI] UI 模板渲染失败: {ex.Message}"); }
        }

        // ① 按**该建筑自己的规格**隐藏控件 —— 只在**首次**处理这个窗口时做一次。
        //
        // 为什么只做一次：
        //   * Apply 被窗口刷新方法每帧调用，而 HideAll 内部有反射扫描 + 层级遍历，每帧跑很浪费
        //   * 反复隐藏同一个物体也没意义。用「已处理标记」平衡：
        //     窗口关闭时标记随物体一起销毁，下次开窗会重新处理。
        if (!IsProcessed(window))
        {
            int hidden = NativeUi.HideAll(window, spec.HideControls);
            MarkProcessed(window);
            Plugin.LogV($"[FacilityUI] 建筑「{spec.Name}」窗口 {window.name}" +
                        $" → 隐名单 {spec.HideControls.Length} 项，实际隐藏 {hidden} 个");
        }

        // ② 按规格决定要不要往原生下拉里填候选
        if (spec.FillExtraProductDropdown) NativeDropdown.Refresh(window);


        // ③ 按规格决定要不要改写文案（熔炉窗口不改 —— 它的文案是游戏自己的）
        if (spec.RewriteWindowTexts) RewriteTexts(window);

        // 诊断：列出窗口一级子物体与所有被隐藏的物体（详细模式），
        // 用来回答「下拉去哪了 / 我隐藏了什么」，不靠猜。
        if (Plugin.VerboseEntry?.Value == true) { DumpHidden(window); DumpDropdownChain(window); }

        // ④ 自绘选择条（「额外产品」那条）：**只给声明了要它的建筑画**。
        //
        // ⚠⚠ 这里踩过一个坑：原来只判断「窗口里有没有 `txt_forest_coverage_rate`」，
        //   而这是**采集营地窗口**的固有文本 —— 于是任何借 `window_gatherers_hut`
        //   的建筑（例如 3×3 高炉）都被画上了一条「额外产品」下拉，
        //   看起来就跟生产所一模一样（用户实测反馈「新建的 3×3 高炉功能和之前的生产所一样，
        //   UI 还是乱的」）。
        //   **判据必须用建筑规格，不能用窗口内容。**
        if (!spec.FillExtraProductDropdown) return;

        var coverage = FindText(window, "txt_forest_coverage_rate");
        if (coverage == null) return;

        var bar = EnsureSelector(window);
        if (bar == null) return;

        var barRt = bar.GetComponent<RectTransform>();
        var srcRt = coverage.GetComponent<RectTransform>();
        if (barRt.transform.parent != srcRt.parent)
            barRt.SetParent(srcRt.parent, false);
        var le = bar.gameObject.GetComponent<LayoutElement>() ?? bar.gameObject.AddComponent<LayoutElement>();
        le.ignoreLayout = true;
        barRt.anchorMin = srcRt.anchorMin;
        barRt.anchorMax = srcRt.anchorMax;
        barRt.pivot = srcRt.pivot;
        barRt.anchoredPosition = srcRt.anchoredPosition + new Vector2(0, -32f);

        // ⚠ 宽度**不要写死**：锚点是从 `txt_forest_coverage_rate` 抄来的，
        // 如果那对锚点会横向拉伸（anchorMin.x=0 / anchorMax.x=1），
        // `sizeDelta.x` 就只是「相对父容器的增量」而不是实际宽度 ——
        // 这正是反复改 BarWidth 却始终「超出窗口」的原因。
        // 正确做法：把锚点收回成一个点，宽度取**窗口内容区的实际宽度**。
        barRt.anchorMin = new Vector2(srcRt.anchorMin.x, srcRt.anchorMin.y);
        barRt.anchorMax = new Vector2(srcRt.anchorMin.x, srcRt.anchorMin.y);
        float contentW = ContentWidth(window, srcRt);
        barRt.sizeDelta = new Vector2(contentW, BarHeight);

        // 诊断：把选择条到窗口根节点的**父链**打出来（名字 + rect 宽 + 锚点 + 位置）。
        // 用户反复反馈「长度还是超出」，需要确认到底哪一层的 rect 比窗口宽 ——
        // 如果父容器本身就比可见窗口宽，那么子控件「和父容器同宽」照样会超出。
        if (Plugin.VerboseEntry?.Value == true) DumpParentChain(window, bar);

        // 让候选列表**每帧跟随**标题条（窗口被拖动/换位置后列表会飘走，
        // 早期版本只在展开那一刻算一次位置，所以「打开后下拉框不正确」）。
        SyncListPosition(window, bar);

        // 换了窗口就把上一个展开的列表收起来（避免两个窗口的列表同时飘着）
        if (_openListOwner != null && _openListOwner != bar && IsListOpen(bar) == false)
            CloseList(FindOwnerOf(_openListOwner));

        LogSelectorScreenPos(bar);

        // 详细模式：把窗口里所有控件 + 下拉框的实际状态打出来。
        // 用途：用户反馈「下拉框不正确 / 没有工作」时，靠这份清单精确定位
        // （是位置不对、被遮挡、还是点击没派发），不用再猜。
        if (Plugin.VerboseEntry?.Value == true) DumpWindowState(window, bar);
    }

    /// <summary>
    /// 诊断（详细模式）：把原生下拉 `dp_blueprint` **自身的可见性链路**打出来 ——
    /// 它以及每一级祖先的 activeSelf/activeInHierarchy/屏幕坐标。
    ///
    /// 为什么需要：用户反馈「下拉被填充了却看不见」。
    /// 光看"我隐藏了哪些物体"不够（早期版本据此误判，以为没隐藏下拉就没事），
    /// 必须直接问下拉自己：你到底是不是活的、在哪。
    /// </summary>
    private static void DumpDropdownChain(GameObject window)
    {
        try
        {
            var dd = NativeUi.Find<TMPro.TMP_Dropdown>(window, "dp_blueprint");
            if (dd == null)
            {
                Plugin.LogV("[FacilityUI] 下拉链路: 找不到 dp_blueprint（可能被隐藏或字段取不到）");
                return;
            }
            var sb = new System.Text.StringBuilder("[FacilityUI] 下拉链路（自己 → 各级祖先）:\n");
            var cur = dd.transform;
            int depth = 0;
            while (cur != null && depth++ < 12)
            {
                var rt = cur.GetComponent<RectTransform>();
                sb.Append("  ").Append(new string(' ', depth * 2)).Append(cur.name)
                  .Append(" activeSelf=").Append(cur.gameObject.activeSelf)
                  .Append(" inHierarchy=").Append(cur.gameObject.activeInHierarchy);
                if (rt != null)
                {
                    sb.Append(" screen=").Append(rt.position.x.ToString("0")).Append(',')
                      .Append(rt.position.y.ToString("0"))
                      .Append(" size=").Append(rt.rect.width.ToString("0")).Append('x')
                      .Append(rt.rect.height.ToString("0"));
                }
                sb.Append(cur.gameObject == window ? "   ← 窗口根" : "").Append('\n');
                if (cur.gameObject == window) break;
                cur = cur.parent;
            }
            // 下拉自身的显示值 + 选项数，确认数据侧是好的
            string shown = "?";
            try { shown = dd.captionText != null ? dd.captionText.text : "(captionText=null)"; } catch { }
            int optCount = 0;
            try { optCount = dd.options.Count; } catch { }
            sb.Append("  显示值=").Append(shown).Append("  选项数=").Append(optCount).Append('\n');
            Plugin.LogV(sb.ToString());
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 下拉链路诊断失败: {ex.Message}"); }
    }

    /// <summary>
    /// 诊断（详细模式）：列出窗口一级子物体（名字/是否可见）+ 所有被隐藏的物体路径。
    /// 用来回答「下拉去哪了 / 我隐藏了什么」这类问题，避免再靠猜。
    /// 踩过的坑：把下拉的**祖先**隐藏了，导致原生下拉整个消失。
    /// </summary>
    private static void DumpHidden(GameObject window)
    {
        try
        {
            var sb = new System.Text.StringBuilder("[FacilityUI] 窗口一级子物体:\n");
            foreach (var t in window.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.parent != window.transform) continue;
                sb.Append("  ").Append(t.name)
                  .Append(t.gameObject.activeSelf ? " [显示]" : " [已隐藏]").Append('\n');
            }
            sb.Append("[FacilityUI] 被隐藏的物体:\n");
            int n = 0;
            foreach (var t in window.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.gameObject == window) continue;
                if (t.gameObject.activeSelf) continue;
                // 祖先已经隐藏的不重复列（避免一列一大片）
                if (t.parent != null && !t.parent.gameObject.activeSelf) continue;
                sb.Append("  ").Append(DiagPathOf(t, window.transform)).Append('\n');
                if (++n > 40) break;
            }
            if (n == 0) sb.Append("  (无)\n");
            Plugin.LogV(sb.ToString());
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] DumpHidden 失败: {ex.Message}"); }
    }

    /// <summary>取相对路径（诊断用）</summary>
    private static string DiagPathOf(Transform t, Transform root)
    {
        var parts = new System.Collections.Generic.List<string>();
        var cur = t;
        while (cur != null && cur != root) { parts.Insert(0, cur.name); cur = cur.parent; }
        return string.Join("/", parts);
    }

    /// <summary>诊断：把窗口控件的实际坐标与下拉框状态打一行（仅详细模式）</summary>
    private static void DumpWindowState(GameObject window, GameObject bar)
    {
        try
        {
            var sb = new System.Text.StringBuilder("[FacilityUI] 窗口诊断:\n");
            foreach (var t in window.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                if (t == null) continue;
                var rt = t.rectTransform;
                sb.Append("  [TMP] ").Append(t.name)
                  .Append(" pos=").Append(rt.position.x.ToString("0")).Append(',')
                  .Append(rt.position.y.ToString("0"))
                  .Append(" size=").Append(rt.rect.width.ToString("0")).Append('x')
                  .Append(rt.rect.height.ToString("0"))
                  .Append(" text=").Append(t.text != null && t.text.Length > 18 ? t.text[..18] : t.text)
                  .Append('\n');
            }
            var barRt = bar.GetComponent<RectTransform>();
            sb.Append("  [选择条] ").Append(bar.name)
              .Append(" screen=").Append(barRt.position.x.ToString("0")).Append(',')
              .Append(barRt.position.y.ToString("0"))
              .Append(" size=").Append(barRt.rect.width.ToString("0")).Append('x')
              .Append(barRt.rect.height.ToString("0"))
              .Append(" active=").Append(bar.activeInHierarchy).Append('\n');
            var list = FindChildByName(bar.transform.parent, DropdownListName);
            if (list == null)
            {
                sb.Append("  [候选列表] 不存在\n");
            }
            else
            {
                var lrt = list.GetComponent<RectTransform>();
                sb.Append("  [候选列表] screen=").Append(lrt.position.x.ToString("0")).Append(',')
                  .Append(lrt.position.y.ToString("0"))
                  .Append(" size=").Append(lrt.rect.width.ToString("0")).Append('x')
                  .Append(lrt.rect.height.ToString("0"))
                  .Append(" open=").Append(list.activeSelf)
                  .Append(" rows=").Append(list.transform.childCount).Append('\n');
                for (int i = 0; i < list.transform.childCount && i < 6; i++)
                {
                    var row = list.transform.GetChild(i);
                    if (row == null) continue;
                    var rrt = row.GetComponent<RectTransform>();
                    var btn = row.GetComponent<Button>();
                    var img = row.GetComponent<Image>();
                    sb.Append("    [行").Append(i).Append("] ").Append(row.name)
                      .Append(" screen=").Append(rrt.position.x.ToString("0")).Append(',')
                      .Append(rrt.position.y.ToString("0"))
                      .Append(" size=").Append(rrt.rect.width.ToString("0")).Append('x')
                      .Append(rrt.rect.height.ToString("0"))
                      .Append(" button=").Append(btn != null)
                      .Append(" raycast=").Append(img != null && img.raycastTarget)
                      .Append(" active=").Append(row.gameObject.activeInHierarchy)
                      .Append('\n');
                }
            }
            Plugin.LogV(sb.ToString());
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 窗口诊断失败: {ex.Message}"); }
    }

    /// <summary>
    /// 这个窗口是不是绑在**本 mod 的设施**上。
    ///
    /// ⚠⚠ 必须**判不出来就返回 false**（fail-safe）。
    /// 血泪教训：早期版本取不到 `stuff_id` 字段时直接 `return true`（想着「宁可多改」），
    /// 结果**把游戏原版制造台的窗口也改了** —— 用户实测截图对比：材料需求图标与
    /// 「进度 0%」整块消失、「材料取用范围 50」的数字没了。
    /// 原因是我们的建筑(105050) 和制造台(105010) 用**同一个** `window_prefab`
    /// （`window_blacksmith`），组件类型相同，**只有 `stuff_id` / `facility_guid` 能区分**。
    ///
    /// 判据顺序：
    ///   1. 窗口上 `stuff_id` == 本 mod 设施 id        → true
    ///   2. 窗口上 `facility_guid` 命中本 mod 建筑      → true
    ///   3. 都取不到                                    → **false（不动它）**
    /// </summary>
    /// <summary>
    /// 按**窗口类型**给出「要隐藏的控件」名单。
    ///
    /// ## 为什么必须区分窗口类型（血泪教训）
    /// 早期版本用**一套通用名单**（`icon_num` / `my_progress_make` / `res_grid` …），
    /// 结果用到熔炉窗口（`window_furnace`）时，把这些名字在熔炉里**同样存在**的控件也隐藏了。
    ///
    /// ## ⚠⚠ 分派判据只能用**窗口名**，不能用组件类型
    /// 诊断实证（`[FacilityUI] 窗口 window_furnace（组件: … WindowWorkshop …）`）：
    /// **`window_furnace` 上也挂着 `WindowWorkshop` 组件** ——
    /// `WindowFurnace` 大概是 `WindowWorkshop` 的派生类，所以「按组件类型分派」会失效，
    /// 熔炉也吃到制造台名单（实测：隐名单 16 项）。
    /// 正确判据：**`window.name`**（`window_blacksmith` / `window_workshop` / `window_furnace` …）。
    ///
    /// ## ⚠⚠ `fomula_item_main` **绝不能进名单**（这个坑踩了两次）
    /// 它是原生下拉 `dp_blueprint` 的**父容器**。隐藏它 → 下拉看不见：
    /// 诊断显示 `dp_blueprint activeSelf=True inHierarchy=False`，
    /// 祖先 `fomula_item_main activeSelf=False` → 用户看到「下拉消失」。
    /// 同理「进度」`my_progress_make` 与「材料取用范围」是用户要求保留/实现的功能，也不该隐藏。
    /// </summary>
    private static string[] HideListFor(GameObject window)
    {
        string n = "";
        try { n = window.name ?? ""; } catch { }

        // 只有制造台系窗口才隐藏「工坊配方」专有的那些
        if (n != "window_blacksmith" && n != "window_workshop") return Array.Empty<string>();

        return new[]
        {
            "formula_item_alternative", "fomula_item_alternative",   // 游戏里拼写是 fomula
            "auto_make_product_of_materials",
            "material_settings", "material_settings_grid", "material_settings_panel",
            "tmp_product",
            "btn_add_alternative",
            // 「可使用的材料」里的格子。**只隐藏格子，不隐藏容器 `res_grid`** ——
            // 原生下拉 dp_blueprint 可能挂在容器下面。
            "icon_num", "icon_num_1", "icon_num_2", "icon_num_3",
            "icon_num_4", "icon_num_5",
        };
    }

    /// <summary>窗口上有没有指定名字的组件（用来认窗口类型）</summary>
    private static bool HasComponentNamed(GameObject window, string typeName)
    {
        try
        {
            foreach (var c in window.GetComponents<Component>())
            {
                if (c != null && c.GetType().Name == typeName) return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 诊断（详细模式）：读出窗口上绑定的设施对象字段（`furnace` / `workshop` / `facility` …）
    /// 是否为 null。
    ///
    /// 用途：`WindowWorkshop.SetInfo` 在 `this.workshop == null` 时会抛空引用并**中途中断**，
    /// 窗口就停在默认值、回调没接上（用户描述为「全是默认值且无法交互」）。
    /// 读这些字段能直接判定是不是「设施类型与窗口机制不匹配」。
    /// </summary>
    private static void DumpBoundFacility(GameObject window)
    {
        try
        {
            var sb = new System.Text.StringBuilder($"[FacilityUI] 窗口 {window.name} 绑定的设施字段:\n");
            foreach (var c in window.GetComponents<Component>())
            {
                if (c == null) continue;
                var t = c.GetType();
                if (t.Name == "RectTransform" || t.Name == "Transform") continue;
                sb.Append("  组件 ").Append(t.Name).Append(": ");
                bool any = false;
                foreach (var fname in new[] { "facility", "furnace", "workshop", "stuff_id", "facility_guid" })
                {
                    object? v = null;
                    try
                    {
                        var pi = t.GetProperty(fname);
                        if (pi != null) v = pi.GetValue(c);
                        if (v == null)
                        {
                            var fi = t.GetField(fname);
                            if (fi != null) v = fi.GetValue(c);
                        }
                    }
                    catch { }
                    if (v == null) continue;
                    any = true;
                    string vs = v is UnityEngine.Object uo
                        ? (uo == null ? "null" : uo.GetType().Name)
                        : v.ToString() ?? "?";
                    sb.Append(fname).Append('=').Append(vs).Append(' ');
                }
                if (!any) sb.Append("(无相关字段)");
                sb.Append('\n');
            }
            Plugin.LogV(sb.ToString());
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] DumpBoundFacility 失败: {ex.Message}"); }
    }

    /// <summary>窗口是否已经按规格处理过（用实例 ID 做标记，窗口销毁即失效）</summary>
    private static readonly System.Collections.Generic.HashSet<int> _processedWindows = new();

    private static bool IsProcessed(GameObject window)
    {
        try { return _processedWindows.Contains(window.GetInstanceID()); }
        catch { return false; }
    }

    private static void MarkProcessed(GameObject window)
    {
        try
        {
            // 标记集合会随窗口开关增长 —— 定期把已销毁的实例清掉
            if (_processedWindows.Count > 64)
            {
                var alive = new System.Collections.Generic.HashSet<int>();
                foreach (var w in UnityEngine.Object.FindObjectsOfType<Transform>())
                {
                    if (w == null) continue;
                    int id = w.gameObject.GetInstanceID();
                    if (_processedWindows.Contains(id)) alive.Add(id);
                }
                _processedWindows.Clear();
                foreach (var id in alive) _processedWindows.Add(id);
            }
            _processedWindows.Add(window.GetInstanceID());
        }
        catch { }
    }

    /// <summary>读窗口上绑定的 `facility_guid`（读不到返回 0）</summary>
    private static int ReadWindowFacilityGuid(GameObject window)
    {
        try
        {
            foreach (var c in window.GetComponents<Component>())
            {
                if (c == null) continue;
                var g = ReadInt(c, "facility_guid");
                if (g.HasValue && g.Value != 0) return g.Value;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// 由设施 guid 找到它对应的**建筑规格**。
    /// 做法：先按 guid 找到场景里的设施对象取 `stuff_id`，再查规格表。
    /// 结果缓存（guid → 规格），避免每帧扫场景。
    /// </summary>
    private static BuildingSpec? SpecByGuid(int guid)
    {
        if (_specByGuid.TryGetValue(guid, out var cached)) return cached;

        int stuffId = 0;
        try
        {
            foreach (var f in UnityEngine.Object.FindObjectsOfType<Facility>())
            {
                if (f == null) continue;
                int g = 0;
                try { g = f.guid; } catch { }
                if (g != guid) continue;
                try { stuffId = f.stuff_id; } catch { }
                break;
            }
        }
        catch { }

        var spec = stuffId != 0 ? Buildings.ByStuffId(stuffId) : null;
        _specByGuid[guid] = spec;
        if (spec != null) Plugin.LogV($"[FacilityUI] guid={guid} 归属建筑「{spec.Name}」（stuff_id={stuffId}）");
        return spec;
    }

    /// <summary>guid → 建筑规格 的缓存（拆除/换档时由 InvalidateManagedGuids 一起清）</summary>
    private static readonly System.Collections.Generic.Dictionary<int, BuildingSpec?> _specByGuid = new();

    /// <summary>
    /// 诊断（详细模式）：打印**熔炉窗口里配方下拉**的项数。
    /// 用于区分「配方数据没进游戏」和「数据进了但 UI 没显示」两种情况 ——
    /// 实测 lueprint_list_dic_by_facility[105052] 有 7 条，
    /// 所以若这里显示 0 项，就是 UI 侧的问题（窗口字段没绑上/没刷新）。
    /// </summary>
    private static void DumpFurnaceRecipeDropdown(GameObject window)
    {
        try
        {
            if (window.name != "window_furnace") return;
            var sb = new System.Text.StringBuilder($"[FacilityUI] 熔炉窗口配方下拉诊断:\n");
            foreach (var c in window.GetComponents<Component>())
            {
                if (c == null) continue;
                var t = c.GetType();
                object? dd = null;
                try
                {
                    var fi = t.GetField("dp_blueprint") ?? t.GetField("dp_formula")
                             ?? t.GetField("dropdown");
                    if (fi != null) dd = fi.GetValue(c);
                    if (dd == null)
                    {
                        var pi = t.GetProperty("dp_blueprint");
                        if (pi != null) dd = pi.GetValue(c);
                    }
                }
                catch { }
                if (dd is TMPro.TMP_Dropdown td)
                {
                    int n = 0; string shown = "?";
                    try { n = td.options.Count; } catch { }
                    try { shown = td.captionText != null ? td.captionText.text : "(null)"; } catch { }
                    sb.Append($"  组件{t.Name}.{td.name}: 选项 {n} 项，显示值={shown}，" +
                              $"active={td.gameObject.activeInHierarchy}\n");
                }
            }
            Plugin.LogV(sb.ToString());
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 配方下拉诊断失败: {ex.Message}"); }
    }

    /// <summary>
    /// 诊断（详细模式）：枚举窗口里**所有** `TMP_Dropdown`，打印路径 / 选项数 / 显示值。
    ///
    /// ## 为什么需要
    /// 熔炉窗口（`window_furnace`）的「配方下拉」是空的，而原生熔炉有值。
    /// 已知数据侧没问题（`blueprint_list_dic_by_facility[105052]` 有 7 条），
    /// 所以问题在**UI 侧**：必须知道窗口里到底有几个下拉、哪个才是配方下拉。
    /// 之前只看了 `WindowWorkshop.dp_blueprint`（0 项）—— 那是**工坊基类**的字段，
    /// 不一定是熔炉用的那个。
    /// </summary>
    private static void DumpAllDropdowns(GameObject window)
    {
        try
        {
            var sb = new System.Text.StringBuilder($"[FacilityUI] 窗口 {window.name} 的下拉枚举:\n");
            int n = 0;
            foreach (var td in window.GetComponentsInChildren<TMPro.TMP_Dropdown>(true))
            {
                if (td == null) continue;
                n++;
                int cnt = 0;
                string shown = "?";
                try { cnt = td.options.Count; } catch { }
                try { shown = td.captionText != null ? td.captionText.text : "(null)"; } catch { }
                sb.Append($"  [{n}] 路径={DiagPathOf(td.transform, window.transform)}")
                  .Append($" go={td.gameObject.name}")
                  .Append($" 选项={cnt} 显示值={shown}")
                  .Append($" active={td.gameObject.activeInHierarchy}\n");
            }
            if (n == 0) sb.Append("  （没有 TMP_Dropdown）\n");

            // 顺带把熔炉特有的几个字段对象在不在也打一下
            foreach (var fname in new[] { "go_formula", "stock_adjust_list_view",
                                          "panel_stuff_stock_adjust", "stuff_content_root" })
            {
                var go = NativeUi.FindGo(window, fname, true);
                sb.Append($"  字段 {fname}: {(go == null ? "【null/找不到】" : go.name + $" active={go.activeInHierarchy}")}\n");
            }
            Plugin.LogV(sb.ToString());
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 下拉枚举失败: {ex.Message}"); }
    }

    private static string _lastApplyKey = "";

    /// <summary>最近一次处理过的本 mod 建筑窗口（UI 构建实验 / 模板的挂载点）</summary>
    private static GameObject? _currentWindow;

    /// <summary>最近一次处理过的建筑规格（模板要知道给谁渲染）</summary>
    private static BuildingSpec? _currentSpec;

    /// <summary>最近一次处理过的建筑实例（UI 模板要用它的 bag / guid）</summary>
    private static Facility? _currentFacility;

    /// <summary>按 guid 找场景里的设施实例</summary>
    private static Facility? FindFacilityByGuid(int guid)
    {
        if (guid == 0) return null;
        try
        {
            foreach (var f in UnityEngine.Object.FindObjectsOfType<Facility>())
            {
                if (f == null) continue;
                int g = 0;
                try { g = f.guid; } catch { }
                if (g == guid) return f;
            }
        }
        catch { }
        return null;
    }

    /// <summary>已做过详细诊断的窗口（避免每帧刷屏）</summary>
    private static readonly System.Collections.Generic.HashSet<int> _diagDone = new();

    /// <summary>窗口上挂的组件名清单（诊断用，用来确认窗口类型判断对不对）</summary>
    private static string ComponentNames(GameObject window)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in window.GetComponents<Component>())
            {
                if (c == null) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(c.GetType().Name);
            }
            return sb.ToString();
        }
        catch { return "?"; }
    }

    /// <summary>
    /// 当前打开的本 mod 建筑窗口（供 UI 构建实验 / 模板定位挂载点）。
    /// 由 `Apply` 在每次处理窗口时更新；窗口关闭后 Unity 的 `== null` 判断会成立。
    /// </summary>
    internal static GameObject? CurrentWindowForProbe()
    {
        try
        {
            if (_currentWindow == null || !_currentWindow) { _currentWindow = null; return null; }
            return _currentWindow.activeInHierarchy ? _currentWindow : null;
        }
        catch { return null; }
    }

    /// <summary>供安全护栏补丁复用（判断窗口是否属于本 mod 建筑）</summary>
    internal static bool IsOurWindowPublic(GameObject window) => IsOurWindow(window);

    private static bool IsOurWindow(GameObject window)
    {
        try
        {
            foreach (var c in window.GetComponents<Component>())
            {
                if (c == null) continue;

                var sid = ReadInt(c, "stuff_id");
                if (sid.HasValue && sid.Value != 0)
                {
                    bool ours = Plugin.IsManaged(sid.Value);
                    LogOwnershipOnce($"stuff_id={sid.Value}", ours);
                    return ours;
                }

                var g = ReadInt(c, "facility_guid");
                if (g.HasValue && g.Value != 0)
                {
                    bool ours = IsManagedGuid(g.Value);
                    LogOwnershipOnce($"facility_guid={g.Value}", ours);
                    return ours;
                }
            }
        }
        catch { }

        // 判不出来就不动：宁可我们的窗口少改一点，也绝不能破坏原版窗口
        LogOwnershipOnce("取不到 stuff_id/facility_guid", false);
        return false;
    }

    /// <summary>归属判断的日志只打一次（同窗口同结果重复打会刷屏）</summary>
    private static void LogOwnershipOnce(string key, bool ours)
    {
        if (_lastOwnershipKey == key && _lastOwnershipOurs == ours) return;
        _lastOwnershipKey = key;
        _lastOwnershipOurs = ours;
        Plugin.LogV($"[FacilityUI] 归属判断: {key} → {(ours ? "我们的" : "原版的（不动）")}");
    }

    private static string _lastOwnershipKey = "";
    private static bool _lastOwnershipOurs;

    /// <summary>
    /// 这个 guid 是不是本 mod 建出来的设施。
    ///
    /// ⚠ 必须带缓存：窗口开着时 `Apply` **每帧**都会被调，
    /// 不能每帧 `FindObjectsOfType` 扫场景（性能与日志都会爆）。
    /// 缓存由 `FacilityComponent` 的每帧循环维护（新建/拆除/换档时置脏重建）。
    /// </summary>
    private static bool IsManagedGuid(int guid)
    {
        if (_managedGuidsStale
            || Time.frameCount - _managedGuidsFrame > 180)   // 兜底：最长 3 秒重建一次
        {
            CacheManagedGuids();
        }
        return _managedGuids.Contains(guid);
    }

    private static readonly HashSet<int> _managedGuids = new();
    private static bool _managedGuidsStale = true;
    private static int _managedGuidsFrame = -999;

    /// <summary>重建「本 mod 建筑 guid」缓存（换档/新建/拆除后由主循环置脏）</summary>
    internal static void CacheManagedGuids()
    {
        try
        {
            _managedGuids.Clear();
            foreach (var f in UnityEngine.Object.FindObjectsOfType<Facility>())
            {
                if (f == null) continue;
                int sid = 0, g = 0;
                try { sid = f.stuff_id; } catch { }
                if (!Plugin.IsManaged(sid)) continue;
                try { g = f.guid; } catch { }
                if (g != 0) _managedGuids.Add(g);
            }
        }
        catch { }
        _managedGuidsStale = false;
        _managedGuidsFrame = Time.frameCount;
        _specByGuid.Clear();   // 换档/拆迁后规格缓存也要失效
        Plugin.LogV($"[FacilityUI] 本 mod 建筑 guid 缓存已更新：{_managedGuids.Count} 座");
    }

    /// <summary>标脏：下次判断时重建缓存</summary>
    internal static void InvalidateManagedGuids() => _managedGuidsStale = true;

    /// <summary>从对象上读一个 int 字段/属性（取不到返回 null）</summary>
    private static int? ReadInt(object target, string name)
    {
        const System.Reflection.BindingFlags F =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance;
        var t = target.GetType();
        try
        {
            var fi = t.GetField(name, F) ?? t.GetField(name + "_", F);
            if (fi != null) return System.Convert.ToInt32(fi.GetValue(target));
        }
        catch { }
        try
        {
            var pi = t.GetProperty(name, F) ?? t.GetProperty(name + "_", F);
            if (pi != null && pi.CanRead) return System.Convert.ToInt32(pi.GetValue(target));
        }
        catch { }
        return null;
    }

    /// <summary>把候选列表的位置/尺寸对齐到当前标题条（每帧调用，防止窗口移动后列表飘走）</summary>
    private static void SyncListPosition(GameObject window, GameObject bar)
    {
        try
        {
            var list = FindChildByName(bar.transform.parent, DropdownListName);
            if (list == null) return;
            var listRt = list.GetComponent<RectTransform>();
            var barRt = bar.GetComponent<RectTransform>();
            if (listRt == null || barRt == null) return;
            listRt.anchorMin = barRt.anchorMin;
            listRt.anchorMax = barRt.anchorMax;
            listRt.pivot = new Vector2(barRt.pivot.x, 1f);
            listRt.anchoredPosition = barRt.anchoredPosition + new Vector2(0, -BarHeight - 1f);
            listRt.sizeDelta = new Vector2(BarWidth, RowHeight * Math.Max(2, CountCandidates()) + 4f);
            list.transform.SetAsLastSibling();
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 同步列表位置失败: {ex.Message}"); }
    }

    /// <summary>候选数量（含「无」那一项）</summary>
    private static int CountCandidates() => 1 + Plugin.ParseExtraCandidates().Count;

    // ---------- 文案改写 ----------

    internal static void RewriteTexts(GameObject window)
    {
        foreach (var t in window.GetComponentsInChildren<TMPro.TMP_Text>(true))
        {
            if (t == null) continue;
            if (t.name == "txt_forest_coverage_rate")
                t.text = "生产所：工人每日自动产出（数量可在配置调整）";
            else if (t.name == "txt_tip" && !string.IsNullOrEmpty(t.text) && t.text.Contains("森林覆盖率"))
                t.text = "工人越多，每日产出越多；点下方下拉框可选择额外产品";
        }
    }

    private static TMPro.TMP_Text? FindText(GameObject window, string name)
    {
        foreach (var t in window.GetComponentsInChildren<TMPro.TMP_Text>(true))
            if (t != null && t.name == name) return t;
        return null;
    }

    // ---------- 下拉框：标题条 ----------

    private static GameObject? EnsureSelector(GameObject window)
    {
        GameObject? bar = null;
        foreach (var t in window.GetComponentsInChildren<Transform>(true))
        {
            if (t != null && t.name == SelectorName) { bar = t.gameObject; break; }
        }
        if (bar == null) bar = BuildSelector(window);
        if (bar == null) return null;
        RefreshLabel(bar);
        return bar;
    }

    private static void RefreshLabel(GameObject bar)
    {
        var label = FindChildComponent<Text>(bar, "Label");
        if (label == null) return;
        int extra = Plugin.ExtraProduct;
        string name = CandidateName(extra);
        label.text = extra == 0
            ? "额外产品：无  ▼"
            : $"额外产品：{name} +{Plugin.ExtraPerDay}/日  ▼";
    }

    private static string CandidateName(int stuffId)
    {
        if (stuffId == 0) return "无";
        foreach (var (id, nm) in Plugin.ParseExtraCandidates())
            if (id == stuffId) return nm;
        return stuffId.ToString();
    }

    private static GameObject? BuildSelector(GameObject window)
    {
        var go = new GameObject(SelectorName);
        go.transform.SetParent(window.transform, false);
        go.AddComponent<RectTransform>();
        var img = go.AddComponent<Image>();
        img.color = PanelColor;

        var label = CreateText(go.transform, "Label", UiFont, 14, TextAnchor.MiddleLeft);
        var lr = label.rectTransform;
        lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
        lr.offsetMin = new Vector2(8, 1); lr.offsetMax = new Vector2(-8, -1);

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(Convert(new Action(() => OnHeaderClick(go))));
        // 双保险：EventTrigger 走 Unity 事件系统（游戏的输入派发若绕过 Button 也能收到）
        AddPointerClick(go, () => OnHeaderClick(go));
        return go;
    }

    // ---------- 下拉框：选项面板 ----------

    private static void OnHeaderClick(GameObject bar)
    {
        if (!ShouldHandleClick(bar)) return;
        try
        {
            var list = EnsureList(bar);
            if (list == null) return;
            bool open = !list.activeSelf;
            list.SetActive(open);
            _openListOwner = open ? bar : null;
            Plugin.LogV($"[FacilityUI] 下拉框{(open ? "展开" : "收起")}（{Plugin.ParseExtraCandidates().Count} 个候选）");
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 下拉框展开失败: {ex.Message}"); }
    }

    private static GameObject? EnsureList(GameObject bar)
    {
        // 先找已有的
        var existing = FindChildByName(bar.transform.parent, DropdownListName);
        if (existing != null)
        {
            RebuildRows(existing, bar);
            return existing;
        }

        var list = new GameObject(DropdownListName);
        list.transform.SetParent(bar.transform.parent, false);
        list.AddComponent<RectTransform>();
        var bg = list.AddComponent<Image>();
        bg.color = ListColor;
        list.SetActive(false);
        RebuildRows(list, bar);
        return list;
    }

    /// <summary>按当前候选清单重建选项行（候选可在 cfg 里改，所以每次展开前都重建）。</summary>
    private static void RebuildRows(GameObject list, GameObject bar)
    {
        // 清掉旧行
        for (int i = list.transform.childCount - 1; i >= 0; i--)
        {
            var c = list.transform.GetChild(i);
            if (c != null) UnityEngine.Object.Destroy(c.gameObject);
        }

        var cands = new List<(int id, string name)> { (0, "无") };
        cands.AddRange(Plugin.ParseExtraCandidates());

        var listRt = list.GetComponent<RectTransform>();
        var barRt = bar.GetComponent<RectTransform>();
        listRt.anchorMin = barRt.anchorMin;
        listRt.anchorMax = barRt.anchorMax;
        listRt.pivot = new Vector2(barRt.pivot.x, 1f);
        // 紧贴标题条下方；列表往右展开（标题条左边凸出窗口，往左会出屏）
        listRt.anchoredPosition = barRt.anchoredPosition + new Vector2(0, -BarHeight - 1f);
        listRt.sizeDelta = new Vector2(BarWidth, RowHeight * cands.Count + 4f);

        int current = Plugin.ExtraProduct;
        float y = -2f;
        foreach (var (id, nm) in cands)
        {
            var row = new GameObject("Row_" + id);
            row.transform.SetParent(list.transform, false);
            row.AddComponent<RectTransform>();
            var rrt = row.GetComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0f, 1f);
            rrt.anchorMax = new Vector2(1f, 1f);
            rrt.pivot = new Vector2(0.5f, 1f);
            rrt.anchoredPosition = new Vector2(0f, y);
            rrt.sizeDelta = new Vector2(0f, RowHeight);
            y -= RowHeight;

            var rimg = row.AddComponent<Image>();
            rimg.color = id == current ? RowCurColor : RowColor;

            string text = id == current ? "● " + nm : "　 " + nm;
            var label = CreateText(row.transform, "Label", UiFont, 13, TextAnchor.MiddleLeft);
            label.text = text;
            var lrt = label.rectTransform;
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = new Vector2(10, 0); lrt.offsetMax = new Vector2(-6, 0);

            int captured = id;
            var btn = row.AddComponent<Button>();
            btn.targetGraphic = rimg;
            btn.onClick.AddListener(Convert(new Action(() => OnOptionClick(list, captured))));
            AddPointerClick(row, () => OnOptionClick(list, captured));
        }
        list.transform.SetAsLastSibling();   // 盖在窗口其它元素之上
    }

    private static void OnOptionClick(GameObject list, int stuffId)
    {
        if (!ShouldHandleClick(list)) return;
        try
        {
            Plugin.SetExtraProduct(stuffId);
            list.SetActive(false);
            _openListOwner = null;
            var bar = FindChildByName(list.transform.parent, SelectorName);
            if (bar != null) RefreshLabel(bar);
            Plugin.LogV($"[FacilityUI] 下拉框选中 {stuffId}（{CandidateName(stuffId)}）");
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 下拉框选中失败: {ex.Message}"); }
    }

    // ---------- 测试钩子 ----------

    /// <summary>当前存活的选择条（跨全部已打开窗口）。</summary>
    internal static GameObject? FindLiveSelector()
    {
        try
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
                if (t != null && t.name == SelectorName && t.gameObject.activeInHierarchy)
                    return t.gameObject;
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] FindLiveSelector 失败: {ex.Message}"); }
        return null;
    }

    /// <summary>走 Unity 原生事件系统派发一次点击（自动化测试用）。</summary>
    internal static bool SimulateClick(GameObject target)
    {
        if (target == null) return false;
        var es = EventSystem.current;
        if (es == null)
        {
            Plugin.LogV("[FacilityUI] EventSystem.current 为空，无法派发点击");
            return false;
        }
        var data = new PointerEventData(es);
        try
        {
            ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
            Plugin.LogV($"[FacilityUI] 已程序化派发 PointerClick 给 {target.name}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogV($"[FacilityUI] 程序化点击失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 自动化测试钩子（F8）：操作**原生下拉** —— 读当前值 → 选下一项 → 校验 cfg 被写入。
    ///
    /// 用途：验证「原生下拉 → 写 cfg」这条链路，不依赖 OS 鼠标坐标标定。
    /// 详细模式关闭时也生效。日志格式不要改，`_tools/e2e.py` 会解析。
    ///
    /// 注：早期版本点的是自绘控件（`FacilityExtraProductSelector`），
    /// 改成原生 `dp_blueprint` 后那套已不再使用。
    /// </summary>
    internal static void TestClickSelector()
    {
        try
        {
            var win = FindLiveWindow();
            if (win == null)
            {
                Plugin.LogV("[FacilityUI] 测试点击：当前没有打开的生产所窗口");
                return;
            }
            var dd = NativeUi.Find<TMPro.TMP_Dropdown>(win, "dp_blueprint");
            if (dd == null)
            {
                Plugin.LogV("[FacilityUI] 测试点击：窗口里没有原生下拉 dp_blueprint");
                return;
            }

            int before = Plugin.ExtraProduct;
            int count = 0;
            try { count = dd.options.Count; } catch { }
            if (count <= 1)
            {
                Plugin.LogV($"[FacilityUI] 测试点击：下拉只有 {count} 项（候选没填进去？）");
                return;
            }

            int cur = 0;
            try { cur = dd.value; } catch { }
            int next = (cur + 1) % count;
            Plugin.LogV($"[FacilityUI] 原生下拉：共 {count} 项，当前 {cur}，切到 {next}");

            // 走标准 API（等价于玩家点选），会触发 onValueChanged → 写 cfg
            dd.value = next;
            dd.RefreshShownValue();

            int after = Plugin.ExtraProduct;
            if (after != before)
                Plugin.LogV($"[FacilityUI] 下拉选中 {next}（无={0}）→ 额外产品 {before} → {after}  [OK]");
            else
                Plugin.LogV($"[FacilityUI] 下拉选中 {next}，但额外产品没变（仍是 {after}）—— 回调没接上？");
            return;
        }
        catch (Exception ex)
        {
            Plugin.LogV($"[FacilityUI] 测试点击失败: {ex.Message}");
            return;
        }
    }

    /// <summary>找当前打开着的、属于本 mod 设施的窗口</summary>
    private static GameObject? FindLiveWindow()
    {
        try
        {
            foreach (var w in UnityEngine.Object.FindObjectsOfType<WindowWorkFacility>())
            {
                if (w == null) continue;
                var go = w.gameObject;
                if (!go.activeInHierarchy) continue;
                if (NativeUi.Find<TMPro.TMP_Dropdown>(go, "dp_blueprint") == null) continue;
                return go;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 自动化测试钩子（F10）：打开建筑窗口。
    /// **优先开 `PreferredProbeStuffId` 指定的那座**（用于做对照实验，例如
    /// 105052 熔炉对照 vs 105051 三乘三高炉），否则开第一座本 mod 建筑。
    /// </summary>
    internal static void TestOpenWindow()
    {
        try
        {
            int want = PreferredProbeStuffId;
            if (want != 0)
            {
                foreach (var f in UnityEngine.Object.FindObjectsOfType<Facility>())
                {
                    if (f == null) continue;
                    int sid = 0;
                    try { sid = f.stuff_id; } catch { }
                    if (sid != want) continue;
                    Plugin.LogV($"[FacilityUI] F10 开对照建筑 sid={sid} guid={SafeGuid(f)}");
                    if (TryShowWindow(f)) return;
                }
                Plugin.LogV($"[FacilityUI] F10 没找到 sid={want} 的建筑，改开第一座本 mod 建筑");
            }

            foreach (var f in UnityEngine.Object.FindObjectsOfType<Facility>())
            {
                if (f == null) continue;
                int sid = 0;
                try { sid = f.stuff_id; } catch { }
                if (!Plugin.IsManaged(sid)) continue;
                Plugin.LogV($"[FacilityUI] F10 打开窗口尝试：guid={SafeGuid(f)} sid={sid}");
                if (TryShowWindow(f)) return;
            }
            Plugin.LogV("[FacilityUI] F10：没找到可开窗的建筑");
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] F10 失败: {ex.Message}"); }
    }

    /// <summary>
    /// F10 优先打开的设施 id（0 = 不指定，开第一座本 mod 建筑）。
    /// 做对照实验时由 `FacilityComponent` 按热键设置。
    /// </summary>
    internal static int PreferredProbeStuffId = 0;

    /// <summary>调游戏的 ShowWindow/OpenWindow/OnClick（按名字找，找到就调）</summary>
    private static bool TryShowWindow(Facility f)
    {
        try
        {
            var m = AccessTools.Method(f.GetType(), "ShowWindow")
                    ?? AccessTools.Method(f.GetType(), "OpenWindow")
                    ?? AccessTools.Method(f.GetType(), "OnClick")
                    ?? AccessTools.Method(f.GetType(), "OnSelected");
            if (m == null || m.GetParameters().Length != 0) return false;
            m.Invoke(f, null);
            Plugin.LogV($"[FacilityUI] F10 调用 {m.Name}() 成功");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogV($"[FacilityUI] F10 调用异常: {ex.Message}");
            return false;
        }
    }

    private static int SafeGuid(Facility f)
    {
        try { return f.guid; } catch { return 0; }
    }

    /// <summary>旧的自绘选择条路径（保留编译，实际不再使用）</summary>
    private static void TestClickSelectorLegacy()
    {
        try
        {
            var bar = FindLiveSelector();
            if (bar == null)
            {
                Plugin.LogV("[FacilityUI] 测试点击：当前没有打开的综合生产所窗口（找不到下拉框）");
                return;
            }
            int before = Plugin.ExtraProduct;

            // 第一步：展开（走事件系统，等价于真点标题条）
            SimulateClick(bar);
            var list = FindChildByName(bar.transform.parent, DropdownListName);
            if (list == null)
            {
                Plugin.LogV("[FacilityUI] 测试点击：下拉框列表没建出来");
                return;
            }
            if (!list.activeSelf)
            {
                Plugin.LogV("[FacilityUI] 测试点击：下拉框没展开（EventTrigger/Button 未收到点击）");
                return;
            }

            // 第二步：点「下一个候选项」那一行。
            // 候选列表 = 无(0) + cfg 候选，跟下拉框里的行一一对应（Row_0 就是「无」）。
            var ids = new List<int> { 0 };
            foreach (var (cid, _) in Plugin.ParseExtraCandidates()) ids.Add(cid);
            int cur = Plugin.ExtraProduct;
            int idx = ids.IndexOf(cur);
            int nextId = ids[(idx + 1) % ids.Count];
            var row = FindChildByName(list.transform, "Row_" + nextId);
            if (row == null)
            {
                Plugin.LogV($"[FacilityUI] 测试点击：找不到候选行 Row_{nextId}");
                return;
            }
            SimulateClick(row);

            int after = Plugin.ExtraProduct;
            Plugin.LogV($"[FacilityUI] 测试点击下拉框：额外产品 {before} -> {after}" +
                           (before == after ? "（未变化，请检查选项行的点击回调）" : "（已写入 cfg）"));
        }
        catch (Exception ex) { Plugin.LogError($"[FacilityUI] 测试点击异常: {ex}"); }
    }

    // ---------- 小工具 ----------

    private static bool IsListOpen(GameObject bar)
    {
        var list = FindChildByName(bar.transform.parent, DropdownListName);
        return list != null && list.activeSelf;
    }

    private static GameObject? FindOwnerOf(GameObject list) => list;

    private static void CloseList(GameObject? list)
    {
        try
        {
            if (list != null) list.SetActive(false);
            _openListOwner = null;
        }
        catch { }
    }

    private static GameObject? FindChildByName(Transform? parent, string name)
    {
        if (parent == null) return null;
        foreach (var t in parent.GetComponentsInChildren<Transform>(true))
            if (t != null && t.name == name) return t.gameObject;
        return null;
    }

    private static T? FindChildComponent<T>(GameObject parent, string name) where T : Component
    {
        foreach (var c in parent.GetComponentsInChildren<T>(true))
        {
            if (c != null && c.name == name) return c;
        }
        return null;
    }

    /// <summary>
    /// 诊断：把选择条 → 窗口根 的父链打一行（名字 / rect 宽高 / 世界位置 / 锚点）。
    /// 用来确认「到底是哪一层比窗口宽」——用户反复反馈下拉框超出窗口，
    /// 很可能是**父容器本身就比窗口宽**，那样子控件就算「与父同宽」也会超出。
    /// </summary>
    private static void DumpParentChain(GameObject window, GameObject bar)
    {
        try
        {
            var sb = new System.Text.StringBuilder("[FacilityUI] 选择条父链:\n");
            var cur = bar.transform;
            int depth = 0;
            while (cur != null && depth++ < 10)
            {
                var rt = cur.GetComponent<RectTransform>();
                if (rt == null) { sb.Append("  (无 RectTransform) ").Append(cur.name).Append('\n'); break; }
                sb.Append("  ").Append(new string(' ', depth * 2)).Append(cur.name)
                  .Append(" w=").Append(rt.rect.width.ToString("0"))
                  .Append(" h=").Append(rt.rect.height.ToString("0"))
                  .Append(" pos=").Append(rt.position.x.ToString("0")).Append(',')
                  .Append(rt.position.y.ToString("0"))
                  .Append(" anchor=").Append(rt.anchorMin.x.ToString("0.##")).Append('-')
                  .Append(rt.anchorMax.x.ToString("0.##"))
                  .Append(cur.gameObject == window ? "   ← 窗口根" : "")
                  .Append('\n');
                if (cur.gameObject == window) break;
                cur = cur.parent;
            }
            var wWin = window.GetComponent<RectTransform>();
            if (wWin != null)
                sb.Append("  窗口根 rect = ").Append(wWin.rect.width.ToString("0")).Append('x')
                  .Append(wWin.rect.height.ToString("0")).Append('\n');
            Plugin.LogV(sb.ToString());
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 父链诊断失败: {ex.Message}"); }
    }

    /// <summary>
    /// 取「窗口内容区」的实际宽度（用于把下拉框做成和游戏内控件同宽）。
    ///
    /// 顺序：容器 `content_area` → 参照文本 `txt_forest_coverage_rate` 的 rect 宽 → 兜底 BarWidth。
    /// 关键：都用 `rect.width`（锚点拉伸后的**真实像素宽**），
    /// 而不是 `sizeDelta`（那只是「相对父容器的增量」，写死了也没用）。
    /// </summary>
    private static float ContentWidth(GameObject window, RectTransform? fallback)
    {
        try
        {
            foreach (var t in window.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.name != "content_area") continue;
                var rt = t.GetComponent<RectTransform>();
                if (rt != null && rt.rect.width > 50f) return rt.rect.width;
            }
        }
        catch { }
        try
        {
            if (fallback != null && fallback.rect.width > 50f) return fallback.rect.width;
        }
        catch { }
        return BarWidth;
    }

    private static Text CreateText(Transform parent, string name, Font? font, int size, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var t = go.AddComponent<Text>();
        if (font != null) t.font = font;
        t.fontSize = size;
        t.color = TextColor;
        t.alignment = anchor;
        t.raycastTarget = false;   // 不挡点击，让 Button/EventTrigger 收到
        return t;
    }

    /// <summary>IL2CPP 下 Action → UnityAction 必须走 DelegateSupport 转换。</summary>
    private static UnityEngine.Events.UnityAction Convert(Action a)
        => DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(a);

    /// <summary>
    /// 给对象挂 EventTrigger 的 PointerClick。
    /// 用 EventTrigger 而不是只靠 Button.onClick：游戏的输入派发是手写的，
    /// 两条路径都挂上才能保证「真鼠标点击」和「程序化派发」都能进来。
    /// </summary>
    private static void AddPointerClick(GameObject go, Action handler)
    {
        try
        {
            var trigger = go.GetComponent<EventTrigger>() ?? go.AddComponent<EventTrigger>();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            entry.callback.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<BaseEventData>>(
                new Action<BaseEventData>(_ => handler())));
            trigger.triggers.Add(entry);
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] EventTrigger 挂载失败（退化为只用 Button）: {ex.Message}"); }
    }

    /// <summary>上一次上报过的选择条屏幕坐标，用于去重（Apply 会被窗口每次刷新调到，不能每次都打日志）。</summary>
    private static int _lastLoggedX = int.MinValue;
    private static int _lastLoggedY = int.MinValue;

    /// <summary>
    /// 详细模式下打印选择条的**屏幕坐标**——供无人值守 UI 自动化直接点击。
    /// 不要改这个格式：`_tools/e2e.py` 用正则 `选择条屏幕坐标=(\d+),(\d+)` 解析。
    /// （踩坑：靠截图找深色长条不可靠——游戏地砖本身就有大量深色像素。）
    /// ⚠ 坐标不变时不重复打日志：Apply 挂在窗口刷新链上，每帧都会跑，
    ///   不去重的话日志会被这一行刷爆（实测 1 秒上百行）。
    /// </summary>
    private static void LogSelectorScreenPos(GameObject bar)
    {
        try
        {
            var c = CanvasCameras(bar) ?? Camera.main;
            if (c == null) return;
            Vector3 sp = RectTransformUtility.WorldToScreenPoint(c, bar.transform.position);
            // Unity 屏幕坐标系原点在左下；Windows 客户区原点在左上 → y 翻转
            int wx = Mathf.RoundToInt(sp.x);
            int wy = Mathf.RoundToInt(Screen.height - sp.y);
            if (wx == _lastLoggedX && wy == _lastLoggedY) return;
            _lastLoggedX = wx;
            _lastLoggedY = wy;
            Plugin.LogV($"[FacilityUI] 选择条屏幕坐标={wx},{wy} 客户区={Screen.width}x{Screen.height}");
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 选择条坐标上报失败: {ex.Message}"); }
    }

    /// <summary>取该 UI 所在 Canvas 的相机（Overlay 画布为 null，用 Camera.main 兜底）</summary>
    private static Camera? CanvasCameras(GameObject bar)
    {
        try
        {
            var canvas = bar.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                return canvas.worldCamera;
        }
        catch { }
        return null;
    }
}
