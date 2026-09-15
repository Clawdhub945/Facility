using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace FacilityMod;

/// <summary>
/// **通用 UI 模板** —— 按 <see cref="UiLayout"/> 在建筑窗口里渲染一块自绘内容区。
///
/// ## 设计要点
/// * **与占地尺寸无关**：布局全靠 `RectTransform.anchoredPosition` 手工计算，
///   所以 2×2 / 3×3 / N×N 的建筑都能用同一套模板
/// * **与建筑数量无关**：一座建筑一份 <see cref="UiLayout"/>，互不影响
/// * **静态优先**：`VerticalLayoutGroup` / `HorizontalLayoutGroup` 游戏里没有，
///   所以行位置由模板自上而下累加计算
/// * **按状态签名刷新**：每帧被调用，但只有"内容真的变了"才重排文字/图标，
///   避免每帧重建（性能 + 闪烁）
///
/// ## 可行性依据
/// 运行时构建 uGUI 的能力已由 `UiProbe` 实测验证（新建物体 / SetParent /
/// RectTransform / Image / TextMeshProUGUI 全部成功，游戏稳定）。
/// ⚠ 但**不要**在这里创建带 `SpriteRenderer` 的物体 —— 早前那样做导致游戏静默退出。
///
/// ## 样式
/// 参照游戏自己的面板配色（深色底 + 金色描边 + 暖白文字），
/// 见 `docs/UI模板.md` 的取色记录。
/// </summary>
internal static class UiTemplate
{
    // ---- 名称常量（便于识别与清理）----
    internal const string RootName = "facility_ui_template";
    private const string ViewportName = "viewport";
    private const string ContentName = "content";

    // ---- 尺寸 ----
    private const float PadX = 10f;         // 左右内边距
    private const float PadY = 8f;          // 上下内边距
    private const float LabelW = 76f;       // 左侧标题列宽
    private const float IconSize = 30f;     // 物品图标边长
    private const float IconGap = 8f;       // 图标间距
    private const float BarHeight = 10f;    // 进度条高度

    // ---- 配色（照游戏自己的面板取色）----
    private static readonly Color PanelColor = new Color(0.15f, 0.14f, 0.13f, 0.94f);
    private static readonly Color LineColor = new Color(0.86f, 0.79f, 0.67f, 0.55f);
    private static readonly Color TextColor = new Color(0.92f, 0.88f, 0.78f, 1f);
    private static readonly Color DimColor = new Color(0.68f, 0.65f, 0.58f, 1f);
    private static readonly Color OkColor = new Color(0.55f, 0.86f, 0.55f, 1f);
    private static readonly Color BadColor = new Color(0.95f, 0.55f, 0.45f, 1f);
    private static readonly Color BarBgColor = new Color(0.10f, 0.10f, 0.09f, 1f);
    private static readonly Color BarFgColor = new Color(0.85f, 0.68f, 0.32f, 1f);

    private static GameObject? _root;
    private static RectTransform? _content;
    private static ScrollRect? _scroll;
    private static GameObject? _ownerWindow;          // 模板挂在哪个窗口上

    /// <summary>
    /// 定位标定值（实测得出）：面板相对"锚点屏幕坐标"的偏移。
    ///
    /// 为什么需要标定：窗口根的 `rect` 是 550×0、其 UI `position` 约 (0,0)，
    /// 而 Canvas 可视区是 `y = 0 .. -1000` —— 直接按锚点放会贴到屏幕最上沿（y≈-10），
    /// 看起来就是"没显示"。这两个值就是把面板推进可视区所需的偏移。
    /// 调整方法见 `docs/UI模板.md`。
    /// </summary>
    private static float _calibX = 240f;
    private static float _calibY = -170f;

    /// <summary>标定值改为读 cfg（热生效），方便在游戏里试位置而不用改代码重编译</summary>
    private static void LoadCalibration()
    {
        try
        {
            if (Plugin.UiOffXEntry != null) _calibX = Plugin.UiOffXEntry.Value;
            if (Plugin.UiOffYEntry != null) _calibY = Plugin.UiOffYEntry.Value;
        }
        catch { }
    }

    private static string _lastSig = "";              // 上次渲染的状态签名
    private static float _contentHeight;

    // 每行渲染出来的对象（按行索引存），用于局部刷新
    private static readonly List<GameObject> _rowObjects = new();

