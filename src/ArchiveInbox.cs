using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;

// Read-only download discovery. Installing is always a separate reviewed action.
public static class ArchiveInbox
{
    public sealed class Item
    {
        public string Path,Hash,State;
        public long Length;
        public DateTime Written;
        public ModCatalog.Entry Mod;
    }
    public static List<Item> Scan(string folder,List<ModCatalog.Entry> entries,IDictionary<string,Item> cache)
    {
        var result=new List<Item>();long budget=512L*1024*1024;
        if(!Directory.Exists(folder))return result;
        foreach(var file in new DirectoryInfo(folder).GetFiles("*.zip").OrderByDescending(f=>f.LastWriteTimeUtc).Take(20))
        {
            if((file.Attributes&FileAttributes.ReparsePoint)!=0)continue;
            var item=new Item{Path=file.FullName,Length=file.Length,Written=file.LastWriteTimeUtc,State="Загрузка ещё не завершена"};result.Add(item);
            if(file.Length>128L*1024*1024){item.State="Большой архив — выберите вручную";continue;}
            if(DateTime.UtcNow-file.LastWriteTimeUtc<TimeSpan.FromSeconds(3))continue;
            Item old;
            if(cache.TryGetValue(file.FullName,out old)&&old.Length==file.Length&&old.Written==file.LastWriteTimeUtc&&old.Hash!=null)item.Hash=old.Hash;
            else
            {
                if(file.Length>budget){item.State="Выберите вручную для проверки";continue;}budget-=file.Length;
                try{using(var stream=new FileStream(file.FullName,FileMode.Open,FileAccess.Read,FileShare.None))using(var sha=SHA256.Create())item.Hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
                catch(IOException){continue;}catch(UnauthorizedAccessException){item.State="Нет доступа к файлу";continue;}
            }
            var matches=entries.Where(e=>!String.IsNullOrEmpty(e.ArchiveSha256)&&String.Equals(e.ArchiveSha256,item.Hash,StringComparison.OrdinalIgnoreCase)).ToList();
            item.Mod=matches.Count==1?matches[0]:null;
            item.State=item.Mod==null?"ZIP готов; источник не определён":"Готов · хеш совпадает с каталогом";
            cache[item.Path]=item;
        }
        return result;
    }
}
