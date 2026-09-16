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
    /// ## 两种跳过策略
    /// * **按条件**（`Conditional`）：只在窗口确实缺 `workshop` 对象时跳过。
    ///   适用于 `WindowWorkshop` 系（有 `workshop` 字段，为 null 时必崩）。
    /// * **无条件**（默认）：只要是**本 mod 的窗口**就跳过。
    ///   适用于「本来就没有 `workshop` 字段、调用必崩」的方法 ——
    ///   例如 `WindowGatherersHut.ShowWindowTip`：
    ///   它**没有 `workshop` 字段**，所以"按条件"判据永远返回"不算缺" → 照旧执行 → 仍崩。
    ///   （踩过：加了护栏但报错依旧，就是这个原因。）
    /// </summary>
    private static readonly (string Type, string Method, bool Conditional)[] Guarded =
    {
        // —— 无条件跳过（本 mod 窗口一律不调）——
        ("WindowGatherersHut", "ShowWindowTip", false),
        ("WindowWorkFacility", "ShowWindowTip", false),
        ("WindowWorkFacilityWithStockAdjust", "ShowWindowTip", false),
        ("WindowWorkshop", "ShowWindowTip", false),

        // —— 按条件跳过（缺 workshop 才跳；否则放行，让游戏自己填配方下拉）——
        ("WindowWorkshop", "InitDpBlueprint", true),
        ("WindowWorkshop", "UpdateAlternativeFormula", true),
        ("WindowWorkshop", "Refresh", true),
        ("WindowWorkshop", "UpdateAutoMakeProductOfMaterials", true),
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static IEnumerable<MethodBase> TargetMethods()
    {
        var list = new List<MethodBase>();
        foreach (var (typeName, methodName, _) in Guarded)
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
    /// ## 两种跳过策略（见 <see cref="Guarded"/> 的注释）
    /// * **无条件**：本 mod 的窗口一律跳过 —— 用于「本来就没有 `workshop` 字段、
    ///   调用必崩」的方法（`*ShowWindowTip`）。
    ///   ⚠ 踩过：`WindowGatherersHut.ShowWindowTip` 加过护栏但报错依旧 ——
    ///   因为它**没有 `workshop` 字段**，"按条件"判据永远返回"不算缺" → 照旧执行 → 仍崩。
    /// * **按条件**：只在窗口确实缺 `workshop` 对象时跳过 —— 用于 `WindowWorkshop` 系
    ///   （无脑跳过会让熔炉的配方下拉永远是空的）。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static bool Prefix(object __instance, MethodBase __originalMethod)
    {
        try
        {
            if (__instance is not Component c || c == null) return true;
            bool ours = FacilityWindowUi.IsOurWindowPublic(c.gameObject);
            if (!ours) return true;                     // 原版窗口：完全放行

            string name = __originalMethod?.Name ?? "?";
            bool conditional = !name.EndsWith("ShowWindowTip");   // ShowWindowTip 系无条件跳过

            if (!conditional && !NeedsWorkshopButMissing(c)) return true;

            Plugin.LogV($"[Facility] 护栏跳过 {c.GetType().Name}.{name}" +
                        (conditional ? "（窗口缺 workshop 对象）" : "（该方法在本 mod 建筑上必崩）"));
            return false;
        }
        catch { }
        return true;
    }

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
