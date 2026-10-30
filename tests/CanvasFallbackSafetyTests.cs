using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Missing-editor simulation is scoped to the hidden fixture window. Never
 // remove or rename a packaged component, or touch the installed user profile.
 internal static class CanvasFallbackSafetyTests {
  const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Hidden).GetValue(main);}
  static Dictionary<string,object> Map(params object[] values){var result=new Dictionary<string,object>();for(int i=0;i<values.Length;i+=2)result.Add((string)values[i],values[i+1]);return result;}
  static Dictionary<string,byte[]> Files(string root){return Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(p=>p,File.ReadAllBytes,StringComparer.OrdinalIgnoreCase);}
  static bool SameFiles(string root,Dictionary<string,byte[]> files){var now=Directory.GetFiles(root,"*",SearchOption.AllDirectories);return now.Length==files.Count&&files.All(p=>File.Exists(p.Key)&&File.ReadAllBytes(p.Key).SequenceEqual(p.Value));}
  static MatureCanvasDocument TextProject(MatureCanvasStore store){
   var document=store.Create("新版项目里的文字");var json=new JavaScriptSerializer();var scene=(Dictionary<string,object>)json.DeserializeObject(document.SceneJson);var snapshot=(Dictionary<string,object>)scene["document"];var records=(Dictionary<string,object>)snapshot["store"];
   records.Add("shape:fixture-text",Map("id","shape:fixture-text","typeName","shape","type","text","x",10,"y",20,"rotation",0,"index","a1","parentId","page:page","isLocked",false,"opacity",1,"meta",Map(),"props",Map("color","black","size","m","font","sans","text","不能降级后变成空白","textAlign","start","w",250,"autoSize",true,"scale",1)));
   document.SceneJson=json.Serialize(scene);store.Save(document);return document;
  }
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string profile=Path.Combine(root,"profile");var legacy=new FreeCanvasStore(profile);var mature=new MatureCanvasStore(profile);
   check(MainWindow.ClassicCanvasFallbackAllowed(profile),"an empty profile remains eligible for the classic editor fallback");
   var old=legacy.Create("可读取的旧版项目");check(MainWindow.ClassicCanvasFallbackAllowed(profile),"legacy-only profiles retain their existing classic fallback");
   var board=TextProject(mature);check(legacy.Load(board.Id).Items.Count==0&&mature.Load(board.Id).TextCount==1,"fixture represents a mature scene whose compatible legacy index is empty");
   var before=Files(legacy.Root);check(!MainWindow.ClassicCanvasFallbackAllowed(profile)&&SameFiles(legacy.Root,before),"a mature sidecar blocks classic fallback without rewriting its scene or recovery index");
   var rows=CanvasProjectsPage.QueryProjects(legacy,mature);check(rows.Single(p=>p.Id==board.Id).Text==1&&rows.Single(p=>p.Id==board.Id).Name==board.Name,"project metadata still comes from mature contents instead of an empty legacy index");
   string manifest=Path.Combine(legacy.Root,board.Id,MatureCanvasStore.ManifestName),backup=manifest+".bak";byte[] intact=File.ReadAllBytes(manifest);
   File.WriteAllText(manifest,"{ synthetic corrupt scene");var damaged=Files(legacy.Root);check(!MainWindow.ClassicCanvasFallbackAllowed(profile)&&SameFiles(legacy.Root,damaged),"an unreadable mature sidecar cannot silently fall back to its empty recovery index");File.WriteAllBytes(manifest,intact);
   string retained=manifest+".fixture-preserved";File.Move(manifest,retained);try{var backupOnly=Files(legacy.Root);check(File.Exists(backup)&&!MainWindow.ClassicCanvasFallbackAllowed(profile)&&SameFiles(legacy.Root,backupOnly),"a recoverable mature backup without a primary manifest also blocks destructive-looking classic fallback");}finally{File.Move(retained,manifest);}

   var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
   string oldData=Environment.GetEnvironmentVariable("MORI_DESK_DATA"),oldTheme=Theme.Current;bool oldClassic=Entry.ClassicEnabled,oldBoard=Entry.BoardEnabled,oldInteractive=Entry.InteractiveShell;MainWindow main=null;
   Environment.SetEnvironmentVariable("MORI_DESK_DATA",profile);Entry.ClassicEnabled=true;Entry.BoardEnabled=false;Entry.InteractiveShell=false;
   try{
    string libraryRoot=Path.Combine(root,"library"),source=Path.Combine(root,"source.png");using(var bitmap=new System.Drawing.Bitmap(12,8))bitmap.Save(source,System.Drawing.Imaging.ImageFormat.Png);byte[] original=File.ReadAllBytes(source);DateTime timestamp=File.GetLastWriteTimeUtc(source);
    new Settings{LibraryPath=libraryRoot,Theme="light"}.SaveTo(Path.Combine(Entry.DataRoot,"settings.json"));
    using(var library=new Library(libraryRoot)){
     var image=library.Import(source,"","测试图片","","","").Item;main=new MainWindow(library){PreviewMode=true,FreeCanvasPresentationSuppressedForTests=true};main.SetSection("pictures");var protectedFiles=Files(legacy.Root);var protectedDirectories=Directory.GetDirectories(legacy.Root).OrderBy(p=>p,StringComparer.Ordinal).ToArray();
     check(!main.OpenFreeCanvasToProject(board.Id,null)&&Field(main,"freeCanvas")==null&&Field(main,"matureCanvas")==null,"opening a mature project with the editor unavailable cannot create a blank classic window");
     check(((TextBlock)main.Window.FindName("Status")).Text.Contains("不会改用旧版"),"blocked fallback explains the missing component and preserved project instead of claiming a blank board opened");
     check(!main.OpenFreeCanvasToProject(null,null)&&!main.OpenFreeCanvasToProject(old.Id,null),"implicit reopen and explicit legacy open cannot expose mature recovery indices through a classic project selector");
     bool showingProjects=main.ShowCanvasProjects();CanvasPageTestSupport.Wait(main.CanvasProjects);check(showingProjects&&main.CanvasProjects.ProjectsForTests.Single(p=>p.Id==board.Id).Text==1,"the real project page still shows committed mature metadata when editor startup is unavailable");main.CanvasProjects.CreateProject();
     check(SameFiles(legacy.Root,protectedFiles)&&Directory.GetDirectories(legacy.Root).OrderBy(p=>p,StringComparer.Ordinal).SequenceEqual(protectedDirectories),"New in the unavailable-engine project page cannot create or trash a ghost project");
     main.SetSection("pictures");var add=main.AddSelectionToCanvasAsync(new[]{image.Id},new CanvasDestination{NewName="不应创建的空项目"});check(add.IsCompleted,"unavailable-engine new-destination rejection does not begin a background import");add.GetAwaiter().GetResult();
     check(SameFiles(legacy.Root,protectedFiles)&&Directory.GetDirectories(legacy.Root).OrderBy(p=>p,StringComparer.Ordinal).SequenceEqual(protectedDirectories)&&Field(main,"freeCanvas")==null&&Field(main,"matureCanvas")==null,"selected-picture New also rejects before creating a destination, window or resource");
     check(library.All().Count==1&&File.ReadAllBytes(source).SequenceEqual(original)&&File.GetLastWriteTimeUtc(source)==timestamp,"fallback rejection preserves library assets and original source bytes and timestamps");main.Window.Close();main=null;
    }
   }finally{if(main!=null)main.Window.Close();ThumbnailImage.CurrentLibrary=null;Environment.SetEnvironmentVariable("MORI_DESK_DATA",oldData);Entry.ClassicEnabled=oldClassic;Entry.BoardEnabled=oldBoard;Entry.InteractiveShell=oldInteractive;Theme.Set(oldTheme);SynchronizationContext.SetSynchronizationContext(context);app.ShutdownMode=shutdown;}
  }
 }
}
