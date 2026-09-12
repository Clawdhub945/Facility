using HarmonyLib;

namespace FacilityMod;

/// <summary>
/// 工位数补丁：原版工位数 = career 表 manpower_limit（我们配 4），
/// 窗口 +/- 只能在 [0, 原始工位] 内调。这里把「原始工位」接到 cfg
/// 「每座最大工位数」（默认 10），窗口里即可原生增减 1..10 人。
/// 仅 105040 生效，其他设施走原逻辑。
/// </summary>
[HarmonyPatch(typeof(Facility), nameof(Facility.GetOriginalWorkPosCount))]
internal static class FacilityGetOriginalWorkPosCountPatch
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static void Postfix(Facility __instance, ref int __result)
    {
        try
        {
            if (__instance != null && __instance.stuff_id == Plugin.FacilityId)
                __result = Plugin.WorkPosMaxEntry?.Value ?? __result;
        }
        catch { }
    }
}
