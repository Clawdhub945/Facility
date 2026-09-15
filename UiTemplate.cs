using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FacilityMod;

/// <summary>
/// **通用 UI 模板** —— 按 <see cref="UiLayout"/> 在建筑窗口里渲染一块自绘内容区。
///
/// ## 设计要点
/// * **与占地尺寸无关**：布局全靠 `RectTransform.anchoredPosition` 手工计算，
///   所以 2×2 / 3×3 / N×N 的建筑都能用同一套模板
/// * **与建筑数量无关**：一座建筑一份 <see cref="UiLayout"/>，互不影响
/// * **静态优先**：`VerticalLayoutGroup` / `HorizontalLayoutGroup` 游戏里没有，
///   所以行位置由模板自上而下累加计算
/// * **按状态签名刷新**：每帧被调用，但只有"内容真的变了"才重排文字/图标，
///   避免每帧重建（性能 + 闪烁）
///
/// ## 可行性依据
/// 运行时构建 uGUI 的能力已由 `UiProbe` 实测验证（新建物体 / SetParent /
/// RectTransform / Image / TextMeshProUGUI 全部成功，游戏稳定）。
/// ⚠ 但**不要**在这里创建带 `SpriteRenderer` 的物体 —— 早前那样做导致游戏静默退出。
///
/// ## 样式
/// 参照游戏自己的面板配色（深色底 + 金色描边 + 暖白文字），
/// 见 `docs/UI模板.md` 的取色记录。
/// </summary>
internal static class UiTemplate
{
    // ---- 名称常量（便于识别与清理）----
    internal const string RootName = "facility_ui_template";
    private const string ViewportName = "viewport";
    private const string ContentName = "content";

    // ---- 尺寸 ----
    private const float PadX = 10f;         // 左右内边距
    private const float PadY = 8f;          // 上下内边距
    private const float LabelW = 76f;       // 左侧标题列宽
    private const float IconSize = 30f;     // 物品图标边长
    private const float IconGap = 8f;       // 图标间距
    private const float BarHeight = 10f;    // 进度条高度

    // ---- 配色（照游戏自己的面板取色）----
    private static readonly Color PanelColor = new Color(0.15f, 0.14f, 0.13f, 0.94f);
    private static readonly Color LineColor = new Color(0.86f, 0.79f, 0.67f, 0.55f);
    private static readonly Color TextColor = new Color(0.92f, 0.88f, 0.78f, 1f);
    private static readonly Color DimColor = new Color(0.68f, 0.65f, 0.58f, 1f);
    private static readonly Color OkColor = new Color(0.55f, 0.86f, 0.55f, 1f);
    private static readonly Color BadColor = new Color(0.95f, 0.55f, 0.45f, 1f);
    private static readonly Color BarBgColor = new Color(0.10f, 0.10f, 0.09f, 1f);
    private static readonly Color BarFgColor = new Color(0.85f, 0.68f, 0.32f, 1f);

    private static GameObject? _root;
    private static RectTransform? _content;
    private static ScrollRect? _scroll;
    private static GameObject? _ownerWindow;          // 模板挂在哪个窗口上
    private static string _lastSig = "";              // 上次渲染的状态签名
    private static float _contentHeight;

    // 每行渲染出来的对象（按行索引存），用于局部刷新
    private static readonly List<GameObject> _rowObjects = new();

    /// <summary>销毁模板（换窗口/关窗时调用）</summary>
    internal static void Destroy()
    {
        try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
        _root = null; _content = null; _scroll = null; _ownerWindow = null;
        _lastSig = ""; _rowObjects.Clear();
    }

    /// <summary>
    /// 渲染（或刷新）模板。每帧调用即可 —— 内部按状态签名去重。
    /// </summary>
    /// <param name="window">要挂到哪个窗口</param>
    /// <param name="spec">建筑规格（提供 UiLayout 与数据来源）</param>
    /// <param name="facility">建筑实例（提供 bag / guid 等运行时数据）</param>
    internal static void Render(GameObject? window, BuildingSpec? spec, Facility? facility)
    {
        try
        {
            if (window == null || spec?.Ui == null || facility == null) return;

            // 换了窗口 → 重建
            if (_root != null && _ownerWindow != window) Destroy();

            if (_root == null)
            {
                if (!Build(window, spec)) return;
                _ownerWindow = window;
            }

            // 状态签名：内容变了才刷新
            string sig = BuildSignature(spec, facility);
            if (sig == _lastSig) return;
            _lastSig = sig;

            Refresh(spec, facility);
        }
        catch (Exception ex) { Plugin.LogError($"[FacilityUI] UI 模板渲染异常: {ex}"); }
    }

