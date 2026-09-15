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
    /// <summary>
    /// 版本号：**每次改动都往上加**，启动横幅会打出来。
    /// 用途：确认游戏加载的到底是哪一版 DLL —— 这个项目踩过「改了代码但游戏跑的是旧 DLL」
    /// 的坑（日志刷屏怎么关都关不掉，就是因为在看旧版本的输出）。
    /// </summary>
    public const string PLUGIN_VERSION = "1.2.0-super";

    /// <summary>综合生产所的设施 id（Defs/stuff.json + build.json + tech.json 同步）</summary>
    public const int FacilityId = 105040;

    /// <summary>超级生产所的设施 id（自定义外观，挂在自己新建的科技节点 909050 下）</summary>
    public const int SuperFacilityId = 105050;

    /// <summary>
    /// 实验建筑 105051「三乘三高炉实验」：
    /// 3×3 占地 + **熔炉机制**（`FacilityFurnace`）+ 熔炉窗口（`window_furnace`）。
    ///
    /// 目的：验证工业 mod 要用的「高炉烧煤（煤当燃料）」能不能做成 3×3。
    /// 背景：熔炉原生是 2×2、矿井是 3×3；而熔炉的 `CreateMaterialsPosList`
    /// 会按格子摆材料位置，**对占地可能有硬假设** —— 这个实验就是来验它的。
    ///
    /// ⚠ 它**不参与**本 mod 的每日产出逻辑（不是 `FacilityGatherersHut`）；
    /// 放进 <see cref="ManagedFacilityIds"/> 只为两件事：窗口归属判断、不误伤原版窗口。
    /// </summary>
    public const int Furnace3TestId = 105051;

    /// <summary>
    /// 对照实验 105052「熔炉对照实验」：完全照抄原生熔炉的骨架/类/窗口（2×2）。
    /// 与 105051 只有骨架 prefab 不同，用来定位「窗口能显示但控件不能交互」的原因。
    /// </summary>
    public const int FurnaceNativeTestId = 105052;

    /// <summary>本 mod 管辖的全部设施 id（产出循环 / 工位数补丁 / 窗口 UI 都用它判归属）</summary>
    /// <summary>
    /// 本 mod 管辖的全部设施 id。
    /// ⚠ 现在**从 Buildings 规格表派生**（唯一事实来源）——
    /// 加新建筑只需在 BuildingSpec.cs 里加一条规格，不必再改这里。
    /// </summary>
    public static int[] ManagedFacilityIds => Buildings.AllIds();

    /// <summary>该设施是否由本 mod 添加</summary>
    public static bool IsManaged(int stuffId)
    {
        foreach (int id in ManagedFacilityIds)
            if (id == stuffId) return true;
        return false;
    }

    internal static ManualLogSource Logger = null!;
    internal static BepInEx.Configuration.ConfigFile? ModConfigFile;
    internal static BepInEx.Configuration.ConfigEntry<string>? ProductsEntry;
    internal static BepInEx.Configuration.ConfigEntry<string>? ExtraCandidatesEntry;
    internal static BepInEx.Configuration.ConfigEntry<int>? ExtraProductEntry;
    internal static BepInEx.Configuration.ConfigEntry<int>? ExtraPerDayEntry;
    internal static BepInEx.Configuration.ConfigEntry<int>? WorkPosMaxEntry;
    internal static BepInEx.Configuration.ConfigEntry<bool>? VerboseEntry;
    /// <summary>超级生产所自定义贴图的整体缩放（cfg「外观.超级生产所贴图缩放」）</summary>
    internal static BepInEx.Configuration.ConfigEntry<float>? SpriteScaleEntry;
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

            // 自定义外观缩放：改完**最多 1.5 秒热生效**（不用重启游戏）
            SpriteScaleEntry = Config.Bind("外观", "超级生产所贴图缩放", 1.3f,
                new BepInEx.Configuration.ConfigDescription(
                    "超级生产所自定义贴图的整体缩放。1.0 = 按 64 像素/格 1:1 渲染。" +
                    "觉得模型比占地格小就调大、大了就调小；改完最多 1.5 秒生效，无需重启。",
                    new BepInEx.Configuration.AcceptableValueRange<float>(0.3f, 3f)));

            LogV($"[Facility] 产出配置: {ProductsEntry.Value}, 最大工位数 {WorkPosMaxEntry.Value} (BepInEx/config/{PLUGIN_GUID}.cfg)");
        }
        catch (Exception ex) { LogError($"[Facility] 配置绑定失败: {ex}"); }

        // 失焦时 Unity Update 不跑 → 换日检测/产出全部饿死，与 JianZhu 同款处理
        try { Application.runInBackground = true; }
        catch (Exception ex) { LogError($"[Facility] 设置 runInBackground 失败: {ex.Message}"); }

        // 工位数补丁 + 自定义外观补丁。
        // ⚠ 这里**逐个类打补丁**并各自 try/catch，而不是一把 `PatchAll()`：
        // 实测事故——自定义外观补丁因为找不到方法抛异常，`PatchAll()` 整条中断，
        // 结果**工位数补丁也没挂上**（窗口 +/- 失效），而且只有一行 Error 容易看漏。
        try
        {
            _harmony = new HarmonyLib.Harmony(PLUGIN_GUID);
            int ok = 0, fail = 0;
            foreach (var t in new[]
                     {
                         typeof(FacilityGetOriginalWorkPosCountPatch),
                         typeof(CustomAppearancePatch),
                         typeof(FacilityWindowPatches),
                         typeof(NativeWindowSafetyPatch),
                                              })
            {
                try
                {
                    _harmony.CreateClassProcessor(t).Patch();
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    LogError($"[Facility] 补丁 {t.Name} 挂载失败（其余补丁不受影响）: {ex.Message}");
                }
            }
            LogBanner($"[Facility] 补丁挂载完成：成功 {ok} 个，失败 {fail} 个（工位数 / 自定义外观 / 窗口UI）");
        }
        catch (Exception ex) { LogError($"[Facility] 打补丁失败: {ex}"); }

        DumpPrefabManagerApi();
        DumpSpriteManagerApi();

        // ⚠ 自定义外观必须**尽早**建好：游戏的 Def 加载（`D.LoadData`）会按名字解析
        // stuff.json 里的 prefab，那一刻要是拿不到，建筑就是「没有模型」的状态
        // （表现：建造菜单图标空、放置时空引用 / 显示成别的建筑）。
        // 早期版本把建 prefab 放在 Update 里延迟做，等它建好时 Def 早就解析完了 —— 所以一直放不下去。
        CustomSprite.RegisterIcon();
        CustomSprite.ReapplyToAll();

        ClassInjector.RegisterTypeInIl2Cpp<FacilityComponent>();
        var go = new GameObject("FacilityModRoot");
        GameObject.DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<FacilityComponent>();

        LogBanner($"{PLUGIN_NAME} v{PLUGIN_VERSION} 已加载！");
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
                LogV($"[Facility] 额外产品 → {(stuffId == 0 ? "无" : $"{name}({stuffId})")}，下一游戏日开始产出");
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
                    LogV($"[Facility] 产出配置有坏段已跳过: 「{s}」（正确格式 物品id:数量）");
                continue;
            }
            list.Add((sid, per));
        }
        _lastBadRaw = raw;
        return list;
    }

    /// <summary>
    /// 诊断：把 `PrefabManager` 的全部方法名打出来（详细模式）。
    /// 用途：自定义外观要拦「按名字取 prefab」的入口，而 interop 里到底暴露了哪些方法
    /// 只能运行时看（实测 `GetPrefab` 私有、无 C# 绑定，`AccessTools.Method` 找不到）。
    /// </summary>
    private static void DumpPrefabManagerApi()
    {
        try
        {
            if (VerboseEntry == null || !VerboseEntry.Value) return;
            var sb = new System.Text.StringBuilder("[Facility] PrefabManager 方法清单:\n");
            foreach (var m in typeof(PrefabManager).GetMethods(
                         System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                         System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
                         System.Reflection.BindingFlags.DeclaredOnly))
            {
                var ps = m.GetParameters();
                sb.Append("  ").Append(m.IsPublic ? "public " : "nonpub ")
                  .Append(m.IsStatic ? "static " : "inst ")
                  .Append(m.ReturnType.Name).Append(' ').Append(m.Name).Append('(');
                for (int i = 0; i < ps.Length; i++)
                    sb.Append(i > 0 ? ", " : "").Append(ps[i].ParameterType.Name);
                sb.Append(")\n");
            }
            LogV(sb.ToString());
        }
        catch (Exception ex) { LogV($"[Facility] dump PrefabManager 失败: {ex.Message}"); }
    }

    /// <summary>诊断：把 `SpriteManager` 的全部方法打出来（详细模式），用于核对图标补丁的目标签名</summary>
    private static void DumpSpriteManagerApi()
    {
        try
        {
            if (VerboseEntry == null || !VerboseEntry.Value) return;
            var sb = new System.Text.StringBuilder("[Facility] SpriteManager 方法清单:\n");
            var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.DeclaredOnly;
            for (var t = typeof(SpriteManager); t != null && t != typeof(object); t = t.BaseType)
            {
                sb.Append("  [").Append(t.Name).Append("]\n");
                System.Reflection.MethodInfo[] ms;
                try { ms = t.GetMethods(flags); } catch { continue; }
                foreach (var m in ms)
                {
                    var ps = m.GetParameters();
                    sb.Append("    ").Append(m.IsPublic ? "public " : "nonpub ")
                      .Append(m.IsStatic ? "static " : "inst ")
                      .Append(m.ReturnType.Name).Append(' ').Append(m.Name).Append('(');
                    for (int i = 0; i < ps.Length; i++)
                        sb.Append(i > 0 ? ", " : "").Append(ps[i].ParameterType.Name);
                    sb.Append(")\n");
                }
            }
            LogV(sb.ToString());
        }
        catch (Exception ex) { LogV($"[Facility] dump SpriteManager 失败: {ex.Message}"); }
    }

    // ==================== 日志 ====================
    //
    // 静默模式（默认）下**只输出错误**。用户明确要求：不要把换日诊断、每座建筑明细、
    // career 行、每日汇总这些刷进日志。
    //
    // 实现要点（这几条都是踩过坑的）：
    //   1. `LogV` 只认 cfg「调试.日志详细模式」，**不缓存**——每 1.5 秒的 cfg.Reload()
    //      会刷新 ConfigEntry.Value，所以要每次读。
    //   2. 即使 cfg 读不到 / 抛异常，也**默认静默**（catch 里直接 return），
    //      绝不「读不到就当详细模式开」。
    //   3. 详细模式打开时仍然有节流：同一类明细最多每 `_verboseIntervalSec` 秒一次，
    //      避免又变成刷屏（真要全量就调大间隔或看 Player.log）。

    /// <summary>上次输出明细日志的时间（节流用；详细模式下也不会刷屏）</summary>
    private static float _lastVerboseAt;
    private const float VerboseIntervalSec = 0.0f;   // 0 = 不节流（详细模式就全量输出）

    internal static void LogError(string msg)
    {
        try { Logger.LogError(msg); } catch { }
    }

    /// <summary>启动横幅：每次启动各一条，方便确认 mod 到底有没有加载、加载的是哪一版</summary>
    internal static void LogBanner(string msg)
    {
        try { Logger.LogInfo(msg); } catch { }
    }

    /// <summary>诊断日志：只有 cfg「调试.日志详细模式」= true 才输出；其余一律丢弃</summary>
    internal static void LogV(string msg)
    {
        bool verbose;
        try { verbose = VerboseEntry != null && VerboseEntry.Value; }
        catch { verbose = false; }          // 读配置失败 → 静默（绝不因此刷屏）
        if (!verbose) return;

        if (VerboseIntervalSec > 0f)
        {
            float now = 0f;
            try { now = UnityEngine.Time.realtimeSinceStartup; } catch { }
            if (now - _lastVerboseAt < VerboseIntervalSec) return;
            _lastVerboseAt = now;
        }
        try { Logger.LogInfo(msg); } catch { }
    }
}
