using System;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

// Descriptive metadata only: no plugin or script is ever loaded for execution.
public static class ModCatalog
{
    public sealed class Entry
    {
        public string Id, Name, Version, Author, Summary, Description;
        public bool? ContainsAdultContent;
        public int NexusModId;
        public string NexusGameDomain, DownloadUrl, ArchiveSha256, LastUpdated, BepinexVersion, DllVersion;
        public Dictionary<string,string> Links = Map(), BepinexPlugins = Map(), DllSha256s = Map(), DllVersions = Map();
        public List<string> DllNames = new List<string>(), Dependencies = new List<string>();
    }
    public sealed class InstalledInfo
    {
        public bool Installed, Ambiguous, Disabled, Partial;
        public bool? Managed;
        public string Version, MatchReason, Confidence, ArchiveVersion;
        public List<string> Files = new List<string>();
        public Dictionary<string,string> VersionsByFile = Map();
    }
    public enum VersionComparison { Older, Equal, Newer, Unknown, DifferentChannel }
    public enum UpdateState { UpdateAvailable, Current, InstalledNewer, Unknown, DifferentChannel, NotInstalled }
    public static bool IsStale { get; private set; }
    public static string LastError { get; private set; }
    public static DateTime? LastSuccessfulFetchUtc { get; private set; }
    public static string SourceUsed { get; private set; }
    public const string PrimaryUrl = "https://jimmyking9999999.github.io/Metadata-generator/nexusmods.json";
    public const string FallbackUrl = "https://raw.githubusercontent.com/jimmyking9999999/Metadata-generator/main/nexusmods.json";
    const int Limit = 8*1024*1024;
    const string State = "BepInEx/config/CasualtiesModManager/";
    static readonly object Gate = new object(), ScanGate = new object();
    static readonly string CacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CasualtiesModManager","catalog");
    static JavaScriptSerializer Json { get { return new JavaScriptSerializer { MaxJsonLength=Limit, RecursionLimit=64 }; } }
    static Dictionary<string,HashSet<string>> HashOwners = new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string,Scan> Scans = new Dictionary<string,Scan>(StringComparer.OrdinalIgnoreCase);
    sealed class CacheMeta { public string Source, ETag, LastModified, FetchedUtc; }
    sealed class ScanFile { public string Relative, Name, Hash, Version; }
    sealed class Scan { public DateTime Utc; public List<ScanFile> Files=new List<ScanFile>(); public string Error; }
    static Dictionary<string,string> Map() { return new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); }

    public static List<Entry> Load(bool force=false)
    {
        lock(Gate)
        {
            LastError=null; SourceUsed=null; IsStale=true;
            List<Entry> cached=null; CacheMeta meta=null; string cachePath=Path.Combine(CacheRoot,"catalog.json");
            try {
                SafeDirectory(CacheRoot,false);
                if(File.Exists(cachePath)) cached=Parse(ReadBounded(cachePath),false);
                string metadata=Path.Combine(CacheRoot,"cache-meta.json");
                if(File.Exists(metadata)) meta=Json.Deserialize<CacheMeta>(ReadBounded(metadata,16384));
            } catch(Exception e) { LastError="Кеш каталога недоступен: "+e.Message; }
            DateTime fetched;
            LastSuccessfulFetchUtc=meta!=null&&DateTime.TryParse(meta.FetchedUtc,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out fetched)?(DateTime?)fetched.ToUniversalTime():null;
            if(!force&&cached!=null&&LastSuccessfulFetchUtc.HasValue&&DateTime.UtcNow-LastSuccessfulFetchUtc.Value<TimeSpan.FromHours(6)&&LastSuccessfulFetchUtc.Value<=DateTime.UtcNow.AddMinutes(5)) {
                IsStale=false;SourceUsed="cache";IndexHashes(cached);return cached;
            }
            var failures=new List<string>();
            foreach(string endpoint in new[]{PrimaryUrl,FallbackUrl}) {
                try {
                    string etag,modified;bool unchanged;
                    string body=Fetch(endpoint,cached!=null&&meta!=null&&meta.Source==endpoint?meta:null,out etag,out modified,out unchanged);
                    List<Entry> entries=unchanged?cached:Parse(body,false);
                    if(entries==null)throw new InvalidDataException("HTTP304 получен без пригодного кеша.");
                    var fresh=new CacheMeta {Source=endpoint,ETag=etag??(unchanged&&meta!=null?meta.ETag:null),LastModified=modified??(unchanged&&meta!=null?meta.LastModified:null),FetchedUtc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture)};
                    LastSuccessfulFetchUtc=DateTime.UtcNow;IsStale=false;SourceUsed=endpoint;
                    try {SafeDirectory(CacheRoot,true);if(!unchanged)AtomicWrite(cachePath,body);AtomicWrite(Path.Combine(CacheRoot,"cache-meta.json"),Json.Serialize(fresh));}
                    catch(Exception e){LastError="Каталог загружен, но кеш не сохранён: "+e.Message;}
                    IndexHashes(entries);return entries;
                } catch(Exception e){failures.Add(endpoint+": "+e.Message);}
            }
            LastError="Нет связи с каталогом. "+String.Join("; ",failures.ToArray());
            if(cached!=null){SourceUsed="cache (offline)";IndexHashes(cached);return cached;}
            using(Stream snapshot=Assembly.GetExecutingAssembly().GetManifestResourceStream("CatalogSnapshot.json")) {
                if(snapshot==null)throw new IOException(LastError+" В комплекте отсутствует офлайн-снимок.");
                List<Entry> entries=Parse(ReadStream(snapshot,Limit),false);SourceUsed="embedded snapshot";IndexHashes(entries);return entries;
            }
        }
    }
    public static List<Entry> Parse(string source) {return Parse(source,true);}
    static List<Entry> Parse(string source,bool buildIndex)
    {
        if(source==null||source.Length>Limit)throw new InvalidDataException("Каталог превышает8МиБ.");
        object raw;try{raw=Json.DeserializeObject(source.TrimStart('\ufeff'));}catch(Exception e){throw new InvalidDataException("Некорректный JSON каталога.",e);}
        var sequence=raw as IEnumerable;
        if(sequence==null||raw is string||raw is IDictionary)throw new InvalidDataException("Ожидался JSON-массив модов.");
        var result=new List<Entry>();var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int count=0;
        foreach(object item in sequence) {
            if(++count>10000)throw new InvalidDataException("В каталоге более10000 записей.");
            var data=item as Dictionary<string,object>;if(data==null)throw new InvalidDataException("Запись каталога не является объектом.");
            Entry e=ParseEntry(data);if(!ids.Add(e.Id))throw new InvalidDataException("Повтор ID: "+e.Id);result.Add(e);
        }
        if(result.Count==0)throw new InvalidDataException("Каталог пуст.");
        if(buildIndex)lock(Gate)IndexHashes(result);return result;
    }
    static Entry ParseEntry(Dictionary<string,object> d)
    {
        var e=new Entry {Id=Text(d,"Id",128),Name=Text(d,"Name",512),Version=Text(d,"Version",128),Author=Text(d,"Author",512)};
        object id;int nexus;if(!d.TryGetValue("NexusModId",out id)||!Int32.TryParse(Convert.ToString(id,CultureInfo.InvariantCulture),out nexus)||nexus<=0)throw new InvalidDataException("Некорректный NexusModId.");
        e.NexusModId=nexus;e.NexusGameDomain=Text(d,"NexusGameDomain",64);
        if(e.Id!="nexus-"+nexus.ToString(CultureInfo.InvariantCulture)||e.NexusGameDomain!="scavprototype"||String.IsNullOrWhiteSpace(e.Name))throw new InvalidDataException("ID или игра записи каталога не совпадают.");
        e.Summary=Text(d,"Summary",32768);e.Description=Text(d,"Description",262144);e.LastUpdated=Text(d,"LastUpdated",64);e.BepinexVersion=Text(d,"bepinexVersion",128);e.DllVersion=Text(d,"dllVersion",128);
        object adult;if(d.TryGetValue("ContainsAdultContent",out adult)&&adult is bool)e.ContainsAdultContent=(bool)adult;
        e.Links=ReadMap(d,"Links",false);e.BepinexPlugins=ReadMap(d,"bepinexPlugins",false);e.DllVersions=ReadMap(d,"dllVersions",true);e.DllSha256s=ReadMap(d,"dllSHA256s",true);
        foreach(string key in e.DllSha256s.Keys.ToArray())if(!IsHash(e.DllSha256s[key]))e.DllSha256s.Remove(key);else e.DllSha256s[key]=e.DllSha256s[key].ToLowerInvariant();
        foreach(string key in e.Links.Keys.ToArray()){Uri uri;if(!Uri.TryCreate(e.Links[key],UriKind.Absolute,out uri)||uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0)e.Links.Remove(key);}
        foreach(string key in e.BepinexPlugins.Keys.ToArray())if(!Regex.IsMatch(key,@"^[A-Za-z0-9._-]{1,256}$"))e.BepinexPlugins.Remove(key);
        e.DllNames=Strings(d,"dllNames",512).Where(IsDllName).Concat(e.DllSha256s.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        e.Dependencies=Strings(d,"Dependencies",256).Where(s=>Regex.IsMatch(s,@"^nexus-[1-9][0-9]{0,9}$")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string archive=Text(d,"SHA256",128);if(IsHash(archive))e.ArchiveSha256=archive.ToLowerInvariant();
        string download=Text(d,"DownloadUrl",4096);Uri nxm;
        if(Uri.TryCreate(download,UriKind.Absolute,out nxm)&&nxm.Scheme=="nexus"&&nxm.Host=="scavprototype"&&nxm.Query.Length==0&&nxm.Fragment.Length==0&&Regex.IsMatch(nxm.AbsolutePath,"^/"+nexus.ToString(CultureInfo.InvariantCulture)+@"/[1-9][0-9]*$"))e.DownloadUrl=download;
        return e;
    }
    static string Text(Dictionary<string,object>d,string key,int max){object v;if(!d.TryGetValue(key,out v)||v==null)return null;if(!(v is string))throw new InvalidDataException("Неверный тип поля "+key);string s=(string)v;if(s.Length>max)throw new InvalidDataException("Слишком длинное поле "+key);return s;}
    static Dictionary<string,string> ReadMap(Dictionary<string,object>d,string key,bool dllKeys)
    {
        var result=Map();object v;if(!d.TryGetValue(key,out v)||v==null)return result;var map=v as Dictionary<string,object>;
        if(map==null||map.Count>1024)throw new InvalidDataException("Некорректный словарь "+key);
        foreach(var pair in map)if(pair.Value is string&&pair.Key.Length<=256&&((string)pair.Value).Length<=(key=="Links"?4096:128)&&(!dllKeys||IsDllName(pair.Key)))result[pair.Key]=(string)pair.Value;
        return result;
    }
    static List<string> Strings(Dictionary<string,object>d,string key,int limit)
    {
        var result=new List<string>();object v;if(!d.TryGetValue(key,out v)||v==null)return result;var list=v as IEnumerable;
        if(list==null||v is string||v is IDictionary)throw new InvalidDataException("Некорректный список "+key);
        int count=0;foreach(object item in list){if(++count>limit)throw new InvalidDataException("Слишком длинный список "+key);if(item is string&&((string)item).Length<=512)result.Add((string)item);}return result;
    }
    static bool IsDllName(string s){return !String.IsNullOrWhiteSpace(s)&&s.Length<=255&&s.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&s.IndexOfAny(new[]{'/','\\',':','\0'})<0&&!s.EndsWith(" ",StringComparison.Ordinal)&&!s.StartsWith(".",StringComparison.Ordinal);}
    static bool IsHash(string value){return value!=null&&Regex.IsMatch(value,"^[a-fA-F0-9]{64}$");}
    static void IndexHashes(IEnumerable<Entry>entries)
    {
        var index=new Dictionary<string,HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach(Entry e in entries)foreach(string hash in e.DllSha256s.Values){HashSet<string>owners;if(!index.TryGetValue(hash,out owners))index[hash]=owners=new HashSet<string>(StringComparer.OrdinalIgnoreCase);owners.Add(e.Id);}HashOwners=index;
    }
    public static string NexusUrl(Entry e){return e!=null&&e.NexusModId>0&&e.NexusGameDomain=="scavprototype"?"https://www.nexusmods.com/scavprototype/mods/"+e.NexusModId.ToString(CultureInfo.InvariantCulture):null;}
    public static string AuthorGitHubUrl(Entry e)
    {
        if(e==null)return null;var urls=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var pair in e.Links)if(Regex.IsMatch(pair.Key,@"^(github|source|sourcecode|source code|repository)$",RegexOptions.IgnoreCase)){string repo=GitHubRepo(pair.Value);if(repo!=null)urls.Add(repo);}
        string text=e.Description??"";
        foreach(Match m in Regex.Matches(text,@"https://github\.com/[A-Za-z0-9-]+/[A-Za-z0-9._-]+")){
            int start=Math.Max(0,m.Index-100),end=Math.Min(text.Length,m.Index+m.Length+100);string context=text.Substring(start,end-start).Replace(m.Value,"");
            if(!Regex.IsMatch(context,@"source\s*code|repository|исходн|исходники|репозитор|源码|源代码|\bgithub\b",RegexOptions.IgnoreCase)||Regex.IsMatch(context,@"dependenc|requirement|credits?|thanks|зависим|благодар|依赖|前置",RegexOptions.IgnoreCase))continue;
            string repo=GitHubRepo(m.Value);if(repo!=null)urls.Add(repo);
        }
        return urls.Count==1?urls.First():null;
    }
    static string GitHubRepo(string text)
    {
        Uri uri;if(!Uri.TryCreate(text,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.Host!="github.com"||uri.UserInfo.Length!=0||!uri.IsDefaultPort)return null;
        string[]p=uri.AbsolutePath.Trim('/').Split('/');if(p.Length<2||!Regex.IsMatch(p[0],@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}$")||!Regex.IsMatch(p[1],@"^[A-Za-z0-9._-]{1,100}$")||p[1]=="."||p[1]=="..")return null;
        if(new[]{"settings","login","features","marketplace","topics","collections","organizations","orgs","users","search"}.Contains(p[0],StringComparer.OrdinalIgnoreCase))return null;
        return "https://github.com/"+p[0]+"/"+(p[1].EndsWith(".git",StringComparison.OrdinalIgnoreCase)?p[1].Substring(0,p[1].Length-4):p[1]);
    }
    public static string SummaryText(Entry e,int maxLength=260){return PlainText(e==null?"":(!String.IsNullOrWhiteSpace(e.Summary)?e.Summary:e.Description),maxLength).Replace("\n"," ");}
    public static string PlainText(string text,int maxLength=65536)
    {
        if(maxLength<0||maxLength>262144)throw new ArgumentOutOfRangeException("maxLength");if(String.IsNullOrEmpty(text)||maxLength==0)return "";if(text.Length>262144)text=text.Substring(0,262144);
        text=WebUtility.HtmlDecode(text);text=Regex.Replace(text,@"<(script|style)\b[^>]*>.*?</\1\s*>","",RegexOptions.IgnoreCase|RegexOptions.Singleline);text=Regex.Replace(text,@"<br\s*/?>|</(?:p|div|li)\s*>","\n",RegexOptions.IgnoreCase);
        text=Regex.Replace(text,@"<[^>\r\n]{0,2048}>","");text=Regex.Replace(text,@"\[[^\]\r\n]{0,1024}\]","");var clean=new StringBuilder();
        foreach(char c in text)if((!Char.IsControl(c)||c=='\r'||c=='\n'||c=='\t')&&!(c>='\u202a'&&c<='\u202e')&&!(c>='\u2066'&&c<='\u2069'))clean.Append(c);
        text=clean.ToString().Replace("\r\n","\n").Replace('\r','\n');text=Regex.Replace(text,@"[ \t]+"," ");text=Regex.Replace(text,@"\n[ \t]*\n(?:[ \t]*\n)+","\n\n").Trim();return text.Length<=maxLength?text:text.Substring(0,Math.Max(0,maxLength-1))+"…";
    }
    public static void ClearDetectionCache(){lock(ScanGate)Scans.Clear();}
    public static InstalledInfo Detect(string root,Entry entry)
    {
        if(entry==null)throw new ArgumentNullException("entry");root=GameRoot(root);Scan scan=GetScan(root);InstalledInfo managed=ReadManaged(root,entry,scan);if(managed!=null)return managed;
        var result=new InstalledInfo{Confidence="none",Managed=false};var exact=scan.Files.Where(f=>f.Hash!=null&&entry.DllSha256s.Values.Contains(f.Hash,StringComparer.OrdinalIgnoreCase)).ToList();
        var unique=exact.Where(f=>{HashSet<string>owners;return HashOwners.TryGetValue(f.Hash,out owners)&&owners.Count==1&&owners.Contains(entry.Id);}).ToList();
        if(unique.Count>0){result.Installed=true;result.Confidence="exact-sha256";result.Files=unique.Select(f=>f.Relative).ToList();result.Partial=entry.DllSha256s.Any(pair=>!exact.Any(f=>String.Equals(f.Hash,pair.Value,StringComparison.OrdinalIgnoreCase)&&String.Equals(f.Name,pair.Key,StringComparison.OrdinalIgnoreCase)));result.MatchReason="Точное совпадение SHA-256 DLL"+(result.Partial?"; часть файлов каталога не найдена":"");FillVersions(result,unique);}
        else{
            var candidates=scan.Files.Where(f=>entry.DllNames.Contains(f.Name,StringComparer.OrdinalIgnoreCase)).ToList();result.Files=candidates.Select(f=>f.Relative).ToList();
            if(candidates.Count>0||exact.Count>0){result.Ambiguous=true;result.Confidence=exact.Count>0?"shared-dependency-sha256":"filename-only";result.MatchReason=exact.Count>0?"Найдена общая DLL: её SHA-256 встречается у нескольких модов":"Совпало только имя DLL; принадлежность моду не подтверждена";FillVersions(result,candidates);}else result.MatchReason="Подтверждённые файлы мода не найдены";
        }
        if(!String.IsNullOrEmpty(scan.Error))result.MatchReason+=". Сканирование неполное: "+scan.Error;return result;
    }
    static InstalledInfo ReadManaged(string root,Entry e,Scan scan)
    {
        try{
            string file=Under(root,State+"community/"+e.Id+".json");if(!File.Exists(file))return null;var d=Json.Deserialize<Dictionary<string,object>>(ReadBounded(file,1024*1024));object id;if(d==null||!d.TryGetValue("Id",out id)||!(id is string)||(string)id!=e.Id)return null;
            object flag;bool disabled=d.TryGetValue("Disabled",out flag)&&flag is bool&&(bool)flag;var info=new InstalledInfo{Managed=true,Disabled=disabled,Confidence="managed-manifest",ArchiveVersion=Text(d,"Version",128)};object files;if(!d.TryGetValue("Files",out files)||!(files is IEnumerable))return null;
            var found=new List<ScanFile>();int count=0;
            foreach(object raw in (IEnumerable)files){
                if(++count>4096)throw new InvalidDataException("Слишком много файлов манифеста.");var record=raw as Dictionary<string,object>;if(record==null)return null;string relative=Text(record,"Path",1024),expected=Text(record,"Hash",128);if(String.IsNullOrWhiteSpace(relative)||!IsHash(expected))return null;
                Under(root,relative);relative=relative.Replace('\\','/');if(!relative.StartsWith("BepInEx/plugins/",StringComparison.OrdinalIgnoreCase)&&!relative.StartsWith("BepInEx/patchers/",StringComparison.OrdinalIgnoreCase))continue;if(!relative.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))continue;
                string actual=relative+(disabled?".cumm-disabled":"");ScanFile match=scan.Files.FirstOrDefault(f=>String.Equals(f.Relative,actual,StringComparison.OrdinalIgnoreCase));info.Files.Add(actual);if(match==null||!String.Equals(match.Hash,expected,StringComparison.OrdinalIgnoreCase))info.Ambiguous=true;if(match!=null)found.Add(match);
            }
            info.Installed=found.Count>0;info.Partial=info.Ambiguous;info.MatchReason=info.Ambiguous?"Управляемая установка: DLL отсутствуют или изменены":"Принадлежность подтверждена манифестом менеджера и SHA-256";FillVersions(info,found);return info;
        }catch{return null;}
    }
    static void FillVersions(InstalledInfo info,IEnumerable<ScanFile>files){foreach(ScanFile f in files)if(f.Version!=null)info.VersionsByFile[f.Relative]=f.Version;string[] versions=info.VersionsByFile.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();info.Version=versions.Length==1?versions[0]:null;}
    static Scan GetScan(string root)
    {
        lock(ScanGate){
            Scan cached;if(Scans.TryGetValue(root,out cached)&&DateTime.UtcNow-cached.Utc<TimeSpan.FromSeconds(10))return cached;var result=new Scan{Utc=DateTime.UtcNow};long bytes=0;int count=0;
            foreach(string relative in new[]{"BepInEx/plugins","BepInEx/patchers"}){
                try{
                    string folder=Under(root,relative);if(!Directory.Exists(folder))continue;
                    foreach(string path in Walk(root,folder)){
                        bool disabled=path.EndsWith(".dll.cumm-disabled",StringComparison.OrdinalIgnoreCase);if(!path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&!disabled)continue;
                        if(++count>10000)throw new IOException("Достигнут предел10000 DLL.");var before=new FileInfo(path);if(before.Length>128L*1024*1024){result.Error="пропущены DLL больше128МиБ";continue;}bytes+=before.Length;if(bytes>512L*1024*1024)throw new IOException("Достигнут предел чтения512МиБ.");
                        try{
                            string hash;using(var sha=SHA256.Create())using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))hash=BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();
                            var after=new FileInfo(path);if(after.Length!=before.Length||after.LastWriteTimeUtc!=before.LastWriteTimeUtc){result.Error="DLL изменялись во время чтения";continue;}
                            string version=null;try{version=AssemblyName.GetAssemblyName(path).Version.ToString();}catch{}
                            string name=Path.GetFileName(path);if(disabled)name=name.Substring(0,name.Length-".cumm-disabled".Length);
                            result.Files.Add(new ScanFile{Relative=path.Substring(root.Length+1).Replace('\\','/'),Name=name,Hash=hash,Version=version});
                        }catch(Exception e){result.Error="не все DLL доступны: "+e.Message;}
                    }
                }catch(Exception e){result.Error=e.Message;}
            }
            Scans[root]=result;return result;
        }
    }
    static IEnumerable<string> Walk(string root,string folder,int depth=0){if(depth>48)throw new IOException("Слишком глубокая вложенность каталогов.");Under(root,folder.Substring(root.Length+1)+"/check");foreach(string f in Directory.GetFiles(folder)){if((File.GetAttributes(f)&FileAttributes.ReparsePoint)!=0)continue;Under(root,f.Substring(root.Length+1));yield return f;}foreach(string child in Directory.GetDirectories(folder)){if((File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)continue;foreach(string f in Walk(root,child,depth+1))yield return f;}}
    static string GameRoot(string root){if(String.IsNullOrWhiteSpace(root))throw new ArgumentException("Не задана папка игры.");root=Path.GetFullPath(root).TrimEnd('\\','/');if(!Directory.Exists(root))throw new DirectoryNotFoundException(root);SafeDirectory(root,false);return root;}
    static string Under(string root,string relative){if(String.IsNullOrWhiteSpace(relative)||relative.IndexOf(':')>=0||relative.StartsWith("/")||relative.StartsWith("\\")||relative.Replace('\\','/').Split('/').Any(p=>p=="."||p==".."))throw new InvalidDataException("Недопустимый путь.");string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Путь выходит за папку игры.");SafeDirectory(Path.GetDirectoryName(full),false);if((File.Exists(full)||Directory.Exists(full))&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new IOException("Ссылка или junction пропущены.");return full;}
    public static UpdateState CompareInstalled(InstalledInfo info,Entry entry)
    {
        if(info==null||entry==null)return UpdateState.Unknown;if(!info.Installed)return info.Ambiguous?UpdateState.Unknown:UpdateState.NotInstalled;if(info.Ambiguous||info.Partial)return UpdateState.Unknown;
        bool older=false,newer=false,compared=false,unknown=false;
        foreach(var actual in info.VersionsByFile){
            string name=DllLeaf(actual.Key);string available;
            if(!entry.DllVersions.TryGetValue(name,out available))continue;VersionComparison c=CompareVersions(actual.Value,available);if(c==VersionComparison.DifferentChannel)return UpdateState.DifferentChannel;if(c==VersionComparison.Unknown){unknown=true;continue;}compared=true;older|=c==VersionComparison.Older;newer|=c==VersionComparison.Newer;
        }
        foreach(string expected in entry.DllVersions.Keys)if(!info.VersionsByFile.Keys.Any(path=>String.Equals(DllLeaf(path),expected,StringComparison.OrdinalIgnoreCase)))unknown=true;
        if(!compared||unknown)return UpdateState.Unknown;if(older&&newer)return UpdateState.DifferentChannel;return older?UpdateState.UpdateAvailable:newer?UpdateState.InstalledNewer:UpdateState.Current;
    }
    static string DllLeaf(string path){string name=Path.GetFileName(path);return name.EndsWith(".cumm-disabled",StringComparison.OrdinalIgnoreCase)?name.Substring(0,name.Length-".cumm-disabled".Length):name;}
    sealed class Semantic{public long[] Numbers;public string Pre;}
    public static VersionComparison CompareVersions(string installed,string available)
    {
        Semantic a=Semver(installed),b=Semver(available);if(a==null||b==null)return VersionComparison.Unknown;if(Channel(a.Pre)!=Channel(b.Pre))return VersionComparison.DifferentChannel;
        for(int i=0;i<4;i++){int c=a.Numbers[i].CompareTo(b.Numbers[i]);if(c!=0)return c<0?VersionComparison.Older:VersionComparison.Newer;}
        if(a.Pre==b.Pre)return VersionComparison.Equal;string[]ap=a.Pre.Split('.'),bp=b.Pre.Split('.');
        for(int i=0;i<Math.Min(ap.Length,bp.Length);i++){long ai,bi;bool an=Int64.TryParse(ap[i],out ai),bn=Int64.TryParse(bp[i],out bi);int c=an&&bn?ai.CompareTo(bi):an!=bn?(an?-1:1):String.CompareOrdinal(ap[i],bp[i]);if(c!=0)return c<0?VersionComparison.Older:VersionComparison.Newer;}
        return ap.Length==bp.Length?VersionComparison.Equal:ap.Length<bp.Length?VersionComparison.Older:VersionComparison.Newer;
    }
    static string Channel(string pre){return String.IsNullOrEmpty(pre)?"stable":Regex.Match(pre,@"^[A-Za-z]+|^[0-9]+").Value.ToLowerInvariant();}
    static Semantic Semver(string text){if(String.IsNullOrWhiteSpace(text)||text.Length>128)return null;Match m=Regex.Match(text.Trim(),@"^[vV]?([0-9]+(?:\.[0-9]+){0,3})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$");if(!m.Success)return null;var numbers=new long[4];string[]parts=m.Groups[1].Value.Split('.');for(int i=0;i<parts.Length;i++)if(!Int64.TryParse(parts[i],out numbers[i]))return null;return new Semantic{Numbers=numbers,Pre=m.Groups[2].Value};}
    static string Fetch(string endpoint,CacheMeta meta,out string etag,out string modified,out bool unchanged)
    {
        ServicePointManager.SecurityProtocol=(SecurityProtocolType)3072;var request=(HttpWebRequest)WebRequest.Create(endpoint);request.Timeout=20000;request.ReadWriteTimeout=20000;request.AllowAutoRedirect=false;request.AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate;request.UserAgent="CasualtiesModManager-Catalog/0.5.0";
        if(meta!=null){if(!String.IsNullOrEmpty(meta.ETag)&&meta.ETag.Length<=512&&meta.ETag.IndexOfAny(new[]{'\r','\n'})<0)request.Headers[HttpRequestHeader.IfNoneMatch]=meta.ETag;DateTime date;if(DateTime.TryParse(meta.LastModified,CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal|DateTimeStyles.AdjustToUniversal,out date))request.IfModifiedSince=date.ToUniversalTime();}
        HttpWebResponse response;try{response=(HttpWebResponse)request.GetResponse();}catch(WebException e){response=e.Response as HttpWebResponse;if(response==null||response.StatusCode!=HttpStatusCode.NotModified){if(response!=null)response.Close();throw;}}
        using(response){etag=response.Headers["ETag"];modified=response.Headers["Last-Modified"];unchanged=response.StatusCode==HttpStatusCode.NotModified;if(unchanged)return null;if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("HTTP "+(int)response.StatusCode);if(response.ContentLength>Limit)throw new InvalidDataException("Каталог превышает8МиБ.");using(Stream stream=response.GetResponseStream())return ReadStream(stream,Limit);}
    }
    static string ReadStream(Stream stream,int limit){using(var memory=new MemoryStream()){byte[]buffer=new byte[32768];int count;while((count=stream.Read(buffer,0,buffer.Length))>0){if(memory.Length+count>limit)throw new InvalidDataException("Превышен предел размера JSON.");memory.Write(buffer,0,count);}return new UTF8Encoding(false,true).GetString(memory.ToArray());}}
    static string ReadBounded(string file,int limit=Limit){if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("Файл кеша является ссылкой.");using(var input=File.OpenRead(file)){if(input.Length>limit)throw new InvalidDataException("Файл слишком велик.");return ReadStream(input,limit);}}
    static void SafeDirectory(string path,bool create){for(var d=new DirectoryInfo(path);d!=null;d=d.Parent)if(d.Exists&&(d.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Путь содержит ссылку или junction.");if(create)Directory.CreateDirectory(path);}
    static void AtomicWrite(string path,string text){if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Файл кеша является ссылкой.");string temp=path+".tmp-"+Guid.NewGuid().ToString("N");try{using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new StreamWriter(file,new UTF8Encoding(false)))writer.Write(text);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}}
}
