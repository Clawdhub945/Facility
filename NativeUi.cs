using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FacilityMod;

/// <summary>
/// 原生窗口控件助手：按**字段名**从游戏的窗口对象上取控件。
///
/// ## 为什么这么做
/// 游戏窗口的每个控件都是类里的 `[SerializeField]` 字段（例如
/// `WindowWorkshop.dp_blueprint`、`WindowWorkFacility.num_adjust`）。
/// 反编译语料库（`C:\AI\游戏向量库\code_meta.json`）与
/// UnityExplorer 层级快照都能查到这些字段名，所以**按名字取控件**是通用做法：
/// 换任何窗口预制体都适用，不需要知道它的层级路径。
///
/// ## 设计成通用的原因
/// 用户的目标是「后续还要加更多建筑、定制各类 UI」——
/// 所以这里不做一次性硬编码，而是提供
/// `Find(window, "字段名")` + `FindComponent&lt;T&gt;(window, "字段名")` 两个入口，
/// 新建筑只需要在自己的配置表里写「要哪个控件、填什么数据」。
/// </summary>
internal static class NativeUi
{
    /// <summary>
    /// 从窗口对象上按字段名取值。
    ///
    /// ⚠ `byNameFallback` 默认 **false**：
    /// 按「物体名」搜整个层级会**误伤原版窗口** —— 我们的建筑(105050) 和制造台(105010)
    /// 共用 `window_blacksmith`，制造台窗口里也有 `res_grid` / `my_progress_make` /
    /// `num_adjust_of_materials_access_range` 这些同名物体，一旦按名字搜到就会把它们隐藏，
    /// 用户实测：制造台的**材料需求图标、「进度 0%」、「材料取用范围 50」的数字全部消失**。
    ///
    /// 所以默认只走**正规路径**（反射窗口自身的 `[SerializeField]` 字段，只命中该窗口类型
    /// 真正持有的控件）；只有明确知道自己在做什么（如诊断）才开 fallback。
    /// </summary>
    internal static object? FindRaw(GameObject window, string fieldName, bool byNameFallback = false)
    {
        if (window == null || string.IsNullOrEmpty(fieldName)) return null;

        // ① 从窗口自身的 MonoBehaviour 上按字段名反射（游戏的正规做法，最安全）
        try
        {
            foreach (var c in window.GetComponents<Component>())
            {
                if (c == null) continue;
                object? v = GetMember(c, fieldName);
                if (v != null) return v;
            }
        }
        catch { }

        // ② 可选兜底：按**物体名**找（默认关闭，见上面的警告）
        if (byNameFallback)
        {
            try
            {
                foreach (var t in window.GetComponentsInChildren<Transform>(true))
                {
                    if (t != null && t.name == fieldName) return t.gameObject;
                }
            }
            catch { }
        }

        return null;
    }

    /// <summary>按字段名取指定类型的控件（取不到返回 null）</summary>
    internal static T? Find<T>(GameObject window, string fieldName, bool byNameFallback = false)
        where T : Component
    {
        try
        {
            var v = FindRaw(window, fieldName, byNameFallback);
            switch (v)
            {
                case T hit:
                    return hit;
                case GameObject go:
                    return go.GetComponent<T>();
                case Component comp:
                    return comp.GetComponent<T>();
            }
        }
        catch { }
        return null;
    }

    /// <summary>按字段名取控件所在的 GameObject</summary>
    internal static GameObject? FindGo(GameObject window, string fieldName, bool byNameFallback = false)
    {
        try
        {
            var v = FindRaw(window, fieldName, byNameFallback);
            return v switch
            {
                GameObject go => go,
                Component comp => comp.gameObject,
                _ => null,
            };
        }
        catch { }
        return null;
    }

    /// <summary>字段或属性取值（IL2CPP 的属性名可能带下划线，做几种兜底）</summary>
    private static object? GetMember(object target, string name)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var t = target.GetType();

        var fi = t.GetField(name, F) ?? t.GetField(name + "_", F);
        if (fi != null)
        {
            try { return fi.GetValue(target); } catch { }
        }

        var pi = t.GetProperty(name, F) ?? t.GetProperty(name + "_", F);
        if (pi != null && pi.CanRead)
        {
            try { return pi.GetValue(target); } catch { }
        }

        return null;
    }

    /// <summary>隐藏一个控件（连带它的物体）</summary>
    internal static bool Hide(GameObject window, string fieldName, bool byNameFallback = false)
    {
        var go = FindGo(window, fieldName, byNameFallback);
        if (go == null) return false;
        try { if (go.activeSelf) go.SetActive(false); return true; }
        catch { return false; }
    }

    /// <summary>
    /// 批量隐藏。**两阶段**：
    ///   ① 先按窗口类的 `[SerializeField]` 字段精确命中（最安全）
    ///   ② 剩下的再在**本窗口子树内**按物体名找（用于 `res_grid` 里的
    ///      「可使用的材料:」、`my_progress_make` 的「建造 100%」这类**嵌套**物体 ——
    ///      它们不是窗口类的直接字段，精确反射找不到）。
    ///
    /// ⚠ **只能在已确认「这个窗口属于本 mod 建筑」之后调用**（调用点已判）。
    /// 早期版本把第 ② 步做成全局按名搜索，结果把**原版制造台**窗口里的同名物体
    /// （`res_grid` / `my_progress_make` / `num_adjust_of_materials_access_range`）
    /// 也隐藏了 —— 用户实测截图：制造台的材料需求、进度、材料取用范围数字全消失。
    /// 现在第 ② 步限定在**本窗口子树**内，且调用方已保证窗口是我们的。
    /// </summary>
    internal static int HideAll(GameObject window, params string[] fieldNames)
    {
        int n = 0;
        var missed = new List<string>();

        // ① 精确字段
        foreach (var f in fieldNames)
        {
            if (FindGo(window, f) != null)
            {
                if (Hide(window, f)) n++;
            }
            else missed.Add(f);
        }

        // ② 本窗口子树内按名兜底
        if (missed.Count > 0)
        {
            try
            {
                foreach (var t in window.GetComponentsInChildren<Transform>(true))
                {
                    if (t == null || t.gameObject == window) continue;
                    if (!missed.Contains(t.name)) continue;
                    if (!t.gameObject.activeSelf) continue;
                    t.gameObject.SetActive(false);
                    n++;
                }
            }
            catch { }
        }
        return n;
    }
}

