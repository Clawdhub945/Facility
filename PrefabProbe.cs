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

    /// <summary>
    /// 诊断：检查**游戏实际加载的配方表**里有没有我们的行。
    ///
    /// ## 为什么需要
    /// 新建的熔炉（105052/105051）窗口里「配方下拉」是空的，而原生熔炉(105028) 有。
    /// 怀疑是 mod 通道加载 `blueprint.json` 时**没有把我们的行装进配方索引**。
    /// 光看文件内容不够（文件里确实有 8 条）—— 必须问**游戏运行时的表**。
    ///
    /// 检查两个索引：
    ///   * `D.Ins.blueprint_dic`                 按 formula_id 索引
    ///   * `D.Ins.blueprint_list_dic_by_facility` 按 facility_id 索引（窗口下拉读的就是它）
    /// </summary>
    internal static void DumpRecipeTables()
    {
        try
        {
            var ins = D.Ins;
            if (ins == null) { Plugin.LogV("[Facility] D.Ins 为空，跳过配方诊断"); return; }

            // ① blueprint_dic：按 formula_id
            int total = 0, ours = 0;
            var byFacility = new System.Collections.Generic.Dictionary<int, int>();
            try
            {
                var dic = ins.blueprint_dic;
                if (dic != null)
                {
                    foreach (var kv in dic)
                    {
                        total++;
                        var bi = kv.Value;
                        int fid = 0;
                        try { fid = bi.facility_id; } catch { }
                        if (fid != 0)
                        {
                            byFacility.TryGetValue(fid, out int c);
                            byFacility[fid] = c + 1;
                        }
                        if (fid == Plugin.FurnaceNativeTestId || fid == Plugin.Furnace3TestId
                            || fid == Plugin.FacilityId || fid == Plugin.SuperFacilityId) ours++;
                    }
                }
            }
            catch (Exception ex) { Plugin.LogV($"[Facility] 读 blueprint_dic 失败: {ex.Message}"); }

            var sb = new System.Text.StringBuilder();
            sb.Append($"[Facility] 配方表诊断: blueprint_dic 共 {total} 条，其中本 mod 的 {ours} 条\n");
            foreach (int fid in new[] { 105028, Plugin.FurnaceNativeTestId, Plugin.Furnace3TestId })
            {
                byFacility.TryGetValue(fid, out int c);
                sb.Append($"    facility_id={fid} → {c} 条\n");
            }

            // ② blueprint_list_dic_by_facility：窗口下拉直接读这个
            try
            {
                var byFac = ins.blueprint_list_dic_by_facility;
                if (byFac != null)
                {
                    sb.Append($"  blueprint_list_dic_by_facility 共 {byFac.Count} 个键\n");
                    foreach (int fid in new[] { 105028, Plugin.FurnaceNativeTestId, Plugin.Furnace3TestId })
                    {
                        bool has = false;
                        int n = 0;
                        try
                        {
                            has = byFac.ContainsKey(fid);
                            if (has)
                            {
                                var lst = byFac[fid];
                                n = lst?.Count ?? 0;
                            }
                        }
                        catch { }
                        sb.Append($"    facility_id={fid} → {(has ? $"有，{n} 条" : "【没有这个键】")}\n");
                    }
                }
                else sb.Append("  blueprint_list_dic_by_facility 为 null\n");
            }
            catch (Exception ex) { sb.Append($"  读 blueprint_list_dic_by_facility 失败: {ex.Message}\n"); }

            Plugin.LogV(sb.ToString());
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] 配方表诊断失败: {ex.Message}"); }
    }
}
