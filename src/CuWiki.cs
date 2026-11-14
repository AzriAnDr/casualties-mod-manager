using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.EventSystems;

[BepInPlugin("local.casualties.wiki", "Полевой справочник · WIP", "0.2.1")]
public sealed class CuWiki : BaseUnityPlugin
{
    internal static CuWiki Instance;
    private Harmony harmony;
    private bool opened, dirty;
    private float nextSave;
    private string query = "", selected = "", notice = "";
    private Vector2 listScroll, detailScroll;
    private EventSystem blockedEvents;
    private string category = "Все";
    private Texture2D background, surface, selectedSurface;
    private GUIStyle headingStyle, bodyStyle, smallStyle, entryStyle, selectedStyle, searchStyle;
    private bool CanOpen { get { return PauseHandler.paused || (PreRunScript.instance != null && (!PlayerCamera.main || !PlayerCamera.main.body)); } }
    private readonly Dictionary<string, Article> articles = new Dictionary<string, Article>();
    private string JournalPath { get { return Path.Combine(Paths.ConfigPath, "RuFieldGuide", "journal.json"); } }
    public sealed class Article { public string Id, Title, Text, Kind; }

    private void Awake()
    {
        Instance = this;
        try
        {
            if (File.Exists(JournalPath))
                foreach (var a in JsonConvert.DeserializeObject<List<Article>>(File.ReadAllText(JournalPath)) ?? new List<Article>())
                    if (a != null && !string.IsNullOrEmpty(a.Id)) articles[a.Id] = a;
        }
        catch (Exception e) { Logger.LogWarning("Журнал не прочитан; исходный файл сохранён: " + e.Message); notice = "Не удалось прочитать журнал. Он не будет перезаписан."; }
        harmony = new Harmony("local.casualties.wiki");
        harmony.Patch(AccessTools.Method(typeof(PlayerCamera), "ItemHoverDescription"), postfix: new HarmonyMethod(typeof(CuWiki), "ObserveItem"));
        Logger.LogInfo("CuWiki 0.2.1 WIP loaded; game=" + Application.version + "; F8 opens journal from pause menu. No network or gameplay patches.");
    }

    // Only a local, identified tooltip is a discovery. Never enumerate world objects.
    private static void ObserveItem(Item item)
    {
        if (Instance == null || !item || !PlayerCamera.main || !PlayerCamera.main.body) return;
        try
        {
            ItemInfo s = item.Stats;
            if (s == null || !s.rec.recognizable) return;
            string text = Clean(s.description) + "\n\nМасса базового предмета: " + s.weight.ToString("0.###") + " u (игровые единицы)";
            if (s.qualities != null && s.qualities.Count > 0)
                text += "\nСвойства для изготовления: " + string.Join(", ", s.qualities.Select(q => Clean(q.LocaleName) + " (" + q.amount.ToString("0.##") + ")").ToArray());
            if (s.wearable) text += "\nБроня: " + s.wearableArmor.ToString("0.##") + "; теплоизоляция: " + s.wearableIsolation.ToString("0.##");
            Instance.Remember("item:" + item.id, Clean(s.fullName), text, s.category == "medical" || s.category == "drug" ? "Медицина" : "Предметы");
        }
        catch (Exception e) { Instance.Logger.LogDebug("Discovery skipped: " + e.Message); }
    }

    private void Remember(string id, string title, string text, string kind)
    {
        Article old;
        if (articles.TryGetValue(id, out old) && old.Title == title && old.Text == text) return;
        articles[id] = new Article { Id = id, Title = title, Text = text, Kind = kind };
        dirty = true;
    }

