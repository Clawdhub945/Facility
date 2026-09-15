using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// 「工人每日产出」逻辑 —— **只服务规格里 `DailyProducer = true` 的建筑**。
///
/// ## 为什么单独成文件（架构重构）
/// 早期这段逻辑写在 `FacilityComponent` 的换日流程里，用**全局判据**
/// （`IsManaged` + 硬编码的 `FacilityGatherersHut` 检查）筛建筑。
/// 后果：改「高炉不产木石」会连带影响另外两座（用户实测反馈：
/// 「你改高炉不再产木石，其他 2 种也跟着不生产了」）。
///
/// 现在改成**按规格分派**：调用方传入要处理的建筑集合，
/// 本文件只认 `spec.DailyProducer`，与窗口/归属逻辑彻底解耦。
/// </summary>
internal static class FacilityProducer
{
    /// <summary>
    /// 给「所有 `DailyProducer = true` 的建筑的设施」发一天的量。
    /// </summary>
    /// <param name="dayKey">当前游戏日（仅用于日志）</param>
    /// <returns>实际发产成功的建筑数</returns>
    internal static int ProduceForAll(int dayKey)
    {
        var specs = Buildings.All.Where(b => b.DailyProducer).ToArray();
        if (specs.Length == 0) return 0;

        var wanted = new HashSet<int>();
        foreach (var s in specs) wanted.Add(s.StuffId);

        Facility[] all;
        try { all = UnityEngine.Object.FindObjectsOfType<Facility>(); }
        catch { return 0; }
        if (all == null) return 0;

        var products = Plugin.ParseProducts();
        int extraId = Plugin.ExtraProduct;
        if (extraId > 0)
        {
            bool exists = false;
            foreach (var (sid, _) in products) if (sid == extraId) { exists = true; break; }
            if (!exists) products.Add((extraId, Plugin.ExtraPerDay));
        }
        if (products.Count == 0) return 0;

        int produced = 0;
        foreach (var f in all)
        {
            if (f == null) continue;

            int sid = 0;
            try { sid = f.stuff_id; } catch { }
            if (!wanted.Contains(sid)) continue;          // 只处理规格里声明「日常产出」的建筑

            var spec = Buildings.ByStuffId(sid);
            if (spec == null || !spec.DailyProducer) continue;

            if (ProduceOne(f, spec, products, dayKey)) produced++;
        }

        if (produced > 0)
            Plugin.LogV($"[Facility] 第 {dayKey} 日：{produced} 座建筑完成产出（{products.Count} 种产品）");
        return produced;
    }

    /// <summary>给单座建筑发一天的量（工人数 × cfg 产出表）</summary>
    private static bool ProduceOne(Facility f, BuildingSpec spec,
                                   List<(int id, int perWorker)> products, int dayKey)
    {
        try
        {
            bool finished;
            try { finished = f.is_build_finished; } catch { finished = true; }
            if (!finished) return false;

            int workers = 0;
            try { workers = f.npc_list?.Count ?? 0; } catch { }
            if (workers <= 0)
            {
                Plugin.LogV($"[Facility] {spec.Name} guid={SafeGuid(f)} 无工人，今日跳过");
                return false;
            }

            foreach (var (stuffId, perWorker) in products)
            {
                int count = workers * perWorker;
                if (count <= 0) continue;
                try
                {
                    f.AddStuff(stuffId, count);
                    f.RecordProduct(stuffId, count);
                    Plugin.LogV($"[Facility] {spec.Name} guid={SafeGuid(f)} 工人×{workers} → 物品{stuffId} +{count}");
                }
                catch (Exception ex)
                {
                    Plugin.LogError($"[Facility] {spec.Name} 发产失败 物品{stuffId}: {ex.Message}");
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Facility] {spec.Name} 发产异常: {ex.Message}");
            return false;
        }
    }

    private static int SafeGuid(Facility f)
    {
        try { return f.guid; } catch { return 0; }
    }
}
