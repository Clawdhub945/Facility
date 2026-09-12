using System;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// IL2CPP 注入的 MonoBehaviour：主线程驱动「综合生产所」(105040) 的每日产出。
/// 建筑本体是 Def 注入（复制采集营地行：原生工人系统/人数可调/自带 bag），
/// 本组件在游戏换日时按 工人数 × cfg 产出表 调 Facility.AddStuff 入袋，
/// 之后由游戏原生搬运工把产出搬进仓库。
/// </summary>
public class FacilityComponent : MonoBehaviour
{
    private float _nextAt;
    private int _lastDay = -1;
    private bool _dayApiBroken;

    private void Update()
    {
        if (Time.time < _nextAt) return;
        _nextAt = Time.time + 1.5f;
        try
        {
            // cfg 热生效：改 BepInEx/config/claude.facility.cfg 后最多 1.5s 生效
            try { Plugin.ModConfigFile?.Reload(); } catch { }

            int day = GetDayKey();
            if (day < 0) return;
            if (_lastDay < 0) { _lastDay = day; return; } // 进档/启动首日只记基线，不产出（防重复发）
            if (day == _lastDay) return;
            _lastDay = day;

            ProduceForNewDay();
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Facility] Update 异常: {ex}");
        }
    }

    /// <summary>游戏日序号（年/月/日拼合）。Clock 为游戏日历（蜂房等原生设施也用它判日）。</summary>
    private int GetDayKey()
    {
        if (_dayApiBroken) return -1;
        try
        {
            var now = Clock.Now;
            int y = now.Year;
            int m = now.MonthInYear;
            int d = now.DayInMonth;
            return y * 10000 + m * 100 + d;
        }
        catch (Exception ex)
        {
            _dayApiBroken = true;
            Plugin.LogError($"[Facility] 读取游戏日期失败，每日产出停用: {ex.Message}");
            return -1;
        }
    }

    private void ProduceForNewDay()
    {
        var products = Plugin.ParseProducts();
        int extraId = Plugin.ExtraProduct;
        if (extraId > 0)
        {
            bool exists = false;
            foreach (var (sid, _) in products)
                if (sid == extraId) { exists = true; break; }
            if (!exists) products.Add((extraId, Plugin.ExtraPerDay));
        }
        if (products.Count == 0) return;

        // 深度诊断：career 行真实字段 + 工位状态（配合排查窗口 99/99 问题）
        try
        {
            var dic = D.Ins.career_dic_with_facility_id_as_key;
            if (dic != null && dic.ContainsKey(Plugin.FacilityId))
            {
                var ci = dic[Plugin.FacilityId];
                Plugin.LogV($"[Facility] career行: data_id={ci.data_id} limit={ci.manpower_limit} " +
                            $"factor={ci.manpower_factor} npc_type={ci.npc_type} main={ci.is_main_facility}");
            }
            else
            {
                Plugin.LogV("[Facility] career行: 字典中无 105040！");
            }
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] career dump 失败: {ex.Message}"); }

        var facilities = UnityEngine.Object.FindObjectsOfType<Facility>();
        if (facilities == null) return;

        int built = 0;
        foreach (var f in facilities)
        {
            if (f == null || f.stuff_id != Plugin.FacilityId) continue;
            bool finished;
            try { finished = f.is_build_finished; }
            catch { finished = true; }

            int workers = 0;
            try { workers = f.npc_list?.Count ?? 0; } catch { }
            int workPos = -1, origPos = -1, customLimit = -1;
            bool isCustom = false;
            try { workPos = f.GetWorkPositionCount(); } catch { }
            try { origPos = f.GetOriginalWorkPosCount(); } catch { }
            try { isCustom = f.is_custom_worker_count_limit; customLimit = f.worker_count_limit; } catch { }
            Plugin.LogV($"[Facility] 生产所 guid={f.guid} 完工={finished} 工位数={workPos} 原始工位={origPos} " +
                        $"自定义上限={isCustom}({customLimit}) 工人={workers}");
            if (!finished) continue;
            if (workers <= 0)
            {
                Plugin.LogV($"[Facility] 生产所 guid={f.guid} 无工人，今日跳过");
                continue;
            }

            built++;
            foreach (var (sid, per) in products)
            {
                int count = workers * per;
                try
                {
                    f.AddStuff(sid, count);
                    Plugin.LogV($"[Facility] 生产所 guid={f.guid} 工人×{workers} → 物品{sid} +{count}");
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"[Facility] 入袋失败 guid={f.guid} 物品{sid}: {ex.Message}");
                }
            }
        }
        if (built > 0)
            Plugin.LogInfo($"[Facility] 新的一天：{built} 座综合生产所完成产出（{products.Count} 种产品）");
    }
}
