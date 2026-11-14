using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

// Resolve only our pinned, embedded metadata library. Downloaded plugins are never loaded.
internal static class MetadataRuntime
{
    static readonly object gate = new object();
    static bool initialized;
    static Assembly library;
    internal static void Initialize()
    {
        lock(gate)
        {
            if(initialized)return;
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            initialized=true;
        }
    }
    static Assembly Resolve(object sender,ResolveEventArgs args)
    {
        var requested=new AssemblyName(args.Name);
        if(requested.Name!="Mono.Cecil"||requested.Version!=new Version(0,11,6,0)||BitConverter.ToString(requested.GetPublicKeyToken()??new byte[0]).Replace("-","").ToLowerInvariant()!="50cebf1cceb9d05e")return null;
        lock(gate)
        {
            if(library!=null)return library;
            using(var input=typeof(MetadataRuntime).Assembly.GetManifestResourceStream("Mono.Cecil.dll"))
            {
                if(input==null||input.Length>2*1024*1024)throw new InvalidDataException("Р’ РєРѕРјРїР»РµРєС‚Рµ РѕС‚СЃСѓС‚СЃС‚РІСѓРµС‚ Р±РёР±Р»РёРѕС‚РµРєР° С‡С‚РµРЅРёСЏ РјРµС‚Р°РґР°РЅРЅС‹С….");
                byte[] bytes;
                using(var memory=new MemoryStream()){input.CopyTo(memory);bytes=memory.ToArray();}
                using(var sha=SHA256.Create())
                    if(BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant()!="c41bdb9ffd3c5f6e17d2382c1012d73703e035e3f1100245fdd4e08c8dc6eb5b")
                        throw new InvalidDataException("РџРѕРІСЂРµР¶РґРµРЅР° РІСЃС‚СЂРѕРµРЅРЅР°СЏ Р±РёР±Р»РёРѕС‚РµРєР° С‡С‚РµРЅРёСЏ РјРµС‚Р°РґР°РЅРЅС‹С….");
                return library=Assembly.Load(bytes);
            }
        }
    }
}
