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
    /// 把某栋建筑的模型换成我们的贴图（rotation 决定用哪张）。
    /// 两套渲染都要改：MySpriteRenderer 认 sprite_id，原生 SpriteRenderer 认 sprite。
    /// </summary>
    internal static void ApplyAppearance(Facility facility, int rotation)
    {
        if (facility == null) return;
        var sprite = BodySprite(rotation);
        if (sprite == null)
        {
            Plugin.LogV("[Facility] 自定义外观贴图没加载出来，保持原样");
            return;
        }

        int native = 0, custom = 0;
        try
        {
            // ① 游戏自绘批渲染（MySpriteRenderer）：只认 sprite_id
            var spBody = facility.sp_body;
            if (spBody != null)
            {
                try
                {
                    var sm = Manager;
                    if (sm != null)
                    {
                        string spriteName = SpriteNamePrefix + "_" + rotation;
                        spBody.sprite_id = sm.TryGetSpriteId(spriteName);
                        custom++;
                        Plugin.LogV($"[Facility] sprite_id ← {spriteName}");
                    }
                    else Plugin.LogV("[Facility] 拿不到 SpriteManager 实例，跳过 sprite_id");
                }
                catch (Exception ex) { Plugin.LogV($"[Facility] 设置 sprite_id 失败: {ex.Message}"); }

                // 必须强制刷新：MySpriteRenderer 不会自己发现 sprite_id 变了
                try
                {
                    spBody.gameObject.SetActive(false);
                    spBody.gameObject.SetActive(true);
                }
                catch (Exception ex) { Plugin.LogV($"[Facility] 刷新渲染失败（可忽略）: {ex.Message}"); }
            }

            // ② 原生 SpriteRenderer 兜底（有些 prefab 用原生渲染）
            foreach (var sr in facility.gameObject.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == null) continue;
                sr.sprite = sprite;
                sr.enabled = true;
                native++;
            }
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Facility] 换外观失败: {ex}");
        }
        Plugin.LogV($"[Facility] 超级生产所外观已替换（朝向 {rotation}：自定义渲染 {custom} 个 / 原生渲染 {native} 个）");
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
