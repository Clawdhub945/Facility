using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FacilityMod;

/// <summary>
/// **通用 UI 模板渲染器（v2）** —— 按 <see cref="UiLayout"/> 用 <see cref="UiKit"/> 画界面。
///
/// ## 与 v1 的区别（v1 已删除）
/// v1 把控件建在**游戏窗口内部**，而窗口根的 `rect` 是 **550×0**
/// （游戏 UI 是自绘批渲染的，uGUI 布局从不运行）→ 尺寸永远算成 0，怎么调都看不见。
/// v2 用**独立 Canvas**（`ScreenSpaceOverlay` + 高 `sortingOrder`），
/// `rect` 就是屏幕尺寸，布局完全可用 —— 实测面板/文字/图标全部正常渲染。
///
/// ## 设计目标（用户要求）
/// 「无论是 2×2、3×3，还是 N×N，UI 来一个全新模板，可以被后续多个建筑来自定义 UI 里的内容」
/// → 建筑只在 <see cref="BuildingSpec.Ui"/> 里描述"显示什么"，本类负责"怎么画"。
///   **加建筑不用改 UI 代码**。
///
/// ## 布局策略
/// 游戏里**没有** `VerticalLayoutGroup` / `HorizontalLayoutGroup`（实测），
/// 所以行位置由本类**自上而下手工累加**（每行的 `Height` 由行类型决定）。
/// 行宽固定 `Width`，与建筑占地尺寸无关 —— 所以 2×2 / 3×3 / N×N 共用一套模板。
/// </summary>
internal static class UiTemplate
{
    private static GameObject? _canvas;
    private static RectTransform? _panel;
    private static BuildingSpec? _current;     // 正在渲染哪座建筑
    private static string _lastSig = "";
    private static readonly List<GameObject> _rows = new();

    // ---- 布局常量（集中在此，便于调整）----
    private const float Width = 360f;          // 面板宽（像素）
    private const float PadX = 12f;
    private const float PadY = 10f;
    private const float LabelW = 74f;
    private const float IconSize = 30f;
    private const float IconGap = 8f;

    // ---- 配色（照游戏面板取色）----
    private static readonly Color PanelColor = new Color(0.11f, 0.105f, 0.10f, 0.93f);
    private static readonly Color TextColor = new Color(0.92f, 0.88f, 0.78f, 1f);
    private static readonly Color DimColor = new Color(0.66f, 0.63f, 0.56f, 1f);
    private static readonly Color OkColor = new Color(0.55f, 0.86f, 0.55f, 1f);
    private static readonly Color BadColor = new Color(0.95f, 0.55f, 0.45f, 1f);
    private static readonly Color BarBgColor = new Color(0.06f, 0.06f, 0.05f, 1f);
    private static readonly Color BarFgColor = new Color(0.85f, 0.68f, 0.32f, 1f);

    /// <summary>渲染/刷新模板。每帧调用即可（内部按状态签名去重）。</summary>
    internal static void Render(BuildingSpec? spec, Facility? facility)
    {
        try
        {
            // 总开关：关掉后完全不创建（隔离测试用 —— 判断操作被挡是不是我们的 UI 造成的）
            if (Plugin.UiEnabledEntry?.Value == false) { Destroy(); return; }

            if (spec?.Ui == null || facility == null) { Destroy(); return; }
            if (!facility.gameObject.activeInHierarchy) { Destroy(); return; }

            var canvas = UiKit.EnsureRoot();
            if (canvas == null) return;

            if (_canvas != canvas || _panel == null || _current != spec)
            {
                DestroyPanel();
                _panel = UiKit.AddPanel(canvas.GetComponent<RectTransform>(), "facility_ui_panel",
                                        PanelColor, Vector2.zero, new Vector2(Width, 200f));
                if (_panel == null) return;
                _canvas = canvas; _current = spec; _lastSig = "";
                Plugin.LogV($"[FacilityUI] 模板：为建筑「{spec.Name}」建面板");
            }

            // 面板位置（cfg 可调，热生效）—— 避免和游戏窗口重叠
            float px = Plugin.UiPanelXEntry?.Value ?? 420f;
            float py = Plugin.UiPanelYEntry?.Value ?? 0f;
            if (_panel.anchoredPosition != new Vector2(px, py))
            {
                _panel.anchoredPosition = new Vector2(px, py);
                _lastSig = "";       // 位置变了触发一次重排（日志）
            }

            UiKit.SyncVisibility();

            string sig = Signature(spec, facility);
            if (sig == _lastSig) return;
            _lastSig = sig;
            Rebuild(spec, facility);
        }
        catch (Exception ex) { Plugin.LogError($"[FacilityUI] 模板渲染异常: {ex}"); }
    }

