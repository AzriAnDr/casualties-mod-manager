using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using System.Threading.Tasks;
using System.Reflection;
using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Diagnostics;
using System.Web.Script.Serialization;

public sealed partial class Manager
{
    private List<ModCatalog.Entry> communityEntries = new List<ModCatalog.Entry>();
    private Dictionary<string,ModCatalog.InstalledInfo> communityDetected = new Dictionary<string,ModCatalog.InstalledInfo>();
    private List<CommunityInstaller.InstalledRecord> managedCommunity = new List<CommunityInstaller.InstalledRecord>();
    private TabControl mainTabs;
    private ListView catalogList, inboxList;
    private TextBox catalogSearch, catalogDescription, inboxFolder;
    private CheckBox showAdult, installedOnly, updatesOnly;
    private Label catalogStatus, catalogHeading, catalogFacts, inboxStatus;
    private Button catalogNexus, catalogGithub, catalogImport, catalogAdopt;
    private Button catalogTagsButton, catalogTagsClear;
    private Label catalogTagsSummary;
    private readonly HashSet<string> selectedCatalogTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private FlowLayoutPanel builtinActions, communityActions;
    private FileSystemWatcher inboxWatcher;
    private System.Windows.Forms.Timer searchTimer, inboxTimer;
    private bool loadingCommunity, scanningInbox, compactCatalogDetails;
    private readonly Dictionary<string,ArchiveInbox.Item> inboxCache = new Dictionary<string,ArchiveInbox.Item>(StringComparer.OrdinalIgnoreCase);
    private sealed class CommunitySelection { public CommunityInstaller.InstalledRecord Record; public ModCatalog.Entry Entry; public ModCatalog.InstalledInfo Info; }
    private sealed class DarkTabs : TabControl
    {
        public DarkTabs(){SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Canvas);
            using(var pen=new Pen(Border)){var area=DisplayRectangle;area.Inflate(1,1);e.Graphics.DrawRectangle(pen,area);}
            for(int i=0;i<TabPages.Count;i++){var rect=GetTabRect(i);using(var brush=new SolidBrush(i==SelectedIndex?Surface:Canvas))e.Graphics.FillRectangle(brush,rect);TextRenderer.DrawText(e.Graphics,TabPages[i].Text,Font,rect,i==SelectedIndex?Accent:Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);}
        }
        protected override void OnSelectedIndexChanged(EventArgs e){base.OnSelectedIndexChanged(e);Invalidate();}
    }

    private void InstallCatalogUi(TableLayoutPanel outer, Control information, FlowLayoutPanel actions)
    {
        builtinActions = actions;
        outer.Controls.Remove(list); outer.Controls.Remove(information); outer.Controls.Remove(actions);
        outer.Controls.Remove(actionStatus); outer.Controls.Remove(log);
        outer.RowStyles.Clear(); outer.RowCount = 4;
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent,100)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainTabs = new DarkTabs { Dock=DockStyle.Fill, Font=Font, DrawMode=TabDrawMode.OwnerDrawFixed, ItemSize=new Size(170,36), SizeMode=TabSizeMode.Fixed, Margin=new Padding(0,0,0,12) };
        mainTabs.DrawItem += delegate(object s, DrawItemEventArgs e) { bool active=e.Index==mainTabs.SelectedIndex; using(var brush=new SolidBrush(active?Surface:Canvas))e.Graphics.FillRectangle(brush,e.Bounds);TextRenderer.DrawText(e.Graphics,mainTabs.TabPages[e.Index].Text,Font,e.Bounds,active?Accent:Muted,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter); };
        var mine = NewPage("Мои моды"); var catalog = NewPage("Каталог"); var downloads = NewPage("Загрузки");var history=NewPage("История действий");
        mainTabs.TabPages.AddRange(new[]{mine,catalog,downloads,history});history.Controls.Add(log);
        var mineLayout = new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=3,BackColor=Canvas };
        mineLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); mineLayout.RowStyles.Add(new RowStyle(SizeType.Percent,100));mineLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));mineLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        list.MinimumSize=new Size(0,145); mineLayout.Controls.Add(list,0,0);mineLayout.Controls.Add(information,0,1);
        var combinedActions = new FlowLayoutPanel { Dock=DockStyle.Fill,AutoSize=true,FlowDirection=FlowDirection.TopDown,WrapContents=false,Margin=Padding.Empty };
        combinedActions.Controls.Add(actions);
        communityActions = new FlowLayoutPanel { AutoSize=true,WrapContents=true,Margin=Padding.Empty,Visible=false };
        var update=MakeButton("Обновить из каталога",205,true);var toggleExtra=MakeButton("Включить / отключить",190,false);var removeExtra=MakeButton("Удалить",100,false);var adoptExtra=MakeButton("Управлять модом…",180,false);
        update.Click+=delegate { var selected=SelectedCommunity();if(selected!=null&&selected.Entry!=null)SelectCatalogEntry(selected.Entry.Id); };
        toggleExtra.Click+=delegate { ManageCommunity("переключение",false); };removeExtra.Click+=delegate { ManageCommunity("удаление",false); };adoptExtra.Click+=delegate { ManageCommunity("подключение управления",true); };
        communityActions.Controls.AddRange(new Control[]{update,toggleExtra,removeExtra,adoptExtra});combinedActions.Controls.Add(communityActions);mineLayout.Controls.Add(combinedActions,0,2);mine.Controls.Add(mineLayout);
        mineLayout.SizeChanged+=delegate{int width=Math.Max(250,mineLayout.ClientSize.Width);actions.MaximumSize=new Size(width,0);actions.Width=width;communityActions.MaximumSize=new Size(width,0);communityActions.Width=width;};
        BuildCatalogPage(catalog);BuildInboxPage(downloads);
        game.TextChanged+=delegate{communityDetected.Clear();ModCatalog.ClearDetectionCache();RenderCatalog();var reload=ReloadCommunity(false);};
        outer.Controls.Add(mainTabs,0,2);outer.Controls.Add(actionStatus,0,3);
        actionStatus.Text="Мои моды — управление установкой. Каталог — поиск новых модов. Загрузки — готовые ZIP с Nexus. Все изменения требуют закрытой игры.";
        Shown+=async delegate { await ReloadCommunity(false);StartInboxWatcher(); };
        FormClosed+=delegate { if(inboxWatcher!=null)inboxWatcher.Dispose();if(searchTimer!=null)searchTimer.Dispose();if(inboxTimer!=null)inboxTimer.Dispose(); };
    }
    private TabPage NewPage(string text){return new TabPage(text){BackColor=Canvas,ForeColor=Ink,Padding=new Padding(12)};}
    private TextBox DarkText(bool multiline){return new TextBox{BackColor=Surface,ForeColor=Ink,BorderStyle=BorderStyle.FixedSingle,Dock=DockStyle.Fill,Multiline=multiline,ReadOnly=multiline,ScrollBars=multiline?ScrollBars.Vertical:ScrollBars.None};}
    private ListView DarkList(params string[] columns)
    {
        var view=new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,MultiSelect=false,HideSelection=false,BackColor=Surface,ForeColor=Ink,BorderStyle=BorderStyle.None,ShowItemToolTips=true,OwnerDraw=true};
        foreach(string c in columns)view.Columns.Add(c);
        view.DrawColumnHeader+=delegate(object sender,DrawListViewColumnHeaderEventArgs e){using(var brush=new SolidBrush(Border))e.Graphics.FillRectangle(brush,e.Bounds);TextRenderer.DrawText(e.Graphics,e.Header.Text,Font,e.Bounds,Ink,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);};
        view.DrawItem+=delegate(object sender,DrawListViewItemEventArgs e){if(view.View!=View.Details)e.DrawDefault=true;};
        view.DrawSubItem+=delegate(object sender,DrawListViewSubItemEventArgs e){using(var brush=new SolidBrush(e.Item.Selected?Color.FromArgb(26,74,73):Surface))e.Graphics.FillRectangle(brush,e.Bounds);var rect=e.Bounds;rect.X+=6;rect.Width-=8;TextRenderer.DrawText(e.Graphics,e.SubItem.Text,Font,rect,Ink,TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.EndEllipsis);};
        bool sizingColumns=false;
        view.SizeChanged+=delegate{if(sizingColumns)return;sizingColumns=true;try{int width=Math.Max(80,view.ClientSize.Width-24);int[] shares=view.Columns.Count==5?new[]{34,18,12,16,20}:view.Columns.Count==4?new[]{42,22,12,24}:null;for(int i=0;i<view.Columns.Count;i++)view.Columns[i].Width=shares==null?width/view.Columns.Count:width*shares[i]/100;}finally{sizingColumns=false;}};
        view.SmallImageList=new ImageList{ImageSize=new Size(1,32)};view.SmallImageList.Images.Add(new Bitmap(1,32));return view;
    }
    private void BuildCatalogPage(TabPage page)
    {
        // Explicit toolbar heights keep autosized flow panels from consuming the list's space.
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Margin=Padding.Empty};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,66));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,76));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,28));
        var filters=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Margin=Padding.Empty};filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,210));filters.RowStyles.Add(new RowStyle(SizeType.Absolute,22));filters.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var searchLabel=new Label{Text="Найти мод · название, автор или описание",AutoSize=true,ForeColor=Muted,Margin=Padding.Empty};filters.Controls.Add(searchLabel,0,0);filters.SetColumnSpan(searchLabel,2);
        catalogSearch=DarkText(false);catalogSearch.Anchor=AnchorStyles.Left|AnchorStyles.Right;catalogSearch.Margin=new Padding(0,0,12,8);var refresh=MakeButton("Обновить каталог",200,false);refresh.Dock=DockStyle.Fill;refresh.Margin=new Padding(0,0,0,8);refresh.Click+=async delegate{await ReloadCommunity(true);};filters.Controls.Add(catalogSearch,0,1);filters.Controls.Add(refresh,1,1);layout.Controls.Add(filters,0,0);
        var filterRows=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=Padding.Empty};filterRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));filterRows.RowStyles.Add(new RowStyle(SizeType.Absolute,46));filterRows.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        var checks=new FlowLayoutPanel{Dock=DockStyle.Fill,WrapContents=false,Margin=Padding.Empty,Padding=Padding.Empty};
        installedOnly=new CheckBox{Text="Установленные",AutoSize=true,ForeColor=Ink,Margin=new Padding(0,12,16,0)};updatesOnly=new CheckBox{Text="Есть обновление",AutoSize=true,ForeColor=Ink,Margin=new Padding(0,12,16,0)};showAdult=new CheckBox{Text="18+",AutoSize=true,ForeColor=Muted,Margin=new Padding(0,12,20,0)};
        catalogTagsButton=MakeButton("Теги: все…",155,false);catalogTagsButton.Margin=new Padding(0,0,8,0);catalogTagsButton.Click+=delegate{ChooseCatalogTags();};catalogTagsClear=MakeButton("Сбросить теги",145,false);catalogTagsClear.Margin=new Padding(0);catalogTagsClear.Click+=delegate{selectedCatalogTags.Clear();RenderCatalog();};
        checks.Controls.AddRange(new Control[]{installedOnly,updatesOnly,showAdult,catalogTagsButton,catalogTagsClear});filterRows.Controls.Add(checks,0,0);
        catalogTagsSummary=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=Muted,Margin=new Padding(0,0,0,6),TextAlign=ContentAlignment.MiddleLeft};filterRows.Controls.Add(catalogTagsSummary,0,1);layout.Controls.Add(filterRows,0,1);UpdateTagFilterCaption();
        var split=new SplitContainer{Name="CatalogContent",Size=new Size(1000,450),Dock=DockStyle.Fill,Margin=Padding.Empty,Orientation=Orientation.Vertical,BackColor=Border,SplitterWidth=10,Panel1MinSize=390,Panel2MinSize=300};split.Panel1.BackColor=Canvas;split.Panel2.BackColor=Canvas;split.SizeChanged+=delegate{if(split.Width>600)split.SplitterDistance=Math.Max(split.Panel1MinSize,Math.Min(split.Width-split.Panel2MinSize-split.SplitterWidth,split.Width*57/100));};
        catalogList=DarkList("Мод","Автор","Мод v.","Игра","Состояние");catalogList.Name="CatalogModList";catalogList.SelectedIndexChanged+=delegate{ShowCatalogDetails();};split.Panel1.Controls.Add(catalogList);
        var detail=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Margin=Padding.Empty,Padding=new Padding(14,0,0,0)};detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));detail.RowStyles.Add(new RowStyle(SizeType.Absolute,36));detail.RowStyles.Add(new RowStyle(SizeType.Absolute,50));detail.RowStyles.Add(new RowStyle(SizeType.Percent,100));detail.RowStyles.Add(new RowStyle(SizeType.Absolute,84));
        catalogHeading=new Label{Text="Выбери мод слева",Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=Accent,Font=new Font(Font.FontFamily,13,FontStyle.Bold)};catalogFacts=new Label{Dock=DockStyle.Fill,AutoEllipsis=true,ForeColor=Muted,Margin=new Padding(0,4,0,4)};compactCatalogDetails=false;detail.SizeChanged+=delegate{catalogHeading.MaximumSize=catalogFacts.MaximumSize=new Size(Math.Max(150,detail.ClientSize.Width-15),0);bool compact=detail.Height<260;if(compact!=compactCatalogDetails){compactCatalogDetails=compact;detail.RowStyles[1].Height=compact?0:50;catalogFacts.Visible=!compact;if(catalogDescription!=null)ShowCatalogDetails();}};catalogDescription=DarkText(true);catalogDescription.Name="CatalogDescription";catalogDescription.Margin=new Padding(0,4,0,8);catalogDescription.Text="Поиск по названию, автору и краткому описанию.\r\nСовместимость с полной игрой и Together проверяйте в описании автора.";
        var buttons=new TableLayoutPanel{Dock=DockStyle.Fill,Height=84,Margin=Padding.Empty,ColumnCount=2,RowCount=2};buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));buttons.RowStyles.Add(new RowStyle(SizeType.Percent,50));buttons.RowStyles.Add(new RowStyle(SizeType.Percent,50));catalogNexus=MakeButton("Скачать на Nexus",170,true);catalogGithub=MakeButton("Скачать с GitHub…",185,false);catalogImport=MakeButton("Установить ZIP…",170,false);catalogAdopt=MakeButton("Управлять копией…",185,false);
        catalogNexus.Click+=delegate{var e=SelectedCatalog();if(e!=null)try{Process.Start(ModCatalog.NexusUrl(e));SetStatus(e.Name+": открыта страница Nexus. Скачайте ZIP; он появится во вкладке «Загрузки».",false);}catch(Exception error){ShowFailure(e.Name+": страница Nexus",error);}};
        catalogGithub.Click+=async delegate{var e=SelectedCatalog();if(e!=null)await DownloadFromGitHub(e);};catalogImport.Click+=delegate{var e=SelectedCatalog();if(e!=null)ChooseAndInstall(e);};catalogAdopt.Click+=delegate{var e=SelectedCatalog();if(e!=null)AdoptDetected(e);};var actionButtons=new[]{catalogNexus,catalogGithub,catalogImport,catalogAdopt};for(int i=0;i<actionButtons.Length;i++){actionButtons[i].Dock=DockStyle.Fill;actionButtons[i].Margin=new Padding(i%2==0?0:4,i<2?0:4,i%2==0?4:0,i<2?4:0);buttons.Controls.Add(actionButtons[i],i%2,i/2);}
        detail.Controls.Add(catalogHeading,0,0);detail.Controls.Add(catalogFacts,0,1);detail.Controls.Add(catalogDescription,0,2);detail.Controls.Add(buttons,0,3);split.Panel2.Controls.Add(detail);layout.Controls.Add(split,0,2);
        catalogStatus=new Label{Text="Загрузка каталога…",ForeColor=Muted,Dock=DockStyle.Fill,AutoEllipsis=true,TextAlign=ContentAlignment.MiddleLeft,Margin=new Padding(0,6,0,0)};layout.Controls.Add(catalogStatus,0,3);page.Controls.Add(layout);
        searchTimer=new System.Windows.Forms.Timer{Interval=220};searchTimer.Tick+=delegate{searchTimer.Stop();RenderCatalog();};catalogSearch.TextChanged+=delegate{searchTimer.Stop();searchTimer.Start();};installedOnly.CheckedChanged+=delegate{RenderCatalog();};updatesOnly.CheckedChanged+=delegate{RenderCatalog();};showAdult.CheckedChanged+=delegate{RenderCatalog();};ShowCatalogDetails();
    }
    private async Task ReloadCommunity(bool force)
    {
        if(loadingCommunity)return;loadingCommunity=true;bool retryChangedFolder=false;catalogStatus.Text="Получаю каталог и проверяю установленные версии…";
        try
        {
            string root=SteamGameLocator.IsGame(game.Text)?Path.GetFullPath(game.Text):null;
            var loaded=await Task.Run(()=>ModCatalog.Load(force));
            var detected=await Task.Run(()=>DetectEntries(root,loaded));
            if(force||KnownTogetherVersion==null)
                try{var release=await Task.Run(()=>ReadGitHubRelease("creaturefeaturelarry","casualties-together"));KnownTogetherVersion=Convert.ToString(release["tag_name"]).TrimStart('v','V');}catch(Exception error){WriteLog("Together: версию GitHub пока не удалось проверить. "+error.Message);}
            if(IsDisposed)return;communityEntries=loaded;communityDetected=detected;
            string currentRoot=SteamGameLocator.IsGame(game.Text)?Path.GetFullPath(game.Text):null;
            if(!String.Equals(root,currentRoot,StringComparison.OrdinalIgnoreCase)){communityDetected.Clear();retryChangedFolder=true;return;}
            RenderCatalog();RefreshList();
            string freshness=ModCatalog.IsStale?"Сохранённая копия; сеть недоступна":"Каталог обновлён";
            catalogStatus.Text=freshness+" · "+communityEntries.Count+" записей · Metadata-generator (MIT) · совместимость не гарантируется";
            if(ModCatalog.IsStale&&ModCatalog.LastError!=null)WriteLog("Каталог: используется резервная копия. "+ModCatalog.LastError);
            await ScanInbox();
        }
        catch(Exception error){catalogStatus.Text="Не удалось получить каталог: "+error.Message;WriteLog(catalogStatus.Text);}
        finally{loadingCommunity=false;if(retryChangedFolder&&!IsDisposed){var retry=ReloadCommunity(false);}}
    }
    private Dictionary<string,ModCatalog.InstalledInfo> DetectEntries(string root,List<ModCatalog.Entry> entries)
    {
        var result=new Dictionary<string,ModCatalog.InstalledInfo>();if(root==null)return result;
        foreach(var e in entries)try{result[e.Id]=ModCatalog.Detect(root,e);}catch{}
        return result;
    }
    private string UpdateText(ModCatalog.Entry e)
    {
        ModCatalog.InstalledInfo info;if(!communityDetected.TryGetValue(e.Id,out info))return "Не установлен";
        if(info.Managed==true&&info.Ambiguous)return "Файлы изменены / отсутствуют";
        if(!info.Installed)return info.Ambiguous?"Найден похожий DLL":"Не установлен";
        if(info.Partial)return "Найдена часть DLL";
        switch(ModCatalog.CompareInstalled(info,e)){case ModCatalog.UpdateState.UpdateAvailable:return "Доступно обновление";case ModCatalog.UpdateState.Current:return "Актуален";case ModCatalog.UpdateState.InstalledNewer:return "Установлена более новая";case ModCatalog.UpdateState.DifferentChannel:return "Другая ветка версии";default:return "Версию нужно проверить";}
    }
    private void RenderCatalog()
    {
        if(catalogList==null)return;string query=catalogSearch.Text.Trim();string selected=SelectedCatalog()==null?null:SelectedCatalog().Id;
        catalogList.BeginUpdate();catalogList.Items.Clear();
        foreach(var e in communityEntries)
        {
            if(!showAdult.Checked&&e.ContainsAdultContent==true)continue;
            if(query.Length>0&&(e.Name+" "+e.Author+" "+e.Summary+" "+e.Id).IndexOf(query,StringComparison.OrdinalIgnoreCase)<0)continue;
            if(!ModCatalog.MatchesTags(e,selectedCatalogTags))continue;
            ModCatalog.InstalledInfo installed;bool present=communityDetected.TryGetValue(e.Id,out installed)&&installed.Installed;
            if(installedOnly.Checked&&!present)continue;if(updatesOnly.Checked&&(!present||ModCatalog.CompareInstalled(installed,e)!=ModCatalog.UpdateState.UpdateAvailable))continue;
            var row=new ListViewItem(new[]{e.Name,e.Author,e.Version,ModCatalog.GameVersionText(e).Replace("Не указана","Не указ."),UpdateText(e)}){Tag=e,ToolTipText="Версия игры: "+ModCatalog.GameVersionText(e)+"\n"+ModCatalog.SummaryText(e,400)};catalogList.Items.Add(row);if(e.Id==selected)row.Selected=true;
        }
        catalogList.EndUpdate();UpdateTagFilterCaption();ShowCatalogDetails();
    }
    private void UpdateTagFilterCaption()
    {
        if(catalogTagsButton==null)return;
        catalogTagsButton.Text=selectedCatalogTags.Count==0?"Теги: все…":"Теги: "+selectedCatalogTags.Count+"…";
        catalogTagsClear.Enabled=selectedCatalogTags.Count>0;
        catalogTagsSummary.Text=selectedCatalogTags.Count==0?"Только теги, указанные авторами в каталоге":"Любой из выбранных: "+String.Join(", ",selectedCatalogTags.OrderBy(t=>t,StringComparer.OrdinalIgnoreCase).ToArray());
    }
    private void ChooseCatalogTags()
    {
        // Keep the pending selection independent of visible/search-filtered rows and of a later reload.
        var pending=new HashSet<string>(selectedCatalogTags,StringComparer.OrdinalIgnoreCase);
        var available=ModCatalog.AvailableTags(communityEntries).Concat(selectedCatalogTags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t=>t,StringComparer.OrdinalIgnoreCase).ToList();
        using(var form=new Form{Text="Фильтр по тегам Nexus",ClientSize=new Size(600,600),MinimumSize=new Size(480,450),StartPosition=FormStartPosition.CenterParent,BackColor=Canvas,ForeColor=Ink,Font=Font})
        {
            var panel=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),ColumnCount=1,RowCount=5};panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.RowStyles.Add(new RowStyle(SizeType.Percent,100));panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var hint=new Label{Text="Показывать моды с любым из выбранных тегов.\nФильтр сочетается с поиском, 18+ и состоянием установки.\nНазвания взяты из каталога Nexus; теги есть не у всех модов.",AutoSize=true,ForeColor=Muted,MaximumSize=new Size(540,0)};
            var search=DarkText(false);search.Margin=new Padding(0,12,0,10);
            var choices=new CheckedListBox{Dock=DockStyle.Fill,BackColor=Surface,ForeColor=Ink,BorderStyle=BorderStyle.FixedSingle,CheckOnClick=true,IntegralHeight=false,HorizontalScrollbar=true};
            var selection=new Label{AutoSize=true,ForeColor=Accent,Margin=new Padding(0,10,0,10)};
            bool rendering=false;
            Action render=delegate
            {
                rendering=true;choices.BeginUpdate();choices.Items.Clear();
                foreach(string tag in available)if(search.Text.Length==0||tag.IndexOf(search.Text.Trim(),StringComparison.OrdinalIgnoreCase)>=0)choices.Items.Add(tag,pending.Contains(tag));
                choices.EndUpdate();rendering=false;selection.Text="Выбрано: "+pending.Count+". Пустой выбор — все моды.";
            };
            choices.ItemCheck+=delegate(object sender,ItemCheckEventArgs e){if(rendering)return;string tag=(string)choices.Items[e.Index];if(e.NewValue==CheckState.Checked)pending.Add(tag);else pending.Remove(tag);selection.Text="Выбрано: "+pending.Count+". Пустой выбор — все моды.";};
            search.TextChanged+=delegate{render();};
            var buttons=new FlowLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};var apply=MakeButton("Применить",150,true);apply.DialogResult=DialogResult.OK;var clear=MakeButton("Снять все",145,false);clear.Click+=delegate{pending.Clear();render();};var cancel=MakeButton("Отмена",115,false);cancel.DialogResult=DialogResult.Cancel;buttons.Controls.AddRange(new Control[]{apply,clear,cancel});
            panel.Controls.Add(hint,0,0);panel.Controls.Add(search,0,1);panel.Controls.Add(choices,0,2);panel.Controls.Add(selection,0,3);panel.Controls.Add(buttons,0,4);form.Controls.Add(panel);form.AcceptButton=apply;form.CancelButton=cancel;render();
            if(form.ShowDialog(this)==DialogResult.OK){selectedCatalogTags.Clear();foreach(string tag in pending)selectedCatalogTags.Add(tag);RenderCatalog();}
        }
    }
    private ModCatalog.Entry SelectedCatalog(){return catalogList==null||catalogList.SelectedItems.Count==0?null:catalogList.SelectedItems[0].Tag as ModCatalog.Entry;}
    private void ShowCatalogDetails()
    {
        if(catalogNexus==null)return;var e=SelectedCatalog();catalogNexus.Enabled=catalogImport.Enabled=e!=null&&!busy;catalogGithub.Enabled=false;catalogAdopt.Enabled=false;
        if(e==null){catalogHeading.Text="Выбери мод слева";catalogFacts.Text="Поищи название или автора. Список работает и без выбранной папки игры.";catalogDescription.Text="Выберите запись, чтобы прочитать описание автора и проверить зависимости.\r\n\r\nДля Nexus скачайте ZIP на сайте. Завершённый архив появится во вкладке «Загрузки»; установка потребует проверки списка файлов.";return;}
        catalogHeading.Text=e.Name;catalogFacts.Text="Автор: "+e.Author+"\nВерсия мода: "+e.Version+" · "+UpdateText(e);
        catalogDescription.Text=(compactCatalogDetails?catalogFacts.Text.Replace("\r\n","\n").Replace("\n","\r\n")+"\r\n\r\n":"")+"Версия игры (по данным автора): "+ModCatalog.GameVersionText(e)+". Совместимость с Together проверяйте у автора.\r\n\r\n"+ModCatalog.PlainText(e.Description,70000);
        if(e.Dependencies!=null&&e.Dependencies.Count>0)catalogDescription.Text="Зависимости: "+String.Join(", ",e.Dependencies.ToArray())+"\r\n\r\n"+catalogDescription.Text;
        if(e.Tags!=null&&e.Tags.Count>0)catalogDescription.Text="Теги Nexus: "+String.Join(", ",e.Tags.ToArray())+"\r\n\r\n"+catalogDescription.Text;
        catalogGithub.Enabled=!busy&&!String.IsNullOrEmpty(ModCatalog.AuthorGitHubUrl(e));
        ModCatalog.InstalledInfo info;catalogAdopt.Enabled=!busy&&communityDetected.TryGetValue(e.Id,out info)&&info.Installed&&!(info.Managed==true)&&!IsBuiltinEntry(e);
    }
    private bool IsBuiltinEntry(ModCatalog.Entry e){return e.DllNames!=null&&e.DllNames.Any(n=>new[]{"CasualtiesMP.dll","CuRussian.dll","CuWiki.dll"}.Contains(Path.GetFileName(n),StringComparer.OrdinalIgnoreCase));}
    private void SelectCatalogEntry(string id){mainTabs.SelectedIndex=1;catalogSearch.Text="";installedOnly.Checked=updatesOnly.Checked=false;var entry=communityEntries.FirstOrDefault(e=>e.Id==id);if(entry!=null&&entry.ContainsAdultContent==true)showAdult.Checked=true;if(entry!=null&&!ModCatalog.MatchesTags(entry,selectedCatalogTags))selectedCatalogTags.Clear();RenderCatalog();foreach(ListViewItem row in catalogList.Items)if(((ModCatalog.Entry)row.Tag).Id==id){row.Selected=true;row.EnsureVisible();break;}}
    private void ChooseAndInstall(ModCatalog.Entry e){if(busy)return;using(var dialog=new OpenFileDialog{Filter="ZIP-архив|*.zip",Title="Архив для «"+e.Name+"»"})if(dialog.ShowDialog(this)==DialogResult.OK)InstallCommunityZip(e,dialog.FileName,e.ArchiveSha256,e.Version);}
    private IEnumerable<string> PrimaryDlls(ModCatalog.Entry e){return e.DllVersions!=null&&e.DllVersions.Count>0?e.DllVersions.Keys:(IEnumerable<string>)(e.DllNames??new List<string>());}
    private void InstallCommunityZip(ModCatalog.Entry e,string file,string hash,string version)
    {
        if(busy)return;
        if(IsBuiltinEntry(e)){MessageBox.Show(this,"Для этого мода используйте его основную карточку во вкладке «Мои моды». Это сохраняет выбранный источник и управление файлами.","Основной мод");return;}
        try
        {
            busy=true;UpdateDetails();ShowCatalogDetails();string root=Validate(game.Text,true);
            if(!EnsureLoaderForCommunity(root,false))return;
            var plan=CommunityInstaller.Preview(root,e.Id,file,hash,PrimaryDlls(e));
            string warning=plan.Warnings.Count>0?"\r\n\r\n"+String.Join("\r\n",plan.Warnings.ToArray()):"";
            if(!Preview(plan.Files,e.Name+"\r\n"+plan.Summary+"\r\nСовместимость с игрой и Together проверяйте у автора."+warning,"Установить файлы")){SetStatus(e.Name+": установка отменена.",false);return;}
            var result=CommunityInstaller.Install(root,plan,e.Name,version);
            SetStatus(e.Name+": установлен / обновлён, "+result.FileCount+" файлов. Резервная копия сохранена.",false);WriteLog(e.Name+": установка из "+Path.GetFileName(file)+"; backup: "+result.BackupPath);ModCatalog.ClearDetectionCache();
        }
        catch(Exception error){ShowFailure(e.Name+": установка ZIP",error);}
        finally{busy=false;RefreshList();ShowCatalogDetails();var refresh=ReloadCommunity(false);}
    }
    private bool EnsureLoaderForCommunity(string root,bool lockHeld)
    {
        if(File.Exists(Under(root,"BepInEx/core/BepInEx.dll")))return true;
        if(MessageBox.Show(this,"Для установки модов нужен BepInEx. Он уже встроен в лаунчер. Установить загрузчик сейчас?","Нужен загрузчик",MessageBoxButtons.YesNo,MessageBoxIcon.Information)!=DialogResult.Yes)return false;
        Module loader=Find("bepinex");
        using(var resource=Resource(loader))
        {
            var files=ReadZip(resource,loader);if(!Preview(files,"Установить BepInEx? Общий загрузчик нужен для модов.","Установить загрузчик"))return false;
            if(lockHeld)Install(root,loader,files,"bundled");else using(var operationLock=LockGame(root))Install(root,loader,files,"bundled");
        }
        WriteLog("BepInEx: установлен как зависимость; резервная копия: "+LastBackup);return true;
    }
    private void AppendCommunityRows(string root)
    {
        if(communityActions==null)return;
        managedCommunity=CommunityInstaller.ListInstalled(root);var ids=new HashSet<string>();
        foreach(var record in managedCommunity)
        {
            ids.Add(record.Id);var entry=communityEntries.FirstOrDefault(e=>e.Id==record.Id);ModCatalog.InstalledInfo info=null;if(entry!=null)communityDetected.TryGetValue(entry.Id,out info);
            var selection=new CommunitySelection{Record=record,Entry=entry,Info=info};
            list.Items.Add(new ListViewItem(new[]{record.Name,record.Disabled?"Отключён":"Из каталога · под управлением",record.Version+" (пакет)",entry==null?"Нужен каталог":UpdateText(entry),InstalledGameVersion(entry,info,record.Version)}){Tag=selection});
        }
        foreach(var e in communityEntries)
        {
            ModCatalog.InstalledInfo info;if(ids.Contains(e.Id)||IsBuiltinEntry(e)||!communityDetected.TryGetValue(e.Id,out info)||!info.Installed)continue;
            list.Items.Add(new ListViewItem(new[]{e.Name,"Из каталога · вручную",info.Version,UpdateText(e),InstalledGameVersion(e,info,null)}){Tag=new CommunitySelection{Entry=e,Info=info}});
        }
    }
    private string InstalledGameVersion(ModCatalog.Entry entry,ModCatalog.InstalledInfo info,string archiveVersion)
    {
        if(entry==null)return "Не указана";
        bool matches=info!=null&&ModCatalog.CompareInstalled(info,entry)==ModCatalog.UpdateState.Current;
        if(!matches&&archiveVersion!=null)matches=ModCatalog.CompareVersions(archiveVersion,entry.Version)==ModCatalog.VersionComparison.Equal;
        return matches?ModCatalog.GameVersionText(entry):"Не указана";
    }
    private CommunitySelection SelectedCommunity(){return list.SelectedItems.Count==0?null:list.SelectedItems[0].Tag as CommunitySelection;}
    private bool ShowCommunitySelection()
    {
        if(communityActions==null)return false;var selected=SelectedCommunity();bool extra=selected!=null;communityActions.Visible=extra;builtinActions.Visible=!extra;if(!extra)return false;
        details.Text=selected.Entry!=null?ModCatalog.SummaryText(selected.Entry,1200):selected.Record.Name;
        context.Text=selected.Record!=null?"Управление включено. "+selected.Record.Files.Count+" файлов. Отключение и удаление сохраняют игровые настройки.":"Найдена ручная установка по хешу DLL. Подключите управление после проверки списка файлов.";
        communityActions.Controls[0].Enabled=!busy&&selected.Entry!=null;communityActions.Controls[1].Enabled=communityActions.Controls[2].Enabled=!busy&&selected.Record!=null;communityActions.Controls[3].Enabled=!busy&&selected.Record==null;return true;
    }
    private void ManageCommunity(string operation,bool adopt)
    {
        var selected=SelectedCommunity();if(selected==null||busy)return;
        if(adopt){if(selected.Entry!=null)AdoptDetected(selected.Entry);return;}
        var record=selected.Record;if(record==null)return;string displayName=record.Name;
        try
        {
            busy=true;UpdateDetails();string root=Validate(game.Text,true);
            record=CommunityInstaller.ListInstalled(root).FirstOrDefault(r=>r.Id==record.Id);
            if(record==null)throw new Exception("В выбранной папке этот управляемый мод не найден. Обновите список.");
            if(operation=="удаление")
            {
                if(!Preview(record.Files.ToDictionary(f=>f.Path,f=>new byte[0]),"Удалить «"+record.Name+"»? Сохранения и конфиги не удаляются.","Удалить файлы")){SetStatus(record.Name+": удаление отменено.",false);return;}
                CommunityInstaller.Remove(root,record.Id);SetStatus(record.Name+": удалён; настройки сохранены.",false);
            }
            else {CommunityInstaller.SetEnabled(root,record.Id,record.Disabled);SetStatus(record.Name+(record.Disabled?": включён.":": отключён."),false);}
            WriteLog(record.Name+": "+operation);ModCatalog.ClearDetectionCache();
        }
        catch(Exception error){ShowFailure(displayName+": "+operation,error);}
        finally{busy=false;RefreshList();var refresh=ReloadCommunity(false);}
    }
    private void AdoptDetected(ModCatalog.Entry entry)
    {
        if(busy)return;
        try
        {
            busy=true;UpdateDetails();ShowCatalogDetails();string root=Validate(game.Text,true);
            ModCatalog.ClearDetectionCache();var info=ModCatalog.Detect(root,entry);
            if(!info.Installed||info.Ambiguous||info.Managed==true)throw new Exception("Состав установки изменился. Обновите каталог и ещё раз выберите мод.");
            if(!Preview(info.Files.ToDictionary(f=>f,f=>new byte[0]),"Подключить управление «"+entry.Name+"»? Найденные DLL будут сохранены в резервную копию. Другие файлы и настройки не принимаются под управление.","Подключить управление")){SetStatus(entry.Name+": подключение управления отменено.",false);return;}
            ModCatalog.ClearDetectionCache();var current=ModCatalog.Detect(root,entry);
            if(!current.Installed||current.Ambiguous||!current.Files.OrderBy(f=>f).SequenceEqual(info.Files.OrderBy(f=>f)))throw new Exception("Файлы изменились во время подтверждения. Обновите список и повторите.");
            CommunityInstaller.Adopt(root,entry.Id,entry.Name,info.Version,info.Files);SetStatus(entry.Name+": управление включено; копия сохранена.",false);ModCatalog.ClearDetectionCache();RefreshList();var refresh=ReloadCommunity(false);
        }
        catch(Exception error){ShowFailure(entry.Name+": подключение управления",error);}
        finally{busy=false;UpdateDetails();ShowCatalogDetails();var refresh=ReloadCommunity(false);}
    }
    private void BuildInboxPage(TabPage page)
    {
        var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,RowCount=4};layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var folder=new TableLayoutPanel{Dock=DockStyle.Fill,AutoSize=true,ColumnCount=3};folder.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));folder.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));folder.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));inboxFolder=DarkText(false);inboxFolder.Text=DownloadsFolder();inboxFolder.Margin=new Padding(0,8,10,8);var choose=MakeButton("Папка ZIP…",135,false);var refresh=MakeButton("Проверить",125,false);choose.Click+=delegate{using(var d=new FolderBrowserDialog())if(d.ShowDialog(this)==DialogResult.OK){inboxFolder.Text=d.SelectedPath;Directory.CreateDirectory(SettingsDir);File.WriteAllText(Path.Combine(SettingsDir,"downloads-path.txt"),d.SelectedPath);StartInboxWatcher();}};refresh.Click+=async delegate{await ScanInbox();};folder.Controls.Add(inboxFolder,0,0);folder.Controls.Add(choose,1,0);folder.Controls.Add(refresh,2,0);layout.Controls.Add(folder,0,0);
        inboxList=DarkList("ZIP-архив","Мод","Состояние");layout.Controls.Add(inboxList,0,1);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Fill,AutoSize=true};var installZip=MakeButton("Установить выбранный ZIP",245,true);installZip.Click+=delegate{if(inboxList.SelectedItems.Count==0)return;var item=(ArchiveInbox.Item)inboxList.SelectedItems[0].Tag;if(item.Mod==null){MessageBox.Show(this,"Не удалось однозначно определить мод. Найдите его в каталоге и нажмите «Установить ZIP…».","Выберите мод");return;}InstallCommunityZip(item.Mod,item.Path,item.Mod.ArchiveSha256,item.Mod.Version);};var open=MakeButton("Открыть папку",165,false);open.Click+=delegate{if(Directory.Exists(inboxFolder.Text))Process.Start(inboxFolder.Text);};buttons.Controls.AddRange(new Control[]{installZip,open});layout.Controls.Add(buttons,0,2);
        inboxStatus=new Label{AutoSize=true,ForeColor=Muted,Text="Здесь появляются завершённые ZIP-загрузки. Установка только после подтверждения. Каталог должен быть загружен."};layout.Controls.Add(inboxStatus,0,3);page.Controls.Add(layout);
        inboxTimer=new System.Windows.Forms.Timer{Interval=3000};inboxTimer.Tick+=async delegate{inboxTimer.Stop();await ScanInbox();};
    }
    private string DownloadsFolder()
    {
        try{string settings=Path.Combine(SettingsDir,"downloads-path.txt");if(File.Exists(settings)&&Directory.Exists(File.ReadAllText(settings)))return File.ReadAllText(settings);
            using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders")){string value=key==null?null:key.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;if(value!=null)return Environment.ExpandEnvironmentVariables(value);}}
        catch{}return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
    }
    private void StartInboxWatcher()
    {
        if(inboxWatcher!=null){inboxWatcher.Dispose();inboxWatcher=null;}
        try
        {
            if(!Directory.Exists(inboxFolder.Text))return;inboxWatcher=new FileSystemWatcher(inboxFolder.Text,"*.zip"){IncludeSubdirectories=false,NotifyFilter=NotifyFilters.FileName|NotifyFilters.Size|NotifyFilters.LastWrite};
            FileSystemEventHandler changed=delegate{if(IsDisposed||!IsHandleCreated)return;try{BeginInvoke((Action)delegate{inboxTimer.Stop();inboxTimer.Start();});}catch{}};
            inboxWatcher.Created+=changed;inboxWatcher.Changed+=changed;inboxWatcher.Renamed+=delegate(object s,RenamedEventArgs e){changed(s,e);};inboxWatcher.EnableRaisingEvents=true;inboxTimer.Start();
        }
        catch(Exception error){inboxStatus.Text="Не удалось следить за папкой: "+error.Message;}
    }
    private async Task ScanInbox()
    {
        if(scanningInbox||inboxList==null||!Directory.Exists(inboxFolder.Text))return;scanningInbox=true;
        try
        {
            string folder=inboxFolder.Text;var entries=communityEntries.ToList();var items=await Task.Run(()=>ArchiveInbox.Scan(folder,entries,inboxCache));if(IsDisposed)return;
            inboxList.Items.Clear();foreach(var item in items)inboxList.Items.Add(new ListViewItem(new[]{Path.GetFileName(item.Path),item.Mod==null?"Определите через каталог":item.Mod.Name,item.State}){Tag=item});
            inboxStatus.Text="Проверены последние "+items.Count+" ZIP. Новые загрузки подхватываются автоматически; старый архив можно выбрать через каталог.";
            if(items.Any(item=>item.State=="Загрузка ещё не завершена"))inboxTimer.Start();
        }
        catch(Exception error){inboxStatus.Text="Проверка загрузок: "+error.Message;}
        finally{scanningInbox=false;}
    }
    private async Task DownloadFromGitHub(ModCatalog.Entry entry)
    {
        if(busy)return;string repository=ModCatalog.AuthorGitHubUrl(entry);if(String.IsNullOrEmpty(repository))return;
        try
        {
            busy=true;ShowCatalogDetails();SetStatus(entry.Name+": получаю выпуск GitHub…",false);
            var repo=new Uri(repository).AbsolutePath.Trim('/').Split('/');if(repo.Length!=2)throw new Exception("Не удалось определить репозиторий автора.");
            var release=await Task.Run(()=>ReadGitHubRelease(repo[0],repo[1]));
            var rawAssets=release["assets"] as System.Collections.ArrayList;
            var archives=rawAssets.Cast<Dictionary<string,object>>().Where(a=>((string)a["name"]).EndsWith(".zip",StringComparison.OrdinalIgnoreCase)).ToList();
            if(archives.Count==0)throw new Exception("В последнем выпуске нет ZIP. Откройте страницу автора.");
            var asset=ChooseGitHubAsset(archives,Convert.ToString(release["tag_name"]));if(asset==null){SetStatus(entry.Name+": загрузка отменена.",false);return;}
            string url=Convert.ToString(asset["browser_download_url"]);if(!url.StartsWith("https://github.com/"+repo[0]+"/"+repo[1]+"/releases/download/",StringComparison.OrdinalIgnoreCase))throw new Exception("Неожиданный адрес архива.");
            string file=Path.Combine(SettingsDir,"downloads",Guid.NewGuid().ToString("N")+".zip");Directory.CreateDirectory(Path.GetDirectoryName(file));
            await Task.Run(()=>DownloadZip(url,file));string hash=null;if(asset.ContainsKey("digest")&&Convert.ToString(asset["digest"]).StartsWith("sha256:"))hash=Convert.ToString(asset["digest"]).Substring(7);
            busy=false;InstallCommunityZip(entry,file,hash,Convert.ToString(release["tag_name"]));
        }
        catch(Exception error){ShowFailure(entry.Name+": GitHub",error);}
        finally{busy=false;ShowCatalogDetails();UpdateDetails();}
    }
    private Dictionary<string,object> ReadGitHubRelease(string owner,string repository)
    {
        string url="https://api.github.com/repos/"+Uri.EscapeDataString(owner)+"/"+Uri.EscapeDataString(repository)+"/releases/latest";
        using(var data=new MemoryStream()){FetchGitHub(url,data,2*1024*1024,20000,false);return new JavaScriptSerializer{MaxJsonLength=2*1024*1024,RecursionLimit=32}.Deserialize<Dictionary<string,object>>(new UTF8Encoding(false,true).GetString(data.ToArray()));}
    }
    private Dictionary<string,object> ChooseGitHubAsset(List<Dictionary<string,object>> assets,string version)
    {
        using(var form=new Form{Text="Выберите ZIP для игры · "+version,ClientSize=new Size(670,350),StartPosition=FormStartPosition.CenterParent,BackColor=Canvas,ForeColor=Ink,Font=Font})
        {
            var layout=new TableLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(18),RowCount=3,ColumnCount=1};layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.Controls.Add(new Label{Text="Проверьте версию игры в имени файла. ZIP из GitHub может отличаться от версии Nexus.",AutoSize=true,MaximumSize=new Size(620,0)},0,0);
            var choices=new ListBox{Dock=DockStyle.Fill,BackColor=Surface,ForeColor=Ink};foreach(var asset in assets)choices.Items.Add(Convert.ToString(asset["name"]));choices.SelectedIndex=0;layout.Controls.Add(choices,0,1);
            var buttons=new FlowLayoutPanel{AutoSize=true};var ok=MakeButton("Скачать выбранный",210,true);ok.DialogResult=DialogResult.OK;var cancel=MakeButton("Отмена",100,false);cancel.DialogResult=DialogResult.Cancel;buttons.Controls.AddRange(new Control[]{ok,cancel});layout.Controls.Add(buttons,0,2);form.Controls.Add(layout);form.CancelButton=cancel;
            return form.ShowDialog(this)==DialogResult.OK?assets[choices.SelectedIndex]:null;
        }
    }
    private void DownloadZip(string url,string file)
    {
        string partial=file+".partial";bool created=false;
        try
        {
            for(var directory=new DirectoryInfo(Path.GetDirectoryName(file));directory!=null;directory=directory.Parent)if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка загрузок содержит ссылку или junction.");
            using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;FetchGitHub(url,output,128*1024*1024,120000,true);}
            File.Move(partial,file);
        }
        catch{try{if(created&&File.Exists(partial))File.Delete(partial);}catch{}throw;}
    }
    private static void FetchGitHub(string url,Stream output,int limit,int deadlineMs,bool archive)
    {
        ServicePointManager.SecurityProtocol=(SecurityProtocolType)3072;
        var timer=Stopwatch.StartNew();Uri current=new Uri(url);
        for(int redirect=0;redirect<=5;redirect++)
        {
            string host=current.DnsSafeHost.ToLowerInvariant();
            bool allowed=archive?(host=="github.com"||redirect>0&&(host=="release-assets.githubusercontent.com"||host=="objects.githubusercontent.com")):host=="api.github.com";
            if(!allowed||current.Scheme!="https"||!current.IsDefaultPort||current.UserInfo.Length!=0||current.Fragment.Length!=0)throw new InvalidDataException("Неожиданный адрес сервера GitHub.");
            int remaining=deadlineMs-(int)timer.ElapsedMilliseconds;if(remaining<=0)throw new WebException("Истекло время загрузки GitHub.",WebExceptionStatus.Timeout);
            var request=(HttpWebRequest)WebRequest.Create(current);request.UserAgent="CasualtiesModManager/0.5.3";request.AllowAutoRedirect=false;request.Timeout=request.ReadWriteTimeout=remaining;request.KeepAlive=false;
            using(var response=(HttpWebResponse)request.GetResponse())
            {
                int status=(int)response.StatusCode;if(status>=300&&status<400){string location=response.Headers["Location"];if(redirect==5||String.IsNullOrWhiteSpace(location))throw new InvalidDataException("Некорректное перенаправление GitHub.");current=new Uri(current,location);continue;}
                if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("GitHub вернул HTTP "+status);
                if(response.ContentLength>limit)throw new InvalidDataException("Ответ GitHub превышает допустимый размер.");
                long total=0;byte[] buffer=new byte[32768];using(var input=response.GetResponseStream())while(true)
                {
                    remaining=deadlineMs-(int)timer.ElapsedMilliseconds;if(remaining<=0)throw new WebException("Истекло время загрузки GitHub.",WebExceptionStatus.Timeout);if(input.CanTimeout)input.ReadTimeout=remaining;
                    int count=input.Read(buffer,0,buffer.Length);if(count==0)break;total+=count;if(total>limit)throw new InvalidDataException("Ответ GitHub превышает допустимый размер.");output.Write(buffer,0,count);
                }
                if(total==0)throw new InvalidDataException("GitHub вернул пустой файл.");return;
            }
        }
        throw new IOException("Слишком много перенаправлений GitHub.");
    }
}
