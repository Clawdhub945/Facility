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
    /// <summary>需要护栏的「窗口类.方法名」清单</summary>
    private static readonly (string Type, string Method)[] Guarded =
    {
        // ShowWindowTip 系列：内部访问 this.workshop（制造台的工坊组件），
        // 我们的建筑是 FacilityGatherersHut，没有这个对象 → 必崩。
        ("WindowWorkshop", "ShowWindowTip"),
        ("WindowWorkFacility", "ShowWindowTip"),
        ("WindowWorkFacilityWithStockAdjust", "ShowWindowTip"),

        // 下拉初始化：`SetInfo → InitDpBlueprint → InitWorkshopOptionData(...)`
        // 需要制造台的 `formula_list`（配方表），我们的建筑没有配方 → 传 null → 崩。
        // **而且我们不需要它**：下拉的候选是我们自己用 NativeDropdown 填的。
        ("WindowWorkshop", "InitDpBlueprint"),
        ("WindowWorkshop", "UpdateAlternativeFormula"),
        ("WindowWorkshop", "Refresh"),
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
            if (m.GetParameters().Length != 0) continue;   // 只护栏无参方法
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
    /// 是本 mod 的窗口就跳过原方法（避免空引用）；原版窗口照旧。
    /// 用 `object __instance` 接，兼容多类型补丁。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static bool Prefix(object __instance)
    {
        try
        {
            if (__instance is not Component c || c == null) return true;
            bool ours = FacilityWindowUi.IsOurWindowPublic(c.gameObject);
            if (ours) return false;      // 跳过：我们没那套组件，调了必崩
        }
        catch { }
        return true;
    }
}