    /// <summary>销毁面板（建筑窗口关闭时调用）</summary>
    internal static void Destroy()
    {
        DestroyPanel();
        _canvas = null; _current = null; _lastSig = "";
    }

    private static void DestroyPanel()
    {
        foreach (var go in _rows) { try { if (go != null) UnityEngine.Object.Destroy(go); } catch { } }
        _rows.Clear();
        if (_panel != null) { try { UnityEngine.Object.Destroy(_panel.gameObject); } catch { } }
        _panel = null;
    }

    // ==================================================================
    // 内容
    // ==================================================================

    private static void Rebuild(BuildingSpec spec, Facility facility)
    {
        if (_panel == null) return;
        foreach (var go in _rows) { try { if (go != null) UnityEngine.Object.Destroy(go); } catch { } }
        _rows.Clear();

        float y = -PadY;                       // 从顶部往下累加
        foreach (var row in spec.Ui!.Rows)
        {
            var go = RenderRow(row, spec, facility, y);
            if (go != null) _rows.Add(go);
            y -= row.Height;
        }
        float total = -y + PadY;

        _panel.sizeDelta = new Vector2(Width, total);   // 面板高度自适应
        Plugin.LogV($"[FacilityUI] 模板已刷新：{spec.Ui.Rows.Count} 行，面板 {Width:0}×{total:0}");
    }

    private static GameObject? RenderRow(UiRow row, BuildingSpec spec, Facility facility, float topY)
    {
        try
        {
            var go = new GameObject("row_" + row.GetType().Name);
            go.layer = UiKit.UiLayer;
            go.transform.SetParent(_panel!, false);
            var rt = go.AddComponent<RectTransform>();
            // 行的锚点：面板左上角
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(0f, topY);
            rt.sizeDelta = new Vector2(Width, row.Height);

            float x = PadX;                    // 行内从左开始（行自身左上角为原点）
            float avail = Width - PadX * 2f;

            if (!string.IsNullOrEmpty(row.Label))
            {
                AddText(rt, "label", row.Label, x, 0f, LabelW, row.Height, DimColor);
                x += LabelW;
                avail -= LabelW;
            }

            switch (row)
            {
                case UiLabelRow lb:
                    AddText(rt, "text", lb.Text, x, 0f, avail, row.Height,
                            lb.Secondary ? DimColor : TextColor);
                    break;

                case UiStatusRow st:
                {
                    string txt = StatusText(st, facility);
                    bool ok = st.Source != UiStatusSource.SmelterBlockReason
                              || string.IsNullOrEmpty(SmelterConsumer.BlockReason(SafeGuid(facility)));
                    AddText(rt, "text", txt, x, 0f, avail, row.Height, ok ? OkColor : BadColor);
                    break;
                }

                case UiItemRow item:
                    RenderItems(rt, item, spec, facility, x, avail);
                    break;

                case UiProgressRow pr:
                    RenderProgress(rt, pr, spec, facility, x, avail, row.Height);
                    break;
            }
            return go;
        }
        catch (Exception ex)
        {
            Plugin.LogV($"[FacilityUI] 渲染行失败({row.GetType().Name}): {ex.Message}");
            return null;
        }
    }

