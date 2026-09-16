using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FacilityMod;

/// <summary>
/// **运行时构建 UI 的能力验证**（小实验，见 `docs/UI模板.md`）。
///
/// ## 为什么要单独验证
/// 早前为了给下拉框做"金色描边"，在运行时**新增了一个 GameObject**并调
/// `SetAsFirstSibling`，结果**游戏进档后静默退出**（连 `BepInEx/ErrorLog.log` 都没写）。
/// 那次加的是带 `SpriteRenderer` 的物体；这次要构建的是**纯 uGUI**，
/// 风险性质不同，但**必须实测确认**，否则整套「通用 UI 模板」都建在流沙上。
///
/// ## 这个实验做什么
/// 在**当前打开的窗口**里挂一个自建小组件（背景图 + 一行文字），
/// 观察游戏是否稳定：不崩、能正常关窗、日志无空引用。
///
/// ## 安全设计
/// * **默认不启用**：只在 cfg「调试.日志详细模式」打开时才响应热键
/// * 只挂在窗口内部（借用它已有的 Canvas / 层级），**不新建 Canvas**
/// * 物体名带固定前缀，方便随时销毁/清理
/// * 失败只记日志，不抛异常拖垮调用方
/// </summary>
internal static class UiProbe
{
    /// <summary>自建物体的名字前缀（便于识别与清理）</summary>
    internal const string ProbeRootName = "facility_ui_probe";

    private static GameObject? _probe;

    /// <summary>
    /// **自动模式**：处理某个窗口时，确保标定红块存在。
    ///
    /// ⚠ 不能"只建一次"：窗口关闭时红块**跟着窗口一起被销毁**，
    /// 之后再开窗就没红块了（踩过：用户反复报"红块没显示"，就是这个原因）。
    /// 所以每次处理窗口都检查一次：物体还在就跳过，不在就重建。
    ///
    /// 为什么不用热键：实测 `keybd_event` 发 F12 游戏收不到
    /// （F10/F8 都正常，F12 可能被系统或别的程序截了）。
    /// </summary>
    internal static void AutoEnsure(GameObject? window)
    {
        if (window == null) return;
        if (Plugin.VerboseEntry?.Value != true) return;

        // 红块还挂在这个窗口下 → **只同步位置**（cfg 可能刚改过，要热生效）
        try
        {
            if (_probe != null && _probe && _probe.transform.parent == window.transform.parent)
            {
                // 自绘路线：位置 = UI 根位置 + cfg 偏移（世界单位，1 单位 = 100 像素）
                var marker = _probe.transform.Find("marker_sprite");
                if (marker != null)
                {
                    float offX = Plugin.UiOffXEntry?.Value ?? 0f;
                    float offY = Plugin.UiOffYEntry?.Value ?? 0f;
                    var want = _probe.transform.position + new Vector3(offX * 0.01f, offY * 0.01f, 0f);
                    if (marker.position != want)
                    {
                        marker.position = want;
                        LogMarkerScreenPos(marker, want);
                    }
                }
                return;
            }
        }
        catch { }

        Destroy();          // 旧的可能已失效/挂在别的窗口上
        Toggle(window);
    }

