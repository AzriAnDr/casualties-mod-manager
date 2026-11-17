using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

// Static bridge for the WPF launcher. Never creates a Manager/Form or a native
// control. Preparation reads/verifies payloads; only Execute/Toggle mutate games.
public sealed partial class Manager
{
    public static class LauncherBackend
    {
        static readonly object gate=new object();
        sealed class Selection
        {
            internal string Root;internal Module Module;internal Manifest Manifest;
            internal CommunityInstaller.InstalledRecord Record;
            internal ModCatalog.Entry Entry;internal ModCatalog.InstalledInfo Info;internal ModCatalog.ManualPlugin Manual;
        }
        public sealed class Row
        {
            public string Id{get;internal set;}public string Name{get;internal set;}public string State{get;internal set;}
            public string Version{get;internal set;}public string Available{get;internal set;}public string GameVersion{get;internal set;}
            public string Description{get;internal set;}public string Kind{get;internal set;}public string SourceUrl{get;internal set;}
            public bool Managed{get;internal set;}public bool Disabled{get;internal set;}public bool CanAdopt{get;internal set;}
            public bool CanRemove{get;internal set;}public bool CanToggle{get;internal set;}public object Native{get;internal set;}
        }
        public sealed class Plan
        {
            public string Title{get;internal set;}public string Summary{get;internal set;}public string Warning{get;internal set;}
            public Dictionary<string,byte[]> Files{get;internal set;}public bool NeedsLoader{get;internal set;}
            internal string Root,Seal;internal bool EngineLock,Used;internal Func<string> Action;
        }
        public sealed class GitHubAsset
        {
            public string Name{get;internal set;}public string Version{get;internal set;}public string Url{get;internal set;}
            public string Sha256{get;internal set;}public string Notes{get;internal set;}internal string Repository;
        }
        public static string DefaultRoot(){return DefaultPath();}
        public static string ValidateRoot(string root,bool mutate){return Validate(root,mutate);}
        public static void SaveRoot(string root){root=Validate(root,false);Directory.CreateDirectory(SettingsDir);File.WriteAllText(Path.Combine(SettingsDir,"game-path.txt"),root);}
        public static string GameVersion(string root){return ReadGameVersion(Validate(root,false))??"Не указана";}
        public static string History()
        {
            try
            {
                if(!File.Exists(LogFile))return "История пока пуста.";
                using(var input=new FileStream(LogFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))
                {
                    long length=input.Length;int size=(int)Math.Min(length,1024*1024);bool tail=length>size;if(tail)input.Seek(length-size,SeekOrigin.Begin);
                    byte[] data=new byte[size];int offset=0,read;while(offset<size&&(read=input.Read(data,offset,size-offset))>0)offset+=read;
                    string text=Encoding.UTF8.GetString(data,0,offset).TrimStart('\ufeff');if(tail){int newline=text.IndexOf('\n');text=newline<0?"":text.Substring(newline+1);}
                    return (tail?"Показан конец истории (до1МиБ).\r\n":"")+text;
                }
            }
            catch(Exception e){return "История недоступна: "+e.Message;}
        }
        public static void LogMessage(string message){lock(gate)Log(message);}
        public static string DownloadsRoot()
        {
            try{string saved=Path.Combine(SettingsDir,"downloads-path.txt");if(File.Exists(saved)&&Directory.Exists(File.ReadAllText(saved)))return File.ReadAllText(saved);
                using(var key=Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders")){string value=key==null?null:key.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") as string;if(value!=null)return Environment.ExpandEnvironmentVariables(value);}}
            catch{}return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
        }
        public static void SaveDownloadRoot(string folder)
        {
            string path=Path.GetFullPath(folder);if(!Directory.Exists(path))throw new DirectoryNotFoundException("Папка загрузок не найдена.");
            for(var dir=new DirectoryInfo(path);dir!=null;dir=dir.Parent)if((dir.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка загрузок содержит ссылку или junction.");
            Directory.CreateDirectory(SettingsDir);File.WriteAllText(Path.Combine(SettingsDir,"downloads-path.txt"),path);
        }
        public static void CheckModules(string root)
        {
            var packages=ModuleCatalog.Check();string together=null;
            try{together=Convert.ToString(ReadLatest("creaturefeaturelarry","casualties-together")["tag_name"]).TrimStart('v','V');}catch(Exception e){LogMessage("Together: версия пока недоступна. "+e.Message);}
            lock(gate){onlineModules=packages;if(together!=null)KnownTogetherVersion=together;if(SteamGameLocator.IsGame(root))selectedGameVersion=ReadGameVersion(Validate(root,false));}
        }
        static bool BuiltinEntry(ModCatalog.Entry e){return e.DllNames!=null&&e.DllNames.Any(n=>new[]{"CasualtiesMP.dll","CuRussian.dll","CuWiki.dll"}.Contains(Path.GetFileName(n),StringComparer.OrdinalIgnoreCase));}
        static string Canonical(string path){string p=(path??"").Replace('\\','/');return p.EndsWith(".cumm-disabled",StringComparison.OrdinalIgnoreCase)?p.Substring(0,p.Length-".cumm-disabled".Length):p;}
        static string CatalogState(ModCatalog.Entry e,ModCatalog.InstalledInfo info)
        {
            if(info==null||!info.Installed)return "Не установлен";
            if(info.Ambiguous)return "Файлы изменены / связь неоднозначна";
            if(info.Partial)return "Найдена часть DLL";
            switch(ModCatalog.CompareInstalled(info,e)){case ModCatalog.UpdateState.UpdateAvailable:return "Есть обновление";case ModCatalog.UpdateState.Current:return "Текущая версия";case ModCatalog.UpdateState.InstalledNewer:return "Установлена новее";default:return "Версия пакета не подтверждена";}
        }
        static string InstalledGame(ModCatalog.Entry entry,ModCatalog.InstalledInfo info,string archiveVersion)
        {
            if(entry==null)return "Не указана";
            bool same=info!=null&&ModCatalog.CompareInstalled(info,entry)==ModCatalog.UpdateState.Current;
            if(!same&&archiveVersion!=null)same=ModCatalog.CompareVersions(archiveVersion,entry.Version)==ModCatalog.VersionComparison.Equal;
            return same?ModCatalog.GameVersionText(entry):"Не указана";
        }
        public static List<Row> Rows(string root,List<ModCatalog.Entry> catalog)
        {
            lock(gate)
            {
                root=Validate(root,false);selectedGameVersion=ReadGameVersion(root);ModCatalog.ClearDetectionCache();
                catalog=catalog??new List<ModCatalog.Entry>();var rows=new List<Row>();var represented=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var ids=new HashSet<string>();
                var detections=new Dictionary<string,ModCatalog.InstalledInfo>();foreach(var e in catalog)try{detections[e.Id]=ModCatalog.Detect(root,e);}catch{}
                foreach(var m in VisibleModules(root))
                {
                    var mf=Load(root,m);var detected=Detect(root,m);foreach(string path in detected)represented.Add(Canonical(path));if(mf!=null)foreach(var f in mf.Files)represented.Add(Canonical(f.Path));
                    string version=VersionOf(root,m,mf);rows.Add(new Row{Id=m.Id,Name=m.Name,State=ModuleState(m,mf,detected.Count,version),Version=version,Available=Available(m),GameVersion=ModuleGameVersion(m,version,mf),Description=m.Description,Kind="builtin",SourceUrl=Source(m)=="bundled"?null:Source(m),Managed=mf!=null,Disabled=mf!=null&&mf.Disabled,CanAdopt=mf==null&&detected.Count>0,CanRemove=mf!=null,CanToggle=mf!=null&&m.Id!="bepinex",Native=new Selection{Root=root,Module=m,Manifest=mf}});
                }
                foreach(var record in CommunityInstaller.ListInstalled(root))
                {
                    int present=CommunityInstaller.CountPresent(root,record);if(present==0)continue;ids.Add(record.Id);foreach(var f in record.Files)represented.Add(Canonical(f.Path));var e=catalog.FirstOrDefault(c=>c.Id==record.Id);ModCatalog.InstalledInfo info=null;if(e!=null)detections.TryGetValue(e.Id,out info);
                    rows.Add(new Row{Id=record.Id,Name=record.Name,State=present<record.Files.Count?"Установка неполная":record.Disabled?"Отключён":record.Id.StartsWith("local-",StringComparison.Ordinal)?"Локальный мод · под управлением":"Под управлением",Version=record.Version,Available=e==null?"Из нового ZIP":CatalogState(e,info),GameVersion=InstalledGame(e,info,record.VersionSource=="plugin-metadata"?null:record.Version),Description=e==null?record.Name:ModCatalog.SummaryText(e,1200),Kind="community",SourceUrl=e==null?null:ModCatalog.NexusUrl(e),Managed=true,Disabled=record.Disabled,CanRemove=true,CanToggle=true,Native=new Selection{Root=root,Record=record,Entry=e,Info=info}});
                }
                foreach(var e in catalog)
                {
                    ModCatalog.InstalledInfo info;if(ids.Contains(e.Id)||BuiltinEntry(e)||!detections.TryGetValue(e.Id,out info)||!info.Installed)continue;
                    if(info.Files.Count>0&&info.Files.All(f=>represented.Contains(Canonical(f))))continue;foreach(string f in info.Files)represented.Add(Canonical(f));
                    rows.Add(new Row{Id=e.Id,Name=e.Name,State=info.Disabled?"Вручную · отключён":"Из каталога · вручную",Version=info.Version,Available=CatalogState(e,info),GameVersion=InstalledGame(e,info,null),Description=ModCatalog.SummaryText(e,1200)+"\r\n"+info.MatchReason,Kind="catalog-manual",SourceUrl=ModCatalog.NexusUrl(e),Disabled=info.Disabled,CanAdopt=!info.Ambiguous&&!info.Disabled,CanRemove=!info.Ambiguous,Native=new Selection{Root=root,Entry=e,Info=info}});
                }
                foreach(var plugin in ModCatalog.ListManualPlugins(root))
                {
                    if(!represented.Add(Canonical(plugin.RelativePath)))continue;string name=String.Join(" / ",plugin.Plugins.Select(p=>p.Name).Distinct());
                    rows.Add(new Row{Id="manual:"+plugin.RelativePath,Name=name,State=plugin.Disabled?"Вручную · отключён":"Вручную · связь не подтверждена",Version=String.Join(" / ",plugin.Plugins.Select(p=>p.Version).Distinct()),Available="Не проверено",GameVersion="Не указана",Description=plugin.RelativePath+"\r\n"+String.Join("\r\n",plugin.Plugins.Select(p=>p.Guid))+"\r\nСвязь с каталогом и совместимость не подтверждены.",Kind="manual",Disabled=plugin.Disabled,CanRemove=true,Native=new Selection{Root=root,Manual=plugin}});
                }
                return rows;
            }
        }
        static string Snapshot(object value){return Hash(Encoding.UTF8.GetBytes(Json.Serialize(value)));}
        static string PlanSeal(Plan p)
        {
            var text=new StringBuilder(p.Root+"\n"+p.Title+"\n"+p.Summary+"\n"+p.Warning+"\n"+p.NeedsLoader+"\n");
            foreach(var f in p.Files.OrderBy(f=>f.Key,StringComparer.OrdinalIgnoreCase)){text.Append(f.Key.Length).Append(':').Append(f.Key).Append(':').Append(Hash(f.Value)).Append('\n');}return Hash(Encoding.UTF8.GetBytes(text.ToString()));
        }
        static Plan NewPlan(string root,string title,string warning,Dictionary<string,byte[]>files,bool loader,bool engineLock,Func<string> action)
        {
            var p=new Plan{Root=root,Title=title,Warning=warning??"",Summary="Файлов: "+files.Count+"\r\n"+String.Join("\r\n",files.Keys.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)),Files=files,NeedsLoader=loader,EngineLock=engineLock,Action=action};p.Seal=PlanSeal(p);return p;
        }
        static bool NeedsLoader(string root){return !File.Exists(Under(root,"BepInEx/core/BepInEx.dll"));}
        static void GuardHashes(string root,Dictionary<string,string> expected){foreach(var f in expected)if(!File.Exists(Under(root,f.Key))||Hash(Under(root,f.Key))!=f.Value)throw new InvalidOperationException("Файл изменился после просмотра: "+f.Key+". Повторите проверку.");}
        static Dictionary<string,byte[]> PreviewPaths(IEnumerable<string> paths){return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(p=>p,p=>new byte[0],StringComparer.OrdinalIgnoreCase);}
        static Dictionary<string,string> Hashes(string root,IEnumerable<string> paths){return paths.ToDictionary(p=>p,p=>Hash(Under(root,p)),StringComparer.OrdinalIgnoreCase);}
        static Selection Selected(string root,Row row){var s=row==null?null:row.Native as Selection;if(s==null||!String.Equals(root,s.Root,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Папка игры изменилась. Обновите список и выберите мод.");return s;}
        public static Plan PrepareBuiltin(string root,string id,string zipPath=null)
        {
            root=Validate(root,false);Module module=Find(id);if(module.Id=="unlock")throw new InvalidOperationException("Прежняя карточка доступна только для удаления или отключения.");
            Dictionary<string,byte[]> files;string version;string[] games=null;string warning="Настройки сохраняются.";
            if(zipPath!=null){using(var input=File.OpenRead(zipPath))files=ReadZip(input,module);version="import";warning+=" Источник ZIP не подтверждён структурой архива.";}
            else if(module.Id=="together")
            {
                var release=ReadLatest("creaturefeaturelarry","casualties-together");var choices=Assets(release).Where(a=>Convert.ToString(a["name"]).EndsWith(".zip",StringComparison.OrdinalIgnoreCase)&&Convert.ToString(a["name"]).IndexOf("fullgame",StringComparison.OrdinalIgnoreCase)>=0).ToList();
                if(choices.Count!=1)throw new InvalidDataException("Не удалось однозначно выбрать fullgame ZIP Together. Скачайте нужный ZIP вручную.");var asset=choices[0];string url=Convert.ToString(asset["browser_download_url"]);ValidateTogetherUrl(url,false,true);var data=DownloadTogether(url,TogetherArchiveBytes,false);VerifyDigest(data,Digest(asset));using(var input=new MemoryStream(data))files=ReadZip(input,module);version=Convert.ToString(release["tag_name"]);warning+="\r\n"+Convert.ToString(release["body"])+"\r\nСовместимость с игрой проверяйте по описанию выпуска.";
            }
            else
            {
                ModuleCatalog.Package package;lock(gate){selectedGameVersion=ReadGameVersion(root);package=RecommendedPackage(module);}
                if(package!=null){string archive=ModuleCatalog.Download(package,Path.Combine(SettingsDir,"downloads"));using(var input=File.OpenRead(archive))files=ReadZip(input,module);version=package.Version;games=package.GameVersions;warning+=" Каталог пакетов: SHA-256 проверен.";}
                else{using(var input=Resource(module))files=ReadZip(input,module);version=BundledVersion(module.Id);warning+=" Встроенный пакет.";}
            }
            string[] packageGames=PackageGameVersions(module,files,games);if(module.Id=="russian"||module.Id=="wiki")RequireGameVersion(root,packageGames);
            var originalManifest=Load(root,module);string manifestSeal=Snapshot(originalManifest);string installed=VersionOf(root,module,originalManifest);if(ModCatalog.CompareVersions(installed,version)==ModCatalog.VersionComparison.Newer)throw new InvalidOperationException("Установлена более новая версия "+installed+". Понижение не выполнено.");
            return NewPlan(root,module.Name+" · "+version,warning,files,module.Id!="bepinex"&&NeedsLoader(root),true,delegate{if(Snapshot(Load(root,module))!=manifestSeal)throw new InvalidOperationException("Установка изменилась после просмотра. Повторите проверку.");Install(root,module,files,version,games);return module.Name+": установлен / обновлён; резервная копия: "+LastBackup;});
        }
        static IEnumerable<string> PrimaryDlls(ModCatalog.Entry e){return e.DllVersions!=null&&e.DllVersions.Count>0?e.DllVersions.Keys:(IEnumerable<string>)(e.DllNames??new List<string>());}
        public static Plan PrepareCatalog(string root,ModCatalog.Entry entry,string zipPath,bool reviewDifferentArchive=false)
        {
            root=Validate(root,false);if(entry==null||BuiltinEntry(entry))throw new InvalidOperationException("Для встроенного модуля используйте его основную карточку.");
            var plan=CommunityInstaller.Preview(root,entry.Id,zipPath,entry.ArchiveSha256,PrimaryDlls(entry),reviewDifferentArchive,entry.BepinexPlugins.Keys);return CommunityPlan(root,plan,entry.Name,entry.Version);
        }
        public static Plan PrepareLocal(string root,string zipPath){root=Validate(root,false);var plan=CommunityInstaller.PreviewLocal(root,zipPath);return CommunityPlan(root,plan,plan.DisplayName,plan.PluginVersion);}
        static Plan CommunityPlan(string root,CommunityInstaller.Plan plan,string name,string version)
        {
            string recordSeal=Snapshot(CommunityInstaller.ListInstalled(root).SingleOrDefault(r=>r.Id==plan.ModuleId));
            return NewPlan(root,name+" · "+(plan.PluginVersion??version),String.Join("\r\n",plan.Warnings),plan.Files,NeedsLoader(root),false,delegate{if(Snapshot(CommunityInstaller.ListInstalled(root).SingleOrDefault(r=>r.Id==plan.ModuleId))!=recordSeal)throw new InvalidOperationException("Установка изменилась после просмотра. Повторите проверку.");var result=CommunityInstaller.Install(root,plan,name,version);return name+": установлен / обновлён; файлов: "+result.FileCount+"; резервная копия: "+result.BackupPath;});
        }
        public static Plan PrepareRemove(string root,Row row)
        {
            root=Validate(root,false);var s=Selected(root,row);
            if(s.Module!=null)
            {
                var current=Load(root,s.Module);if(current==null)throw new InvalidOperationException("Ручная установка не удаляется автоматически: сначала включите управление после просмотра файлов.");string seal=Snapshot(current);var files=PreviewPaths(current.Files.Where(f=>s.Module.Id!="together"||!IsTogetherCoreLibrary(f.Path)).Select(f=>Actual(current,f)));
                return NewPlan(root,"Удалить «"+row.Name+"»","Конфиги, сохранения и посторонние ресурсы сохраняются.",files,false,true,delegate{if(Snapshot(Load(root,s.Module))!=seal)throw new InvalidOperationException("Манифест изменился после просмотра.");Remove(root,s.Module);return row.Name+": удалён; резервная копия: "+LastBackup;});
            }
            if(s.Record!=null)
            {
                var record=CommunityInstaller.ListInstalled(root).SingleOrDefault(r=>r.Id==s.Record.Id);if(record==null)throw new InvalidOperationException("Мод уже не управляется менеджером.");string seal=Snapshot(record);string name=record.Name,id=record.Id;
                return NewPlan(root,"Удалить «"+name+"»","Конфиги, сохранения и посторонние ресурсы сохраняются.",PreviewPaths(record.Files.Select(f=>f.Path+(record.Disabled&&f.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)?".cumm-disabled":""))),false,false,delegate{if(Snapshot(CommunityInstaller.ListInstalled(root).SingleOrDefault(r=>r.Id==id))!=seal)throw new InvalidOperationException("Манифест изменился после просмотра.");var result=CommunityInstaller.Remove(root,id);return name+": удалён; резервная копия: "+result.BackupPath;});
            }
            var expected=s.Manual!=null?new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase){{s.Manual.RelativePath,s.Manual.Sha256}}:s.Info!=null&&s.Info.Installed&&!s.Info.Ambiguous?s.Info.Files.ToDictionary(f=>f,f=>s.Info.HashesByFile[f],StringComparer.OrdinalIgnoreCase):null;
            if(expected==null||expected.Count==0)throw new InvalidOperationException("Нет однозначно проверенных DLL для удаления.");string title=row.Name;
            return NewPlan(root,"Удалить DLL «"+title+"»","Только перечисленные DLL; соседние ресурсы, конфиги и сохранения сохраняются.",PreviewPaths(expected.Keys),false,false,delegate{var result=CommunityInstaller.RemoveManual(root,expected);return title+": DLL удалены; резервная копия: "+result.BackupPath;});
        }
        public static Plan PrepareAdopt(string root,Row row)
        {
            root=Validate(root,false);var s=Selected(root,row);string name=row.Name;
            if(s.Module!=null)
            {
                if(Load(root,s.Module)!=null)throw new InvalidOperationException("Мод уже управляется менеджером.");var paths=Detect(root,s.Module);if(paths.Count==0)throw new InvalidOperationException("Ручные файлы не найдены.");var hashes=Hashes(root,paths);
                return NewPlan(root,"Управлять «"+name+"»","Файлы будут сохранены в резервной копии и приняты под управление без переустановки. Настройки не меняются.",PreviewPaths(paths),false,true,delegate{GuardHashes(root,hashes);if(!Detect(root,s.Module).OrderBy(x=>x).SequenceEqual(paths.OrderBy(x=>x)))throw new InvalidOperationException("Список файлов изменился после просмотра.");foreach(var owner in CommunityInstaller.ListInstalled(root))if(owner.Files.Any(f=>paths.Contains(f.Path,StringComparer.OrdinalIgnoreCase)))throw new InvalidOperationException("Файл уже принадлежит модулю сообщества.");Adopt(root,s.Module);return name+": управление включено; резервная копия: "+LastBackup;});
            }
            if(s.Entry==null||s.Info==null||!s.Info.Installed||s.Info.Ambiguous||s.Info.Disabled)throw new InvalidOperationException("Не найдена однозначная включённая ручная установка для принятия под управление.");
            var expected=Hashes(root,s.Info.Files);string id=s.Entry.Id,version=s.Info.Version;var reviewed=s.Info.Files.ToArray();
            return NewPlan(root,"Управлять «"+name+"»","Принимаются только явно перечисленные DLL с резервной копией; ресурсы и настройки не присваиваются.",PreviewPaths(reviewed),false,false,delegate{GuardHashes(root,expected);var result=CommunityInstaller.Adopt(root,id,name,version,reviewed);return name+": управление включено; резервная копия: "+result.BackupPath;});
        }
        public static string Execute(Plan plan)
        {
            lock(gate)
            {
                if(plan==null||plan.Action==null||plan.Used||plan.Seal!=PlanSeal(plan))throw new InvalidOperationException("План изменён или уже выполнен. Повторите предварительный просмотр.");
                Validate(plan.Root,true);if(plan.NeedsLoader&&NeedsLoader(plan.Root))throw new InvalidOperationException("Сначала подтвердите и установите BepInEx отдельным планом.");
                string result;if(plan.EngineLock)using(var operationLock=LockGame(plan.Root))result=plan.Action();else result=plan.Action();plan.Used=true;ModCatalog.ClearDetectionCache();Log(result);return result;
            }
        }
        public static string Toggle(string root,Row row)
        {
            lock(gate)
            {
                root=Validate(root,true);var s=Selected(root,row);string result;
                if(s.Module!=null){var mf=Load(root,s.Module);if(mf==null||s.Manifest==null||Snapshot(mf)!=Snapshot(s.Manifest))throw new InvalidOperationException("Состояние изменилось. Обновите список.");using(var operationLock=LockGame(root))Manager.Toggle(root,s.Module,!mf.Disabled);result=row.Name+(mf.Disabled?": включён.":": отключён.");}
                else if(s.Record!=null){var record=CommunityInstaller.ListInstalled(root).SingleOrDefault(r=>r.Id==s.Record.Id);if(record==null||Snapshot(record)!=Snapshot(s.Record))throw new InvalidOperationException("Состояние изменилось. Обновите список.");CommunityInstaller.SetEnabled(root,record.Id,record.Disabled);result=row.Name+(record.Disabled?": включён.":": отключён.");}
                else throw new InvalidOperationException("Сначала включите управление модом.");ModCatalog.ClearDetectionCache();Log(result);return result;
            }
        }
        static Dictionary<string,object> ReadLatest(string owner,string repository)
        {
            using(var memory=new MemoryStream()){FetchGitHub("https://api.github.com/repos/"+Uri.EscapeDataString(owner)+"/"+Uri.EscapeDataString(repository)+"/releases/latest",memory,2*1024*1024,20000,false);return new JavaScriptSerializer{MaxJsonLength=2*1024*1024,RecursionLimit=32}.Deserialize<Dictionary<string,object>>(new UTF8Encoding(false,true).GetString(memory.ToArray()));}
        }
        static IEnumerable<Dictionary<string,object>> Assets(Dictionary<string,object> release)
        {
            object raw;if(!release.TryGetValue("assets",out raw)||!(raw is IEnumerable)||raw is string)throw new InvalidDataException("В выпуске нет списка файлов.");return ((IEnumerable)raw).Cast<object>().Select(x=>x as Dictionary<string,object>).Where(x=>x!=null);
        }
        static string Digest(Dictionary<string,object> asset){object raw;string value=asset.TryGetValue("digest",out raw)?raw as string:null;return value!=null&&value.StartsWith("sha256:",StringComparison.Ordinal)?value.Substring(7):null;}
        static void VerifyDigest(byte[] data,string digest){if(digest!=null&&!String.Equals(Hash(data),digest,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("SHA-256 архива не совпадает с GitHub.");}
        public static List<GitHubAsset> CatalogGitHubAssets(ModCatalog.Entry entry)
        {
            string repo=entry==null?null:ModCatalog.AuthorGitHubUrl(entry);if(String.IsNullOrEmpty(repo))throw new InvalidOperationException("Не найден однозначный GitHub автора.");var parts=new Uri(repo).AbsolutePath.Trim('/').Split('/');if(parts.Length!=2)throw new InvalidDataException("Некорректный репозиторий автора.");var release=ReadLatest(parts[0],parts[1]);var result=new List<GitHubAsset>();
            foreach(var a in Assets(release)){string name=Convert.ToString(a["name"]),url=Convert.ToString(a["browser_download_url"]);if(!name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))continue;if(!url.StartsWith("https://github.com/"+parts[0]+"/"+parts[1]+"/releases/download/",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Неожиданный адрес файла выпуска.");result.Add(new GitHubAsset{Name=name,Url=url,Sha256=Digest(a),Version=Convert.ToString(release["tag_name"]),Notes=Convert.ToString(release["body"]),Repository=repo});}return result;
        }
        public static Plan PrepareCatalogOnline(string root,ModCatalog.Entry entry){var assets=CatalogGitHubAssets(entry);if(assets.Count!=1)throw new InvalidOperationException("В выпуске "+assets.Count+" ZIP. Выберите подходящий на странице автора и импортируйте вручную.");return PrepareCatalogGitHub(root,entry,assets[0]);}
        public static Plan PrepareCatalogGitHub(string root,ModCatalog.Entry entry,GitHubAsset asset)
        {
            root=Validate(root,false);if(asset==null||asset.Repository!=ModCatalog.AuthorGitHubUrl(entry)||BuiltinEntry(entry))throw new InvalidDataException("Файл не принадлежит репозиторию выбранного мода.");var parts=new Uri(asset.Repository).AbsolutePath.Trim('/').Split('/');if(!asset.Url.StartsWith("https://github.com/"+parts[0]+"/"+parts[1]+"/releases/download/",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Некорректный адрес GitHub.");
            byte[] data;using(var memory=new MemoryStream()){FetchGitHub(asset.Url,memory,128*1024*1024,120000,true);data=memory.ToArray();}VerifyDigest(data,asset.Sha256);string folder=Path.Combine(SettingsDir,"downloads");
            for(var dir=new DirectoryInfo(folder);dir!=null;dir=dir.Parent)if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Папка загрузок содержит ссылку или junction.");
            Directory.CreateDirectory(folder);string file=Path.Combine(folder,Guid.NewGuid().ToString("N")+".zip"),partial=file+".partial";bool created=false;
            try{using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;output.Write(data,0,data.Length);}File.Move(partial,file);}
            catch{try{if(created&&File.Exists(partial))File.Delete(partial);}catch{}throw;}
            var plan=CommunityInstaller.Preview(root,entry.Id,file,asset.Sha256,PrimaryDlls(entry));return CommunityPlan(root,plan,entry.Name,asset.Version);
        }
    }
}
