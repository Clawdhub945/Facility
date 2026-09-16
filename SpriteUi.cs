using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// **自绘 UI 内核** —— 走游戏自己的渲染方式（`SpriteRenderer` + `MySpriteRenderer`），
/// 而不是标准 uGUI。
///
/// ## 为什么放弃 uGUI（实测结论，见 `docs/UI模板.md`）
/// 游戏窗口根节点的 `rect` 是 **550×0** —— 因为游戏 UI 是**自绘批渲染**画的，
/// uGUI 布局**从不运行**，所以：
///   * `rect` 永远是 0（布局没跑，缓存没更新）
///   * `anchorMin=0/anchorMax=1` 的拉伸子物体高度为 0
///   * 拿父 rect 推算尺寸也是 0
///   → 标准 uGUI 在这类窗口里**没有可用的布局信息**，怎么调都看不见。
///
/// ## 游戏的做法（反编译 `Facility.InitBodySp`）
/// ```c
/// SpriteRenderer *sr = FindGameObject("body/sp").GetComponent&lt;SpriteRenderer&gt;();
/// MySpriteRenderer *sp = MySpriteRenderer.Create(sr);       // 包装已有 SpriteRenderer
/// if (sr.sprite != null)
///     sp.sprite_id = SpriteManager.Ins.TryGetSpriteId(sr.sprite.name);
/// ```
/// 即：**SpriteRenderer 负责"画什么"（sprite + 位置），MySpriteRenderer 挂在同一物体上
/// 参与批渲染**。位置用 `Transform` / 世界坐标，**不依赖 uGUI 布局系统**。
///
/// ## 本文件的思路
/// 自己建 `GameObject` + `SpriteRenderer` + `MySpriteRenderer`，用**世界坐标**定位，
/// 挂到窗口的父级下。这样：
///   * 不受"窗口 rect 为 0"影响
///   * 能与游戏 UI 一起被批渲染（同一套渲染管线，层级/风格天然一致）
///   * 布局完全由我们自己算 —— **这就是"高自由度"的来源**
/// </summary>
internal static class SpriteUi
{
    /// <summary>我们创建的 UI 根物体名（便于识别/清理）</summary>
    internal const string RootName = "facility_sprite_ui";

    /// <summary>
    /// **UI 层**（实测：游戏自己的 UI 元素都在 UI 层，content_area 的 Layer = UI）。
    ///
    /// ⚠⚠ 这是看不见的**真正原因**：游戏 UI 相机的 cullingMask 只渲染 UI 层，
    ///   而 `new GameObject()` 默认是 Default(0) 层 → **被相机直接剔除**，
    ///   物体存在、坐标正确，但一个像素都不会画出来。
    ///   （用户实测反馈：selftest_red 在 UnityExplorer 里能选中，但屏幕上没有。）
    /// </summary>
    internal const int UiLayer = 5;      // Unity 内置 "UI" 层的索引是 5

