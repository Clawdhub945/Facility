using System;
using System.Collections.Generic;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// **自研冶炼炉**：3×3 高炉的燃料 + 配方 + 产出逻辑（不依赖游戏的熔炉机制）。
///
/// ## 为什么自研（而不是用游戏的熔炉机制）
/// 对照实验结论：`window_furnace` 的控件依赖 **prefab 上的 `FacilityFurnace` 组件**，
/// 而原生熔炉骨架是 **2×2** —— 借 3×3 骨架配 `FacilityFurnace` 会导致
/// 「窗口能显示但控件不可交互」（配方下拉空、燃料按钮点不动）。
/// 用户要求 **3×3**，所以燃料/配方/产出这套逻辑由我们自己实现。
///
/// ## 工作模型（刻意从简，便于扩展）
/// * **入料**：玩家把材料（铁锭）与燃料（煤）放进建筑自己的仓库
///   （骨架用原生 `stuff_type=1` 建筑，自带存储界面；也可靠搬运工自动补料）
/// * **每天结算一次**（挂在本 mod 的换日流程上）：
///   1. 检查仓库里有没有 `input` 材料 与 `fuel` 燃料
///   2. 够 → 扣除材料与燃料，产出 `output`（走 `AddStuff` + `RecordProduct` 记账）
///   3. 不够 → 记录原因（供窗口提示 / 日志）
/// * **燃料**与材料分开记账，因为工业 mod 里"烧什么"是要单独配置的
///
/// ## 扩展方式
/// 给 <see cref="BuildingSpec.Smelting"/> 配一份 <see cref="SmeltingRecipe"/> 即可，
/// 例如以后加"用煤烧铁矿出铁锭"的炉子：只改规格，不动逻辑。
/// </summary>
internal static class SmelterConsumer
{
    /// <summary>每座建筑今天是否已经结算过（防同日重复；换日清空）</summary>
    private static readonly HashSet<int> _doneToday = new();

    /// <summary>最近一次未开工的原因（guid → 原因；供诊断/日志）</summary>
    private static readonly Dictionary<int, string> _lastBlockReason = new();

    /// <summary>换日时清空当日标记</summary>
    internal static void OnNewDay() { _doneToday.Clear(); _workedToday.Clear(); }

    /// <summary>某座建筑今天没开工的原因（空串 = 正常）</summary>
    internal static string BlockReason(int guid)
        => _lastBlockReason.TryGetValue(guid, out var r) ? r : "";

    /// <summary>某座建筑今天是否已经开工过（供 UI 模板显示进度）</summary>
    internal static bool WorkedToday(int guid) => _workedToday.Contains(guid);

    /// <summary>
    /// 取建筑仓库的物品字典（`物品id → 数量`）。
    /// 暴露给 UI 模板复用 —— 模板需要列库存，不该再实现一遍反射。
    /// 拿不到返回 null。
    /// </summary>
    internal static System.Collections.IDictionary? BagDictionaryOf(Facility f)
    {
        try
        {
            var bag = GetBag(f);
            return bag == null ? null : GetStuffDic(bag);
        }
        catch { return null; }
    }

    /// <summary>今天已开工的建筑 guid</summary>
    private static readonly HashSet<int> _workedToday = new();

    /// <summary>
    /// 给所有「配置了冶炼规格」的建筑结算一天。
    /// </summary>
    /// <returns>实际开工的建筑数</returns>
    internal static int ProcessAll()
    {
        var specs = new List<BuildingSpec>();
        foreach (var b in Buildings.All)
            if (b.Smelting != null) specs.Add(b);
        if (specs.Count == 0) return 0;

        Facility[] all;
        try { all = UnityEngine.Object.FindObjectsOfType<Facility>(); }
        catch { return 0; }
        if (all == null) return 0;

        int worked = 0;
        int candidates = 0;
        foreach (var f in all)
        {
            if (f == null) continue;
            int sid = 0;
            try { sid = f.stuff_id; } catch { }
            var spec = Buildings.ByStuffId(sid);
            if (spec?.Smelting == null) continue;
            candidates++;
            Plugin.LogV($"[Facility] 冶炼炉候选：guid={SafeGuid(f)} stuff_id={sid} 规格「{spec.Name}」");
            if (ProcessOne(f, spec, spec.Smelting)) worked++;
        }
        if (worked == 0)
            Plugin.LogV($"[Facility] 本日无冶炼产出（候选炉子 {candidates} 座；" +
                        $"原因见上方的「未开工」日志）");
        return worked;
    }

