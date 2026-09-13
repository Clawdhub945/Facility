using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// 自定义建筑外观（超级生产所 105050）——**换贴图**方案。
///
/// ## 为什么是换贴图，而不是新建 prefab（血泪结论）
/// 试过并全部失败的做法：
///   1. `textures.xml` + `action="add"` + stuff.json 里写新 prefab 名
///      → 放置时 `NullReferenceException`（游戏按名字找不到预制体）
///   2. `action="replace"` 覆盖 `workbench_0` → 洋红探针实测不生效（世界里 0 个洋红像素）
///   3. DLL 运行时自造 GameObject 当 prefab + 拦 `PrefabManager` 入口
///      → 贴图/prefab/图标都建成功了，但游戏**从不请求我们自造的名字**
///        （日志抓到的一直是 `workbench_0`），模型解析受 `class_name` 约束
///
/// **可行做法（本文件实现，来自作者侧验证过的方案）**：
///   * stuff.json 的 `prefab` 用**游戏已有的** prefab（`workbench`）
///   * 贴图用 `textures.xml` 的 `action="add"` 注册成自己的名字（`super_factory_0` 等）
///   * 建筑造好后，**直接把这栋建筑的 SpriteRenderer.sprite / MySpriteRenderer.sprite_id 换成我们的图**
///     （`Harmony` 挂在 `BuildHelper.DoBuildFacility` 的 Postfix 上）
///
/// ## 游戏有两套渲染，必须都改（否则贴图是透明的）
///   1. `MySpriteRenderer`（游戏自绘批渲染）——只认 `sprite_id`，改完要
///      `SetActive(false/true)` 或 `RefreshBody()` 才刷新
///   2. 原生 `SpriteRenderer` —— 兜底直接 `sr.sprite = ...`
/// </summary>
internal static class CustomSprite
{
    /// <summary>被替换外观的建筑 id</summary>
    internal const int TargetStuffId = Plugin.SuperFacilityId;

    /// <summary>贴图资源名前缀：4 个朝向分别是 `super_factory_0..3`</summary>
    internal const string SpriteNamePrefix = "super_factory";

    /// <summary>UI 图标资源名（= Defs/stuff.json 的 stuff_img）</summary>
    internal const string IconSpriteName = "ui_105050";

    /// <summary>朝向数量（游戏建筑 4 向旋转，贴图 0..3 对应）</summary>
    private const int RotationCount = 4;

    /// <summary>PNG 文件名（放在插件目录 Textures/ 下）</summary>
    private static readonly string[] BodyPngs =
    {
        "super_factory_0.png", "super_factory_1.png",
        "super_factory_2.png", "super_factory_3.png",
    };
    private const string IconPng = "ui_105050.png";

    /// <summary>每个朝向的 Sprite（下标 = rotation）</summary>
    private static readonly Sprite?[] _bodySprites = new Sprite?[RotationCount];
    private static Sprite? _iconSprite;
    private static bool _iconRegistered;
    private static bool _spritesLoaded;

    // ------------------------------------------------------------------
    // 贴图加载
    // ------------------------------------------------------------------

    private static string PluginDir
    {
        get
        {
            try
            {
                string loc = typeof(CustomSprite).Assembly.Location;
                if (!string.IsNullOrEmpty(loc)) return Path.GetDirectoryName(loc) ?? ".";
            }
            catch { }
            return ".";
        }
    }

