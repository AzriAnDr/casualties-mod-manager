using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.Cryptography;

// Read-only ZIP discovery. Relevance signals never authorize an installation.
public static class ArchiveInbox
{
    const long FileLimit = 128L * 1024 * 1024, ScanBudget = 512L * 1024 * 1024;
    const string LoaderSha256 = "82f9878551030f54657792c0740d9d51a09500eeae1fba21106b0c441e6732c4";
    public static bool HasPendingFiles { get; private set; }
    public sealed class Item
    {
        public string Path, Hash, State, Kind, DisplayName;
        public long Length;
        public DateTime Written;
        public ModCatalog.Entry Mod;
        public bool PotentiallyRelevant, Ready;
    }
    sealed class BudgetException : IOException { }
    public static List<Item> Scan(string folder, List<ModCatalog.Entry> entries, IDictionary<string, Item> cache, bool includeUnknown = false)
    {
        var result = new List<Item>(); HasPendingFiles = false; long budget = ScanBudget;
        if (!Directory.Exists(folder)) return result;
        for (var dir = new DirectoryInfo(Path.GetFullPath(folder)); dir != null; dir = dir.Parent) if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) return result;
        entries = entries ?? new List<ModCatalog.Entry>(); var names = KnownNames(entries);
        foreach (var file in new DirectoryInfo(folder).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Take(200))
        {
            try
            {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var item = new Item { Path = file.FullName, Length = file.Length, Written = file.LastWriteTimeUtc, Kind = "unknown", DisplayName = "Не определён", State = "ZIP готов; источник не определён" };
            bool hint = FilenameHint(file.Name, entries);
            if (file.Length > FileLimit) { item.State = "Больше 128 МиБ — выберите вручную"; Add(result, item, includeUnknown || hint); if (result.Count == 20) break; continue; }
            if (DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromSeconds(3))
            { Pending(item); Add(result, item, includeUnknown || hint); if (result.Count == 20) break; continue; }
            try
            {
                using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    if (stream.Length != file.Length) { Pending(item); Add(result, item, includeUnknown || hint); if (result.Count == 20) break; continue; }
                    List<string> paths = CentralPaths(stream, ref budget);
                    // Recompute under this exclusive handle: length/timestamp-only cache reuse cannot
                    // prove that a replaced archive still has the catalog's cryptographic identity.
                    if (stream.Length <= budget)
                    { budget -= stream.Length; stream.Position = 0; using (var sha = SHA256.Create()) item.Hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
                    Classify(item, paths, entries, names); file.Refresh();
                    if (file.Length != item.Length || file.LastWriteTimeUtc != item.Written || (file.Attributes & FileAttributes.ReparsePoint) != 0) { item.Hash = null; item.Mod = null; Pending(item); }
                    else { item.Ready = true; if (item.Hash == null) item.State += " · SHA-256 не проверен: предел чтения"; if (cache != null) cache[item.Path] = item; }
                }
            }
            catch (InvalidDataException) { item.Hash = null; item.Mod = null; item.Kind = "invalid"; item.State = "Некорректный или неподдерживаемый ZIP — выберите вручную"; item.Ready = false; }
            catch (BudgetException) { item.Kind = "unknown"; item.State = "Предел чтения — выберите архив вручную"; item.Ready = false; }
            catch (IOException) { Pending(item); }
            catch (UnauthorizedAccessException) { item.Kind = "unknown"; item.State = "Нет доступа к архиву"; item.Ready = false; }
            Add(result, item, includeUnknown || item.PotentiallyRelevant || (hint && !item.Ready)); if (result.Count == 20) break;
            }
            catch (IOException) { /* Download renamed/deleted between enumeration and inspection. */ }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }
    static void Add(List<Item> result, Item item, bool include) { if (include) result.Add(item); }
    static void Pending(Item item) { HasPendingFiles = true; item.Kind = "pending"; item.State = "Загрузка ещё не завершена"; item.Ready = false; item.Mod = null; item.PotentiallyRelevant = false; }
    static bool IsHash(string hash) { return hash != null && hash.Length == 64 && hash.All(c => (c >= '0' && c <= '9') || (Char.ToLowerInvariant(c) >= 'a' && Char.ToLowerInvariant(c) <= 'f')); }
    static bool FilenameHint(string name, List<ModCatalog.Entry> entries)
    {
        if (new[] { "Casualties-", "Casualties_", "CuRussian", "CuWiki", "MPMod_", "BepInEx_win_x64_" }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return true;
        // A filename is only a pending hint, never identification of a completed ZIP.
        return entries.Any(e => e.NexusModId > 0 && name.IndexOf("-" + e.NexusModId + "-", StringComparison.OrdinalIgnoreCase) >= 0);
    }
    static HashSet<string> KnownNames(List<ModCatalog.Entry> entries)
    {
        var owners = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries) foreach (string name in entry.DllNames ?? new List<string>())
        {
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) continue;
            HashSet<string> ids; if (!owners.TryGetValue(name, out ids)) owners[name] = ids = new HashSet<string>(); ids.Add(entry.Id);
        }
        return new HashSet<string>(owners.Where(p => p.Value.Count == 1 && !new[] { "Mod.dll", "Plugin.dll", "Assembly-CSharp.dll", "BepInEx.dll", "0Harmony.dll", "Newtonsoft.Json.dll" }.Contains(p.Key, StringComparer.OrdinalIgnoreCase) && !p.Key.StartsWith("System.", StringComparison.OrdinalIgnoreCase) && !p.Key.StartsWith("Unity", StringComparison.OrdinalIgnoreCase)).Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
    }
    static void Classify(Item item, List<string> paths, List<ModCatalog.Entry> entries, HashSet<string> known)
    {
        if (String.Equals(item.Hash, LoaderSha256, StringComparison.OrdinalIgnoreCase)) { Builtin(item, "bepinex", "BepInEx 5.4.23.5", "Готов · официальный SHA-256 загрузчика"); return; }
        var matches = entries.Where(e => IsHash(e.ArchiveSha256) && String.Equals(e.ArchiveSha256, item.Hash, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) { item.Mod = matches[0]; item.Kind = "catalog"; item.DisplayName = item.Mod.Name; item.PotentiallyRelevant = true; item.State = "Готов · хеш совпадает с каталогом"; return; }
        if (matches.Count > 1) { Builtin(item, "potential", "Несколько записей каталога", "ZIP готов · общий хеш, выберите мод вручную"); return; }
        var canonical = paths.Select(Canonical).ToList();
        if (canonical.Contains("BepInEx/plugins/CuRussian/CuRussian.dll", StringComparer.OrdinalIgnoreCase)) { Builtin(item, "russian", "Русский перевод", "ZIP готов · структура русского модуля, проверьте источник"); return; }
        if (canonical.Contains("BepInEx/plugins/CuWiki/CuWiki.dll", StringComparer.OrdinalIgnoreCase)) { Builtin(item, "wiki", "Справочник открытий", "ZIP готов · структура справочника, проверьте источник"); return; }
        if (canonical.Contains("BepInEx/plugins/Together/CasualtiesMP.dll", StringComparer.OrdinalIgnoreCase) && (canonical.Contains("BepInEx/plugins/Together/LiteNetLib.dll", StringComparer.OrdinalIgnoreCase) || canonical.Contains("BepInEx/patchers/CT_AutoUpdateApplier.dll", StringComparer.OrdinalIgnoreCase))) { Builtin(item, "together", "Casualties: Together", "ZIP готов · структура Together, проверьте версию и источник"); return; }
        if (paths.Any(p => p.Split('/').Any(segment => segment.Equals("gamedata", StringComparison.OrdinalIgnoreCase)))) return;
        bool knownDll = paths.Any(p => known.Contains(Path.GetFileName(p)) && PlausiblePluginPath(p));
        if (knownDll) Builtin(item, "potential", "Возможно, мод из каталога", "ZIP готов · имя DLL есть в каталоге; принадлежность не подтверждена");
    }
    static void Builtin(Item item, string kind, string name, string state) { item.Kind = kind; item.DisplayName = name; item.State = state; item.PotentiallyRelevant = true; }
    static string Canonical(string path) { int slash = path.IndexOf('/'); return slash > 0 && path.Substring(slash + 1).StartsWith("BepInEx/", StringComparison.OrdinalIgnoreCase) ? path.Substring(slash + 1) : path; }
    static bool PlausiblePluginPath(string path)
    {
        string c = Canonical(path); if (c.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase)) return true;
        var pieces = path.Split('/'); return pieces.Length <= 2 && !pieces.Any(p => new[] { "bin", "gamedata", "scripts", "mods", "patchers", "core" }.Contains(p, StringComparer.OrdinalIgnoreCase));
    }
    // Bounded standard ZIP central-directory parser: no entry data is opened/decompressed.
    static List<string> CentralPaths(Stream stream, ref long budget)
    {
        if (stream.Length < 22 || stream.Length > FileLimit) throw new InvalidDataException();
        int tailSize = (int)Math.Min(stream.Length, 65557); Spend(ref budget, tailSize); byte[] tail = new byte[tailSize]; stream.Position = stream.Length - tailSize; ReadExact(stream, tail);
        int end = -1;
        for (int i = tail.Length - 22; i >= 0; i--) if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { end = i; break; }
        if (end < 0 || U16(tail, end + 4) != 0 || U16(tail, end + 6) != 0) throw new InvalidDataException();
        int count = U16(tail, end + 10); long size = U32(tail, end + 12), offset = U32(tail, end + 16);
        if (count == 65535 || count > 10000 || U16(tail, end + 8) != count || size > 8 * 1024 * 1024 || offset + size > stream.Length - tailSize + end) throw new InvalidDataException();
        Spend(ref budget, size); stream.Position = offset; var paths = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = new BinaryReader(stream, Encoding.UTF8, true)) for (int i = 0; i < count; i++)
        {
            long start = stream.Position; byte[] header = reader.ReadBytes(46); if (header.Length != 46 || U32(header, 0) != 0x02014b50) throw new InvalidDataException();
            int flags = U16(header, 8), length = U16(header, 28), extra = U16(header, 30), comment = U16(header, 32); uint external = U32(header, 38);
            if ((flags & 1) != 0 || length == 0 || length > 1024 || U16(header, 34) != 0 || U32(header, 20) == UInt32.MaxValue || U32(header, 24) == UInt32.MaxValue || U32(header, 42) == UInt32.MaxValue || (external & 0x400) != 0 || ((external >> 16) & 0xF000) == 0xA000 || start + 46L + length + extra + comment > offset + size) throw new InvalidDataException();
            byte[] nameBytes = reader.ReadBytes(length); if (nameBytes.Length != length) throw new InvalidDataException(); string name;
            try { name = ((flags & 0x800) != 0 ? (Encoding)new UTF8Encoding(false, true) : Encoding.GetEncoding(437)).GetString(nameBytes).Replace('\\', '/'); } catch { throw new InvalidDataException(); }
            bool directory = name.EndsWith("/"); name = name.TrimEnd('/');
            if (name.Length == 0 || name.StartsWith("/") || name.IndexOf(':') >= 0 || name.Any(c => Char.IsControl(c)) || name.Split('/').Any(p => p.Length == 0 || p == "." || p == ".." || p.EndsWith(".") || p.EndsWith(" ")) || !seen.Add(name)) throw new InvalidDataException();
            if (!directory) paths.Add(name); stream.Position += extra + comment;
        }
        if (stream.Position != offset + size) throw new InvalidDataException(); return paths;
    }
    static void Spend(ref long budget, long bytes) { if (bytes < 0 || bytes > budget) throw new BudgetException(); budget -= bytes; }
    static void ReadExact(Stream input, byte[] data) { int offset = 0; while (offset < data.Length) { int read = input.Read(data, offset, data.Length - offset); if (read == 0) throw new InvalidDataException(); offset += read; } }
    static int U16(byte[] data, int p) { return data[p] | data[p + 1] << 8; }
    static uint U32(byte[] data, int p) { return (uint)(data[p] | data[p + 1] << 8 | data[p + 2] << 16 | data[p + 3] << 24); }
}
