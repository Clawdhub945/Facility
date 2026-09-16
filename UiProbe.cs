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
            if (_probe != null && _probe && _probe.transform.parent == window.transform)
            {
                var rt = _probe.GetComponent<RectTransform>();
                // ⚠ 保险：高度为 0 说明是"修复前建的旧物体"（窗口根 rect 高度 0 导致）
                //   → 销毁重建，否则会一直看不到（实测踩过：日志显示尺寸 260×0）
                if (rt != null && (rt.rect.height < 1f || rt.rect.width < 1f))
                {
                    Destroy();
                    Toggle(window);
                    return;
                }
                if (rt != null)
                {
                    var want = new Vector2(Plugin.UiOffXEntry?.Value ?? 0f,
                                           Plugin.UiOffYEntry?.Value ?? 0f);
                    if (rt.anchoredPosition != want)
                    {
                        rt.anchoredPosition = want;
                        LogMarkerPos(rt, "位置已热更新");
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
    private static GameObject? BuildMarker(GameObject window)
    {
        var root = new GameObject(ProbeRootName);
        root.transform.SetParent(window.transform, false);
        var rt = root.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        // ⚠⚠ **尺寸必须显式给**，不能用任何"拉伸/锚点推算" ——
        //   窗口根的 `rect` 高度是 **0**，所以：
        //     * `anchorMin=0/anchorMax=1` 的拉伸 → 高度 0
        //     * 拿父 rect 算 sizeDelta          → 也是 0
        //   实测日志：`标定红块...尺寸 260×0` —— 物体在、位置对，但**0 像素高看不见**。
        rt.sizeDelta = new Vector2(260f, 150f);
        rt.anchoredPosition = new Vector2(
            Plugin.UiOffXEntry?.Value ?? 0f,
            Plugin.UiOffYEntry?.Value ?? 0f);
        try { rt.SetAsLastSibling(); } catch { }

        // ⚠ 与模板一样**必须挂独立 Canvas**：游戏 UI 是自绘批渲染，
        //   不加 `overrideSorting` 的话 uGUI 会被它盖住（实测：看不见）。
        try
        {
            var cv = root.AddComponent<Canvas>();
            cv.overrideSorting = true;
            cv.sortingOrder = 30001;          // 比模板再高 1，标定时一定看得见
            root.AddComponent<UnityEngine.UI.GraphicRaycaster>();
        }
        catch (Exception cex) { Plugin.LogV($"[Facility] 标定块挂 Canvas 失败: {cex.Message}"); }

        // 图片直接挂在 root 上（不再建子物体 —— 子物体若用拉伸会继承"高度 0"的毛病）
        var img = root.AddComponent<Image>();
        img.color = new Color(1f, 0f, 0f, 0.85f);
        img.raycastTarget = false;

        LogMarkerPos(rt, "已铺");
        return root;
    }

    /// <summary>
    /// 打印红块的 **anchoredPosition ↔ 屏幕像素** 对应关系。
    ///
    /// 为什么要这个：这个窗口的坐标系有额外的缩放层 ——
    /// 实测 `anchoredPosition` 从 0 改到 ±400，屏幕位置几乎没动
    /// （`(0,0)→屏幕(0,900)`、`(400,-400)→屏幕(4,904)`）。
    /// 有了这组对应关系才能反算出目标偏移，而不是盲试。
    /// </summary>
    private static void LogMarkerPos(RectTransform rt, string tag)
    {
        try
        {
            var canvas = rt.GetComponentInParent<Canvas>();
            float k = (canvas != null && canvas.scaleFactor > 0.001f) ? canvas.scaleFactor : 1f;
            float sx = rt.position.x * k;
            float sy = Screen.height - rt.position.y * k;
            Vector2 p = rt.anchoredPosition;
            Plugin.LogV($"[Facility] 标定红块{tag}：anchoredPosition=({p.x:0},{p.y:0})" +
                        $" → 屏幕像素≈({sx:0},{sy:0})（中心点；尺寸 " +
                        $"{rt.rect.width:0}×{rt.rect.height:0}，Canvas 换算 {k:0.###}）；" +
                        $"屏幕 {Screen.width}×{Screen.height}");
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] 标定块坐标打印失败: {ex.Message}"); }
    }
}
