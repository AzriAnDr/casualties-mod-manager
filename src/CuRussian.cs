using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;

[BepInPlugin("local.casualties.russian", "Русский перевод игры и Together", "0.2.7")]
[BepInDependency("CasualtiesMP", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class CuRussian : BaseUnityPlugin
{
    private const string SupportedGameVersion = "1.0.2";
    private Harmony harmony;
    private static CuRussian instance;
    private Language russian;
    private bool applied;
    private void Awake()
    {
        instance = this;
        if (Application.version != SupportedGameVersion) { Logger.LogWarning("This Russian package supports game " + SupportedGameVersion + "; no changes applied to " + Application.version); return; }
        try
        {
            string dir = Path.GetDirectoryName(typeof(CuRussian).Assembly.Location);
            russian = JsonConvert.DeserializeObject<Language>(File.ReadAllText(Path.Combine(dir, "ru-RU.json")));
            if (russian == null || russian.version != SupportedGameVersion || russian.main == null || russian.main.Count < 100 || russian.other == null) throw new InvalidDataException("Invalid Russian locale");
            var multiplayer = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(Path.Combine(dir, "together-ru.json")));
            foreach (var pair in multiplayer)
            {
                string key = pair.Key.StartsWith("casualtiestogether_") ? pair.Key : "casualtiestogether_" + pair.Key;
                russian.other[key] = pair.Value;
                string shortKey = key.Substring("casualtiestogether_".Length);
                if (shortKey.StartsWith("gameset_")) russian.other["gameset" + shortKey.Substring("gameset_".Length)] = pair.Value;
            }
            harmony = new Harmony("local.casualties.russian");
            var patch = new HarmonyMethod(typeof(CuRussian), "Apply"); patch.priority = Priority.Last;
            harmony.Patch(AccessTools.Method(typeof(Locale), "LoadLanguage"), postfix: patch);
            var tooltipType = AccessTools.TypeByName("Together.UIBullshit");
            var tooltip = tooltipType == null ? null : AccessTools.Method(tooltipType, "_GUI_SetTooltip");
            if (tooltip != null) harmony.Patch(tooltip, prefix: new HarmonyMethod(typeof(CuRussian), "TranslateRuleTooltip"));
            if (Config.Bind("Display", "UnifiedRussianFont", true, "Использовать один шрифт для кириллицы и пунктуации, чтобы знаки не смещались. Требуется перезапуск.").Value)
                try { RussianFontFix.Enable(harmony, Logger); } catch (Exception fontError) { Logger.LogWarning("Font compatibility hooks unavailable: " + fontError.Message); }
            if (Config.Bind("Together", "LocalizeGeneratedSpeech", true, "Показывать игровые реплики Together на языке получателя; ручной чат не переводится.").Value)
                try { RussianNetworkText.Enable(harmony, Logger, russian); } catch (Exception networkError) { Logger.LogWarning("Together speech localization unavailable: " + networkError.Message); }
            Apply();
            Logger.LogInfo("Russian enabled: game 1.0.2, Together " + multiplayer.Count + " strings. No saved language preference changed.");
        }
        catch (Exception e) { Logger.LogError("Russian locale was not applied: " + e); }
    }
    private static void TranslateRuleTooltip(ref string __0)
    {
        string translated;
        if (instance != null && instance.russian != null && !string.IsNullOrEmpty(__0)
            && instance.russian.other.TryGetValue("casualtiestogether_rule_name_" + __0, out translated)) __0 = translated;
    }
    private static void Apply()
    {
        if (instance == null || instance.russian == null) return;
        // Keep foreign extension entries; this is shared with Together and other UI mods.
        if (Locale.currentLang != null && Locale.currentLang != instance.russian)
            foreach (var p in Locale.currentLang.other)
                if (!instance.russian.other.ContainsKey(p.Key)) instance.russian.other[p.Key] = p.Value;
        Locale.currentLang = instance.russian;
        instance.applied = true;
    }
    private void OnDestroy()
    {
        if (harmony != null) harmony.UnpatchSelf();
        if (applied && Locale.currentLang == russian) Locale.currentLang = null;
        instance = null;
    }
}