    /// <summary>物品行：图标 + 数量（横向排列；放不下就截断并提示 +N）</summary>
    private static void RenderItems(RectTransform parent, UiItemRow item, BuildingSpec spec,
                                    Facility facility, float x0, float avail)
    {
        var entries = new List<(int id, int count)>();

        if (item.Source == UiItemSource.BuildingBag)
        {
            var dic = SmelterConsumer.BagDictionaryOf(facility);
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
            AddText(parent, "empty", "（空）", x0, 0f, avail, 20f, DimColor);
            return;
        }

        int perRow = Mathf.Max(1, Mathf.FloorToInt(avail / (IconSize + IconGap)));
        int shown = Mathf.Min(entries.Count, item.MaxItems);

        for (int i = 0; i < shown; i++)
        {
            var (id, count) = entries[i];
            int col = i % perRow, line = i / perRow;
            float px = x0 + col * (IconSize + IconGap) + IconSize * 0.5f;
            float py = -(line * (IconSize + 6f) + IconSize * 0.5f + 4f);

            UiKit.AddIcon(parent, $"item_{id}", $"ui_{id}",
                          new Vector2(px, py), IconSize, new Vector2(0f, 1f));
            AddText(parent, $"num_{id}", count.ToString(),
                    px + IconSize * 0.30f, py - IconSize * 0.32f, 40f, 14f, TextColor);
        }

        if (entries.Count > shown)
        {
            int col2 = shown % perRow, line2 = shown / perRow;
            AddText(parent, "more", $"+{entries.Count - shown}",
                    x0 + col2 * (IconSize + IconGap),
                    -(line2 * (IconSize + 6f) + 8f), 40f, 16f, DimColor);
        }
    }

    /// <summary>进度行：底槽 + 填充 + 百分比</summary>
    private static void RenderProgress(RectTransform parent, UiProgressRow pr, BuildingSpec spec,
                                       Facility facility, float x0, float avail, float rowH)
    {
        float ratio = ProgressRatio(pr, spec, facility);
        float barW = pr.ShowValueText ? Mathf.Max(40f, avail - 56f) : avail;
        float barY = -(rowH * 0.5f);
        const float barH = 12f;

        UiKit.AddPanel(parent, "bar_bg", BarBgColor,
                       new Vector2(x0 + barW * 0.5f, barY), new Vector2(barW, barH),
                       new Vector2(0f, 1f));

        if (ratio > 0.001f)
            UiKit.AddPanel(parent, "bar_fg", BarFgColor,
                           new Vector2(x0 + barW * ratio * 0.5f, barY),
                           new Vector2(barW * ratio, barH), new Vector2(0f, 1f));

        if (pr.ShowValueText)
            AddText(parent, "val", $"{Mathf.RoundToInt(ratio * 100f)}%",
                    x0 + barW + 4f, -8f, 52f, 18f, TextColor);
    }

    /// <summary>
    /// 在行内加一段文字。
    /// 行的锚点是左上 `(0,1)`，所以子控件也用左上锚点；
    /// `topOffset` 是"距行顶部的距离"（向下为正），内部换算成中心锚点。
    /// </summary>
    private static void AddText(RectTransform parent, string name, string text,
                                float x, float topOffset, float w, float h, Color color)
    {
        UiKit.AddText(parent, name, text,
                      new Vector2(x + w * 0.5f, -(topOffset + h * 0.5f)),
                      new Vector2(w, h), 13f, TextAlignmentOptions.Left, color,
                      new Vector2(0f, 1f));
    }

    // ==================================================================
    // 取值 / 状态签名
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
                return Mathf.Clamp01(have / (float)(r.FuelPerBatch * 10));
            }
        }
        return 0f;
    }

    private static int CountInBag(Facility f, int stuffId)
    {
        var dic = SmelterConsumer.BagDictionaryOf(f);
        if (dic == null) return 0;
        try { return dic.Contains(stuffId) ? System.Convert.ToInt32(dic[stuffId] ?? 0) : 0; }
        catch { return 0; }
    }

    private static int SafeGuid(Facility f)
    {
        try { return f.guid; } catch { return 0; }
    }

    /// <summary>状态签名：任何影响显示的数据变化都要体现在这里（避免每帧重建）</summary>
    private static string Signature(BuildingSpec spec, Facility facility)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(spec.StuffId).Append('|');
        foreach (var row in spec.Ui!.Rows)
        {
            switch (row)
            {
                case UiItemRow item when item.Source == UiItemSource.BuildingBag:
                {
                    var dic = SmelterConsumer.BagDictionaryOf(facility);
                    if (dic == null) { sb.Append("nobag,"); break; }
                    var keys = new List<int>();
                    foreach (var k in dic.Keys) if (k is int id) keys.Add(id);
                    keys.Sort();
                    foreach (var id in keys) sb.Append(id).Append(':').Append(dic[id]).Append(',');
                    break;
                }
                case UiStatusRow st:
                    sb.Append(StatusText(st, facility)).Append('|');
                    break;
                case UiProgressRow pr:
                    sb.Append(ProgressRatio(pr, spec, facility).ToString("0.00")).Append('|');
                    break;
            }
        }
        return sb.ToString();
    }
}
