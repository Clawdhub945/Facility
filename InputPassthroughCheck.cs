using System;
using UnityEngine;
using UnityEngine.UI;

namespace FacilityMod;

/// <summary>
/// **输入穿透自检** —— 进档后自动做一次严格的输入隔离测试，把结果打进日志。
///
/// ## 为什么需要它
/// 用户反馈"打开面板后无法关闭/点击/移动"。已做的修复：
/// 移除 `GraphicRaycaster`、所有 `Image.raycastTarget=false`、
/// Canvas 加 `CanvasGroup{blocksRaycasts=false, interactable=false}`。
/// 但问题仍在 —— 需要**确定性数据**来判断到底是谁在拦输入，而不是继续猜。
///
/// ## 测试内容
/// 1. 我们 Canvas 上到底有哪些"会拦输入"的组件
///    （`GraphicRaycaster` / `CanvasGroup` / `Image.raycastTarget` /
///      `EventSystem` 状态 / `Canvas.overrideSorting` / `sortingOrder`）
/// 2. 场景里有几个 `EventSystem`、几个 `GraphicRaycaster`
/// 3. **同一时刻游戏自己的 Canvas 设置**（对比）
/// 4. 我们自己 Canvas 的 `GetComponentsInChildren<Graphic>()` 里有几个 raycastTarget=true
/// </summary>
internal static class InputPassthroughCheck
{
    private static bool _done;

    internal static void Tick()
    {
        if (_done) return;
        if (Plugin.VerboseEntry?.Value != true) return;   // 只在详细模式跑
        Facility[] all = Array.Empty<Facility>();
        try { all = UnityEngine.Object.FindObjectsOfType<Facility>(); } catch { }
        if (all == null || all.Length < 3) return;
        _done = true;
        try { Run(); } catch (Exception ex) { Plugin.LogError($"[FacilityUI] 输入自检异常: {ex}"); }
    }

    private static void Run()
    {
        Plugin.LogV("[FacilityUI] ===== 输入穿透自检 =====");

        // ① EventSystem
        try
        {
            var systems = UnityEngine.Object.FindObjectsOfType<UnityEngine.EventSystems.EventSystem>();
            Plugin.LogV($"[FacilityUI] ① EventSystem 数量 = {systems?.Length ?? 0}");
            foreach (var es in systems)
            {
                if (es == null) continue;
                Plugin.LogV($"[FacilityUI]    {es.name}: enabled={es.enabled} " +
                            $"sendNavigationEvents={es.sendNavigationEvents} " +
                            $"currentSelected={(es.currentSelectedGameObject != null ? es.currentSelectedGameObject.name : "null")}");
            }
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] ① 失败: {ex.Message}"); }

        // ② 场景里的 GraphicRaycaster
        try
        {
            var rays = UnityEngine.Object.FindObjectsOfType<GraphicRaycaster>();
            Plugin.LogV($"[FacilityUI] ② 场景 GraphicRaycaster 数量 = {rays?.Length ?? 0}");
            foreach (var r in rays)
            {
                if (r == null) continue;
                var c = r.GetComponent<Canvas>();
                Plugin.LogV($"[FacilityUI]    {r.name}: enabled={r.enabled} " +
                            $"canvas={(c != null ? c.name : "?")} order={(c != null ? c.sortingOrder : -1)} " +
                            $"override={(c != null && c.overrideSorting)}");
            }
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] ② 失败: {ex.Message}"); }

        // ③ 我们自己的 Canvas 逐项检查
        try
        {
            var mine = UiKit.Root;
            if (mine == null) { Plugin.LogV("[FacilityUI] ③ 我们的 Canvas 不存在（未渲染）"); }
            else
            {
                var c = mine.GetComponent<Canvas>();
                var cg = mine.GetComponent<CanvasGroup>();
                var gr = mine.GetComponent<GraphicRaycaster>();
                int total = 0, blocking = 0;
                foreach (var g in mine.GetComponentsInChildren<Graphic>(true))
                {
                    if (g == null) continue;
                    total++;
                    if (g.raycastTarget) blocking++;
                }
                Plugin.LogV($"[FacilityUI] ③ 我们的 Canvas: layer={mine.layer} " +
                            $"renderMode={c?.renderMode} order={c?.sortingOrder} override={c?.overrideSorting} " +
                            $"active={mine.activeInHierarchy}");
                Plugin.LogV($"[FacilityUI] ③   GraphicRaycaster={(gr != null ? "【存在！会拦输入】" : "无 ✔")}");
                Plugin.LogV($"[FacilityUI] ③   CanvasGroup={(cg != null ? $"blocksRaycasts={cg.blocksRaycasts} interactable={cg.interactable}" : "无")}");
                Plugin.LogV($"[FacilityUI] ③   Graphic 总数={total}，其中 raycastTarget=true 的 = {blocking}" +
                            (blocking == 0 ? " ✔" : " 【会拦输入！】"));

                // 逐个列出还在拦的（如果有）
                if (blocking > 0)
                {
                    foreach (var g in mine.GetComponentsInChildren<Graphic>(true))
                    {
                        if (g == null || !g.raycastTarget) continue;
                        Plugin.LogV($"[FacilityUI] ③   拦截者: {g.name} ({g.GetType().Name})");
                    }
                }
            }
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] ③ 失败: {ex.Message}"); }

        Plugin.LogV("[FacilityUI] ===== 输入穿透自检结束 =====");
    }
}
