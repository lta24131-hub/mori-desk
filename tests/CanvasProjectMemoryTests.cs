using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Mori;

namespace MoriDesk {
 // Project memory is a small preference, never a manifest write. All files and
 // windows here are isolated fixtures; no window is shown and no profile is used.
 internal static class CanvasProjectMemoryTests {
  const string MemoryFile="last-opened.txt";
  [DllImport("kernel32.dll",EntryPoint="CreateSymbolicLinkW",CharSet=CharSet.Unicode,SetLastError=true)]
  [return:MarshalAs(UnmanagedType.I1)]
  static extern bool CreateSymbolicLink(string link,string target,uint flags);
  static string Manifest(FreeCanvasStore store,string id){return Path.Combine(store.Root,id,"project.json");}
  static bool NoPending(FreeCanvasStore store){return Directory.GetFiles(store.Root,"opened-*.tmp").Length==0;}
  internal static void Run(string root,Action<bool,string> check){
   Directory.CreateDirectory(root);string data=Path.Combine(root,"profile");var store=new FreeCanvasStore(data);string memory=Path.Combine(store.Root,MemoryFile);
   check(store.LastOpenedId==null&&store.List().Count==0&&!File.Exists(memory),"missing canvas memory does not fabricate a project or preference");
   var a=store.Create("画布 A");var b=store.Create("画布 B");string aFile=Manifest(store,a.Id),bFile=Manifest(store,b.Id);byte[] aBytes=File.ReadAllBytes(aFile),bBytes=File.ReadAllBytes(bFile);DateTime aTime=File.GetLastWriteTimeUtc(aFile),bTime=File.GetLastWriteTimeUtc(bFile);string aUpdated=a.Updated,bUpdated=b.Updated;
   store.RememberOpened(a.Id);store.RememberOpened(b.Id);store.RememberOpened(a.Id);
   check(store.LastOpenedId==a.Id&&new FreeCanvasStore(data).LastOpenedId==a.Id,"A then B then A viewing sequence persists A independently of window lifetime");
   check(File.ReadAllText(memory)==a.Id&&new FileInfo(memory).Length==32&&NoPending(store)&&store.List().Count==2,"canvas memory is one bounded ID file, atomically committed without extra projects or pending files");
   check(File.ReadAllBytes(aFile).SequenceEqual(aBytes)&&File.ReadAllBytes(bFile).SequenceEqual(bBytes)&&File.GetLastWriteTimeUtc(aFile)==aTime&&File.GetLastWriteTimeUtc(bFile)==bTime&&store.Load(a.Id).Updated==aUpdated&&store.Load(b.Id).Updated==bUpdated,"viewing older boards leaves both manifests, timestamps and Updated unchanged");
   foreach(string invalid in new[]{null,""," ","../outside",new string('A',32),new string('a',31),new string('a',33),Guid.NewGuid().ToString("N")})store.RememberOpened(invalid);
   check(store.LastOpenedId==a.Id&&File.ReadAllText(memory)==a.Id&&NoPending(store),"invalid, traversal and nonexistent project IDs cannot replace a valid canvas preference");
   File.WriteAllText(bFile,"{ damaged");store.RememberOpened(b.Id);
   check(store.LastOpenedId==a.Id&&File.ReadAllText(memory)==a.Id&&File.ReadAllText(bFile)=="{ damaged","an unreadable project cannot replace memory or be silently repaired by opening");File.WriteAllBytes(bFile,bBytes);
   foreach(string bad in new[]{"",a.Id+"\n",new string('x',32),"../outside",Guid.NewGuid().ToString("N"),new string('a',1024*1024)}){
    File.WriteAllText(memory,bad);byte[] original=File.ReadAllBytes(memory);check(store.LastOpenedId==null&&File.ReadAllBytes(memory).SequenceEqual(original),"missing-target, damaged or oversized memory safely falls back without rewriting it");
   }
   store.RememberOpened(a.Id);File.WriteAllText(aFile,"{ incomplete");
   check(store.LastOpenedId==null&&File.ReadAllText(aFile)=="{ incomplete","memory of a damaged board safely falls back and preserves the damaged original");File.WriteAllBytes(aFile,aBytes);
   var stale=store.Load(a.Id);store.RememberOpened(b.Id);store.RememberOpened(a.Id);stale.Name="画布 A 保存";store.Save(stale);
   check(store.Load(a.Id).Name=="画布 A 保存"&&store.LastOpenedId==a.Id,"remembering a view does not invalidate a loaded document's save hash");
   using(var locked=new FileStream(memory,FileMode.Open,FileAccess.Read,FileShare.None)){
    check(store.LastOpenedId==null,"temporarily inaccessible canvas memory does not prevent fallback");store.RememberOpened(b.Id);var editable=store.Load(b.Id);editable.Name="画布 B 保存";store.Save(editable);
    check(store.Load(b.Id).Name=="画布 B 保存"&&NoPending(store),"inaccessible preference updates leave normal project saves working and clean their pending file");
   }
   check(store.LastOpenedId==a.Id,"a failed preference update preserves the previously committed viewed project");
   File.Delete(memory);Directory.CreateDirectory(memory);store.RememberOpened(b.Id);
   check(store.LastOpenedId==null&&Directory.Exists(memory)&&NoPending(store)&&store.Load(b.Id).Name=="画布 B 保存","a directory at the preference path is not overwritten and does not block reading a project");Directory.Delete(memory);
   string outside=Path.Combine(root,"outside-id.txt");File.WriteAllText(outside,b.Id);
   bool linked=CreateSymbolicLink(memory,outside,2);if(linked){
    try{check((File.GetAttributes(memory)&FileAttributes.ReparsePoint)!=0&&store.LastOpenedId==null,"canvas memory rejects a symbolic-link preference path");store.RememberOpened(a.Id);check(File.ReadAllText(outside)==b.Id&&(File.GetAttributes(memory)&FileAttributes.ReparsePoint)!=0&&NoPending(store),"rejecting linked memory never overwrites its outside target or removes the link");}
    finally{File.Delete(memory);}
   }
   store.RememberOpened(a.Id);string removed=store.Trash(a.Id);
   check(store.LastOpenedId==null&&Directory.Exists(removed)&&File.ReadAllText(memory)==a.Id,"deleting the remembered board returns no usable ID without erasing its recoverable history");
   store.RememberOpened(b.Id);check(store.RestoreLastDeleted()==a.Id&&store.LastOpenedId==b.Id,"restoring a deleted board preserves the current viewed-board preference until it is opened");store.RememberOpened(a.Id);
   check(store.LastOpenedId==a.Id&&store.Load(a.Id).Name=="画布 A 保存"&&store.List().Count==2,"a restored board can be remembered again with its existing identity and content");
   Ui(Path.Combine(root,"ui-profile"),check);
  }
  static void Ui(string data,Action<bool,string> check){
   var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;var store=new FreeCanvasStore(data);var a=store.Create("先前画布 A");var b=store.Create("新的画布 B");store.RememberOpened(b.Id);byte[] aBytes=File.ReadAllBytes(Manifest(store,a.Id)),bBytes=File.ReadAllBytes(Manifest(store,b.Id));FreeCanvasWindow window=null,reopened=null;
   try{
    window=new FreeCanvasWindow(data,new Settings());check(window.DocumentForTests.Id==b.Id&&!window.IsVisible,"opening a canvas uses the explicit viewed-project memory without presenting a fixture window");
    var selector=(ComboBox)typeof(FreeCanvasWindow).GetField("projects",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);selector.SelectedItem=((System.Collections.Generic.IEnumerable<FreeCanvasProjectInfo>)selector.ItemsSource).Single(p=>p.Id==a.Id);
    check(window.DocumentForTests.Id==a.Id&&store.LastOpenedId==a.Id,"actual project-selector change remembers A even when it is only viewed, not edited");window.Close();window=null;
    reopened=new FreeCanvasWindow(data,new Settings());check(reopened.DocumentForTests.Id==a.Id&&!reopened.IsVisible&&store.List().Count==2,"closing after B-to-A viewing and reopening returns to A rather than creating or choosing another board");
    check(File.ReadAllBytes(Manifest(store,a.Id)).SequenceEqual(aBytes)&&File.ReadAllBytes(Manifest(store,b.Id)).SequenceEqual(bBytes),"opening, switching and closing unchanged boards never mark either project edited");
    reopened.Close();reopened=null;File.WriteAllText(Path.Combine(store.Root,MemoryFile),"broken preference");string fallback=store.List().First(p=>string.IsNullOrEmpty(p.Error)).Id;
    reopened=new FreeCanvasWindow(data,new Settings());check(reopened.DocumentForTests.Id==fallback&&store.LastOpenedId==fallback&&store.List().Count==2,"an invalid remembered ID reopens an existing readable board rather than fabricating another one");reopened.Close();reopened=null;
    store.RememberOpened(a.Id);File.WriteAllText(Manifest(store,a.Id),"{ damaged A");reopened=new FreeCanvasWindow(data,new Settings());check(reopened.DocumentForTests.Id==b.Id&&store.LastOpenedId==b.Id&&File.ReadAllText(Manifest(store,a.Id))=="{ damaged A","an unreadable remembered board falls back to B and preserves A for recovery");reopened.Close();reopened=null;File.WriteAllBytes(Manifest(store,a.Id),aBytes);
    store.RememberOpened(a.Id);store.Trash(a.Id);reopened=new FreeCanvasWindow(data,new Settings());check(reopened.DocumentForTests.Id==b.Id&&store.LastOpenedId==b.Id&&store.List().Count==1,"a deleted remembered board falls back to the remaining board without restoring or recreating it");reopened.Close();reopened=null;check(store.RestoreLastDeleted()==a.Id,"the remembered-project fallback does not interfere with later project recovery");
   }finally{if(window!=null)window.Close();if(reopened!=null)reopened.Close();app.ShutdownMode=shutdown;}
  }
 }
}
