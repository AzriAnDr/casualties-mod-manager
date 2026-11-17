using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

// Read-only metadata export. No mod assembly is loaded for execution.
public static class CoopDiagnostics
{
    const long FileLimit=128L*1024*1024, TotalLimit=512L*1024*1024;
    sealed class Dll
    {
        public string Path, Hash;
        public bool Disabled;
        public List<ModCatalog.PluginMetadata> Plugins;
    }
    sealed class Audit
    {
        public string Root;
        public long Bytes;
        public int Entries, Dlls, Unrecognised, Declarations;
        public readonly List<Dll> Plugins=new List<Dll>();
        public readonly SortedSet<string> Warnings=new SortedSet<string>(StringComparer.Ordinal);
    }
    public static string Build(string gameRoot)
    {
        if(String.IsNullOrWhiteSpace(gameRoot))throw new ArgumentException("Не задана папка игры.");
        string root=System.IO.Path.GetFullPath(gameRoot).TrimEnd('\\','/');
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("Папка игры не найдена.");
        CheckDirectory(root);
        ModCatalog.ClearDetectionCache();
        var audit=new Audit{Root=root};
        string assemblyHash=null;
        try {
            string game=Under(audit,"CasualtiesUnknown_Data/Managed/Assembly-CSharp.dll");
            if(!File.Exists(game))audit.Warnings.Add("Assembly-CSharp.dll не найден; версия игры по имени папки не определяется.");
            else assemblyHash=Hash(ReadImage(audit,game));
        } catch {audit.Warnings.Add("Assembly-CSharp.dll не удалось безопасно прочитать.");}
        try {
            string plugins=Under(audit,"BepInEx/plugins");
            if(Directory.Exists(plugins))Walk(audit,plugins,0);
            else audit.Warnings.Add("Папка BepInEx/plugins отсутствует.");
        } catch {audit.Warnings.Add("Обход папки плагинов прерван.");}
        var text=new StringBuilder();
        text.AppendLine("Casualties: Unknown — отчёт совместимости кооператива / формат 1");
        text.AppendLine("Снимок файлов на диске; не подтверждает, что эти DLL загружены в запущенной игре.");
        text.AppendLine("Одинаковые GUID и версии могут иметь разные SHA-256. Проверки подключения не изменяются.");
        text.AppendLine("Состав: DLL с объявлением BepInPlugin в BepInEx/plugins; ресурсы, конфиги, сохранения и DLL-зависимости не сравниваются.");
        text.AppendLine("Assembly-CSharp.dll SHA-256: "+(assemblyHash??"НЕ ПРОВЕРЕНО"));
        var together=audit.Plugins.Where(d=>d.Plugins.Any(p=>p.Guid=="CasualtiesMP")).OrderBy(d=>d.Path,StringComparer.Ordinal).ToList();
        text.AppendLine("Копий Together на диске: "+together.Count.ToString(CultureInfo.InvariantCulture));
        foreach(var dll in together)text.AppendLine("  "+State(dll)+" | "+Safe(dll.Path)+" | SHA-256 "+dll.Hash);
        if(together.Count==0)audit.Warnings.Add("Together с GUID CasualtiesMP не найден; совместимость Together не проверена.");
        foreach(var group in audit.Plugins.SelectMany(d=>d.Plugins.Select(p=>new{Dll=d,Plugin=p})).GroupBy(x=>x.Plugin.Guid,StringComparer.Ordinal).Where(g=>g.Count()>1).OrderBy(g=>g.Key,StringComparer.Ordinal))
            audit.Warnings.Add("Повтор GUID "+Safe(group.Key)+": "+group.Count().ToString(CultureInfo.InvariantCulture)+" объявлений (включая отключённые копии).");
        text.AppendLine();text.AppendLine("Плагины (версия из BepInPlugin; не версия ZIP):");
        foreach(var dll in audit.Plugins.OrderBy(d=>d.Path,StringComparer.Ordinal))
            foreach(var plugin in dll.Plugins.OrderBy(p=>p.Guid,StringComparer.Ordinal).ThenBy(p=>p.Version,StringComparer.Ordinal)) {
                text.AppendLine("  "+Safe(plugin.Guid)+" | "+Safe(plugin.Name)+" | "+Safe(plugin.Version)+" | "+State(dll));
                text.AppendLine("    "+Safe(dll.Path)+" | SHA-256 "+dll.Hash);
            }
        text.AppendLine("Прочитано DLL: "+audit.Dlls.ToString(CultureInfo.InvariantCulture)+"; без распознанного объявления плагина: "+audit.Unrecognised.ToString(CultureInfo.InvariantCulture)+" (включая нативные DLL/зависимости).");
        text.AppendLine();AppendHistoricalLog(audit,text);
        text.AppendLine();
        bool incomplete=audit.Warnings.Count>0;
        text.AppendLine("Статус проверки: "+(incomplete?"НЕПОЛНАЯ / есть предупреждения; совпадение не подтверждено":"файлы в указанной области прочитаны; совместимость запуска не подтверждена"));
        foreach(string warning in audit.Warnings)text.AppendLine("  Предупреждение: "+warning);
        // Paths and iteration order are deliberately absent from this comparison identity.
        // Duplicate declarations remain repeated, so extra copies change the fingerprint.
        var logical=audit.Plugins.SelectMany(d=>d.Plugins.Select(p=>p.Guid+"\t"+p.Version+"\t"+d.Hash+"\t"+(d.Disabled?"disabled":"enabled"))).OrderBy(s=>s,StringComparer.Ordinal).ToArray();
        string canonical="CoopDiagnostics/1\nAssembly-CSharp="+(assemblyHash??"unknown")+"\n"+String.Join("\n",logical);
        text.AppendLine("Логический SHA-256 (без путей и порядка, с состоянием и числом копий): "+Hash(Encoding.UTF8.GetBytes(canonical)));
        text.AppendLine("Совпадение этого отпечатка не доказывает совпадение ресурсов, зависимостей или настроек; неполные отчёты нельзя считать подтверждением совместимости.");
        return text.ToString();
    }
    static void Walk(Audit a,string folder,int depth)
    {
        if(depth>48){a.Warnings.Add("Пропущены слишком глубокие папки плагинов.");return;}
        try {
            CheckDirectory(folder);
            foreach(string item in Directory.EnumerateFileSystemEntries(folder)) {
                if(++a.Entries>20000){a.Warnings.Add("Достигнут предел20000 записей папок плагинов.");return;}
                try {
                    var attr=File.GetAttributes(item);
                    if((attr&FileAttributes.ReparsePoint)!=0){a.Warnings.Add("Пропущена ссылка/junction в папке плагинов.");continue;}
                    if((attr&FileAttributes.Directory)!=0){Walk(a,item,depth+1);if(a.Entries>20000)return;continue;}
                    bool disabled=item.EndsWith(".dll.cumm-disabled",StringComparison.OrdinalIgnoreCase);
                    if(!disabled&&!item.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))continue;
                    if(++a.Dlls>10000){a.Warnings.Add("Достигнут предел10000 DLL.");return;}
                    string relative=item.Substring(a.Root.Length+1).Replace('\\','/');
                    byte[] image=ReadImage(a,Under(a,relative));
                    var plugins=ModCatalog.InspectPlugins(image);
                    if(plugins.Count==0){a.Unrecognised++;continue;}
                    if(a.Declarations+plugins.Count>4096){a.Warnings.Add("Достигнут предел4096 объявлений плагинов в отчёте.");continue;}
                    a.Declarations+=plugins.Count;
                    a.Plugins.Add(new Dll{Path=relative,Hash=Hash(image),Disabled=disabled,Plugins=plugins});
                } catch {a.Warnings.Add("Не все DLL или метаданные удалось безопасно прочитать.");}
            }
        } catch {a.Warnings.Add("Не все папки плагинов доступны для чтения.");}
    }
    static byte[] ReadImage(Audit a,string path)
    {
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException();
        var before=new FileInfo(path);long length=before.Length;DateTime modified=before.LastWriteTimeUtc;
        if(length>FileLimit||length<0||a.Bytes+length>TotalLimit)throw new IOException();
        a.Bytes+=length;
        using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
            if(input.Length!=length)throw new IOException();
            byte[] bytes=new byte[(int)length];int offset=0,read;
            while(offset<bytes.Length&&(read=input.Read(bytes,offset,Math.Min(32768,bytes.Length-offset)))>0)offset+=read;
            if(offset!=bytes.Length||input.ReadByte()!=-1)throw new IOException();
            var after=new FileInfo(path);
            if(after.Length!=length||after.LastWriteTimeUtc!=modified||(after.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException();
            return bytes;
        }
    }
    static void AppendHistoricalLog(Audit a,StringBuilder text)
    {
        text.AppendLine("Последний сохранённый BepInEx/LogOutput.log (история, не состояние текущего процесса):");
        try {
            string path=Under(a,"BepInEx/LogOutput.log");
            if(!File.Exists(path)){text.AppendLine("  Лог отсутствует; загруженные версии неизвестны.");return;}
            var before=new FileInfo(path);DateTime modified=before.LastWriteTimeUtc;long length=before.Length;bool truncated=length>2*1024*1024;
            string log;
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite)) {
                int size=(int)Math.Min(length,2*1024*1024);byte[] bytes=new byte[size];int offset=0,read;
                while(offset<size&&(read=stream.Read(bytes,offset,Math.Min(32768,size-offset)))>0)offset+=read;
                if(offset!=size)throw new IOException();log=Encoding.UTF8.GetString(bytes);
            }
            var after=new FileInfo(path);if(after.Length!=length||after.LastWriteTimeUtc!=modified)throw new IOException();
            text.AppendLine("  Время изменения лога UTC: "+modified.ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture));
            text.AppendLine("  Игра из лога: "+LastVersion(log,@"Game version: ""([A-Za-z0-9._+\-]{1,64})"""));
            text.AppendLine("  Together из лога: "+LastVersion(log,@"Plugin CasualtiesMP version ([A-Za-z0-9._+\-]{1,64}) is loaded!"));
            if(truncated){text.AppendLine("  Прочитаны только первые2МиБ лога.");a.Warnings.Add("Исторический лог прочитан не полностью из-за размера.");}
        } catch {text.AppendLine("  Лог недоступен или менялся во время чтения; загруженные версии неизвестны.");}
    }
    static string LastVersion(string log,string pattern){var matches=Regex.Matches(log,pattern,RegexOptions.CultureInvariant);return matches.Count>0?Safe(matches[matches.Count-1].Groups[1].Value):"не найдена";}
    static string State(Dll d){return d.Disabled?"отключён (.cumm-disabled)":"на диске включён";}
    static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
    static string Under(Audit a,string relative)
    {
        if(String.IsNullOrWhiteSpace(relative)||relative.IndexOf(':')>=0||relative.StartsWith("/")||relative.StartsWith("\\")||relative.Replace('\\','/').Split('/').Any(p=>p=="."||p==".."))throw new IOException();
        string full=System.IO.Path.GetFullPath(System.IO.Path.Combine(a.Root,relative.Replace('/',System.IO.Path.DirectorySeparatorChar)));
        if(!full.StartsWith(a.Root+System.IO.Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException();
        CheckDirectory(System.IO.Path.GetDirectoryName(full));
        if((File.Exists(full)||Directory.Exists(full))&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException();
        return full;
    }
    static void CheckDirectory(string path){for(var d=new DirectoryInfo(path);d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Путь содержит ссылку или junction.");}
    static string Safe(string value)
    {
        string text=value??"";text=new string(text.Where(c=>!Char.IsControl(c)&&!(c>='\u202a'&&c<='\u202e')&&!(c>='\u2066'&&c<='\u2069')).ToArray());
        // Metadata is untrusted text and can contain local absolute paths or account names.
        text=Regex.Replace(text,@"[A-Za-z]:[\\/][^|]*|\\\\[^|]*","[путь скрыт]");
        string user=Environment.UserName;if(!String.IsNullOrEmpty(user))text=Regex.Replace(text,Regex.Escape(user),"[пользователь]",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        return text.Length<=1024?text:text.Substring(0,1024)+"…";
    }
}