    /// <summary>销毁模板（换窗口/关窗时调用）</summary>
    internal static void Destroy()
    {
        try { if (_root != null) UnityEngine.Object.Destroy(_root); } catch { }
        _root = null; _content = null; _scroll = null; _ownerWindow = null;
        _lastSig = ""; _rowObjects.Clear();
    }

    /// <summary>
    /// 渲染（或刷新）模板。每帧调用即可 —— 内部按状态签名去重。
    /// </summary>
    /// <param name="window">要挂到哪个窗口</param>
    /// <param name="spec">建筑规格（提供 UiLayout 与数据来源）</param>
    /// <param name="facility">建筑实例（提供 bag / guid 等运行时数据）</param>
    internal static void Render(GameObject? window, BuildingSpec? spec, Facility? facility)
    {
        try
        {
            if (window == null || spec?.Ui == null || facility == null) return;

            // 换了窗口 → 重建
            if (_root != null && _ownerWindow != window) Destroy();

            if (_root == null)
            {
                if (!Build(window, spec)) return;
                _ownerWindow = window;
            }

            // 跟随窗口移动（拖动窗口后面板要贴在窗口上）
            UpdateFollow();

            // 状态签名：内容变了才刷新
            string sig = BuildSignature(spec, facility);
            if (sig == _lastSig) return;
            _lastSig = sig;

            Refresh(spec, facility);
        }
        catch (Exception ex) { Plugin.LogError($"[FacilityUI] UI 模板渲染异常: {ex}"); }
    }

    // ==================================================================
    // 构建骨架
    // ==================================================================

    private static bool Build(GameObject window, BuildingSpec spec)
    {
        LoadCalibration();
        try
        {
            // ⚠⚠ 定位策略：**不能以窗口根为参照**
            //   窗口根的 `rect` 是 550×0（容器，尺寸由子物体撑开），
            //   而且它在屏幕上的位置可能是负的 —— 实测面板落在 `y=-1..-241`，
            //   完全在可视区之外（屏幕上什么都看不到）。
            //   所以改为找一个**真实可见的子控件**当锚点。
            var anchor = FindAnchor(window);

            // ⚠⚠⚠ 定位与尺寸的**根本约束**（实测结论，别再试别的写法）
            //   这个窗口根的 `rect` 是 **550x0**！
            //   所以任何 `anchorMin=0 / anchorMax=1` 的**拉伸**写法都会得到**高度 0**
            //   → 渲染不出来（游戏自己的子控件也是 550x0，同因）。
            //   实测证据：铺一张"全屏"洋红遮罩只出现一条 0 高度的残条；
            //             而「中心锚点 + 明确 sizeDelta」的红块**正常显示**。
            //
            //   因此一律：**中心锚点 + 明确 sizeDelta + 相对中心的 anchoredPosition**。
            //
            // ⚠ 尺寸也用**固定值**，不从锚点推算 —— 实测锚点的 `rect` 也是 0，
            //   推算出来的面板会变成 260×56 这种畸形尺寸。
            //   260×150 是实测能完整放进「高炉」窗口（可视区约 340×230）的尺寸。
            const float winW = 260f, winH = 150f;

            var root = new GameObject(RootName);
            root.transform.SetParent(window.transform, false);
            var rrt = root.AddComponent<RectTransform>();
            rrt.anchorMin = new Vector2(0.5f, 0.5f);
            rrt.anchorMax = new Vector2(0.5f, 0.5f);
            rrt.pivot = new Vector2(0.5f, 0.5f);
            rrt.sizeDelta = new Vector2(winW, winH);
            rrt.anchoredPosition = new Vector2(_calibX, _calibY);
            _root = root;
            try
            {
                var w0 = window.GetComponent<RectTransform>();
                _lastWindowPos = w0 != null ? new Vector2(w0.position.x, w0.position.y) : Vector2.zero;
            }
            catch { _lastWindowPos = Vector2.zero; }

            // ⚠ **必须置顶**：新加的子物体默认排在最后（被别的 UI 盖住）。
            try { rrt.SetAsLastSibling(); } catch { }
            if (Plugin.VerboseEntry?.Value == true) DumpChildren(window);

            // 面板底
            var bg = new GameObject("panel");
            bg.transform.SetParent(root.transform, false);
            var brt = bg.AddComponent<RectTransform>();
            brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one;
            brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            var bimg = bg.AddComponent<Image>();
            bimg.color = PanelColor;
            bimg.raycastTarget = true;      // 挡住穿透点击（窗口底层的东西不该被点到）

            // 视口（带 Mask，实现裁剪 + 滚动）
            var vp = new GameObject(ViewportName);
            vp.transform.SetParent(root.transform, false);
            var vrt = vp.AddComponent<RectTransform>();
            vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
            vrt.offsetMin = new Vector2(PadX, PadY);
            vrt.offsetMax = new Vector2(-PadX, -PadY);
            var vimg = vp.AddComponent<Image>();
            vimg.color = new Color(0f, 0f, 0f, 0.004f);   // 几乎全透明，但作为 Mask 需要非 null Image
            vp.AddComponent<Mask>().showMaskGraphic = false;

            // 内容容器（自上而下排）
            var content = new GameObject(ContentName);
            content.transform.SetParent(vp.transform, false);
            var crt = content.AddComponent<RectTransform>();
            crt.anchorMin = new Vector2(0f, 1f);
            crt.anchorMax = new Vector2(1f, 1f);
            crt.pivot = new Vector2(0.5f, 1f);
            crt.anchoredPosition = Vector2.zero;
            crt.sizeDelta = new Vector2(0f, 10f);
            _content = crt;

            // ScrollRect（纵向）
            var sr = root.AddComponent<ScrollRect>();
            sr.content = crt;
            sr.viewport = vrt;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 30f;
            _scroll = sr;

            Plugin.LogV($"[FacilityUI] UI 模板已构建：窗口 {window.name}，" +
                        $"面板 {winW:0}×{winH:0}（建筑「{spec.Name}」）");
            DumpPlacement(window, rrt);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[FacilityUI] UI 模板构建失败: {ex}");
            Destroy();
            return false;
        }
    }

