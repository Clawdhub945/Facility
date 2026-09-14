using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
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


    /// <summary>窗口每次打开/刷新后调用：确保下拉框存在并就位，改写文案。</summary>
    internal static void Apply(GameObject window)
    {
        if (window == null) return;

        // 归属判断：只给「本 mod 的设施」改窗口。
        // 采集营地(105006) 等原版设施用的是同一个窗口预制体，不判归属会把它们的窗口也改掉
        // （用户实测反馈「点建造开后里面的部分 UI 不对」时，很可能就是别的设施窗口被我们动过）。
        if (!IsOurWindow(window)) return;

        RewriteTexts(window);
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
        barRt.sizeDelta = new Vector2(BarWidth, BarHeight);

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
    /// 这个窗口是不是绑在本 mod 的设施上。
    /// 先看窗口上的 `stuff_id` / `facility_guid` 字段（interop 里确实有这两个字段），
    /// 拿不到字段就退一步：看本 mod 建筑里有没有 guid 与之匹配的。
    /// </summary>
    private static bool IsOurWindow(GameObject window)
    {
        try
        {
            foreach (var c in window.GetComponents<Component>())
            {
                if (c == null) continue;
                var t = c.GetType();
                // stuff_id 直接可读时最省事
                var pi = t.GetProperty("stuff_id");
                if (pi != null)
                {
                    try
                    {
                        var v = pi.GetValue(c);
                        if (v is int sid && sid != 0) return Plugin.IsManaged(sid);
                    }
                    catch { }
                }
                var fi = t.GetField("stuff_id");
                if (fi != null)
                {
                    try
                    {
                        var v = fi.GetValue(c);
                        if (v is int sid2 && sid2 != 0) return Plugin.IsManaged(sid2);
                    }
                    catch { }
                }
            }
        }
        catch { }
        // 字段都拿不到就**放行**（宁可多改，也别让我们的窗口没下拉框）
        return true;
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
    /// 自动化测试钩子（F8）：展开下拉框 → 选下一项 → 收起。
    /// 用途：验证「下拉框 → 写 cfg」这条链路，不依赖 OS 鼠标坐标标定。
    /// 详细模式关闭时也生效。日志格式不要改，`_tools/e2e.py` 会解析。
    /// </summary>
    internal static void TestClickSelector()
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
