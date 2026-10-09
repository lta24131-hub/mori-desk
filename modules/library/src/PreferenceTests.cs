using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mori {
 public static class PreferenceTests {
  public static List<string> Run(string output){
   Directory.CreateDirectory(output);var log=new List<string>();
   Action<bool,string> check=delegate(bool ok,string name){if(!ok)throw new Exception("FAIL: "+name);log.Add("PASS: "+name);};
   string path=Path.Combine(output,"settings.json");
   if(File.Exists(path))throw new IOException("Preference tests require a new output directory.");
   var settings=Settings.LoadFrom(path);
   check(settings.Theme=="dark"&&!string.IsNullOrWhiteSpace(settings.LibraryPath),"preferences default to dark with a library path");
   check(!File.Exists(path),"reading missing preferences does not create a file");
   string library=Path.Combine(output,"图库 with spaces");
   settings.LibraryPath=library;settings.Theme="light";settings.SaveTo(path);
   var loaded=Settings.LoadFrom(path);
   check(loaded.LibraryPath==library&&loaded.Theme=="light","library and light theme survive preferences reload");
   loaded.LibraryPath=Path.Combine(output,"second-library");loaded.SaveTo(path);
   check(Settings.LoadFrom(path).Theme=="light","changing libraries preserves selected theme");
   check(Directory.GetFiles(output,"*.tmp").Length==0,"atomic preference replacement leaves no temporary file");
   var validBytes=File.ReadAllBytes(path);loaded.LibraryPath=" ";bool rejected=false;
   try{loaded.SaveTo(path);}catch(InvalidDataException){rejected=true;}
   check(rejected&&validBytes.SequenceEqual(File.ReadAllBytes(path)),"invalid save preserves existing preferences");
   var json=new System.Web.Script.Serialization.JavaScriptSerializer();
   File.WriteAllText(path,json.Serialize(new Dictionary<string,object>{{"LibraryPath",library}}));
   loaded=Settings.LoadFrom(path);
   check(loaded.LibraryPath==library&&loaded.Theme=="dark","legacy settings retain library with default theme");
   File.WriteAllText(path,json.Serialize(new Dictionary<string,object>{{"LibraryPath",library},{"Theme",123}}));
   loaded=Settings.LoadFrom(path);
   check(loaded.LibraryPath==library&&loaded.Theme=="dark","invalid theme cannot discard a valid library path");
   loaded.Theme="LIGHT";check(loaded.Theme=="light","theme value is normalized");
   string corrupt="{\"LibraryPath\":\"do not replace this file\"";File.WriteAllText(path,corrupt);rejected=false;
   try{Settings.LoadFrom(path);}catch(InvalidDataException ex){rejected=ex.Message.Contains(path);}
   check(rejected&&File.ReadAllText(path)==corrupt,"malformed preferences report their path and preserve original bytes");
   File.WriteAllText(path,"null");rejected=false;try{Settings.LoadFrom(path);}catch(InvalidDataException){rejected=true;}
   check(rejected&&File.ReadAllText(path)=="null","null preferences are reported rather than silently reset");
   var startup=new MemoryStartupStore();startup.Values["Other App"]="keep this entry";
   string executable=Path.Combine(output,"应用 folder","Mori.exe");
   check(!StartupRegistration.IsEnabled(executable,startup),"Windows startup defaults off");
   StartupRegistration.SetEnabled(true,executable,startup);
   check(startup.Values[StartupRegistration.ValueName]=="\""+Path.GetFullPath(executable)+"\" --startup","Windows startup command quotes the complete executable path");
   check(StartupRegistration.IsEnabled(executable,startup),"explicit opt-in enables the exact Mori executable");
   check(!StartupRegistration.IsEnabled(Path.Combine(output,"another","Mori.exe"),startup),"Windows startup does not match a different executable");
   StartupRegistration.SetEnabled(false,executable,startup);StartupRegistration.SetEnabled(false,executable,startup);
   check(!StartupRegistration.IsEnabled(executable,startup)&&startup.Values.Count==1&&startup.Values["Other App"]=="keep this entry","opting out removes only Mori and can be repeated");
   rejected=false;try{StartupRegistration.SetEnabled(true,executable+"\" --bad",startup);}catch(ArgumentException){rejected=true;}
   check(rejected&&startup.Values.Count==1,"startup command rejects embedded quotes before writing");
   Uri uri;
   check(SourceLink.TryParse(" https://example.com/reference?q=one#image ",out uri)&&uri.Host=="example.com","normal HTTPS source link validates");
   check(SourceLink.TryParse("http://example.com/image.png",out uri),"normal HTTP source link validates");
   foreach(string invalid in new[]{"","relative/image.png","file:///C:/Windows/notepad.exe","javascript:alert(1)","mori://open","ftp://example.com/image.png","https://user:password@example.com/image.png","https://example.com/\r\nother","https://"})
    check(!SourceLink.TryParse(invalid,out uri)&&uri==null,"unsafe or malformed source link rejected: "+invalid.Replace("\r","\\r").Replace("\n","\\n"));
   return log;
  }
  sealed class MemoryStartupStore:StartupRegistration.IStartupStore {
   internal readonly Dictionary<string,string> Values=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   public string GetValue(string name){string value;return Values.TryGetValue(name,out value)?value:null;}
   public void SetValue(string name,string value){Values[name]=value;}
   public void DeleteValue(string name){Values.Remove(name);}
  }
 }
}