    /// <summary>
    /// 诊断：打印模板的挂载位置 —— 父链、世界坐标、尺寸、是否在 Canvas 下、是否可见。
    ///
    /// 为什么需要：模板日志显示"已构建"，但屏幕上**看不到**。
    /// 要么被别的 UI 盖住（层级/排序），要么挂在了一个不参与渲染的父节点上，
    /// 要么坐标在窗口可视区之外。这三件事只能靠这份数据区分，不能猜。
    /// </summary>
    private static void DumpPlacement(GameObject window, RectTransform rt)
    {
        try
        {
            var sb = new StringBuilder("[FacilityUI] UI 模板挂载诊断:\n");
            var cur = rt.transform;
            int depth = 0;
            while (cur != null && depth++ < 8)
            {
                var crt = cur.GetComponent<RectTransform>();
                sb.Append("  ").Append(new string(' ', depth * 2)).Append(cur.name);
                if (crt != null)
                    sb.Append($" size={crt.rect.width:0}x{crt.rect.height:0}")
                      .Append($" pos={crt.position.x:0},{crt.position.y:0}")
                      .Append($" sibling={cur.GetSiblingIndex()}/{cur.parent?.childCount ?? 0}");
                sb.Append(cur.gameObject.activeInHierarchy ? " [可见]" : " [不可见]").Append('\n');
                if (cur.gameObject == window) break;
                cur = cur.parent;
            }
            // 有没有 Canvas 祖先（没有就不会渲染）
            bool hasCanvas = false;
            try { hasCanvas = rt.GetComponentInParent<Canvas>() != null; } catch { }
            sb.Append("  Canvas 祖先: ").Append(hasCanvas ? "有" : "【没有 → 不会渲染】").Append('\n');
            // ⚠ 屏幕坐标最关键：窗口根 rect 常常是 550×0，光看它判断不出面板在不在可视区。
            sb.Append($"  面板屏幕区: x={rt.position.x:0}..{rt.position.x + rt.rect.width:0}" +
                      $" y={rt.position.y:0}..{rt.position.y - rt.rect.height:0}\n");
            try
            {
                var canvas = rt.GetComponentInParent<Canvas>();
                if (canvas != null)
                {
                    var crt = canvas.GetComponent<RectTransform>();
                    if (crt != null)
                        sb.Append($"  Canvas 屏幕区: x={crt.position.x:0}..{crt.position.x + crt.rect.width:0}" +
                                  $" y={crt.position.y:0}..{crt.position.y - crt.rect.height:0}\n");
                }
            }
            catch { }
            sb.Append($"  面板缩放={rt.localScale.x:0.##}");
            Plugin.LogV(sb.ToString());
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 挂载诊断失败: {ex.Message}"); }
    }

    /// <summary>
    /// 诊断：列出窗口一级子控件的**屏幕区域**，用来挑一个"真实可见"的锚点。
    ///
    /// 为什么要这样：窗口根节点的 `rect` 是 550×0，且它的位置在屏幕上可能是负的
    /// （面板曾落在 `y=-1..-241`，完全在可视区之外）。
    /// 所以模板必须参照**某个可见子控件**来定位，而不是窗口根。
    /// </summary>
    private static void DumpChildren(GameObject window)
    {
        try
        {
            var sb = new StringBuilder("[FacilityUI] 窗口子控件屏幕区:\n");
            foreach (var t in window.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.parent != window.transform) continue;
                var rt = t.GetComponent<RectTransform>();
                if (rt == null) continue;
                sb.Append($"  {t.name}: x={rt.position.x:0}..{rt.position.x + rt.rect.width:0}" +
                          $" y={rt.position.y:0}..{rt.position.y - rt.rect.height:0}" +
                          $" size={rt.rect.width:0}x{rt.rect.height:0}\n");
            }
            Plugin.LogV(sb.ToString());
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 子控件诊断失败: {ex.Message}"); }
    }

    /// <summary>
    /// 找一个"真实可见的锚点控件"：优先 `content_area`（窗口内容区），
    /// 否则用面积最大的可见子控件。返回 null 表示没找到。
    /// </summary>
    private static RectTransform? FindAnchor(GameObject window)
    {
        try
        {
            RectTransform? best = null;
            float bestArea = 0f;
            foreach (var t in window.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.parent != window.transform) continue;
                var rt = t.GetComponent<RectTransform>();
                if (rt == null || !t.gameObject.activeInHierarchy) continue;
                float w = Mathf.Abs(rt.rect.width), h = Mathf.Abs(rt.rect.height);
                if (w < 40f || h < 40f) continue;
                if (t.name == "content_area") return rt;         // 首选
                float area = w * h;
                if (area > bestArea) { bestArea = area; best = rt; }
            }
            return best;
        }
        catch { return null; }
    }

