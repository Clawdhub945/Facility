using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// 自定义建筑外观的加载器：把 mod 自带的 PNG 变成游戏能用的 `Sprite`，
/// 再基于场景里**已有一座建筑**的模型克隆出「换了图」的 prefab 给新建筑用。
///
/// 为什么必须这么做（2026-09-13 实测踩坑，两次）：
/// 1. `Textures/textures.xml` 的 `action="add"` **只能加图片资源**，加不了游戏不认识的新 prefab；
///    stuff.json 的 `prefab` 写新名字 → 放置建筑时游戏按名字找不到预制体 → `NullReferenceException`。
/// 2. 改用 `action="replace"` 覆盖已有贴图名（workbench_0）——**实测没生效**：
///    洋红探针测试里世界里 0 个洋红像素（只有工具栏 14 个）。
///
/// 现在的 DLL 路线：运行时自己造 Sprite → 克隆模板模型换图 →
/// Harmony 拦 `PrefabManager.GetPrefab("super_factory")` 返回这个克隆。
/// 完全不依赖游戏美术资源，改图只要换 PNG 重启游戏。
/// </summary>
internal static class CustomSprite
{
    /// <summary>本 mod 自定义外观对应的 prefab 名（与 Defs/stuff.json 的 prefab 字段一致）</summary>
    internal const string CustomPrefabName = "super_factory";

    /// <summary>
    /// 游戏解析建筑外观时用的名字 = `stuff_img_on_map`，即 **`<prefab>_0`**（带 `_0` 后缀）。
    /// 实测证据：日志里旧建筑（prefab=workbench）发出的是
    /// `PrefabManager` 入口请求 `"workbench_0"`，而不是 `"workbench"`。
    /// 所以自定义外观必须**同时认这两个名字**，只认 `super_factory` 会漏掉真正的请求。
    /// </summary>
    internal const string CustomSpriteName = CustomPrefabName + "_0";

    /// <summary>请求的名字是否属于我们的自定义外观</summary>
    internal static bool IsCustomName(string? name)
        => name == CustomPrefabName || name == CustomSpriteName;

    /// <summary>
    /// 自定义外观建不出来时的**保底 prefab**：一个游戏自带的建筑 prefab。
    /// 用途：保证「超级生产所」在任何情况下都能被建造（外观退化成原版建筑），
    /// 而不是让玩家点一下吃 NullReferenceException、完全放不下去。
    /// </summary>
    internal const string FallbackPrefabName = "workbench";

    private const string BodyPngName = "super_factory.png";
    private const string IconPngName = "ui_105050.png";

    private static Sprite? _bodySprite;
    private static Sprite? _iconSprite;
    private static GameObject? _customPrefab;
    private static bool _tried;
    private static bool _failed;

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

    /// <summary>找 PNG：插件目录/Textures、插件目录、TerritoryModTest/Textures 都试一遍</summary>
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

    private static Sprite? LoadSprite(string fileName, float pixelsPerUnit, Vector2 pivot)
    {
        try
        {
            string? path = FindPng(fileName);
            if (path == null)
            {
                Plugin.LogV($"[Facility] 找不到自定义贴图 {fileName}" +
                                  "（找过 插件目录/Textures、插件目录、TerritoryModTest/Textures）");
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            // IL2CPP 下 C# byte[] → Il2CppStructArray<byte> 有隐式转换（CS0571：不能显式调 op_Implicit）
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> arr = bytes;
            if (!ImageConversion.LoadImage(tex, arr, false))
            {
                Plugin.LogV($"[Facility] LoadImage 失败: {path}");
                return null;
            }
            tex.filterMode = FilterMode.Point;    // 像素风：不做插值
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.hideFlags = HideFlags.HideAndDontSave;

            var rect = new Rect(0f, 0f, tex.width, tex.height);
            var sprite = Sprite.Create(tex, rect, pivot, pixelsPerUnit, 0,
                                       SpriteMeshType.FullRect, Vector4.zero);
            sprite.hideFlags = HideFlags.HideAndDontSave;
            Plugin.LogV($"[Facility] 自定义贴图已加载: {Path.GetFileName(path)} " +
                           $"{tex.width}×{tex.height} ppu={pixelsPerUnit} pivot=({pivot.x:0.##},{pivot.y:0.##})");
            return sprite;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Facility] 加载自定义贴图 {fileName} 异常: {ex}");
            return null;
        }
    }

