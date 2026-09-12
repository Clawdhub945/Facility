using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace FacilityMod;

[BepInPlugin(PLUGIN_GUID, PLUGIN_NAME, PLUGIN_VERSION)]
public class Plugin : BasePlugin
{
    public const string PLUGIN_GUID = "claude.facility";
    public const string PLUGIN_NAME = "Facility";
    public const string PLUGIN_VERSION = "1.0";

    /// <summary>综合生产所的设施 id（Defs/stuff.json + build.json + tech.json 同步）</summary>
    public const int FacilityId = 105040;

    internal static ManualLogSource Logger = null!;
    internal static BepInEx.Configuration.ConfigFile? ModConfigFile;
    internal static BepInEx.Configuration.ConfigEntry<string>? ProductsEntry;
    internal static BepInEx.Configuration.ConfigEntry<string>? ExtraCandidatesEntry;
    internal static BepInEx.Configuration.ConfigEntry<int>? ExtraProductEntry;
    internal static BepInEx.Configuration.ConfigEntry<int>? ExtraPerDayEntry;
    internal static BepInEx.Configuration.ConfigEntry<int>? WorkPosMaxEntry;
    internal static BepInEx.Configuration.ConfigEntry<bool>? VerboseEntry;
    private HarmonyLib.Harmony? _harmony;

    public override void Load()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        Logger = Log;

        try
        {
            ModConfigFile = Config;
            // 每人每日产出表："物品id:每人每日数量" 逗号分隔 —— 后续加产品直接追加即可（留好的扩展位）
            ProductsEntry = Config.Bind("生产", "每人每日产出", "604001:10,605001:10",
                new BepInEx.Configuration.ConfigDescription(
                    "格式：物品id:每人每日数量，多组用英文逗号分隔。604001=原木，605001=石料。" +
                    "默认 604001:10,605001:10 = 每个工人每个游戏日产出 10 原木 + 10 石料。" +
                    "要加其它产品就按同格式追加（例如 603001:5），改完最多 1.5 秒热生效。"));
            VerboseEntry = Config.Bind("调试", "日志详细模式", false,
                new BepInEx.Configuration.ConfigDescription(
                    "默认 false=安静模式（仅错误与换日汇总）。改为 true 输出每座建筑每项产出的明细日志。"));

            // 每座最大工位数：Harmony 补丁 Facility.GetOriginalWorkPosCount（仅 105040），
            // 建筑窗口的工人 +/- 控件即可在 1..该值 范围内原生调整人数
            WorkPosMaxEntry = Config.Bind("生产", "每座最大工位数", 10,
                new BepInEx.Configuration.ConfigDescription(
                    "建筑窗口里工人 +/- 可调整的上限。默认 10；改小/改大后窗口里即可生效。",
                    new BepInEx.Configuration.AcceptableValueRange<int>(1, 25)));

            // 额外产品（窗口下拉框）：候选清单 + 当前选择 + 每日数量
            ExtraCandidatesEntry = Config.Bind("生产", "额外产品候选", "616001:铁矿,616002:秘银矿,612001:红宝石",
                new BepInEx.Configuration.ConfigDescription(
                    "建筑窗口下拉框里的候选产品，格式 物品id:名称，逗号分隔。常用：616001=铁矿，" +
                    "616002=秘银矿，612001=红宝石，612002=蓝宝石，612003=绿宝石。"));
            ExtraProductEntry = Config.Bind("生产", "额外产品", 0,
                new BepInEx.Configuration.ConfigDescription(
                    "当前选中的额外产品（窗口下拉框写入）。0=无；改成物品 id 立即生效。"));
            ExtraPerDayEntry = Config.Bind("生产", "额外产品每日数量", 10,
                new BepInEx.Configuration.ConfigDescription(
                    "额外产品每人每个游戏日的产出数量。",
                    new BepInEx.Configuration.AcceptableValueRange<int>(1, 999)));

            LogInfo($"[Facility] 产出配置: {ProductsEntry.Value}, 最大工位数 {WorkPosMaxEntry.Value} (BepInEx/config/{PLUGIN_GUID}.cfg)");
        }
        catch (Exception ex) { LogError($"[Facility] 配置绑定失败: {ex}"); }

