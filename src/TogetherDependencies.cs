using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;

public sealed partial class Manager
{
    const string SharedCorePath="BepInEx/plugins/CUCoreLib.dll";
    const string SharedCoreId="shared-cucorelib";
    sealed class CoreCopy { public string Path, Hash; public bool Disabled; }

    // The library is shared with other mods. Never put it in Together's ownership
    // list; a fresh installation gets a separate record in the same transaction.
    static Dictionary<string,byte[]> PrepareTogetherDependencies(string root,Module module,Dictionary<string,byte[]> files,Dictionary<string,byte[]> dependencyWrites)
    {
        var result=new Dictionary<string,byte[]>(files,StringComparer.OrdinalIgnoreCase);
        byte[] library;
        if(module.Id!="together"||!result.TryGetValue(SharedCorePath,out library))return result;
        ValidateTogetherCoreLibrary(library);
        result.Remove(SharedCorePath);
        string wanted=Hash(library);
        var records=CommunityInstaller.ListInstalled(root);
        var owned=records.SingleOrDefault(r=>r.Id==SharedCoreId);
        if(owned!=null) {
            if(owned.Disabled)throw new InvalidOperationException("CUCoreLib отключена. Включите общую библиотеку перед обновлением Together.");
            if(owned.Files.Count!=1||!String.Equals(owned.Files[0].Path,SharedCorePath,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Неожиданный состав записи общей CUCoreLib. Файлы сохранены без изменений.");
            string current=Under(root,SharedCorePath);
            if(Directory.Exists(current)||(File.Exists(current)&&Hash(current)!=owned.Files[0].Hash))throw new InvalidOperationException("Управляемая CUCoreLib изменена. Сохраните изменения отдельно перед обновлением.");
        }
        var copies=FindCoreCopies(root);
        if(copies.Count>1)throw new InvalidOperationException("Найдено несколько копий CUCoreLib. Устраните дубли через их карточки перед обновлением Together.");
        if(copies.Count==1) {
            var copy=copies[0];
            if(copy.Disabled)throw new InvalidOperationException("CUCoreLib установлена, но отключена. Включите библиотеку перед обновлением Together.");
            if(owned!=null&&!String.Equals(copy.Path,SharedCorePath,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("CUCoreLib найдена вне пути её управляемой установки. Проверьте записи библиотеки перед обновлением.");
            if(copy.Hash==wanted)return result; // Reuse identical bytes without claiming somebody else's files.
            if(owned==null||!String.Equals(copy.Path,SharedCorePath,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Установленная CUCoreLib отличается от файла в архиве Together. Обновите библиотеку через её карточку или сохраните и удалите прежнюю копию отдельно; она не перезаписана.");
        }
        else {
            if(File.Exists(Under(root,SharedCorePath))||Directory.Exists(Under(root,SharedCorePath))||File.Exists(Under(root,SharedCorePath+".cumm-disabled")))throw new InvalidOperationException("Путь CUCoreLib занят нераспознанным файлом или папкой. Они сохранены без изменений.");
        }
        foreach(var record in records.Where(r=>r.Id!=SharedCoreId))
            if(record.Files.Any(f=>String.Equals(f.Path,SharedCorePath,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Путь CUCoreLib принадлежит другому управляемому моду: "+record.Name);
        foreach(var other in AllModules.Where(m=>m.Id!="together")) {
            var record=Load(root,other);
            if(record!=null&&record.Files.Any(f=>String.Equals(f.Path,SharedCorePath,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("Путь CUCoreLib принадлежит модулю "+other.Name);
        }
        var metadata=ModCatalog.InspectPlugins(library).Single(p=>p.Guid=="net.cucorelib");
        var next=new CommunityInstaller.InstalledRecord { Id=SharedCoreId,Name="CUCoreLib · общая библиотека",Version=metadata.Version,VersionSource="plugin-metadata",CatalogArchiveUnverified=true,Files=new List<CommunityInstaller.OwnedFile>{new CommunityInstaller.OwnedFile{Path=SharedCorePath,Hash=wanted}} };
        dependencyWrites.Add(SharedCorePath,library);
        dependencyWrites.Add(State+"community/"+SharedCoreId+".json",Encoding.UTF8.GetBytes(Json.Serialize(next)));
        return result;
    }

    static List<CoreCopy> FindCoreCopies(string root)
    {
        var found=new List<CoreCopy>();
        var pending=new Queue<KeyValuePair<string,int>>();pending.Enqueue(new KeyValuePair<string,int>("BepInEx/plugins",0));
        int entries=0;long total=0;
        while(pending.Count>0) {
            var folder=pending.Dequeue();
            if(folder.Value>48)throw new IOException("Слишком глубокая папка плагинов при проверке CUCoreLib.");
            string full=Under(root,folder.Key+"/check");full=Path.GetDirectoryName(full);
            if(!Directory.Exists(full))continue;
            foreach(string path in Directory.EnumerateFileSystemEntries(full)) {
                if(++entries>20000)throw new IOException("Превышен предел проверки файлов CUCoreLib.");
                string relative=folder.Key+"/"+Path.GetFileName(path);Under(root,relative);
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Проверка CUCoreLib встретила ссылку или junction.");
                if(Directory.Exists(path)){pending.Enqueue(new KeyValuePair<string,int>(relative,folder.Value+1));continue;}
                bool disabled=path.EndsWith(".dll.cumm-disabled",StringComparison.OrdinalIgnoreCase);
                if(!disabled&&!path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))continue;
                byte[] bytes;
                using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    if(input.Length>MaximumZipFileBytes||(total+=input.Length)>512L*1024*1024)throw new IOException("Превышен предел чтения DLL при проверке CUCoreLib.");
                    bytes=new byte[(int)input.Length];int offset=0,read;
                    while(offset<bytes.Length&&(read=input.Read(bytes,offset,Math.Min(32768,bytes.Length-offset)))>0)offset+=read;
                    if(offset!=bytes.Length||input.ReadByte()!=-1)throw new IOException("DLL изменилась при чтении.");
                }
                var declarations=ModCatalog.InspectPlugins(bytes).Where(p=>p.Guid=="net.cucorelib").ToList();
                foreach(var declaration in declarations)found.Add(new CoreCopy{Path=relative,Hash=Hash(bytes),Disabled=disabled});
            }
        }
        return found;
    }
}
