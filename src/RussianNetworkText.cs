using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

// Presentation only: Together 5.5 sends an already selected Talker string, not its locale key.
// Never re-roll a line or touch player chat, reader/writer positions, timing, entity IDs or rules.
public static class RussianNetworkText
{
    static Language english, russian, cachedTarget;
    static PhraseIndex englishIndex, russianIndex, cachedIndex;
    static bool enabled;
    [ThreadStatic] static int generatedDepth;
    [ThreadStatic] static int receivedDepth;
    [ThreadStatic] static object generatedTalker;
    static readonly string[] LimbKeys = { "HeadShort", "UpTorsoShort", "DownTorsoShort", "UpArmFShort", "DownArmFShort", "HandFShort", "UpArmBShort", "DownArmBShort", "HandBShort", "ThighFShort", "CrusFShort", "FootFShort", "ThighBShort", "CrusBShort", "FootBShort" };
    static readonly Regex Wrapper = new Regex(@"\A<(b|i|color|size)(=[^<>\r\n]{1,80})?>", RegexOptions.CultureInvariant);
    sealed class Scope { public int Depth; public object Talker; }
    internal sealed class PhraseIndex
    {
        readonly Dictionary<int, Dictionary<string, string>> maps = new Dictionary<int, Dictionary<string, string>>();
        readonly Dictionary<int, HashSet<string>> ambiguous = new Dictionary<int, HashSet<string>>();
        internal void Add(int species, string source, string target)
        {
            if (String.IsNullOrEmpty(source) || String.IsNullOrEmpty(target) || source.Length > 4096 || target.Length > 4096) return;
            Dictionary<string, string> map; HashSet<string> conflicts;
            if (!maps.TryGetValue(species, out map)) { maps[species] = map = new Dictionary<string, string>(StringComparer.Ordinal); ambiguous[species] = new HashSet<string>(StringComparer.Ordinal); }
            conflicts = ambiguous[species]; if (conflicts.Contains(source)) return;
            string old;
            if (map.TryGetValue(source, out old) && old != target) { map.Remove(source); conflicts.Add(source); }
            else map[source] = target;
        }
        internal string Translate(string text, int species)
        {
            if (String.IsNullOrEmpty(text) || text.Length > 4096) return text;
            Dictionary<string, string> map;
            // For a known species, conflicting lines must not fall back to another species' meaning.
            if (maps.TryGetValue(species, out map)) return Wrapped(text, map, 0);
            return maps.TryGetValue(-1, out map) ? Wrapped(text, map, 0) : text;
        }
        static string Wrapped(string text, Dictionary<string, string> map, int depth)
        {
            string result; if (map.TryGetValue(text, out result)) return result;
            if (depth >= 8) return text;
            Match m = Wrapper.Match(text); if (!m.Success) return text;
            string tag = m.Groups[1].Value, end = "</" + tag + ">";
            if (!text.EndsWith(end, StringComparison.Ordinal) || text.Length < m.Length + end.Length) return text;
            if ((tag == "b" || tag == "i") && m.Groups[2].Success) return text;
            if ((tag == "color" || tag == "size") && !m.Groups[2].Success) return text;
            string inner = text.Substring(m.Length, text.Length - m.Length - end.Length);
            string translated = Wrapped(inner, map, depth + 1);
            return translated == inner ? text : m.Value + translated + end;
        }
    }
    public static void Enable(Harmony harmony, ManualLogSource log, Language russianLanguage)
    {
        if (enabled) return;
        try
        {
            string path = Path.Combine(Application.dataPath, "Lang", "EN.json");
            var en = JsonConvert.DeserializeObject<Language>(File.ReadAllText(path));
            if (en == null || en.version != "1.0.2" || russianLanguage == null || russianLanguage.version != "1.0.2" || en.character == null || russianLanguage.character == null) throw new InvalidDataException("Expected paired game 1.0.2 locales");
            var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "CasualtiesMP");
            if (asm == null) { log.LogInfo("Network speech localization: Together is not loaded; no network hooks installed."); return; }
            Type talkPatch = asm.GetType("Together.Talker_Talk_MultiplayerPatch", false), receiverType = asm.GetType("Together.YOU_SHOULD_KILL_YOURSELF_NOOOW", false), serverType = asm.GetType("Together.ServerMain", false);
            MethodInfo generated = Find(talkPatch, "Postfix", "Talker");
            MethodInfo receiver = Find(receiverType, "Client_ForceTalkerSay", "Together.knetid", "LiteNetLib.Utils.NetDataReader");
            MethodInfo display = Find(talkPatch, "ForceNoSpeechImpairment", "Talker", "System.String", "System.Boolean");
            MethodInfo send = Find(serverType, "TalkerSayAnnounce", "Together.knetid&", "System.Byte&", "System.String&", "System.Collections.Generic.IReadOnlyList`1[Together.knetid]");
            if (generated == null || receiver == null || display == null || send == null) { log.LogWarning("Network speech localization: unsupported Together method signatures; no hooks installed."); return; }
            english = en; russian = russianLanguage;
            englishIndex = BuildIndex(english, english, russian); russianIndex = BuildIndex(russian, english, russian);
            // Finalizers restore thread-local scopes even if Together or another patch throws.
            harmony.Patch(generated, prefix: Hook("GeneratedBegin"), finalizer: Hook("GeneratedEnd"));
            harmony.Patch(receiver, prefix: Hook("ReceivedBegin"), finalizer: Hook("ReceivedEnd"));
            harmony.Patch(display, prefix: Hook("DisplayLine"));
            harmony.Patch(send, prefix: Hook("CanonicalLine"));
            enabled = true;
            log.LogInfo("Network speech localization enabled for Together " + asm.GetName().Version + ": known generated lines only; typed chat unchanged.");
        }
        catch (Exception e) { log.LogWarning("Network speech localization was not enabled: " + e.GetType().Name + ": " + e.Message); }
    }
    static HarmonyMethod Hook(string name) { var method = new HarmonyMethod(typeof(RussianNetworkText), name); method.priority = Priority.First; return method; }
    static MethodInfo Find(Type type, string name, params string[] signature)
    {
        if (type == null) return null;
        return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).SingleOrDefault(m => m.Name == name && m.GetParameters().Select(p => p.ParameterType.ToString()).SequenceEqual(signature));
    }
    static void GeneratedBegin(object __0, out Scope __state) { __state = new Scope { Depth = generatedDepth, Talker = generatedTalker }; generatedDepth++; generatedTalker = __0; }
    static Exception GeneratedEnd(Exception __exception, Scope __state) { if (__state != null) { generatedDepth = __state.Depth; generatedTalker = __state.Talker; } return __exception; }
    static void ReceivedBegin(out int __state) { __state = receivedDepth; receivedDepth++; }
    static Exception ReceivedEnd(Exception __exception, int __state) { receivedDepth = __state; return __exception; }
    static void CanonicalLine(ref string __2)
    {
        if (generatedDepth == 0 || englishIndex == null) return;
        __2 = englishIndex.Translate(__2, CharacterType(generatedTalker));
    }
    static void DisplayLine(object __0, ref string __1)
    {
        // Chat.Client_ChatMessageReceive calls this same helper, but does NOT enter receivedDepth.
        if (receivedDepth == 0) return;
        __1 = Localize(__1, CharacterType(__0), Locale.currentLang);
    }
    internal static string Localize(string text, int species, Language target)
    {
        if (target == null || englishIndex == null) return text;
        if (System.Object.ReferenceEquals(target, english)) return englishIndex.Translate(text, species);
        if (System.Object.ReferenceEquals(target, russian)) return russianIndex.Translate(text, species);
        if (!System.Object.ReferenceEquals(cachedTarget, target)) { cachedIndex = BuildIndex(target, english, russian); cachedTarget = target; }
        return cachedIndex.Translate(text, species);
    }
    static int CharacterType(object talker)
    {
        if (talker == null) return -1;
        try { object body = AccessTools.Field(talker.GetType(), "body").GetValue(talker); return body == null ? -1 : (int)AccessTools.Field(body.GetType(), "charType").GetValue(body); }
        catch { return -1; }
    }
    internal static PhraseIndex BuildIndex(Language target, params Language[] sources)
    {
        var index = new PhraseIndex(); if (target == null || target.character == null) return index;
        foreach (Language source in sources.Where(l => l != null && l.character != null))
        {
            int count = Math.Min(source.character.Length, target.character.Length);
            for (int species = 0; species < count; species++)
            {
                var from = source.character[species]; var to = target.character[species]; if (from == null || to == null) continue;
                foreach (var category in from)
                {
                    List<string> translated;
                    // Unequal list sizes indicate drift; positional pairing cannot be trusted at all.
                    if (category.Value == null || !to.TryGetValue(category.Key, out translated) || translated == null || category.Value.Count != translated.Count) continue;
                    for (int line = 0; line < category.Value.Count; line++) AddLine(index, species, category.Value[line], translated[line], source, target);
                }
                // Shared devices/objects also use Talker, e.g. the recharging station's cheer.
                if (source.other != null && target.other != null) foreach (var pair in source.other) { string translated; if (target.other.TryGetValue(pair.Key, out translated)) AddLine(index, species, pair.Value, translated, source, target); }
            }
        }
        return index;
    }
    static void AddLine(PhraseIndex index, int species, string from, string to, Language source, Language target)
    {
        if (String.IsNullOrEmpty(from) || String.IsNullOrEmpty(to)) return;
        if (from.Contains("<limb>") || to.Contains("<limb>"))
        {
            if (!from.Contains("<limb>") || !to.Contains("<limb>") || source.other == null || target.other == null) return;
            foreach (string key in LimbKeys)
            {
                string a, b;
                if (source.other.TryGetValue(key, out a) && target.other.TryGetValue(key, out b) && !String.IsNullOrEmpty(a) && !String.IsNullOrEmpty(b)) Add(index, species, from.Replace("<limb>", a.ToLower(CultureInfo.CurrentCulture)), to.Replace("<limb>", b.ToLower(CultureInfo.CurrentCulture)));
            }
        }
        else Add(index, species, from, to);
    }
    static void Add(PhraseIndex index, int species, string from, string to) { index.Add(species, from, to); index.Add(-1, from, to); }
}