    /// <summary>给单座建筑结算一次冶炼</summary>
    private static bool ProcessOne(Facility f, BuildingSpec spec, SmeltingRecipe r)
    {
        int guid = SafeGuid(f);
        if (!_doneToday.Add(guid)) return false;      // 今日已结算

        try
        {
            bool finished;
            try { finished = f.is_build_finished; } catch { finished = true; }
            if (!finished) { _lastBlockReason[guid] = "还没建好"; return false; }

            var bag = GetBag(f);
            if (bag == null) { _lastBlockReason[guid] = "建筑没有仓库（has_bag 没开？）"; return false; }

            // ① 原料够不够
            foreach (var (id, need) in r.Inputs)
            {
                int have = CountInBag(bag, id);
                if (have < need)
                {
                    _lastBlockReason[guid] = $"缺少原料 {id}（需要 {need}，现有 {have}）";
                    Plugin.LogV($"[Facility] 高炉 guid={guid} 未开工：{_lastBlockReason[guid]}");
                    return false;
                }
            }
            // ② 燃料够不够
            if (r.FuelId != 0 && r.FuelPerBatch > 0)
            {
                int haveFuel = CountInBag(bag, r.FuelId);
                if (haveFuel < r.FuelPerBatch)
                {
                    _lastBlockReason[guid] = $"缺少燃料 {r.FuelId}（需要 {r.FuelPerBatch}，现有 {haveFuel}）";
                    Plugin.LogV($"[Facility] 高炉 guid={guid} 未开工：{_lastBlockReason[guid]}");
                    return false;
                }
            }

            // ③ 扣除原料与燃料，产出成品
            foreach (var (id, need) in r.Inputs) RemoveFromBag(bag, id, need);
            if (r.FuelId != 0 && r.FuelPerBatch > 0) RemoveFromBag(bag, r.FuelId, r.FuelPerBatch);

            f.AddStuff(r.OutputId, r.OutputCount);
            try { f.RecordProduct(r.OutputId, r.OutputCount); } catch { }

            _lastBlockReason[guid] = "";
            _workedToday.Add(guid);
            Plugin.LogV($"[Facility] 高炉 guid={guid} 开工：消耗 {Describe(r.Inputs)} + 燃料{r.FuelId}×{r.FuelPerBatch}" +
                        $" → 产出 {r.OutputId}×{r.OutputCount}");
            return true;
        }
        catch (Exception ex)
        {
            _lastBlockReason[guid] = $"异常: {ex.Message}";
            Plugin.LogError($"[Facility] 高炉 guid={guid} 冶炼异常: {ex}");
            return false;
        }
    }

    private static string Describe(List<(int id, int n)> list)
    {
        var parts = new List<string>();
        foreach (var (id, n) in list) parts.Add($"{id}×{n}");
        return string.Join(" + ", parts);
    }

    private static int SafeGuid(Facility f)
    {
        try { return f.guid; } catch { return 0; }
    }

    // ------------------------------------------------------------------
    // bag 读写
    //
    // ⚠ `Bag.stuff_dic` **没有出现在 interop 程序集里**（编译不过），
    //   所以这里统一用**反射**访问字段（字段名在反编译语料库里确认过：
    //   `Bag.stuff_dic` 是 `物品id → 数量` 的字典）。
    //   反射拿不到时返回 0 / 放弃扣除，并写日志 —— 不抛异常拖垮换日流程。
    // ------------------------------------------------------------------

    private static Bag? GetBag(Facility f)
    {
        try { return f.bag; } catch { return null; }
    }

