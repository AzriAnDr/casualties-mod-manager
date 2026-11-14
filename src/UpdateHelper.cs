using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading;
using System.Text;

// Compiled as a separate hidden .NET Framework EXE and embedded as UpdateHelper.exe.
internal static class UpdateHelper
{
    const long MaximumExeBytes = 32L * 1024 * 1024;
    static string DetailLog;
    static readonly string GlobalLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CasualtiesModManager", "update.log");

    [STAThread] static int Main(string[] args)
    {
        string target = null, backup = null, pending = null;
        try
        {
            if (args.Length != 7 || args[0] != "--apply") throw new InvalidDataException("Некорректные параметры помощника обновления.");
            int parentId; if (!Int32.TryParse(args[1], out parentId) || parentId <= 0 || parentId == Process.GetCurrentProcess().Id) throw new InvalidDataException("Некорректный PID менеджера.");
            string staged = Absolute(args[2]); target = Absolute(args[3]); string hash = args[4], ready = Absolute(args[5]), token = args[6];
            string stageDir = Path.GetDirectoryName(staged);
            RequireStage(staged, ready, token);
            DetailLog = Path.Combine(stageDir, "update-helper.log");
            Log("Подготовка обновления: " + target);
            RequireRegularFile(target); RequireRegularFile(staged); VerifyExe(staged, hash);
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || target.Equals(staged, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Некорректный EXE назначения.");
            using (Process parent = Process.GetProcessById(parentId))
            {
                if (!String.Equals(Absolute(parent.MainModule.FileName), target, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Путь EXE не совпадает с запущенным менеджером.");
                // Readiness is published only after all paths, hash, EXE and parent identity were checked.
                using (var file = new FileStream(ready, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { byte[] text = Encoding.ASCII.GetBytes(token); file.Write(text, 0, text.Length); }
                Log("Готово; ожидание завершения PID " + parentId);
                if (!parent.WaitForExit(120000)) throw new IOException("Менеджер не завершился за две минуты. Обновление отменено; исходный EXE сохранён.");
            }
            VerifyExe(staged, hash); RequireRegularFile(target);
            string targetDir = Path.GetDirectoryName(target);
            pending = Path.Combine(targetDir, "." + Path.GetFileName(target) + ".pending-" + Guid.NewGuid().ToString("N") + ".exe");
            // Copy beside the live EXE first, so the final replacement is always on one volume.
            using (var source = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read)) using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(output);
            VerifyExe(pending, hash);
            backup = target + ".bak";
            if (File.Exists(backup) || Directory.Exists(backup)) backup = target + ".bak." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
            Retry(delegate { RequireRegularFile(target); VerifyExe(pending, hash); File.Replace(pending, target, backup, true); });
            VerifyExe(target, hash);
            Log("Обновление установлено; резервная копия: " + backup);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = targetDir });
            Log("Запуск нового менеджера запрошен. Резервная копия сохраняется до инициализации его окна.");
            return 0;
        }
        catch (Exception error)
        {
            Log("ОШИБКА: " + error);
            if (backup != null && File.Exists(backup))
            {
                try
                {
                    string failed = target + ".failed-" + Guid.NewGuid().ToString("N") + ".exe";
                    Retry(delegate { RequireRegularFile(backup); if (File.Exists(target)) File.Replace(backup, target, failed, true); else File.Move(backup, target); });
                    Log("Исходный EXE восстановлен. Неудачная версия сохранена: " + failed);
                }
                catch (Exception restore) { Log("Не удалось автоматически восстановить исходный EXE. Резервная копия: " + backup + ". " + restore); }
            }
            return 1;
        }
        finally
        {
            if (pending != null) try { if (File.Exists(pending)) File.Delete(pending); } catch { }
        }
    }

    static void Retry(Action action)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            try { action(); return; }
            catch (IOException) { if (timer.ElapsedMilliseconds >= 30000) throw; }
            catch (UnauthorizedAccessException) { if (timer.ElapsedMilliseconds >= 30000) throw; }
            Thread.Sleep(400);
        }
    }
    static string Absolute(string path)
    {
        if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new InvalidDataException("Требуется абсолютный путь.");
        string full = Path.GetFullPath(path);
        if (full.StartsWith("\\\\", StringComparison.Ordinal) || full.IndexOf(':', 2) >= 0 || full.IndexOf('"') >= 0) throw new InvalidDataException("Недопустимый путь обновления.");
        for (var d = new DirectoryInfo(Path.GetDirectoryName(full)); d != null; d = d.Parent) if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Путь содержит ссылку или junction: " + d.FullName);
        return full;
    }
    static void RequireStage(string staged, string ready, string token)
    {
        string root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CasualtiesModManager", "updates")).TrimEnd('\\') + "\\";
        Guid value;
        if (!staged.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(staged) != "Casualties-ModManager.update.exe" || !Guid.TryParseExact(Path.GetFileName(Path.GetDirectoryName(staged)), "N", out value)) throw new InvalidDataException("Неизвестная папка подготовленного обновления.");
        if (!Guid.TryParseExact(token, "N", out value) || !String.Equals(Path.GetDirectoryName(staged), Path.GetDirectoryName(ready), StringComparison.OrdinalIgnoreCase) || Path.GetFileName(ready) != "ready-" + token + ".txt") throw new InvalidDataException("Некорректный путь подтверждения готовности.");
        string helperSelf = Path.GetFullPath(Assembly.GetExecutingAssembly().Location);
        if (!String.Equals(Path.GetDirectoryName(helperSelf), Path.GetDirectoryName(staged), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Помощник должен находиться рядом с подготовленным EXE.");
    }
    static void RequireRegularFile(string path)
    {
        Absolute(path);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("EXE отсутствует или является ссылкой: " + path);
    }
    static void VerifyExe(string path, string expected)
    {
        if (expected == null || expected.Length != 64) throw new InvalidDataException("Отсутствует SHA-256 EXE.");
        foreach (char c in expected) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) throw new InvalidDataException("Некорректный SHA-256.");
        RequireRegularFile(path);
        using (var file = File.OpenRead(path))
        {
            if (file.Length > MaximumExeBytes || file.Length < 256) throw new InvalidDataException("Некорректный размер EXE.");
            string actual; using (var sha = SHA256.Create()) actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA-256 EXE изменился. Установка отменена.");
            file.Position = 0;
            using (var reader = new BinaryReader(file))
            {
                if (reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Нет MZ-заголовка EXE.");
                file.Position = 0x3c; int pe = reader.ReadInt32(); if (pe < 64 || pe > file.Length - 26) throw new InvalidDataException("Некорректный адрес PE-заголовка.");
                file.Position = pe; if (reader.ReadUInt32() != 0x4550) throw new InvalidDataException("Нет PE-сигнатуры EXE.");
                ushort machine = reader.ReadUInt16(); file.Position = pe + 22; ushort flags = reader.ReadUInt16(), optional = reader.ReadUInt16();
                if ((machine != 0x14c && machine != 0x8664) || (flags & 2) == 0 || (flags & 0x2000) != 0 || (optional != 0x10b && optional != 0x20b)) throw new InvalidDataException("Неподдерживаемый EXE обновления.");
            }
        }
        try { AssemblyName.GetAssemblyName(path); } catch (Exception e) { throw new InvalidDataException("Обновление должно быть .NET-приложением.", e); }
    }
    static void Log(string text)
    {
        string line = DateTime.Now.ToString("s") + " " + text + Environment.NewLine;
        try { Directory.CreateDirectory(Path.GetDirectoryName(GlobalLog)); File.AppendAllText(GlobalLog, line, Encoding.UTF8); } catch { }
        try { if (DetailLog != null) File.AppendAllText(DetailLog, line, Encoding.UTF8); } catch { }
    }
}
