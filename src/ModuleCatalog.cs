using System;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Text;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

// Independently updated built-in packages. No launcher-latest lookup or installation here.
public static class ModuleCatalog
{
    public const string FeedUrl = "https://github.com/AzriAnDr/casualties-mod-manager/releases/download/modules-stable/modules.json";
    const int JsonLimit = 256 * 1024;
    const long ZipLimit = 128L * 1024 * 1024;
    public sealed class Package
    {
        public string Id, Version, Url, Sha256;
        public string[] GameVersions;
    }
    internal sealed class Response : IDisposable
    {
        public int Status;
        public string Location;
        public long Length = -1;
        public Stream Body;
        internal WebResponse Original;
        internal Timer AbortTimer;
        public void Dispose() { try { if (Body != null) Body.Dispose(); } finally { try { if (Original != null) Original.Dispose(); } finally { if (AbortTimer != null) AbortTimer.Dispose(); } } }
    }
#if MODULE_TESTS
    internal static Func<Uri, DateTime, Response> TestTransport;
#endif
    static InvalidOperationException Error(string text) { return new InvalidOperationException(text); }
    public static Dictionary<string, Package> Check()
    {
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            using (var response = Fetch(new Uri(FeedUrl), deadline))
            using (var memory = new MemoryStream())
            {
                CopyBounded(response.Body, memory, response.Length, JsonLimit, deadline);
                var result = Parse(new UTF8Encoding(false, true).GetString(memory.ToArray())); Remaining(deadline); return result;
            }
        }
        catch (WebException) { throw Error("Не удалось получить каталог модулей по HTTPS. Повторите позже."); }
        catch (IOException) { throw Error("Не удалось полностью получить каталог модулей. Повторите позже."); }
        catch (ObjectDisposedException) { throw Error("Соединение с каталогом модулей прервано или истекло время ожидания."); }
        catch (DecoderFallbackException) { throw Error("Каталог модулей содержит некорректный текст UTF-8."); }
    }
    internal static Dictionary<string, Package> Parse(string source)
    {
        if (source == null || Encoding.UTF8.GetByteCount(source) > JsonLimit) throw Error("Каталог модулей превышает 256 КиБ.");
        object raw;
        try { raw = new JavaScriptSerializer { MaxJsonLength = JsonLimit, RecursionLimit = 32 }.DeserializeObject(source.TrimStart('\ufeff')); }
        catch { throw Error("Каталог модулей содержит некорректный JSON."); }
        var root = raw as Dictionary<string, object>; object schema, modules;
        if (root == null || !root.TryGetValue("schema", out schema) || !(schema is int) || (int)schema != 1 || !root.TryGetValue("modules", out modules) || !(modules is object[])) throw Error("Неподдерживаемая схема каталога модулей.");
        var rows = (object[])modules;
        if (rows.Length != 3) throw Error("Каталог должен содержать ровно три модуля: russian, wiki и bepinex.");
        var result = new Dictionary<string, Package>(StringComparer.Ordinal);
        foreach (object row in rows)
        {
            var data = row as Dictionary<string, object>; if (data == null) throw Error("Некорректная запись каталога модулей.");
            object games;
            if (!data.TryGetValue("gameVersions", out games) || !(games is object[]) || ((object[])games).Any(g => !(g is string))) throw Error("Не указан корректный список версий игры для модуля.");
            var package = new Package { Id = Text(data, "id", 16), Version = Text(data, "version", 40), Url = Text(data, "url", 2048), Sha256 = Text(data, "sha256", 64), GameVersions = ((object[])games).Cast<string>().ToArray() };
            Validate(package);
            if (result.ContainsKey(package.Id)) throw Error("Модуль повторяется в каталоге: " + package.Id);
            package.Sha256 = package.Sha256.ToLowerInvariant(); result.Add(package.Id, package);
        }
        if (!new[] { "russian", "wiki", "bepinex" }.All(result.ContainsKey)) throw Error("Каталог модулей неполон.");
        return result;
    }
    static string Text(Dictionary<string, object> row, string key, int max)
    {
        object value; if (!row.TryGetValue(key, out value) || !(value is string) || String.IsNullOrWhiteSpace((string)value) || ((string)value).Length > max) throw Error("Некорректное поле каталога модулей: " + key);
        return (string)value;
    }
    static bool NumericVersion(string value)
    {
        return value != null && value.Length <= 40 && Regex.IsMatch(value, @"\A(?:0|[1-9][0-9]{0,8})(?:\.(?:0|[1-9][0-9]{0,8})){1,3}\z", RegexOptions.CultureInvariant);
    }
    static void Validate(Package p)
    {
        if (p == null || !new[] { "russian", "wiki", "bepinex" }.Contains(p.Id, StringComparer.Ordinal)) throw Error("Неизвестный модуль в каталоге.");
        if (!NumericVersion(p.Version)) throw Error("Модуль содержит некорректную числовую версию.");
        if (p.Id == "bepinex" && p.Version.Split('.')[0] != "5") throw Error("Для этой игры поддерживаются только пакеты BepInEx 5.");
        if (p.Sha256 == null || p.Sha256.Length != 64 || !Regex.IsMatch(p.Sha256, "^[a-fA-F0-9]{64}$")) throw Error("Для модуля не указан корректный SHA-256.");
        if (p.GameVersions == null || p.GameVersions.Length > 16 || ((p.Id == "russian" || p.Id == "wiki") && p.GameVersions.Length == 0) || p.GameVersions.Any(v => !NumericVersion(v)) || p.GameVersions.Distinct(StringComparer.Ordinal).Count() != p.GameVersions.Length) throw Error("Некорректный список поддерживаемых версий игры.");
        Uri uri; if (p.Url == null || p.Url.Length > 2048 || !Uri.TryCreate(p.Url, UriKind.Absolute, out uri) || !PackageUrl(p.Id, uri)) throw Error("Источник пакета модуля не разрешён. Загрузка остановлена.");
    }
    static bool Secure(Uri uri) { return uri != null && uri.IsAbsoluteUri && uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.HostNameType == UriHostNameType.Dns; }
    internal static bool PackageUrl(string id, Uri uri)
    {
        if (!Secure(uri) || !uri.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Query.Length != 0 || uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal)) return false;
        string[] parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length != 6 || parts[2] != "releases" || parts[3] != "download" || !Regex.IsMatch(parts[4], @"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$") || !Regex.IsMatch(parts[5], @"^[A-Za-z0-9][A-Za-z0-9._-]{0,159}\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        bool own = parts[0].Equals("AzriAnDr", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("casualties-mod-manager", StringComparison.OrdinalIgnoreCase);
        bool officialLoader = id == "bepinex" && parts[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase) && parts[1].Equals("BepInEx", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(parts[4], @"^v5(?:\.[0-9]+){1,3}$");
        return own || officialLoader;
    }
    internal static bool RedirectAllowed(Uri original, Uri candidate)
    {
        if (!Secure(candidate)) return false;
        if (candidate.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return candidate.Query.Length == 0 && String.Equals(original.AbsolutePath, candidate.AbsolutePath, StringComparison.Ordinal) && original.IdnHost.Equals(candidate.IdnHost, StringComparison.OrdinalIgnoreCase);
        return candidate.IdnHost.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || candidate.IdnHost.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }
    public static string Download(Package p, string folder)
    {
        if (p == null) throw Error("Не выбран пакет модуля.");
        // Snapshot mutable caller fields before any network work.
        p = new Package { Id = p.Id, Version = p.Version, Url = p.Url, Sha256 = p.Sha256, GameVersions = p.GameVersions == null ? null : (string[])p.GameVersions.Clone() };
        Validate(p);
        folder = Path.GetFullPath(folder); SafePath(Path.Combine(folder, "download.zip")); Directory.CreateDirectory(folder);
        string final = Path.Combine(folder, p.Id + "-" + p.Version + "-" + Guid.NewGuid().ToString("N") + ".zip"), partial = final + ".partial";
        bool owned = false;
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(120);
            using (var response = Fetch(new Uri(p.Url), deadline))
            {
                SafePath(partial);
                using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var sha = SHA256.Create())
                {
                    owned = true; byte[] signature = CopyBounded(response.Body, output, response.Length, ZipLimit, deadline, sha);
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    string actual = BitConverter.ToString(sha.Hash).Replace("-", "");
                    if (!String.Equals(actual, p.Sha256, StringComparison.OrdinalIgnoreCase)) throw Error("SHA-256 пакета модуля не совпадает с каталогом. Файл не будет установлен.");
                    if (signature.Length < 4 || signature[0] != 0x50 || signature[1] != 0x4b || !((signature[2] == 3 && signature[3] == 4) || (signature[2] == 5 && signature[3] == 6))) throw Error("Полученный пакет не имеет сигнатуры ZIP.");
                    output.Flush(true); Remaining(deadline);
                }
            }
            SafePath(final); SafePath(partial); File.Move(partial, final); return final;
        }
        catch (WebException) { throw Error("Не удалось загрузить пакет модуля по HTTPS. Повторите позже."); }
        catch (IOException) { throw Error("Загрузка пакета модуля или запись файла прервана. Повторите позже."); }
        catch (ObjectDisposedException) { throw Error("Загрузка пакета модуля прервана или истекло время ожидания."); }
        finally
        {
            if (owned) try { SafePath(partial); if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } catch (UnauthorizedAccessException) { } catch (InvalidOperationException) { }
        }
    }
    static void SafePath(string path)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))); dir != null; dir = dir.Parent) if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw Error("Папка загрузки содержит ссылку или junction.");
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Error("Путь загрузки является ссылкой.");
    }
    static int Remaining(DateTime deadline)
    {
        double ms = (deadline - DateTime.UtcNow).TotalMilliseconds; if (ms <= 0) throw Error("Превышено время ожидания загрузки модулей. Повторите позже.");
        return (int)Math.Max(1, Math.Min(20000, ms));
    }
    internal static HttpWebRequest BuildRequest(Uri uri, DateTime deadline)
    {
        if (!Secure(uri)) throw Error("Недопустимый адрес HTTPS пакета.");
        var request = (HttpWebRequest)WebRequest.Create(uri); request.Method = "GET"; request.AllowAutoRedirect = false;
        request.UserAgent = "CasualtiesModManager-Modules/0.5.4"; request.Timeout = request.ReadWriteTimeout = Remaining(deadline);
        request.UseDefaultCredentials = false; request.PreAuthenticate = false; request.CookieContainer = null; request.AutomaticDecompression = DecompressionMethods.None;
        request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
        return request;
    }
    static Response Open(Uri uri, DateTime deadline)
    {
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        var request = BuildRequest(uri, deadline);
#if MODULE_TESTS
        if (TestTransport != null) return TestTransport(uri, deadline);
#endif
        var timer = new Timer(delegate { try { request.Abort(); } catch { } }, null, Math.Max(1, (int)Math.Min(Int32.MaxValue, (deadline - DateTime.UtcNow).TotalMilliseconds)), Timeout.Infinite);
        try
        {
            HttpWebResponse response;
            try { response = (HttpWebResponse)request.GetResponse(); }
            catch (WebException e) { response = e.Response as HttpWebResponse; if (response == null) throw Error("Не удалось связаться с источником модулей по HTTPS."); }
            return new Response { Status = (int)response.StatusCode, Location = response.Headers["Location"], Length = response.ContentLength, Body = response.GetResponseStream(), Original = response, AbortTimer = timer };
        }
        catch { timer.Dispose(); throw; }
    }
    static Response Fetch(Uri original, DateTime deadline)
    {
        Uri current = original;
        for (int redirects = 0; redirects <= 3; redirects++)
        {
            Remaining(deadline); Response response = Open(current, deadline);
            if (response.Status == 200) return response;
            try
            {
                if (response.Status == 301 || response.Status == 302 || response.Status == 303 || response.Status == 307 || response.Status == 308)
                {
                    Uri next;
                    if (redirects == 3 || String.IsNullOrEmpty(response.Location) || !Uri.TryCreate(current, response.Location, out next) || !RedirectAllowed(original, next)) throw Error("Небезопасное или слишком длинное перенаправление загрузки модулей.");
                    current = next; continue;
                }
                if (response.Status == 404) throw Error("Каталог или пакет модулей пока не опубликован либо удалён.");
                if (response.Status == 429) throw Error("Источник модулей ограничил число запросов. Подождите и повторите позже.");
                throw Error("Источник модулей вернул HTTP " + response.Status.ToString(CultureInfo.InvariantCulture) + ". Повторите позже.");
            }
            finally { response.Dispose(); }
        }
        throw Error("Слишком много перенаправлений загрузки модулей.");
    }
    internal static byte[] CopyBounded(Stream input, Stream output, long declared, long limit, DateTime deadline, HashAlgorithm digest = null)
    {
        if (declared < -1 || declared > limit) throw Error("Ответ источника модулей превышает допустимый размер.");
        var signature = new List<byte>(4); byte[] buffer = new byte[32768]; long total = 0;
        while (true)
        {
            Remaining(deadline); int count = input.Read(buffer, 0, buffer.Length); Remaining(deadline); if (count == 0) break;
            total += count; if (total > limit || (declared >= 0 && total > declared)) throw Error("Загрузка модуля превышает допустимый размер.");
            for (int i = 0; i < count && signature.Count < 4; i++) signature.Add(buffer[i]);
            if (digest != null) digest.TransformBlock(buffer, 0, count, buffer, 0); output.Write(buffer, 0, count);
        }
        if (declared >= 0 && declared != total) throw Error("Пакет модуля загружен не полностью.");
        return signature.ToArray();
    }
}
