using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

// Plugin-only imports. Never loads downloaded assemblies or touches user saves/configuration.
public static class CommunityInstaller
{
    const string State = "BepInEx/config/CasualtiesModManager/";
    const string Database = State + "community/";
    const long MaxFile = 128L * 1024 * 1024, MaxTotal = 512L * 1024 * 1024;
    static JavaScriptSerializer Json { get { return new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 }; } }
    public sealed class OwnedFile { public string Path; public string Hash; }
    public sealed class InstalledRecord
    {
        public string Id, Name, Version;
        public bool Disabled;
        public List<OwnedFile> Files = new List<OwnedFile>();
    }
    public sealed class Plan
    {
        public Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public string Summary, ArchiveHash, ModuleId;
        public List<string> Warnings = new List<string>();
        internal string Seal;
    }
    public sealed class Result { public string BackupPath; public int FileCount; }
#if COMMUNITY_TESTS
    internal static Action<string> BeforeWrite;
    internal static bool SkipProcessGuard;
#endif
    static Exception Fail(string message) { return new InvalidOperationException(message); }
    static void Id(string id) { if (id == null || !Regex.IsMatch(id, "^[a-zA-Z0-9_-]{1,80}$")) throw Fail("Недопустимый идентификатор мода."); }
    static string Hash(byte[] bytes) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    static string FileHash(string path) { using (var s = File.OpenRead(path)) using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
    static string Clean(string p)
    {
        if (String.IsNullOrWhiteSpace(p)) throw Fail("Пустой путь.");
        p = p.Replace('\\', '/');
        if (p.StartsWith("/") || p.IndexOf(':') >= 0 || p.IndexOf('\0') >= 0) throw Fail("Недопустимый путь: " + p);
        string[] pieces = p.Split('/');
        foreach (string part in pieces)
        {
            if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ") || part.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 || part.Any(c => c < 32)) throw Fail("Недопустимый путь: " + p);
            string stem = part.Split('.')[0];
            if (Regex.IsMatch(stem, "^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase)) throw Fail("Зарезервированное имя: " + p);
        }
        return p;
    }
    static string Under(string root, string relative)
    {
        relative = Clean(relative);
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw Fail("Путь выходит за папку игры.");
        for (var d = new DirectoryInfo(Path.GetDirectoryName(full)); d != null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw Fail("Путь содержит ссылку: " + d.FullName);
        if ((File.Exists(full) || Directory.Exists(full)) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw Fail("Файл является ссылкой: " + relative);
        return full;
    }
    static string Root(string gameRoot, bool mutate)
    {
        string root = Path.GetFullPath(gameRoot).TrimEnd('\\', '/');
        if (!File.Exists(Under(root, "CasualtiesUnknown.exe")) || !Directory.Exists(Under(root, "CasualtiesUnknown_Data"))) throw Fail("Выберите папку установленной Casualties: Unknown.");
        if (mutate) GuardProcesses(root);
        return root;
    }
    static void GuardProcesses(string root)
    {
#if COMMUNITY_TESTS
        if (SkipProcessGuard) return;
#endif
        string target = Path.Combine(root, "CasualtiesUnknown.exe");
        foreach (Process p in Process.GetProcesses())
        {
            using (p)
            {
                string processName = null;
                try { processName = p.ProcessName; } catch { }
                if (String.Equals(processName, "CasualtiesUnknown", StringComparison.OrdinalIgnoreCase)) throw Fail("Сначала закройте игру.");
                string path = null;
                try { path = p.MainModule.FileName; } catch { }
                if (path != null && String.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase)) throw Fail("Сначала закройте игру.");
            }
        }
    }
    static FileStream Lock(string root)
    {
        string p = Under(root, State + "operation.lock"); Directory.CreateDirectory(Path.GetDirectoryName(p));
        try { return new FileStream(p, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw Fail("Другой экземпляр менеджера выполняет операцию."); }
    }
    static bool IsDll(string p) { return p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase); }
    static void PluginPath(string path)
    {
        string canonical = Clean(path);
        if (path != canonical) throw Fail("Используйте нормализованный путь с разделителями /.");
        if (!path.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) || path.Length <= 16) throw Fail("Разрешены только файлы внутри BepInEx/plugins: " + path);
        string leaf = Path.GetFileName(path);
        if (path.EndsWith(".cumm-disabled", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(leaf, "\\.(exe|com|bat|cmd|ps1|vbs|js|msi|lnk|url|sys)$", RegexOptions.IgnoreCase)) throw Fail("Недопустимый исполняемый файл: " + path);
        string[] reserved = { "CuRussian", "CuWiki", "Together", "RuFieldGuide" };
        string tail = path.Substring("BepInEx/plugins/".Length);
        foreach (string r in reserved) if (tail.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) || tail.Equals(r + ".dll", StringComparison.OrdinalIgnoreCase)) throw Fail("Для этого встроенного модуля используйте отдельную карточку: " + r);
        if (leaf.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase) || leaf.Equals("BepInEx.dll", StringComparison.OrdinalIgnoreCase) || leaf.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase)) throw Fail("Загрузчик не является обычным плагином.");
    }
    static string Seal(Plan plan)
    {
        var text = new StringBuilder(plan.ModuleId + "\n" + plan.ArchiveHash + "\n");
        foreach (var p in plan.Files.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)) text.Append(p.Key).Append(':').Append(Hash(p.Value)).Append('\n');
        return Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }
    public static Plan Preview(string gameRoot, string moduleId, string zipPath, string expectedArchiveSha256, IEnumerable<string> expectedDllNames)
    {
        string root = Root(gameRoot, false); Id(moduleId);
        var names = new HashSet<string>(expectedDllNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0 || names.Any(n => !IsDll(n) || n != Path.GetFileName(n) || n.IndexOf('/') >= 0 || n.IndexOf('\\') >= 0)) throw Fail("В каталоге не указано допустимое имя основной DLL.");
        // Hold a deny-write handle through hashing and parsing so the verified archive cannot change between them.
        using (var sourceLock = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
        if (new FileInfo(zipPath).Length > MaxTotal) throw Fail("Архив слишком велик.");
        var plan = new Plan { ModuleId = moduleId, ArchiveHash = FileHash(zipPath) };
        if (!String.IsNullOrWhiteSpace(expectedArchiveSha256))
        {
            string h = expectedArchiveSha256.Replace("sha256:", "").Trim();
            if (!Regex.IsMatch(h, "^[a-fA-F0-9]{64}$") || !String.Equals(h, plan.ArchiveHash, StringComparison.OrdinalIgnoreCase)) throw Fail("SHA-256 не совпадает. Выберите файл именно этой записи каталога.");
        }
        else plan.Warnings.Add("Каталог не содержит SHA-256: подлинность загружаемого файла не подтверждена.");
        if (IsDll(zipPath))
        {
            string leaf = Path.GetFileName(zipPath); if (!names.Contains(leaf)) throw Fail("Имя DLL не соответствует выбранному моду.");
            if (new FileInfo(zipPath).Length > MaxFile) throw Fail("DLL слишком велика.");
            plan.Files.Add("BepInEx/plugins/Community/" + moduleId + "/" + leaf, File.ReadAllBytes(zipPath));
            plan.Warnings.Add("Импортирована отдельная DLL. Убедитесь, что этому моду не нужны соседние ресурсы.");
        }
        else
        {
            using (var stream = File.OpenRead(zipPath)) using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > 10000) throw Fail("В архиве слишком много файлов.");
                var raw = new List<KeyValuePair<string, ZipArchiveEntry>>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
                foreach (var entry in zip.Entries)
                {
                    string p = Clean(entry.FullName.TrimEnd('/', '\\'));
                    if (!seen.Add(p)) throw Fail("Повтор пути в архиве: " + p);
                    int mode = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (mode == 0xA000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0) throw Fail("Ссылки в архиве запрещены.");
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) continue;
                    if (entry.Length > MaxFile || entry.Length < 0 || (total += entry.Length) > MaxTotal || (entry.Length > 1024 * 1024 && entry.Length > Math.Max(1, entry.CompressedLength) * 1000)) throw Fail("Архив превышает допустимые размеры или степень сжатия.");
                    raw.Add(new KeyValuePair<string, ZipArchiveEntry>(p, entry));
                }
                var prefixes = raw.Where(e => Regex.IsMatch(e.Key, "^(?:[^/]+/)?BepInEx/", RegexOptions.IgnoreCase)).Select(e => e.Key.Substring(0, e.Key.IndexOf("BepInEx/", StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (prefixes.Count > 1) throw Fail("В архиве несколько корневых папок BepInEx.");
                // A package may start at plugins/ rather than at the game root. Treat it as an
                // explicit install root, not a mod folder named plugins (including one wrapper).
                var pluginPrefixes = raw.Where(e => !Regex.IsMatch(e.Key, "^(?:[^/]+/)?BepInEx/", RegexOptions.IgnoreCase)).Select(e => Regex.Match(e.Key, "^(?:[^/]+/)?plugins/", RegexOptions.IgnoreCase)).Where(m => m.Success).Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (pluginPrefixes.Count > 1 || (prefixes.Count > 0 && pluginPrefixes.Count > 0)) throw Fail("В архиве несколько несовместимых корневых папок plugins.");
                bool bareRootDll = raw.Any(e => e.Key.IndexOf('/') < 0 && names.Contains(e.Key));
                if (prefixes.Count == 0 && pluginPrefixes.Count == 0 && !bareRootDll && raw.Where(e => !IsDocument(e.Key) || e.Key.IndexOf('/') >= 0).Select(e => e.Key.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) throw Fail("Выберите ZIP с одной папкой мода или основной DLL в корне.");
                foreach (var item in raw)
                {
                    string p = item.Key;
                    if (prefixes.Count == 1)
                    {
                        string prefix = prefixes[0];
                        if (p.StartsWith(prefix + "BepInEx/", StringComparison.OrdinalIgnoreCase)) p = p.Substring(prefix.Length);
                        else { if (IsDocument(p)) continue; throw Fail("Файл за пределами plugins: " + p); }
                    }
                    else if (pluginPrefixes.Count == 1)
                    {
                        string prefix = pluginPrefixes[0];
                        if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) p = "BepInEx/plugins/" + p.Substring(prefix.Length);
                        else { if (IsDocument(p)) continue; throw Fail("Файл за пределами plugins: " + p); }
                    }
                    else
                    {
                        if (Regex.IsMatch(p, "(^|/)(BepInEx|patchers|config|core)(/|$)", RegexOptions.IgnoreCase)) throw Fail("Архив содержит файлы загрузчика, конфигурации или patchers.");
                        // A single mod folder keeps its original name and all neighbouring assets.
                        if (!bareRootDll && p.IndexOf('/') < 0 && IsDocument(p)) continue;
                        p = bareRootDll ? "BepInEx/plugins/Community/" + moduleId + "/" + p : "BepInEx/plugins/" + p;
                    }
                    PluginPath(p); Under(root, p);
                    if (plan.Files.ContainsKey(p)) throw Fail("Повтор конечного пути: " + p);
                    using (var input = item.Value.Open()) using (var mem = new MemoryStream())
                    {
                        byte[] buffer = new byte[81920]; int count; long length = 0;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) { length += count; if (length > MaxFile || length > item.Value.Length) throw Fail("Недопустимая длина распакованного файла."); mem.Write(buffer, 0, count); }
                        if (length != item.Value.Length) throw Fail("Повреждённый ZIP.");
                        plan.Files.Add(p, mem.ToArray());
                    }
                }
            }
        }
        if (!plan.Files.Any(p => names.Contains(Path.GetFileName(p.Key)))) throw Fail("В архиве не найдена основная DLL выбранного мода: " + String.Join(", ", names));
        if (plan.Files.Count == 0) throw Fail("Архив пуст.");
        plan.Warnings.Add("Код стороннего мода запустится при следующем запуске игры. Совместимость зависит от автора мода.");
        plan.Summary = "Файлов: " + plan.Files.Count + "; SHA-256: " + plan.ArchiveHash;
        plan.Seal = Seal(plan); return plan;
        }
    }
    static bool IsDocument(string p) { string ext = Path.GetExtension(p); return new[] { ".txt", ".md", ".pdf", ".png", ".jpg", ".jpeg" }.Contains(ext, StringComparer.OrdinalIgnoreCase); }
    static string Actual(InstalledRecord r, OwnedFile f) { return f.Path + (r.Disabled && IsDll(f.Path) ? ".cumm-disabled" : ""); }
    static void ValidateRecord(InstalledRecord r, string id)
    {
        if (r == null || r.Id != id || r.Files == null || r.Files.Count == 0 || r.Files.Count > 10000) throw Fail("Повреждён манифест мода: " + id);
        Id(r.Id); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in r.Files) { if (f == null) throw Fail("Повреждён список файлов."); PluginPath(f.Path); if (!seen.Add(f.Path) || f.Hash == null || !Regex.IsMatch(f.Hash, "^[a-fA-F0-9]{64}$")) throw Fail("Повреждён манифест: " + id); }
    }
    static InstalledRecord Load(string root, string id)
    {
        Id(id); string path = Under(root, Database + id + ".json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw Fail("Манифест слишком велик.");
        var r = Json.Deserialize<InstalledRecord>(File.ReadAllText(path)); ValidateRecord(r, id); return r;
    }
    public static List<InstalledRecord> ListInstalled(string gameRoot)
    {
        string root = Root(gameRoot, false); string dir = Under(root, Database.TrimEnd('/'));
        var result = new List<InstalledRecord>(); if (!Directory.Exists(dir)) return result;
        foreach (string file in Directory.GetFiles(dir, "*.json")) result.Add(Load(root, Path.GetFileNameWithoutExtension(file)));
        return result;
    }
    // Read-only status for the UI: absence never silently deletes ownership metadata.
    public static int CountPresent(string gameRoot, InstalledRecord record)
    {
        string root = Root(gameRoot, false);
        if (record == null) throw Fail("Нет записи установленного мода.");
        ValidateRecord(record, record.Id);
        return record.Files.Count(f => File.Exists(Under(root, Actual(record, f))));
    }
    static void CheckOwned(string root, InstalledRecord r, bool allowMissing = false)
    {
        if (r == null) return;
        foreach (var f in r.Files)
        {
            string path = Under(root, Actual(r, f));
            if (!File.Exists(path) && !Directory.Exists(path) && allowMissing) continue;
            if (!File.Exists(path) || !String.Equals(FileHash(path), f.Hash, StringComparison.OrdinalIgnoreCase)) throw Fail("Управляемый файл отсутствует или изменён: " + Actual(r, f) + ". Сохраните свои изменения отдельно.");
        }
    }
    static HashSet<string> OtherOwned(string root, string except)
    {
        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ListInstalled(root).Where(r => !String.Equals(r.Id, except, StringComparison.OrdinalIgnoreCase))) foreach (var f in r.Files) { owned.Add(f.Path); owned.Add(f.Path + ".cumm-disabled"); }
        string dir = Under(root, State.TrimEnd('/'));
        if (Directory.Exists(dir)) foreach (string file in Directory.GetFiles(dir, "*.json"))
        {
            Under(root, State + Path.GetFileName(file));
            if (new FileInfo(file).Length > 8 * 1024 * 1024) throw Fail("Манифест слишком велик.");
            var manifest = Json.DeserializeObject(File.ReadAllText(file)) as Dictionary<string, object>;
            object items;
            if (manifest == null || !manifest.TryGetValue("Files", out items)) throw Fail("Не удалось проверить владельца файлов: " + Path.GetFileName(file));
            var array = items as object[]; if (array == null) throw Fail("Некорректный список файлов манифеста.");
            foreach (object item in array) { var row = item as Dictionary<string, object>; object value; if (row == null || !row.TryGetValue("Path", out value)) throw Fail("Некорректный манифест владельца."); string path = Clean(Convert.ToString(value)); Under(root, path); owned.Add(path); owned.Add(path + ".cumm-disabled"); }
        }
        return owned;
    }
    static void EnsureFree(string root, string path, HashSet<string> previous, HashSet<string> other)
    {
        if (other.Contains(path)) throw Fail("Файл принадлежит другому модулю: " + path);
        string full = Under(root, path);
        if ((File.Exists(full) || Directory.Exists(full)) && !previous.Contains(path)) throw Fail("Найден существующий неуправляемый файл: " + path + ". Он не будет перезаписан.");
        string alternate = IsDll(path) ? path + ".cumm-disabled" : path.EndsWith(".dll.cumm-disabled", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - ".cumm-disabled".Length) : null;
        if (alternate != null && (other.Contains(alternate) || (File.Exists(Under(root, alternate)) && !previous.Contains(alternate)))) throw Fail("Конфликт включённой/отключённой DLL: " + alternate);
    }
    public static Result Install(string gameRoot, Plan plan, string displayName, string version)
    {
        string root = Root(gameRoot, true);
        if (plan == null) throw Fail("Нет плана установки."); Id(plan.ModuleId);
        if (plan.Seal == null || plan.Seal != Seal(plan)) throw Fail("План установки изменён. Повторите предварительный просмотр.");
        using (Lock(root))
        {
            GuardProcesses(root);
            if (!File.Exists(Under(root, "BepInEx/core/BepInEx.dll"))) throw Fail("Сначала установите BepInEx отдельным модулем.");
            var old = Load(root, plan.ModuleId); CheckOwned(root, old, true);
            var previous = new HashSet<string>(old == null ? new string[0] : old.Files.Select(f => Actual(old, f)), StringComparer.OrdinalIgnoreCase);
            var other = OtherOwned(root, plan.ModuleId);
            if (old != null && old.Files.Any(f => other.Contains(f.Path) || other.Contains(Actual(old, f)))) throw Fail("Конфликт владельцев ранее установленных файлов.");
            var next = new InstalledRecord { Id = plan.ModuleId, Name = displayName, Version = version, Disabled = old != null && old.Disabled };
            var writes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in plan.Files) { PluginPath(p.Key); next.Files.Add(new OwnedFile { Path = p.Key, Hash = Hash(p.Value) }); }
            foreach (var f in next.Files) { string dest = Actual(next, f); EnsureFree(root, dest, previous, other); writes.Add(dest, plan.Files[f.Path]); }
            writes.Add(Database + next.Id + ".json", Encoding.UTF8.GetBytes(Json.Serialize(next)));
            return Transaction(root, writes, previous.Where(p => !writes.ContainsKey(p)).ToList(), "community-install-" + next.Id, next.Files.Count);
        }
    }
    public static Result Remove(string gameRoot, string moduleId)
    {
        string root = Root(gameRoot, true); Id(moduleId);
        using (Lock(root))
        {
            GuardProcesses(root); var old = Load(root, moduleId); if (old == null) throw Fail("Мод не управляется менеджером.");
            CheckOwned(root, old, true); var other = OtherOwned(root, moduleId);
            var present = old.Files.Where(f => File.Exists(Under(root, Actual(old, f)))).ToList();
            if (present.Any(f => other.Contains(f.Path) || other.Contains(Actual(old, f)))) throw Fail("Конфликт владельцев файлов.");
            // Retain absent owned paths in the transaction so their now-empty ancestors can also
            // be pruned after explicit removal; no file is recreated or deleted at an absent path.
            var deletes = old.Files.Select(f => Actual(old, f)).ToList(); deletes.Add(Database + moduleId + ".json");
            return Transaction(root, new Dictionary<string, byte[]>(), deletes, "community-remove-" + moduleId, present.Count);
        }
    }
    // Explicit adoption records only individually selected DLLs. It never guesses ownership of assets.
    public static Result Adopt(string gameRoot, string moduleId, string displayName, string version, IEnumerable<string> relativeFiles)
    {
        string root = Root(gameRoot, true); Id(moduleId);
        using (Lock(root))
        {
            GuardProcesses(root);
            if (Load(root, moduleId) != null) throw Fail("Мод уже управляется менеджером.");
            var paths = (relativeFiles ?? new string[0]).ToList();
            if (paths.Count == 0 || paths.Count > 10000 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count) throw Fail("Выберите неповторяющийся список DLL для принятия под управление.");
            var other = OtherOwned(root, moduleId); var r = new InstalledRecord { Id = moduleId, Name = displayName, Version = version };
            var writes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                PluginPath(path); if (!IsDll(path)) throw Fail("Принять под управление можно только явно выбранные DLL.");
                string full = Under(root, path);
                if (!File.Exists(full) || other.Contains(path) || other.Contains(path + ".cumm-disabled") || File.Exists(Under(root, path + ".cumm-disabled"))) throw Fail("DLL отсутствует или принадлежит другому модулю: " + path);
                if (new FileInfo(full).Length > MaxFile) throw Fail("DLL слишком велика."); byte[] data = File.ReadAllBytes(full);
                r.Files.Add(new OwnedFile { Path = path, Hash = Hash(data) });
                // Journals originals in the same transaction as the new ownership record.
                writes.Add(path, data);
            }
            writes.Add(Database + moduleId + ".json", Encoding.UTF8.GetBytes(Json.Serialize(r)));
            return Transaction(root, writes, new List<string>(), "community-adopt-" + moduleId, r.Files.Count);
        }
    }
    public static Result SetEnabled(string gameRoot, string moduleId, bool enabled)
    {
        string root = Root(gameRoot, true); Id(moduleId);
        using (Lock(root))
        {
            GuardProcesses(root); var old = Load(root, moduleId); if (old == null) throw Fail("Мод не управляется менеджером."); CheckOwned(root, old);
            if (old.Disabled == !enabled) return new Result { FileCount = 0 };
            var previous = new HashSet<string>(old.Files.Select(f => Actual(old, f)), StringComparer.OrdinalIgnoreCase); var other = OtherOwned(root, moduleId);
            if (old.Files.Any(f => other.Contains(f.Path) || other.Contains(Actual(old, f)))) throw Fail("Конфликт владельцев файлов.");
            var writes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase); var deletes = new List<string>();
            foreach (var f in old.Files.Where(f => IsDll(f.Path))) { string source = Actual(old, f), dest = f.Path + (enabled ? "" : ".cumm-disabled"); EnsureFree(root, dest, previous, other); writes.Add(dest, File.ReadAllBytes(Under(root, source))); deletes.Add(source); }
            old.Disabled = !enabled; writes.Add(Database + moduleId + ".json", Encoding.UTF8.GetBytes(Json.Serialize(old)));
            return Transaction(root, writes, deletes, (enabled ? "community-enable-" : "community-disable-") + moduleId, old.Files.Count);
        }
    }
    static Result Transaction(string root, Dictionary<string, byte[]> writes, List<string> deletes, string label, int count)
    {
        var paths = writes.Keys.Concat(deletes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var before = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        // Do not duplicate already long plugin paths underneath a long operation directory.
        // Fixed-length transaction IDs and flat blob names keep backup paths short on legacy .NET.
        string backupRel = State + "backups/" + Guid.NewGuid().ToString("N").Substring(0, 16) + "/";
        string journal = Under(root, backupRel + "transaction.json");
        foreach (string path in paths) { string full = Under(root, path); if (Directory.Exists(full)) throw Fail("Путь занят папкой: " + path); if (File.Exists(full) && new FileInfo(full).Length > MaxFile) throw Fail("Существующий файл слишком велик: " + path); before.Add(path, File.Exists(full) ? File.ReadAllBytes(full) : null); }
        Directory.CreateDirectory(Path.GetDirectoryName(journal));
        var backupNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); int blob = 0;
        foreach (var p in before.Where(p => p.Value != null))
        {
            string name = "files/" + (blob++).ToString("D6") + ".bin", copy = Under(root, backupRel + name);
            Directory.CreateDirectory(Path.GetDirectoryName(copy)); File.WriteAllBytes(copy, p.Value); backupNames.Add(p.Key, name);
        }
        var entries = before.Select(p => new { path = p.Key, backup = p.Value == null ? null : backupNames[p.Key], absent = p.Value == null, sha256 = p.Value == null ? null : Hash(p.Value) }).ToArray();
        File.WriteAllText(journal, Json.Serialize(new { status = "prepared", operation = label, paths = paths, entries = entries, absent = before.Where(p => p.Value == null).Select(p => p.Key).ToArray() }));
        try
        {
            foreach (var p in writes)
            {
                string full = Under(root, p.Key); Directory.CreateDirectory(Path.GetDirectoryName(full));
#if COMMUNITY_TESTS
                if (BeforeWrite != null) BeforeWrite(p.Key);
#endif
                File.WriteAllBytes(full, p.Value);
            }
            foreach (string path in deletes.Where(p => !writes.ContainsKey(p))) { string full = Under(root, path); if (File.Exists(full)) File.Delete(full); }
            File.WriteAllText(journal, Json.Serialize(new { status = "committed", operation = label, paths = paths, entries = entries }));
        }
        catch (Exception original)
        {
            var failures = new List<Exception>();
            foreach (var p in before) try { string full = Under(root, p.Key); if (p.Value == null) { if (File.Exists(full)) File.Delete(full); } else { Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllBytes(full, p.Value); } } catch (Exception e) { failures.Add(e); }
            File.WriteAllText(journal, Json.Serialize(new { status = failures.Count == 0 ? "rolled_back" : "rollback_incomplete", operation = label, paths = paths, entries = entries }));
            if (failures.Count > 0) throw Fail("Откат не завершён. Резервная копия: " + Path.GetDirectoryName(journal) + "; исходная ошибка: " + original.Message);
            throw;
        }
        PruneEmptyParents(root, deletes.Where(p => !writes.ContainsKey(p)));
        return new Result { BackupPath = Path.GetDirectoryName(journal), FileCount = count };
    }
    static void PruneEmptyParents(string root, IEnumerable<string> deletedPaths)
    {
        const string stop = "BepInEx/plugins";
        foreach (string deleted in deletedPaths)
        {
            try
            {
                string relative = Clean(deleted);
                if (!relative.StartsWith(stop + "/", StringComparison.OrdinalIgnoreCase)) continue;
                string parent = relative.Substring(0, relative.LastIndexOf('/'));
                while (parent.Length > stop.Length && parent.StartsWith(stop + "/", StringComparison.OrdinalIgnoreCase))
                {
                    string path = Under(root, parent);
                    if (Directory.Exists(path))
                    {
                        if (Directory.EnumerateFileSystemEntries(path).Any()) break;
                        Directory.Delete(path, false); // Never recurse or remove a nonempty/foreign tree.
                    }
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                }
            }
            catch { /* Optional post-commit cleanup must never roll back a successful operation. */ }
        }
    }
}