    // ==================================================================
    // 构建骨架
    // ==================================================================

    private static bool Build(GameObject window, BuildingSpec spec)
    {
        try
        {
            // 只有第一次需要算尺寸；用窗口自身的 rect 作参考
            float winW = 420f, winH = 300f;
            try
            {
                var wrt = window.GetComponent<RectTransform>();
                if (wrt != null && wrt.rect.width > 100f)
                {
                    winW = wrt.rect.width - 24f;
                    // ⚠ 窗口 rect 可能是**布局前的初始值**（很小甚至 0），
                    //   减出来后就成了负数 —— 踩过：日志里出现「面板 526×-96」。
                    //   所以这里做下限保护，并取绝对值兜底。
                    float h = Mathf.Abs(wrt.rect.height) - 80f;
                    if (h >= 120f) winH = h;
                }
            }
            catch { }

            var root = new GameObject(RootName);
            root.transform.SetParent(window.transform, false);
            var rrt = root.AddComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0.5f, 0.5f);
            rrt.anchorMax = new Vector2(0.5f, 0.5f);
            rrt.pivot = new Vector2(0.5f, 0.5f);
            rrt.anchoredPosition = new Vector2(0f, -18f);
            rrt.sizeDelta = new Vector2(winW, winH);
            _root = root;

            // 面板底
            var bg = new GameObject("panel");
            bg.transform.SetParent(root.transform, false);
            var brt = bg.AddComponent<RectTransform>();
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            var bimg = bg.AddComponent<Image>();
            bimg.color = PanelColor;
            bimg.raycastTarget = true;      // 挡住穿透点击（窗口底层的东西不该被点到）

            // 视口（带 Mask，实现裁剪 + 滚动）
            var vp = new GameObject(ViewportName);
            vp.transform.SetParent(root.transform, false);
            var vrt = vp.AddComponent<RectTransform>();
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(PadX, PadY);
            vrt.offsetMax = new Vector2(-PadX, -PadY);
            var vimg = vp.AddComponent<Image>();
            vimg.color = new Color(0f, 0f, 0f, 0.004f);   // 几乎全透明，但作为 Mask 需要非 null Image
            vp.AddComponent<Mask>().showMaskGraphic = false;

            // 内容容器（自上而下排）
            var content = new GameObject(ContentName);
            content.transform.SetParent(vp.transform, false);
            var crt = content.AddComponent<RectTransform>();
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = Vector2.zero;
            crt.sizeDelta = new Vector2(0f, 10f);
            _content = crt;

            // ScrollRect（纵向）
            var sr = root.AddComponent<ScrollRect>();
            sr.content = crt;
            sr.viewport = vrt;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 30f;
            _scroll = sr;

            Plugin.LogV($"[FacilityUI] UI 模板已构建：窗口 {window.name}，" +
                        $"面板 {winW:0}×{winH:0}（建筑「{spec.Name}」）");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] UI 模板构建失败: {ex}");
            Destroy();
            return false;
        }
    }

    // ==================================================================
    // 刷新内容
    // ==================================================================

    /// <summary>状态签名：任何会影响显示的数据变化都要体现在这里</summary>
    private static string BuildSignature(BuildingSpec spec, Facility facility)
    {
        var sb = new StringBuilder();
        sb.Append(spec.StuffId).Append('|');

        foreach (var row in spec.Ui!.Rows)
        {
            switch (row)
            {
                case UiItemRow item:
                    if (item.Source == UiItemSource.BuildingBag)
                        AppendBagSignature(sb, facility);
                    else
                        AppendRecipeSignature(sb, spec);
                    break;
                case UiStatusRow st:
                    sb.Append(StatusText(st, facility)).Append('|');
                    break;
                case UiProgressRow pr:
                    sb.Append(ProgressRatio(pr, spec, facility).ToString("0.00")).Append('|');
                    break;
                case UiLabelRow lb:
                    sb.Append(lb.Text).Append('|');
                    break;
            }
        }
        return sb.ToString();
    }

    private static void AppendBagSignature(StringBuilder sb, Facility f)
    {
        var dic = BagOf(f);
        if (dic == null) { sb.Append("nobag|"); return; }
        var keys = new List<int>();
        foreach (var k in dic.Keys) if (k is int id) keys.Add(id);
        keys.Sort();
        foreach (var id in keys)
        {
            object? v = dic[id];
            sb.Append(id).Append(':').Append(v).Append(',');
        }
        sb.Append('|');
    }

    private static void AppendRecipeSignature(StringBuilder sb, BuildingSpec spec)
    {
        var r = spec.Smelting;
        if (r == null) { sb.Append("norecipe|"); return; }
        foreach (var (id, n) in r.Inputs) sb.Append(id).Append('x').Append(n).Append(',');
        sb.Append("fuel").Append(r.FuelId).Append('x').Append(r.FuelPerBatch)
          .Append("out").Append(r.OutputId).Append('x').Append(r.OutputCount).Append('|');
    }

    private static void Refresh(BuildingSpec spec, Facility facility)
    {
        if (_content == null) return;

        // 清掉旧行
        foreach (var go in _rowObjects) { try { if (go != null) UnityEngine.Object.Destroy(go); } catch { } }
        _rowObjects.Clear();

        float y = 0f;
        int idx = 0;
        foreach (var row in spec.Ui!.Rows)
        {
            var rowGo = RenderRow(row, idx++, spec, facility, y);
            if (rowGo != null) _rowObjects.Add(rowGo);
            y += row.Height;
        }

        _contentHeight = y + 4f;
        try
        {
            _content.sizeDelta = new Vector2(0f, _contentHeight);
            _content.anchoredPosition = Vector2.zero;
        }
        catch { }
    }

    /// <summary>渲染一行，返回它的根物体</summary>
    private static GameObject? RenderRow(UiRow row, int index, BuildingSpec spec,
                                        Facility facility, float yOffset)
    {
        try
        {
            float rowW = 0f;
            try { rowW = _content != null ? _content.rect.width : 380f; } catch { }
            if (rowW < 50f) rowW = 380f;

            var go = new GameObject($"row_{index}_{row.GetType().Name}");
            go.transform.SetParent(_content!.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -yOffset);
            rt.sizeDelta = new Vector2(0f, row.Height);

            // 行标题（左侧固定列）
            float x = 0f;
            if (!string.IsNullOrEmpty(row.Label))
            {
                AddText(go.transform, "label", row.Label, LabelW, row.Height - 4f,
                        x, -2f, DimColor, TextAlignmentOptions.Left);
                x += LabelW;
            }

            float avail = Mathf.Max(60f, rowW - x);

            switch (row)
            {
                case UiLabelRow lb:
                    AddText(go.transform, "text", lb.Text, avail, row.Height - 4f, x, -2f,
                            lb.Secondary ? DimColor : TextColor, TextAlignmentOptions.Left);
                    break;

                case UiStatusRow st:
                {
                    string txt = StatusText(st, facility);
                    bool ok = (st.Source == UiStatusSource.SmelterBlockReason)
                              ? string.IsNullOrEmpty(SmelterConsumer.BlockReason(SafeGuid(facility)))
                              : true;
                    AddText(go.transform, "text", txt, avail, row.Height - 4f, x, -2f,
                            ok ? OkColor : BadColor, TextAlignmentOptions.Left);
                    break;
                }

                case UiItemRow item:
                    RenderItemRow(go.transform, item, spec, facility, x, avail);
                    break;

                case UiProgressRow pr:
                    RenderProgressRow(go.transform, pr, spec, facility, x, avail, row.Height);
                    break;
            }
            return go;
        }
        catch (Exception ex)
        {
            Plugin.LogV($"[FacilityUI] 渲染行失败（{row.GetType().Name}）: {ex.Message}");
            return null;
        }
    }

    /// <summary>物品行：图标 + 数量，横向排列，超出宽度换行（每行图标数按可用宽度算）</summary>
    private static void RenderItemRow(Transform parent, UiItemRow item, BuildingSpec spec,
                                      Facility facility, float x0, float avail)
    {
        var entries = new List<(int id, int count)>();

        if (item.Source == UiItemSource.BuildingBag)
        {
            var dic = BagOf(facility);
            if (dic != null)
            {
                foreach (var k in dic.Keys)
                {
                    if (k is not int id) continue;
                    int c = 0;
                    try { c = System.Convert.ToInt32(dic[k] ?? 0); } catch { }
                    if (c > 0) entries.Add((id, c));
                }
                entries.Sort((a, b) => b.count.CompareTo(a.count));
            }
        }
        else
        {
            var r = spec.Smelting;
            if (r != null)
            {
                foreach (var (id, n) in r.Inputs) entries.Add((id, n));
                if (r.FuelId != 0 && r.FuelPerBatch > 0) entries.Add((r.FuelId, r.FuelPerBatch));
                if (r.OutputId != 0) entries.Add((r.OutputId, r.OutputCount));
            }
        }

        if (item.ExcludeIds.Length > 0)
            entries.RemoveAll(e => Array.IndexOf(item.ExcludeIds, e.id) >= 0);

        if (entries.Count == 0)
        {
            AddText(parent, "empty", "（空）", avail, 20f, x0, -2f, DimColor, TextAlignmentOptions.Left);
            return;
        }

        int perRow = Mathf.Max(1, Mathf.FloorToInt(avail / (IconSize + IconGap)));
        int shown = Mathf.Min(entries.Count, item.MaxItems);

        for (int i = 0; i < shown; i++)
        {
            var (id, count) = entries[i];
            int col = i % perRow, line = i / perRow;
            float px = x0 + col * (IconSize + IconGap);
            float py = -2f - line * (IconSize + 14f);
            AddItemCell(parent, id, count, px, py);
        }

        if (entries.Count > shown)
        {
            int col2 = shown % perRow, line2 = shown / perRow;
            AddText(parent, "more", $"+{entries.Count - shown}",
                    IconSize + 6f, 20f,
                    x0 + col2 * (IconSize + IconGap), -2f - line2 * (IconSize + 14f) - 4f,
                    DimColor, TextAlignmentOptions.Left);
        }
    }

    /// <summary>一个物品格：图标 + 右下角数量</summary>
    private static void AddItemCell(Transform parent, int stuffId, int count, float x, float y)
    {
        try
        {
            var cell = new GameObject($"item_{stuffId}");
            cell.transform.SetParent(parent, false);
            var rt = cell.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(IconSize, IconSize);

            var sprite = ResolveStuffIcon(stuffId);
            if (sprite != null)
            {
                var img = cell.AddComponent<Image>();
                img.sprite = sprite;
                img.raycastTarget = false;
                img.preserveAspect = true;
            }
            else
            {
                // 图标取不到 → 画个占位块，至少不空白
                var img = cell.AddComponent<Image>();
                img.color = new Color(0.3f, 0.3f, 0.3f, 0.8f);
                img.raycastTarget = false;
            }

            // 数量（右下角，带描边色以便在图标上可读）
            AddText(cell.transform, "num", count.ToString(),
                    IconSize + 4f, 14f, IconSize * 0.35f, -(IconSize - 12f),
                    TextColor, TextAlignmentOptions.Right, outline: true);
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 物品格失败 {stuffId}: {ex.Message}"); }
    }

    /// <summary>进度行：自绘进度条 + 右侧数值</summary>
    private static void RenderProgressRow(Transform parent, UiProgressRow pr, BuildingSpec spec,
                                          Facility facility, float x0, float avail, float rowH)
    {
        float ratio = ProgressRatio(pr, spec, facility);
        float barW = pr.ShowValueText ? Mathf.Max(40f, avail - 64f) : avail;
        float barY = -(rowH - BarHeight) * 0.5f;

        // 底槽
        var bg = new GameObject("bar_bg");
        bg.transform.SetParent(parent, false);
        var brt = bg.AddComponent<RectTransform>();
        brt.anchorMin = new Vector2(0f, 1f); brt.anchorMax = new Vector2(0f, 1f);
        brt.pivot = new Vector2(0f, 1f);
        brt.anchoredPosition = new Vector2(x0, barY);
        brt.sizeDelta = new Vector2(barW, BarHeight);
        var bimg = bg.AddComponent<Image>();
        bimg.color = BarBgColor; bimg.raycastTarget = false;

        // 进度
        var fg = new GameObject("bar_fg");
        fg.transform.SetParent(bg.transform, false);
        var frt = fg.AddComponent<RectTransform>();
        frt.anchorMin = new Vector2(0f, 0f); frt.anchorMax = new Vector2(0f, 1f);
        frt.pivot = new Vector2(0f, 0.5f);
        frt.anchoredPosition = Vector2.zero;
        frt.sizeDelta = new Vector2(barW * Mathf.Clamp01(ratio), 0f);
        var fimg = fg.AddComponent<Image>();
        fimg.color = BarFgColor; fimg.raycastTarget = false;

        if (pr.ShowValueText)
            AddText(parent, "val", $"{Mathf.RoundToInt(ratio * 100f)}%", 60f, rowH - 4f,
                    x0 + barW + 4f, -2f, TextColor, TextAlignmentOptions.Left);
    }

    // ==================================================================
    // 数据取值
    // ==================================================================

    private static string StatusText(UiStatusRow st, Facility facility)
    {
        if (st.Source == UiStatusSource.Static) return st.StaticText;
        string reason = SmelterConsumer.BlockReason(SafeGuid(facility));
        return string.IsNullOrEmpty(reason) ? st.OkText : $"{st.FailText}：{reason}";
    }

    private static float ProgressRatio(UiProgressRow pr, BuildingSpec spec, Facility facility)
    {
        switch (pr.Source)
        {
            case UiProgressSource.Static:
                return Mathf.Clamp01(pr.StaticRatio);

            case UiProgressSource.WorkedToday:
                return SmelterConsumer.WorkedToday(SafeGuid(facility)) ? 1f : 0f;

            case UiProgressSource.FuelRatio:
            {
                var r = spec.Smelting;
                if (r == null || r.FuelId == 0 || r.FuelPerBatch <= 0) return 0f;
                int have = CountInBag(facility, r.FuelId);
                // 显示"够烧几天"：按 10 批封顶，避免条永远满
                return Mathf.Clamp01(have / (float)(r.FuelPerBatch * 10));
            }
        }
        return 0f;
    }

    private static System.Collections.IDictionary? BagOf(Facility f)
    {
        try { return SmelterConsumer.BagDictionaryOf(f); } catch { return null; }
    }

    private static int CountInBag(Facility f, int stuffId)
    {
        var dic = BagOf(f);
        if (dic == null) return 0;
        try { return dic.Contains(stuffId) ? System.Convert.ToInt32(dic[stuffId] ?? 0) : 0; }
        catch { return 0; }
    }

    private static int SafeGuid(Facility f)
    {
        try { return f.guid; } catch { return 0; }
    }

    /// <summary>
    /// 取物品图标 sprite：`物品id` → 游戏的 `stuff_dic` → `stuff_img`（如 `ui_603010`）
    /// → `SpriteManager.Get(name)`。取不到返回 null（调用方画占位块）。
    /// </summary>
    private static Sprite? ResolveStuffIcon(int stuffId)
    {
        if (_iconCache.TryGetValue(stuffId, out var cached)) return cached;
        Sprite? sp = null;
        try
        {
            var dic = D.Ins.stuff_dic;
            if (dic != null && dic.ContainsKey(stuffId))
            {
                var info = dic[stuffId];
                string? name = null;
                try { name = info.stuff_img; } catch { }
                if (!string.IsNullOrEmpty(name)) sp = SpriteManager.Get(name);
            }
        }
        catch { }
        _iconCache[stuffId] = sp;
        return sp;
    }

    private static readonly Dictionary<int, Sprite?> _iconCache = new();

    // ==================================================================
    // 基础控件
    // ==================================================================

    private static void AddText(Transform parent, string name, string text,
                                float w, float h, float x, float y,
                                Color color, TextAlignmentOptions align, bool outline = false)
    {
        try
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text ?? "";
            tmp.fontSize = 13f;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            if (outline)
            {
                // 数量叠在图标上，加一层描边保证可读
                var ol = go.AddComponent<Shadow>();
                ol.effectColor = new Color(0f, 0f, 0f, 0.9f);
                ol.effectDistance = new Vector2(1f, -1f);
            }
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 加文本失败({name}): {ex.Message}"); }
    }
}
