using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace FacilityMod;

/// <summary>
/// 综合生产所的窗口 UI 定制（由 FacilityWindowUiPatch 在窗口 SetInfo/刷新后调用）：
/// 1. 改写「附近森林覆盖率」文案（本建筑产出与森林无关）→ 改为产出说明
/// 2. 在文案下方注入一个原生 uGUI 选择条：点击循环切换「额外产品」（如铁矿），
///    选择写入 cfg（换日产出时并入每人产出，热生效）
/// </summary>
internal static class FacilityWindowUi
{
    private const string SelectorName = "FacilityExtraProductSelector";
    private static Font? _font;

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

    /// <summary>窗口每次打开/刷新后调用：确保选择条存在并就位，改写文案</summary>
    internal static void Apply(GameObject window)
    {
        if (window == null) return;
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
        barRt.sizeDelta = new Vector2(240f, 28f);
    }

    internal static void RewriteTexts(GameObject window)
    {
        foreach (var t in window.GetComponentsInChildren<TMPro.TMP_Text>(true))
        {
            if (t == null) continue;
            if (t.name == "txt_forest_coverage_rate")
                t.text = "综合生产所：工人每日自动产出（数量可在配置调整）";
            else if (t.name == "txt_tip" && !string.IsNullOrEmpty(t.text) && t.text.Contains("森林覆盖率"))
                t.text = "工人越多，每日产出越多；点击下方选择条可添加额外产品";
        }
    }

    private static TMPro.TMP_Text FindText(GameObject window, string name)
    {
        foreach (var t in window.GetComponentsInChildren<TMPro.TMP_Text>(true))
            if (t != null && t.name == name) return t;
        return null;
    }

    // ---------- 选择条（点击循环切换） ----------

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
        var label = bar.GetComponentInChildren<Text>();
        if (label == null) return;
        int extra = Plugin.ExtraProduct;
        string name = "";
        foreach (var (id, nm) in Plugin.ParseExtraCandidates())
            if (id == extra) { name = nm; break; }
        label.text = extra == 0
            ? "额外产品：无（点击选择）"
            : $"额外产品：{name} +{Plugin.ExtraPerDay}/日 ▶";
    }

    private static GameObject? BuildSelector(GameObject window)
    {
        var font = UiFont;
        var go = new GameObject(SelectorName);
        go.transform.SetParent(window.transform, false);
        go.AddComponent<RectTransform>();
        var img = go.AddComponent<Image>();
        img.color = new Color(0.18f, 0.17f, 0.15f, 0.97f);

        var label = CreateText(go.transform, "Label", font, 14, TextAnchor.MiddleLeft);
        var lr = label.GetComponent<RectTransform>();
        lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
        lr.offsetMin = new Vector2(8, 1); lr.offsetMax = new Vector2(-8, -1);

        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        btn.onClick.AddListener(DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(
            new Action(() => OnSelectorClick(go))));
        return go;
    }

    /// <summary>点击循环：无 → 候选1 → 候选2 → … → 无；写入 cfg 并刷新文字</summary>
    private static void OnSelectorClick(GameObject bar)
    {
        try
        {
            var cands = Plugin.ParseExtraCandidates();
            if (cands.Count == 0) return;
            int cur = Plugin.ExtraProduct;
            int idx = -1;
            for (int i = 0; i < cands.Count; i++)
                if (cands[i].id == cur) { idx = i; break; }
            int next = idx + 1;                       // -1 → 0（从"无"切到第一个候选）
            int newId = next >= cands.Count ? 0 : cands[next].id;
            Plugin.SetExtraProduct(newId);
            RefreshLabel(bar);
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 选择条点击失败: {ex.Message}"); }
    }

    private static Text CreateText(Transform parent, string name, Font? font, int size, TextAnchor anchor)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        var t = go.AddComponent<Text>();
        if (font != null) t.font = font;
        t.fontSize = size;
        t.color = new Color(0.92f, 0.90f, 0.86f, 1f);
        t.alignment = anchor;
        t.raycastTarget = false;   // 不挡点击，让 Button 收到
        return t;
    }
}
