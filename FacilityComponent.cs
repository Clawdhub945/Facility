using System;
using System.Collections.Generic;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// IL2CPP 注入的 MonoBehaviour：主线程驱动「综合生产所」(105040) 的每日产出。
/// 建筑本体是 Def 注入（复制采集营地行：原生工人系统/人数可调/自带 bag），
/// 本组件在游戏换日时按 工人数 × cfg 产出表 调 Facility.AddStuff 入袋 +
/// Facility.RecordProduct 记入产量统计，之后由游戏原生搬运工把产出搬进仓库。
/// </summary>
public class FacilityComponent : MonoBehaviour
{
    private float _nextAt;
    private int _lastDay = -1;
    private bool _dayApiBroken;

    /// <summary>本游戏日已经发过产的建筑 guid（防同一日重复产出；换日清空）。</summary>
    private readonly HashSet<int> _producedToday = new();

    /// <summary>自动化测试热键：F8 程序化点击窗口里的「额外产品」下拉框（每帧只触发一次）。</summary>
    private bool _testKeyLatch;

    /// <summary>F10 = 打开建筑窗口（无人值守测试用）</summary>
    private bool _openKeyLatch;

    /// <summary>F12 = UI 构建实验开关</summary>
    private bool _uiProbeLatch;

    /// <summary>预制体清单只打一次（找游戏自带 UI 预制体用）</summary>
    private bool _dumpedPrefabs;

    

    /// <summary>
    /// ⚠ 曾经的诊断热键 F9（往日志里倒设施清单）**已移除**：
    /// 用户反馈「按下 F9 后刷屏太多」——它一次要打几十行对象信息，
    /// 而且诊断目的已经达到（移动预览体的问题已修好）。留着只会制造噪音。
    /// 需要时改用 `CustomSprite.DumpAllFacilities(tag)` 手动调一次即可。
    /// </summary>

    /// <summary>自定义外观的延迟初始化开关（每帧轮询，成功后置 true 不再试）</summary>
    private bool _customSpriteReady;

