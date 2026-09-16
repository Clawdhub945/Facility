using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace FacilityMod;

/// <summary>
/// **`MySpriteRenderer` 反射探测** —— 查清为什么挂不上这个组件。
///
/// ## 背景
/// 自检日志显示红块的组件只有 `Transform, SpriteRenderer`，
/// **没有 `MySpriteRenderer`** —— 说明 <see cref="SpriteUi"/> 里的反射查找失败了，
/// 但之前的日志没打出失败原因（被 `LogV` 详细模式吞了或异常被静默）。
///
/// 这里把每一步的结果都明确打出来：
///   ① `AccessTools.TypeByName("MySpriteRenderer")` 能不能拿到类型
///   ② 它的 `Create` 重载有哪些（参数类型逐个列出）
///   ③ 直接调 `Create(sr)` 和 `Create(sr, name, order, pos)` 的结果
///   ④ `AddComponent` 能否成功
/// </summary>
internal static class MySpriteRendererProbe
{
    internal static void Run(GameObject host)
    {
        Plugin.LogV("[FacilityUI] ===== MySpriteRenderer 探测 =====");

        // ① 找类型
        Type? t = null;
        try
        {
            t = AccessTools.TypeByName("MySpriteRenderer");
            Plugin.LogV($"[FacilityUI] ① AccessTools.TypeByName → {(t == null ? "【null】" : t.FullName)}");
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] ① 异常: {ex.Message}"); }

        if (t == null)
        {
            // 兜底：扫所有已加载程序集
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type? found = null;
                    try { found = asm.GetType("MySpriteRenderer", false); } catch { }
                    if (found != null)
                    {
                        t = found;
                        Plugin.LogV($"[FacilityUI] ① 兜底扫描在 {asm.GetName().Name} 找到: {t.FullName}");
                        break;
                    }
                }
            }
            catch (Exception ex) { Plugin.LogV($"[FacilityUI] ① 兜底扫描异常: {ex.Message}"); }
        }

        if (t == null) { Plugin.LogV("[FacilityUI] 探测终止：找不到 MySpriteRenderer 类型"); return; }

        // ② 列出 Create 重载
        try
        {
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Create") continue;
                var ps = m.GetParameters();
                var sb = new System.Text.StringBuilder();
                foreach (var p in ps) sb.Append(p.ParameterType.Name).Append(' ');
                Plugin.LogV($"[FacilityUI] ② Create({sb.ToString().Trim()}) 返回={m.ReturnType.Name}");
            }
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] ② 列出重载异常: {ex.Message}"); }

        // ③ 造一个测试 SpriteRenderer 并试着挂
        try
        {
            var go = new GameObject("probe_sr");
            go.transform.SetParent(host.transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = SpriteUi.WhiteSprite();

            // 用 Create(SpriteRenderer) —— 反编译里 `Facility.InitBodySp` 用的就是这个
            var m1 = t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
                                 null, new[] { typeof(SpriteRenderer) }, null);
            Plugin.LogV($"[FacilityUI] ③ Create(SpriteRenderer) 找到 = {(m1 != null)}");
            if (m1 != null)
            {
                try
                {
                    var r = m1.Invoke(null, new object[] { sr });
                    Plugin.LogV($"[FacilityUI] ③ Create(sr) 返回 = {(r == null ? "null" : r.GetType().Name)}");
                }
                catch (Exception ex) { Plugin.LogV($"[FacilityUI] ③ Create(sr) 调用异常: {ex.GetType().Name}: {ex.Message}"); }
            }

            Plugin.LogV($"[FacilityUI] ③ 挂完后组件 = {Names(go)}");

            // ④ AddComponent 兜底
            try
            {
                var added = go.AddComponent(Il2CppInterop.Runtime.Il2CppType.From(t));
                Plugin.LogV($"[FacilityUI] ④ AddComponent 结果 = {(added == null ? "null" : added.GetType().Name)}" +
                            $"，组件 = {Names(go)}");
            }
            catch (Exception ex) { Plugin.LogV($"[FacilityUI] ④ AddComponent 异常: {ex.GetType().Name}: {ex.Message}"); }

            UnityEngine.Object.Destroy(go);
        }
        catch (Exception ex) { Plugin.LogV($"[FacilityUI] ③④ 异常: {ex.Message}"); }

        Plugin.LogV("[FacilityUI] ===== MySpriteRenderer 探测结束 =====");
    }

    private static string Names(GameObject go)
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
}
