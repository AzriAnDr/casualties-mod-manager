using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Net;
using System.Text;
using System.Threading.Tasks;

[assembly: AssemblyVersion("0.5.3.0")]
[assembly: AssemblyFileVersion("0.5.3.0")]

public sealed partial class Manager : Form
{
    const string LauncherVersion = "0.5.3";
    const string UpdateFeed = "https://github.com/AzriAnDr/casualties-mod-manager/releases/latest/download/update.json";
    static string KnownTogetherVersion;
    public sealed class Item { public string Path; public string Hash; }
    public sealed class Manifest { public string Module; public string Version; public bool Disabled; public List<Item> Files = new List<Item>(); }
    public sealed class Module { public string Id, Name, Description, Prefix, Dll, Resource; public Module(string id,string name,string description,string prefix,string dll,string resource) { Id=id;Name=name;Description=description;Prefix=prefix;Dll=dll;Resource=resource; } }
    static readonly Module[] Catalog = {
        new Module("russian","Русский перевод","Перевод интерфейса и игровых текстов. Пакет уже встроен в EXE. Обновление из нового комплекта или ZIP; онлайн-репозитория этого плагина пока нет.","BepInEx/plugins/CuRussian/","CuRussian.dll","russian.zip"),
        new Module("wiki","Справочник открытий · WIP","В разработке (WIP). Экспериментальная внутриигровая энциклопедия; полнота и работа в игре ещё проверяются. Пакет уже встроен в EXE. Обновление из нового комплекта или ZIP. Открытия сохраняются при удалении.","BepInEx/plugins/CuWiki/","CuWiki.dll","wiki.zip"),
        new Module("together","Casualties Together","Совместная игра. ZIP последнего fullgame-релиза с GitHub; совместимость смотрите в описании релиза.","BepInEx/plugins/Together/","CasualtiesMP.dll",null),
        new Module("bepinex","BepInEx 5","Общий загрузчик. Установите до модов. Удаление блокируется, пока в plugins есть DLL.","BepInEx/core/","BepInEx.dll","Loader.zip"),
    };
    // Kept solely to manage/remove installations made by older manager versions.
    static readonly Module RetiredUnlock = new Module("unlock","Character Unlock · прежняя установка","Больше не входит в предлагаемые модули. Здесь можно отключить или удалить прежнюю управляемую установку.","BepInEx/plugins/SkipCharacterUnlock/","SkipCharacterUnlock.dll",null);
    static IEnumerable<Module> AllModules { get { return Catalog.Concat(new[]{RetiredUnlock}); } }
    static IEnumerable<Module> VisibleModules(string root) { return root != null && (Load(root,RetiredUnlock)!=null || Detect(root,RetiredUnlock).Count>0) ? AllModules : Catalog; }
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength=16*1024*1024 };
    static readonly Dictionary<string,string> BundledVersions = ReadBundledVersions();
    static Dictionary<string,string> ReadBundledVersions()
    {
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BundledCatalog.json"))
        {
            if (stream == null) return new Dictionary<string,string>();
            using (var reader = new StreamReader(stream)) return Json.Deserialize<Dictionary<string,string>>(reader.ReadToEnd());
        }
    }
    static string BundledVersion(string id) { string value; return BundledVersions.TryGetValue(id, out value) ? value : "пакет отсутствует"; }
    static readonly string SettingsDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CasualtiesModManager");
    static readonly string LogFile=Path.Combine(SettingsDir,"manager.log");
    const string State="BepInEx/config/CasualtiesModManager/";
    TextBox game, details, log; ListView list; Button install, remove, toggle, import, adopt, online; bool busy;
    [STAThread] public static int Main(string[] args) {
        Application.EnableVisualStyles();
        try {
            if(args.Length>0) { if(args.Length<2||(args[0]!="--scan"&&args.Length<3))throw new Exception("Использование: --scan <папка игры>; --install <папка> <модуль> [ZIP]; --remove/--disable/--enable/--adopt <папка> <модуль>."); string root=Validate(args[1],args[0]!="--scan"); string result="";
                if(args[0]=="--scan") result=ScanText(root);
                else { using(FileStream operationLock=LockGame(root)) { Module m=Find(args[2]); if(args[0]=="--install") { using(Stream s=args.Length>3?File.OpenRead(args[3]):Resource(m)) Install(root,m,ReadZip(s,m),"import"); }
                else if(args[0]=="--remove") Remove(root,m); else if(args[0]=="--disable") Toggle(root,m,true); else if(args[0]=="--enable") Toggle(root,m,false);
                else if(args[0]=="--adopt") Adopt(root,m); else throw new Exception("Неизвестная команда"); result=ScanText(root); } }
                Console.WriteLine(result); Log(result); File.WriteAllText(Path.Combine(SettingsDir,"last-cli-result.json"),result,Encoding.UTF8); return 0; }
            Application.Run(new Manager());return 0;
        } catch(Exception e) { Log(e.ToString()); if(args.Length==0) MessageBox.Show(e.Message,"Менеджер модов",MessageBoxButtons.OK,MessageBoxIcon.Warning); else { Console.Error.WriteLine(e.Message); File.WriteAllText(Path.Combine(SettingsDir,"last-cli-error.txt"),e.ToString()); } return 1; }
    }
    static readonly Color Canvas = Color.FromArgb(14, 22, 29);
    static readonly Color Surface = Color.FromArgb(23, 35, 44);
    static readonly Color Border = Color.FromArgb(49, 69, 80);
    static readonly Color Ink = Color.FromArgb(228, 237, 240);
    static readonly Color Muted = Color.FromArgb(155, 178, 187);
    static readonly Color Accent = Color.FromArgb(66, 209, 183);
    static string LastBackup;
    Label context, actionStatus;
    Button launch, browse, scan;

    public Manager()
    {
        Text = "Casualties: Unknown · Менеджер модов " + LauncherVersion;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        BackColor = Canvas;
        ForeColor = Ink;
        ClientSize = new Size(1100, 880);
        MinimumSize = new Size(950, 760);
        StartPosition = FormStartPosition.CenterScreen;
        try { using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIcon.ico")) if (s != null) using (var icon = new Icon(s)) Icon = (Icon)icon.Clone(); } catch { }

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 7, AutoScroll = true, BackColor = Canvas };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 90, AutoSize = false, ColumnCount = 2, RowCount = 2, Margin = new Padding(0, 0, 0, 10) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var title = new Label { Text = "CASUALTIES: UNKNOWN", Font = new Font("Segoe UI Semibold", 21), AutoSize = true, ForeColor = Ink, Margin = Padding.Empty };
        var subtitle = new Label { Text = "МЕНЕДЖЕР " + LauncherVersion + "  /  РУС " + BundledVersion("russian") + "  /  ВИКИ " + BundledVersion("wiki"), Font = new Font("Segoe UI", 9), ForeColor = Accent, AutoSize = true, Margin = new Padding(2, 4, 0, 0) };
        launch = MakeButton("Запустить через Steam", 220, true);
        launch.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        launch.Click += delegate { try { Process.Start("steam://rungameid/4576490"); WriteLog("Steam: отправлена команда запуска Casualties: Unknown."); } catch (Exception e) { ShowFailure("Запуск через Steam", e); } };
        var tips = new ToolTip { AutoPopDelay = 12000 };
        tips.SetToolTip(launch, "Запускает установленную Steam-версию (App 4576490). Папка ниже используется для управления файлами.");
        var launcherActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };
        var updateLauncher = MakeButton("Обновить лаунчер", 220, false);
        updateLauncher.Click += async delegate { if (busy) return; updateLauncher.Enabled = false; try { await CheckLauncherUpdate(); } finally { if (!IsDisposed) updateLauncher.Enabled = true; } };
        tips.SetToolTip(updateLauncher, "Проверяет GitHub Releases и обновляет весь EXE вместе со встроенными модами и каталогом.");
        launcherActions.Controls.Add(launch); launcherActions.Controls.Add(updateLauncher);
        header.Controls.Add(title, 0, 0); header.Controls.Add(subtitle, 0, 1); header.Controls.Add(launcherActions, 1, 0); header.SetRowSpan(launcherActions, 2);
        layout.Controls.Add(header, 0, 0);

        var path = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 70, AutoSize = false, ColumnCount = 3, RowCount = 2, Margin = new Padding(0, 0, 0, 12) };
        path.RowStyles.Add(new RowStyle(SizeType.Absolute,22));path.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        path.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); path.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); path.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var pathLabel = new Label { Text = "ПАПКА ИГРЫ", ForeColor = Muted, AutoSize = true, Font = new Font("Segoe UI", 9), Margin = new Padding(0, 0, 0, 8) };
        game = new TextBox { Dock = DockStyle.Fill, Text = DefaultPath(), BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 7, 10, 0) };
        browse = MakeButton("Выбрать…", 110, false); scan = MakeButton("Обновить", 112, false);
        browse.Click += delegate { using (var d = new FolderBrowserDialog()) { d.Description = "Папка с CasualtiesUnknown.exe"; if (Directory.Exists(game.Text)) d.SelectedPath = game.Text; if (d.ShowDialog() == DialogResult.OK) { game.Text = d.SelectedPath; RefreshList(); } } };
        scan.Click += delegate { RefreshList(); };
        path.Controls.Add(pathLabel, 0, 0); path.SetColumnSpan(pathLabel, 3); path.Controls.Add(game, 0, 1); path.Controls.Add(browse, 1, 1); path.Controls.Add(scan, 2, 1);
        layout.Controls.Add(path, 0, 1);

        list = new ListView { Dock = DockStyle.Fill, MinimumSize = new Size(0, 185), View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, HeaderStyle = ColumnHeaderStyle.Nonclickable, BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.None, OwnerDraw = true, ShowItemToolTips = true, Margin = new Padding(0, 0, 0, 16) };
        list.Columns.Add("Модуль"); list.Columns.Add("Состояние"); list.Columns.Add("Версия DLL"); list.Columns.Add("Доступно"); list.Columns.Add("Версия игры");
        list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e) { using (var b = new SolidBrush(Border)) e.Graphics.FillRectangle(b, e.Bounds); TextRenderer.DrawText(e.Graphics, e.Header.Text, Font, new Rectangle(e.Bounds.X + 12, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height), Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); };
        list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e) { if (list.View != View.Details) e.DrawDefault = true; };
        list.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e) { bool selected = e.Item.Selected; Color color = selected ? Color.FromArgb(26, 74, 73) : Surface; using (var b = new SolidBrush(color)) e.Graphics.FillRectangle(b, e.Bounds); Color text = e.ColumnIndex == 1 && !selected ? Accent : Ink; TextRenderer.DrawText(e.Graphics, e.SubItem.Text, Font, new Rectangle(e.Bounds.X + 12, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 16), e.Bounds.Height), text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); };
        // A scaled image list supplies readable, evenly spaced native rows without custom hit-testing.
        list.SmallImageList = new ImageList { ImageSize = new Size(1, 38), ColorDepth = ColorDepth.Depth32Bit };
        list.SmallImageList.Images.Add(new Bitmap(1, 38));
        bool sizingModuleColumns=false;list.SizeChanged += delegate {if(sizingModuleColumns)return;sizingModuleColumns=true;try{int width=Math.Max(200,list.ClientSize.Width-SystemInformation.VerticalScrollBarWidth-4);int[] shares={23,25,12,18,22};for(int i=0;i<5;i++)list.Columns[i].Width=width*shares[i]/100;}finally{sizingModuleColumns=false;}};
        list.SelectedIndexChanged += delegate { UpdateDetails(); };
        layout.Controls.Add(list, 0, 2);

        var information = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 12) };
        details = new TextBox { Dock = DockStyle.Fill, Height = 65, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Canvas, ForeColor = Ink, WordWrap = true, ScrollBars = ScrollBars.Vertical, Margin = Padding.Empty, TabStop = false };
        context = new Label { AutoSize = true, ForeColor = Accent, Margin = new Padding(0, 8, 0, 0), Text = "Выберите модуль, чтобы увидеть доступные действия." };
        information.Controls.Add(details, 0, 0); information.Controls.Add(context, 0, 1);
        information.SizeChanged += delegate { context.MaximumSize = new Size(Math.Max(200, information.ClientSize.Width), 0); };
        layout.Controls.Add(information, 0, 3);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 0, 12) };
        install = MakeButton("Установить / обновить", 206, true); toggle = MakeButton("Отключить", 120, false); remove = MakeButton("Удалить", 100, false); import = MakeButton("Импорт ZIP…", 138, false); adopt = MakeButton("Управлять модом…", 178, false); online = MakeButton("Страница мода", 142, false);
        actions.Controls.AddRange(new Control[] { install, toggle, remove, import, adopt, online });
        tips.SetToolTip(adopt, "Для установленного вручную мода: посмотреть найденные файлы, сохранить копию и разрешить менеджеру обновлять или удалять только эти файлы.");
        tips.SetToolTip(import, "Выберите ZIP для выделенного модуля. Перед установкой менеджер покажет список файлов.");
        install.Click += delegate { ActionRun("установка / обновление", delegate(string root, Module m) { if(m.Id!="bepinex"&&!EnsureLoaderForCommunity(root,true))return null; if (m.Id == "together") return OnlineInstall(root, m); using (Stream s = Resource(m)) { var files = ReadZip(s, m); if (!Preview(files, "Установить или обновить «" + m.Name + "»?")) return null; bool update = Load(root, m) != null; Install(root, m, files, "bundled"); return update ? "Обновлён из комплекта" : "Установлен из комплекта"; } }); };
        remove.Click += delegate { ActionRun("удаление", delegate(string root, Module m) { Manifest old = Load(root, m); if (old == null) throw new Exception("Для ручной установки сначала выберите «Управлять модом…» и проверьте найденные файлы."); if (!Preview(old.Files.ToDictionary(f => f.Path, f => new byte[0]), "Удалить перечисленные файлы «" + m.Name + "»? Конфиги и открытия сохранятся.", "Удалить файлы")) return null; Remove(root, m); return "Управляемые файлы удалены; настройки сохранены"; }); };
        toggle.Click += delegate { ActionRun("переключение состояния", delegate(string root, Module m) { Manifest old = Load(root, m); if (old == null) throw new Exception("Сначала включите управление найденным модом."); bool disabled = !old.Disabled; Toggle(root, m, disabled); return disabled ? "Отключён; DLL временно исключены из загрузки" : "Включён; DLL восстановлены для загрузки"; }); };
        import.Click += delegate { ActionRun("импорт ZIP", delegate(string root, Module m) { if (m.Id == "unlock" && MessageBox.Show(this, "Совместимость SkipCharacterUnlock с игрой 1.0.2 и Together не подтверждена. Импорт возможен только из ZIP с опознаваемым SkipCharacterUnlock.dll. Продолжить?", "Совместимость не проверена", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return null; using (var d = new OpenFileDialog { Filter = "ZIP-архив|*.zip", Title = "ZIP для модуля: " + m.Name }) { if (d.ShowDialog(this) != DialogResult.OK) return null; using (Stream s = File.OpenRead(d.FileName)) { var files = ReadZip(s, m); if (!Preview(files, "Импорт в модуль «" + m.Name + "». Проверьте пути перед установкой.")) return null; Install(root, m, files, "import"); return "Установлен / обновлён из ZIP «" + Path.GetFileName(d.FileName) + "»"; } } }); };
        adopt.Click += delegate { ActionRun("включение управления", delegate(string root, Module m) { var files = Detect(root, m); if (files.Count == 0) throw new Exception("Файлы ручной установки не найдены."); string message = "«" + m.Name + "» уже установлен вручную.\r\n\r\nМенеджер сохранит резервную копию перечисленных файлов и запомнит их состав. После этого он сможет обновлять и удалять только эти файлы. Мод не переустанавливается, его настройки не меняются.\r\n\r\nПроверьте список и разрешите управление:"; if (!Preview(files.ToDictionary(p => p, p => new byte[0]), message, "Включить управление")) return null; Adopt(root, m); return "Управление включено для " + files.Count + " файлов ручной установки; мод и настройки сохранены"; }); };
        online.Click += delegate { Module m = Selected(); if (m == null) return; try { Process.Start(Source(m)); WriteLog(m.Name + ": открыта страница автора."); } catch (Exception e) { ShowFailure(m.Name + ": открытие страницы", e); } };
        layout.Controls.Add(actions, 0, 4);

        actionStatus = new Label { AutoSize = true, Text = "Для друзей достаточно этого EXE: перевод, вики и BepInEx уже внутри. Together скачивается с GitHub. Перед установкой закройте игру.", ForeColor = Muted, Margin = new Padding(0, 0, 0, 12) };
        layout.SizeChanged += delegate { actionStatus.MaximumSize = new Size(Math.Max(200, layout.ClientSize.Width - layout.Padding.Horizontal), 0); };
        layout.Controls.Add(actionStatus, 0, 5);
        log = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = Surface, ForeColor = Muted, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9), Margin = Padding.Empty };
        layout.Controls.Add(log, 0, 6);
        InstallCatalogUi(layout, information, actions);
        Shown += delegate { LauncherUpdate.CleanupPreviousBackup(); };
        Controls.Add(layout);
        Shown += delegate { RefreshList(); };
    }

    sealed class ThemeButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled) { base.OnPaint(e); return; }
            e.Graphics.Clear(BackColor);
            using (var pen = new Pen(Border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(111, 139, 150), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
    Button MakeButton(string text, int width, bool primary)
    {
        var b = new ThemeButton { Text = text, Width = width, Height = 39, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Surface, ForeColor = primary ? Canvas : Ink, Margin = new Padding(0, 0, 8, 8), UseVisualStyleBackColor = false, Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = primary ? Accent : Border;
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(109, 226, 203) : Color.FromArgb(36, 55, 65);
        b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(46, 175, 155) : Border;
        return b;
    }
    async Task CheckLauncherUpdate()
    {
        try
        {
            busy = true; UpdateDetails(); SetStatus("Проверяю обновление лаунчера " + LauncherVersion + "…", false);
            var update = await Task.Run(() => LauncherUpdate.Check(UpdateFeed, LauncherVersion));
            if (update == null) { SetStatus("Лаунчер " + LauncherVersion + ": установлена последняя версия.", false); return; }
            if (MessageBox.Show(this, "Доступен лаунчер " + update.Version + ".\r\nВместе с ним обновятся встроенные пакеты и каталог модов. Уже установленные в игре моды обновляются отдельно.\r\n\r\n" + update.Notes + "\r\n\r\nСкачать и перезапустить лаунчер?", "Обновление лаунчера", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            { SetStatus("Обновление лаунчера отменено.", false); return; }
            SetStatus("Скачиваю лаунчер " + update.Version + " и проверяю SHA-256…", false);
            string staged = await Task.Run(() => LauncherUpdate.DownloadStage(update));
            LauncherUpdate.ApplyOnExit(staged, Assembly.GetExecutingAssembly().Location);
            WriteLog("Подготовлено обновление лаунчера до " + update.Version + ". Замена EXE после закрытия; предыдущий EXE сохранится как .bak.");
            Close();
        }
        catch (Exception e) { ShowFailure("Обновление лаунчера", e); }
        finally { if (!IsDisposed) { busy = false; UpdateDetails(); } }
    }
    Module Selected() { return list.SelectedItems.Count == 0 ? null : list.SelectedItems[0].Tag as Module; }
    static string ModuleState(Module m,Manifest manifest,int detected,string installedVersion)
    {
        string state=manifest!=null?(manifest.Disabled?"Отключён":"Под управлением"):(detected>0?"Установлен вручную":"Не установлен");
        string latest=m.Resource!=null?BundledVersion(m.Id):m.Id=="together"?KnownTogetherVersion:null;
        if((manifest!=null||detected>0)&&latest!=null&&ModCatalog.CompareVersions(installedVersion,latest)==ModCatalog.VersionComparison.Older)state+=" · есть обновление";
        return state;
    }
    void RefreshList()
    {
        try
        {
            string root = Validate(game.Text, false);
            Directory.CreateDirectory(SettingsDir); File.WriteAllText(Path.Combine(SettingsDir, "game-path.txt"), root);
            int index = list.SelectedIndices.Count > 0 ? list.SelectedIndices[0] : 0;
            list.BeginUpdate();
            try { list.Items.Clear(); foreach (Module m in VisibleModules(root)) { Manifest mf = Load(root, m); var detected = Detect(root, m); string version = VersionOf(root, m, mf); string state = ModuleState(m,mf,detected.Count,version); var row = new ListViewItem(new[] { m.Name, state, version, Available(m),ModuleGameVersion(m,version) }); row.Tag=m; row.ToolTipText = m.Name + "\n" + state + "\n" + m.Description; list.Items.Add(row); } list.Items[Math.Min(index, list.Items.Count - 1)].Selected = true; }
            finally { list.EndUpdate(); }
            AppendCommunityRows(root);
            UpdateDetails();
        }
        catch (Exception e)
        {
            list.Items.Clear();
            foreach (Module m in Catalog) list.Items.Add(new ListViewItem(new[] { m.Name, "Выберите папку игры", "—", Available(m),ModuleGameVersion(m) }){Tag=m});
            if (list.Items.Count > 0) list.Items[0].Selected = true;
            UpdateDetails(); SetStatus("Папка игры не найдена или недоступна. Нажмите «Выбрать…». Встроенные пакеты уже есть в EXE.", true); WriteLog("Проверка папки: " + e.Message);
        }
    }
    void UpdateDetails()
    {
        if (ShowCommunitySelection()) return;
        Module m = Selected(); bool usable = m != null && !busy;
        details.Text = m == null ? "Выберите папку игры и модуль в списке. Менеджер различает ручную установку и файлы под своим управлением." : m.Description;
        install.Enabled = usable && m.Id != "unlock"; import.Enabled = usable && m.Id != "unlock";
        remove.Enabled = toggle.Enabled = adopt.Enabled = false;
        online.Enabled = usable && m.Id == "together";
        launch.Enabled = browse.Enabled = scan.Enabled = game.Enabled = !busy;
        context.Text = m == null ? "Сначала выберите папку с CasualtiesUnknown.exe." : "Модуль не установлен: используйте комплект или импорт ZIP.";
        if (!usable) return;
        try
        {
            string root = Validate(game.Text, false); Manifest mf = Load(root, m); int count = Detect(root, m).Count;
            remove.Enabled = mf != null; toggle.Enabled = mf != null && m.Id != "bepinex"; adopt.Enabled = mf == null && count > 0;
            toggle.Text = mf != null && mf.Disabled ? "Включить" : "Отключить";
            if (mf != null) context.Text = "Под управлением: " + mf.Files.Count + " файлов. Доступны обновление, удаление" + (m.Id == "bepinex" ? "." : " и " + (mf.Disabled ? "включение." : "отключение."));
            else if (count > 0) context.Text = "Установлен вручную. «Управлять модом…» покажет " + count + " файлов, сохранит копию и включит их обновление и удаление.";
            else if (m.Id == "unlock") context.Text = "Скачайте ZIP со страницы автора. Совместимость с игрой 1.0.2 и Together не проверена.";
            if (m.Id == "russian" || m.Id == "wiki") if (File.Exists(Under(root, "BepInEx/plugins/RuFieldGuide/RuFieldGuide.dll"))) { details.Text += "\r\nНайден прежний RuFieldGuide: сначала удалите его старым установщиком. Открытия сохранятся."; }
        }
        catch (Exception e) { install.Enabled = remove.Enabled = toggle.Enabled = import.Enabled = adopt.Enabled = false; context.Text = e.Message; }
    }
    void SetStatus(string text, bool error) { actionStatus.Text = text; actionStatus.ForeColor = error ? Color.FromArgb(255, 172, 147) : Accent; }
    void WriteLog(string s) { log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine); Log(s); }
    void ShowFailure(string operation, Exception e) { SetStatus(operation + ": " + e.Message, true); WriteLog(operation + ": " + e.Message); MessageBox.Show(this, e.Message, operation, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    string ResultingState(string root, Module m)
    {
        Manifest mf = Load(root, m);
        return mf == null ? (Detect(root, m).Count == 0 ? "Состояние: не установлен." : "Состояние: остались неуправляемые файлы.") : "Состояние: " + (mf.Disabled ? "отключён" : "под управлением") + "; DLL " + VersionOf(root, m, mf) + ".";
    }
    void ActionRun(string operation, Func<string, Module, string> action)
    {
        Module m = Selected(); if (m == null) return;
        try
        {
            busy = true; UpdateDetails(); Cursor = Cursors.WaitCursor; SetStatus(m.Name + ": " + operation + "…", false);
            string root = Validate(game.Text, true); string result; LastBackup = null;
            using (FileStream operationLock = LockGame(root)) result = action(root, m);
            if (result == null) { string cancelled = m.Name + ": действие «" + operation + "» отменено. Файлы модуля не изменены."; SetStatus(cancelled, false); WriteLog(cancelled); return; }
            string message = m.Name + ": " + result + ". " + ResultingState(root, m);
            string status = message;
            if (LastBackup != null) { status += " Копия сохранена: " + Path.GetFileName(LastBackup.TrimEnd('/')) + "."; message += " Резервная копия: " + LastBackup; }
            SetStatus(status, false); WriteLog(message);
        }
        catch (Exception e) { ShowFailure(m.Name + ": " + operation, e); }
        finally { busy = false; Cursor = Cursors.Default; ModCatalog.ClearDetectionCache(); RefreshList(); var catalogRefresh = ReloadCommunity(false); }
    }
    bool Preview(Dictionary<string, byte[]> files, string title, string confirm = "Подтвердить")
    {
        using (var f = new Form { Text = "Проверка файлов", ClientSize = new Size(790, 610), MinimumSize = new Size(650, 490), StartPosition = FormStartPosition.CenterParent, Font = Font, BackColor = Canvas, ForeColor = Ink, AutoScaleMode = AutoScaleMode.Dpi })
        {
            if (Icon != null) f.Icon = Icon;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 1, RowCount = 3 };
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var heading = new Label { AutoSize = true, Text = title + "\r\n\r\nФайлов в списке: " + files.Count, ForeColor = Ink, MaximumSize = new Size(740, 0), Margin = new Padding(0, 0, 0, 16) };
            panel.SizeChanged += delegate { heading.MaximumSize = new Size(Math.Max(200, panel.ClientSize.Width - panel.Padding.Horizontal), 0); };
            var t = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = Surface, ForeColor = Ink, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 9), Text = String.Join("\r\n", files.Keys.OrderBy(x => x).ToArray()), Margin = new Padding(0, 0, 0, 16) };
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true };
            var ok = MakeButton(confirm, 210, true); ok.DialogResult = DialogResult.OK;
            var cancel = MakeButton("Отмена", 110, false); cancel.DialogResult = DialogResult.Cancel;
            buttons.Controls.Add(ok); buttons.Controls.Add(cancel); panel.Controls.Add(heading, 0, 0); panel.Controls.Add(t, 0, 1); panel.Controls.Add(buttons, 0, 2); f.Controls.Add(panel); f.CancelButton = cancel;
            return f.ShowDialog(this) == DialogResult.OK;
        }
    }
    static Module Find(string id){Module m=AllModules.FirstOrDefault(x=>x.Id==id);if(m==null)throw new Exception("Модуль недоступен: "+id);return m;}
    static string DefaultPath()
    {
        string saved = null;
        try { string file = Path.Combine(SettingsDir,"game-path.txt"); if (File.Exists(file)) saved = File.ReadAllText(file); } catch { }
        return SteamGameLocator.Find(saved, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
    }
    static void Log(string s){Directory.CreateDirectory(SettingsDir);File.AppendAllText(LogFile,DateTime.Now.ToString("s")+" "+s+Environment.NewLine,Encoding.UTF8);}
    static string Validate(string path,bool mutate){string root=Path.GetFullPath(path.Trim().Trim('"')).TrimEnd('\\','/');if(!File.Exists(Path.Combine(root,"CasualtiesUnknown.exe"))||!Directory.Exists(Path.Combine(root,"CasualtiesUnknown_Data")))throw new Exception("Выберите папку с CasualtiesUnknown.exe и CasualtiesUnknown_Data.");for(var d=new DirectoryInfo(root);d!=null;d=d.Parent)if((d.Attributes&FileAttributes.ReparsePoint)!=0)throw new Exception("Путь содержит junction или ссылку.");if(mutate&&Process.GetProcessesByName("CasualtiesUnknown").Length>0)throw new Exception("Сначала закройте игру.");return root;}
    static string Under(string root,string relative){if(String.IsNullOrWhiteSpace(relative)||relative.IndexOf(':')>=0||relative.StartsWith("/")||relative.StartsWith("\\"))throw new Exception("Недопустимый путь: "+relative);string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new Exception("Путь выходит за папку игры.");for(var d=new DirectoryInfo(Path.GetDirectoryName(full));d!=null&&d.FullName.Length>=root.Length;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new Exception("Папка содержит ссылку: "+d.FullName);if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new Exception("Файл является ссылкой.");return full;}
    static FileStream LockGame(string root){string path=Under(root,State+"operation.lock");Directory.CreateDirectory(Path.GetDirectoryName(path));try{return new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException){throw new Exception("Другой экземпляр менеджера выполняет операцию. Дождитесь завершения.");}}
    static string Hash(string path){using(var s=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
    static string Hash(byte[] data){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
    static string ManifestPath(string root,Module m){return Under(root,State+m.Id+".json");}
    new static Manifest Load(string root,Module m){string p=ManifestPath(root,m);if(!File.Exists(p))return null;Manifest mf=Json.Deserialize<Manifest>(File.ReadAllText(p));if(mf==null||mf.Module!=m.Id||mf.Files==null)throw new Exception("Повреждён манифест "+m.Id);foreach(Item f in mf.Files)if(!Allowed(m,f.Path)||String.IsNullOrEmpty(f.Hash))throw new Exception("Некорректный манифест "+m.Id);if(mf.Files.Select(f=>f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=mf.Files.Count)throw new Exception("Повтор путей в манифесте");return mf;}
    static Stream Resource(Module m){if(m.Resource==null)throw new Exception("Используйте ZIP или загрузку Together из GitHub.");Stream s=Assembly.GetExecutingAssembly().GetManifestResourceStream(m.Resource);if(s==null)throw new Exception("В сборке нет ресурса "+m.Resource);return s;}
    static bool Allowed(Module m,string path){string p=path.Replace('\\','/');if(p.Split('/').Any(x=>x==".."||x=="."||x.EndsWith(".")||x.EndsWith(" "))||p.IndexOf(':')>=0)return false;if(m.Id=="russian"||m.Id=="wiki"||m.Id=="unlock")return p.StartsWith(m.Prefix,StringComparison.OrdinalIgnoreCase)||(m.Id=="unlock"&&p.Equals("BepInEx/plugins/SkipCharacterUnlock.dll",StringComparison.OrdinalIgnoreCase));if(m.Id=="together")return p.StartsWith("BepInEx/plugins/Together/",StringComparison.OrdinalIgnoreCase)||p.Equals("BepInEx/patchers/CT_AutoUpdateApplier.dll",StringComparison.OrdinalIgnoreCase);if(m.Id=="bepinex")return p.StartsWith("BepInEx/core/",StringComparison.OrdinalIgnoreCase)||p.Equals("winhttp.dll",StringComparison.OrdinalIgnoreCase)||p.Equals("doorstop_config.ini",StringComparison.OrdinalIgnoreCase)||p.Equals(".doorstop_version",StringComparison.OrdinalIgnoreCase);return false;}
    const long MaximumZipFileBytes = 128L * 1024 * 1024;
    const long MaximumZipTotalBytes = 256L * 1024 * 1024;
    const int TogetherMetadataBytes = 2 * 1024 * 1024;
    const int TogetherArchiveBytes = 128 * 1024 * 1024;
    const int TogetherTimeoutMilliseconds = 20000;

    static Dictionary<string, byte[]> ReadZip(Stream source, Module module)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0, actualTotal = 0;
        using (var zip = new ZipArchive(source, ZipArchiveMode.Read))
        {
            if (zip.Entries.Count > 10000) throw new InvalidDataException("Слишком много файлов в ZIP.");
            foreach (var entry in zip.Entries)
            {
                string path = entry.FullName.Replace('\\', '/');
                if (module.Id == "together" && path.StartsWith("mod/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(4);
                if (module.Id == "unlock" && (path.Equals("SkipCharacterUnlock.dll", StringComparison.OrdinalIgnoreCase) || path.Equals("BepInEx/plugins/SkipCharacterUnlock.dll", StringComparison.OrdinalIgnoreCase))) path = module.Prefix + "SkipCharacterUnlock.dll";
                if (path.EndsWith("/")) continue;
                if (path.StartsWith("/") || path.IndexOf(':') >= 0 || path.Split('/').Any(p => p == ".." || p == ".")) throw new InvalidDataException("Опасный путь в ZIP: " + path);
                if (!Allowed(module, path))
                {
                    if ((module.Id == "bepinex" || module.Id == "together") && (path.Equals("CHANGELOG.txt", StringComparison.OrdinalIgnoreCase) || path.Equals("FOR-NERDS.txt", StringComparison.OrdinalIgnoreCase) || path.Equals("INSTALLATION.txt", StringComparison.OrdinalIgnoreCase) || path.Equals("LICENSE", StringComparison.OrdinalIgnoreCase) || path.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase) || path.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) || path.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase) || path.Equals(".doorstop_version", StringComparison.OrdinalIgnoreCase))) continue;
                    throw new InvalidDataException("ZIP содержит файл другого модуля: " + path);
                }
                if (entry.Length < 0 || entry.Length > MaximumZipFileBytes || entry.Length > MaximumZipTotalBytes - declaredTotal) throw new InvalidDataException("ZIP слишком велик.");
                declaredTotal += entry.Length;
                if (result.ContainsKey(path)) throw new InvalidDataException("Повтор пути в ZIP: " + path);
                using (Stream input = entry.Open()) result.Add(path, ReadZipEntry(input, entry.Length, ref actualTotal));
            }
        }
        if (result.Count == 0 || !result.Keys.Any(p => Path.GetFileName(p).Equals(module.Dll, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("В ZIP не найден основной файл выбранного модуля: " + module.Dll + ". Выберите предназначенный для этого модуля ZIP.");
        return result;
    }
    static byte[] ReadZipEntry(Stream input, long declaredLength, ref long actualTotal)
    {
        if (declaredLength < 0 || declaredLength > MaximumZipFileBytes) throw new InvalidDataException("Файл ZIP слишком велик.");
        using (var memory = new MemoryStream())
        {
            byte[] buffer = new byte[32768]; long actualLength = 0; int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                // Count bytes before allocating/writing them; central-directory lengths are untrusted.
                if (read > declaredLength - actualLength || read > MaximumZipFileBytes - actualLength || read > MaximumZipTotalBytes - actualTotal) throw new InvalidDataException("Распакованные данные превышают заявленную длину или допустимый размер ZIP.");
                actualLength += read; actualTotal += read; memory.Write(buffer, 0, read);
            }
            if (actualLength != declaredLength) throw new InvalidDataException("ZIP повреждён: фактическая длина файла не совпадает с заявленной.");
            return memory.ToArray();
        }
    }
    static string Actual(Manifest mf,Item f){return f.Path+(mf.Disabled&&f.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?".cumm-disabled":"");}
    static void CheckOwned(string root,Manifest mf){if(mf==null)return;foreach(Item f in mf.Files){string p=Under(root,Actual(mf,f));if(!File.Exists(p)||Hash(p)!=f.Hash)throw new Exception("Управляемый файл отсутствует или изменён: "+Actual(mf,f)+". Восстановите файл из копии либо сохраните изменения отдельно.");}}
    static List<string> Detect(string root,Module m){var files=new List<string>();if(m.Id=="unlock"&&File.Exists(Under(root,"BepInEx/plugins/SkipCharacterUnlock.dll")))files.Add("BepInEx/plugins/SkipCharacterUnlock.dll");string prefix=m.Prefix;string dir=Under(root,prefix.TrimEnd('/'));if(Directory.Exists(dir))foreach(string f in EnumerateSafe(root,dir)){string rel=f.Substring(root.Length+1).Replace('\\','/');if(Allowed(m,rel)&&(m.Id!="together"||KnownTogether(rel)))files.Add(rel);}if(m.Id=="bepinex")foreach(string p in new[]{"winhttp.dll","doorstop_config.ini",".doorstop_version"})if(File.Exists(Under(root,p)))files.Add(p);if(m.Id=="together"){string patch=Under(root,"BepInEx/patchers");if(Directory.Exists(patch))foreach(string f in EnumerateSafe(root,patch)){string rel=f.Substring(root.Length+1).Replace('\\','/');if(Path.GetFileName(f).Equals("CT_AutoUpdateApplier.dll",StringComparison.OrdinalIgnoreCase))files.Add(rel);}}return files;}
    static bool KnownTogether(string p){string[] names={"CasualtiesMP.dll","opus.dll","OpusSharp.Core.dll","RefMagic.dll","LiteNetLib.dll","LiteEntitySystem.dll","System.Runtime.CompilerServices.Unsafe.dll","K4os.Compression.LZ4.dll","Multiupdater.dll"};return p.StartsWith("BepInEx/plugins/Together/",StringComparison.OrdinalIgnoreCase)&&names.Contains(Path.GetFileName(p),StringComparer.OrdinalIgnoreCase);}
    static IEnumerable<string> EnumerateSafe(string root,string dir){Under(root,dir.Substring(root.Length+1));foreach(string f in Directory.GetFiles(dir)){Under(root,f.Substring(root.Length+1));yield return f;}foreach(string child in Directory.GetDirectories(dir)){Under(root,child.Substring(root.Length+1)+"/test");foreach(string f in EnumerateSafe(root,child))yield return f;}}
    static string VersionOf(string root,Module m,Manifest mf){IEnumerable<string> paths=mf!=null?mf.Files.Where(f=>f.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)).Select(f=>Under(root,Actual(mf,f))):Detect(root,m).Where(p=>p.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)).Select(p=>Under(root,p));foreach(string p in paths.OrderBy(p=>Path.GetFileName(p).Equals(m.Dll,StringComparison.OrdinalIgnoreCase)?0:1)){try{return AssemblyName.GetAssemblyName(p).Version.ToString();}catch{}}return "—";}
    static string ModuleGameVersion(Module module,string installedVersion=null)
    {
        string version=installedVersion;if(String.IsNullOrWhiteSpace(version)||version=="—")version=BundledVersion(module.Id);
        System.Version parsed;if(!System.Version.TryParse(version,out parsed))return "Не указана";
        string numeric=parsed.Major+"."+parsed.Minor+"."+Math.Max(0,parsed.Build);
        if(module.Id=="russian"){if(numeric=="0.2.4"||numeric=="0.2.5"||numeric=="0.2.6")return "1.0.2";if(new[]{"0.2.0","0.2.1","0.2.2","0.2.3"}.Contains(numeric))return "1.0.1";}
        if(module.Id=="wiki"){if(numeric=="0.2.1")return "1.0.2 · WIP";if(numeric=="0.2.0")return "1.0.1 · WIP";}
        return "Не указана";
    }
    static string Available(Module m){return m.Resource != null ? BundledVersion(m.Id) + " (в EXE)" : m.Id=="together"?(KnownTogetherVersion??"Проверить fullgame-релиз"):"1.0.2, совместимость не подтверждена";}
    static string Source(Module m){return m.Id=="together"?"https://github.com/creaturefeaturelarry/casualties-together/releases":m.Id=="unlock"?"https://www.nexusmods.com/scavprototype/mods/884":"bundled";}
    static string ScanText(string root){return Json.Serialize(VisibleModules(root).Select(m=>new{module=m.Id,available=Available(m),source=Source(m),compatibility=m.Id=="unlock"?"unverified":m.Id=="together"?"read release notes":"game 1.0.2",managed=Load(root,m)!=null,disabled=Load(root,m)!=null&&Load(root,m).Disabled,version=VersionOf(root,m,Load(root,m)),detected=Detect(root,m)}).ToArray());}
    // Each operation journals every touched byte, including the manifest.
    // A failed restore must not prevent restoration of the remaining files.
    static void Transaction(string root, Dictionary<string, byte[]> writes, List<string> deletes, string label)
    {
        string backupRel = State + "backups/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + label + "/";
        string journal = Under(root, backupRel + "transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journal));
        var before = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var paths = writes.Keys.Concat(deletes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (string relative in paths)
        {
            string full = Under(root, relative); before[relative] = File.Exists(full) ? File.ReadAllBytes(full) : null;
            if (before[relative] != null) { string copy = Under(root, backupRel + "files/" + relative); Directory.CreateDirectory(Path.GetDirectoryName(copy)); File.WriteAllBytes(copy, before[relative]); }
        }
        File.WriteAllText(journal, Json.Serialize(new { status = "prepared", paths = paths, absent = before.Where(p => p.Value == null).Select(p => p.Key).ToArray() }));
        try
        {
            foreach (var entry in writes) { string full = Under(root, entry.Key); Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllBytes(full, entry.Value); }
            foreach (string relative in deletes) if (!writes.ContainsKey(relative)) { string full = Under(root, relative); if (File.Exists(full)) File.Delete(full); }
            File.WriteAllText(journal, Json.Serialize(new { status = "committed", paths = paths }));
        }
        catch (Exception original)
        {
            List<Exception> failures = RestoreBefore(root, before);
            try { File.WriteAllText(journal, Json.Serialize(new { status = failures.Count == 0 ? "rolled_back" : "rollback_incomplete", paths = paths, errors = failures.Select(e => e.Message).ToArray() })); }
            catch (Exception journalError) { failures.Add(new IOException("Не удалось записать результат отката: " + journal, journalError)); }
            if (failures.Count > 0)
            {
                failures.Insert(0, original);
                throw new IOException("Операция не выполнена: " + original.Message + ". Откат или запись его результата не завершены. Резервная копия: " + Path.GetDirectoryName(journal), new AggregateException(failures));
            }
            throw;
        }
        LastBackup = backupRel; Log(label + "; backup: " + backupRel);
    }
    static List<Exception> RestoreBefore(string root, Dictionary<string, byte[]> before)
    {
        var failures = new List<Exception>();
        foreach (var entry in before)
        {
            try
            {
                string full = Under(root, entry.Key);
                if (entry.Value == null) { if (File.Exists(full)) File.Delete(full); }
                else { Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllBytes(full, entry.Value); }
            }
            catch (Exception error) { failures.Add(new IOException("Не удалось восстановить «" + entry.Key + "».", error)); }
        }
        return failures;
    }
    static void RequireGameVersion(string root){string path=Under(root,"CasualtiesUnknown_Data/Lang/EN.json");if(!File.Exists(path))throw new Exception("Не удалось определить версию игры: отсутствует CasualtiesUnknown_Data/Lang/EN.json. Перевод и справочник рассчитаны на игру 1.0.2.");Dictionary<string,object> lang;try{lang=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));}catch{throw new Exception("Не удалось прочитать версию игры из Lang/EN.json. Файлы модулей не изменены.");}object version;if(lang==null||!lang.TryGetValue("version",out version)||Convert.ToString(version)!="1.0.2")throw new Exception("Эта сборка перевода и справочника поддерживает только Casualties: Unknown 1.0.2. Найдена другая или неизвестная версия; файлы модулей не изменены.");}
    static void Install(string root,Module m,Dictionary<string,byte[]> files,string version){if(m.Id=="unlock")throw new InvalidOperationException("Character Unlock удалён из встроенных модулей; установка через эту карточку недоступна.");if(m.Id=="russian"||m.Id=="wiki")RequireGameVersion(root);Manifest old=Load(root,m);CheckOwned(root,old);if((m.Id=="russian"||m.Id=="wiki")&&File.Exists(Under(root,"BepInEx/plugins/RuFieldGuide/RuFieldGuide.dll")))throw new Exception("Обнаружен прежний объединённый RuFieldGuide. Удалите его предыдущим установщиком перед установкой отдельных модулей. Журнал открытий будет сохранён.");if(old!=null&&old.Disabled)throw new Exception("Сначала включите модуль перед обновлением.");if(m.Id!="bepinex"&&!File.Exists(Under(root,"BepInEx/core/BepInEx.dll")))throw new Exception("Сначала установите BepInEx отдельным модулем.");if(old==null&&Detect(root,m).Count>0)throw new Exception("Обнаружена ручная установка. Проверьте список и нажмите «Управлять модом…» перед обновлением.");var oldFiles=old==null?new List<Item>():old.Files;foreach(string rel in files.Keys){Under(root,rel);if(File.Exists(Under(root,rel))&&!oldFiles.Any(f=>f.Path.Equals(rel,StringComparison.OrdinalIgnoreCase)))throw new Exception("Файл не принадлежит модулю: "+rel+". Он сохранён без изменений.");foreach(Module other in AllModules.Where(x=>x.Id!=m.Id)){Manifest om=Load(root,other);if(om!=null&&om.Files.Any(f=>f.Path.Equals(rel,StringComparison.OrdinalIgnoreCase)))throw new Exception("Файл принадлежит модулю "+other.Name+": "+rel);}}var next=new Manifest{Module=m.Id,Version=version,Files=files.Select(kv=>new Item{Path=kv.Key,Hash=Hash(kv.Value)}).ToList()};var writes=new Dictionary<string,byte[]>(files,StringComparer.OrdinalIgnoreCase);writes.Add(State+m.Id+".json",Encoding.UTF8.GetBytes(Json.Serialize(next)));Transaction(root,writes,oldFiles.Where(f=>!files.ContainsKey(f.Path)).Select(f=>f.Path).ToList(),"install-"+m.Id);}
    static void Remove(string root,Module m){Manifest old=Load(root,m);if(old==null)throw new Exception("Ручная установка не удаляется автоматически.");CheckOwned(root,old);if(m.Id=="bepinex"){string dir=Under(root,"BepInEx/plugins");string patchers=Under(root,"BepInEx/patchers");if((Directory.Exists(dir)&&EnumerateSafe(root,dir).Any(f=>f.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||f.EndsWith(".cumm-disabled",StringComparison.OrdinalIgnoreCase)))||(Directory.Exists(patchers)&&EnumerateSafe(root,patchers).Any(f=>f.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||f.EndsWith(".cumm-disabled",StringComparison.OrdinalIgnoreCase))))throw new Exception("Сначала удалите моды: загрузчик используется plugins или patchers.");}var deletes=old.Files.Select(f=>Actual(old,f)).ToList();deletes.Add(State+m.Id+".json");Transaction(root,new Dictionary<string,byte[]>(),deletes,"remove-"+m.Id);}
    static void Toggle(string root,Module m,bool disabled){if(m.Id=="bepinex")throw new Exception("Общий загрузчик нельзя отключать этим способом.");Manifest old=Load(root,m);if(old==null)throw new Exception("Модуль не управляется менеджером.");CheckOwned(root,old);if(old.Disabled==disabled)return;var writes=new Dictionary<string,byte[]>();var deletes=new List<string>();foreach(Item f in old.Files.Where(f=>f.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))){string source=Actual(old,f),dest=f.Path+(disabled?".cumm-disabled":"");if(File.Exists(Under(root,dest)))throw new Exception("Путь уже занят: "+dest);writes.Add(dest,File.ReadAllBytes(Under(root,source)));deletes.Add(source);}old.Disabled=disabled;writes.Add(State+m.Id+".json",Encoding.UTF8.GetBytes(Json.Serialize(old)));Transaction(root,writes,deletes,(disabled?"disable-":"enable-")+m.Id);}
    static void Adopt(string root,Module m){if(Load(root,m)!=null)throw new Exception("Модуль уже управляется менеджером.");var files=Detect(root,m);if(files.Count==0)throw new Exception("Файлы не найдены.");var mf=new Manifest{Module=m.Id,Version="adopted",Files=files.Select(p=>new Item{Path=p,Hash=Hash(Under(root,p))}).ToList()};foreach(Module other in AllModules.Where(x=>x.Id!=m.Id)){var om=Load(root,other);if(om!=null&&om.Files.Any(f=>files.Contains(f.Path,StringComparer.OrdinalIgnoreCase)))throw new Exception("Некоторые файлы уже принадлежат другому модулю.");}var writes=files.ToDictionary(p=>p,p=>File.ReadAllBytes(Under(root,p)));writes.Add(State+m.Id+".json",Encoding.UTF8.GetBytes(Json.Serialize(mf)));Transaction(root,writes,new List<string>(),"adopt-"+m.Id);}
    static Uri ValidateTogetherUrl(string text, bool metadata, bool initial)
    {
        Uri url;
        if (!Uri.TryCreate(text, UriKind.Absolute, out url) || url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Fragment.Length != 0) throw new InvalidDataException("Неожиданный HTTPS-адрес Together.");
        if (metadata)
        {
            if (url.Host != "api.github.com" || url.AbsolutePath != "/repos/creaturefeaturelarry/casualties-together/releases/latest" || url.Query.Length != 0) throw new InvalidDataException("Неожиданный адрес метаданных Together.");
        }
        else if (url.Host == "github.com")
        {
            string[] parts = Uri.UnescapeDataString(url.AbsolutePath).Split('/');
            if (url.Query.Length != 0 || parts.Length != 7 || parts[1] != "creaturefeaturelarry" || parts[2] != "casualties-together" || parts[3] != "releases" || parts[4] != "download" || String.IsNullOrWhiteSpace(parts[5]) || parts[5] == "." || parts[5] == ".." || !parts[6].EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Архив Together должен находиться в официальном GitHub Releases.");
        }
        else if (initial || (url.Host != "release-assets.githubusercontent.com" && url.Host != "objects.githubusercontent.com")) throw new InvalidDataException("Неожиданный сервер загрузки Together.");
        string path = Uri.UnescapeDataString(url.AbsolutePath);
        if (path.IndexOf('\\') >= 0 || path.Split('/').Any(p => p == "." || p == "..")) throw new InvalidDataException("Некорректный путь загрузки Together.");
        return url;
    }
    static byte[] DownloadTogether(string text, int maximumBytes, bool metadata)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        Uri current = ValidateTogetherUrl(text, metadata, true);
        var timer = Stopwatch.StartNew();
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            ValidateTogetherUrl(current.AbsoluteUri, metadata, redirects == 0);
            int remaining = TogetherTimeoutMilliseconds - (int)timer.ElapsedMilliseconds;
            if (remaining <= 0) throw new WebException("Загрузка Together превысила 20 секунд.", WebExceptionStatus.Timeout);
            var request = (HttpWebRequest)WebRequest.Create(current);
            request.AllowAutoRedirect = false; request.Timeout = remaining; request.ReadWriteTimeout = remaining; request.KeepAlive = false;
            request.UserAgent = "CasualtiesModManager/" + LauncherVersion;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                int code = (int)response.StatusCode;
                if (code >= 300 && code <= 399)
                {
                    string location = response.Headers["Location"];
                    if (String.IsNullOrEmpty(location) || redirects == 5) throw new InvalidDataException("Некорректное перенаправление Together.");
                    current = new Uri(current, location); continue;
                }
                if (response.StatusCode != HttpStatusCode.OK) throw new IOException("Сервер Together вернул HTTP " + code);
                using (Stream input = response.GetResponseStream()) return ReadTogetherBody(input, response.ContentLength, maximumBytes, timer, TogetherTimeoutMilliseconds);
            }
        }
        throw new IOException("Не удалось загрузить Together.");
    }
    static byte[] ReadTogetherBody(Stream input, long declaredLength, int maximumBytes, Stopwatch timer, int timeoutMilliseconds)
    {
        if (declaredLength > maximumBytes) throw new InvalidDataException("Загрузка Together превышает допустимый размер.");
        using (var memory = new MemoryStream())
        {
            byte[] buffer = new byte[32768]; int total = 0;
            while (true)
            {
                long remaining = timeoutMilliseconds - timer.ElapsedMilliseconds;
                if (remaining <= 0) throw new WebException("Загрузка Together превысила допустимое время.", WebExceptionStatus.Timeout);
                if (input.CanTimeout) input.ReadTimeout = (int)Math.Min(remaining, Int32.MaxValue);
                int read = input.Read(buffer, 0, buffer.Length);
                if (timer.ElapsedMilliseconds >= timeoutMilliseconds) throw new WebException("Загрузка Together превысила допустимое время.", WebExceptionStatus.Timeout);
                if (read == 0) break;
                if (read > maximumBytes - total) throw new InvalidDataException("Загрузка Together превышает допустимый размер.");
                total += read; memory.Write(buffer, 0, read);
            }
            if (total == 0 || (declaredLength >= 0 && total != declaredLength)) throw new InvalidDataException("Сервер Together вернул пустой или неполный файл.");
            return memory.ToArray();
        }
    }
    string OnlineInstall(string root, Module module)
    {
        string source = new UTF8Encoding(false, true).GetString(DownloadTogether("https://api.github.com/repos/creaturefeaturelarry/casualties-together/releases/latest", TogetherMetadataBytes, true));
        var release = Json.Deserialize<Dictionary<string, object>>(source);
        var assets = (System.Collections.ArrayList)release["assets"];
        var candidates = assets.Cast<Dictionary<string, object>>().Where(a => ((string)a["name"]).EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && ((string)a["name"]).IndexOf("fullgame", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (candidates.Count != 1) throw new InvalidDataException("Не удалось однозначно выбрать fullgame ZIP. Откройте релизы и импортируйте нужный ZIP вручную.");
        var asset = candidates[0]; string url = (string)asset["browser_download_url"];
        ValidateTogetherUrl(url, false, true);
        if (MessageBox.Show(this, "Релиз: " + release["tag_name"] + "\r\nАрхив: " + asset["name"] + "\r\n\r\n" + release["body"] + "\r\n\r\nЗагрузить для предварительного просмотра?", "Together: совместимость", MessageBoxButtons.OKCancel) != DialogResult.OK) return null;
        byte[] data = DownloadTogether(url, TogetherArchiveBytes, false);
        if (asset.ContainsKey("digest") && asset["digest"] != null && ((string)asset["digest"]).StartsWith("sha256:") && Hash(data) != ((string)asset["digest"]).Substring(7)) throw new InvalidDataException("SHA-256 архива не совпадает с GitHub.");
        using (var memory = new MemoryStream(data))
        {
            var files = ReadZip(memory, module);
            if (!Preview(files, "Together: установить файлы из " + asset["name"] + "? Встроенный в архив загрузчик пропускается.")) return null;
            Install(root, module, files, (string)release["tag_name"]);
            return "Установлен / обновлён из GitHub, релиз " + release["tag_name"];
        }
    }

}
