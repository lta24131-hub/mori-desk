using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Collections.Generic;
using System.Web.Script.Serialization;
namespace MoriDesk {
 public static class IterationTests {
  static IEnumerable<string> HugeDirectory(Action tick){for(int i=0;i<20000;i++){tick();yield return "C:\\samples\\"+i+".png";}}
  static void ReferenceUi(string root,Action<bool,string> check){
   string original=Path.Combine(root,"ui-original.step");File.WriteAllText(original,"synthetic original");
   using(var lib=new Mori.Library(Path.Combine(root,"ui-cache-library"))){
    var store=new ReferenceStore(lib.Db);store.Add(new[]{original},"");
    var owner=new System.Windows.Window();var page=new ReferencePage(owner,store,()=>{},s=>{},(p,n)=>{});
    var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
    var list=(System.Windows.Controls.ListView)typeof(ReferencePage).GetField("files",flags).GetValue(page);
    page.Refresh();check(list.Items.Count==1,"local page renders cached rows");list.SelectedIndex=0;
    ((FileReference)list.SelectedItem).State="可打开";check(list.SelectedItems.Count==1,"availability notification preserves UI selection");
    typeof(ReferencePage).GetMethod("Remove",flags).Invoke(page,null);check(list.Items.Count==0,"remove invalidates local page cache immediately");check(File.ReadAllText(original)=="synthetic original","UI remove never alters original file");
    typeof(ReferencePage).GetMethod("Undo",flags).Invoke(page,null);check(list.Items.Count==1,"undo refreshes local page cache");
    var row=store.All()[0];string folder=store.AddFolder("Moved");store.Move(new[]{row.Id},folder);page.Refresh();check(((FileReference)list.Items[0]).Folder==folder,"category edit refreshes cached row");
    var selected=(FileReference)list.Items[0];list.SelectedIndex=0;page.Refresh();check(list.SelectedItems.Count==1&&((FileReference)list.SelectedItem).Id==selected.Id,"manual refresh preserves selected ID");
    page.Visibility=System.Windows.Visibility.Collapsed;owner.Close();
   }
  }
  public static void Run(string root,Action<bool,string> check){
   string leaseDir=Path.Combine(root,"capture-lease");using(var lease=CaptureLease.TryAcquire(leaseDir)){check(lease!=null,"first window owns capture data");using(var second=CaptureLease.TryAcquire(leaseDir))check(second==null,"concurrent libraries cannot write the same pins");}using(var nextLease=CaptureLease.TryAcquire(leaseDir))check(nextLease!=null,"library handoff acquires capture ownership after release");
   string exe=typeof(Entry).Assembly.Location,a=new string('a',32),b=new string('b',32);var json=new JavaScriptSerializer();
   check(BrowserConnection.ExtensionDirectory(exe)==Path.Combine(Path.GetDirectoryName(exe),"browser-extension"),"packaged browser folder is next to executable");
   check(BrowserConnection.ValidateId(" "+a+" ")==a,"extension ID whitespace trimmed");
   foreach(string bad in new[]{"",new string('q',32),new string('A',32),a+"/",a+"\nextra","https://example.com"}){bool rejected=false;try{BrowserConnection.ValidateId(bad);}catch(ArgumentException){rejected=true;}check(rejected,"invalid extension ID rejected");}
   string folder=Path.Combine(root,"browser-test");string path=BrowserConnection.WriteManifest(folder,exe,a);string old=File.ReadAllText(path);BrowserConnection.WriteManifest(folder,exe,b);var data=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));var origins=((System.Collections.IEnumerable)data["allowed_origins"]).Cast<string>().ToArray();
   check(origins.Length==2&&origins.Contains("chrome-extension://"+a+"/")&&origins.Contains("chrome-extension://"+b+"/"),"Chrome and Edge IDs coexist");
   BrowserConnection.WriteManifest(folder,exe,b);check(File.ReadAllText(path)==json.Serialize(data),"repeat connection idempotent");
   string before=File.ReadAllText(path);try{BrowserConnection.WriteManifest(folder,exe,"bad");}catch(ArgumentException){}check(File.ReadAllText(path)==before,"invalid connection preserves previous manifest");
   bool refused=false;try{BrowserConnection.Manifest(exe,a,"{\"name\":\"app.mori.library\"}");}catch(InvalidDataException){refused=true;}check(refused,"never import unrelated Mori host permissions");
   var moved=json.Deserialize<Dictionary<string,object>>(BrowserConnection.Manifest(Path.Combine(root,"new-version","Mori.exe"),a,old));check(Convert.ToString(moved["path"]).Contains("new-version"),"reconnect updates executable location");
   var refs=Enumerable.Range(0,10000).Select(i=>new FileReference{Id=i.ToString(),Name="Model "+i,Path="C:\\not-read\\"+i+".step",Folder=i%2==0?"a":"b",State="可打开"}).ToList();
   var timer=System.Diagnostics.Stopwatch.StartNew();for(int i=0;i<20;i++)ReferencePage.Filter(refs,"*","Model");timer.Stop();
   check(ReferencePage.Filter(refs,"a","").Count==5000,"reference search scoped to category");check(ReferencePage.Filter(refs,"*","MODEL 9999").Single()==refs[9999],"search case insensitive and preserves row identity");check(ReferencePage.Filter(refs,"*","not-read").Count==10000,"search path without probing files");check(refs.All(x=>x.State=="可打开"),"search keeps availability states");check(ReferencePage.Filter(refs,"*","nothing").Count==0,"search no matches");
   int notifications=0;refs[0].PropertyChanged+=(s,e)=>{if(e.PropertyName=="State")notifications++;};refs[0].State="失效";refs[0].State="失效";check(notifications==1,"status updates in place without rebuilding selection");
   File.WriteAllText(Path.Combine(root,"reference-search-benchmark.txt"),"20 filters / 10000 references: "+timer.ElapsedMilliseconds+" ms; no filesystem probes");
   string current="C:\\samples\\current.png";int inspected=0;var images=ImageDirectory.Select(current,HugeDirectory(()=>inspected++),CancellationToken.None);check(inspected==ImageDirectory.Limit,"directory enumeration bounded");check(images.Contains(current),"current image retained when directory limit reached");check(images.Length==ImageDirectory.Limit+1,"image navigation list bounded");
   var token=new CancellationTokenSource();token.Cancel();bool canceled=false;try{ImageDirectory.Select(current,HugeDirectory(()=>{}),token.Token);}catch(OperationCanceledException){canceled=true;}check(canceled,"directory scan cancelable on close or replacement");
   var selected=ImageDirectory.Select(current,new[]{current,current.ToUpperInvariant(),"C:\\samples\\other.GIF","C:\\samples\\notes.txt"},CancellationToken.None);check(selected.Length==2,"directory formats and case-insensitive duplicates");
   ReferenceUi(root,check);
  }
 }
}
