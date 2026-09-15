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
    /// **自动模式**：详细模式开启时，第一次处理某个窗口就自动建一次实验 UI。
    ///
    /// 为什么不用热键：实测 `keybd_event` 发 F12 游戏收不到
    /// （F10/F8 都正常，F12 可能被系统或别的程序截了）——
    /// 与其在热键上纠缠，不如让它在"开窗"这个必然发生的时机自动跑一次。
    /// </summary>
    internal static void AutoOnce(GameObject? window)
    {
        if (window == null || _autoDone) return;
        if (Plugin.VerboseEntry?.Value != true) return;
        _autoDone = true;
        Toggle(window);
    }

    private static bool _autoDone;

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
            var root = Build(window);
            if (root == null)
            {
                Plugin.LogV("[Facility] UI 实验：构建失败（详情见上方日志）");
                return false;
            }
            _probe = root;
            Plugin.LogV("[Facility] UI 实验：自建 UI 已挂到窗口 " + window.name +
                        "（若游戏稳定，说明运行时构建 uGUI 可行）");
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
    /// 构建一个最小可用的小组件：半透明底板 + 一行文字。
    /// 逐步记日志，方便定位"哪一步在 IL2CPP 下不成立"。
    /// </summary>
    private static GameObject? Build(GameObject window)
    {
        // ① 根物体（挂到窗口下，继承它的 Canvas / 层级）
        var root = new GameObject(ProbeRootName);
        Plugin.LogV("[Facility] UI 实验 ① 新建 GameObject 成功");
        root.transform.SetParent(window.transform, false);
        Plugin.LogV("[Facility] UI 实验 ② SetParent 成功");

        var rt = root.AddComponent<RectTransform>();
        if (rt == null) { Plugin.LogV("[Facility] UI 实验 ③ RectTransform 取不到"); return null; }
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(12f, -44f);
        rt.sizeDelta = new Vector2(300f, 96f);
        Plugin.LogV("[Facility] UI 实验 ③ RectTransform 配置成功");

        // ② 底板（Image）
        try
        {
            var bg = new GameObject("bg");
            bg.transform.SetParent(root.transform, false);
            bg.AddComponent<RectTransform>();
            var img = bg.AddComponent<Image>();
            img.color = new Color(0.15f, 0.14f, 0.13f, 0.92f);
            img.raycastTarget = false;
            var bgRt = bg.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero;
            bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero;
            bgRt.offsetMax = Vector2.zero;
            Plugin.LogV("[Facility] UI 实验 ④ Image 底板成功");
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] UI 实验 ④ Image 失败: {ex.Message}"); }

        // ③ 文本（TMP）
        try
        {
            var go = new GameObject("txt");
            go.transform.SetParent(root.transform, false);
            var trt = go.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(8f, 6f);
            trt.offsetMax = new Vector2(-8f, -6f);
            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "UI 实验：\n运行时构建 uGUI\n成功✓";
            tmp.fontSize = 14;
            tmp.alignment = TextAlignmentOptions.TopLeft;
            tmp.color = new Color(0.95f, 0.9f, 0.78f, 1f);
            tmp.raycastTarget = false;
            Plugin.LogV("[Facility] UI 实验 ⑤ TextMeshProUGUI 成功");
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] UI 实验 ⑤ TMP 失败: {ex.Message}"); }

        return root;
    }
}