    /// <summary>反射取字典对象（字段名 + 类型名双匹配，避免撞上同名但不同类型的成员）</summary>
    private static System.Collections.IDictionary? GetStuffDic(Bag bag)
    {
        if (_dicCache != null) return _dicCache;
        const System.Reflection.BindingFlags F =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance;
        try
        {
            foreach (var name in new[] { "stuff_dic", "stuff_dic_", "stuffDic" })
            {
                var fi = bag.GetType().GetField(name, F);
                if (fi != null && fi.GetValue(bag) is System.Collections.IDictionary d1)
                {
                    Plugin.LogV($"[Facility] bag 字典字段 = {name}（{d1.Count} 项）");
                    return _dicCache = d1;
                }
                var pi = bag.GetType().GetProperty(name, F);
                if (pi != null && pi.GetValue(bag) is System.Collections.IDictionary d2)
                {
                    Plugin.LogV($"[Facility] bag 字典属性 = {name}（{d2.Count} 项）");
                    return _dicCache = d2;
                }
            }
            // 兜底：扫所有 IDictionary 字段，挑键为 int 的那个
            foreach (var fi in bag.GetType().GetFields(F))
            {
                object? v = null;
                try { v = fi.GetValue(bag); } catch { }
                if (v is System.Collections.IDictionary d && LooksLikeStuffDic(d))
                {
                    Plugin.LogV($"[Facility] bag 字典（兜底扫描）= {fi.Name}（{d.Count} 项）");
                    return _dicCache = d;
                }
            }
        }
        catch (Exception ex) { Plugin.LogError($"[Facility] 取 bag 字典失败: {ex.Message}"); }
        return null;
    }

    private static System.Collections.IDictionary? _dicCache;

    private static bool LooksLikeStuffDic(System.Collections.IDictionary d)
    {
        int n = 0;
        foreach (var k in d.Keys)
        {
            if (k is int) n++;
            if (++n > 3) break;
        }
        return n > 0;
    }

    /// <summary>数仓库里某个物品有多少</summary>
    private static int CountInBag(Bag bag, int stuffId)
    {
        var dic = GetStuffDic(bag);
        if (dic == null) return 0;
        try { return dic.Contains(stuffId) ? System.Convert.ToInt32(dic[stuffId] ?? 0) : 0; }
        catch { return 0; }
    }

    /// <summary>从仓库扣掉指定数量（扣到 0 时移除键）</summary>
    private static void RemoveFromBag(Bag bag, int stuffId, int count)
    {
        var dic = GetStuffDic(bag);
        if (dic == null)
        {
            Plugin.LogError("[Facility] 扣料失败：拿不到 bag 的物品字典");
            return;
        }
        try
        {
            int have = dic.Contains(stuffId) ? System.Convert.ToInt32(dic[stuffId] ?? 0) : 0;
            int left = have - count;
            if (left > 0) dic[stuffId] = left;
            else dic.Remove(stuffId);
            Plugin.LogV($"[Facility] 扣料 物品{stuffId}：{have} → {Math.Max(0, left)}");
        }
        catch (Exception ex) { Plugin.LogError($"[Facility] 扣料异常 物品{stuffId}: {ex.Message}"); }
    }
}

/// <summary>
/// 一份冶炼配方：**输入 + 燃料 → 输出**。
/// 与游戏的 `blueprint.json` 无关 —— 那是工坊/熔炉机制的配方表，
/// 自研炉用自己的规格（这样 3×3 也能自由定义）。
/// </summary>
internal sealed class SmeltingRecipe
{
    /// <summary>每炉消耗的原料</summary>
    internal List<(int id, int n)> Inputs { get; init; } = new();

    /// <summary>每炉消耗的燃料 id（0 = 不需要燃料）</summary>
    internal int FuelId { get; init; }

    /// <summary>每炉消耗的燃料数量</summary>
    internal int FuelPerBatch { get; init; }

    /// <summary>产出物品 id</summary>
    internal int OutputId { get; init; }

    /// <summary>产出数量</summary>
    internal int OutputCount { get; init; } = 1;
}
