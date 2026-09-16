using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// 安全护栏：当**本 mod 的建筑**借用原版窗口（例如 `window_blacksmith`）时，
/// 原版窗口的一些方法会因为「没有对应的设施组件」而空引用。
///
/// ## 具体案例（实测报错）
/// ```
/// System.NullReferenceException
///   at WindowWorkshop.ShowWindowTip ()
/// ```
/// 原因：`WindowWorkshop.ShowWindowTip` 内部要用 `this.workshop`
/// （类型 `FacilityWorkshop`，即制造台那一套工坊逻辑）——
/// 而我们的建筑是 `FacilityGatherersHut`，**根本没有 workshop 对象**，所以必崩。
///
/// ## 处理策略
/// 这些方法只是「显示提示文案」，跳过它不影响功能：
/// 用 Prefix 在**确认是本 mod 建筑的窗口**时直接跳过（返回 false），
/// 原版窗口照旧执行（归属判断 fail-safe，见 `FacilityWindowUi.IsOurWindow`）。
///
/// ## 扩展方式
/// 以后给新建筑借别的窗口时，如果日志里出现类似的 `NullReferenceException`，
/// 把「类名.方法名」加到 <see cref="Guarded"/> 里即可。
/// </summary>
[HarmonyPatch]
internal static class NativeWindowSafetyPatch
{
    /// <summary>
    /// 需要护栏的「窗口类.方法名」清单。
    ///
    /// ## ⚠ 护栏是"最后手段"，能不用就不用
    /// 实测教训：把 `InitDpBlueprint` 护栏掉之后，**熔炉的配方下拉永远是空的** ——
    /// 因为原生熔炉的配方下拉正是由这条链填充的
    /// （数据源 `D.Ins.blueprint_list_dic_by_facility[facility_id]`，
    /// 实测我们的 105052 在该索引里**有 7 条**）。
    /// 护栏的副作用是"窗口少了功能"，比崩溃更难发现。
    ///
    /// 所以现在改成**按条件跳过**：只有窗口的 `workshop` 字段确实为 null 时才跳过
    /// （那种情况调下去必抛空引用，会让整个 SetInfo 中断）。
    /// </summary>
    private static readonly (string Type, string Method)[] Guarded =
    {
        ("WindowWorkshop", "ShowWindowTip"),
        ("WindowWorkFacility", "ShowWindowTip"),
        // ⚠ 实测报错：WindowGatherersHut.ShowWindowTip 抛空引用
        //   （我们的建筑借 window_gatherers_hut 时，它要的字段没绑上）
        ("WindowGatherersHut", "ShowWindowTip"),
        ("WindowWorkFacilityWithStockAdjust", "ShowWindowTip"),
        ("WindowWorkshop", "InitDpBlueprint"),
        ("WindowWorkshop", "UpdateAlternativeFormula"),
        ("WindowWorkshop", "Refresh"),
        ("WindowWorkshop", "UpdateAutoMakeProductOfMaterials"),
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static IEnumerable<MethodBase> TargetMethods()
    {
        var list = new List<MethodBase>();
        foreach (var (typeName, methodName) in Guarded)
        {
            Type? t = null;
            try { t = AccessTools.TypeByName(typeName); } catch { }
            if (t == null) continue;
            var m = AccessTools.Method(t, methodName);
            if (m == null) continue;
            // SetInfo(Facility) 是带参方法，也要能护栏
            if (m.GetParameters().Length > 1) continue;
            list.Add(m);
        }

        // `InitWorkshopOptionData` 是**静态**方法且参数多，按「名字 + 参数个数」找。
        // 它是 `SetInfo → InitDpBlueprint → InitWorkshopOptionData` 链条的终点，
        // 空引用就发生在它内部（要用制造台的 formula_list，我们没有配方）。
        try
        {
            var wt = AccessTools.TypeByName("WindowWorkshop");
            if (wt != null)
            {
                foreach (var m in wt.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                BindingFlags.Static | BindingFlags.Instance |
                                                BindingFlags.DeclaredOnly))
                {
                    if (m.Name != "InitWorkshopOptionData") continue;
                    list.Add(m);
                    break;   // 同名重载只挂第一个够用（参数最多的那个一般就是它）
                }
            }
        }
        catch { }

        if (list.Count > 0)
            Plugin.LogV($"[Facility] 窗口安全护栏挂了 {list.Count} 个方法: " +
                        string.Join(", ", list.ConvertAll(m => $"{m.DeclaringType?.Name}.{m.Name}")));
        return list;
    }

    /// <summary>
    /// 只在本 mod 的窗口**且确实会崩**时跳过原方法；其余一律放行。
    ///
    /// ⚠ 这里的判据从「是我们的窗口就跳过」收窄成「是我们的窗口**且缺必要对象**才跳过」——
    /// 因为无脑跳过会让窗口**悄悄少掉功能**（实测：跳过 `InitDpBlueprint` →
    /// 熔炉的配方下拉永远是空的，而数据其实都在）。
    ///
    /// 崩溃的根因是 `this.workshop == null`（我们的建筑借窗口预制体时，
    /// 设施组件类型可能和窗口期望的不一致）→ 那种情况必须跳过，否则整个 SetInfo 中断。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static bool Prefix(object __instance)
    {
        try
        {
            if (__instance is not Component c || c == null) return true;
            bool ours = FacilityWindowUi.IsOurWindowPublic(c.gameObject);
            if (!ours) return true;                     // 原版窗口：完全放行

            // 是我们的窗口：只在「窗口上没有 workshop 对象」时跳过（否则必抛空引用）
            if (NeedsWorkshopButMissing(c))
            {
                Plugin.LogV($"[Facility] 护栏跳过 {c.GetType().Name}.{_currentMethod}（窗口缺 workshop 对象）");
                return false;
            }
            return true;
        }
        catch { }
        return true;
    }

    /// <summary>当前正在护栏的方法名（TargetMethods 时记录，仅用于日志）</summary>
    private static string _currentMethod = "?";

    /// <summary>
    /// 窗口上需要 `workshop` 字段但它是 null 吗。
    /// 读不到该字段（例如熔炉窗口本来就没这字段）→ 不算缺（放行，让它自己跑）。
    /// </summary>
    private static bool NeedsWorkshopButMissing(Component c)
    {
        try
        {
            var t = c.GetType();
            object? v = null;
            bool found = false;
            var fi = t.GetField("workshop");
            if (fi != null) { v = fi.GetValue(c); found = true; }
            if (!found)
            {
                var pi = t.GetProperty("workshop");
                if (pi != null) { v = pi.GetValue(c); found = true; }
            }
            if (!found) return false;        // 没这个字段 → 不是缺对象的问题 → 放行
            return v == null;                // 有字段但为 null → 跳过（会崩）
        }
        catch { return false; }
    }
}
