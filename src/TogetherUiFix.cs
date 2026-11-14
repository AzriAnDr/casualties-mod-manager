using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Together's player action grid is IMGUI (100x45 scaled cells), not TMP.
// Enlarge shared grid dimensions coherently, then fit a private nested button style.
// Never resize global fonts or widen an individual button independently of its hit box/grid.
internal static class TogetherUiFix
{
    [ThreadStatic] private static int actionDepth;
    private static bool enabled;
    private static ManualLogSource logger;
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
            logger = log;
            Type owner = AccessTools.TypeByName("Together.UIInGame"), drawing = AccessTools.TypeByName("Together.UIBullshit");
            var actionTypes = new[] { typeof(Rect).MakeByRefType(), typeof(bool), typeof(string).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(bool), typeof(bool) };
            var drawingTypes = new[] { typeof(Rect).MakeByRefType(), typeof(string).MakeByRefType(), typeof(bool).MakeByRefType(), typeof(GUIStyle) };
            MethodInfo action = Exact(owner, "DoPlayerInteractionMenuButton", actionTypes), button = Exact(drawing, "_GUI_FocusableButton", drawingTypes);
            MethodInfo layout = owner == null ? null : owner.GetMethod("_GUI_PlayerInteractionGui", BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (action == null || button == null || layout == null || layout.ReturnType != typeof(void)) { log.LogInfo("Together action text fitting: compatible Together methods not loaded; no UI hooks installed."); return; }
            harmony.Patch(layout, transpiler: new HarmonyMethod(typeof(TogetherUiFix), "ExpandGrid"));
            harmony.Patch(action, prefix: new HarmonyMethod(typeof(TogetherUiFix), "BeginAction"), finalizer: new HarmonyMethod(typeof(TogetherUiFix), "EndAction"));
            var before = new HarmonyMethod(typeof(TogetherUiFix), "BeforeButton"); before.priority = Priority.Last;
            harmony.Patch(button, prefix: before);
            enabled = true;
            log.LogInfo("Together player action labels: viewport-bounded shared grid and private wrapping/fitting styles enabled; other UI/font sizes unchanged.");
        }
        catch (Exception e) { log.LogWarning("Together action text fitting unavailable: " + e.GetType().Name + ": " + e.Message); }
    }
    private static MethodInfo Exact(Type type, string name, Type[] signature)
    {
        return type == null ? null : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SingleOrDefault(m => m.Name == name && m.ReturnType == typeof(bool) && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(signature));
    }
    private static IEnumerable<CodeInstruction> ExpandGrid(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.Select(i => new CodeInstruction(i)).ToList();
        var widths = Enumerable.Range(0, code.Count).Where(i => DimensionStore(code, i, "buttonxsize", 100f)).ToList();
        var heights = Enumerable.Range(0, code.Count).Where(i => DimensionStore(code, i, "buttonysize", 45f)).ToList();
        if (widths.Count != 1 || heights.Count != 1)
        {
            if (logger != null) logger.LogWarning("Together action grid layout differs from the verified 5.5.0 method; original geometry retained.");
            return code;
        }
        var result = new List<CodeInstruction>();
        for (int i = 0; i < code.Count; i++)
        {
            if (i == widths[0] || i == heights[0])
            {
                var call = new CodeInstruction(OpCodes.Call, typeof(TogetherUiFix).GetMethod(i == widths[0] ? "ExpandedWidth" : "ExpandedHeight", BindingFlags.Static | BindingFlags.NonPublic));
                // Preserve branches/exception boundaries at the original field assignment.
                MoveLabels(code[i], call);
                call.blocks.AddRange(code[i].blocks); code[i].blocks.Clear(); result.Add(call);
            }
            result.Add(code[i]);
        }
        return result;
    }
    private static void MoveLabels(CodeInstruction source, CodeInstruction destination)
    {
        // The installed Harmony exposes List<mscorlib.Label>; access through non-generic IList
        // to avoid requiring its Framework-only Label type in this netstandard plugin build.
        var field = typeof(CodeInstruction).GetField("labels");
        var from = (IList)field.GetValue(source); var to = (IList)field.GetValue(destination);
        foreach (object label in from) to.Add(label);
        from.Clear();
    }
    private static bool DimensionStore(List<CodeInstruction> code, int i, string fieldName, float original)
    {
        var field = code[i].operand as FieldInfo;
        return i >= 3 && code[i].opcode == OpCodes.Stfld && field != null && field.Name == fieldName && field.FieldType == typeof(float) && field.DeclaringType != null && field.DeclaringType.DeclaringType != null && field.DeclaringType.DeclaringType.FullName == "Together.UIInGame"
            && code[i - 1].opcode == OpCodes.Mul && code[i - 3].opcode == OpCodes.Ldc_R4 && code[i - 3].operand is float && (float)code[i - 3].operand == original;
    }
    private static float ExpandedWidth(float original) { return CellSize(original, 1.3f, Screen.width, 3f); }
    private static float ExpandedHeight(float original) { return CellSize(original, 56f / 45f, Screen.height, 2.5f); }
    internal static float CellSize(float original, float expansion, float viewport, float clampMargin)
    {
        // Together clamps menu center between3 cell widths /2.5 cell heights and the opposite
        // screen edge. Respect those same margins, rather than expanding rects after hit testing.
        if (Single.IsNaN(original) || Single.IsInfinity(original) || original <= 0) original = 1;
        if (Single.IsNaN(viewport) || Single.IsInfinity(viewport) || viewport <= 0) viewport = 1;
        float limit = Math.Max(0.1f, (viewport - 8f) / (2f * clampMargin));
        return Math.Max(0.1f, Math.Min(original * expansion, limit));
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
        return minimum; // Clipping is last resort; drawing and hit testing share the same grid rect.
    }
}
