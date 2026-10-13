using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 public static class LocalCollectionTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Flags).GetValue(main);}
  static void Call(MainWindow main,string name){typeof(MainWindow).GetMethod(name,Flags,null,Type.EmptyTypes,null).Invoke(main,null);}
  static List<Asset> Visible(MainWindow main){return (List<Asset>)Field(main,"visible");}
  static void Pump(MainWindow main){var frame=new DispatcherFrame();main.Window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static DragEventArgs Drop(MainWindow main,UIElement target,DataObject data){var args=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),Flags,null,new object[]{data,DragDropKeyStates.None,DragDropEffects.Copy,main.Window,new Point(10,10)},System.Globalization.CultureInfo.InvariantCulture);args.RoutedEvent=UIElement.PreviewDropEvent;target.RaiseEvent(args);Pump(main);return args;}
  static bool Reject(Action action){try{action();return false;}catch{return true;}}
  public static void Run(string root,Action<bool,string> check){
   string source=Path.Combine(root,"collection-originals"),path=Path.Combine(root,"collection-library");Directory.CreateDirectory(source);
   string image=Path.Combine(source,"参考图.png"),model=Path.Combine(source,"结构.step"),directory=Path.Combine(source,"真实项目文件夹");File.Copy(Path.Combine(root,"reference-photo.png"),image);File.WriteAllText(model,"ISO-10303-21;fixture");Directory.CreateDirectory(directory);File.WriteAllText(Path.Combine(directory,"never-import.mp4"),"fixture");
   var bytes=File.ReadAllBytes(image);var timestamp=File.GetLastWriteTimeUtc(image);
   string group="",second="",imageId="",modelId="";
   using(var lib=new Library(path)){
    lib.Import(image,"","图库副本","","","");var legacy=new ReferenceStore(lib.Db);legacy.Add(new[]{image},"");lib.EnableReferenceAssets();var recovered=lib.All().Single(a=>a.IsReference);
    check(recovered.AddedRecovered&&recovered.Added.Length>0&&recovered.Bytes==bytes.Length,"legacy refs receive stable ordering and correct file size");
    check(lib.All().First().IsReference,"recovered local image no longer buried below managed gallery");
    string registered=recovered.Added;using(var reopen=new Library(path)){reopen.EnableReferenceAssets();check(reopen.All().Single(a=>a.IsReference).Added==registered,"legacy registration migration is idempotent");}
    imageId=recovered.Id;modelId=lib.AddReferenceAssets(new[]{model},"").Single();var dirId=lib.AddReferenceAssets(new[]{directory},"").Single();
    check(lib.All().First().Id==dirId&&lib.All().Where(a=>MediaTypes.Contains(a,"assets")).First().Id==modelId,"unified chronological ordering of new refs and library copies");
    check(!MediaTypes.Contains(lib.All().Single(a=>a.Id==dirId),"assets")&&MediaTypes.Contains(lib.All().Single(a=>a.Id==dirId),"shortcuts"),"real folders only appear in local shortcuts, not all materials");
    var history=new UndoHistory(lib.Db,"board");
    history.Record("create",()=>group=lib.CreateLocalCollection("项目参考"));second=lib.CreateLocalCollection("模型资料");
    check(lib.LocalCollections().Count==2&&!lib.Folders().Any(f=>f.Id==group),"local collection folders do not pollute sidebar categories");
    check(Reject(()=>lib.CreateLocalCollection("项目参考"))&&Reject(()=>lib.RenameLocalCollection(second,"项目参考")),"duplicate collection names are rejected");
    check(Reject(()=>lib.CreateLocalCollection(" "))&&Reject(()=>lib.CreateLocalCollection(new string('x',81))),"invalid collection names rejected");
    history.Record("move",()=>lib.MoveToLocalCollection(new[]{imageId,modelId,dirId},group));
    check(lib.All().Count(a=>a.LocalCollection==group)==3,"batch classify includes file and directory shortcuts");
    check(File.Exists(image)&&Directory.GetFiles(directory).Length==1&&lib.Db.Query("SELECT id FROM assets").Count==1,"classification never copies or moves source files");
    check(history.Undo()!=null&&lib.All().All(a=>string.IsNullOrEmpty(a.LocalCollection)),"undo batch classification restores root");
    string managed=lib.All().First(a=>!a.IsReference).Id;
    check(Reject(()=>lib.MoveToLocalCollection(new[]{imageId,managed},group))&&lib.All().Single(a=>a.Id==imageId).LocalCollection=="","mixed invalid move rejects whole batch");
    check(Reject(()=>lib.MoveToLocalCollection(new[]{imageId},"missing")),"missing destination cannot orphan references");
    lib.MoveToLocalCollection(new[]{imageId,dirId},group);history.Record("rename",()=>lib.RenameLocalCollection(group,"设计参考"));check(history.Undo()!=null&&lib.LocalCollections().Any(f=>f.Name=="项目参考"),"undo rename");
    history.Record("dissolve",()=>lib.DeleteLocalCollection(group));check(!lib.LocalCollections().Any(f=>f.Id==group)&&lib.All().All(a=>a.LocalCollection!=group),"dissolving collection returns contents to root");
    check(history.Undo()!=null&&lib.All().Count(a=>a.LocalCollection==group)==2,"undo dissolve restores folder and all contents");
    history.Record("remove shortcut",()=>lib.Trash(new[]{imageId},true));check(history.Undo()!=null&&lib.All().Single(a=>a.Id==imageId).LocalCollection==group,"undo removing shortcut preserves local grouping");
    string guarded="";history.Record("new",()=>guarded=lib.CreateLocalCollection("并发保护"));lib.MoveToLocalCollection(new[]{modelId},guarded);check(Reject(()=>history.Undo())&&lib.LocalCollections().Any(f=>f.Id==guarded),"undo cannot drop a folder that gained new items");lib.MoveToLocalCollection(new[]{modelId},"");lib.DeleteLocalCollection(guarded);
    Entry.ClassicEnabled=true;Entry.BoardEnabled=false;var main=new MainWindow(lib){PreviewMode=true};
    try{
     main.SetSection("shortcuts");check(Visible(main).Count==1&&Visible(main)[0].Id==modelId,"local root shows only ungrouped shortcuts");
     var cards=(WrapPanel)main.Window.FindName("LocalCollectionList");check(cards.Children.Count==2,"local page displays classification cards");
     var inspector=(StackPanel)main.Window.FindName("InspectorBody");check(ReferenceEquals(inspector.Children[inspector.Children.Count-1],main.Window.FindName("LocalAddressPanel")),"local address is the last inspector block");
     var address=(TextBox)main.Window.FindName("LocalAddress");address.ApplyTemplate();check(address.Template.FindName("InputBorder",address)==null,"local address has no editable-input frame");
     check(main.CreateGalleryMenu().Items.OfType<MenuItem>().Any(m=>Convert.ToString(m.Header).Contains("新建分类文件夹")),"blank context menu can create a local collection");
     main.OpenLocalCollection(group);check(Visible(main).Count==2&&Visible(main).All(a=>a.LocalCollection==group),"opening collection displays its contents only");
     check(((Button)main.Window.FindName("NewLocalCollection")).Visibility==Visibility.Collapsed,"phone-style folders do not create confusing nested folders");
     main.OpenLocalCollection("");((TextBox)main.Window.FindName("Search")).Text="参考图";Call(main,"RefreshGallery");check(Visible(main).Any(a=>a.Id==imageId),"root search finds shortcuts inside folders");
     ((TextBox)main.Window.FindName("Search")).Clear();Call(main,"RefreshGallery");
     cards=(WrapPanel)main.Window.FindName("LocalCollectionList");var target=cards.Children.OfType<Button>().First(b=>b.ToolTip.ToString().StartsWith("模型资料"));
     var data=new DataObject();data.SetData("Mori.AssetIds",new[]{modelId});data.SetData(DataFormats.FileDrop,new[]{model});
     check(Drop(main,target,data).Handled&&lib.All().Single(a=>a.Id==modelId).LocalCollection==second,"actual internal routed drop moves shortcut into collection");
     main.OpenLocalCollection(second);var back=(Button)main.Window.FindName("LocalCollectionBack");
     check(Drop(main,back,data).Handled&&lib.All().Single(a=>a.Id==modelId).LocalCollection=="","dragging onto back breadcrumb moves item to root");
     main.OpenLocalCollection("");cards=(WrapPanel)main.Window.FindName("LocalCollectionList");target=cards.Children.OfType<Button>().First(b=>b.ToolTip.ToString().StartsWith("模型资料"));
     string extra=Path.Combine(source,"拖入图.png");File.Copy(image,extra);var external=new DataObject(DataFormats.FileDrop,new[]{extra});
     check(Drop(main,target,external).Handled&&lib.All().Single(a=>a.ReferencePath==extra).LocalCollection==second,"external drop targets collection without confirmation");
     main.OpenLocalCollection(second);string inside=Path.Combine(source,"组内图.png");File.Copy(image,inside);main.CollectClassicPaths(new[]{inside},"");check(lib.All().Single(a=>a.ReferencePath==inside).LocalCollection==second,"external collect inside collection stays in collection");
     main.SetSection("assets");check(Visible(main).Any(a=>a.Id==imageId)&&Visible(main).All(a=>!a.IsDirectory),"all materials still includes grouped images but never real folders");
     check(((StackPanel)main.Window.FindName("LocalCollectionPage")).Visibility==Visibility.Collapsed,"collection controls absent from all materials");
     main.SetSection("bookmarks");check(((StackPanel)main.Window.FindName("LocalCollectionPage")).Visibility==Visibility.Collapsed,"website UI unchanged");
     string sidebar=lib.CreateFolder("侧栏测试",""),sidebarDirectory=Path.Combine(source,"侧栏拖入文件夹");Directory.CreateDirectory(sidebarDirectory);main.SetSection("assets");main.CollectClassicPaths(new[]{sidebarDirectory},sidebar);
     check((string)Field(main,"mediaSection")=="shortcuts"&&Visible(main).Any(a=>a.IsDirectory),"directory-only sidebar drop reveals local folder instead of an empty media category");
     main.SetSection("shortcuts");main.OpenLocalCollection(second);lib.DeleteLocalCollection(second);Call(main,"RefreshGallery");check((string)Field(main,"localCollection")=="","deleting current collection externally returns to root safely");
     // Restore the preview fixture with useful folders and contents.
     second=main.CreateLocalCollection("模型资料",new[]{modelId});
    }finally{main.Window.Close();Entry.ClassicEnabled=false;ThumbnailImage.CurrentLibrary=null;}
    string targetLibrary=Path.Combine(root,"collection-transfer");LibraryTransfer.Transfer(lib,targetLibrary,null,()=>false);using(var moved=new Library(targetLibrary)){moved.EnableReferenceAssets();check(moved.LocalCollections().Count==2&&moved.All().Single(a=>a.Id==imageId).LocalCollection==group,"library migration preserves local folder classification");}
    check(File.ReadAllBytes(image).SequenceEqual(bytes)&&File.GetLastWriteTimeUtc(image)==timestamp,"all collection operations preserve source bytes and timestamps");
   }
   using(var reopened=new Library(path)){reopened.EnableReferenceAssets();check(reopened.LocalCollections().Count==2&&reopened.All().Single(a=>a.Id==imageId).LocalCollection==group,"collection folders and membership survive restart");}
  }
 }
}
