using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal static class SteamGameLocator
{
    internal static bool IsGame(string path)
    {
        try { return !String.IsNullOrWhiteSpace(path) && File.Exists(Path.Combine(path, "CasualtiesUnknown.exe")) && Directory.Exists(Path.Combine(path, "CasualtiesUnknown_Data")); }
        catch { return false; }
    }
    internal static string Unescape(string value) { return value.Replace("\\\\", "\\").Replace("\\\"", "\""); }
    internal static IEnumerable<string> Libraries(string steamRoot, string text)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        paths.Add(Path.GetFullPath(steamRoot));
        // Modern libraryfolders.vdf: "path" "D:\\SteamLibrary".
        // Old layout: "1" "D:\\SteamLibrary".
        foreach (Match m in Regex.Matches(text ?? "", "\"(?:path|[0-9]+)\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.IgnoreCase))
        {
            string value = Unescape(m.Groups[1].Value);
            if (!Path.IsPathRooted(value)) continue;
            try { paths.Add(Path.GetFullPath(value)); } catch { }
        }
        return paths;
    }
    internal static string InLibrary(string library)
    {
        try
        {
            string common = Path.GetFullPath(Path.Combine(library, "steamapps", "common"));
            string manifest = Path.Combine(library, "steamapps", "appmanifest_4576490.acf");
            string directory = "Casualties Unknown";
            if (File.Exists(manifest))
            {
                Match m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s*\"((?:\\\\.|[^\"\\\\])*)\"");
                if (m.Success) directory = Unescape(m.Groups[1].Value);
            }
            string candidate = Path.GetFullPath(Path.Combine(common, directory));
            if (!candidate.StartsWith(common + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            return IsGame(candidate) ? candidate : null;
        }
        catch { return null; }
    }
    static void RegistryPath(HashSet<string> roots, RegistryKey hive, string key, string name)
    {
        try { using (var sub = hive.OpenSubKey(key)) { string path = sub == null ? null : sub.GetValue(name) as string; if (!String.IsNullOrWhiteSpace(path)) roots.Add(path); } } catch { }
    }
    internal static string Find(string saved, string launcherDirectory)
    {
        if (IsGame(saved)) return Path.GetFullPath(saved);
        if (IsGame(launcherDirectory)) return Path.GetFullPath(launcherDirectory);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RegistryPath(roots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        RegistryPath(roots, Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        RegistryPath(roots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));
        foreach (string root in roots)
        {
            try
            {
                string file = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                foreach (string library in Libraries(root, File.Exists(file) ? File.ReadAllText(file) : ""))
                { string game = InLibrary(library); if (game != null) return game; }
            }
            catch { }
        }
        return "";
    }
}
