using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;

public sealed partial class Manager
{
    static Dictionary<string,ModuleCatalog.Package> onlineModules=new Dictionary<string,ModuleCatalog.Package>();
    static string selectedGameVersion;
    bool checkingModules;
    private async Task CheckModuleUpdates(bool announce)
    {
        if(checkingModules)return;checkingModules=true;
        try{var packages=await Task.Run(()=>ModuleCatalog.Check());if(IsDisposed)return;onlineModules=packages;
            if(announce)try{var release=await Task.Run(()=>ReadGitHubRelease("creaturefeaturelarry","casualties-together"));KnownTogetherVersion=Convert.ToString(release["tag_name"]).TrimStart('v','V');}catch(Exception error){WriteLog("Together: версия недоступна. "+error.Message);}
            if(IsDisposed)return;RefreshList();if(announce)SetStatus("Модули: каталог версий обновлён. Выберите модуль и нажмите «Установить / обновить».",false);}
        catch(Exception error){if(!IsDisposed){WriteLog("Обновления модулей: "+error.Message);if(announce)SetStatus("Каталог модулей недоступен. Встроенные пакеты доступны; повторите проверку позже.",true);}}
        finally{checkingModules=false;}
    }
    static ModuleCatalog.Package RecommendedPackage(Module module)
    {
        ModuleCatalog.Package package;if(module.Resource==null||!onlineModules.TryGetValue(module.Id,out package))return null;
        var compare=ModCatalog.CompareVersions(BundledVersion(module.Id),package.Version);
        if(compare!=ModCatalog.VersionComparison.Older&&compare!=ModCatalog.VersionComparison.Equal)return null;
        if(module.Id!="bepinex"&&!String.IsNullOrEmpty(selectedGameVersion)&&!package.GameVersions.Contains(selectedGameVersion,StringComparer.Ordinal))return null;
        return package;
    }
    static string AvailableModuleVersion(Module module){var package=RecommendedPackage(module);return package==null?BundledVersion(module.Id):package.Version;}
    private async Task InstallBuiltIn()
    {
        Module module=Selected();if(module==null||busy)return;
        if(module.Id=="together"){ActionRun("установка / обновление",delegate(string root,Module m){if(!EnsureLoaderForCommunity(root,true))return null;return OnlineInstall(root,m);});return;}
        try
        {
            busy=true;UpdateDetails();string root=Validate(game.Text,true);selectedGameVersion=ReadGameVersion(root);
            var package=RecommendedPackage(module);Dictionary<string,byte[]> files;string version;string[] games=null;
            if(package!=null)
            {
                SetStatus(module.Name+": скачиваю пакет "+package.Version+"…",false);
                string archive=await Task.Run(()=>ModuleCatalog.Download(package,Path.Combine(SettingsDir,"downloads")));
                using(var input=File.OpenRead(archive))files=ReadZip(input,module);version=package.Version;games=package.GameVersions;
            }
            else{using(var input=Resource(module))files=ReadZip(input,module);version=BundledVersion(module.Id);}
            if(IsDisposed)return;
            string installed=VersionOf(root,module,Load(root,module));
            if(ModCatalog.CompareVersions(installed,version)==ModCatalog.VersionComparison.Newer)throw new InvalidOperationException("Установлена более новая версия "+installed+". Понижение до "+version+" не выполнено; повторите онлайн-проверку модулей.");
            if(module.Id!="bepinex"&&!EnsureLoaderForCommunity(root,false))return;
            if(!Preview(files,"«"+module.Name+"» · "+version+"\r\nИсточник: "+(package==null?"встроенный пакет":"проверенный каталог пакетов, SHA-256 совпал")+". Настройки сохраняются.","Установить / обновить")){SetStatus(module.Name+": установка отменена.",false);return;}
            using(var operationLock=LockGame(root))Install(root,module,files,version,games);
            SetStatus(module.Name+": установлен / обновлён до "+version+". Лаунчер не заменялся.",false);WriteLog(module.Name+": установлен пакет "+version+"; backup: "+LastBackup);
        }
        catch(Exception error){ShowFailure(module.Name+": установка / обновление",error);}
        finally{busy=false;if(!IsDisposed){RefreshList();UpdateDetails();}}
    }
    private void ImportKnownArchive(ArchiveInbox.Item item)
    {
        if(busy||!item.Ready)return;
        Module module=AllModules.FirstOrDefault(m=>m.Id==item.Kind);if(module==null)return;
        try
        {
            busy=true;UpdateDetails();string root=Validate(game.Text,true);Dictionary<string,byte[]> files;
            using(var input=File.OpenRead(item.Path))files=ReadZip(input,module);
            if(module.Id!="bepinex"&&!EnsureLoaderForCommunity(root,false))return;
            if(!Preview(files,"Проверить ZIP для «"+module.Name+"». Структура архива не подтверждает его автора.","Установить выбранные файлы"))return;
            using(var operationLock=LockGame(root))Install(root,module,files,"import");
            SetStatus(module.Name+": ZIP установлен; настройки сохранены.",false);
        }
        catch(Exception error){ShowFailure(module.Name+": импорт ZIP",error);}
        finally{busy=false;if(!IsDisposed){RefreshList();UpdateDetails();}}
    }
    static string ReadGameVersion(string root)
    {
        string path=Under(root,"CasualtiesUnknown_Data/Lang/EN.json");if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>8*1024*1024)throw new InvalidDataException("Файл языка игры слишком велик.");
        var lang=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));object value;return lang!=null&&lang.TryGetValue("version",out value)?Convert.ToString(value):null;
    }
    static string[] PackageGameVersions(Module module,Dictionary<string,byte[]> files,string[] declared)
    {
        if(module.Id!="russian"&&module.Id!="wiki")return new string[0];
        if(module.Id=="russian")
        {
            byte[] raw;if(!files.TryGetValue(module.Prefix+"ru-RU.json",out raw))throw new InvalidDataException("В пакете нет ru-RU.json.");
            var locale=Json.Deserialize<Dictionary<string,object>>(new UTF8Encoding(false,true).GetString(raw).TrimStart('\ufeff'));object value;
            if(locale!=null&&locale.TryGetValue("version",out value)&&value is string){System.Version version;if(System.Version.TryParse((string)value,out version)){if(declared!=null&&declared.Length>0&&!declared.Contains((string)value,StringComparer.Ordinal))throw new InvalidDataException("Версия языка пакета не совпадает с каталогом модулей.");return new[]{(string)value};}}
            throw new InvalidDataException("Пакет перевода не указывает числовую версию игры.");
        }
        return declared!=null&&declared.Length>0?declared:new[]{"1.0.2"}; // Legacy manual wiki ZIP has no compatibility manifest.
    }
}
