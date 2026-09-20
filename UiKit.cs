using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FacilityMod;

/// <summary>
/// **UI 内核（v2）** —— 用游戏自己的方式画界面：`CanvasRenderer` + `UnityEngine.UI.Image`。
///
/// ## 为什么最终选这条路（两次走弯路的结论）
/// 1. **标准 uGUI 挂在游戏窗口里 → 不可见**
///    窗口根的 `rect` 是 **550×0**（游戏 UI 是自绘的，uGUI 布局从不运行），
///    所以 `rect`/`offsetMin/offsetMax` 全是 0，任何尺寸推算都得到 0。
/// 2. **`SpriteRenderer` → 能画出来，但会被游戏 UI 盖住**
///    实测：自检绿块确实渲染到了屏幕上（用户截图确认），
///    但**一打开建筑窗口就被盖住**（游戏 UI 后画，且在不同渲染体系里）。
/// 3. **✅ 正确做法：学游戏自己** —— UnityExplorer 实测游戏 UI 元素的组件是
///    `RectTransform + CanvasRenderer + UnityEngine.UI.Image`（`icon_num` 实例），
///    Layer = `UI`。
///    → 我们建一个**独立的 Canvas**（不挂在窗口里，避免受"窗口 rect = 0"影响），
///      在里面用 `Image` 画面板/图标，用 `TextMeshProUGUI` 写字。
///      这样：层级独立、排序可控（`sortingOrder`）、和游戏 UI 同一套渲染管线。
///
/// ## 与旧版的区别
/// * v1（`SpriteUi` 的 SpriteRenderer 路线）保留但不再作为主路线
/// * v2 用 `Image`，**不依赖任何父容器的布局**（自身 `sizeDelta` 明确给出）
/// </summary>
internal static class UiKit
{
    internal const string RootName = "facility_uikit";
    internal const int UiLayer = 5;                 // Unity 内置 "UI" 层

    private static GameObject? _canvasGo;
    private static Canvas? _canvas;
    private static RectTransform? _canvasRt;

    /// <summary>UI 根（Canvas）</summary>
    internal static GameObject? Root => _canvasGo;

    /// <summary>临时开关：true = 强制隐藏我们的 UI（不受 cfg 影响，F9 用）</summary>
    private static bool _forceHidden;

    /// <summary>
    /// 切换我们的 UI 显示/隐藏，返回切换后的状态（true = 显示中）。
    ///
    /// 为什么需要热键：实测这份 cfg **不热重载**（改完文件游戏内毫无变化），
    /// 所以"隐藏我们的 UI → 看游戏窗口是否恢复操作"这个隔离测试只能靠热键做。
    /// </summary>
    internal static bool ToggleVisible()
    {
        _forceHidden = !_forceHidden;
        try
        {
            if (_canvasGo != null && _canvasGo) _canvasGo.SetActive(!_forceHidden);
        }
        catch { }
        return !_forceHidden;
    }

    /// <summary>当前是否被强制隐藏（诊断用）</summary>
    internal static bool ForceHidden => _forceHidden;

    /// <summary>
    /// 根据 cfg「显示」开关同步可见性。
    /// ⚠ 实测 cfg **不热重载**，所以这个同步只在"进档/开窗"时生效；
    ///   需要即时切换请用 <see cref="ToggleVisible"/>（F9 热键）。
    /// </summary>
    internal static void SyncVisibility()
    {
        try
        {
            if (_canvasGo == null || !_canvasGo) return;
            bool want = (Plugin.UiShowEntry?.Value ?? true) && !_forceHidden;
            if (_canvasGo.activeSelf != want) _canvasGo.SetActive(want);
        }
        catch { }
    }

