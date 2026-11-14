using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

// Together-only tooltip measurements and the three named lobby action-row controls.
// Ordinary game labels, world speech and original host/start/ready branches are untouched.
internal static class TogetherTextWrap
{
    static FieldInfo pending, title, description, tooltipScale, uiScale;
    static MethodInfo originalCursor;
    static bool enabled;
    static bool lobbyEnabled;
    static FieldInfo lobbyScale;
    [ThreadStatic] static LobbyState lobby;
    sealed class LobbyState { internal LobbyState Previous; internal float Width, Height; }
    [ThreadStatic] static State current;
    sealed class State
    {
        internal State Previous;
        internal GUIStyle Label;
        internal int FontSize;
        internal bool Rich, Wrap, Active;
        internal Color Color;
        internal float MaximumWidth;
        internal int TooltipFontSize;
        internal Vector2 Origin;
    }
    internal static void Enable(Harmony harmony, ManualLogSource log)
    {
        EnableLobby(harmony, log);
        if (enabled) return;
        var type = AccessTools.TypeByName("Together.UIBullshit");
        if (type == null) return;
        var method = type.GetMethod("__GUI__DoTooltipBullshit", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
        pending = AccessTools.Field(type, "_gui_tooltip"); title = AccessTools.Field(type, "_gui_tooltip_name");
        description = AccessTools.Field(type, "_gui_tooltip_desc"); tooltipScale = AccessTools.Field(type, "TOOLTIP_SCALE");
        uiScale = AccessTools.Field(type, "uiScale");
        originalCursor = type.GetMethod("GetCursorPosInGUI", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
        if (method == null || method.ReturnType != typeof(void) || pending == null || !pending.IsStatic || pending.FieldType != typeof(bool)
            || title == null || !title.IsStatic || title.FieldType != typeof(string) || description == null || !description.IsStatic || description.FieldType != typeof(string)
            || tooltipScale == null || !tooltipScale.IsStatic || tooltipScale.FieldType != typeof(float) || originalCursor == null || originalCursor.ReturnType != typeof(Vector2))
        { log.LogWarning("Together tooltip wrapping: unsupported layout; no hooks installed."); return; }
        try
        {
            harmony.Patch(method, prefix: Hook("Before"), transpiler: Hook("WrapMeasurements"), finalizer: Hook("After"));
            enabled = true; log.LogInfo("Together tooltip wrapping: measured multiline titles/descriptions and viewport-aware placement enabled.");
        }
        catch (Exception e)
        {
            harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
            log.LogWarning("Together tooltip wrapping unavailable: " + e.Message);
        }
    }
    static void EnableLobby(Harmony harmony, ManualLogSource log)
    {
        if (lobbyEnabled) return;
        var type = AccessTools.TypeByName("Together.UIMainMenu");
        var method = type == null ? null : type.GetMethod("_GUI__DirectConnect", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Rect) }, null);
        if (method == null || method.ReturnType != typeof(void)) return;
        var uiType = AccessTools.TypeByName("Together.UIBullshit");
        lobbyScale = uiType == null ? null : AccessTools.Field(uiType, "uiScale");
        try
        {
            harmony.Patch(method, prefix: Hook("BeforeLobby"), transpiler: Hook("LobbyButtons"), finalizer: Hook("AfterLobby"));
            lobbyEnabled = true; log.LogInfo("Together lobby action row: full captions wrap inside existing permitted controls; no start/ready permissions changed.");
        }
        catch (Exception e) { harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id); log.LogWarning("Together lobby action row fitting unavailable: " + e.GetType().Name + ": " + e.Message); }
    }
    static void BeforeLobby(Rect __0, out LobbyState __state)
    {
        float scale = 1;
        try { if (lobbyScale != null && lobbyScale.IsStatic && lobbyScale.FieldType == typeof(float)) scale = (float)lobbyScale.GetValue(null); } catch { }
        if (Single.IsNaN(scale) || Single.IsInfinity(scale) || scale <= 0) scale = 1;
        __state = new LobbyState { Previous = lobby, Width = __0.width, Height = 50 * scale }; lobby = __state;
    }
    static Exception AfterLobby(Exception __exception, LobbyState __state) { if (__state != null) lobby = __state.Previous; return __exception; }
    internal static float LobbySlot(float panelWidth) { return Math.Max(1, (panelWidth - 24) / 3); }
    static bool LobbyButton(string text, GUILayoutOption[] options)
    {
        if (lobby == null) return GUILayout.Button(text, options);
        GUIStyle style; float width = LobbySlot(lobby.Width), height = Math.Max(1, lobby.Height);
        try { style = TogetherUiFix.FitButtonStyle(GUI.skin.button, text, width, height); }
        catch { return GUILayout.Button(text, options); }
        var layout = (options ?? new GUILayoutOption[0]).Concat(new[] { GUILayout.Width(width), GUILayout.Height(height) }).ToArray();
        // Exactly one original GUILayout control, original bool result and disabled state.
        // Only calls already reached by Together's host/lobby/run-page branches are replaced.
        return GUILayout.Button(text, style, layout);
    }
    static IEnumerable<CodeInstruction> LobbyButtons(IEnumerable<CodeInstruction> source)
    {
        var code = source.Select(i => new CodeInstruction(i)).ToList();
        var keys = new HashSet<string>(new[] { "mmct_serveroptions", "mmct_copylobbycode", "mmct_gotorunsettings" }, StringComparer.Ordinal);
        var matches = new Dictionary<string, int>();
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Ldstr || !(code[i].operand is string) || !keys.Contains((string)code[i].operand)) continue;
            string key = (string)code[i].operand;
            for (int j = i + 1; j < Math.Min(code.Count, i + 14); j++)
            {
                var method = code[j].operand as MethodInfo;
                if (method != null && method.DeclaringType == typeof(GUILayout) && method.Name == "Button" && method.ReturnType == typeof(bool) && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(string), typeof(GUILayoutOption[]) }))
                { if (matches.ContainsKey(key)) throw new InvalidOperationException("Duplicate Together lobby caption call."); matches.Add(key, j); break; }
            }
        }
        if (matches.Count != 3) throw new InvalidOperationException("Unsupported Together lobby action row.");
        MethodInfo helper = typeof(TogetherTextWrap).GetMethod("LobbyButton", BindingFlags.Static | BindingFlags.NonPublic);
        foreach (int index in matches.Values) { code[index].opcode = OpCodes.Call; code[index].operand = helper; }
        return code;
    }
    static HarmonyMethod Hook(string name) { return new HarmonyMethod(typeof(TogetherTextWrap), name); }
    static void Before(out State __state)
    {
        __state = new State { Previous = current }; current = __state;
        if (!(bool)pending.GetValue(null)) return;
        try
        {
            var source = GUI.skin.label; __state.Label = source; __state.FontSize = source.fontSize;
            __state.Rich = source.richText; __state.Wrap = source.wordWrap; __state.Color = GUI.color;
            var style = new GUIStyle(source) { wordWrap = true, richText = true };
            style.fontSize = (int)(source.fontSize * (float)tooltipScale.GetValue(null));
            float scale = uiScale != null && uiScale.IsStatic && uiScale.FieldType == typeof(float) ? (float)uiScale.GetValue(null) : 1;
            if (Single.IsNaN(scale) || Single.IsInfinity(scale) || scale <= 0) scale = 1;
            float available = Math.Max(1, Screen.width - 16);
            float limit = Math.Min(available, Math.Min(640 * scale, Screen.width * .68f));
            __state.MaximumWidth = Math.Max(1, limit);
            var name = new GUIContent(title.GetValue(null) as string ?? "");
            var desc = new GUIContent(description.GetValue(null) as string ?? "");
            var a = Measure(style, name); var b = Measure(style, desc);
            // Wider wrapping is preferable to shrinking text on very short displays.
            if (a.y + b.y > Screen.height - 16) { __state.MaximumWidth = available; a = Measure(style, name); b = Measure(style, desc); }
            if (a.y + b.y > Screen.height - 16)
            {
                int maximum = style.fontSize > 0 ? style.fontSize : style.font != null ? Math.Max(6, style.font.fontSize) : 16;
                int minimum = Math.Min(maximum, 6);
                for (int size = maximum - 1; size >= minimum && a.y + b.y > Screen.height - 16; size--)
                { style.fontSize = size; a = Measure(style, name); b = Measure(style, desc); }
            }
            __state.TooltipFontSize = style.fontSize;
            var raw = (Vector2)originalCursor.Invoke(null, null);
            __state.Origin = Place(raw.x + 10, raw.y + 10, Math.Max(a.x, b.x) + Math.Max(a.y, b.y) * .1f, a.y + b.y, Screen.width, Screen.height);
            __state.Active = true;
        }
        catch { __state.Active = false; } // Keep the original renderer when measurement is unavailable.
    }
    static Exception After(Exception __exception, State __state)
    {
        if (__state == null) return __exception;
        try
        {
            if (__state.Label != null)
            {
                __state.Label.fontSize = __state.FontSize; __state.Label.richText = __state.Rich;
                __state.Label.wordWrap = __state.Wrap; GUI.color = __state.Color;
            }
        }
        finally { current = __state.Previous; }
        return __exception;
    }
    internal static Vector2 Place(float x, float y, float width, float height, float viewportWidth, float viewportHeight)
    {
        float margin = Math.Min(8, Math.Max(0, Math.Min(viewportWidth, viewportHeight) * .05f));
        return new Vector2(Math.Max(margin, Math.Min(x, viewportWidth - margin - width)), Math.Max(margin, Math.Min(y, viewportHeight - margin - height)));
    }
    static Vector2 Measure(GUIStyle style, GUIContent content)
    {
        if (String.IsNullOrEmpty(content.text)) return Vector2.zero;
        Vector2 size = style.CalcSize(content);
        float width = Math.Max(1, Math.Min(size.x, current.MaximumWidth));
        return new Vector2(width, style.CalcHeight(content, width));
    }
    static Vector2 WrappedSize(GUIStyle style, GUIContent content)
    {
        return current != null && current.Active ? Measure(style, content) : style.CalcSize(content);
    }
    static void SetNameWrap(GUIStyle style, bool value) { style.wordWrap = current != null && current.Active || value; }
    static void SetTooltipSize(GUIStyle style, int value) { style.fontSize = current != null && current.Active ? current.TooltipFontSize : value; }
    static Vector2 Cursor()
    {
        return current != null && current.Active ? new Vector2(current.Origin.x - 10, current.Origin.y - 10) : (Vector2)originalCursor.Invoke(null, null);
    }
    static IEnumerable<CodeInstruction> WrapMeasurements(IEnumerable<CodeInstruction> source)
    {
        var code = new List<CodeInstruction>(source); int measurements = 0, cursor = 0, wrap = 0, font = 0;
        foreach (var i in code)
        {
            var m = i.operand as MethodInfo;
            if (m != null && m.DeclaringType == typeof(GUIStyle) && m.Name == "CalcSize") measurements++;
            if (m != null && m.Equals(originalCursor)) cursor++;
        }
        for (int i = 1; i < code.Count; i++)
        {
            var m = code[i].operand as MethodInfo;
            if (m != null && m.DeclaringType == typeof(GUIStyle) && m.Name == "set_wordWrap" && code[i - 1].opcode == OpCodes.Ldc_I4_0) wrap++;
            if (m != null && m.DeclaringType == typeof(GUIStyle) && m.Name == "set_fontSize" && code[i - 1].opcode == OpCodes.Conv_I4) font++;
        }
        if (measurements != 2 || cursor != 1 || wrap != 1 || font != 1) throw new InvalidOperationException("Unsupported Together tooltip measurement layout.");
        for (int i = 0; i < code.Count; i++)
        {
            var m = code[i].operand as MethodInfo; string helper = null;
            if (m != null && m.DeclaringType == typeof(GUIStyle) && m.Name == "CalcSize") helper = "WrappedSize";
            else if (m != null && m.Equals(originalCursor)) helper = "Cursor";
            else if (i > 0 && m != null && m.DeclaringType == typeof(GUIStyle) && m.Name == "set_wordWrap" && code[i - 1].opcode == OpCodes.Ldc_I4_0) helper = "SetNameWrap";
            else if (i > 0 && m != null && m.DeclaringType == typeof(GUIStyle) && m.Name == "set_fontSize" && code[i - 1].opcode == OpCodes.Conv_I4) helper = "SetTooltipSize";
            if (helper != null) { code[i].opcode = OpCodes.Call; code[i].operand = typeof(TogetherTextWrap).GetMethod(helper, BindingFlags.Static | BindingFlags.NonPublic); }
        }
        return code;
    }
}
