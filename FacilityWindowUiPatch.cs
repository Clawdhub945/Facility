using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// 窗口补丁：建筑窗口打开 / 刷新后调 <see cref="FacilityWindowUi.Apply"/>。
///
/// ## 为什么改成「按名字反射挂一堆方法」，而不是写死 `WindowGatherersHut.SetInfo`
/// 早期版本只挂了 `[HarmonyPatch(typeof(WindowGatherersHut), nameof(WindowGatherersHut.SetInfo))]`，
/// 结果用户的建筑窗口**没有下拉框 / UI 不对**（实测反馈）。原因有两个：
///   1. 我们的建筑 prefab 虽然是 `gatherers_hut`，但游戏按 `window_prefab` 开窗，
///      实际窗口类型可能是基类 `WindowWorkFacility` 或派生类
///      `WindowWorkFacilityWithStockAdjust` —— **只挂子类会漏掉**。
///   2. interop 里 `SetInfo` 重载极多（`SetInfo(Facility)` / `SetInfo(int)` /
///      `SetInfo(Facility, Action<int>)` …），写死一个签名同样会漏。
///
/// 现在：**枚举窗口类型 + 按方法名挂全部可用重载**。多挂无害 ——
/// `Apply` 内部有归属判断（只改本 mod 的建筑）且幂等。
/// </summary>
[HarmonyPatch]
internal static class FacilityWindowPatches
{
    /// <summary>要挂的窗口类型名（基类 + 相关派生类，能找到就挂）</summary>
    private static readonly string[] WindowTypeNames =
    {
        "WindowGatherersHut",
        "WindowWorkFacility",
        "WindowWorkFacilityWithStockAdjust",
        "WindowWorkshop",
    };

    /// <summary>窗口打开/刷新相关的方法名（按名字匹配，不锁签名）</summary>
    private static readonly string[] MethodNames = { "SetInfo", "UpdateState", "ShowWindowTip", "Refresh" };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static IEnumerable<MethodBase> TargetMethods()
    {
        var list = new List<MethodBase>();
        var seen = new HashSet<string>();
        foreach (var typeName in WindowTypeNames)
        {
            Type? t = null;
            try { t = AccessTools.TypeByName(typeName); } catch { }
            if (t == null) continue;

            var flags = BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly;
            MethodInfo[] ms;
            try { ms = t.GetMethods(flags); } catch { continue; }
            foreach (var m in ms)
            {
                if (Array.IndexOf(MethodNames, m.Name) < 0) continue;
                var ps = m.GetParameters();
                // 只挂能安全跑 Postfix 的：无参、或第一个参数是 Facility
                bool ok = ps.Length == 0 || ps[0].ParameterType == typeof(Facility);
                if (!ok) continue;
                if (!seen.Add(t.Name + "." + m.Name + "/" + ps.Length)) continue;
                list.Add(m);
            }
        }

        if (list.Count == 0)
            Plugin.LogV("[FacilityUI] 没找到任何窗口方法，窗口补丁未挂上");
        else
            Plugin.LogV($"[FacilityUI] 窗口补丁挂了 {list.Count} 个方法: " +
                        string.Join(", ", list.ConvertAll(
                            m => $"{m.DeclaringType?.Name}.{m.Name}/{m.GetParameters().Length}")));
        return list;
    }

    /// <summary>
    /// 通用 Postfix：`__instance` 用 object 接（多类型补丁不能写强类型），
    /// 是 Component 就取其 gameObject 交给 `Apply`。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static void Postfix(object __instance)
    {
        try
        {
            if (__instance is not Component c || c == null) return;
            FacilityWindowUi.Apply(c.gameObject);
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 窗口补丁后处理失败: {ex.Message}"); }
    }
}
