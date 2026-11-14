using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

// Presentation only: the existing Together label producer retains every branch,
// counter and caller. No TMP setters, callbacks, ready state or network code patched.
internal static class TogetherReadyLabels
{
    private static Language russian;
    private static bool enabled;
    internal static void Enable(Harmony harmony,ManualLogSource log,Language russianLocale)
    {
        russian=russianLocale;
        if(enabled)return;
        var type=AccessTools.TypeByName("Together.CharacterSelectWait");
        var target=type==null?null:type.GetMethod("ReadyButtonText",BindingFlags.Static|BindingFlags.NonPublic,null,Type.EmptyTypes,null);
        if(target==null||target.ReturnType!=typeof(string))
        {
            log.LogWarning("Together ready-label localization: compatible label producer not found; no hooks installed.");return;
        }
        var postfix=new HarmonyMethod(typeof(TogetherReadyLabels),"AfterLabel");postfix.priority=Priority.Last;
        harmony.Patch(target,postfix:postfix);enabled=true;
        log.LogInfo("Together Join/Ready label localization enabled; original permissions and counters retained.");
    }
    private static void AfterLabel(ref string __result)
    {
        // CuRussian owns this exact Language instance. English/another selected
        // locale must never receive its Russian strings, even when keys overlap.
        if(russian==null||!Object.ReferenceEquals(Locale.currentLang,russian))return;
        __result=Translate(__result,russian.other);
    }
    internal static string Translate(string original,Dictionary<string,string> strings)
    {
        if(original==null||strings==null)return original;
        string key=null,ready=null,max=null;
        if(original=="Join")key="casualtiestogether_mmsb_lobbyjoin";
        else if(original=="Joining...")key="casualtiestogether_charselect_joining";
        else
        {
            string prefix;
            if(original.StartsWith("Ready up [",StringComparison.Ordinal)){prefix="Ready up [";key="casualtiestogether_charselect_readyup";}
            else if(original.StartsWith("Ready [",StringComparison.Ordinal)){prefix="Ready [";key="casualtiestogether_charselect_ready";}
            else return original;
            // Match only the exact producer template. Manual chat, suffixes,
            // rich text and unrelated UI strings remain unchanged.
            if(original.Length>64||!original.EndsWith("]",StringComparison.Ordinal))return original;
            string counts=original.Substring(prefix.Length,original.Length-prefix.Length-1);
            int slash=counts.IndexOf('/');
            if(slash<=0||slash==counts.Length-1||counts.IndexOf('/',slash+1)>=0)return original;
            ready=counts.Substring(0,slash);max=counts.Substring(slash+1);
            if(!Digits(ready)||!Digits(max))return original;
        }
        string value;
        if(!strings.TryGetValue(key,out value)||String.IsNullOrWhiteSpace(value))return original;
        if(ready==null)return value;
        // Preserve the exact two counter substrings, including zeroes. A bad
        // locale template must not blank the live button or discard its count.
        if(Count(value,"{0}")!=1||Count(value,"{1}")!=1)return original;
        string rest=value.Replace("{0}","").Replace("{1}","");
        if(rest.IndexOf('{')>=0||rest.IndexOf('}')>=0)return original;
        try{return String.Format(CultureInfo.InvariantCulture,value,ready,max);}
        catch(FormatException){return original;}
    }
    private static bool Digits(string value)
    {
        foreach(char c in value)if(c<'0'||c>'9')return false;return value.Length>0;
    }
    private static int Count(string text,string token)
    {
        int count=0,start=0,index;
        while((index=text.IndexOf(token,start,StringComparison.Ordinal))>=0){count++;start=index+token.Length;}
        return count;
    }
}
