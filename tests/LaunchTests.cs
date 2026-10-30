using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Mori;
namespace MoriDesk {
 internal static class LaunchTests {
  static void Reject(string[] args,Action<bool,string> check,string label){bool rejected=false;try{LaunchRequest.Parse(args);}catch(ArgumentException){rejected=true;}catch(FileNotFoundException){rejected=true;}check(rejected,label);}
  public static void Run(string root,Action<bool,string> check){
   string folder=Path.Combine(root,"open with 空格");Directory.CreateDirectory(folder);
   foreach(string ext in FileRouter.Pictures){string path=Path.Combine(folder,"图片 (2)"+ext.ToUpperInvariant());File.WriteAllText(path,"routing fixture");var request=LaunchRequest.Parse(new[]{path});check(request.Mode=="image"&&request.Path==path,"bare Explorer image routes to viewer: "+ext);}
   foreach(string ext in FileRouter.Models){string path=Path.Combine(folder,"装配"+ext.ToUpperInvariant());File.WriteAllText(path,"routing fixture");var request=LaunchRequest.Parse(new[]{path});check(request.Mode=="model"&&request.Path==path,"bare Explorer model routes to viewer: "+ext);}
   string image=Path.Combine(folder,"图片 (2).JPG"),model=Path.Combine(folder,"装配.STEP");
   check(LaunchRequest.Parse(new[]{"--image",image}).Path==image,"explicit image route retained");
   check(LaunchRequest.Parse(new[]{"--model",model}).Path==model,"explicit model route retained");
   check(LaunchRequest.Parse(new[]{"--image"}).Path==null&&LaunchRequest.Parse(new[]{"--model"}).Path==null,"empty viewers still open their file picker");
   string cwd=Environment.CurrentDirectory;try{Environment.CurrentDirectory=folder;check(LaunchRequest.Parse(new[]{"图片 (2).JPG"}).Path==image,"relative file resolves against caller directory");}finally{Environment.CurrentDirectory=cwd;}
   foreach(var args in new[]{new string[0],new[]{"--startup"},new[]{"--library",folder},new[]{"--ui-test",folder},new[]{"--library",folder,"--handoff","test-event"}})check(LaunchRequest.Parse(args).Mode=="library","intentional library startup retained: "+string.Join(" ",args));
   foreach(string worker in new[]{"--svg-render","--video-thumbnail","--video-self-test","--benchmark","--native-test","--image-codec","--pdf-text-worker","--self-test","chrome-extension://test-id/"})check(LaunchRequest.Parse(new[]{worker,"fixture"}).Mode=="worker","internal worker does not become main library: "+worker);
   foreach(var args in new[]{new[]{""},new[]{"--unknown"},new[]{"--library"},new[]{"--ui-test"},new[]{"--startup","extra"},new[]{"--image",model},new[]{"--model",image},new[]{image,model},new[]{"--image",image,model},new[]{folder},new[]{Path.Combine(folder,"missing.jpg")},new[]{Path.Combine(folder,"script.exe")},new[]{"https://example.com/a.jpg"},new[]{"file:///C:/a.jpg"},new[]{"--native-test"}})Reject(args,check,"invalid launch cannot fall through into a new library: "+string.Join(" ",args));
   string name=Program.InstanceName(folder);
   check(name==Program.InstanceName(folder+"\\")&&name==Program.InstanceName(folder+"/")&&name==Program.InstanceName(folder.ToUpperInvariant())&&name==Program.InstanceName(Path.Combine(folder,"..",Path.GetFileName(folder))),"equivalent library paths share one instance lock");
   check(name!=Program.InstanceName(folder+"2"),"different libraries retain independent locks for handoff");
   check(!Directory.Exists(Path.Combine(folder,".mori")),"resolving files never creates a library");
   check(!Program.ShouldRememberLibrary(new string[0])&&!Program.ShouldRememberLibrary(new[]{"--startup"}),"ordinary launch never writes back a stale library selection");
   check(Program.ShouldRememberLibrary(new[]{"--library",folder}),"explicit library launch still persists its selected location");
   check(!Program.ShouldRememberLibrary(new[]{"--ui-test",folder})&&!Program.ShouldRememberLibrary(new[]{"--library",folder,"--handoff","event"}),"preview and uncommitted handoff cannot change the default library");
   SettingsConcurrency(Path.Combine(root,"startup-settings.json"),root,check);
   StartupLibrarySafety(Path.Combine(root,"startup-library-safety"),check);
   ExternalFileOpenTests.Run(Path.Combine(root,"external-open"),check);
  }
  static void StartupLibrarySafety(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string path=Path.Combine(root,"settings.json"),library=Path.Combine(root,"remembered-library");
   var firstRun=Settings.LoadFrom(path);firstRun.LibraryPath=library;
   Program.ValidateStartupLibrary(new string[0],firstRun);Program.ValidateStartupLibrary(new[]{"--startup"},firstRun);
   check(!firstRun.LoadedFromFile&&!File.Exists(path)&&!Directory.Exists(library),"first-run startup remains allowed without creating settings or library files during validation");
   new Settings{LibraryPath=library}.SaveTo(path);var configured=Settings.LoadFrom(path);byte[] settingsBefore=File.ReadAllBytes(path);
   check(configured.LoadedFromFile,"persisted settings retain their file origin for startup validation");
   foreach(var args in new[]{new string[0],new[]{"--startup"}}){
    bool rejected=false;try{Program.ValidateStartupLibrary(args,configured);}catch(DirectoryNotFoundException ex){rejected=ex.Message.Contains(library)&&ex.Message.Contains("操作已停止")&&ex.Message.Contains("重新指定");}
    check(rejected&&!Directory.Exists(library)&&File.ReadAllBytes(path).SequenceEqual(settingsBefore),"missing remembered library is rejected without creating an empty replacement or changing settings: "+string.Join(" ",args));
   }
   foreach(var args in new[]{new[]{"--library",library},new[]{"--library",library,"--handoff","event"},new[]{"--ui-test",library},new[]{"--ui-test",library,"--handoff","event"}}){
    Program.ValidateStartupLibrary(args,configured);
    check(!Directory.Exists(library)&&File.ReadAllBytes(path).SequenceEqual(settingsBefore),"explicit creation, migration and preview bypass remembered-library validation: "+string.Join(" ",args));
   }
   foreach(string action in new[]{"folders","save"}){
    bool rejected=false;try{NativeBridge.HandleAtCurrentLibrary(new Dictionary<string,object>{{"action",action}},null,()=>Settings.LoadFrom(path));}catch(DirectoryNotFoundException){rejected=true;}
    check(rejected&&!Directory.Exists(library)&&File.ReadAllBytes(path).SequenceEqual(settingsBefore),"native current-library request cannot recreate a missing configured directory: "+action);
   }
   Directory.CreateDirectory(library);string original=Path.Combine(library,"original-image.fixture");File.WriteAllText(original,"preserve original bytes");byte[] originalBefore=File.ReadAllBytes(original);
   foreach(var args in new[]{new string[0],new[]{"--startup"}}){
    bool rejected=false;try{Program.ValidateStartupLibrary(args,configured);}catch(FileNotFoundException ex){rejected=ex.FileName==Path.Combine(library,".mori","library.db")&&ex.Message.Contains("恢复此索引");}
    check(rejected&&!Directory.Exists(Path.Combine(library,".mori"))&&File.ReadAllBytes(original).SequenceEqual(originalBefore)&&File.ReadAllBytes(path).SequenceEqual(settingsBefore),"missing index preserves original files and settings without recreating database: "+string.Join(" ",args));
   }
   foreach(string action in new[]{"folders","save"}){
    bool rejected=false;try{NativeBridge.HandleAtCurrentLibrary(new Dictionary<string,object>{{"action",action}},null,()=>Settings.LoadFrom(path));}catch(FileNotFoundException){rejected=true;}
    check(rejected&&!Directory.Exists(Path.Combine(library,".mori"))&&File.ReadAllBytes(original).SequenceEqual(originalBefore)&&File.ReadAllBytes(path).SequenceEqual(settingsBefore),"native current-library request cannot recreate a missing index or modify original files: "+action);
   }
   string indexFolder=Path.Combine(library,".mori");Directory.CreateDirectory(indexFolder);string database=Path.Combine(indexFolder,"library.db");File.WriteAllText(database,"existence-only index fixture");byte[] databaseBefore=File.ReadAllBytes(database);
   Program.ValidateStartupLibrary(new string[0],configured);Program.ValidateStartupLibrary(new[]{"--startup"},configured);
   check(File.ReadAllBytes(database).SequenceEqual(databaseBefore)&&File.ReadAllBytes(original).SequenceEqual(originalBefore)&&File.ReadAllBytes(path).SequenceEqual(settingsBefore),"existing remembered library passes startup validation without opening or changing its index");
   string explicitRoot=Path.Combine(root,"explicit-native-fixture");bool handled=false;
   try{NativeBridge.HandleAtCurrentLibrary(new Dictionary<string,object>{{"action","fixture-unknown"}},explicitRoot,()=>{throw new Exception("Explicit fixture must not load real settings");});}catch(InvalidDataException ex){handled=ex.Message=="未知请求";}
   check(handled&&File.Exists(Path.Combine(explicitRoot,".mori","library.db")),"explicit native test root can still create an isolated fixture without loading current settings");
   string firstNativeRoot=Path.Combine(root,"first-native-fixture");var firstNative=new Settings{LibraryPath=firstNativeRoot};handled=false;
   try{NativeBridge.HandleAtCurrentLibrary(new Dictionary<string,object>{{"action","fixture-unknown"}},null,()=>firstNative);}catch(InvalidDataException ex){handled=ex.Message=="未知请求";}
   check(handled&&File.Exists(Path.Combine(firstNativeRoot,".mori","library.db")),"native first-run defaults remain allowed to create their initial library");
  }
  static void SettingsConcurrency(string path,string root,Action<bool,string> check){
   string original=Path.Combine(root,"original-library"),chosen=Path.Combine(root,"chosen-library");
   new Settings{LibraryPath=original,Theme="dark",ThumbnailSize=210}.SaveTo(path);
   var oldEditor=Settings.LoadFrom(path);var otherEditor=Settings.LoadFrom(path);
   Settings.RememberLibraryAt(path,chosen);otherEditor.Theme="light";otherEditor.SaveTo(path);
   oldEditor.ThumbnailSize=264;oldEditor.SaveTo(path);var merged=Settings.LoadFrom(path);
   check(merged.LibraryPath==chosen&&merged.Theme=="light"&&merged.ThumbnailSize==264,"stale preference editors preserve the new library and each other's changed fields");
   check(oldEditor.LibraryPath==chosen&&oldEditor.Theme=="light","saved editor refreshes its baseline to the merged settings");
   var unchanged=Settings.LoadFrom(path);Settings.RememberLibraryAt(path,original);unchanged.SaveTo(path);
   check(Settings.LoadFrom(path).LibraryPath==original,"unchanged settings save cannot undo explicit library selection");
   var firstRun=Settings.LoadFrom(path+".first-run");new Settings{LibraryPath=chosen,Theme="light"}.SaveTo(path+".first-run");firstRun.ThumbnailSize=250;firstRun.SaveTo(path+".first-run");
   check(Settings.LoadFrom(path+".first-run").LibraryPath==chosen,"editor loaded before settings existed preserves a concurrently selected library");
   var intentional=Settings.LoadFrom(path);intentional.LibraryPath=chosen;intentional.SaveTo(path);
   check(Settings.LoadFrom(path).LibraryPath==chosen,"intentional changed library selection remains supported");
   intentional.SaveTo(path+".copy");check(Settings.LoadFrom(path+".copy").LibraryPath==chosen,"saving to another settings file keeps the selected values");
   var parallelA=Settings.LoadFrom(path);var parallelB=Settings.LoadFrom(path);parallelA.ThumbnailSize=280;parallelB.ShowAssetNames=false;
   Exception failureA=null,failureB=null;
   using(var start=new ManualResetEvent(false)){
    var writerA=new Thread(()=>{try{start.WaitOne();parallelA.SaveTo(path);}catch(Exception ex){failureA=ex;}});
    var writerB=new Thread(()=>{try{start.WaitOne();parallelB.SaveTo(path);}catch(Exception ex){failureB=ex;}});
    writerA.Start();writerB.Start();start.Set();writerA.Join();writerB.Join();
   }
   merged=Settings.LoadFrom(path);check(failureA==null&&failureB==null&&merged.ThumbnailSize==280&&!merged.ShowAssetNames&&merged.LibraryPath==chosen,"simultaneous settings writers preserve independent changes and current library");
   byte[] beforeSwitch=File.ReadAllBytes(path);Exception leaseFailure=null;bool refused=false;long refusalMilliseconds;
   using(var held=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){
    var writer=new Thread(()=>{try{using(var lease=LibraryTransfer.TryAcquireWrite(chosen)){if(lease==null)throw new IOException("fixture lease unavailable");held.Set();release.WaitOne(10000);}}catch(Exception ex){leaseFailure=ex;}finally{held.Set();}});
    writer.Start();
    try{
     if(!held.WaitOne(5000))throw new Exception("fixture writer did not acquire its lease");
     var clock=System.Diagnostics.Stopwatch.StartNew();try{Settings.RememberLibraryAt(path,original);}catch(IOException ex){refused=ex.Message.Contains("暂未切换");}refusalMilliseconds=clock.ElapsedMilliseconds;
    }finally{release.Set();writer.Join();}
   }
   check(leaseFailure==null&&refused&&refusalMilliseconds<1000&&File.ReadAllBytes(path).SequenceEqual(beforeSwitch),"active old-root write immediately refuses routing switch and preserves exact settings bytes");
   Settings.RememberLibraryAt(path,original);check(Settings.LoadFrom(path).LibraryPath==original,"library switch succeeds after old-root write lease is released");
   using(var lease=LibraryTransfer.TryAcquireWrite(original)){Settings.RememberLibraryAt(path,chosen);}
   check(Settings.LoadFrom(path).LibraryPath==chosen,"same-thread migration can recursively commit its verified library selection");
   var stale=Settings.LoadFrom(path);string corrupt="{\"LibraryPath\":\"do not overwrite";File.WriteAllText(path,corrupt);stale.ThumbnailSize=270;bool rejected=false;
   try{stale.SaveTo(path);}catch(InvalidDataException){rejected=true;}
   check(rejected&&File.ReadAllText(path)==corrupt,"stale editor does not overwrite a newly damaged settings file");
   foreach(string invalid in new[]{"{}","{\"Theme\":\"light\"}","{\"LibraryPath\":\"\"}","{\"LibraryPath\":\"  \"}","{\"LibraryPath\":null}","{\"LibraryPath\":123}"}){
    File.WriteAllText(path,invalid);rejected=false;try{Settings.LoadFrom(path);}catch(InvalidDataException){rejected=true;}
    check(rejected&&File.ReadAllText(path)==invalid,"existing settings without a usable library path are rejected, not reset: "+invalid);
   }
   string absent=path+".absent";var defaults=Settings.LoadFrom(absent);check(!string.IsNullOrWhiteSpace(defaults.LibraryPath)&&!File.Exists(absent),"missing settings file still permits first-run defaults without saving");
   check(!Directory.Exists(original)&&!Directory.Exists(chosen),"remembering a location never creates or edits library data");
  }
 }
}