    private static string? FindPng(string fileName)
    {
        var dir = PluginDir;
        string[] candidates =
        {
            Path.Combine(dir, "Textures", fileName),
            Path.Combine(dir, fileName),
            Path.Combine(@"C:\TerritoryModTest", "Textures", fileName),
            Path.Combine(@"C:\TerritoryModTest", fileName),
        };
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    /// <summary>
    /// 把 PNG 读成 Sprite。pivot 用左下角 (0,0)：建筑贴图要跟占地格左下角对齐
    /// （官方 mod 教程里建筑图的 anchor 就是 `0,0`）。
    /// </summary>
    private static Sprite? LoadSprite(string fileName, float pixelsPerUnit, Vector2 pivot)
    {
        try
        {
            string? path = FindPng(fileName);
            if (path == null)
            {
                Plugin.LogV($"[Facility] 找不到贴图 {fileName}");
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> arr = bytes;
            if (!ImageConversion.LoadImage(tex, arr, false))
            {
                Plugin.LogError($"[Facility] LoadImage 失败: {path}");
                return null;
            }
            tex.filterMode = FilterMode.Point;      // 像素风不插值
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.HideAndDontSave;

            var rect = new Rect(0f, 0f, tex.width, tex.height);
            var sprite = Sprite.Create(tex, rect, pivot, pixelsPerUnit, 0,
                                       SpriteMeshType.FullRect, Vector4.zero);
            // ⚠ 必须**显式命名**：Sprite.Create 造出来的图默认没有名字，
            // 诊断日志里会显示成空字符串，排查时分不清「图没加载」还是「图加载了但没名字」。
            try { sprite.name = Path.GetFileNameWithoutExtension(fileName); } catch { }
            sprite.hideFlags = HideFlags.HideAndDontSave;
            Plugin.LogV($"[Facility] 贴图已加载 {Path.GetFileName(path)} {tex.width}×{tex.height} ppu={pixelsPerUnit}");
            return sprite;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Facility] 加载贴图 {fileName} 异常: {ex}");
            return null;
        }
    }

    /// <summary>
    /// ppu 决定模型在世界里的物理大小，必须跟游戏建筑贴图一致。
    /// 实测取不到游戏原图的 ppu（`SpriteManager.Get("workbench_0")` 返回 null），
    /// 所以用**推算值 64**：游戏一格 = 64 像素（官方 mod 教程里 3×3 建筑示例
    /// `kingdom_treasure_box_0.png` = 192×192 = 3×64），建筑图 ppu 就等于每格像素数。
    /// 如果进游戏发现模型比占地格大/小，就调这个值（大→调大，小→调小）。
    /// </summary>
    private const float DefaultPixelsPerUnit = 64f;

    private static float BasePixelsPerUnit()
    {
        foreach (var name in new[] { "workbench_0", "gatherers_hut_0", "mine_0" })
        {
            try
            {
                var s = SpriteManager.Get(name);
                if (s != null && s.pixelsPerUnit > 1f) return s.pixelsPerUnit;
            }
            catch { }
        }
        return DefaultPixelsPerUnit;
    }

    private static void EnsureSprites()
    {
        if (_spritesLoaded) return;
        float ppu = BasePixelsPerUnit();
        int ok = 0;
        for (int i = 0; i < RotationCount; i++)
        {
            if (_bodySprites[i] != null) { ok++; continue; }
            _bodySprites[i] = LoadSprite(BodyPngs[i], ppu, Vector2.zero);
            if (_bodySprites[i] != null) ok++;
        }
        if (_iconSprite == null) _iconSprite = LoadSprite(IconPng, ppu, new Vector2(0.5f, 0.5f));
        if (ok == RotationCount) _spritesLoaded = true;
        if (ok > 0)
            Plugin.LogV($"[Facility] 自定义外观贴图：{ok}/{RotationCount} 个朝向已加载（ppu={ppu}）");
    }

    /// <summary>取某个朝向的贴图（越界就回落到 0）</summary>
    internal static Sprite? BodySprite(int rotation)
    {
        EnsureSprites();
        if (rotation < 0 || rotation >= RotationCount) rotation = 0;
        return _bodySprites[rotation] ?? _bodySprites[0];
    }

    // ------------------------------------------------------------------
    // 图标：注册进游戏贴图表（建造菜单用）
    // ------------------------------------------------------------------

    internal static Sprite? IconSprite
    {
        get
        {
            EnsureSprites();
            return _iconSprite;
        }
    }

    /// <summary>
    /// 取 `SpriteManager` 实例。
    /// ⚠ 静态字段 `Ins` 实测一直是 null（它不是静态单例），所以退回 `FindObjectOfType` 找场景实例，
    /// 找到了就缓存（后面每次换贴图都要用，不能每帧 Find）。
    /// </summary>
    private static SpriteManager? _sm;
    internal static SpriteManager? Manager
    {
        get
        {
            if (_sm != null) return _sm;
            try
            {
                var f = AccessTools.Field(typeof(SpriteManager), "Ins");
                _sm = f?.GetValue(null) as SpriteManager;
            }
            catch { }
            if (_sm == null)
            {
                try { _sm = UnityEngine.Object.FindObjectOfType<SpriteManager>(); } catch { }
            }
            return _sm;
        }
    }

    /// <summary>
    /// 把图标注册进游戏的贴图表（`SpriteManager.AddSpriteToDic`，私有方法，靠反射调）。
    /// </summary>
    internal static bool RegisterIcon()
    {
        if (_iconRegistered) return true;
        try
        {
            var icon = IconSprite;
            if (icon == null) return false;

            var sm = Manager;
            if (sm == null) return false;

            var m = AccessTools.Method(typeof(SpriteManager), "AddSpriteToDic",
                                       new[] { typeof(string), typeof(Sprite) });
            if (m == null)
            {
                Plugin.LogV("[Facility] 找不到 AddSpriteToDic(string,Sprite)");
                return false;
            }
            m.Invoke(sm, new object[] { IconSpriteName, icon });
            _iconRegistered = true;
            Plugin.LogV($"[Facility] 图标已注册进游戏贴图表: {IconSpriteName}");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogV($"[Facility] 注册图标失败: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 核心：给一栋刚建好的建筑换外观
    // ------------------------------------------------------------------

    /// <summary>
    /// 我们自己的渲染器挂在建筑根节点下的这个名字上（单独一个 SpriteRenderer）。
    /// 这样即使建筑自带的渲染器还残留，也只会出现「两套图重叠」而不是「我们的图被覆盖」；
    /// 同时下面会把自带那套关掉。
    /// </summary>
    private const string OwnRendererName = "facility_custom_body";

    /// <summary>
    /// 已经把外观换好的建筑 guid → 用的朝向。
    /// ⚠ 为什么要记住：游戏刷新建筑时会**把 `body/sp` 重新 SetActive(true)**，
    /// 于是「原来的制造台」又画出来了（用户实测：贴图是 3×2 但背后还叠着 1×3 的制造台）。
    /// 所以 `ApplyAppearance` 会被反复调用（每帧），这里用来记录状态、并在每次调用里强制对齐。
    /// </summary>
    private static readonly Dictionary<int, int> _applied = new();

    /// <summary>
    /// 把某栋建筑的模型换成我们的贴图（rotation 决定用哪张）。
    ///
    /// ⚠⚠ 极其重要的副作用：`sprite_id` 指向的是**共享的图集**。
    /// 综合生产所(105040) 和超级生产所(105050) **用的是同一个 `workbench` prefab**，
    /// 所以一旦改 `sprite_id`，4 座综合生产所**也会跟着变成我们的图**
    /// （用户实测反馈「4 座综合不见了，只剩超级生产所」就是这个原因）。
    ///
    /// 因此做法必须是：
    ///   1. 只对 105050 生效（调用方已判）
    ///   2. **不碰共享的 `sprite_id`**，只操作这栋建筑自己的原生渲染器
    ///   3. 挂一个自有渲染器画我们的图 + 把自带渲染器整个关掉，并且**每帧重做**
    ///      （游戏刷新会把自带渲染器重新激活）
    /// </summary>
    internal static void ApplyAppearance(Facility facility, int rotation)
    {
        if (facility == null) return;
        var sprite = BodySprite(rotation);
        if (sprite == null) return;

        int guid = 0;
        try { guid = facility.guid; } catch { }

        int disabled = 0, ours = 0;
        bool firstTime = !_applied.ContainsKey(guid);

        // ① 关掉自带渲染器（物体也关，游戏刷新会重新激活 → 所以每帧都要做）
        try
        {
            foreach (var sr in facility.gameObject.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == null || sr.gameObject.name == OwnRendererName) continue;
                if (sr.enabled) sr.enabled = false;
                disabled++;
                try
                {
                    if (sr.gameObject != facility.gameObject && sr.gameObject.activeSelf)
                        sr.gameObject.SetActive(false);
                }
                catch { }
            }
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] 关闭自带渲染器失败: {ex.Message}"); }

        // ② 挂/更新我们自己的渲染器（画自定义贴图）
        try
        {
            var t = facility.transform.Find(OwnRendererName);
            SpriteRenderer sr;
            if (t == null)
            {
                var go = new GameObject(OwnRendererName);
                go.transform.SetParent(facility.transform, false);
                sr = go.AddComponent<SpriteRenderer>();
                var refSr = facility.gameObject.GetComponentInChildren<SpriteRenderer>(true);
                if (refSr != null)
                {
                    try { sr.sharedMaterial = refSr.sharedMaterial; } catch { }
                    try { sr.sortingLayerID = refSr.sortingLayerID; } catch { }
                    try { sr.sortingOrder = refSr.sortingOrder; } catch { }
                }
            }
            else sr = t.GetComponent<SpriteRenderer>();
            if (sr != null)
            {
                if (sr.sprite != sprite) sr.sprite = sprite;
                if (!sr.enabled) sr.enabled = true;
                if (!sr.gameObject.activeSelf) sr.gameObject.SetActive(true);
                ours = 1;
            }
        }
        catch (Exception ex) { Plugin.LogError($"[Facility] 挂自定义渲染器失败: {ex}"); }

        // ⚠ **不动 `sp_body.sprite_id`**：它指向共享图集，改了会连带把 105040 一起换掉。
        // （早期版本改过它，结果用户看到「综合生产所不见了、只剩超级生产所」。）

        if (firstTime)
        {
            _applied[guid] = rotation;
            Plugin.LogV($"[Facility] 超级生产所 guid={guid} 外观已替换" +
                        $"（朝向 {rotation}：关自带渲染器 {disabled} 个 / 自建渲染器 {ours} 个；" +
                        $"未改共享 sprite_id，避免影响综合生产所）");
        }
    }

    /// <summary>建筑被拆除/换档时清掉记录</summary>
    internal static void Forget(int guid)
    {
        try { _applied.Remove(guid); } catch { }
    }

    /// <summary>取相对路径（诊断用），例如 "body/sp"</summary>
    private static string PathOf(Transform t, Transform root)
    {
        var parts = new List<string>();
        var cur = t;
        while (cur != null && cur != root)
        {
            parts.Insert(0, cur.name);
            cur = cur.parent;
        }
        return string.Join("/", parts);
    }

    /// <summary>给「所有已存在的」超级生产所补一次外观（读档/热重载后用）</summary>
    internal static void ReapplyToAll()
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Facility>();
            if (all == null) return;
            int n = 0;
            foreach (var f in all)
            {
                if (f == null || f.stuff_id != TargetStuffId) continue;
                ApplyAppearance(f, 0);
                n++;
            }
            if (n > 0) Plugin.LogV($"[Facility] 已给 {n} 座已存在的超级生产所补上外观");
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] 补外观失败: {ex.Message}"); }
    }
}

/// <summary>
/// 挂钩建筑创建：让游戏**用 workbench 的预制体**去造我们的建筑
/// （stuff.json 里 prefab 写的就是 workbench，这里只是保险 + 拿到返回值换贴图）。
///
/// `BuildHelper.DoBuildFacility(int stuff_id, int rotation, Vector3 pos, int ..., bool ...)` 是私有方法，
/// 参数里有 `__result`（Facility），建好后我们直接换它的贴图。
/// </summary>
[HarmonyPatch]
internal static class CustomAppearancePatch
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static System.Reflection.MethodBase? TargetMethod()
    {
        var t = AccessTools.TypeByName("BuildHelper");
        if (t == null)
        {
            Plugin.LogError("[Facility] 找不到 BuildHelper 类型，自定义外观补丁未挂上");
            return null;
        }
        var m = AccessTools.Method(t, "DoBuildFacility");
        if (m == null)
            Plugin.LogError("[Facility] 找不到 BuildHelper.DoBuildFacility，自定义外观补丁未挂上");
        return m;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static void Postfix(int stuff_id, int rotation, Facility __result)
    {
        try
        {
            if (stuff_id != CustomSprite.TargetStuffId) return;
            CustomSprite.ApplyAppearance(__result, rotation);
        }
        catch (Exception ex) { Plugin.LogError($"[Facility] 外观补丁异常: {ex}"); }
    }
}
