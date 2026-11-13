using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;

// A whole Russian text uses one font face, including its punctuation.
// Font assets and their glyph metrics are never modified in place.
internal static class RussianFontFix
{
    private const string Required = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя.,!?;:()[]«»—-0123456789";
    private static readonly Dictionary<TMP_FontAsset, TMP_FontAsset> unified = new Dictionary<TMP_FontAsset, TMP_FontAsset>();
    private static ManualLogSource logger;
    private static TMP_FontAsset generated;
    private static bool reportedFailure;

    internal static void Enable(Harmony harmony, ManualLogSource log)
    {
        logger = log;
        var patch = new HarmonyMethod(typeof(RussianFontFix), "BeforeRender");
        harmony.Patch(AccessTools.Method(typeof(TextMeshProUGUI), "OnPreRenderCanvas"), prefix: patch);
        // World-space signs may rebuild from the camera callback, outside the
        // normal canvas material-update pass. Bind the matching material after
        // geometry generation as well, so UVs never sample the previous atlas.
        harmony.Patch(AccessTools.Method(typeof(TextMeshPro), "OnPreRenderObject"), prefix: patch,
            postfix: new HarmonyMethod(typeof(RussianFontFix), "AfterWorldRender"));
    }
    private static bool Cyrillic(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text) if (c >= '\u0400' && c <= '\u04ff') return true;
        return false;
    }
    private static bool Complete(TMP_FontAsset font)
    {
        if (!font) return false;
        uint[] missing;
        return font.HasCharacters(Required, out missing, false, true);
    }
    private static TMP_FontAsset Find(TMP_FontAsset font, HashSet<TMP_FontAsset> visited)
    {
        if (!font || !visited.Add(font)) return null;
        if (Complete(font)) return font;
        if (font.fallbackFontAssetTable != null)
            foreach (var fallback in font.fallbackFontAssetTable)
            {
                var result = Find(fallback, visited);
                if (result) return result;
            }
        return null;
    }
    private static TMP_FontAsset Choose(TMP_FontAsset source)
    {
        var visited = new HashSet<TMP_FontAsset>();
        var result = Find(source, visited);
        if (!result && TMP_Settings.fallbackFontAssets != null)
            foreach (var fallback in TMP_Settings.fallbackFontAssets)
            { result = Find(fallback, visited); if (result) break; }
        if (!result)
        {
            if (!generated)
            {
                var native = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Arial" }, 48);
                generated = TMP_FontAsset.CreateFontAsset(native);
                if (!Complete(generated)) throw new InvalidOperationException("No complete Cyrillic font face found");
                generated.name = "CuRussian-Unified";
                UnityEngine.Object.DontDestroyOnLoad(native);
                UnityEngine.Object.DontDestroyOnLoad(generated);
                UnityEngine.Object.DontDestroyOnLoad(generated.material);
                foreach (var texture in generated.atlasTextures) if (texture) UnityEngine.Object.DontDestroyOnLoad(texture);
            }
            result = generated;
        }
        logger.LogInfo("Unified Russian glyphs: " + source.name + " -> " + result.name);
        return result;
    }
    private static void BeforeRender(TMP_Text __instance)
    {
        if (!__instance || !__instance.font || !Cyrillic(__instance.text)) return;
        try
        {
            var original = __instance.font;
            TMP_FontAsset target;
            if (!unified.TryGetValue(original, out target) || !target)
            {
                target = Choose(original);
                unified[original] = target;
                unified[target] = target;
            }
            if (target != original)
            {
                __instance.font = target;
                __instance.fontSharedMaterial = target.material;
                __instance.havePropertiesChanged = true;
            }
        }
        catch (Exception e)
        {
            if (!reportedFailure) { reportedFailure = true; logger.LogWarning("Unified Russian font unavailable; original font retained: " + e.Message); }
        }
    }
    private static void AfterWorldRender(TextMeshPro __instance)
    {
        if (!__instance || !__instance.font || !Cyrillic(__instance.text)
            || !unified.ContainsKey(__instance.font)) return;
        try
        {
            var font = __instance.font;
            var material = __instance.fontSharedMaterial;
            if (!material || material.GetTexture("_MainTex") != font.atlasTexture)
            {
                material = font.material;
                __instance.fontSharedMaterial = material;
            }
            var renderer = __instance.GetComponent<MeshRenderer>();
            if (renderer && renderer.sharedMaterial != material) renderer.sharedMaterial = material;
        }
        catch (Exception e)
        {
            if (!reportedFailure) { reportedFailure = true; logger.LogWarning("World text font material not synchronized: " + e.Message); }
        }
    }
}
