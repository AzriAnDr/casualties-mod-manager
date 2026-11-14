using System;
using System.IO;
using System.Net;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Network work runs only when the caller explicitly requests an update check/download.
public static class LauncherUpdate
{
    public sealed class UpdateInfo
    {
        public string Version { get; set; }
        public string DownloadUrl { get; set; }
        public string Sha256 { get; set; }
        public string Notes { get; set; }
    }
    const int MaximumExeBytes = 32 * 1024 * 1024;
    const int MaximumFeedBytes = 256 * 1024;
    static readonly string UpdateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CasualtiesModManager", "updates");

    // JSON schema: version, url, sha256, notes. Only this project's exact release EXE and matching v<version> tag are accepted.
    public static UpdateInfo Check(string feedUrl, string currentVersion)
    {
        ParseVersion(currentVersion);
        Uri feed = ValidateUrl(feedUrl, false, true);
        string source;
        using (var data = new MemoryStream()) { Download(feed, data, MaximumFeedBytes, false); source = new UTF8Encoding(false, true).GetString(data.ToArray()).TrimStart('\ufeff'); }
        return ParseFeed(source, currentVersion);
    }
    static UpdateInfo ParseFeed(string source, string currentVersion)
    {
        System.Version current = ParseVersion(currentVersion);
        var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumFeedBytes, RecursionLimit = 16 };
        System.Collections.Generic.Dictionary<string, object> json;
        try { json = serializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(source); }
        catch (Exception e) { throw new InvalidDataException("Не удалось прочитать JSON обновления.", e); }
        var info = new UpdateInfo { Version = Required(json, "version"), DownloadUrl = Required(json, "url"), Sha256 = Required(json, "sha256"), Notes = Required(json, "notes", true) };
        ValidateInfo(info);
        System.Version available = ParseVersion(info.Version);
        int comparison = available.CompareTo(current);
        if (comparison < 0) throw new InvalidDataException("Сервер предлагает более старую версию " + info.Version + ". Понижение версии запрещено.");
        return comparison == 0 ? null : info;
    }

    public static string DownloadStage(UpdateInfo info)
    {
        ValidateInfo(info);
        Uri url = ValidateUrl(info.DownloadUrl, true, true);
        EnsureSafeDirectory(UpdateRoot);
        string folder = Path.Combine(UpdateRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string staged = Path.Combine(folder, "Casualties-ModManager.update.exe");
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) Download(url, output, MaximumExeBytes, true);
            VerifyExe(staged, info.Sha256);
            File.WriteAllText(staged + ".sha256", info.Sha256.ToLowerInvariant(), Encoding.ASCII);
            return staged;
        }
        catch { TryDelete(staged); TryDelete(staged + ".sha256"); try { Directory.Delete(folder); } catch { } throw; }
    }
    // Kept for callers that use the original contract's spelling.
    public static string downloadStage(UpdateInfo info) { return DownloadStage(info); }

    // Returns only after the helper validates the request and acknowledges readiness.
    // The caller must then close the current application normally. No live EXE is changed here.
    public static void ApplyOnExit(string stagedPath, string liveExePath)
    {
        string staged = FullPath(stagedPath), target = FullPath(liveExePath);
        string self = FullPath(Assembly.GetEntryAssembly().Location);
        if (!String.Equals(target, self, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Можно обновить только запущенный экземпляр менеджера.");
        RequireOwnStagingPath(staged);
        string hashPath = staged + ".sha256";
        if (!File.Exists(hashPath)) throw new InvalidDataException("Не найден SHA-256 подготовленного обновления. Загрузите его заново.");
        string hash = File.ReadAllText(hashPath).Trim();
        VerifyExe(staged, hash);
        string helper = Path.Combine(Path.GetDirectoryName(staged), "UpdateHelper.exe");
        if (File.Exists(helper)) throw new IOException("Помощник уже подготовлен в этой папке. Повторите загрузку обновления.");
        using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("UpdateHelper.exe"))
        {
            if (resource == null) throw new InvalidOperationException("В комплекте отсутствует UpdateHelper.exe. Скачайте новую версию менеджера вручную.");
            using (var output = new FileStream(helper, FileMode.CreateNew, FileAccess.Write, FileShare.None)) resource.CopyTo(output);
        }
        ValidatePe(helper);
        string token = Guid.NewGuid().ToString("N"), ready = Path.Combine(Path.GetDirectoryName(staged), "ready-" + token + ".txt");
        string arguments = "--apply " + Process.GetCurrentProcess().Id.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + Quote(staged) + " " + Quote(target) + " " + hash.ToLowerInvariant() + " " + Quote(ready) + " " + token;
        var start = new ProcessStartInfo(helper, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(helper) };
        using (Process helperProcess = Process.Start(start))
        {
            if (helperProcess == null) throw new IOException("Не удалось запустить помощник обновления.");
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 5000)
            {
                if (File.Exists(ready) && File.ReadAllText(ready).Trim() == token) return;
                if (helperProcess.HasExited) throw new IOException("Помощник обновления завершился до подтверждения готовности. Подробности: " + Path.Combine(Path.GetDirectoryName(staged), "update-helper.log"));
                Thread.Sleep(50);
            }
            try { helperProcess.Kill(); } catch { }
            throw new IOException("Помощник не подтвердил готовность за 5 секунд. Текущий менеджер не изменён.");
        }
    }

    static string Required(System.Collections.Generic.Dictionary<string, object> json, string key, bool allowEmpty = false)
    {
        object value;
        if (json == null || !json.TryGetValue(key, out value) || !(value is string) || (!allowEmpty && String.IsNullOrWhiteSpace((string)value))) throw new InvalidDataException("В JSON обновления отсутствует строковое поле «" + key + "».");
        return (string)value;
    }
    static void ValidateInfo(UpdateInfo info)
    {
        if (info == null) throw new ArgumentNullException("info");
        ParseVersion(info.Version); Uri download = ValidateUrl(info.DownloadUrl, true, true); ValidateHash(info.Sha256);
        if (download.AbsolutePath != TrustedReleasePrefix + "download/v" + info.Version + "/" + TrustedExecutableName) throw new InvalidDataException("Тег EXE не совпадает с версией в JSON обновления.");
        if (info.Notes == null || info.Notes.Length > 65536) throw new InvalidDataException("Некорректное описание обновления.");
    }
    static System.Version ParseVersion(string text)
    {
        System.Version value;
        if (String.IsNullOrEmpty(text) || text.Length > 32 || !System.Version.TryParse(text, out value) || value.Major < 0 || value.Minor < 0) throw new InvalidDataException("Некорректная числовая версия обновления: " + text);
        return new System.Version(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    }
    const string TrustedReleasePrefix = "/AzriAnDr/casualties-mod-manager/releases/";
    const string TrustedFeedPath = TrustedReleasePrefix + "latest/download/update.json";
    const string TrustedExecutableName = "Casualties-ModManager.exe";
    static Uri ValidateUrl(string text, bool executable, bool initial)
    {
        Uri url;
        if (!Uri.TryCreate(text, UriKind.Absolute, out url) || url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Fragment.Length != 0) throw new InvalidDataException("Обновления поддерживают только HTTPS-адреса официального репозитория.");
        string host = url.DnsSafeHost.ToLowerInvariant();
        bool github = host == "github.com", asset = host == "release-assets.githubusercontent.com" || host == "objects.githubusercontent.com";
        if (github)
        {
            if (url.Query.Length != 0) throw new InvalidDataException("Параметры в адресе выпуска обновления запрещены.");
            bool latestFeed = !executable && url.AbsolutePath == TrustedFeedPath;
            string prefix = TrustedReleasePrefix + "download/";
            if (!latestFeed)
            {
                if (!url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Обновление должно находиться в AzriAnDr/casualties-mod-manager.");
                string[] suffix = url.AbsolutePath.Substring(prefix.Length).Split('/');
                string name = executable ? TrustedExecutableName : "update.json";
                if (suffix.Length != 2 || !IsReleaseTag(suffix[0]) || suffix[1] != name) throw new InvalidDataException("Неожиданное имя или тег файла обновления.");
            }
            if (initial && !executable && !latestFeed) throw new InvalidDataException("Разрешён только официальный latest/download/update.json.");
        }
        else if (initial || !asset) throw new InvalidDataException("Неподдерживаемый сервер обновления: " + host);
        string decoded = Uri.UnescapeDataString(url.AbsolutePath);
        if (decoded.IndexOf('\\') >= 0 || decoded.Split('/').AnySegmentIsDot()) throw new InvalidDataException("Некорректный путь URL обновления.");
        return url;
    }
    static bool IsReleaseTag(string tag)
    {
        if (String.IsNullOrEmpty(tag) || tag[0] != 'v') return false;
        string numeric = tag.Substring(1); System.Version parsed;
        if (numeric.Length > 32 || !System.Version.TryParse(numeric, out parsed)) return false;
        foreach (char c in numeric) if ((c < '0' || c > '9') && c != '.') return false;
        return parsed.Major >= 0 && parsed.Minor >= 0;
    }
    static void Download(Uri initial, Stream output, int limit, bool executable)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        Uri current = initial;
        var deadline = Stopwatch.StartNew();
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            ValidateUrl(current.AbsoluteUri, executable, redirects == 0);
            if (executable && current.Host == "github.com" && current.AbsolutePath != initial.AbsolutePath) throw new InvalidDataException("Перенаправление изменяет тег EXE обновления.");
            var request = (HttpWebRequest)WebRequest.Create(current);
            int remaining = 20000 - (int)deadline.ElapsedMilliseconds;
            if (remaining <= 0) throw new WebException("Проверка или загрузка обновления превысила 20 секунд.", WebExceptionStatus.Timeout);
            request.Method = "GET"; request.AllowAutoRedirect = false; request.Timeout = remaining; request.ReadWriteTimeout = remaining; request.UserAgent = "CasualtiesModManager-Updater/0.5.1"; request.KeepAlive = false;
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                int code = (int)response.StatusCode;
                if (code >= 300 && code <= 399)
                {
                    string location = response.Headers["Location"];
                    if (String.IsNullOrEmpty(location) || redirects == 5) throw new InvalidDataException("Слишком много или некорректное перенаправление обновления.");
                    current = new Uri(current, location); continue;
                }
                if (response.StatusCode != HttpStatusCode.OK) throw new IOException("Сервер обновлений вернул HTTP " + code);
                if (response.ContentLength > limit) throw new InvalidDataException("Файл обновления превышает допустимый размер.");
                int total = 0; byte[] buffer = new byte[32768];
                using (Stream input = response.GetResponseStream()) { while (true) { remaining = 20000 - (int)deadline.ElapsedMilliseconds; if (remaining <= 0) throw new WebException("Загрузка превысила 20 секунд.", WebExceptionStatus.Timeout); if (input.CanTimeout) input.ReadTimeout = remaining; int read = input.Read(buffer, 0, buffer.Length); if (read == 0) break; total += read; if (total > limit) throw new InvalidDataException("Загрузка превысила допустимый размер."); output.Write(buffer, 0, read); } }
                if (total == 0) throw new InvalidDataException("Сервер вернул пустой файл обновления.");
                return;
            }
        }
        throw new IOException("Не удалось загрузить обновление.");
    }
    static void ValidateHash(string hash)
    {
        if (hash == null || hash.Length != 64) throw new InvalidDataException("В обновлении отсутствует полный SHA-256.");
        foreach (char c in hash) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) throw new InvalidDataException("Некорректный SHA-256 обновления.");
    }
    static void VerifyExe(string path, string hash)
    {
        ValidateHash(hash);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaximumExeBytes) throw new InvalidDataException("Подготовленный EXE отсутствует или слишком велик.");
        string actual;
        using (var sha = SHA256.Create()) using (Stream file = File.OpenRead(path)) actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
        if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 EXE не совпадает с опубликованным. Файл не будет запущен.");
        ValidatePe(path);
    }
    static void ValidatePe(string path)
    {
        using (var file = File.OpenRead(path)) using (var reader = new BinaryReader(file))
        {
            if (file.Length < 256 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Загруженный файл не является Windows EXE.");
            file.Position = 0x3c; int pe = reader.ReadInt32();
            if (pe < 64 || pe > file.Length - 26) throw new InvalidDataException("Повреждён заголовок EXE.");
            file.Position = pe;
            if (reader.ReadUInt32() != 0x4550) throw new InvalidDataException("Некорректная PE-сигнатура EXE.");
            ushort machine = reader.ReadUInt16(); file.Position = pe + 22; ushort flags = reader.ReadUInt16(), optional = reader.ReadUInt16();
            if ((machine != 0x14c && machine != 0x8664) || (flags & 2) == 0 || (flags & 0x2000) != 0 || (optional != 0x10b && optional != 0x20b)) throw new InvalidDataException("Неподдерживаемый формат EXE обновления.");
        }
        try { AssemblyName.GetAssemblyName(path); } catch (Exception e) { throw new InvalidDataException("Обновление не является .NET-приложением менеджера.", e); }
    }
    static string FullPath(string path) { if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new InvalidDataException("Требуется абсолютный путь EXE."); string full = Path.GetFullPath(path); if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0) throw new InvalidDataException("Обновление по сетевому пути не поддерживается."); return full; }
    static void EnsureSafeDirectory(string path) { for (var d = new DirectoryInfo(path); d != null; d = d.Parent) if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка обновления содержит ссылку или junction."); Directory.CreateDirectory(path); }
    static void RequireOwnStagingPath(string path) { string prefix = Path.GetFullPath(UpdateRoot).TrimEnd('\\') + "\\"; Guid folder; if (!Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(path)), "N", out folder) || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != "Casualties-ModManager.update.exe") throw new InvalidDataException("EXE находится вне подготовленной папки обновления."); EnsureSafeDirectory(Path.GetDirectoryName(path)); if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Подготовленный EXE является ссылкой."); }
    static string Quote(string value) { if (value.IndexOf('"') >= 0 || value.EndsWith("\\", StringComparison.Ordinal)) throw new InvalidDataException("Некорректный путь помощника обновления."); return "\"" + value + "\""; }
    static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    static bool AnySegmentIsDot(this string[] parts) { foreach (string part in parts) if (part == "." || part == "..") return true; return false; }
}
