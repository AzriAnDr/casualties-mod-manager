using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Together's player action grid is IMGUI (100x45 scaled cells), not TMP.
// Fit a private style only for the nested button draw; never resize global fonts or hit boxes.
internal static class TogetherUiFix
{
    [ThreadStatic] private static int actionDepth;
    private static bool enabled;
    private const int CacheLimit = 64;
    private sealed class Entry
    {
        internal GUIStyle Source, Fitted;
        internal Font Font;
        internal int FontSize;
        internal bool RichText;
        internal float Width, Height;
    }
    private static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>(StringComparer.Ordinal);

    internal static void Enable(Harmony harmony, ManualLogSource log)
    {
        if (enabled) return;
        try
        {
            Type owner = AccessTools.TypeByName("Together.UIInGame"), drawing = AccessTools.TypeByName("Together.UIBullshit");
            var actionTypes = new[] { typeof(Rect).MakeByRefType(), typeof(bool), typeof(string).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(bool), typeof(bool) };
            var drawingTypes = new[] { typeof(Rect).MakeByRefType(), typeof(string).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(GUIStyle) };
            MethodInfo action = Exact(owner, "DoPlayerInteractionMenuButton", actionTypes), button = Exact(drawing, "_GUI_FocusableButton", drawingTypes);
            if (action == null || button == null) { log.LogInfo("Together action text fitting: compatible Together methods not loaded; no UI hooks installed."); return; }
            harmony.Patch(action, prefix: new HarmonyMethod(typeof(TogetherUiFix), "BeginAction"), finalizer: new HarmonyMethod(typeof(TogetherUiFix), "EndAction"));
            var before = new HarmonyMethod(typeof(TogetherUiFix), "BeforeButton"); before.priority = Priority.Last;
            harmony.Patch(button, prefix: before);
            enabled = true;
            log.LogInfo("Together player action labels: private wrapping/fitting styles enabled; other UI/font sizes unchanged.");
        }
        catch (Exception e) { log.LogWarning("Together action text fitting unavailable: " + e.GetType().Name + ": " + e.Message); }
    }
    private static MethodInfo Exact(Type type, string name, Type[] signature)
    {
        return type == null ? null : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SingleOrDefault(m => m.Name == name && m.ReturnType == typeof(bool) && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(signature));
    }
    private static void BeginAction(out int __state) { __state = actionDepth; actionDepth++; }
    private static Exception EndAction(Exception __exception, int __state) { actionDepth = __state; return __exception; }
    private static void BeforeButton(Rect __0, string __1, ref GUIStyle __3)
    {
        if (actionDepth == 0 || String.IsNullOrEmpty(__1) || __0.width <= 0 || __0.height <= 0) return;
        // A failed measurement must not affect button interaction, disabled state or another UI mod.
        try { __3 = Fit(__3 ?? GUI.skin.button, __1, __0.width, __0.height); } catch { }
    }
    private static GUIStyle Fit(GUIStyle source, string text, float width, float height)
    {
        Entry entry;
        if (cache.TryGetValue(text, out entry) && System.Object.ReferenceEquals(entry.Source, source) && System.Object.ReferenceEquals(entry.Font, source.font) && entry.FontSize == source.fontSize && entry.RichText == source.richText && entry.Width == width && entry.Height == height) return entry.Fitted;
        if (cache.Count >= CacheLimit) cache.Clear();
        var style = new GUIStyle(source);
        style.wordWrap = true; style.clipping = TextClipping.Clip; style.alignment = TextAnchor.MiddleCenter;
        style.fixedWidth = 0; style.fixedHeight = 0; style.contentOffset = Vector2.zero;
        int maximum = source.fontSize > 0 ? source.fontSize : source.font != null && source.font.fontSize > 0 ? source.font.fontSize : 16;
        int minimum = Math.Min(maximum, Math.Max(6, (int)Math.Round(maximum * 0.55)));
        var content = new GUIContent(text);
        var words = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(w => new GUIContent(w)).ToArray();
        // CalcHeight/CalcSize include GUIStyle padding. Leave a small inner safety margin while
        // measuring the exact target rect, so pixel-font rounding cannot cross adjacent cells.
        float safeWidth = Math.Max(1, width - 4), safeHeight = Math.Max(1, height - 4);
        style.fontSize = ChooseSize(maximum, minimum, delegate(int size)
        {
            style.fontSize = size;
            return style.CalcHeight(content, safeWidth) <= safeHeight && words.All(w => style.CalcSize(w).x <= safeWidth);
        });
        cache[text] = new Entry { Source = source, Fitted = style, Font = source.font, FontSize = source.fontSize, RichText = source.richText, Width = width, Height = height };
        return style;
    }
    internal static int ChooseSize(int maximum, int minimum, Func<int, bool> fits)
    {
        for (int size = maximum; size >= minimum; size--) if (fits(size)) return size;
        return minimum; // Clipping is the last resort; the button rect and hit target stay unchanged.
    }
}
