using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 internal static class MetadataRefreshCacheTests {
  static readonly BindingFlags Private=BindingFlags.Instance|BindingFlags.NonPublic;
  static T Field<T>(MainWindow main,string name){return (T)typeof(MainWindow).GetField(name,Private).GetValue(main);}
  static void Call(MainWindow main,string method,params object[] arguments){typeof(MainWindow).GetMethod(method,Private).Invoke(main,arguments);}
  static void Insert(Database db,string id,string name,string ext){db.Exec("INSERT INTO assets(id,hash,name,path,ext,bytes,added) VALUES("+Database.Q(id)+","+Database.Q("hash-"+id)+","+Database.Q(name)+","+Database.Q(id+ext)+","+Database.Q(ext)+",128,'2026-10-13T00:00:00Z')");}
  internal static void Run(string root,Action<bool,string> check){
   string library=Path.Combine(root,"metadata-refresh-cache");bool classic=Entry.ClassicEnabled;var previous=ThumbnailImage.CurrentLibrary;
   Entry.ClassicEnabled=true;
   try{using(var lib=new Library(library)){
    Insert(lib.Db,"alpha","Alpha image",".png");Insert(lib.Db,"beta","Beta video",".mp4");Insert(lib.Db,"gamma","Gamma image",".png");
    var main=new MainWindow(lib){PreviewMode=true};
    try{
     var initial=Field<List<Asset>>(main,"all");var search=(TextBox)main.Window.FindName("Search");var sort=(ComboBox)main.Window.FindName("Sort");var media=(ComboBox)main.Window.FindName("MediaFilter");
     search.Text="Alpha";main.RefreshView();
     check(object.ReferenceEquals(initial,Field<List<Asset>>(main,"all"))&&Field<List<Asset>>(main,"visible").Single().Name=="Alpha image","search reuses loaded metadata and applies query");
     sort.SelectedIndex=2;
     check(object.ReferenceEquals(initial,Field<List<Asset>>(main,"all")),"sort change reuses loaded metadata");
     search.Clear();media.SelectedIndex=2;main.RefreshView();
     check(object.ReferenceEquals(initial,Field<List<Asset>>(main,"all"))&&Field<List<Asset>>(main,"visible").Single().Name=="Beta video","media filter reuses metadata with correct results");
     media.SelectedIndex=0;main.RefreshView();
     Call(main,"Refresh",false);var refreshed=Field<List<Asset>>(main,"all");
     check(!object.ReferenceEquals(initial,refreshed)&&refreshed.Count==3,"explicit refresh still reloads database snapshot");
     var selected=Field<HashSet<string>>(main,"selection");selected.Add("alpha");Call(main,"UpdateInspector");var notes=(TextBox)main.Window.FindName("Notes");notes.Text="Keep this unsaved draft";
     using(var writer=new Database(Path.Combine(library,".mori","library.db")))Insert(writer,"delta","Delta externally added",".png");
     Call(main,"Refresh",false);
     check(object.ReferenceEquals(refreshed,Field<List<Asset>>(main,"all"))&&Field<bool>(main,"metadataReloadRequired")&&notes.Text=="Keep this unsaved draft","external refresh retains pending reload and draft while postponed");
     notes.Text="";search.Text="Delta";main.RefreshView();
     check(!object.ReferenceEquals(refreshed,Field<List<Asset>>(main,"all"))&&Field<List<Asset>>(main,"all").Count==4&&Field<List<Asset>>(main,"visible").Single().Id=="delta","later search consumes postponed external reload after draft is reverted");
     check(!Field<bool>(main,"metadataReloadRequired"),"successful metadata reload clears invalidation");
     var resumed=Field<List<Asset>>(main,"all");main.RefreshView();
     check(object.ReferenceEquals(resumed,Field<List<Asset>>(main,"all")),"subsequent unchanged search returns to reuse path");
     Insert(lib.Db,"epsilon","Epsilon local import",".png");Call(main,"Refresh",false);
     check(Field<List<Asset>>(main,"all").Count==5,"local mutation refresh still discovers new records");
    }finally{
     foreach(string field in new[]{"externalTimer","searchTimer","layoutTimer","sizeSaveTimer","sidebarSaveTimer"})Field<DispatcherTimer>(main,field).Stop();main.Window.Close();
    }
   }}finally{Entry.ClassicEnabled=classic;ThumbnailImage.CurrentLibrary=previous;}
  }
 }
}