    /// <summary>
    /// 主线程循环（每帧）：
    ///   1. 轮询热键（F8 测试点击下拉框 / F9 抓设施清单）
    ///   2. 维持自定义外观（每帧状态校验）
    ///   3. 每 1.5 秒检查一次 cfg 与游戏日历，跨日就发产
    ///
    /// ⚠ 这个方法是本 mod 的命脉，**改动前务必确认它还在**：
    /// 曾经被脚本误删过一次（文件里只剩 Poll 系列方法），结果每日产出悄悄停摆 ——
    /// 编译照样通过、游戏不报错，唯一症状是「日志里不再出现产出记录」。
    /// </summary>
    private void Update()
    {

        // ★ 自绘 UI 自检：进档后自动跑一次（不依赖开窗热键）。
        //   目的：把"画布的真实坐标读数 + 红块实际位置"打进日志，
        //   这样即使 F10 开窗失效也能定位问题（实测 F10 在自动化环境里时好时坏）。
        try { SpriteUiSelfTest.Tick(); } catch { }
        PollTestHotkey();
        PollCustomSprite();

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
            _producedToday.Clear();

            ProduceForNewDay(day);
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Facility] Update 异常: {ex}");
        }
    }

    /// <summary>
    /// 自定义外观 / 图标的**延迟初始化**：插件 Load() 时世界还没加载，
    /// 场景里没有本 mod 的建筑当模板、`SpriteManager.Ins` 也可能还没建好，
    /// 所以放到主线程 Update 里反复试。
    /// ⚠ 两项**各自**重试到成功为止：早期版本用「prefab 到位 && 图标非空」当完成条件，
    /// 结果是 prefab 成了就不再重试，而图标注册其实是失败/没生效的（菜单一直是白块）。
    /// </summary>
    private void PollCustomSprite()
    {
        try
        {
            // 图标注册：只需要成功一次
            if (!_customSpriteReady)
            {
                if (CustomSprite.RegisterIcon() && CustomSprite.IconSprite != null)
                    _customSpriteReady = true;
            }
            // 补外观：**不能只在初始化时做一次** —— 读档/新造/移动的建筑都要补，
            // 所以每帧都扫一遍（FindObjectsOfType 很便宜，建筑数量也就几座）。
            // 早期版本把它放在 _customSpriteReady 判断里，导致读档后已存在的建筑漏掉换图。
            CustomSprite.ReapplyToAll();

            // 进档后打一次预制体清单（详细模式）——找游戏自带的 UI 预制体
            // （目标是原生下拉 StuffIconDropdown / dp_item_list）
            if (!_dumpedPrefabs && Time.frameCount > 120)
            {
                _dumpedPrefabs = true;
                PrefabProbe.DumpRecipeTables();
                PrefabProbe.Dump("dropdown");
                PrefabProbe.Dump("dp_item");
                PrefabProbe.Dump("window_");
            }

            // 被「移动设施」等流程要求强制补一次时，顺带打一份设施清单（详细模式），
            // 用来认出「移动过程中跟随光标的预览体」是什么对象。
            if (CustomSprite.ConsumeForceFlag())
                CustomSprite.DumpAllFacilities("移动/强制补外观");
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] 自定义外观轮询异常: {ex.Message}"); }
    }

    /// <summary>
    /// 测试钩子：按 F8 = 程序化点击选择条（走 Unity 原生事件系统）。
    /// 存在的理由：无人值守 UI 测试里，OS 级合成鼠标点击的坐标标定很容易碎
    /// （游戏窗口分辨率会变：实测同一台机器上出现过 1440×1080 / 1440×900 / 2560×1417），
    /// 而 uGUI 事件派发是确定性的，能稳定验证「按钮 → 写 cfg」这条链路。
    /// OS 级真点击的链路见 `_tools/e2e.py`（截图识别 + SetCursorPos）。
    /// </summary>
    private void PollTestHotkey()
    {
        try
        {
            // 用 UnityEngine.InputLegacyModule 的旧输入 API（游戏本体就是旧输入派发，
            // 单纯 UnityEngine.Input 在 IL2CPP interop 里没有类型）。
            bool down = UnityEngine.Input.GetKey(UnityEngine.KeyCode.F8);
            if (down && !_testKeyLatch) FacilityWindowUi.TestClickSelector();
            _testKeyLatch = down;

            // F10 = 打开建筑窗口（无人值守测试用，OS 级鼠标点击标定太脆）。
            // **Shift+F10** = 打开「熔炉对照实验」(105052)，用来和 3×3 高炉做对照。
            // F12 = UI 构建实验（切换显示自建小组件）。
            // 只在 cfg 详细模式开启时生效 —— 这是「运行时构建 uGUI 会不会崩游戏」的验证开关。
            bool down12 = UnityEngine.Input.GetKey(UnityEngine.KeyCode.F12);
            if (down12 && !_uiProbeLatch)
            {
                if (Plugin.VerboseEntry?.Value == true)
                    UiProbe.Toggle(FacilityWindowUi.CurrentWindowForProbe());
                else
                    Plugin.LogV("[Facility] UI 实验需要打开 cfg「调试.日志详细模式」");
            }
            _uiProbeLatch = down12;

            bool down10 = UnityEngine.Input.GetKey(UnityEngine.KeyCode.F10);
            if (down10 && !_openKeyLatch)
            {
                bool shift = false;
                try
                {
                    shift = UnityEngine.Input.GetKey(UnityEngine.KeyCode.LeftShift)
                            || UnityEngine.Input.GetKey(UnityEngine.KeyCode.RightShift);
                }
                catch { }
                FacilityWindowUi.PreferredProbeStuffId = shift ? Plugin.FurnaceNativeTestId : 0;
                FacilityWindowUi.TestOpenWindow();
            }
            _openKeyLatch = down10;
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] 测试热键轮询失败: {ex.Message}"); }
    }

    /// <summary>
    /// 游戏日序号 = <c>Clock.Days</c>（官方单调递增天数，游戏源码里 264 处都这么判日：
    /// <c>Animal.cs:571</c>、<c>Npc.cs:9061</c>、<c>Facility.cs:1500</c> 等）。
    ///
    /// ⚠ 0.4.0 教训：原先用 <c>Clock.Now.Year*10000+MonthInYear*100+DayInMonth</c> 判日，
    /// 实测会**在一天之内反复给出不同的「日期」**——上一轮日志里 3 座建筑刷了 512 次
    /// 「新的一天」（袋子被灌爆）。<c>Clock.Now</c> 是游戏内部的高精度时间对象，
    /// 判日必须用 <c>Clock.Days</c>（存档 summary.sav 里的 <c>days</c> 字段就是它）。
    /// 兜底：<c>Clock.Days</c> 不可用时退回旧的 Now 组合，并且只在值变化时才产出（双重保险）。
    /// </summary>
    private int GetDayKey()
    {
        if (_dayApiBroken) return -1;
        try
        {
            return Clock.Days;
        }
        catch (Exception ex)
        {
            _dayApiBroken = true;
            Plugin.LogError($"[Facility] 读取 Clock.Days 失败，每日产出停用: {ex.Message}");
            return -1;
        }
    }

    /// <summary>诊断用：把两套日期 API 的原始值打一行，方便下次排查换日问题。</summary>
    private void LogClockDiagnostics(int dayKey)
    {
        try
        {
            var now = Clock.Now;
            Plugin.LogV($"[Facility] 换日诊断: Clock.Days={dayKey} (上次={_lastDay}) " +
                        $"Now.Year={now.Year} Now.MonthInYear={now.MonthInYear}");
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] 换日诊断失败: {ex.Message}"); }
    }


    /// <summary>
    /// 换日流程：诊断输出 + 交给 <see cref="FacilityProducer"/> 发产。
    ///
    /// ## 重构说明
    /// 早期产出逻辑（筛建筑、按工人数发产、入袋、记账）**全写在这个方法里**，
    /// 用的是全局判据 —— 结果改一座建筑的行为会连带影响其他建筑
    /// （用户实测：「你改高炉不再产木石，其他 2 种也跟着不生产了」）。
    /// 现在产出逻辑搬到 `FacilityProducer`，**只认 `BuildingSpec.DailyProducer`**，
    /// 与窗口/归属逻辑彻底解耦。本方法只剩：诊断 + 转调。
    /// </summary>
    private void ProduceForNewDay(int dayKey)
    {
        LogClockDiagnostics(dayKey);
        LogCareerRows();

        // 同日防重：万一日期判断退化（读档后时钟抖动），同一座建筑一天只发一次
        _producedToday.Clear();

        FacilityProducer.ProduceForAll(dayKey);

        // 自研冶炼炉（3×3 高炉这类原生机制做不出的建筑）：换日结算一次
        SmelterConsumer.OnNewDay();
        SmelterConsumer.ProcessAll();
    }

    /// <summary>诊断：打印各建筑 career 行的真实字段（排查工位数/工人上限问题用）</summary>
    private void LogCareerRows()
    {
        try
        {
            var dic = D.Ins.career_dic_with_facility_id_as_key;
            foreach (int fid in Plugin.ManagedFacilityIds)
            {
                if (dic != null && dic.ContainsKey(fid))
                {
                    var ci = dic[fid];
                    Plugin.LogV($"[Facility] career行 {fid}: data_id={ci.data_id} limit={ci.manpower_limit} " +
                                $"factor={ci.manpower_factor} npc_type={ci.npc_type} main={ci.is_main_facility}");
                }
                else Plugin.LogV($"[Facility] career行: 字典中无 {fid}！");
            }
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] career dump 失败: {ex.Message}"); }
    }
}
