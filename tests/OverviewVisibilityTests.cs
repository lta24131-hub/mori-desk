using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Mori;

namespace MoriDesk {
 public static class OverviewVisibilityTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Flags).GetValue(main);}
  static void Call(MainWindow main,string name){typeof(MainWindow).GetMethod(name,Flags,null,Type.EmptyTypes,null).Invoke(main,null);}
  static List<Asset> Visible(MainWindow main){return (List<Asset>)Field(main,"visible");}
  static void Select(MainWindow main,params string[] ids){var selected=(HashSet<string>)Field(main,"selection");selected.Clear();foreach(var id in ids)selected.Add(id);Call(main,"UpdateInspector");}
  static MenuItem Item(MainWindow main,Asset asset,string label){return main.CreateAssetMenu(asset).Items.OfType<MenuItem>().SingleOrDefault(i=>Convert.ToString(i.Header)==label);}
  static bool Reject(Action action){try{action();return false;}catch{return true;}}
  public static void Run(string root,Action<bool,string> check){
   string path=Path.Combine(root,"visibility-library"),originals=Path.Combine(root,"visibility-originals");Directory.CreateDirectory(originals);
   string image=Path.Combine(originals,"图片.png"),model=Path.Combine(originals,"装配.step"),directory=Path.Combine(originals,"真实文件夹"),pdf=Path.Combine(originals,"说明.pdf");
   File.Copy(Path.Combine(root,"reference-photo.png"),image);File.WriteAllText(model,"ISO-10303-21;visibility-fixture");File.WriteAllText(pdf,"%PDF-1.4 fixture");Directory.CreateDirectory(directory);
   byte[] before=File.ReadAllBytes(model);var modified=File.GetLastWriteTimeUtc(model);string pictureId="",modelId="",referenceImageId="";
   using(var lib=new Library(path)){
    var picture=lib.Import(image,"","图库图片","","","").Item;pictureId=picture.Id;var doc=lib.Import(pdf,"","文档","","","").Item;var web=lib.ImportBookmark("https://example.org/visibility","网址","").Item;
    var normalHistory=new UndoHistory(lib.Db);normalHistory.Record("hide",()=>lib.SetHiddenFromAll(new[]{pictureId},true));check(lib.All().Single(a=>a.Id==pictureId).HiddenFromAll,"managed visibility persisted independently of reference mode");normalHistory.Undo();check(!lib.All().Single(a=>a.Id==pictureId).HiddenFromAll,"standard library undo includes visibility");
    lib.EnableReferenceAssets();modelId=lib.AddReferenceAssets(new[]{model},"").Single();referenceImageId=lib.AddReferenceAssets(new[]{image},"").Single();string dirId=lib.AddReferenceAssets(new[]{directory},"").Single();
    string category=lib.CreateFolder("项目分类","");lib.AddToFolder(new[]{pictureId,modelId},category);string group=lib.CreateLocalCollection("模型参考");lib.MoveToLocalCollection(new[]{modelId},group);
    check(lib.All().All(a=>!a.HiddenFromAll),"new and legacy assets default to visible");
    check(Reject(()=>lib.SetHiddenFromAll(new[]{modelId,"missing"},true))&&!lib.All().Single(a=>a.Id==modelId).HiddenFromAll,"invalid mixed batch is atomic");
    check(Reject(()=>lib.SetHiddenFromAll(new[]{modelId,dirId},true))&&Reject(()=>lib.SetHiddenFromAll(new[]{doc.Id},true))&&Reject(()=>lib.SetHiddenFromAll(new[]{web.Id},true)),"folders documents and bookmarks never gain confusing overview preferences");
    check(Reject(()=>lib.SetHiddenFromAll(new string[0],true))&&Reject(()=>lib.SetHiddenFromAll(new string[]{null},true)),"empty and null selections rejected safely");
    var history=new UndoHistory(lib.Db,"board");history.Record("hide",()=>lib.SetHiddenFromAll(new[]{modelId,pictureId,modelId},true));
    check(lib.All().Count(a=>a.HiddenFromAll)==2,"mixed managed and reference visibility batch handles duplicate IDs");
    check(lib.Members(category,false).SetEquals(new[]{pictureId,modelId})&&lib.All().Single(a=>a.Id==modelId).LocalCollection==group,"hiding never changes either kind of classification");
    check(history.Undo()!=null&&lib.All().All(a=>!a.HiddenFromAll),"undo restores all visibility flags as one action");
    Entry.ClassicEnabled=true;var main=new MainWindow(lib){PreviewMode=true};
    try{
     main.SetSection("assets");check(Visible(main).Count==3&&Visible(main).Any(a=>a.Id==modelId),"overview starts with managed image local image and STEP");
     Select(main,modelId);var hide=Item(main,Visible(main).Single(a=>a.Id==modelId),"不在全部素材中显示");check(hide!=null&&hide.IsEnabled&&Item(main,Visible(main).First(),"在全部素材中显示")==null,"shown item menu has one precise hide action");
     hide.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));check(!Visible(main).Any(a=>a.Id==modelId)&&Visible(main).Count==2,"actual menu click immediately removes only selected item from overview");
     check(((HashSet<string>)Field(main,"selection")).Count==0&&((TextBox)main.Window.FindName("Name")).Text=="","hidden item selection and inspector are cleared");
     check(((TextBlock)main.Window.FindName("ViewCount")).Text.StartsWith("2 "),"overview count matches visible items");
     main.UndoLast();check(Visible(main).Any(a=>a.Id==modelId),"Ctrl+Z action restores hidden model in overview");
     Select(main,modelId,pictureId,referenceImageId);main.SetSelectedOverviewHidden(true);check(Visible(main).Count==0&&lib.All().Count==6,"bulk hiding does not remove database records");
     main.SetSection("pictures");check(Visible(main).Count==2&&Visible(main).All(a=>a.HiddenFromAll),"hidden images remain in dedicated picture page");
     main.RequestNavigate(category);check(Visible(main).Count==2&&Visible(main).Any(a=>a.Id==modelId),"hidden managed and local assets remain in original category");
     main.SetSection("shortcuts");main.OpenLocalCollection(group);check(Visible(main).Single().Id==modelId,"hidden model remains inside its local classification folder");
     Select(main,modelId);check(((TextBlock)main.Window.FindName("LocalStorageHint")).Text.Contains("右键可恢复显示"),"local address hint explains how to restore visibility");
     var show=Item(main,Visible(main).Single(),"在全部素材中显示");check(show!=null&&Item(main,Visible(main).Single(),"不在全部素材中显示")==null,"hidden item menu offers restoration instead of removal");
     show.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));main.SetSection("assets");check(Visible(main).Single().Id==modelId,"restore menu returns model without revealing other hidden items");
     main.SetSection("pictures");Select(main,pictureId,referenceImageId);main.SetSelectedOverviewHidden(false);main.SetSection("assets");check(Visible(main).Count==3,"batch restore in dedicated type view restores overview");
     main.SetSection("shortcuts");Select(main,referenceImageId);main.SetSelectedOverviewHidden(true);main.SetSection("pictures");Select(main,pictureId,referenceImageId);
     check(Item(main,Visible(main).First(),"不在全部素材中显示")!=null&&Item(main,Visible(main).First(),"在全部素材中显示")!=null,"mixed visibility selection offers explicit show and hide actions");
     typeof(MainWindow).GetField("busy",Flags).SetValue(main,true);check(!Item(main,Visible(main).First(),"在全部素材中显示").IsEnabled,"visibility menu disabled during import");main.SetSelectedOverviewHidden(false);check(lib.All().Single(a=>a.Id==referenceImageId).HiddenFromAll,"busy visibility request does not change metadata");typeof(MainWindow).GetField("busy",Flags).SetValue(main,false);
     main.SetSelectedOverviewHidden(false);Select(main,pictureId);((TextBox)main.Window.FindName("Name")).Text="未保存的新名称";main.PendingInformationChoice=()=>MessageBoxResult.Cancel;main.SetSelectedOverviewHidden(true);check(!lib.All().Single(a=>a.Id==pictureId).HiddenFromAll&&((TextBox)main.Window.FindName("Name")).Text=="未保存的新名称","cancel preserves unsaved editor and visibility");
     main.PendingInformationChoice=()=>MessageBoxResult.Yes;main.SetSelectedOverviewHidden(true);check(lib.All().Single(a=>a.Id==pictureId).Name=="未保存的新名称"&&lib.All().Single(a=>a.Id==pictureId).HiddenFromAll,"save pending metadata before hiding");main.PendingInformationChoice=null;
     lib.Trash(new[]{pictureId},true);main.RequestNavigate("trash");Select(main,pictureId);check(Visible(main).Any(a=>a.Id==pictureId)&&Item(main,Visible(main).First(),"在全部素材中显示")==null,"hidden item remains recoverable in trash without irrelevant display actions");lib.Trash(new[]{pictureId},false);
     main.SetSection("shortcuts");Select(main,dirId);check(Item(main,lib.All().Single(a=>a.Id==dirId),"不在全部素材中显示")==null,"real directory has no redundant hide action");
     check(!main.Window.IsVisible,"visibility integration tests never take over user's desktop");
    }finally{main.Window.Close();Entry.ClassicEnabled=false;ThumbnailImage.CurrentLibrary=null;}
    lib.SetHiddenFromAll(new[]{modelId,pictureId},true);history.Clear();check(!history.Record("hide again",()=>lib.SetHiddenFromAll(new[]{modelId,pictureId},true))&&history.Count==0,"idempotent hide does not add fake undo step");
    history.Record("show",()=>lib.SetHiddenFromAll(new[]{modelId},false));lib.SetHiddenFromAll(new[]{modelId},true);check(Reject(()=>history.Undo()),"undo refuses to overwrite newer external visibility changes");
    string migrated=Path.Combine(root,"visibility-transfer");LibraryTransfer.Transfer(lib,migrated,null,()=>false);using(var moved=new Library(migrated)){moved.EnableReferenceAssets();check(moved.All().Count(a=>a.HiddenFromAll)==2,"library transfer preserves both kinds of visibility preference");}
    check(File.ReadAllBytes(model).SequenceEqual(before)&&File.GetLastWriteTimeUtc(model)==modified&&File.Exists(lib.FullPath(picture)),"hiding preserves managed originals and source bytes and timestamps");
   }
   using(var reopened=new Library(path)){reopened.EnableReferenceAssets();check(reopened.All().Count(a=>a.HiddenFromAll)==2,"hidden preferences survive restart and idempotent migration");}
  }
 }
}