        // 失焦时 Unity Update 不跑 → 换日检测/产出全部饿死，与 JianZhu 同款处理
        try { Application.runInBackground = true; }
        catch (Exception ex) { LogError($"[Facility] 设置 runInBackground 失败: {ex.Message}"); }

        // 工位数补丁：GetOriginalWorkPosCount → cfg「每座最大工位数」（仅 105040 生效）
        try
        {
            _harmony = new HarmonyLib.Harmony(PLUGIN_GUID);
            _harmony.PatchAll();
            LogInfo("[Facility] 工位数补丁已挂：GetOriginalWorkPosCount（仅 105040）");
        }
        catch (Exception ex) { LogError($"[Facility] 挂工位数补丁失败: {ex}"); }

        ClassInjector.RegisterTypeInIl2Cpp<FacilityComponent>();
        var go = new GameObject("FacilityModRoot");
        GameObject.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<FacilityComponent>();

        LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} 已加载！");
    }

    private static string _lastBadRaw = "";

    /// <summary>当前选中的额外产品（0=无）。窗口下拉框写、产量循环读。</summary>
    internal static int ExtraProduct
    {
        get { try { return ExtraProductEntry?.Value ?? 0; } catch { return 0; } }
    }

    /// <summary>额外产品每人每日数量。</summary>
    internal static int ExtraPerDay
    {
        get { try { return ExtraPerDayEntry?.Value ?? 10; } catch { return 10; } }
    }

    internal static List<(int id, string name)> ParseExtraCandidates()
    {
        var list = new List<(int, string)>();
        string raw = ExtraCandidatesEntry?.Value ?? "";
        foreach (var seg in raw.Split(',', ';', '，', '；'))
        {
            var s = seg.Trim();
            if (s.Length == 0) continue;
            var i = s.IndexOf(':');
            if (i <= 0 || !int.TryParse(s[..i].Trim(), out int sid) || sid <= 0) continue;
            list.Add((sid, s[(i + 1)..].Trim()));
        }
        return list;
    }

    /// <summary>下拉框回调：写回 cfg（Config.Save 落盘），下一次换日即生效。</summary>
    internal static void SetExtraProduct(int stuffId)
    {
        try
        {
            if (ExtraProductEntry != null)
            {
                ExtraProductEntry.Value = stuffId;
                ModConfigFile?.Save();
                string name = "";
                foreach (var (id, nm) in ParseExtraCandidates())
                    if (id == stuffId) { name = nm; break; }
                LogInfo($"[Facility] 额外产品 → {(stuffId == 0 ? "无" : $"{name}({stuffId})")}，下一游戏日开始产出");
            }
        }
        catch (Exception ex) { LogError($"[Facility] 写额外产品配置失败: {ex.Message}"); }
    }

    /// <summary>解析「每人每日产出」配置：604001:10,605001:10 → [(604001,10),(605001,10)]。
    /// Config.Reload() 后 Value 已更新，每次换日重解析即可热生效。坏段跳过并告警（同一串只报一次）。</summary>
    internal static List<(int stuffId, int perDay)> ParseProducts()
    {
        var list = new List<(int, int)>();
        string raw = ProductsEntry?.Value ?? "";
        foreach (var seg in raw.Split(',', ';', '，', '；'))
        {
            var s = seg.Trim();
            if (s.Length == 0) continue;
            var i = s.IndexOf(':');
            if (i <= 0 || !int.TryParse(s[..i].Trim(), out int sid) ||
                !int.TryParse(s[(i + 1)..].Trim(), out int per) || sid <= 0 || per <= 0)
            {
                if (raw != _lastBadRaw)
                    LogWarning($"[Facility] 产出配置有坏段已跳过: 「{s}」（正确格式 物品id:数量）");
                continue;
            }
            list.Add((sid, per));
        }
        _lastBadRaw = raw;
        return list;
    }

    internal static void LogInfo(string msg) => Logger.LogInfo(msg);
    internal static void LogWarning(string msg) => Logger.LogWarning(msg);
    internal static void LogError(string msg) => Logger.LogError(msg);

    /// <summary>诊断日志：仅 cfg「调试.日志详细模式」=true 时输出</summary>
    internal static void LogV(string msg)
    {
        try { if (VerboseEntry != null && !VerboseEntry.Value) return; }
        catch { }
        Logger.LogInfo(msg);
    }
}
