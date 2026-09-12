using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace FacilityMod;

/// <summary>窗口打开/刷新后：注入额外产品下拉框 + 改写森林覆盖率文案</summary>
[HarmonyPatch(typeof(WindowGatherersHut), nameof(WindowGatherersHut.SetInfo))]
internal static class FacilityWindowSetInfoPatch
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static void Postfix(WindowGatherersHut __instance)
    {
        try { FacilityWindowUi.Apply(__instance.gameObject); }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] SetInfo 后处理失败: {ex.Message}"); }
    }
}

/// <summary>窗口状态刷新/悬浮提示更新后：重新改写文案（游戏会周期性覆盖这些文本）</summary>
[HarmonyPatch]
internal static class FacilityWindowRefreshPatches
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static IEnumerable<MethodBase> TargetMethods()
    {
        var list = new List<MethodBase>();
        var t = typeof(WindowGatherersHut);
        foreach (var n in new[] { "UpdateState", "ShowWindowTip" })
        {
            var m = AccessTools.Method(t, n);
            if (m != null) list.Add(m);
        }
        return list;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static void Postfix(WindowGatherersHut __instance)
    {
        try { FacilityWindowUi.Apply(__instance.gameObject); }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 刷新后处理失败: {ex.Message}"); }
    }
}
