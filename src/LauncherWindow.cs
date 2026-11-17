using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Backend = Manager.LauncherBackend;

// Every visible control belongs to WPF. No Manager instance or WinForms host is created.
public sealed class LauncherWindow : Window
{
    static readonly Brush Canvas=ColorBrush(14,22,29), Surface=ColorBrush(23,35,44), Raised=ColorBrush(31,47,58), Edge=ColorBrush(49,69,80), Ink=ColorBrush(228,237,240), Muted=ColorBrush(155,178,187), Teal=ColorBrush(66,209,183);
    const string Feed="https://github.com/AzriAnDr/casualties-mod-manager/releases/latest/download/update.json";
    readonly ObservableCollection<Backend.Row> mine=new ObservableCollection<Backend.Row>();
    readonly ObservableCollection<CatalogRow> catalog=new ObservableCollection<CatalogRow>();
    readonly ObservableCollection<InboxRow> inbox=new ObservableCollection<InboxRow>();
    readonly HashSet<string> tags=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,ArchiveInbox.Item> inboxCache=new Dictionary<string,ArchiveInbox.Item>(StringComparer.OrdinalIgnoreCase);
    List<ModCatalog.Entry> entries=new List<ModCatalog.Entry>();
    Dictionary<string,ModCatalog.InstalledInfo> detected=new Dictionary<string,ModCatalog.InstalledInfo>();
    TextBox game,search,folder,history,mineDescription,catalogDescription;
    TextBlock status,catalogCount,mineHeading,mineFacts,catalogHeading,catalogFacts,tagSummary,inboxFacts;
    CheckBox adult,installedOnly,updatesOnly,showUnknown;
    DataGrid mineGrid,catalogGrid,inboxGrid;
    TabControl tabs;
    Button chooseGame,modulesButton,launcherUpdate,installMine,importMine,removeMine,toggleMine,adoptMine,sourceMine,nexusButton,githubButton,zipButton,adoptCatalog,inboxInstall;
    readonly List<Button> changes=new List<Button>();
    DispatcherTimer rootTimer,inboxTimer;
    FileSystemWatcher watcher;
    bool busy,mutating,closed,initializing=true,scanBusy;
    long generation,inboxGeneration;
    public sealed class CatalogRow
    {
        public ModCatalog.Entry Entry; public ModCatalog.InstalledInfo Info;
        public string Name{get{return Entry.Name;}}public string Author{get{return Entry.Author;}}public string Version{get{return Entry.Version;}}
        public string State{get{return InstalledText(Info,Entry);}}public string GameVersion{get{return ModCatalog.GameVersionText(Entry);}}
    }
    public sealed class InboxRow
    {
        public ArchiveInbox.Item Item;
        public string Name{get{return Path.GetFileName(Item.Path);}}public string Mod{get{return Item.Mod==null?Item.DisplayName:Item.Mod.Name;}}public string State{get{return Item.State;}}
    }
    sealed class Snapshot
    {
        public List<ModCatalog.Entry> Entries;public List<Backend.Row> Rows;public Dictionary<string,ModCatalog.InstalledInfo> Detected;public string History,Problem;
    }
    public static int Run(){var app=Application.Current??new Application();app.ShutdownMode=ShutdownMode.OnMainWindowClose;return app.Run(new LauncherWindow());}
    public LauncherWindow()
    {
        Title="Casualties: Unknown · Менеджер модов "+VersionText();
        var work=SystemParameters.WorkArea;Width=Math.Min(1260,work.Width-40);Height=Math.Min(900,work.Height-40);MinWidth=Math.Min(940,Width);MinHeight=Math.Min(650,Height);WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Theme(this);FontFamily=new FontFamily("Segoe UI");FontSize=14;
        try{using(var icon=Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIcon.ico"))if(icon!=null)Icon=BitmapFrame.Create(icon,BitmapCreateOptions.None,BitmapCacheOption.OnLoad);}catch{}
        var outer=Rows(GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto);outer.Margin=new Thickness(20);Content=outer;
        var header=new StackPanel();header.Children.Add(new TextBlock{Text="CASUALTIES : UNKNOWN",Foreground=Teal,FontSize=25,FontWeight=FontWeights.SemiBold});header.Children.Add(new TextBlock{Text="Моды, переводы и совместная игра",Foreground=Muted,Margin=new Thickness(0,3,0,12)});
        var path=new Grid();path.ColumnDefinitions.Add(new ColumnDefinition());path.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});game=new TextBox{MinHeight=36,ToolTip="Папка с CasualtiesUnknown.exe"};path.Children.Add(game);chooseGame=Button("Выбрать игру…",ChooseGame);Grid.SetColumn(chooseGame,1);path.Children.Add(chooseGame);header.Children.Add(path);
        var top=new WrapPanel{Margin=new Thickness(0,8,0,12)};modulesButton=Button("Проверить обновления модов",async delegate{await CheckModules();});launcherUpdate=Button("Обновить менеджер",async delegate{await UpdateLauncher();});Add(top,Button("Запустить через Steam",delegate{Open("steam://rungameid/4576490");},true),Button("Обновить список",async delegate{await Refresh(false);}),modulesButton,launcherUpdate);header.Children.Add(top);Put(outer,header,0);
        tabs=new TabControl{Background=Canvas,BorderBrush=Edge,Padding=new Thickness(10)};tabs.Items.Add(Page("Мои моды",BuildMine()));tabs.Items.Add(Page("Каталог",BuildCatalog()));tabs.Items.Add(Page("Загрузки",BuildDownloads()));tabs.Items.Add(Page("История",BuildHistory()));Put(outer,tabs,1);
        status=new TextBlock{Text="Чтение установленных модов…",Foreground=Muted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,0)};Put(outer,status,2);
        rootTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(500)};rootTimer.Tick+=async delegate{rootTimer.Stop();await Refresh(false);};
        inboxTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(3)};inboxTimer.Tick+=async delegate{inboxTimer.Stop();await ScanDownloads();};
        game.TextChanged+=delegate{if(initializing)return;++generation;mine.Clear();catalog.Clear();detected.Clear();UpdateActions();rootTimer.Stop();rootTimer.Start();};
        Loaded+=async delegate{try{game.Text=await Task.Run(()=>Backend.DefaultRoot());folder.Text=Backend.DownloadsRoot();history.Text=await Task.Run(()=>Backend.History());}catch(Exception e){Error(e);}finally{initializing=false;}StartWatcher();await Refresh(false);if(!closed&&!busy&&SteamGameLocator.IsGame(game.Text)){var packages=CheckModules();}};
        bool cleanupStarted=false;
        ContentRendered+=async delegate {
            if(cleanupStarted||closed)return;
            cleanupStarted=true;
            // Only a successfully rendered replacement may discard its old EXE.
            // Keep this independent of game-path validation and network checks.
            await Task.Run(()=>LauncherUpdate.CleanupPreviousBackup());
        };
        Closing+=delegate(object sender,System.ComponentModel.CancelEventArgs e){if(mutating){e.Cancel=true;SetStatus("Дождитесь завершения операции с файлами перед закрытием менеджера.");}};
        Closed+=delegate{closed=true;++generation;++inboxGeneration;rootTimer.Stop();inboxTimer.Stop();if(watcher!=null)watcher.Dispose();};
    }
    static string VersionText(){var v=Assembly.GetExecutingAssembly().GetName().Version;return v==null?"0.5.13":v.ToString(3);}
    static Brush ColorBrush(byte r,byte g,byte b){var brush=new SolidColorBrush(Color.FromRgb(r,g,b));brush.Freeze();return brush;}
    static Grid Rows(params GridLength[] sizes){var grid=new Grid();foreach(var size in sizes)grid.RowDefinitions.Add(new RowDefinition{Height=size});return grid;}
    static void Put(Grid grid,UIElement child,int row){Grid.SetRow(child,row);grid.Children.Add(child);}
    static void Add(Panel panel,params UIElement[] children){foreach(var c in children)panel.Children.Add(c);}
    static TabItem Page(string text,UIElement child){return new TabItem{Header=text,Content=child};}
    Button Button(string caption,Action action,bool primary=false){var button=new Button{Content=caption};if(primary){button.Background=Teal;button.Foreground=Brushes.Black;}button.Click+=delegate{action();};return button;}
    static TextBox Description(){return new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,BorderThickness=new Thickness(0),Background=Canvas};}
    static DataGrid Table(params string[] columns)
    {
        var grid=new DataGrid{AutoGenerateColumns=false,IsReadOnly=true,CanUserAddRows=false,CanUserDeleteRows=false,SelectionMode=DataGridSelectionMode.Single,SelectionUnit=DataGridSelectionUnit.FullRow,HeadersVisibility=DataGridHeadersVisibility.Column,GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,RowHeight=39,EnableRowVirtualization=true,EnableColumnVirtualization=true,AlternationCount=2};
        VirtualizingPanel.SetIsVirtualizing(grid,true);VirtualizingPanel.SetVirtualizationMode(grid,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(grid,true);
        for(int i=0;i<columns.Length;i+=2){var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(i==0?12:8,0,8,0)));style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center));grid.Columns.Add(new DataGridTextColumn{Header=new TextBlock{Text=columns[i],Margin=new Thickness(i==0?4:0,0,0,0)},Binding=new Binding(columns[i+1]),Width=new DataGridLength(i==0?2.5:1,DataGridLengthUnitType.Star),MinWidth=80,ElementStyle=style});}return grid;
    }
    static void Theme(Window window)
    {
        window.Background=Canvas;window.Foreground=Ink;
        var button=new Style(typeof(Button));button.Setters.Add(new Setter(Control.BackgroundProperty,Raised));button.Setters.Add(new Setter(Control.ForegroundProperty,Ink));button.Setters.Add(new Setter(Control.BorderBrushProperty,Edge));button.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));button.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(12,8,12,8)));button.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0,0,7,6)));
        var template=new ControlTemplate(typeof(Button));var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(4));border.SetBinding(Border.BackgroundProperty,new Binding("Background"){RelativeSource=RelativeSource.TemplatedParent});border.SetBinding(Border.BorderBrushProperty,new Binding("BorderBrush"){RelativeSource=RelativeSource.TemplatedParent});border.SetBinding(Border.BorderThicknessProperty,new Binding("BorderThickness"){RelativeSource=RelativeSource.TemplatedParent});var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);content.SetBinding(FrameworkElement.MarginProperty,new Binding("Padding"){RelativeSource=RelativeSource.TemplatedParent});border.AppendChild(content);template.VisualTree=border;button.Setters.Add(new Setter(Control.TemplateProperty,template));var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Control.BorderBrushProperty,Teal));button.Triggers.Add(hover);var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,0.42));button.Triggers.Add(disabled);window.Resources[typeof(Button)]=button;
        var text=new Style(typeof(TextBox));text.Setters.Add(new Setter(Control.BackgroundProperty,Surface));text.Setters.Add(new Setter(Control.ForegroundProperty,Ink));text.Setters.Add(new Setter(Control.BorderBrushProperty,Edge));text.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8)));text.Setters.Add(new Setter(TextBox.CaretBrushProperty,Teal));window.Resources[typeof(TextBox)]=text;
        var check=new Style(typeof(CheckBox));check.Setters.Add(new Setter(Control.ForegroundProperty,Ink));check.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(0,7,14,7)));check.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty,VerticalAlignment.Center));window.Resources[typeof(CheckBox)]=check;
        var table=new Style(typeof(DataGrid));table.Setters.Add(new Setter(Control.BackgroundProperty,Canvas));table.Setters.Add(new Setter(Control.ForegroundProperty,Ink));table.Setters.Add(new Setter(Control.BorderBrushProperty,Edge));table.Setters.Add(new Setter(DataGrid.RowBackgroundProperty,Surface));table.Setters.Add(new Setter(DataGrid.AlternatingRowBackgroundProperty,Canvas));table.Setters.Add(new Setter(DataGrid.HorizontalGridLinesBrushProperty,Edge));window.Resources[typeof(DataGrid)]=table;
        var cell=new Style(typeof(DataGridCell));cell.Setters.Add(new Setter(UIElement.ClipToBoundsProperty,true));cell.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));cell.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,6,8,6)));var selection=new Trigger{Property=DataGridCell.IsSelectedProperty,Value=true};selection.Setters.Add(new Setter(Control.BackgroundProperty,ColorBrush(26,74,73)));selection.Setters.Add(new Setter(Control.ForegroundProperty,Ink));cell.Triggers.Add(selection);window.Resources[typeof(DataGridCell)]=cell;
        var head=new Style(typeof(DataGridColumnHeader));head.Setters.Add(new Setter(Control.BackgroundProperty,Raised));head.Setters.Add(new Setter(Control.ForegroundProperty,Muted));head.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8)));head.Setters.Add(new Setter(Control.BorderBrushProperty,Edge));head.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0,0,1,1)));window.Resources[typeof(DataGridColumnHeader)]=head;
        var tab=new Style(typeof(TabItem));tab.Setters.Add(new Setter(Control.ForegroundProperty,Ink));tab.Setters.Add(new Setter(Control.BackgroundProperty,Surface));tab.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(18,10,18,10)));var selected=new Trigger{Property=TabItem.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.Black));selected.Setters.Add(new Setter(Control.BackgroundProperty,Teal));tab.Triggers.Add(selected);window.Resources[typeof(TabItem)]=tab;
        window.Resources[SystemColors.ControlBrushKey]=Surface;window.Resources[SystemColors.ControlTextBrushKey]=Ink;window.Resources[SystemColors.WindowBrushKey]=Surface;window.Resources[SystemColors.WindowTextBrushKey]=Ink;window.Resources[SystemColors.HighlightBrushKey]=ColorBrush(26,74,73);window.Resources[SystemColors.HighlightTextBrushKey]=Ink;
        WpfThemeSupplement.Apply(window);
    }
    UIElement BuildMine()
    {
        var body=Rows(new GridLength(1,GridUnitType.Star),new GridLength(6),new GridLength(220));mineGrid=Table("Мод","Name","Состояние","State","Установлен","Version","Доступен","Available","Игра","GameVersion");mineGrid.ItemsSource=mine;mineGrid.SelectionChanged+=delegate{ShowMine();};Put(body,mineGrid,0);Put(body,new GridSplitter{Height=6,HorizontalAlignment=HorizontalAlignment.Stretch,Background=Edge,ResizeDirection=GridResizeDirection.Rows},1);
        var detail=Rows(GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto);mineHeading=new TextBlock{FontSize=19,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,8,0,3)};mineFacts=new TextBlock{Foreground=Muted,TextWrapping=TextWrapping.Wrap};mineDescription=Description();Put(detail,mineHeading,0);Put(detail,mineFacts,1);Put(detail,mineDescription,2);
        var actions=new WrapPanel();installMine=Button("Установить / обновить",async delegate{var row=Mine();if(row==null)return;if(row.Kind=="builtin")await Mutate(r=>Backend.PrepareBuiltin(r,row.Id));else SelectCatalog(row.Id);},true);importMine=Button("Установить ZIP…",async delegate{var row=Mine();if(row==null)return;if(row.Kind!="builtin"){var entry=entries.FirstOrDefault(e=>e.Id==row.Id);if(entry!=null)await InstallCatalogZip(entry,null);return;}var zip=ChooseZip();if(zip!=null)await Mutate(r=>Backend.PrepareBuiltin(r,row.Id,zip));});toggleMine=Button("Включить / отключить",async delegate{await Toggle();});removeMine=Button("Удалить…",async delegate{var row=Mine();if(row!=null)await Mutate(r=>Backend.PrepareRemove(r,row));});adoptMine=Button("Подключить управление…",async delegate{var row=Mine();if(row!=null)await Mutate(r=>Backend.PrepareAdopt(r,row));});sourceMine=Button("Страница автора",delegate{var row=Mine();if(row!=null)Open(row.SourceUrl);});Add(actions,installMine,importMine,toggleMine,removeMine,adoptMine,sourceMine);changes.AddRange(new[]{installMine,importMine,toggleMine,removeMine,adoptMine});Put(detail,actions,3);Put(body,detail,2);return body;
    }
    UIElement BuildCatalog()
    {
        var body=Rows(GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),new GridLength(210));var line=new Grid();line.ColumnDefinitions.Add(new ColumnDefinition());line.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});search=new TextBox{ToolTip="Поиск по названию, автору и описанию"};search.TextChanged+=delegate{FilterCatalog();};line.Children.Add(search);var reload=Button("Обновить каталог",async delegate{await Refresh(true);});Grid.SetColumn(reload,1);line.Children.Add(reload);Put(body,line,0);
        var filters=new WrapPanel();adult=new CheckBox{Content="Показывать 18+"};installedOnly=new CheckBox{Content="Установленные"};updatesOnly=new CheckBox{Content="Есть обновление"};foreach(var check in new[]{adult,installedOnly,updatesOnly}){check.Checked+=delegate{FilterCatalog();};check.Unchecked+=delegate{FilterCatalog();};}tagSummary=new TextBlock{Foreground=Muted,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)};catalogCount=new TextBlock{Foreground=Muted,VerticalAlignment=VerticalAlignment.Center};Add(filters,adult,installedOnly,updatesOnly,Button("Теги…",ChooseTags),Button("Сбросить теги",delegate{tags.Clear();FilterCatalog();}),tagSummary,catalogCount);Put(body,filters,1);
        catalogGrid=Table("Мод","Name","Автор","Author","Версия","Version","Установка","State","Игра","GameVersion");catalogGrid.ItemsSource=catalog;catalogGrid.SelectionChanged+=delegate{ShowCatalog();};Put(body,catalogGrid,2);
        var detail=Rows(GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto);catalogHeading=new TextBlock{FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,10,0,3)};catalogFacts=new TextBlock{Foreground=Muted,TextWrapping=TextWrapping.Wrap};catalogDescription=Description();Put(detail,catalogHeading,0);Put(detail,catalogFacts,1);Put(detail,catalogDescription,2);var actions=new WrapPanel();nexusButton=Button("Скачать на Nexus",delegate{var e=Entry();if(e!=null)Open(ModCatalog.NexusUrl(e));},true);githubButton=Button("Скачать с GitHub…",async delegate{var e=Entry();if(e!=null)await InstallGitHub(e);});zipButton=Button("Установить ZIP…",async delegate{var e=Entry();if(e!=null)await InstallCatalogZip(e,null);});adoptCatalog=Button("Подключить управление…",async delegate{var row=CatalogMatch();if(row!=null)await Mutate(r=>Backend.PrepareAdopt(r,row));});Add(actions,nexusButton,githubButton,zipButton,adoptCatalog);changes.AddRange(new[]{githubButton,zipButton,adoptCatalog});Put(detail,actions,3);Put(body,detail,3);return body;
    }
    UIElement BuildDownloads()
    {
        var body=Rows(GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto);var line=new Grid();line.ColumnDefinitions.Add(new ColumnDefinition());line.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});line.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});folder=new TextBox{ToolTip="Папка завершённых ZIP-загрузок"};folder.TextChanged+=delegate{++inboxGeneration;if(!initializing){StartWatcher();inboxTimer.Start();}};line.Children.Add(folder);var choose=Button("Папка ZIP…",ChooseFolder);Grid.SetColumn(choose,1);line.Children.Add(choose);var scan=Button("Проверить",async delegate{await ScanDownloads();});Grid.SetColumn(scan,2);line.Children.Add(scan);Put(body,line,0);
        inboxGrid=Table("ZIP-архив","Name","Мод","Mod","Состояние","State");inboxGrid.ItemsSource=inbox;inboxGrid.SelectionChanged+=delegate{UpdateActions();};Put(body,inboxGrid,1);var actions=new WrapPanel{Margin=new Thickness(0,10,0,0)};inboxInstall=Button("Установить выбранный ZIP…",async delegate{await InstallInbox();},true);var manual=Button("Выбрать любой ZIP…",async delegate{var zip=ChooseZip();if(zip!=null)await Mutate(r=>Backend.PrepareLocal(r,zip));});showUnknown=new CheckBox{Content="Показывать все ZIP"};showUnknown.Checked+=async delegate{await ScanDownloads();};showUnknown.Unchecked+=async delegate{await ScanDownloads();};Add(actions,inboxInstall,manual,Button("Открыть папку",delegate{if(Directory.Exists(folder.Text))Open(folder.Text);}),showUnknown);changes.AddRange(new[]{inboxInstall,manual});Put(body,actions,2);inboxFacts=new TextBlock{Foreground=Muted,TextWrapping=TextWrapping.Wrap,Text="Готовность ZIP не подтверждает его безопасность. Перед установкой показываются файлы и предупреждения."};Put(body,inboxFacts,3);return body;
    }
    UIElement BuildHistory()
    {
        var body=Rows(GridLength.Auto,new GridLength(1,GridUnitType.Star));var actions=new WrapPanel();Add(actions,Button("Отчёт для кооператива…",async delegate{await SaveCoop();},true),Button("Сохранить историю…",async delegate{await SaveHistory();}),Button("Обновить историю",async delegate{try{history.Text=await Task.Run(()=>Backend.History());}catch(Exception e){Error(e);}}));Put(body,actions,0);history=Description();history.FontFamily=new FontFamily("Consolas");history.FontSize=12;Put(body,history,1);return body;
    }
    Backend.Row Mine(){return mineGrid==null?null:mineGrid.SelectedItem as Backend.Row;}
    ModCatalog.Entry Entry(){var row=catalogGrid==null?null:catalogGrid.SelectedItem as CatalogRow;return row==null?null:row.Entry;}
    Backend.Row CatalogMatch(){var e=Entry();return e==null?null:mine.FirstOrDefault(r=>r.Id==e.Id);}
    void ShowMine(){if(mineHeading==null)return;var row=Mine();mineHeading.Text=row==null?"Выберите мод":row.Name;mineFacts.Text=row==null?"":row.State+" · установлено: "+row.Version+" · доступно: "+row.Available+" · игра: "+row.GameVersion;mineDescription.Text=row==null?"Установка, обновление и удаление выполняются после просмотра списка файлов.":row.Description;UpdateActions();}
    void ShowCatalog(){if(catalogHeading==null)return;var e=Entry();catalogHeading.Text=e==null?"Выберите мод":e.Name;catalogFacts.Text=e==null?"":e.Author+" · версия "+e.Version+" · игра: "+ModCatalog.GameVersionText(e);catalogDescription.Text=e==null?"Метаданные сообщества. Проверяйте описание и совместимость на странице автора.":ModCatalog.PlainText(String.IsNullOrWhiteSpace(e.Description)?e.Summary:e.Description,65536);UpdateActions();}
    void UpdateActions()
    {
        if(inboxGrid==null||installMine==null||inboxInstall==null||modulesButton==null)return;var row=Mine();var e=Entry();bool builtin=row!=null&&row.Kind=="builtin";foreach(var button in changes)button.IsEnabled=!busy;
        bool known=row!=null&&entries.Any(x=>x.Id==row.Id);installMine.IsEnabled=importMine.IsEnabled=!busy&&(builtin&&row.Id!="unlock"||known);installMine.Content=builtin?"Установить / обновить":"Открыть в каталоге";toggleMine.IsEnabled=!busy&&row!=null&&row.CanToggle;toggleMine.Content=row!=null&&row.Disabled?"Включить":"Отключить";removeMine.IsEnabled=!busy&&row!=null&&row.CanRemove;adoptMine.IsEnabled=!busy&&row!=null&&row.CanAdopt;sourceMine.IsEnabled=row!=null&&!String.IsNullOrEmpty(row.SourceUrl);nexusButton.IsEnabled=e!=null;githubButton.IsEnabled=!busy&&e!=null&&!String.IsNullOrEmpty(ModCatalog.AuthorGitHubUrl(e));zipButton.IsEnabled=!busy&&e!=null;var match=CatalogMatch();adoptCatalog.IsEnabled=!busy&&match!=null&&match.CanAdopt;var zip=inboxGrid.SelectedItem as InboxRow;inboxInstall.IsEnabled=!busy&&zip!=null&&zip.Item.Ready;game.IsEnabled=chooseGame.IsEnabled=modulesButton.IsEnabled=launcherUpdate.IsEnabled=!busy;
    }
    void FilterCatalog()
    {
        if(search==null||catalogGrid==null)return;string query=(search.Text??"").Trim();var selected=Entry();catalog.Clear();foreach(var e in entries){ModCatalog.InstalledInfo info;detected.TryGetValue(e.Id,out info);if(adult.IsChecked!=true&&e.ContainsAdultContent==true)continue;if(installedOnly.IsChecked==true&&(info==null||!info.Installed))continue;if(updatesOnly.IsChecked==true&&ModCatalog.CompareInstalled(info,e)!=ModCatalog.UpdateState.UpdateAvailable)continue;if(!ModCatalog.MatchesTags(e,tags))continue;if(query.Length>0&&((e.Name??"")+" "+(e.Author??"")+" "+(e.Summary??"")).IndexOf(query,StringComparison.OrdinalIgnoreCase)<0)continue;catalog.Add(new CatalogRow{Entry=e,Info=info});}tagSummary.Text=tags.Count==0?"Все теги":"Выбрано: "+tags.Count+" · любой из них";catalogCount.Text=catalog.Count+" / "+entries.Count;if(selected!=null)catalogGrid.SelectedItem=catalog.FirstOrDefault(r=>r.Entry.Id==selected.Id);ShowCatalog();
    }
    static string InstalledText(ModCatalog.InstalledInfo info,ModCatalog.Entry e)
    {
        if(info==null)return "Не проверено";if(!info.Installed)return info.Ambiguous?"Совпадение не подтверждено":"Не установлен";var state=ModCatalog.CompareInstalled(info,e);return (info.Disabled?"Отключён · ":"")+(state==ModCatalog.UpdateState.UpdateAvailable?"Есть обновление":state==ModCatalog.UpdateState.Current?"Актуален":state==ModCatalog.UpdateState.InstalledNewer?"Установленная версия новее":"Установлен · версия не проверена");
    }
    async Task Refresh(bool force)
    {
        if(closed||busy)return;long ticket=++generation;string raw=game.Text;SetStatus("Чтение каталога и установленных DLL…");
        try {
            var snapshot=await Task.Run(()=>{var s=new Snapshot{Entries=ModCatalog.Load(force),Rows=new List<Backend.Row>(),Detected=new Dictionary<string,ModCatalog.InstalledInfo>()};string root=null;try{root=Backend.ValidateRoot(raw,false);}catch(Exception e){s.Problem=e.Message;}if(root!=null){ModCatalog.ClearDetectionCache();s.Rows=Backend.Rows(root,s.Entries);foreach(var e in s.Entries)s.Detected[e.Id]=ModCatalog.Detect(root,e);}s.History=Backend.History();return s;});
            if(!Current(ticket,raw))return;string selected=Mine()==null?null:Mine().Id;entries=snapshot.Entries;detected=snapshot.Detected;mine.Clear();foreach(var row in snapshot.Rows)mine.Add(row);if(selected!=null)mineGrid.SelectedItem=mine.FirstOrDefault(r=>r.Id==selected);if(mineGrid.SelectedItem==null&&mine.Count>0)mineGrid.SelectedIndex=0;history.Text=snapshot.History;FilterCatalog();ShowMine();SetStatus(snapshot.Problem??((ModCatalog.IsStale?"Каталог: сохранённая копия":"Каталог обновлён")+" · "+entries.Count+" модов · совместимость смотрите у автора"));await ScanDownloads();
        }catch(Exception e){if(Current(ticket,raw))Error(e);}
    }
    bool Current(long ticket,string root){return !closed&&!busy&&ticket==generation&&String.Equals(root,game.Text,StringComparison.Ordinal);}
    async Task CheckModules()
    {
        if(busy)return;string raw=game.Text;long ticket=++generation;SetStatus("Проверка версий модулей…");try{await Task.Run(()=>Backend.CheckModules(Backend.ValidateRoot(raw,false)));if(Current(ticket,raw)){SetStatus("Доступные версии модулей обновлены.");await Refresh(false);}}catch(Exception e){if(Current(ticket,raw))Error(e);}
    }
    async Task Mutate(Func<string,Backend.Plan> prepare)
    {
        if(busy||closed)return;busy=true;++generation;UpdateActions();string raw=game.Text;
        try {
            SetStatus("Подготовка списка изменений…");string root=await Task.Run(()=>Backend.ValidateRoot(raw,true));var plan=await Task.Run(()=>prepare(root));if(closed||!Confirm(plan))return;
            Backend.Plan loader=null;if(plan.NeedsLoader){loader=await Task.Run(()=>Backend.PrepareBuiltin(root,"bepinex"));if(closed||!Confirm(loader))return;}
            if(closed)return;SetStatus("Применение изменений. Не закрывайте менеджер…");mutating=true;try{if(loader!=null)await Task.Run(()=>Backend.Execute(loader));string result=await Task.Run(()=>Backend.Execute(plan));SetStatus(result);}finally{mutating=false;}
        }catch(Exception e){Error(e);}finally{busy=false;if(!closed){UpdateActions();var refresh=Refresh(false);}}
    }
    async Task InstallCatalogZip(ModCatalog.Entry entry,string path)
    {
        if(busy)return;string zip=path??ChooseZip();if(zip==null)return;await Mutate(r=>{try{return Backend.PrepareCatalog(r,entry,zip);}catch(CommunityInstaller.ArchiveHashMismatchException){return Backend.PrepareCatalog(r,entry,zip,true);}});
    }
    void SelectCatalog(string id)
    {
        var entry=entries.FirstOrDefault(e=>e.Id==id);if(entry==null)return;search.Text=entry.Name;tags.Clear();installedOnly.IsChecked=false;updatesOnly.IsChecked=false;if(entry.ContainsAdultContent==true)adult.IsChecked=true;FilterCatalog();tabs.SelectedIndex=1;catalogGrid.SelectedItem=catalog.FirstOrDefault(r=>r.Entry.Id==id);if(catalogGrid.SelectedItem!=null)catalogGrid.ScrollIntoView(catalogGrid.SelectedItem);
    }
    async Task InstallGitHub(ModCatalog.Entry entry)
    {
        if(busy||closed)return;busy=true;long ticket=++generation;string raw=game.Text;UpdateActions();Backend.GitHubAsset selected=null;
        try {
            SetStatus("Получение ZIP-выпусков автора…");var assets=await Task.Run(()=>Backend.CatalogGitHubAssets(entry));if(closed||ticket!=generation||raw!=game.Text)return;if(assets.Count==0)throw new InvalidOperationException("В выпуске автора нет ZIP. Откройте страницу Nexus.");
            if(assets.Count==1)selected=assets[0];else {
                var dialog=Modal("Выберите ZIP автора",730,440);var body=Rows(GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto);body.Margin=new Thickness(18);dialog.Content=body;Put(body,new TextBlock{Text="Проверьте название файла и версию игры. Затем появится полный список изменений.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,12)},0);var list=new ListBox{ItemsSource=assets,DisplayMemberPath="Name",SelectedIndex=0,Background=Surface,Foreground=Ink};Put(body,list,1);var buttons=new WrapPanel{Margin=new Thickness(0,12,0,0)};Add(buttons,Button("Выбрать",delegate{dialog.DialogResult=true;},true),Button("Отмена",delegate{dialog.DialogResult=false;}));Put(body,buttons,2);if(dialog.ShowDialog()==true)selected=list.SelectedItem as Backend.GitHubAsset;
            }
        }catch(Exception e){Error(e);}finally{busy=false;if(!closed)UpdateActions();}
        if(selected!=null&&!closed&&ticket==generation&&raw==game.Text)await Mutate(r=>Backend.PrepareCatalogGitHub(r,entry,selected));
    }
    async Task Toggle()
    {
        var row=Mine();if(busy||row==null||!row.CanToggle)return;if(MessageBox.Show(this,(row.Disabled?"Включить":"Отключить")+" «"+row.Name+"»? Игровые настройки сохраняются.","Изменить состояние",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;busy=true;++generation;UpdateActions();string raw=game.Text;try{string root=await Task.Run(()=>Backend.ValidateRoot(raw,true));if(closed)return;mutating=true;try{SetStatus(await Task.Run(()=>Backend.Toggle(root,row)));}finally{mutating=false;}}catch(Exception e){Error(e);}finally{busy=false;if(!closed){UpdateActions();var refresh=Refresh(false);}}
    }
    bool Confirm(Backend.Plan plan)
    {
        var dialog=Modal(plan.Title,800,630);var body=Rows(GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto);body.Margin=new Thickness(20);dialog.Content=body;Put(body,new ScrollViewer{MaxHeight=170,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,0,0,14),Content=new TextBlock{Text=plan.Summary+"\n\n"+(plan.Warning??"")+(plan.NeedsLoader?"\nДля этого мода нужен BepInEx. Его установка будет показана отдельно.":""),TextWrapping=TextWrapping.Wrap,Foreground=Ink}},0);
        var files=new ListBox{ItemsSource=plan.Files.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>p.Key+"    ·    "+(p.Value==null?0:p.Value.Length).ToString("N0")+" байт").ToList(),Background=Surface,Foreground=Ink,BorderBrush=Edge,FontFamily=new FontFamily("Consolas"),FontSize=12};VirtualizingPanel.SetIsVirtualizing(files,true);Put(body,files,1);var actions=new WrapPanel{Margin=new Thickness(0,14,0,0),HorizontalAlignment=HorizontalAlignment.Right};var yes=Button("Подтвердить",delegate{dialog.DialogResult=true;},true);var no=Button("Отмена",delegate{dialog.DialogResult=false;});yes.IsDefault=true;no.IsCancel=true;Add(actions,yes,no);Put(body,actions,2);return dialog.ShowDialog()==true;
    }
    Window Modal(string title,double width,double height){var work=SystemParameters.WorkArea;var w=new Window{Title=title,Width=Math.Min(width,work.Width-40),Height=Math.Min(height,work.Height-40),MinWidth=460,MinHeight=280,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner,FontFamily=FontFamily,FontSize=FontSize};Theme(w);return w;}
    void ChooseTags()
    {
        var dialog=Modal("Теги · достаточно любого выбранного",570,650);var body=Rows(GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto);body.Margin=new Thickness(18);dialog.Content=body;Put(body,new TextBlock{Text="Совпадение с любым выбранным тегом дополняет поиск, 18+ и фильтр установки.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,10)},0);var stack=new StackPanel();var choices=new List<CheckBox>();foreach(string tag in ModCatalog.AvailableTags(entries).Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t=>t,StringComparer.OrdinalIgnoreCase)){var c=new CheckBox{Content=tag,IsChecked=tags.Contains(tag)};choices.Add(c);stack.Children.Add(c);}Put(body,new ScrollViewer{Content=stack,VerticalScrollBarVisibility=ScrollBarVisibility.Auto},1);var actions=new WrapPanel{Margin=new Thickness(0,12,0,0)};Add(actions,Button("Применить",delegate{tags.Clear();foreach(var c in choices.Where(c=>c.IsChecked==true))tags.Add((string)c.Content);dialog.DialogResult=true;},true),Button("Снять все",delegate{foreach(var c in choices)c.IsChecked=false;}),Button("Отмена",delegate{dialog.DialogResult=false;}));Put(body,actions,2);if(dialog.ShowDialog()==true)FilterCatalog();
    }
    async Task ScanDownloads()
    {
        if(closed||busy||scanBusy||folder==null)return;string path=folder.Text;bool all=showUnknown.IsChecked==true;long ticket=++inboxGeneration,rootTicket=generation;var metadata=entries.ToList();scanBusy=true;
        try{var items=await Task.Run(()=>ArchiveInbox.Scan(path,metadata,inboxCache,all));if(closed||busy||ticket!=inboxGeneration||rootTicket!=generation||path!=folder.Text||all!=(showUnknown.IsChecked==true)){if(!closed)inboxTimer.Start();return;}string chosen=(inboxGrid.SelectedItem as InboxRow)==null?null:((InboxRow)inboxGrid.SelectedItem).Item.Path;inbox.Clear();foreach(var item in items)inbox.Add(new InboxRow{Item=item});if(chosen!=null)inboxGrid.SelectedItem=inbox.FirstOrDefault(x=>x.Item.Path==chosen);inboxFacts.Text="Показано ZIP: "+inbox.Count+" · "+(all?"включены неизвестные архивы":"только признаки модов Casualties")+". Установка — после предпросмотра.";UpdateActions();if(ArchiveInbox.HasPendingFiles)inboxTimer.Start();}catch(Exception e){if(!closed&&ticket==inboxGeneration)inboxFacts.Text=e.Message;}finally{scanBusy=false;}
    }
    void StartWatcher()
    {
        if(watcher!=null){watcher.Dispose();watcher=null;}if(folder==null||!Directory.Exists(folder.Text)||closed)return;
        try{watcher=new FileSystemWatcher(folder.Text,"*.zip"){IncludeSubdirectories=false,NotifyFilter=NotifyFilters.FileName|NotifyFilters.Size|NotifyFilters.LastWrite};FileSystemEventHandler changed=delegate{QueueInboxScan();};watcher.Created+=changed;watcher.Changed+=changed;watcher.Deleted+=changed;watcher.Renamed+=delegate{QueueInboxScan();};watcher.Error+=delegate{QueueInboxScan();};watcher.EnableRaisingEvents=true;}catch(Exception e){if(inboxFacts!=null)inboxFacts.Text="Автопроверка папки недоступна: "+e.Message;}
    }
    void QueueInboxScan(){if(closed)return;try{Dispatcher.BeginInvoke(new Action(delegate{if(closed)return;++inboxGeneration;inboxTimer.Stop();inboxTimer.Start();}));}catch{}}
    async Task InstallInbox()
    {
        if(busy)return;var selected=inboxGrid.SelectedItem as InboxRow;if(selected==null)return;var item=selected.Item;if(!item.Ready){SetStatus("Дождитесь завершения загрузки ZIP и нажмите «Проверить».");return;}if(item.Mod!=null){await InstallCatalogZip(item.Mod,item.Path);return;}if(new[]{"russian","wiki","together","bepinex"}.Contains(item.Kind)){await Mutate(r=>Backend.PrepareBuiltin(r,item.Kind,item.Path));return;}await Mutate(r=>Backend.PrepareLocal(r,item.Path));
    }
    void ChooseGame(){if(busy)return;var dialog=new OpenFileDialog{Title="Выберите CasualtiesUnknown.exe",Filter="CasualtiesUnknown.exe|CasualtiesUnknown.exe",CheckFileExists=true};if(dialog.ShowDialog(this)==true){game.Text=Path.GetDirectoryName(dialog.FileName);try{Backend.SaveRoot(game.Text);}catch(Exception e){Error(e);}}}
    string ChooseZip(){var dialog=new OpenFileDialog{Title="Выберите ZIP мода",Filter="ZIP-архив|*.zip",CheckFileExists=true};return dialog.ShowDialog(this)==true?dialog.FileName:null;}
    void ChooseFolder()
    {
        var dialog=Modal("Папка ZIP-загрузок",650,330);var body=new StackPanel{Margin=new Thickness(20)};body.Children.Add(new TextBlock{Text="Укажите папку с завершёнными ZIP. Можно выбрать любой ZIP внутри неё.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});var path=new TextBox{Text=folder.Text};body.Children.Add(path);var actions=new WrapPanel{Margin=new Thickness(0,15,0,0)};Add(actions,Button("Выбрать ZIP в папке…",delegate{var file=ChooseZip();if(file!=null)path.Text=Path.GetDirectoryName(file);}),Button("Применить",delegate{if(Directory.Exists(path.Text)){folder.Text=path.Text;dialog.DialogResult=true;}else MessageBox.Show(dialog,"Папка не найдена.");},true),Button("Отмена",delegate{dialog.DialogResult=false;}));body.Children.Add(actions);dialog.Content=body;if(dialog.ShowDialog()==true){try{Backend.SaveDownloadRoot(folder.Text);}catch(Exception e){Error(e);}var scan=ScanDownloads();}
    }
    async Task SaveCoop()
    {
        if(busy)return;string raw=game.Text;long ticket=generation;SetStatus("Формирование отчёта кооператива…");try{string report=await Task.Run(()=>CoopDiagnostics.Build(Backend.ValidateRoot(raw,false)));if(!Current(ticket,raw))return;var dialog=new SaveFileDialog{Title="Сохранить отчёт для друзей",Filter="Текстовый отчёт|*.txt",FileName="Casualties-coop-report.txt"};if(dialog.ShowDialog(this)==true){await Task.Run(()=>File.WriteAllText(dialog.FileName,report,new UTF8Encoding(false)));SetStatus("Отчёт сохранён. Он сравнивает файлы, а не подтверждает совместимость запущенных игр.");}}catch(Exception e){if(!closed)Error(e);}
    }
    async Task SaveHistory(){var dialog=new SaveFileDialog{Title="Сохранить историю",Filter="Текст|*.txt",FileName="Casualties-manager-history.txt"};if(dialog.ShowDialog(this)!=true)return;string text=history.Text;try{await Task.Run(()=>File.WriteAllText(dialog.FileName,text,new UTF8Encoding(false)));SetStatus("История сохранена.");}catch(Exception e){Error(e);}}
    async Task UpdateLauncher()
    {
        if(busy)return;busy=true;++generation;UpdateActions();try{SetStatus("Проверка обновления менеджера…");var info=await Task.Run(()=>LauncherUpdate.Check(Feed,VersionText()));if(closed)return;if(info==null){SetStatus("Установлена последняя версия менеджера.");return;}if(MessageBox.Show(this,"Доступна версия "+info.Version+".\n\n"+info.Notes+"\n\nСкачать и перезапустить менеджер?","Обновление менеджера",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;SetStatus("Скачивание и проверка обновления менеджера…");string staged=await Task.Run(()=>LauncherUpdate.DownloadStage(info));if(closed)return;await Task.Run(()=>LauncherUpdate.ApplyOnExit(staged,Assembly.GetEntryAssembly().Location));busy=false;Close();}catch(Exception e){if(!closed)Error(e);}finally{busy=false;if(!closed)UpdateActions();}
    }
    void SetStatus(string text){if(!closed&&status!=null)status.Text=text??"";}
    void Error(Exception error){SetStatus(error.Message);try{Backend.LogMessage(error.Message);}catch{}if(!closed)MessageBox.Show(this,error.Message,"Операция не выполнена",MessageBoxButton.OK,MessageBoxImage.Warning);}
    void Open(string location){if(String.IsNullOrWhiteSpace(location))return;try{Process.Start(new ProcessStartInfo(location){UseShellExecute=true});}catch(Exception e){Error(e);}}
}
