using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
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
    internal static BepInEx.Configuration.ConfigEntry<bool>? VerboseEntry;

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

            LogInfo($"[Facility] 产出配置: {ProductsEntry.Value} (BepInEx/config/{PLUGIN_GUID}.cfg)");
        }
        catch (Exception ex) { LogError($"[Facility] 配置绑定失败: {ex}"); }

        // 失焦时 Unity Update 不跑 → 换日检测/产出全部饿死，与 JianZhu 同款处理
        try { Application.runInBackground = true; }
        catch (Exception ex) { LogError($"[Facility] 设置 runInBackground 失败: {ex.Message}"); }

        ClassInjector.RegisterTypeInIl2Cpp<FacilityComponent>();
        var go = new GameObject("FacilityModRoot");
        GameObject.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<FacilityComponent>();

        LogInfo($"{PLUGIN_NAME} v{PLUGIN_VERSION} 已加载！");
    }

    private static string _lastBadRaw = "";

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