    /// <summary>把物体及其所有子物体设为 UI 层（新物体默认在 Default 层，会被相机剔除）</summary>
    internal static void SetUiLayer(GameObject go)
    {
        try
        {
            go.layer = UiLayer;
            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t != null) t.gameObject.layer = UiLayer;
            }
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 设置 UI 层失败: {ex.Message}"); }
    }

    private static GameObject? _root;
    private static GameObject? _owner;

    /// <summary>当前 UI 根（供诊断）</summary>
    internal static GameObject? Root => _root;

    /// <summary>
    /// **屏幕像素 → 世界坐标** 的精确换算。
    ///
    /// ## 为什么需要它
    /// 实测（UnityExplorer）：
    /// ```
    /// marker_sprite   LocalPosition=339,-4599.6   Position(世界)=7.98,-46.0
    /// 父级 ui_canvas  localScale=0.01，rect=1600x1000
    /// ```
    /// 而屏幕只覆盖 `y ∈ [0, -10]`（1000 UI × 0.01）——
    /// 所以世界坐标 `y=-46` 的面板在**屏幕外约 4 倍屏幕高度**的地方（实测踩过：
    /// 用户报告"没看到"，UnityExplorer 里却能看到物体存在且 ActiveSelf）。
    ///
    /// 换算依据：`ui_canvas` 的 `rect`（1600×1000 UI）对应整块屏幕，
    /// 乘上父级缩放（0.01）就是世界尺寸（16×10）。
    /// </summary>
    internal static class Coords
    {
        /// <summary>画布中心的世界坐标（= 屏幕中心，最可靠的参照点）</summary>
        internal static Vector3 CanvasCenter(GameObject uiRoot)
        {
            var rt = FindCanvasRect(uiRoot);
            if (rt != null) return new Vector3(rt.position.x, rt.position.y, 0f);
            return Vector3.zero;
        }

        /// <summary>
        /// 找到游戏 UI 画布的 `RectTransform`（多重兜底）。
        ///
        /// ⚠ 实测教训：`GetComponentInParent&lt;Canvas&gt;()` 在本 mod 场景下**返回 null**
        /// （于是原本退化成 (0,0)），算出的世界坐标跑到屏幕外约 5 倍高度处 ——
        /// 用户报告"没看到"，UnityExplorer 显示 `Position y = +51`。
        /// 所以这里按 ①父链 ②自身 ③按名字找（`ui_canvas`/`ui`/`window_root`）
        /// ④场景里任意活动 Canvas 的顺序兜底。
        /// </summary>
        internal static RectTransform? FindCanvasRect(GameObject uiRoot)
        {
            // ① 父链上的 Canvas
            try
            {
                var c = uiRoot.GetComponentInParent<Canvas>();
                if (c != null) { var r = c.GetComponent<RectTransform>(); if (r != null) return r; }
            }
            catch { }
            // ② 自身
            try
            {
                var c = uiRoot.GetComponent<Canvas>();
                if (c != null) { var r = c.GetComponent<RectTransform>(); if (r != null) return r; }
            }
            catch { }
            // ③ 按名字找（`ui_canvas` 是实测层级里的名字）
            foreach (var nm in new[] { "ui_canvas", "ui", "window_root" })
            {
                try
                {
                    var go = GameObject.Find(nm);
                    if (go == null) continue;
                    var c = go.GetComponent<Canvas>();
                    if (c != null) { var r = c.GetComponent<RectTransform>(); if (r != null) return r; }
                }
                catch { }
            }
            // ④ 场景里任意活动 Canvas
            try
            {
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (c == null) continue;
                    var r = c.GetComponent<RectTransform>();
                    if (r != null && r.rect.width > 1f) return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>诊断：把画布读数打成一行（定位问题的第一手数据）</summary>
        internal static string DescribeCanvas(GameObject uiRoot)
        {
            try
            {
                var rt = FindCanvasRect(uiRoot);
                if (rt == null) return "画布 RectTransform 找不到（全部兜底都失败）";
                var size = CanvasWorldSize(uiRoot);
                return $"canvas={rt.name} rect={rt.rect.width:0}x{rt.rect.height:0}" +
                       $" pos=({rt.position.x:0.###},{rt.position.y:0.###})" +
                       $" pivot=({rt.pivot.x:0.##},{rt.pivot.y:0.##})" +
                       $" scale={rt.localScale.x:0.####}" +
                       $" 世界尺寸={size.x:0.##}x{size.y:0.##}";
            }
            catch (Exception ex) { return $"DescribeCanvas 失败: {ex.Message}"; }
        }

        /// <summary>画布的世界尺寸（宽 × 高）。取不到时用 16×10 兜底。</summary>
        internal static Vector2 CanvasWorldSize(GameObject uiRoot)
        {
            try
            {
                var crt = FindCanvasRect(uiRoot);
                if (crt != null && crt.rect.width > 1f && crt.rect.height > 1f)
                {
                    float k = Mathf.Abs(crt.localScale.x) > 1e-6f ? crt.localScale.x : 1f;
                    return new Vector2(crt.rect.width * k, crt.rect.height * k);
                }
            }
            catch { }
            return new Vector2(16f, 10f);
        }

        /// <summary>画布左上角的世界坐标</summary>
        internal static Vector3 CanvasOrigin(GameObject uiRoot)
        {
            try
            {
                // ⚠ 实测教训：`GetComponentInParent<Canvas>()` 在本 mod 场景下会返回 null，
                //   于是退化到 (0,0)，导致算出的世界坐标跑到屏幕外约 5 倍高度处
                //   （用户报告"没看到"，UnityExplorer 显示 `Position y = +51`）。
                //   所以这里统一走 CanvasCenter()（它有多重兜底），再由中心推左上角。
                Vector3 center = CanvasCenter(uiRoot);
                var size = CanvasWorldSize(uiRoot);
                return new Vector3(center.x - size.x * 0.5f, center.y + size.y * 0.5f, 0f);
            }
            catch { }
            return Vector3.zero;
        }

        /// <summary>
        /// 屏幕像素点 → 世界坐标。
        /// `screenY` 用**左上角为原点**的像素坐标（符合直觉：0 = 屏幕顶）。
        /// </summary>
        internal static Vector3 ScreenToWorld(GameObject uiRoot, float screenX, float screenY)
        {
            var size = CanvasWorldSize(uiRoot);
            Vector3 origin = CanvasOrigin(uiRoot);
            float wx = origin.x + (screenX / Mathf.Max(1f, Screen.width)) * size.x;
            float wy = origin.y - (screenY / Mathf.Max(1f, Screen.height)) * size.y;
            return new Vector3(wx, wy, 0f);
        }

        /// <summary>世界坐标 → 屏幕像素（诊断用，与 <see cref="ScreenToWorld"/> 互逆）</summary>
        internal static Vector2 WorldToScreen(GameObject uiRoot, Vector3 world)
        {
            var size = CanvasWorldSize(uiRoot);
            Vector3 origin = CanvasOrigin(uiRoot);
            float sx = (world.x - origin.x) / Mathf.Max(1e-6f, size.x) * Screen.width;
            float sy = (origin.y - world.y) / Mathf.Max(1e-6f, size.y) * Screen.height;
            return new Vector2(sx, sy);
        }
    }

    /// <summary>销毁（换窗口/关窗）</summary>
    internal static void Destroy()
    {
        try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
        _root = null; _owner = null;
    }

    /// <summary>
    /// 确保 UI 根存在。挂在 `anchor`（通常是窗口根）下，
    /// 位置用世界坐标 —— 这里先放在锚点物体的位置上。
    /// </summary>
    internal static GameObject? EnsureRoot(GameObject anchor, string name = RootName)
    {
        try
        {
            if (_root != null && _root && _owner == anchor) return _root;
            Destroy();

            var go = new GameObject(name);
            // ⚠ 挂到锚点的父级，而不是锚点内部 ——
            //   避免被锚点自身可能存在的缩放/旋转影响（窗口根的 rect 是 0，缩放也未必是 1）。
            var parent = anchor.transform.parent != null ? anchor.transform.parent : anchor.transform;
            go.transform.SetParent(parent, false);
            go.transform.position = anchor.transform.position;   // 与锚点同位（世界坐标）
            go.transform.localScale = Vector3.one;
            SetUiLayer(go);
            _root = go; _owner = anchor;
            Plugin.LogV($"[FacilityUI] 自绘 UI 根已建：{name}（父级 {parent.name}）");
            return go;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] 建自绘 UI 根失败: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 在 UI 根下创建一个"图片元素"：`GameObject` + `SpriteRenderer` + `MySpriteRenderer`。
    ///
    /// @param parent      父物体（一般是 <see cref="EnsureRoot"/> 的返回值）
    /// @param name        物体名（便于识别）
    /// @param spriteName  精灵名（如 `ui_603010`）；取不到就退化成一个纯色块
    /// @param worldPos    世界坐标位置
    /// @param scale       缩放（1 = 原始像素大小）
    /// @param sortingOrder 渲染顺序（越大越靠前）
    /// </summary>
    internal static SpriteRenderer? AddImage(GameObject parent, string name,
                                             string spriteName, Vector3 worldPos,
                                             float scale = 1f, int sortingOrder = 30000)
    {
        try
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.position = worldPos;
            go.transform.localScale = new Vector3(scale, scale, 1f);
            go.layer = UiLayer;

            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = ResolveSprite(spriteName);
            if (sr.sprite == null)
            {
                // 取不到图：给一张 1×1 白图，至少是个可见的方块（便于调试布局）
                sr.sprite = WhiteSprite();
                sr.color = new Color(1f, 0f, 0f, 0.85f);
            }
            // ⚠ 批渲染的排序：`sortingOrder` 直接传给 MySpriteRenderer
            try { sr.sortingOrder = sortingOrder; } catch { }

            // 包装进游戏的批渲染系统（反编译确认：Create(SpriteRenderer) 这个重载最省事）
            AttachMySpriteRenderer(go, sr, spriteName, sortingOrder);
            return sr;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] 建图片元素失败({name}): {ex}");
            return null;
        }
    }

    /// <summary>
    /// 给物体挂 `MySpriteRenderer`（游戏批渲染组件）。
    /// 优先用 `Create(SpriteRenderer, string, int, Vector3)` 这个签名最全的重载；
    /// 失败就退回直接 `AddComponent`（批渲染可能不参与，但 SpriteRenderer 本身还能画）。
    /// </summary>
    private static void AttachMySpriteRenderer(GameObject go, SpriteRenderer sr,
                                              string spriteName, int sortingOrder)
    {
        try
        {
            var t = AccessTools.TypeByName("MySpriteRenderer");
            if (t == null) { Plugin.LogV("[FacilityUI] 找不到 MySpriteRenderer 类型"); return; }

            // ① 优先走游戏工厂方法：Create(SpriteRenderer, string, int, Vector3)
            try
            {
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public |
                                               System.Reflection.BindingFlags.Static))
                {
                    if (m.Name != "Create") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 4) continue;
                    if (ps[0].ParameterType != typeof(SpriteRenderer)) continue;
                    if (ps[1].ParameterType != typeof(string)) continue;
                    if (ps[2].ParameterType != typeof(int)) continue;
                    if (ps[3].ParameterType != typeof(Vector3)) continue;
                    m.Invoke(null, new object[] { sr, spriteName ?? "", sortingOrder, Vector3.zero });
                    Plugin.LogV("[FacilityUI] MySpriteRenderer.Create(sr, name, order, pos) 成功");
                    return;
                }
            }
            catch (Exception ex) { Plugin.LogV($"[FacilityUI] Create 调用失败: {ex.Message}"); }

            // ② 退回：用 Il2Cpp 的 AddComponent(Type)（System.Type 不能直接传，要转 Il2CppSystem.Type）
            try
            {
                var il2cppType = Il2CppInterop.Runtime.Il2CppType.From(t);
                go.AddComponent(il2cppType);
                Plugin.LogV("[FacilityUI] MySpriteRenderer 用 AddComponent 挂上（未走工厂方法）");
            }
            catch (Exception ex) { Plugin.LogV($"[FacilityUI] AddComponent(MySpriteRenderer) 失败: {ex.Message}"); }
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 挂批渲染组件异常: {ex.Message}"); }
    }

    /// <summary>按名字取精灵（游戏贴图表里没有就返回 null）</summary>
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

    private static Sprite? _white;
    /// <summary>1×1 白图（取不到真图时当占位块用）</summary>
    internal static Sprite WhiteSprite()
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
        catch (Exception ex) { Plugin.LogError($"[FacilityUI] 造占位白图失败: {ex.Message}"); }
        return _white!;
    }

    /// <summary>
    /// 建一个**纯色矩形块**，尺寸按**像素**指定（高自由度布局的基础元件）。
    ///
    /// ## 为什么要专门做这个
    /// 直接用 1×1 白图 + 大缩放会变成一个巨大的纯色方块（实测踩过：
    /// 缩放 260 的白图糊满屏幕）。这里用 `pixelsPerUnit = 1` 建精灵，
    /// 于是 **1 世界单位 = 1 像素**，缩放直接传像素数即可 —— 尺寸可预期、可计算。
    ///
    /// ⚠ 注意世界单位与画布的换算：`ui_canvas.localScale = 0.01`，
    /// 所以"1 世界单位 = 0.01 画布单位 = ? 屏幕像素"要按画布算。
    /// 这里的 `widthPx/heightPx` 是**世界单位**意义上的尺寸；
    /// 用 <see cref="Coords"/> 做屏幕换算时保持一致即可。
    /// </summary>
    internal static SpriteRenderer? AddRect(GameObject parent, string name, Color color,
                                            Vector3 worldPos, float widthPx, float heightPx,
                                            int sortingOrder = 30000)
    {
        try
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.layer = UiLayer;                         // ⚠ 必须：否则被 UI 相机剔除（实测的"看不见"根因）
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = UnitSprite();                   // 1×1、ppu=1 → 1 单位 = 1 像素
            sr.color = color;
            try { sr.sortingOrder = sortingOrder; } catch { }
            // ⚠ 自检实测：`sr.enabled = False`（**不是我们关的**，可能是批渲染初始化时改的）
            //   → 显式打开，否则永远不渲染。
            try { sr.enabled = true; } catch { }

            // ⚠ 深度用 **localPosition.z**，不要写世界 z ——
            //   父级（`ui_canvas`）缩放是 **0.01**，写世界 `z=-1` 会变成局部 `z=-100`
            //   （跑到相机后面/视锥外，实测踩过）。
            go.transform.position = new Vector3(worldPos.x, worldPos.y, go.transform.position.z);
            var lp = go.transform.localPosition;
            go.transform.localPosition = new Vector3(lp.x, lp.y, -1f);
            go.transform.localScale = new Vector3(widthPx, heightPx, 1f);

            AttachMySpriteRenderer(go, sr, "", sortingOrder);
            return sr;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] 建色块失败({name}): {ex}");
            return null;
        }
    }

    private static Sprite? _unit;
    /// <summary>1×1、`pixelsPerUnit = 1` 的精灵 → 缩放即像素数</summary>
    private static Sprite UnitSprite()
    {
        if (_unit != null) return _unit;
        try
        {
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            tex.SetPixel(0, 0, Color.white);
            tex.Apply();
            tex.hideFlags = HideFlags.HideAndDontSave;
            _unit = Sprite.Create(tex, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
            _unit.hideFlags = HideFlags.HideAndDontSave;
        }
        catch (Exception ex) { Plugin.LogError($"[FacilityUI] 造单位精灵失败: {ex.Message}"); }
        return _unit!;
    }

    /// <summary>
    /// 在 UI 根下创建一个**文字元素**：`GameObject` + `TextMeshPro`（3D 版，用世界坐标）
    /// 或退化为 `SpriteRenderer` 占位。
    ///
    /// 用 TMP 的 3D 组件（不是 `TextMeshProUGUI`）—— 后者依赖 Canvas/RectTransform，
    /// 而我们的窗口恰恰没有可用的 uGUI 布局。
    /// </summary>
    internal static GameObject? AddText(GameObject parent, string name, string text,
                                        Vector3 worldPos, float size = 1f, int sortingOrder = 30001)
    {
        try
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.position = worldPos;
            go.transform.localScale = Vector3.one * size;

            go.layer = UiLayer;
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text ?? "";
            tmp.fontSize = 3f;
            tmp.color = new Color(0.92f, 0.88f, 0.78f, 1f);
            tmp.alignment = TextAlignmentOptions.Left;
            try { tmp.sortingOrder = sortingOrder; } catch { }
            try { tmp.rectTransform.sizeDelta = new Vector2(40f, 6f); } catch { }
            Plugin.LogV($"[FacilityUI] 文字元素已建：{name}");
            return go;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] 建文字元素失败({name}): {ex}");
            return null;
        }
    }
}