    /// <summary>
    /// 在当前打开的窗口里构建/销毁实验 UI（F12 切换）。
    /// 返回 false 表示没有可用的窗口。
    /// </summary>
    internal static bool Toggle(GameObject? window)
    {
        if (window == null)
        {
            Plugin.LogV("[Facility] UI 实验：当前没有打开的窗口");
            return false;
        }

        if (_probe != null)
        {
            Destroy();
            Plugin.LogV("[Facility] UI 实验：已销毁自建 UI");
            return true;
        }

        try
        {
            // ⚠ 只建**标定红块**了 —— 之前的"实验文本面板"用途已完成
            //   （证明能创建 uGUI），留着只会跟正式模板挤在一起、互相干扰。
            var root = BuildMarker(window);
            if (root == null)
            {
                Plugin.LogV("[Facility] 标定块：构建失败（详情见上方日志）");
                return false;
            }
            _probe = root;
            Plugin.LogV("[Facility] 标定块已挂到窗口 " + window.name +
                        "（与 UI 模板面板同参数：看到它在哪，面板就在哪）");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Facility] UI 实验构建异常: {ex}");
            return false;
        }
    }

    internal static void Destroy()
    {
        try { if (_probe != null) UnityEngine.Object.Destroy(_probe); } catch { }
        _probe = null;
    }

    /// <summary>
    /// 只建**标定红块**：与 UI 模板面板**完全相同的定位参数**
    /// （同锚点 `(0.5,0.5)`、同 `sizeDelta`、同 cfg 偏移）。
    ///
    /// 用户看到红块出现在哪，模板面板就会出现在哪 —— 这是可靠的位置标定手段。
    ///
    /// ⚠ 之前那个"实验文本面板"已完成使命（证明运行时能创建 uGUI），
    ///   留着只会跟正式模板互相干扰，所以删掉了。
    /// </summary>
    /// <summary>
    /// 标定/验证块：**用自绘内核**（`SpriteRenderer` + `MySpriteRenderer`）建一个红块。
    ///
    /// ## 为什么不再用 uGUI
    /// 实测结论：游戏窗口根的 `rect` 是 **550×0**，uGUI 布局**从不运行**
    /// → 标准 uGUI 在这类窗口里没有可用的布局信息（`rect` 永远是 0），怎么调都看不见。
    /// 所以改用游戏自己的渲染方式：`GameObject` + `SpriteRenderer` + `MySpriteRenderer`，
    /// **位置用世界坐标**（`Transform.position`），完全不依赖 uGUI 布局。
    ///
    /// 见 `SpriteUi.cs` 与 `docs/UI模板.md` §五 的完整结论。
    /// </summary>
    private static GameObject? BuildMarker(GameObject window)
    {
        try
        {
            // ① UI 根：挂在窗口父级下，位置与窗口根一致（世界坐标）
            var root = SpriteUi.EnsureRoot(window);
            if (root == null) { Plugin.LogV("[Facility] 自绘标定块：UI 根建不出来"); return null; }

            // ② 放红块的位置：窗口位置 + cfg 偏移（单位是**世界单位**，不是像素）
            float offX = Plugin.UiOffXEntry?.Value ?? 0f;
            float offY = Plugin.UiOffYEntry?.Value ?? 0f;
            Vector3 pos = root.transform.position + new Vector3(offX * 0.01f, offY * 0.01f, 0f);

            // ③ 建红块（取不到图会自动退化为 1×1 白图 + 红色 → 依然可见）
            var sr = SpriteUi.AddImage(root, "marker_sprite", "facility_marker",
                                       pos, scale: 260f, sortingOrder: 30000);
            if (sr == null) { Plugin.LogV("[Facility] 自绘标定块：创建失败"); return null; }
            sr.color = new Color(1f, 0f, 0f, 0.85f);      // 醒目红

            // ④ 顺便建一行文字，验证 TMP(3D) 能不能画出来
            SpriteUi.AddText(root, "marker_text", "自绘 UI 测试 ✓",
                             pos + new Vector3(0f, -0.04f, 0f), size: 1f);

            LogMarkerScreenPos(sr.transform, pos);
            return root;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Facility] 自绘标定块异常: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 打印红块的**世界坐标 → 屏幕像素**换算结果。
    /// 有了这个就能直接算出"要放到屏幕哪个位置，该给什么世界坐标"，
    /// 不用再靠反复试偏移。
    /// </summary>
    private static void LogMarkerScreenPos(Transform tf, Vector3 worldPos)
    {
        try
        {
            var cam = Camera.main;
            string screen = "（取不到主相机）";
            if (cam != null)
            {
                var sp = cam.WorldToScreenPoint(worldPos);
                screen = $"屏幕像素≈({sp.x:0},{sp.y:0})";
            }
            Plugin.LogV($"[Facility] 自绘标定块：世界坐标=({worldPos.x:0.###},{worldPos.y:0.###})" +
                        $" {screen}；屏幕 {Screen.width}×{Screen.height}" +
                        $"；缩放={tf.localScale.x:0}（1 单位=1 世界单位）");
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] 打印标定坐标失败: {ex.Message}"); }
    }
}
