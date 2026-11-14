using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;

// Prepare a coherent font/material BEFORE TMP builds glyph and submesh references.
// Only newly created private font assets receive glyphs or aliases; game assets are read-only.
internal static class RussianFontFix
{
    private const string Russian = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя«»";
    private static readonly string Core = BuildCore();
    private static readonly Dictionary<TMP_FontAsset, TMP_FontAsset> fonts = new Dictionary<TMP_FontAsset, TMP_FontAsset>();
    private static readonly Dictionary<TMP_Text, Binding> bindings = new Dictionary<TMP_Text, Binding>();
    private static readonly HashSet<TMP_Text> applying = new HashSet<TMP_Text>();
    private static readonly HashSet<TMP_FontAsset> privateFonts = new HashSet<TMP_FontAsset>();
    private static readonly HashSet<uint> reportedMissing = new HashSet<uint>();
    private static readonly MethodInfo UpdateWorldMaterial = AccessTools.Method(typeof(TextMeshPro), "UpdateMaterial");
    private static ManualLogSource logger;
    private static Font retro;
    private static bool searchedResources, reportedFailure;
    private sealed class Binding { internal TMP_FontAsset Font; internal Material Material; }

    internal static void Enable(Harmony harmony, ManualLogSource log)
    {
        logger = log;
        var before = new HarmonyMethod(typeof(RussianFontFix), "BeforeLayout");
        // SetArraySizes is also reached by preferred-size and GetTextInfo paths,
        // unlike camera/canvas-only render callbacks. It establishes material indices.
        harmony.Patch(AccessTools.Method(typeof(TextMeshProUGUI), "SetArraySizes"), prefix: before);
        harmony.Patch(AccessTools.Method(typeof(TextMeshPro), "SetArraySizes"), prefix: before);
        var release = new HarmonyMethod(typeof(RussianFontFix), "AfterDestroy");
        harmony.Patch(AccessTools.Method(typeof(TextMeshProUGUI), "OnDestroy"), postfix: release);
        harmony.Patch(AccessTools.Method(typeof(TextMeshPro), "OnDestroy"), postfix: release);
        logger.LogInfo("Russian pixel font preparation enabled before TMP glyph/material layout.");
    }
    private static string BuildCore()
    {
        var value = new System.Text.StringBuilder();
        for (char c = ' '; c <= '~'; c++) value.Append(c);
        return value.Append(Russian).ToString();
    }
    private static bool Cyrillic(string text)
    {
        if (String.IsNullOrEmpty(text)) return false;
        foreach (char c in text) if (c >= '\u0400' && c <= '\u04ff') return true;
        return false;
    }
    private static bool PixelFamily(TMP_FontAsset font)
    {
        if (!font) return false;
        string label = (font.name + " " + font.faceInfo.familyName).ToLowerInvariant();
        return label.Contains("retro") || label.Contains("dos") || label.Contains("consolefont") || label.Contains("unifont");
    }
    private static bool Complete(TMP_FontAsset font)
    {
        if (!font) return false;
        uint[] missing;
        // Never grow a shared game font while merely inspecting it.
        return font.HasCharacters(Core, out missing, false, false);
    }
    private static bool NativeComplete(Font font)
    {
        if (!font) return false;
        foreach (char c in Core) if (!font.HasCharacter(c)) return false;
        return true;
    }
    private static bool IsRetro(Font font)
    {
        return font && String.Equals(font.name, "RetroGamingFIXED", StringComparison.OrdinalIgnoreCase);
    }
    private static Font NativeFor(TMP_FontAsset source)
    {
        // A complete original typeface keeps the original English family/scaling.
        if (source.sourceFontFile && NativeComplete(source.sourceFontFile)) return source.sourceFontFile;
        if (retro && NativeComplete(retro)) return retro;
        foreach (var candidate in Resources.FindObjectsOfTypeAll<Font>())
            if (IsRetro(candidate) && NativeComplete(candidate)) return retro = candidate;
        if (!searchedResources)
        {
            searchedResources = true;
            // There are four native game fonts in resources.assets; no OS font,
            // external file, download, or bundled replacement font is used.
            foreach (var candidate in Resources.LoadAll<Font>(String.Empty))
                if (IsRetro(candidate) && NativeComplete(candidate)) return retro = candidate;
        }
        throw new InvalidOperationException("The game's complete RetroGamingFIXED source font is unavailable");
    }
    private static TMP_FontAsset Choose(TMP_FontAsset source)
    {
        TMP_FontAsset result;
        if (fonts.TryGetValue(source, out result) && result) return result;
        Font native = NativeFor(source);
        int pointSize = Mathf.Clamp((int)source.faceInfo.pointSize, 8, 192);
        int padding = Mathf.Clamp(source.atlasPadding, 1, 16);
        result = TMP_FontAsset.CreateFontAsset(native, pointSize, padding, source.atlasRenderMode, 2048, 2048, AtlasPopulationMode.Dynamic, true);
        if (!result) throw new InvalidOperationException("TMP could not create the private pixel font");
        result.name = "CuRussian-Pixel-" + source.name;
        // Preserve synthetic style behavior without borrowing alternative typefaces
        // that could route bold/italic Cyrillic back to another family.
        result.normalStyle = source.normalStyle; result.boldStyle = source.boldStyle;
        result.italicStyle = source.italicStyle; result.tabSize = source.tabSize;
        string missing;
        result.TryAddCharacters(Core, out missing, true);
        if (!Complete(result)) { UnityEngine.Object.Destroy(result); throw new InvalidOperationException("The private pixel font does not cover Russian, Latin and core punctuation"); }
        privateFonts.Add(result);
        AddAlias(result, 0x2014, '-'); AddAlias(result, 0x2013, '-'); AddAlias(result, 0x2212, '-'); AddAlias(result, 0x00a0, ' ');
        if (source.atlasTexture)
            foreach (var texture in result.atlasTextures) if (texture) texture.filterMode = source.atlasTexture.filterMode;
        UnityEngine.Object.DontDestroyOnLoad(result);
        if (result.material) UnityEngine.Object.DontDestroyOnLoad(result.material);
        foreach (var texture in result.atlasTextures) if (texture) UnityEngine.Object.DontDestroyOnLoad(texture);
        fonts[source] = result;
        logger.LogInfo("Private Russian pixel face: " + source.name + " -> " + native.name + ", sample " + pointSize + ". Dash variants use this face's hyphen glyph.");
        return result;
    }
    private static void AddAlias(TMP_FontAsset font, uint unicode, char donor)
    {
        if (!privateFonts.Contains(font) || font.HasCharacter((int)unicode)) return;
        TMP_Character character;
        if (!font.characterLookupTable.TryGetValue(donor, out character)) return;
        font.characterTable.Add(new TMP_Character(unicode, font, character.glyph));
        font.ReadFontAssetDefinition();
    }
    private static void AddTextGlyphs(TMP_FontAsset font, string text)
    {
        if (!privateFonts.Contains(font) || String.IsNullOrEmpty(text)) return;
        // Adding characters changes only our private atlas. Tags remain untouched:
        // sprite IDs, font tags, bold and italics are still parsed by TMP itself.
        string missing;
        font.TryAddCharacters(text, out missing, true);
        if (String.IsNullOrEmpty(missing)) return;
        foreach (char c in missing)
            if (!Char.IsControl(c) && !Char.IsSurrogate(c) && reportedMissing.Add(c))
                logger.LogWarning("Pixel face has no U+" + ((uint)c).ToString("X4") + "; TMP's normal fallback remains for this rare symbol (e.g. ellipsis/numero). Its meaning is not replaced.");
    }
    private static Material DisplayMaterial(Material original, TMP_FontAsset target)
    {
        var material = new Material(original ? original : target.material);
        material.name = "CuRussian-Pixel-Material";
        material.hideFlags = HideFlags.HideAndDontSave;
        material.SetTexture("_MainTex", target.atlasTexture);
        foreach (string name in new[] { "_TextureWidth", "_TextureHeight", "_GradientScale", "_WeightNormal", "_WeightBold" })
            if (material.HasProperty(name) && target.material.HasProperty(name)) material.SetFloat(name, target.material.GetFloat(name));
        return material;
    }
    private static void BeforeLayout(TMP_Text __instance)
    {
        if (!__instance || !__instance.font || !applying.Add(__instance)) return;
        try
        {
            TMP_FontAsset source = __instance.font;
            Binding binding;
            if (!bindings.TryGetValue(__instance, out binding))
            {
                // Pixel-font roles are routed even for ".", "-" or a number-only
                // component, so a separate punctuation run cannot retain another face.
                if (!PixelFamily(source) && (!Cyrillic(__instance.text) || Complete(source))) return;
                binding = new Binding(); bindings[__instance] = binding;
            }
            if (binding.Font != source)
            {
                TMP_FontAsset target = Choose(source);
                Material display = DisplayMaterial(__instance.fontSharedMaterial, target);
                Material previous = binding.Material;
                __instance.font = target;
                __instance.fontSharedMaterial = display;
                binding.Font = target; binding.Material = display;
                __instance.UpdateMeshPadding();
                // Use TMP's own world material pass BEFORE geometry, not a renderer
                // overwrite after its material/submesh indices have already been built.
                if (__instance is TextMeshPro && UpdateWorldMaterial != null) UpdateWorldMaterial.Invoke(__instance, null);
                if (previous) UnityEngine.Object.Destroy(previous);
            }
            else if (__instance.fontSharedMaterial != binding.Material)
            {
                Material previous = binding.Material;
                binding.Material = DisplayMaterial(__instance.fontSharedMaterial, binding.Font);
                __instance.fontSharedMaterial = binding.Material;
                __instance.UpdateMeshPadding();
                if (__instance is TextMeshPro && UpdateWorldMaterial != null) UpdateWorldMaterial.Invoke(__instance, null);
                if (previous) UnityEngine.Object.Destroy(previous);
            }
            AddTextGlyphs(binding.Font, __instance.text);
        }
        catch (Exception error)
        {
            if (!reportedFailure) { reportedFailure = true; logger.LogWarning("Russian pixel font preparation skipped; original text retained: " + error.Message); }
        }
        finally { applying.Remove(__instance); }
    }
    private static void AfterDestroy(TMP_Text __instance)
    {
        if (ReferenceEquals(__instance, null)) return;
        Binding binding;
        if (bindings.TryGetValue(__instance, out binding)) { if (binding.Material) UnityEngine.Object.Destroy(binding.Material); bindings.Remove(__instance); }
        applying.Remove(__instance);
    }
}