    /// <summary>
    /// 把矩形摆到指定的**绝对屏幕坐标**（左上角对齐）。
    /// 用 `RectTransform.position` 直接写 —— 在"父节点尺寸为 0"的情况下，
    /// `anchoredPosition` 那套算不出正确结果（试过两种，都会落到 y&lt;0 的可视区之外）。
    /// </summary>
    private static void SetScreenPos(RectTransform rt, Vector2 topLeftScreen)
    {
        try
        {
            rt.position = new Vector3(topLeftScreen.x, topLeftScreen.y, rt.position.z);
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 设置面板位置失败: {ex.Message}"); }
    }

    /// <summary>
    /// 让面板跟随窗口移动：记录"面板相对窗口"的偏移，每帧把它贴回去。
    /// UI 模板必须跟着窗口走，否则拖动窗口后面板会留在原地。
    /// </summary>
    private static void UpdateFollow()
    {
        try
        {
            if (_root == null || _ownerWindow == null) return;
            var rrt = _root.GetComponent<RectTransform>();
            var wrt = _ownerWindow.GetComponent<RectTransform>();
            if (rrt == null || wrt == null) return;

            // 窗口移动了 → 面板按同样位移跟随
            Vector2 winNow = new Vector2(wrt.position.x, wrt.position.y);
            if (winNow != _lastWindowPos)
            {
                Vector2 delta = winNow - _lastWindowPos;
                _lastWindowPos = winNow;
                rrt.position = new Vector3(rrt.position.x + delta.x,
                                           rrt.position.y + delta.y, rrt.position.z);
            }
        }
        catch { }
    }

    private static Vector2 _lastWindowPos = new Vector2(float.NaN, float.NaN);

    // ==================================================================
    // 刷新内容
    // ==================================================================

    /// <summary>状态签名：任何会影响显示的数据变化都要体现在这里</summary>
    private static string BuildSignature(BuildingSpec spec, Facility facility)
    {
        var sb = new StringBuilder();
        sb.Append(spec.StuffId).Append('|');

        foreach (var row in spec.Ui!.Rows)
        {
            switch (row)
            {
                case UiItemRow item:
                    if (item.Source == UiItemSource.BuildingBag)
                        AppendBagSignature(sb, facility);
                    else
                        AppendRecipeSignature(sb, spec);
                    break;
                case UiStatusRow st:
                    sb.Append(StatusText(st, facility)).Append('|');
                    break;
                case UiProgressRow pr:
                    sb.Append(ProgressRatio(pr, spec, facility).ToString("0.00")).Append('|');
                    break;
                case UiLabelRow lb:
                    sb.Append(lb.Text).Append('|');
                    break;
            }
        }
        return sb.ToString();
    }

    private static void AppendBagSignature(StringBuilder sb, Facility f)
    {
        var dic = BagOf(f);
        if (dic == null) { sb.Append("nobag|"); return; }
        var keys = new List<int>();
        foreach (var k in dic.Keys) if (k is int id) keys.Add(id);
        keys.Sort();
        foreach (var id in keys)
        {
            object? v = dic[id];
            sb.Append(id).Append(':').Append(v).Append(',');
        }
        sb.Append('|');
    }

    private static void AppendRecipeSignature(StringBuilder sb, BuildingSpec spec)
    {
        var r = spec.Smelting;
        if (r == null) { sb.Append("norecipe|"); return; }
        foreach (var (id, n) in r.Inputs) sb.Append(id).Append('x').Append(n).Append(',');
        sb.Append("fuel").Append(r.FuelId).Append('x').Append(r.FuelPerBatch)
          .Append("out").Append(r.OutputId).Append('x').Append(r.OutputCount).Append('|');
    }

    private static void Refresh(BuildingSpec spec, Facility facility)
    {
        if (_content == null) return;

        // 清掉旧行
        foreach (var go in _rowObjects) { try { if (go != null) UnityEngine.Object.Destroy(go); } catch { } }
        _rowObjects.Clear();

        float y = 0f;
        int idx = 0;
        foreach (var row in spec.Ui!.Rows)
        {
            var rowGo = RenderRow(row, idx++, spec, facility, y);
            if (rowGo != null) _rowObjects.Add(rowGo);
            y += row.Height;
        }

        _contentHeight = y + 4f;
        try
        {
            _content.sizeDelta = new Vector2(0f, _contentHeight);
            _content.anchoredPosition = Vector2.zero;
        }
        catch { }
    }

    /// <summary>渲染一行，返回它的根物体</summary>
    private static GameObject? RenderRow(UiRow row, int index, BuildingSpec spec,
                                        Facility facility, float yOffset)
    {
        try
        {
            float rowW = 0f;
            try { rowW = _content != null ? _content.rect.width : 380f; } catch { }
            if (rowW < 50f) rowW = 380f;

            var go = new GameObject($"row_{index}_{row.GetType().Name}");
            go.transform.SetParent(_content!.transform, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, -yOffset);
            rt.sizeDelta = new Vector2(0f, row.Height);

            // 行标题（左侧固定列）
            float x = 0f;
            if (!string.IsNullOrEmpty(row.Label))
            {
                AddText(go.transform, "label", row.Label, LabelW, row.Height - 4f,
                        x, -2f, DimColor, TextAlignmentOptions.Left);
                x += LabelW;
            }

            float avail = Mathf.Max(60f, rowW - x);

            switch (row)
            {
                case UiLabelRow lb:
                    AddText(go.transform, "text", lb.Text, avail, row.Height - 4f, x, -2f,
                            lb.Secondary ? DimColor : TextColor, TextAlignmentOptions.Left);
                    break;

                case UiStatusRow st:
                {
                    string txt = StatusText(st, facility);
                    bool ok = (st.Source == UiStatusSource.SmelterBlockReason)
                              ? string.IsNullOrEmpty(SmelterConsumer.BlockReason(SafeGuid(facility)))
                              : true;
                    AddText(go.transform, "text", txt, avail, row.Height - 4f, x, -2f,
                            ok ? OkColor : BadColor, TextAlignmentOptions.Left);
                    break;
                }

                case UiItemRow item:
                    RenderItemRow(go.transform, item, spec, facility, x, avail);
                    break;

                case UiProgressRow pr:
                    RenderProgressRow(go.transform, pr, spec, facility, x, avail, row.Height);
                    break;
            }
            return go;
        }
        catch (Exception ex)
        {
            Plugin.LogV($"[FacilityUI] 渲染行失败（{row.GetType().Name}）: {ex.Message}");
            return null;
        }
    }

    /// <summary>物品行：图标 + 数量，横向排列，超出宽度换行（每行图标数按可用宽度算）</summary>
    private static void RenderItemRow(Transform parent, UiItemRow item, BuildingSpec spec,
                                      Facility facility, float x0, float avail)
    {
        var entries = new List<(int id, int count)>();

        if (item.Source == UiItemSource.BuildingBag)
        {
            var dic = BagOf(facility);
            if (dic != null)
            {
                foreach (var k in dic.Keys)
                {
                    if (k is not int id) continue;
                    int c = 0;
                    try { c = System.Convert.ToInt32(dic[k] ?? 0); } catch { }
                    if (c > 0) entries.Add((id, c));
                }
                entries.Sort((a, b) => b.count.CompareTo(a.count));
            }
        }
        else
        {
            var r = spec.Smelting;
            if (r != null)
            {
                foreach (var (id, n) in r.Inputs) entries.Add((id, n));
                if (r.FuelId != 0 && r.FuelPerBatch > 0) entries.Add((r.FuelId, r.FuelPerBatch));
                if (r.OutputId != 0) entries.Add((r.OutputId, r.OutputCount));
            }
        }

        if (item.ExcludeIds.Length > 0)
            entries.RemoveAll(e => Array.IndexOf(item.ExcludeIds, e.id) >= 0);

        if (entries.Count == 0)
        {
            AddText(parent, "empty", "（空）", avail, 20f, x0, -2f, DimColor, TextAlignmentOptions.Left);
            return;
        }

        int perRow = Mathf.Max(1, Mathf.FloorToInt(avail / (IconSize + IconGap)));
        int shown = Mathf.Min(entries.Count, item.MaxItems);

        for (int i = 0; i < shown; i++)
        {
            var (id, count) = entries[i];
            int col = i % perRow, line = i / perRow;
            float px = x0 + col * (IconSize + IconGap);
            float py = -2f - line * (IconSize + 14f);
            AddItemCell(parent, id, count, px, py);
        }

        if (entries.Count > shown)
        {
            int col2 = shown % perRow, line2 = shown / perRow;
            AddText(parent, "more", $"+{entries.Count - shown}",
                    IconSize + 6f, 20f,
                    x0 + col2 * (IconSize + IconGap), -2f - line2 * (IconSize + 14f) - 4f,
                    DimColor, TextAlignmentOptions.Left);
        }
    }

    /// <summary>一个物品格：图标 + 右下角数量</summary>
    private static void AddItemCell(Transform parent, int stuffId, int count, float x, float y)
    {
        try
        {
            var cell = new GameObject($"item_{stuffId}");
            cell.transform.SetParent(parent, false);
            var rt = cell.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(IconSize, IconSize);

            var sprite = ResolveStuffIcon(stuffId);
            if (sprite != null)
            {
                var img = cell.AddComponent<Image>();
                img.sprite = sprite;
                img.raycastTarget = false;
                img.preserveAspect = true;
            }
            else
            {
                // 图标取不到 → 画个占位块，至少不空白
                var img = cell.AddComponent<Image>();
                img.color = new Color(0.3f, 0.3f, 0.3f, 0.8f);
                img.raycastTarget = false;
            }

            // 数量（右下角，带描边色以便在图标上可读）
            AddText(cell.transform, "num", count.ToString(),
                    IconSize + 4f, 14f, IconSize * 0.35f, -(IconSize - 12f),
                    TextColor, TextAlignmentOptions.Right, outline: true);
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 物品格失败 {stuffId}: {ex.Message}"); }
    }

    /// <summary>进度行：自绘进度条 + 右侧数值</summary>
    private static void RenderProgressRow(Transform parent, UiProgressRow pr, BuildingSpec spec,
                                          Facility facility, float x0, float avail, float rowH)
    {
        float ratio = ProgressRatio(pr, spec, facility);
        float barW = pr.ShowValueText ? Mathf.Max(40f, avail - 64f) : avail;
        float barY = -(rowH - BarHeight) * 0.5f;

        // 底槽
        var bg = new GameObject("bar_bg");
        bg.transform.SetParent(parent, false);
        var brt = bg.AddComponent<RectTransform>();
        brt.anchorMin = new Vector2(0f, 1f); brt.anchorMax = new Vector2(0f, 1f);
        brt.pivot = new Vector2(0f, 1f);
        brt.anchoredPosition = new Vector2(x0, barY);
        brt.sizeDelta = new Vector2(barW, BarHeight);
        var bimg = bg.AddComponent<Image>();
        bimg.color = BarBgColor; bimg.raycastTarget = false;

        // 进度
        var fg = new GameObject("bar_fg");
        fg.transform.SetParent(bg.transform, false);
        var frt = fg.AddComponent<RectTransform>();
        frt.anchorMin = new Vector2(0f, 0f); frt.anchorMax = new Vector2(0f, 1f);
        frt.pivot = new Vector2(0f, 0.5f);
        frt.anchoredPosition = Vector2.zero;
        frt.sizeDelta = new Vector2(barW * Mathf.Clamp01(ratio), 0f);
        var fimg = fg.AddComponent<Image>();
        fimg.color = BarFgColor; fimg.raycastTarget = false;

        if (pr.ShowValueText)
            AddText(parent, "val", $"{Mathf.RoundToInt(ratio * 100f)}%", 60f, rowH - 4f,
                    x0 + barW + 4f, -2f, TextColor, TextAlignmentOptions.Left);
    }

    // ==================================================================
    // 数据取值
    // ==================================================================

    private static string StatusText(UiStatusRow st, Facility facility)
    {
        if (st.Source == UiStatusSource.Static) return st.StaticText;
        string reason = SmelterConsumer.BlockReason(SafeGuid(facility));
        return string.IsNullOrEmpty(reason) ? st.OkText : $"{st.FailText}：{reason}";
    }

    private static float ProgressRatio(UiProgressRow pr, BuildingSpec spec, Facility facility)
    {
        switch (pr.Source)
        {
            case UiProgressSource.Static:
                return Mathf.Clamp01(pr.StaticRatio);

            case UiProgressSource.WorkedToday:
                return SmelterConsumer.WorkedToday(SafeGuid(facility)) ? 1f : 0f;

            case UiProgressSource.FuelRatio:
            {
                var r = spec.Smelting;
                if (r == null || r.FuelId == 0 || r.FuelPerBatch <= 0) return 0f;
                int have = CountInBag(facility, r.FuelId);
                // 显示"够烧几天"：按 10 批封顶，避免条永远满
                return Mathf.Clamp01(have / (float)(r.FuelPerBatch * 10));
            }
        }
        return 0f;
    }

    private static System.Collections.IDictionary? BagOf(Facility f)
    {
        try { return SmelterConsumer.BagDictionaryOf(f); } catch { return null; }
    }

    private static int CountInBag(Facility f, int stuffId)
    {
        var dic = BagOf(f);
        if (dic == null) return 0;
        try { return dic.Contains(stuffId) ? System.Convert.ToInt32(dic[stuffId] ?? 0) : 0; }
        catch { return 0; }
    }

    private static int SafeGuid(Facility f)
    {
        try { return f.guid; } catch { return 0; }
    }

    /// <summary>
    /// 取物品图标 sprite：`物品id` → 游戏的 `stuff_dic` → `stuff_img`（如 `ui_603010`）
    /// → `SpriteManager.Get(name)`。取不到返回 null（调用方画占位块）。
    /// </summary>
    private static Sprite? ResolveStuffIcon(int stuffId)
    {
        if (_iconCache.TryGetValue(stuffId, out var cached)) return cached;
        Sprite? sp = null;
        try
        {
            var dic = D.Ins.stuff_dic;
            if (dic != null && dic.ContainsKey(stuffId))
            {
                var info = dic[stuffId];
                string? name = null;
                try { name = info.stuff_img; } catch { }
                if (!string.IsNullOrEmpty(name)) sp = SpriteManager.Get(name);
            }
        }
        catch { }
        _iconCache[stuffId] = sp;
        return sp;
    }

    private static readonly Dictionary<int, Sprite?> _iconCache = new();

    // ==================================================================
    // 基础控件
    // ==================================================================

    private static void AddText(Transform parent, string name, string text,
                                float w, float h, float x, float y,
                                Color color, TextAlignmentOptions align, bool outline = false)
    {
        try
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);

            var tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = text ?? "";
            tmp.fontSize = 13f;
            tmp.color = color;
            tmp.alignment = align;
            tmp.raycastTarget = false;
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Ellipsis;
            if (outline)
            {
                // 数量叠在图标上，加一层描边保证可读
                var ol = go.AddComponent<Shadow>();
                ol.effectColor = new Color(0f, 0f, 0f, 0.9f);
                ol.effectDistance = new Vector2(1f, -1f);
            }
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] 加文本失败({name}): {ex.Message}"); }
    }
}
