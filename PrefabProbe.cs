using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// 诊断工具：把游戏 `PrefabManager` 里的预制体清单打到日志（仅详细模式）。
///
/// ### 为什么要这个
/// 用户反复要求「下拉框用游戏内 UI」。查反编译语料库
/// （`C:\AI\游戏向量库\code_meta.json`）已确认游戏**自带**一个带图标的下拉控件：
/// ```
/// StuffIconDropdown.SetInfo(bool is_check_tech_lock, int cur_stuff_id,
///                           List<int> stuff_id_list, Action<int> on_choose_change,
///                           string empty_tips, string no_other_seed_tips)
/// ```
/// 它出现在作物田/牧场/资源作业等窗口的预制体里（字段 `drop_down`），
/// 而我们用的 `window_gatherers_hut` 里没有。
/// 要复用它，得先知道它的预制体名 —— 这份清单就是答案来源。
/// </summary>
internal static class PrefabProbe
{
    /// <summary>打印名字含关键字的预制体（只打一次，靠调用方控制）</summary>
    internal static void Dump(string keyword)
    {
        try
        {
            var t = AccessTools.TypeByName("PrefabManager");
            if (t == null) { Plugin.LogV("[Facility] 找不到 PrefabManager 类型"); return; }

            var names = new List<string>();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (var f in t.GetFields(flags))
            {
                object? v = null;
                try { v = f.GetValue(null); } catch { }
                Collect(v, names);
            }

            names.Sort();
            var hit = names.FindAll(n => n.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
            Plugin.LogV($"[Facility] PrefabManager 共 {names.Count} 项；含「{keyword}」的 {hit.Count} 项：");
            int n = 0;
            foreach (var h in hit)
            {
                Plugin.LogV($"    {h}");
                if (++n >= 80) { Plugin.LogV("    …（还有更多，已截断）"); break; }
            }
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] PrefabProbe 失败: {ex.Message}"); }
    }

    private static void Collect(object? v, List<string> outNames)
    {
        try
        {
            if (v is IDictionary d)
            {
                foreach (var k in d.Keys) if (k != null) outNames.Add(k.ToString() ?? "");
            }
            else if (v is IEnumerable e && v is not string)
            {
                foreach (var o in e) if (o != null) outNames.Add(o.ToString() ?? "");
            }
        }
        catch { }
    }
}
