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
    private sealed class Binding { internal TMP_FontAsset Font, OriginalFont; internal Material Material, OriginalMaterial; }

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
    private const int SampleSize = 64;
    private const int AtlasSize = 2048;
    private static TMP_FontAsset Choose(TMP_FontAsset source)
    {
        if (privateFonts.Contains(source) || OriginalCyrillic.IsGenerated(source)) return source;
        TMP_FontAsset result;
        if (fonts.TryGetValue(source, out result) && result) return result;
        if (OriginalCyrillic.TryCreate(source, logger, out result)) { fonts[source] = result; return result; }
        Font native = NativeFor(source);
        // Bitmap shader variants may be stripped from this player build. A RASTER
        // font would make TMP's factory construct Material(null). Use one verified
        // SDF pipeline instead of inheriting the source's bitmap render mode.
        var shaderProperty = AccessTools.Property(typeof(ShaderUtilities), "ShaderRef_MobileSDF");
        var shaderGetter = shaderProperty == null ? null : shaderProperty.GetGetMethod(true);
        Shader sdf = shaderGetter == null ? Shader.Find("TextMeshPro/Mobile/Distance Field") : shaderGetter.Invoke(null, null) as Shader;
        if (!sdf) throw new InvalidOperationException("TMP Mobile SDF shader is unavailable; no font or material was replaced");
        result = null;
        try
        {
            result = TMP_FontAsset.CreateFontAsset(native, SampleSize, 8, UnityEngine.TextCore.LowLevel.GlyphRenderMode.SDFAA, AtlasSize, AtlasSize, AtlasPopulationMode.Dynamic, false);
            if (!result || !result.material || !result.material.shader) throw new InvalidOperationException("TMP could not create a valid SDF font/material");
            result.name = "CuRussian-Pixel-" + source.name;
            result.normalStyle = source.normalStyle; result.boldStyle = source.boldStyle;
            result.italicStyle = source.italicStyle; result.tabSize = source.tabSize;
            if (result.material.HasProperty("_WeightNormal")) result.material.SetFloat("_WeightNormal", result.normalStyle);
            if (result.material.HasProperty("_WeightBold")) result.material.SetFloat("_WeightBold", result.boldStyle);
            string ignored;
            result.TryAddCharacters(Core, out ignored, true);
            // TryAddCharacters' out-string is not a reliable missing-glyph list:
            // this TMP version returns the whole input when nothing needed adding.
            // Validate the actual lookup table, glyph atlas and material instead.
            ValidateAtlas(result);
            privateFonts.Add(result);
            AddAlias(result, 0x2014, '-'); AddAlias(result, 0x2013, '-'); AddAlias(result, 0x2212, '-'); AddAlias(result, 0x00a0, ' ');
            ValidateAtlas(result);
            foreach (var texture in result.atlasTextures) if (texture) texture.filterMode = FilterMode.Bilinear;
            UnityEngine.Object.DontDestroyOnLoad(result);
            UnityEngine.Object.DontDestroyOnLoad(result.material);
            foreach (var texture in result.atlasTextures) if (texture) UnityEngine.Object.DontDestroyOnLoad(texture);
            fonts[source] = result;
            logger.LogInfo("Private Russian pixel face ready: " + source.name + " -> " + native.name + "; core " + Core.Length + "/" + Core.Length + ", SDFAA sample64, single2048 atlas, shader " + result.material.shader.name + ".");
            return result;
        }
        catch
        {
            if (result)
            {
                privateFonts.Remove(result);
                if (result.material) UnityEngine.Object.Destroy(result.material);
                if (result.atlasTextures != null) foreach (var texture in result.atlasTextures) if (texture) UnityEngine.Object.Destroy(texture);
                UnityEngine.Object.Destroy(result);
            }
            throw;
        }
    }
    private static void ValidateAtlas(TMP_FontAsset font)
    {
        if (!font || !font.material || !font.material.shader || !font.material.HasProperty("_GradientScale")) throw new InvalidOperationException("Private font has no compatible SDF material");
        if (font.atlasTextures == null || font.atlasTextures.Length != 1 || !font.atlasTexture || font.atlasTexture.width != AtlasSize || font.atlasTexture.height != AtlasSize) throw new InvalidOperationException("Private font does not have the expected single2048 atlas");
        if (font.material.GetTexture("_MainTex") != font.atlasTexture) throw new InvalidOperationException("Private SDF material samples a different atlas");
        foreach (char c in Core)
        {
            TMP_Character character;
            if (!font.characterLookupTable.TryGetValue(c, out character) || character == null || character.glyph == null || character.glyph.atlasIndex != 0) throw new InvalidOperationException("Private core glyph is missing or uses another atlas: U+" + ((uint)c).ToString("X4"));
            var rect = character.glyph.glyphRect;
            if (rect.x < 0 || rect.y < 0 || rect.x + rect.width > AtlasSize || rect.y + rect.height > AtlasSize) throw new InvalidOperationException("Private glyph UV rectangle exceeds its atlas: U+" + ((uint)c).ToString("X4"));
        }
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
        uint[] absent;
        if (font.HasCharacters(text, out absent, false, false)) return;
        var needed = new System.Text.StringBuilder();
        if (absent != null) foreach (uint unicode in absent)
            if (unicode <= 0x10ffff && !(unicode >= 0xd800 && unicode <= 0xdfff)) needed.Append(Char.ConvertFromUtf32((int)unicode));
        if (needed.Length > 0)
        {
            string ignored;
            font.TryAddCharacters(needed.ToString(), out ignored, true);
        }
        // Only a fresh lookup inspection is diagnostic evidence. Never report the
        // TryAddCharacters out-string, which can contain every already-present letter.
        if (font.HasCharacters(text, out absent, false, false) || absent == null) return;
        foreach (uint unicode in absent)
            if (unicode > 31 && !(unicode >= 0xd800 && unicode <= 0xdfff) && reportedMissing.Add(unicode))
                logger.LogWarning("Private pixel lookup has no U+" + unicode.ToString("X4") + "; TMP normal fallback remains for this symbol. Core glyphs were verified independently.");
    }
    private static Material DisplayMaterial(Material original, TMP_FontAsset target)
    {
        if (OriginalCyrillic.IsGenerated(target)) return OriginalCyrillic.DisplayMaterial(original, target);
        ValidateAtlas(target);
        // The atlas is SDFAA. Copying a source bitmap shader with this texture
        // produces incorrect sampling even when _MainTex points to the right atlas.
        var material = new Material(target.material);
        material.name = "CuRussian-Pixel-SDF-Material";
        material.hideFlags = HideFlags.HideAndDontSave;
        if (original)
        {
            if (original.HasProperty("_FaceColor") && material.HasProperty("_FaceColor")) material.SetColor("_FaceColor", original.GetColor("_FaceColor"));
            else if (original.HasProperty("_Color") && material.HasProperty("_FaceColor")) material.SetColor("_FaceColor", original.GetColor("_Color"));
            // These rendering/masking properties do not describe glyph atlas layout.
            foreach (string property in new[] { "_Stencil", "_StencilComp", "_StencilOp", "_StencilReadMask", "_StencilWriteMask", "_ColorMask", "_CullMode", "_ZTestMode" })
                if (original.HasProperty(property) && material.HasProperty(property)) material.SetFloat(property, original.GetFloat(property));
            if (original.HasProperty("_ClipRect") && material.HasProperty("_ClipRect")) material.SetVector("_ClipRect", original.GetVector("_ClipRect"));
            // Preserve SDF-only presets only from another SDF preset, never from a
            // bitmap shader whose similarly named properties can use other units.
            if (original.HasProperty("_GradientScale"))
            {
                foreach (string property in new[] { "_FaceDilate", "_OutlineWidth", "_OutlineSoftness", "_UnderlayOffsetX", "_UnderlayOffsetY", "_UnderlayDilate", "_UnderlaySoftness", "_GlowOffset", "_GlowInner", "_GlowOuter", "_GlowPower" })
                    if (original.HasProperty(property) && material.HasProperty(property)) material.SetFloat(property, original.GetFloat(property));
                foreach (string property in new[] { "_OutlineColor", "_UnderlayColor", "_GlowColor" })
                    if (original.HasProperty(property) && material.HasProperty(property)) material.SetColor(property, original.GetColor(property));
                foreach (string keyword in new[] { "OUTLINE_ON", "UNDERLAY_ON", "UNDERLAY_INNER", "GLOW_ON" })
                    if (original.IsKeywordEnabled(keyword)) material.EnableKeyword(keyword);
            }
        }
        return material;
    }
    private static bool Covers(TMP_FontAsset font, string text)
    {
        if (!font) return false;
        if (String.IsNullOrEmpty(text)) return true;
        uint[] absent;
        return font.HasCharacters(text, out absent, false, false);
    }
    private static string VisibleText(string text, bool richText)
    {
        if (!richText || String.IsNullOrEmpty(text)) return text;
        // Inspect visible glyphs without rewriting TMP's input, sprite IDs or tags.
        var visible = new System.Text.StringBuilder(text.Length);
        bool noParse = false;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '<')
            {
                int end = text.IndexOf('>', i + 1, Math.Min(2048, text.Length - i - 1));
                if (end >= 0)
                {
                    string body = text.Substring(i + 1, end - i - 1).Trim();
                    string tag = body.TrimStart('/');
                    int separator = tag.IndexOfAny(new[] { '=', ' ', '\t' });
                    if (separator >= 0) tag = tag.Substring(0, separator);
                    tag = tag.ToLowerInvariant();
                    if (tag == "noparse") { noParse = !body.StartsWith("/", StringComparison.Ordinal); i = end; continue; }
                    if (!noParse && IsFormattingTag(tag)) { if (tag == "br") visible.Append('\n'); i = end; continue; }
                }
            }
            visible.Append(text[i]);
        }
        return visible.ToString();
    }
    private static bool IsFormattingTag(string name)
    {
        switch (name)
        {
            case "b": case "i": case "u": case "s": case "color": case "alpha": case "size": case "font": case "font-weight":
            case "material": case "sprite": case "style": case "link": case "mark": case "br": case "align": case "voffset":
            case "space": case "cspace": case "mspace": case "line-height": case "line-indent": case "indent": case "width":
            case "margin": case "margin-left": case "margin-right": case "rotate": case "pos": case "gradient": case "nobr":
            case "uppercase": case "lowercase": case "smallcaps": case "sub": case "sup": case "allcaps": return true;
            default: return false;
        }
    }
    private static void ApplyComponentMaterial(TMP_Text text)
    {
        text.UpdateMeshPadding();
        if (text is TextMeshPro && UpdateWorldMaterial != null) UpdateWorldMaterial.Invoke(text, null);
    }
    private static void ReleaseBinding(TMP_Text text, Binding binding, bool restore)
    {
        if (restore && binding.OriginalFont)
        {
            text.font = binding.OriginalFont;
            text.fontSharedMaterial = binding.OriginalMaterial ? binding.OriginalMaterial : binding.OriginalFont.material;
            ApplyComponentMaterial(text);
        }
        bindings.Remove(text);
        if (binding.Material) UnityEngine.Object.Destroy(binding.Material);
    }
    private static void BeforeLayout(TMP_Text __instance)
    {
        if (!__instance || !__instance.font || !applying.Add(__instance)) return;
        try
        {
            TMP_FontAsset current = __instance.font;
            Binding binding;
            bool bound = bindings.TryGetValue(__instance, out binding);
            bool currentlyOwned = bound && current == binding.Font;
            TMP_FontAsset original = currentlyOwned ? binding.OriginalFont : current;
            Material originalMaterial = currentlyOwned ? binding.OriginalMaterial : __instance.fontSharedMaterial;
            string visible = VisibleText(__instance.text, __instance.richText);
            // Original Latin and fully covered Cyrillic keep the actual character's
            // original baked typeface. Only missing Russian glyphs need a coherent
            // whole-run fallback (including its punctuation).
            if (!Cyrillic(visible) || Covers(original, visible))
            {
                if (bound) ReleaseBinding(__instance, binding, currentlyOwned);
                return;
            }
            if (!bound)
            {
                binding = new Binding { OriginalFont = original, OriginalMaterial = originalMaterial };
                bindings[__instance] = binding;
            }
            else if (!currentlyOwned)
            {
                binding.OriginalFont = original; binding.OriginalMaterial = originalMaterial;
            }
            TMP_FontAsset target = Choose(original);
            if (current != target || __instance.fontSharedMaterial != binding.Material)
            {
                Material display = DisplayMaterial(originalMaterial, target);
                Material previous = binding.Material;
                __instance.font = target; __instance.fontSharedMaterial = display;
                binding.Font = target; binding.Material = display;
                ApplyComponentMaterial(__instance);
                if (previous) UnityEngine.Object.Destroy(previous);
            }
            AddTextGlyphs(binding.Font, visible);
        }
        catch (Exception error)
        {
            if (!reportedFailure) { reportedFailure = true; logger.LogWarning("Russian font preparation skipped; original text retained: " + error.Message); }
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
