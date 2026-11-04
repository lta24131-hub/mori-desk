using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Controls;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 internal static class SelectionLookupTests {
  static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Field<T>(MainWindow main,string name){return (T)typeof(MainWindow).GetField(name,Private).GetValue(main);}
  static object Call(MainWindow main,string method,params object[] args){return typeof(MainWindow).GetMethod(method,Private).Invoke(main,args);}
  static List<Asset> Selected(MainWindow main){return (List<Asset>)Call(main,"Selected");}
  internal static void Run(string root,Action<bool,string> check){
   var main=(MainWindow)FormatterServices.GetUninitializedObject(typeof(MainWindow));
   var all=Enumerable.Range(0,10000).Select(i=>new Asset{Id="id-"+i,Name="Image "+i}).ToList();
   var ids=new HashSet<string>();typeof(MainWindow).GetField("all",Private).SetValue(main,all);typeof(MainWindow).GetField("selection",Private).SetValue(main,ids);
   check(Selected(main).Count==0,"empty selection has no stale result");
   ids.Add("id-9999");check(object.ReferenceEquals(Selected(main).Single(),all[9999]),"sparse selection resolves the current asset");
   ids.Add("id-9");ids.Add("id-100");ids.Add("missing");
   check(Selected(main).Select(a=>a.Id).SequenceEqual(new[]{"id-9","id-100","id-9999"}),"Ctrl selection preserves library order and ignores deleted IDs");
   var detached=Selected(main);detached.Clear();check(Selected(main).Count==3,"callers may modify their result without changing selection");
   all[9].Name="Renamed";check(Selected(main)[0].Name=="Renamed","metadata edits remain live");
   ids.Clear();for(int i=9999;i>=0;i--)ids.Add("id-"+i);
   check(Selected(main).SequenceEqual(all),"select all retains library ordering");
   ids.Clear();ids.Add("id-9");
   var replacement=all.AsEnumerable().Reverse().Select(a=>new Asset{Id=a.Id,Name="new snapshot"}).ToList();
   typeof(MainWindow).GetField("all",Private).SetValue(main,replacement);
   check(object.ReferenceEquals(Selected(main).Single(),replacement.Single(a=>a.Id=="id-9")),"same-size refreshed snapshot never resolves old objects or positions");
   ids.Clear();check(Selected(main).Count==0,"clearing after refresh yields no selection");
   Notifications(root,check);
  }
  static void Notifications(string root,Action<bool,string> check){
   bool classic=Entry.ClassicEnabled;var previous=ThumbnailImage.CurrentLibrary;Entry.ClassicEnabled=true;
   try{using(var lib=new Library(Path.Combine(root,"selection-notifications"))){
    for(int i=0;i<100;i++)lib.Db.Exec("INSERT INTO assets(id,hash,name,path,ext,bytes,added) VALUES('asset-"+i+"','hash-"+i+"','Image "+i+"','image-"+i+".png','.png',128,'2026-11-04T00:00:00Z')");
    var main=new MainWindow(lib){PreviewMode=true};
    try{
     var assets=Field<List<Asset>>(main,"all");var ids=Field<HashSet<string>>(main,"selection");int notifications=0;
     foreach(var a in assets)a.PropertyChanged+=(s,e)=>{if(e.PropertyName=="Selected")notifications++;};
     ids.Add(assets[0].Id);Call(main,"SyncSelection");
     check(notifications==1&&assets[0].Selected,"select one refreshes exactly its card");
     check(((TextBox)main.Window.FindName("Name")).Text==assets[0].Name,"selected card still updates inspector");
     notifications=0;Call(main,"SyncSelection");check(notifications==0,"unchanged selection sends no redundant card notifications");
     ids.Clear();ids.Add(assets[1].Id);Call(main,"SyncSelection");
     check(notifications==2&&!assets[0].Selected&&assets[1].Selected,"switching selection updates previous and next cards only");
     notifications=0;ids.Clear();Call(main,"SyncSelection");
     check(notifications==1&&assets.All(a=>!a.Selected),"clear selection removes its highlight");
     notifications=0;Call(main,"SyncSelection");check(notifications==0,"repeated clear does not repaint gallery");
     main.SelectAllVisible();check(assets.All(a=>a.Selected)&&Selected(main).Count==100,"all cards highlight on select all");
     ids.Clear();Call(main,"SyncSelection");main.SetBatchSelecting(true);check(assets.All(a=>a.Selecting),"batch affordance still updates every card");
     main.SetBatchSelecting(false);check(assets.All(a=>!a.Selecting),"batch affordance clears when leaving multi-select");
    }finally{foreach(string field in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})Field<DispatcherTimer>(main,field).Stop();main.Window.Close();}
   }}finally{Entry.ClassicEnabled=classic;ThumbnailImage.CurrentLibrary=previous;}
  }
 }
}