/// <summary>
/// 把「额外产品」候选填进**游戏原生下拉**（`WindowWorkshop.dp_blueprint`，TMP_Dropdown）。
///
/// ## 背景（这一版方案怎么来的）
/// 早期版本是自己用 Button 画一个选择条 + 自建列表，用户反复反馈「不像游戏内 UI」。
/// 查反编译语料库发现：制造台窗口自带 `dp_blueprint`（标准 TMP_Dropdown，
/// 层级为 ItemIcon/Label/Arrow/Template{Viewport{Content{Item}}}），
/// 游戏只负责调 `InitWorkshopOptionData(...)` **填数据**、控件本身是预制体自带的。
/// 于是思路改成：**换窗口预制体拿到原生控件 + 自己填数据**。
///
/// 实测：把建筑的 `window_prefab` 换成 `window_blacksmith` 后，窗口就是原生样式，
/// 只是数值是制造台的默认值 —— 因为游戏按制造台的配方表填的。
/// 这里用 TMP_Dropdown 的**标准 API** 覆盖成我们的物品列表。
/// </summary>
internal static class NativeDropdown
{
    /// <summary>候选列表签名：用来判断要不要重建（避免每帧重建产生垃圾与回调）</summary>
    private static string _lastSignature = "";
    private static GameObject? _lastWindow;

    private const string FieldName = "dp_blueprint";

    /// <summary>刷新原生下拉：填候选、选中当前值、接回调（每帧调用，内部去重）</summary>
    internal static void Refresh(GameObject window)
    {
        try
        {
            var dd = NativeUi.Find<TMP_Dropdown>(window, FieldName);
            if (dd == null) return;

            var candidates = Plugin.ParseExtraCandidates();
            int current = Plugin.ExtraProduct;

            // 签名 = 候选集 + 当前值。只有变了才重建，避免每帧 AddOptions 造成垃圾/回调风暴
            var sig = new System.Text.StringBuilder();
            foreach (var c in candidates) sig.Append(c.id).Append(':').Append(c.name).Append(',');
            sig.Append('#').Append(current);
            sig.Append('@').Append(Plugin.ExtraPerDay);
            string signature = sig.ToString();
            if (_lastWindow == window && signature == _lastSignature) return;

            var labels = new List<string> { "无" };
            int sel = 0;
            foreach (var c in candidates)
            {
                labels.Add($"{c.name} +{Plugin.ExtraPerDay}/日");
                if (c.id == current) sel = labels.Count - 1;
            }

            // 用标准 API 覆盖（ClearOptions + AddOptions + RefreshShownValue）。
            // ⚠ `AddOptions(List<string>)` 的重载在 IL2CPP 里不生成，
            // 必须自己构造 `List<TMP_Dropdown.OptionData>`。
            var optionData = new Il2CppSystem.Collections.Generic.List<TMP_Dropdown.OptionData>();
            foreach (var label in labels)
                optionData.Add(new TMP_Dropdown.OptionData(label));

            dd.ClearOptions();
            dd.AddOptions(optionData);
            dd.SetValueWithoutNotify(sel);
            dd.RefreshShownValue();

            // 回调：只在首次接一次
            if (_lastWindow != window)
            {
                try
                {
                    dd.onValueChanged.RemoveAllListeners();
                    // ⚠ IL2CPP 下 lambda 不能隐式转 UnityEngine.Events.UnityAction<int>，
                    // 必须显式 new 一个委托实例。
                    dd.onValueChanged.AddListener(
                        (UnityEngine.Events.UnityAction<int>)(idx =>
                        {
                            try
                            {
                                var list = Plugin.ParseExtraCandidates();
                                int picked = (idx <= 0 || idx > list.Count) ? 0 : list[idx - 1].id;
                                Plugin.SetExtraProduct(picked);
                                Plugin.LogV($"[FacilityUI] 原生下拉选中索引 {idx} → 额外产品 {picked}");
                            }
                            catch (Exception ex) { Plugin.LogV($"[FacilityUI] 下拉回调异常: {ex.Message}"); }
                        }));
                }
                catch (Exception ex) { Plugin.LogV($"[FacilityUI] 接下拉回调失败: {ex.Message}"); }
            }

            _lastWindow = window;
            _lastSignature = signature;
            Plugin.LogV($"[FacilityUI] 原生下拉已填充 {labels.Count} 项，当前={sel}（{FieldName}）");
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 原生下拉刷新失败: {ex.Message}"); }
    }

    /// <summary>下拉是否到位（供诊断/文案逻辑判断）</summary>
    internal static bool Exists(GameObject window) => NativeUi.Find<TMP_Dropdown>(window, FieldName) != null;
}