    /// <summary>
    /// 建（或复用）我们自己的 **Screen Space Overlay Canvas**。
    ///
    /// 为什么不挂在游戏窗口里：窗口根的 `rect` 是 550×0，挂进去后
    /// 子物体的尺寸/位置都算不出来（实测：`sizeDelta` 设了也没用）。
    /// 独立 Canvas 的 `rect` 就是屏幕尺寸，**布局完全可用**。
    ///
    /// `sortingOrder` 给得很高，保证画在游戏 UI 之上。
    /// </summary>
    internal static GameObject? EnsureRoot(string name = RootName, int sortingOrder = 20000)
    {
        try
        {
            if (_canvasGo != null && _canvasGo) return _canvasGo;
            Destroy();

            var go = new GameObject(name);
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.layer = UiLayer;

            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.overrideSorting = true;             // 独立排序，压过游戏 UI
            _canvas.sortingOrder = sortingOrder;
            // ⚠⚠ **不要** GraphicRaycaster：我们的 UI 是纯展示，**必须点击穿透**。
            //   踩过：加了 GraphicRaycaster + 面板 raycastTarget=true 后，
            //   面板挡住了游戏自己的窗口 → 关不掉、点不动、拖不动（用户实测反馈）。

            // Overlay 模式下 Canvas 自己会撑满屏幕，不需要 CanvasScaler
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            // 双保险：整个 Canvas 设为不拦截射线
            try
            {
                var cg = go.AddComponent<CanvasGroup>();
                cg.blocksRaycasts = false;
                cg.interactable = false;
            }
            catch { }

            _canvasRt = go.GetComponent<RectTransform>();
            _canvasGo = go;
            Plugin.LogV($"[FacilityUI] UiKit Canvas 已建：{name}（Overlay, order={sortingOrder}, layer={go.layer}）");
            return go;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] 建 UiKit Canvas 失败: {ex}");
            return null;
        }
    }

    internal static void Destroy()
    {
        try { if (_canvasGo != null) UnityEngine.Object.Destroy(_canvasGo); } catch { }
        _canvasGo = null; _canvas = null; _canvasRt = null;
    }

    /// <summary>
    /// 交互模式开关：true = 我们的 UI **接收点击**（这样才能有可点的关闭按钮）。
    ///
    /// ⚠ 权衡说明：纯展示时不需要接收点击（那时会拦截游戏窗口的输入），
    ///   但要提供"关闭按钮"就必须能接收点击。
    ///   所以只在**面板自身范围**内接收（面板 `<see cref="AddPanel"/>` 仍设
    ///   `raycastTarget=false`，只有按钮是 true），
    ///   面板之外的区域完全不拦 —— 游戏窗口照常可操作。
    /// </summary>
    internal static void SetInteractive(bool on)
    {
        try
        {
            if (_canvasGo == null || !_canvasGo) return;
            var gr = _canvasGo.GetComponent<GraphicRaycaster>();
            if (on && gr == null) _canvasGo.AddComponent<GraphicRaycaster>();
            else if (!on && gr != null) UnityEngine.Object.Destroy(gr);

            var cg = _canvasGo.GetComponent<CanvasGroup>();
            if (cg != null) cg.blocksRaycasts = on;
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 设置交互模式失败: {ex.Message}"); }
    }

    // ==================================================================
    // 控件
    // ==================================================================

    /// <summary>
    /// 建一个**可点击按钮**（带文字）。
    /// 只有按钮自身 `raycastTarget=true`，其余控件一律穿透 ——
    /// 这样"能点按钮"和"不挡游戏窗口"两者兼得。
    /// </summary>
    internal static Button? AddButton(RectTransform parent, string name, string label,
                                      Vector2 anchoredPos, Vector2 size,
                                      Action onClick,
                                      Color? bgColor = null,
                                      Vector2? anchor = null,
                                      float fontSize = 14f)
    {
        try
        {
            var go = new GameObject(name);
            go.layer = UiLayer;
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            var a = anchor ?? new Vector2(0.5f, 0.5f);
            rt.anchorMin = a; rt.anchorMax = a;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;

            var img = go.AddComponent<Image>();
            img.color = bgColor ?? new Color(0.35f, 0.16f, 0.14f, 0.98f);
            img.raycastTarget = true;                    // ★ 按钮要接收点击

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            // IL2CPP 下 lambda 需要显式转成 UnityAction
            btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() =>
            {
                try { onClick?.Invoke(); } catch (Exception ex) { Plugin.LogError($"[FacilityUI] 按钮回调异常: {ex}"); }
            }));

            // 文字
            var tgo = new GameObject("txt");
            tgo.layer = UiLayer;
            tgo.transform.SetParent(go.transform, false);
            var trt = tgo.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
            var tmp = tgo.AddComponent<TextMeshProUGUI>();
            tmp.text = label ?? "";
            tmp.fontSize = fontSize;
            tmp.color = new Color(0.96f, 0.92f, 0.86f, 1f);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.raycastTarget = false;
            return btn;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] 建按钮失败({name}): {ex}");
            return null;
        }
    }

    /// <summary>
    /// 建一个**面板/色块**。
    /// `anchor` 用屏幕比例（0..1，左上角为 (0,1)），`size` 用像素。
    /// **尺寸和位置都显式给出** —— 不依赖任何父容器布局（这是可用的关键）。
    /// </summary>
    internal static RectTransform? AddPanel(RectTransform parent, string name, Color color,
                                            Vector2 anchoredPos, Vector2 size,
                                            Vector2? anchor = null)
    {
        try
        {
            var go = new GameObject(name);
            go.layer = UiLayer;
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            var a = anchor ?? new Vector2(0.5f, 0.5f);      // 默认：屏幕中心
            rt.anchorMin = a; rt.anchorMax = a;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;

            var img = go.AddComponent<Image>();
            img.color = color;
            // ⚠ **必须 false**：否则挡住游戏窗口的点击（关不掉/点不动/拖不动 —— 踩过）
            img.raycastTarget = false;
            return rt;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] 建面板失败({name}): {ex}");
            return null;
        }
    }

    /// <summary>建一段文字（`TextMeshProUGUI`，左上对齐到给定锚点）</summary>
    internal static TextMeshProUGUI? AddText(RectTransform parent, string name, string text,
                                             Vector2 anchoredPos, Vector2 size,
                                             float fontSize = 16f,
                                             TextAlignmentOptions align = TextAlignmentOptions.TopLeft,
                                             Color? color = null,
                                             Vector2? anchor = null)
    {
        try
        {
            var go = new GameObject(name);
            go.layer = UiLayer;
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            var a = anchor ?? new Vector2(0.5f, 0.5f);
            rt.anchorMin = a; rt.anchorMax = a;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = size;

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text ?? "";
            tmp.fontSize = fontSize;
            tmp.color = color ?? new Color(0.92f, 0.88f, 0.78f, 1f);
            tmp.alignment = align;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            return tmp;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] 建文字失败({name}): {ex}");
            return null;
        }
    }

    /// <summary>建一个**图标**（用游戏贴图表里的精灵名，如 `ui_603010`）</summary>
    internal static Image? AddIcon(RectTransform parent, string name, string spriteName,
                                   Vector2 anchoredPos, float size, Vector2? anchor = null)
    {
        try
        {
            var go = new GameObject(name);
            go.layer = UiLayer;
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            var a = anchor ?? new Vector2(0.5f, 0.5f);
            rt.anchorMin = a; rt.anchorMax = a;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos;
            rt.sizeDelta = new Vector2(size, size);

            var img = go.AddComponent<Image>();
            var sp = ResolveSprite(spriteName);
            if (sp != null) { img.sprite = sp; img.preserveAspect = true; }
            else { img.color = new Color(0.35f, 0.35f, 0.35f, 0.9f); }   // 取不到 → 灰块占位
            img.raycastTarget = false;
            return img;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] 建图标失败({name}): {ex}");
            return null;
        }
    }

    /// <summary>按名字取游戏贴图里的精灵</summary>
    internal static Sprite? ResolveSprite(string spriteName)
    {
        if (string.IsNullOrEmpty(spriteName)) return null;
        try
        {
            var sp = SpriteManager.Get(spriteName);
            if (sp != null) return sp;
        }
        catch { }
        return null;
    }

    /// <summary>建一个 1×1 白精灵（给纯色块用；`Image` 不设 sprite 也能显示颜色，所以非必需）</summary>
    internal static Sprite? WhiteSprite()
    {
        if (_white != null) return _white;
        try
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            _white = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            _white.hideFlags = HideFlags.HideAndDontSave;
        }
        catch (Exception ex) { Plugin.LogError($"[FacilityUI] 造白精灵失败: {ex.Message}"); }
        return _white;
    }
    private static Sprite? _white;
}