    /// <summary>
    /// 把「已经造好的一座本 mod 建筑」当模板：
    /// 找它身上面积最大的 SpriteRenderer（= 建筑本体那张图），
    /// 克隆整个建筑 GameObject 再把那张图换成我们的。
    /// </summary>
    private static GameObject? FindTemplateFromScene()
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType<Facility>();
            if (all == null) return null;
            foreach (var f in all)
            {
                if (f == null || !Plugin.IsManaged(f.stuff_id)) continue;
                var go = f.gameObject;
                if (go == null) continue;
                Plugin.LogV($"[Facility] 用场景里的建筑当模板: {go.name} (stuff={f.stuff_id})");
                return go;
            }
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] 找模板建筑失败: {ex.Message}"); }
        return null;
    }

    /// <summary>在模板里挑「面积最大的 SpriteRenderer」——建筑本体一般是最大的那张图</summary>
    private static SpriteRenderer? PickBodyRenderer(GameObject template)
    {
        SpriteRenderer? best = null;
        float bestArea = -1f;
        try
        {
            foreach (var sr in template.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == null || sr.sprite == null) continue;
                var s = sr.sprite.rect.size;
                float area = s.x * s.y;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = sr;
                }
            }
        }
        catch (Exception ex) { Plugin.LogV($"[Facility] 挑本体渲染器失败: {ex.Message}"); }
        return best;
    }

    /// <summary>
    /// 自定义 UI 图标的哨兵 id：`SpriteManager.TryGetSpriteId("ui_105050")` 被拦下时返回它，
    /// `SpriteManager.GetSprite(id)` 再按它取我们的 Sprite。
    /// 用负数，和游戏自己的贴图 id（正整数）不会撞。
    /// </summary>
    internal const int IconSentinelId = -105050;

    /// <summary>只加载图标（场景里还没有建筑时也能让菜单图标正常）</summary>
    internal static Sprite? EnsureIconOnly()
    {
        if (_iconSprite == null) _iconSprite = LoadSprite(IconPngName, 100f, new Vector2(0.5f, 0.5f));
        return _iconSprite;
    }

    /// <summary>自定义 UI 图标的资源名（= Defs/stuff.json 里超级生产所的 stuff_img）</summary>
    internal const string IconSpriteName = "ui_105050";

    internal static Sprite? IconSprite => _iconSprite;

    /// <summary>是否已经用 `AddSpriteToDic` 把图标注册进游戏贴图表</summary>
    private static bool _iconRegistered;

    /// <summary>
    /// 把图标注册进游戏的贴图表（建造菜单图标就靠这个）。
    ///
    /// 实测要点（dump 出来的方法清单 + interop 签名核对）：
    ///   * 单例是 **静态字段 `SpriteManager.Ins`**（`get_Ins` 的 native 签名里返回类型是 D，
    ///     interop 没给出 C# 属性），所以用反射读字段。
    ///   * `AddSpriteToDic(string, Sprite)` 在 native 签名里是 **Private**，也要反射调。
    /// 注册后游戏按 `stuff_img`（`ui_105050`）查表即可拿到我们的图，菜单不再是白块。
    ///
    /// （早期版本试着拦 `TryGetSpriteId`/`GetSprite` 反而踩坑：
    ///   多个不同签名的方法塞进同一个补丁类 → `IL Compile Error`；
    ///   而且 `GetSprite(int)` 根本不存在，真正的是 `GetSprite(SpriteId)`。）
    /// </summary>
    internal static bool RegisterIcon()
    {
        if (_iconRegistered) return true;
        try
        {
            var icon = EnsureIconOnly();
            if (icon == null) return false;

            object? smObj = null;
            // 先试静态字段 Ins；实测它一直是 null（不是静态单例），
            // 所以在场景里按类型找一个实例（SpriteManager 是 MonoBehaviour）。
            try
            {
                var f = AccessTools.Field(typeof(SpriteManager), "Ins");
                if (f != null) smObj = f.GetValue(null);
            }
            catch (Exception ex) { Plugin.LogV($"[Facility] 读 SpriteManager.Ins 失败: {ex.Message}"); }
            if (smObj is not SpriteManager sm)
            {
                try { sm = UnityEngine.Object.FindObjectOfType<SpriteManager>(); }
                catch (Exception ex) { Plugin.LogV($"[Facility] FindObjectOfType<SpriteManager> 失败: {ex.Message}"); sm = null!; }
            }
            if (sm == null)
            {
                Plugin.LogV("[Facility] 还没找到 SpriteManager 实例，图标注册稍后再试");
                return false;
            }

            var m = AccessTools.Method(typeof(SpriteManager), "AddSpriteToDic",
                                       new[] { typeof(string), typeof(Sprite) });
            if (m == null)
            {
                Plugin.LogV("[Facility] 找不到 AddSpriteToDic(string,Sprite)，图标注册跳过（菜单会显示白块）");
                return false;
            }
            m.Invoke(sm, new object[] { IconSpriteName, icon });
            _iconRegistered = true;
            Plugin.LogV($"[Facility] 自定义图标已注册进游戏贴图表: {IconSpriteName}");
            // 回查一次：注册后能不能按名字查回来。查不回来说明字典不是同一个 / 注册没生效
            try
            {
                var got = ((SpriteManager)sm).TryGetSpriteId(IconSpriteName);
                Plugin.LogV($"[Facility] 图标回查 TryGetSpriteId(\"{IconSpriteName}\") = {got}");
            }
            catch (Exception ex) { Plugin.LogV($"[Facility] 图标回查失败: {ex.Message}"); }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogV($"[Facility] 注册自定义图标失败（菜单会显示白块）: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 造一个 `SpriteId` 结构体，承载哨兵 id。
    /// ⚠ 不用 `new SpriteId(...)`：interop 里 `SpriteId` 的构造函数没有绑定，
    /// 只能用反射找「单 int 参数的构造」。
    /// </summary>
    internal static object? MakeSpriteId(int id)
    {
        try
        {
            if (_spriteIdCtor == null)
            {
                foreach (var c in typeof(SpriteId).GetConstructors(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(int)) { _spriteIdCtor = c; break; }
                }
            }
            if (_spriteIdCtor == null)
            {
                Plugin.LogV("[Facility] SpriteId(int) 构造没找到，图标走不通");
                return null;
            }
            return _spriteIdCtor.Invoke(new object[] { id });
        }
        catch (Exception ex)
        {
            Plugin.LogV($"[Facility] 构造 SpriteId 失败: {ex.Message}");
            return null;
        }
    }

    private static ConstructorInfo? _spriteIdCtor;

    /// <summary>
    /// 自己**从零搭**一个建筑 prefab，而不是克隆整座建筑：
    /// 克隆整座 Facility 会把 BagController / FacilityWork / UI 引用一起复制，
    /// 放置时容易炸（实测用户在放置预览阶段就吃到 NullReferenceException）。
    /// 这里只复制「怎么画」的部分：渲染器的 material / sortingOrder / scale，加一个 BoxCollider2D 供射线命中。
    /// </summary>
    private static GameObject? BuildPrefabFromScratch(SpriteRenderer? template)
    {
        var go = new GameObject(CustomPrefabName);
        go.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(go);

        var sr = go.AddComponent<SpriteRenderer>();
        if (_bodySprite != null) sr.sprite = _bodySprite;
        if (template != null)
        {
            try { sr.sharedMaterial = template.sharedMaterial; } catch { }
            try { sr.sortingLayerID = template.sortingLayerID; } catch { }
            try { sr.sortingOrder = template.sortingOrder; } catch { }
            try { go.transform.localScale = template.transform.lossyScale; } catch { }
        }

        // 不再加碰撞盒：建筑的点击由游戏自己的设施系统处理（它按格子/实体表命中），
        // 而且加 BoxCollider2D 需要额外引用 UnityEngine.Physics2DModule。
        // 少一个组件就少一个炸点。

        Plugin.LogV($"[Facility] 自定义 prefab「{CustomPrefabName}」已就绪（自建 GameObject + SpriteRenderer）");
        return go;
    }

    internal static GameObject? EnsurePrefab()
    {
        if (_tried) return _customPrefab;
        _tried = true;
        try
        {
            // 模板只用来参考「渲染器怎么设的」（ppu/pivot/材质/排序层）。
            // 拿不到也能建（用默认值），所以**不再因为场景里没有建筑就放弃**——
            // 早期版本那样做，导致插件的 Load 阶段建不出 prefab，而 Def 恰恰在那时解析。
            SpriteRenderer? template = null;
            try
            {
                var tplGo = FindTemplateFromScene();
                if (tplGo != null) template = PickBodyRenderer(tplGo);
            }
            catch (Exception ex) { Plugin.LogV($"[Facility] 取模板参考失败（用默认值继续）: {ex.Message}"); }

            float ppu = 100f;
            Vector2 pivot = Vector2.zero;
            if (template?.sprite != null)
            {
                try { ppu = template.sprite.pixelsPerUnit; } catch { }
                try
                {
                    var sz = template.sprite.rect.size;
                    pivot = new Vector2(template.sprite.pivot.x / sz.x, template.sprite.pivot.y / sz.y);
                }
                catch { }
            }

            _bodySprite = LoadSprite(BodyPngName, ppu, pivot);
            if (_bodySprite == null)
            {
                _failed = true;
                return null;
            }
            _iconSprite = LoadSprite(IconPngName, 100f, new Vector2(0.5f, 0.5f));

            _customPrefab = BuildPrefabFromScratch(template);
            TryRegisterInDictionary();
            return _customPrefab;
        }
        catch (Exception ex)
        {
            _failed = true;
            Plugin.LogError($"[Facility] 构建自定义 prefab 失败: {ex}");
            return null;
        }
    }

    internal static bool HasFailed => _failed;

    /// <summary>
    /// 取一份**新的**自定义外观实例（每次实例化一份，不能把同一个 GameObject 交给游戏两次）。
    /// 模板还没准备好时返回 null（调用方回退原逻辑，宁可外观不对也别让建筑炸）。
    /// </summary>
    internal static GameObject? NewInstance()
    {
        var tpl = EnsurePrefab();
        if (tpl == null) return null;
        try
        {
            var go = UnityEngine.Object.Instantiate(tpl);
            go.name = CustomPrefabName;
            return go;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Facility] 实例化自定义外观失败: {ex}");
            return null;
        }
    }

    /// <summary>把自定义 prefab 写进 PrefabManager 的字典（若可写），让游戏按名字查表也能拿到</summary>
    internal static bool TryRegisterInDictionary()
    {
        try
        {
            var tpl = EnsurePrefab();
            if (tpl == null) return false;
            var fld = AccessTools.Field(typeof(PrefabManager), "prefab_dic");
            if (fld == null) return false;
            var dic = fld.GetValue(null) as System.Collections.IDictionary;
            if (dic == null) return false;
            dic[CustomPrefabName] = tpl;
            Plugin.LogV($"[Facility] 已把「{CustomPrefabName}」注册进 PrefabManager.prefab_dic");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogV($"[Facility] 注册 prefab_dic 失败（不影响补丁路线）: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// 让游戏拿到我们的自定义建筑外观。**三个入口都拦**（只拦一个不够——实测
/// `PrefabManager.GetPrefab(string,int)` 是私有方法，`AccessTools.Method` 默认找不到，
/// 而且它在 interop 里没有 C# 绑定；更不能让它把 `PatchAll()` 整条挂掉，
/// 那会连带工位数补丁一起失效——这是 2026-09-13 的真实事故）：
///
///   1. `PrefabManager.Create(string, Transform)`  ← 公开、有绑定，最可能被用的入口
///   2. `PrefabManager.Get(string)`               ← 公开、有绑定
///   3. `PrefabManager.GetPrefab(string, int)`    ← 私有，用反射带 NonPublic 找
///
/// 命中时返回一份**新的实例**（不能返回同一个 GameObject 两次）。
/// </summary>
[HarmonyPatch]
internal static class CustomPrefabPatch
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static IEnumerable<MethodBase> TargetMethods()
    {
        var list = new List<MethodBase>();
        var seen = new HashSet<string>();
        var flags = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        // ⚠ 必须**顺着继承链**枚举：实测 `PrefabManager` 自己的方法清单里没有
        // GetPrefab（只有 Get/Create/TryGetFromCache…），它在基类上。
        // `AccessTools.Method` 默认只找声明的类型，所以之前才找不到。
        for (var t = typeof(PrefabManager); t != null && t != typeof(object); t = t.BaseType)
        {
            MethodInfo[] ms;
            try { ms = t.GetMethods(flags); } catch { continue; }
            foreach (var m in ms)
            {
                bool interesting = m.Name == "GetPrefab" || m.Name == "Get" || m.Name == "Create";
                if (!interesting) continue;
                var ps = m.GetParameters();
                if (ps.Length < 1 || ps[0].ParameterType != typeof(string)) continue;
                if (m.ReturnType != typeof(GameObject)) continue;
                if (!seen.Add(m.DeclaringType?.Name + "." + m.Name + "/" + ps.Length)) continue;
                list.Add(m);
            }
        }

        if (list.Count == 0)
            Plugin.LogError("[Facility] 没找到任何 PrefabManager 入口，自定义外观补丁未挂上");
        else
            Plugin.LogV($"[Facility] 自定义外观补丁已挂 {list.Count} 个入口: " +
                           string.Join(", ", list.ConvertAll(m => $"{m.DeclaringType?.Name}.{m.Name}/{m.GetParameters().Length}")));
        return list;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("ReSharper", "UnusedMember.Global")]
    static bool Prefix(string __0, ref GameObject __result)
    {
        try
        {
            // 全量记录（仅详细模式）：抓清游戏到底用什么名字、走哪个入口取 prefab。
            // 排查「放置建筑时空引用」时就靠这个 —— 就是靠它发现名字带 `_0` 后缀的。
            if (__0 != null && (__0.Contains("factory") || __0.Contains("workbench")))
                Plugin.LogV($"[Facility] prefab 入口请求: \"{__0}\"");

            if (!CustomSprite.IsCustomName(__0)) return true;

            // 第一优先：我们自己的红砖厂房
            var go = CustomSprite.NewInstance();
            if (go != null)
            {
                Plugin.LogV($"[Facility] 自定义外观入口命中（{__0}）→ 已返回自有 prefab 实例");
                __result = go;
                return false;
            }

            // **保底**：自定义 prefab 建不出来时，退回一个游戏已有的建筑 prefab，
            // 保证建筑一定能放下去（只是外观不是我们画的）。
            // 这条兜底的意义：宁可外观不对，也不能让用户「点一下就报空引用、放不下去」。
            try
            {
                var fallback = PrefabManager.Get(CustomSprite.FallbackPrefabName);
                if (fallback != null)
                {
                    Plugin.LogError($"[Facility] 自定义外观不可用，已退回游戏预制体" +
                                    $"「{CustomSprite.FallbackPrefabName}」（建筑可正常建造，但外观不是自定义的）");
                    __result = UnityEngine.Object.Instantiate(fallback);
                    return false;
                }
            }
            catch (Exception ex) { Plugin.LogV($"[Facility] 取保底 prefab 失败: {ex.Message}"); }
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogError($"[Facility] 自定义外观补丁异常: {ex}");
            return true;
        }
    }
}
