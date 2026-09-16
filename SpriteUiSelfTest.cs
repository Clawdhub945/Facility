using System;
using TMPro;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// **自绘 UI 自检** —— 进档后自动跑一次，把关键读数打进日志。
///
/// ## 为什么需要它
/// 定位自绘 UI 问题时，我依赖过"按 F10 开窗"来触发代码，但实测 **F10 在自动化环境里
/// 时好时坏**（有时 `Apply` 一次都不跑），导致反复出现"日志里什么都没有"。
/// 这个自检挂在 `FacilityComponent.Update` 上，**只要进档就会跑**，不依赖任何热键。
///
/// ## 它做什么（只做一次）
/// 1. 打印**画布的真实读数**（`Coords.DescribeCanvas`）——
///    这是定位"世界坐标算错"的第一手数据
///    （踩过：`GetComponentInParent&lt;Canvas&gt;()` 返回 null → 退化成 (0,0)
///      → 红块被放到屏幕外约 5 倍高度处）
/// 2. 在**屏幕中心**画一个红块（用 `Coords.ScreenToWorld` 换算），
///    并回算它落在屏幕的哪个像素处 —— 换算对了就应该回到屏幕中心
/// 3. 把结果打进日志（`UI 自检:` 前缀），便于一眼比对
///
/// 设 `cfg [UI模板] 自检 = false` 可关闭。
/// </summary>
internal static class SpriteUiSelfTest
{
    private static bool _done;
    private static GameObject? _root;

    /// <summary>每帧被调用；只在进档后跑一次</summary>
    internal static void Tick()
    {
        if (_done) return;

        // 等主相机就绪（进档过程中相机可能还没建好）
        Camera? cam = null;
        try { cam = Camera.main; } catch { }
        if (cam == null) return;

        // 等设施加载完（有建筑才说明进档到位）
        Facility[] all = Array.Empty<Facility>();
        try { all = UnityEngine.Object.FindObjectsOfType<Facility>(); } catch { }
        if (all == null || all.Length < 3) return;

        _done = true;
        try { Run(); }
        catch (Exception ex) { Plugin.LogError($"[FacilityUI] UI 自检异常: {ex}"); }
    }

    private static void Run()
    {
        Plugin.LogV("[FacilityUI] ===== UI 自检开始（UiKit v2：Image 方案）=====");

        // ① 建独立 Canvas（不挂在游戏窗口里 —— 窗口根的 rect 是 550x0，挂进去尺寸全算不出来）
        var canvas = UiKit.EnsureRoot();
        if (canvas == null) { Plugin.LogV("[FacilityUI] 自检：Canvas 建不出来，终止"); return; }
        _root = canvas;
        var crt = canvas.GetComponent<RectTransform>();
        Plugin.LogV($"[FacilityUI] 自检 ① Canvas: rect={crt?.rect.width:0}x{crt?.rect.height:0}" +
                    $" renderMode={canvas.GetComponent<Canvas>()?.renderMode}" +
                    $" order={canvas.GetComponent<Canvas>()?.sortingOrder} layer={canvas.layer}" +
                    $" 屏幕={Screen.width}x{Screen.height}");

        // ② 画一块深色面板 + 红/绿/蓝三个色块（三色便于在截图里一眼定位）
        try
        {
            var panel = UiKit.AddPanel(crt, "panel", new Color(0.12f, 0.11f, 0.10f, 0.92f),
                                       Vector2.zero, new Vector2(420f, 220f));
            Plugin.LogV($"[FacilityUI] 自检 ② 面板: {(panel != null ? $"size={panel.sizeDelta} pos={panel.anchoredPosition}" : "失败")}");

            // 三个色块（在面板内，左上起排）
            UiKit.AddPanel(panel, "sw_red", new Color(1f, 0f, 0f, 0.95f),
                           new Vector2(-130f, 60f), new Vector2(100f, 60f));
            UiKit.AddPanel(panel, "sw_green", new Color(0f, 1f, 0f, 0.95f),
                           new Vector2(0f, 60f), new Vector2(100f, 60f));
            UiKit.AddPanel(panel, "sw_blue", new Color(0.2f, 0.5f, 1f, 0.95f),
                           new Vector2(130f, 60f), new Vector2(100f, 60f));

            // 文字
            UiKit.AddText(panel, "title", "UiKit v2 自检：面板 + 文字 + 图标",
                          new Vector2(0f, 0f), new Vector2(400f, 40f), 18f,
                          TextAlignmentOptions.Center);
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 自检 ② 失败: {ex.Message}"); }

        // ③ 图标（用游戏贴图：钢 / 钢制工具 / 高炉）
        try
        {
            var panel = _root.transform.Find("panel") as RectTransform;
            if (panel != null)
            {
                int i = 0;
                foreach (var (nm, id) in new[] { ("钢", "ui_603010"), ("钢制工具", "ui_403010"), ("高炉", "ui_105054") })
                {
                    float x = -130f + i * 130f;
                    var img = UiKit.AddIcon(panel, "icon_" + id, id,
                                            new Vector2(x, -60f), 44f);
                    UiKit.AddText(panel, "cap_" + id, nm, new Vector2(x, -95f),
                                  new Vector2(120f, 20f), 12f, TextAlignmentOptions.Center);
                    Plugin.LogV($"[FacilityUI] 自检 ③ 图标 {id}: " +
                                $"{(img != null ? (img.sprite != null ? "贴图有" : "【贴图 null → 灰块】") : "创建失败")}");
                    i++;
                }
            }
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 自检 ③ 失败: {ex.Message}"); }

        Plugin.LogV("[FacilityUI] ===== UI 自检结束 =====");
    }

    private static string ComponentNames(GameObject go)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in go.GetComponents<Component>())
                sb.Append(c == null ? "null," : c.GetType().Name + ",");
            return sb.ToString().TrimEnd(',');
        }
        catch { return "?"; }
    }

    internal static void Cleanup()
    {
        try { UiKit.Destroy(); } catch { }
        _root = null;
    }
}