    private void ObserveRecipes()
    {
        if (!PlayerCamera.main || !PlayerCamera.main.body || Recipes.recipes == null) return;
        foreach (Recipe r in Recipes.recipes)
        {
            if (r == null || !r.hasMadeBefore) continue;
            var lines = new List<string>();
            foreach (RecipeItem ingredient in r.items)
            {
                string name = ingredient.specific ? (ingredient.isLiquid ? Locale.GetOther(ingredient.specificId) : Locale.GetItem(ingredient.specificId)) : Clean(ingredient.quality.LocaleName) + " (" + ingredient.quality.amount.ToString("0.##") + ")";
                string requirement = ingredient.isLiquid ? (ingredient.specific ? " — " + ingredient.minimumCondition.ToString("0.##") + " мл" : " — требуемое свойство жидкости") : " — состояние от " + (ingredient.minimumCondition * 100).ToString("0.#") + "%";
                lines.Add("• " + Clean(name) + requirement + (ingredient.destroyItem ? " (расходуется)" : " (инструмент)") + (ingredient.noMaxCondition ? "; нельзя использовать при полном состоянии" : ""));
            }
            string text = Clean(r.description) + "\n\nИнгредиенты:\n" + string.Join("\n", lines.ToArray()) + "\n\nТребуемый интеллект: " + r.INT + ".\nРецепт добавлен после успешного изготовления.";
            Remember("recipe:" + r.index, Clean(r.simpleName), text, "Рецепты");
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8))
        {
            if (CanOpen) { opened = !opened; if (opened) { try { ObserveRecipes(); } catch (Exception e) { Logger.LogWarning(e.Message); } } }
        }
        if (!CanOpen) opened = false;
        if (opened && blockedEvents == null && EventSystem.current != null && EventSystem.current.enabled) { blockedEvents = EventSystem.current; blockedEvents.enabled = false; }
        if (!opened) RestoreEvents();
        if (dirty && Time.realtimeSinceStartup > nextSave) { Save(); nextSave = Time.realtimeSinceStartup + 5; }
    }

    private void Save()
    {
        if (!dirty || notice.Length != 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JournalPath));
            string temp = JournalPath + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(articles.Values.OrderBy(a => a.Id).ToList(), Formatting.Indented));
            if (File.Exists(JournalPath)) File.Replace(temp, JournalPath, JournalPath + ".bak"); else File.Move(temp, JournalPath);
            dirty = false;
        }
        catch (Exception e) { Logger.LogWarning("Journal save: " + e.Message); }
    }

    internal static string Clean(string value) { return Regex.Replace(value ?? "", "<[^>]*>", ""); }

    private void OnGUI()
    {
        if (!opened || !CanOpen) return;
        EnsureStyles();
        GUI.depth = -10000;
        float scale = Mathf.Clamp(Screen.height / 900f, 0.7f, 1.8f);
        Matrix4x4 old = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1));
        float width = Mathf.Min(1180, Screen.width / scale - 48), height = Screen.height / scale - 48;
        float left = (Screen.width / scale - width) / 2;
        GUI.DrawTexture(new Rect(left, 24, width, height), background);
        GUILayout.BeginArea(new Rect(left + 26, 44, width - 52, height - 40));
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical();
        GUILayout.Label("ПОЛЕВОЙ СПРАВОЧНИК · WIP", smallStyle);
        GUILayout.Label("То, что ты уже знаешь", headingStyle);
        GUILayout.EndVertical();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Закрыть  /  F8", entryStyle, GUILayout.Width(155), GUILayout.Height(40))) opened = false;
        GUILayout.EndHorizontal();
        GUILayout.Space(12);
        GUILayout.BeginHorizontal();
        foreach (string tab in new[] { "Все", "Предметы", "Медицина", "Рецепты" })
            if (GUILayout.Button(tab, category == tab ? selectedStyle : entryStyle, GUILayout.Width(130), GUILayout.Height(36))) { category = tab; listScroll = Vector2.zero; }
        GUILayout.FlexibleSpace();
        GUILayout.Label(articles.Count + " открытий", smallStyle, GUILayout.Width(110));
        GUILayout.EndHorizontal();
        GUILayout.Space(16);
        if (notice.Length != 0) GUILayout.Label(notice, bodyStyle);
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.Width(310));
        GUILayout.Label("НАЙТИ В ОТКРЫТИЯХ", smallStyle);
        query = GUILayout.TextField(query, searchStyle, GUILayout.Height(38));
        GUILayout.Space(10);
        listScroll = GUILayout.BeginScrollView(listScroll);
        foreach (Article a in articles.Values.OrderBy(a => a.Title))
        {
            if (category != "Все" && a.Kind != category) continue;
            if (query.Length > 0 && (a.Title + " " + a.Text).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (GUILayout.Button(a.Title + "\n" + a.Kind, selected == a.Id ? selectedStyle : entryStyle, GUILayout.MinHeight(58))) { selected = a.Id; detailScroll = Vector2.zero; }
            GUILayout.Space(5);
        }
        GUILayout.EndScrollView();
        GUILayout.EndVertical();
        GUILayout.Space(28);
        detailScroll = GUILayout.BeginScrollView(detailScroll);
        Article current;
        if (articles.TryGetValue(selected, out current)) { GUILayout.Label(current.Kind.ToUpperInvariant(), smallStyle); GUILayout.Space(6); GUILayout.Label(current.Title, headingStyle); GUILayout.Space(20); GUILayout.Label(current.Text, bodyStyle); }
        else
        {
            GUILayout.Space(24);
            GUILayout.Label(articles.Count == 0 ? "У каждого открытия есть история" : "Выбери запись слева", headingStyle);
            GUILayout.Space(20);
            GUILayout.Label("WIP — справочник в разработке.\n\nОсмотри распознанный предмет — его описание останется здесь. Изготовь что-нибудь — и справочник запомнит рецепт.\n\nНезнакомые названия и скрытые рецепты не показываются. Твои открытия сохраняются между забегами.", bodyStyle);
            GUILayout.Space(24);
            GUILayout.Label("F8 в главном меню или меню паузы", smallStyle);
        }
        GUILayout.EndScrollView();
        GUILayout.EndHorizontal();
        GUILayout.Space(14);
        GUILayout.Label("Без спойлеров   ·   Открытия на этом компьютере   ·   В сетевой игре мир не останавливается", smallStyle);
        GUILayout.EndArea();
        GUI.matrix = old;
    }

    private static Texture2D Solid(Color color) { var t = new Texture2D(1, 1); t.SetPixel(0, 0, color); t.Apply(); return t; }
    private void EnsureStyles()
    {
        if (background != null) return;
        background = Solid(new Color(0.055f, 0.075f, 0.09f, 1));
        surface = Solid(new Color(0.10f, 0.13f, 0.15f, 1));
        selectedSurface = Solid(new Color(0.13f, 0.27f, 0.25f, 1));
        Font font = Font.CreateDynamicFontFromOSFont("Segoe UI", 18);
        headingStyle = new GUIStyle(GUI.skin.label) { font = font, fontSize = 30, wordWrap = true, richText = false };
        headingStyle.normal.textColor = new Color(0.94f, 0.95f, 0.92f);
        bodyStyle = new GUIStyle(headingStyle) { fontSize = 18 }; bodyStyle.normal.textColor = new Color(0.79f, 0.84f, 0.85f);
        smallStyle = new GUIStyle(bodyStyle) { fontSize = 13 }; smallStyle.normal.textColor = new Color(0.47f, 0.70f, 0.66f);
        entryStyle = new GUIStyle(GUI.skin.button) { font = font, fontSize = 16, wordWrap = true, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(14, 12, 9, 9), margin = new RectOffset(0, 5, 0, 0) };
        entryStyle.normal.background = surface; entryStyle.normal.textColor = new Color(0.85f, 0.89f, 0.89f); entryStyle.hover.background = selectedSurface; entryStyle.active.background = selectedSurface;
        selectedStyle = new GUIStyle(entryStyle); selectedStyle.normal.background = selectedSurface; selectedStyle.normal.textColor = Color.white;
        searchStyle = new GUIStyle(GUI.skin.textField) { font = font, fontSize = 17, padding = new RectOffset(12, 12, 8, 8) }; searchStyle.normal.background = surface;
    }

    private void RestoreEvents() { if (blockedEvents != null) { blockedEvents.enabled = true; blockedEvents = null; } }
    private void OnDisable() { opened = false; RestoreEvents(); }
    private void OnDestroy() { RestoreEvents(); Save(); if (harmony != null) harmony.UnpatchSelf(); if(background) Destroy(background); if(surface) Destroy(surface); if(selectedSurface) Destroy(selectedSurface); Instance = null; }
    private void OnApplicationQuit() { Save(); }
}
