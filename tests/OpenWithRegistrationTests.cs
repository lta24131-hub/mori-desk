using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace MoriDesk {
 internal static class OpenWithRegistrationTests {
  static string Value(RegistryKey scope,string path,string name){using(var key=scope.OpenSubKey(path))return key==null?null:Convert.ToString(key.GetValue(name,null));}
  static void Set(RegistryKey scope,string path,string name,string value){using(var key=scope.CreateSubKey(path))key.SetValue(name,value);}
  static string CopyProgram(string root,string version,string source){string file=Path.Combine(root,version,"Mori.exe");Directory.CreateDirectory(Path.GetDirectoryName(file));File.Copy(source,file);return file;}
  static bool Changed(RegistryKey scope,string executable,string managedRoot,ref int notices){int count=notices;bool result=OpenWithRegistration.RepairInScope(scope,executable,managedRoot,()=>count++);notices=count;return result;}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);
   string registryPath="Software\\Nook.Tests\\OpenWith-"+Guid.NewGuid().ToString("N");
   // The sole mutable registry location is this newly-created, unique fixture.
   try{
    using(var scope=Registry.CurrentUser.CreateSubKey(registryPath)){
     string managedRoot=Path.Combine(root,"Programs","MoriDesk"),assembly=typeof(Entry).Assembly.Location;
     string old=CopyProgram(managedRoot,"0.1.23-20261014000000",assembly),current=CopyProgram(managedRoot,"0.1.23-20261015000000",assembly),newer=CopyProgram(managedRoot,"0.1.23-20261016000000",assembly);
     string app=OpenWithRegistration.ApplicationKey;
     string choices="Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\.png";
     Set(scope,"Software\\Classes\\.png","","Other.Photo");
     Set(scope,"Software\\Classes\\.png\\OpenWithProgids","Other.Photo","");
     Set(scope,choices+"\\UserChoice","ProgId","Other.Photo");Set(scope,choices+"\\UserChoice","Hash","must-not-change");
     Set(scope,choices+"\\OpenWithList","MRUList","ba");Set(scope,choices+"\\OpenWithList","a","Mori.exe");
     Set(scope,"Software\\RegisteredApplications","Other.Photo","Software\\Other\\Capabilities");
     Set(scope,"Software\\Classes\\Applications\\Other.exe","FriendlyAppName","Other Photo");
     Set(scope,app+"\\shell\\open\\command","","\""+old+"\" \"%1\"");
     Set(scope,app+"\\SupportedTypes",".custom-extension","");
     int notices=0;
     check(Changed(scope,current,managedRoot,ref notices)&&notices==1,"legacy Open-with registration migrates once and notifies Explorer");
     check(Value(scope,app,"FriendlyAppName")=="Nook"&&Value(scope,app+"\\Application","ApplicationName")=="Nook","Explorer application name explicitly uses Nook");
     check(Value(scope,app+"\\DefaultIcon","")=="\""+current+"\",0"&&Value(scope,app+"\\Application","ApplicationIcon")=="\""+current+"\",0","Explorer uses the current embedded Nook icon, not the legacy executable");
     check(Value(scope,app+"\\shell\\open\\command","")=="\""+current+"\" \"%1\"","Open-with launches the verified current installation with a quoted file argument");
     check(OpenWithRegistration.SupportedExtensions.All(ext=>Value(scope,app+"\\SupportedTypes",ext)==""),"all advertised preview extensions are added without selecting defaults");
     check(OpenWithRegistration.SupportedExtensions.Distinct(StringComparer.OrdinalIgnoreCase).Count()==OpenWithRegistration.SupportedExtensions.Length,"supported extensions do not contain duplicate aliases");
     var routed=new HashSet<string>(FileRouter.Pictures.Concat(FileRouter.Models).Concat(Mori.MediaTypes.Documents).Concat(Mori.MediaTypes.Audio).Concat(Mori.MediaTypes.Videos),StringComparer.OrdinalIgnoreCase);
     check(routed.SetEquals(OpenWithRegistration.SupportedExtensions),"Open-with advertises exactly the file router's supported formats; missing="+string.Join(",",routed.Except(OpenWithRegistration.SupportedExtensions,StringComparer.OrdinalIgnoreCase))+" extra="+string.Join(",",OpenWithRegistration.SupportedExtensions.Except(routed,StringComparer.OrdinalIgnoreCase)));
     foreach(var family in new[]{new{Extensions=FileFormats.Pictures,Kind="image",Section="pictures"},new{Extensions=FileFormats.Videos,Kind="preview",Section="videos"},new{Extensions=FileFormats.Audio,Kind="preview",Section="audio"},new{Extensions=FileFormats.Documents,Kind="preview",Section="documents"}}){
      foreach(string extension in family.Extensions.Split(' ')){
       var asset=new Mori.Asset{Ext=extension.ToUpperInvariant()};
       check(FileRouter.Kind("sample"+extension.ToUpperInvariant())==family.Kind&&OpenWithRegistration.SupportedExtensions.Contains(extension),"the same capability routes and registers "+extension+" case insensitively");
       check(new[]{"pictures","videos","audio","documents"}.Count(section=>Mori.MediaTypes.Contains(asset,section))==1&&Mori.MediaTypes.Contains(asset,family.Section),"previewable format belongs to exactly its declared library category: "+extension);
      }
     }
     foreach(string extension in new[]{".flv",".f4v",".3g2",".vob",".dv",".mxf",".m4b",".ac3",".amr",".au",".caf",".weba",".mka",".mp2",".voc",".wv",".tta",".w64",".eac3"})
      check(FileRouter.Kind("converted"+extension)=="preview"&&Value(scope,app+"\\SupportedTypes",extension)=="","newly classified media has a real Open-with registry entry and preview route: "+extension);
     foreach(string extension in FileFormats.ContentDocuments.Split(' '))check(Mori.DocumentContent.Supports(extension)&&Mori.MediaTypes.TextDocuments.Contains(extension),"document content reader and classification share the supported format: "+extension);
     foreach(string extension in FileFormats.Models.Split(' '))check(FileRouter.Kind("model"+extension.ToUpperInvariant())=="model"&&OpenWithRegistration.SupportedExtensions.Contains(extension),"model catalog routes and advertises the same capability: "+extension);
     foreach(string extension in new[]{".qoi",".jxl",".eps",".svgz",".odg"}){
      check(Mori.MediaTypes.Contains(new Mori.Asset{Ext=extension},"pictures")&&FileRouter.Kind("converted"+extension)=="external"&&!OpenWithRegistration.SupportedExtensions.Contains(extension),"conversion picture classification does not falsely advertise an unavailable native preview: "+extension);
     }
     foreach(string extension in new[]{".url",".exe",".ps1",".doc",".xls",".ppt",".unsupported"})check(FileRouter.Kind("unknown"+extension)=="external"&&!OpenWithRegistration.SupportedExtensions.Contains(extension),"external-only formats are not claimed by Explorer preview registration: "+extension);
     check(Value(scope,app+"\\SupportedTypes",".custom-extension")=="","unknown supported-type values remain intact");
     check(Value(scope,"Software\\Classes\\.png","")=="Other.Photo"&&Value(scope,"Software\\Classes\\.png\\OpenWithProgids","Other.Photo")=="","extension defaults and other ProgIDs are not changed");
     check(Value(scope,choices+"\\UserChoice","ProgId")=="Other.Photo"&&Value(scope,choices+"\\UserChoice","Hash")=="must-not-change","Windows protected default-app choice and hash stay intact");
     check(Value(scope,choices+"\\OpenWithList","MRUList")=="ba"&&Value(scope,choices+"\\OpenWithList","a")=="Mori.exe","Explorer's Open-with history is not cleaned or reordered");
     check(Value(scope,"Software\\RegisteredApplications","Other.Photo")=="Software\\Other\\Capabilities"&&Value(scope,"Software\\Classes\\Applications\\Other.exe","FriendlyAppName")=="Other Photo","other applications and capabilities registrations remain intact");
     check(!Changed(scope,current,managedRoot,ref notices)&&notices==1,"idempotent registration neither writes changes nor repeatedly invalidates Explorer icons");
     check(Changed(scope,newer,managedRoot,ref notices)&&notices==2,"a later equal-version installation updates the target");
     check(!Changed(scope,old,managedRoot,ref notices)&&Value(scope,app+"\\shell\\open\\command","")=="\""+newer+"\" \"%1\""&&notices==2,"starting an older equal-version installation cannot downgrade the registered target");
     check(OpenWithRegistration.RetainExisting(old,new Version(0,2),newer,new Version(0,1)),"an actual newer file version wins even if its directory timestamp is older");
     check(!OpenWithRegistration.RetainExisting(newer,new Version(0,1),old,new Version(0,2)),"an actual newer incoming version beats an older file with a later timestamp");
     scope.DeleteSubKeyTree(app);
     check(Changed(scope,current,managedRoot,ref notices)&&Value(scope,app,"FriendlyAppName")=="Nook","a clean install creates only its own stable Applications registration");
     check(OpenWithRegistration.MutexName(scope)!=OpenWithRegistration.MutexName(Registry.CurrentUser),"isolated registry fixtures do not contend with the real application's startup repair");
     using(var start=new ManualResetEventSlim(false)){
      var contenders=Enumerable.Range(0,12).Select(i=>Task.Run(()=>{start.Wait();OpenWithRegistration.RepairInScope(scope,i%2==0?old:newer,managedRoot,null);})).ToArray();
      start.Set();check(Task.WaitAll(contenders,15000),"concurrent old and new registration repairs complete within a bounded wait");
      check(Value(scope,app+"\\shell\\open\\command","")=="\""+newer+"\" \"%1\""&&Value(scope,app+"\\DefaultIcon","")=="\""+newer+"\",0","concurrent older startups cannot downgrade the newest registered command or icon");
     }
     foreach(string bad in new[]{null,"","Mori.exe",Path.Combine(root,"Mori.exe"),Path.Combine(root,"Programs","MoriDeskOther","v1","Mori.exe"),Path.Combine(managedRoot,"v1","child","Mori.exe"),Path.Combine(managedRoot,"v1","Nook.exe"),Path.Combine(managedRoot,"..","Other","v1","Mori.exe")})check(!OpenWithRegistration.ManagedPath(bad,managedRoot),"unknown, adjacent, escaped or nested installation paths are rejected");
     check(OpenWithRegistration.ManagedPath(current,managedRoot)&&OpenWithRegistration.ManagedPath(current.ToUpperInvariant(),managedRoot.ToLowerInvariant()+"\\"),"the controlled direct version directory is normalized case-insensitively");
     foreach(string command in new[]{null,"","Mori.exe %1","\"C:\\Mori.exe\" %1","\"C:\\Mori.exe\" \"%1\" --extra","\"C:\\Mori.exe\" \"%L\"","\"C:\\Mori.exe","cmd.exe /c Mori.exe \"%1\""})check(OpenWithRegistration.CommandTarget(command)==null,"unknown commands and extra arguments are not treated as owned");
     check(OpenWithRegistration.CommandTarget("\""+current+"\" \"%1\"")==current,"the exact historical command can be parsed without evaluating it");
     foreach(string command in new[]{"\""+Path.Combine(root,"Elsewhere","Mori.exe")+"\" \"%1\"","\""+Path.Combine(managedRoot,"missing","Mori.exe")+"\" \"%1\"","cmd.exe /c Mori.exe \"%1\""}){
      Set(scope,app+"\\shell\\open\\command","",command);int before=notices;
      check(!Changed(scope,current,managedRoot,ref notices)&&Value(scope,app+"\\shell\\open\\command","")==command&&notices==before,"external, absent or wrapped application ownership is never guessed or overwritten");
     }
     string other=CopyProgram(managedRoot,"other-product",typeof(string).Assembly.Location);
     Set(scope,app+"\\shell\\open\\command","","\""+other+"\" \"%1\"");
     check(!Changed(scope,current,managedRoot,ref notices)&&Value(scope,app+"\\shell\\open\\command","")=="\""+other+"\" \"%1\"","same executable filename and managed-looking path do not claim an unrelated product");
     Set(scope,app+"\\shell\\open\\command","","\""+current+"\" \"%1\"");Set(scope,app,"FriendlyAppName","Another Mori");
     check(!Changed(scope,current,managedRoot,ref notices)&&Value(scope,app,"FriendlyAppName")=="Another Mori","an application-name collision is preserved for manual resolution");
     int finalNotices=notices;
     check(!Changed(scope,other,managedRoot,ref notices)&&notices==finalNotices,"the candidate must also have verified Nook executable metadata");
    }
   }finally{
     // Delete only the exact fresh test key after confirming it is in the test
     // namespace; production registration keys are never deletion targets.
     if(!registryPath.StartsWith("Software\\Nook.Tests\\OpenWith-",StringComparison.Ordinal)||registryPath.Length!="Software\\Nook.Tests\\OpenWith-".Length+32)throw new InvalidOperationException("Invalid test registry cleanup target");
     Registry.CurrentUser.DeleteSubKeyTree(registryPath);
   }
  }
 }
}
