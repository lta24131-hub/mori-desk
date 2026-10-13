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
 public static class ClassicTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,Flags).GetValue(main);}
  static void Call(MainWindow main,string name){typeof(MainWindow).GetMethod(name,Flags,null,Type.EmptyTypes,null).Invoke(main,null);}
  static List<Asset> Visible(MainWindow main){return (List<Asset>)Field(main,"visible");}
  static void Select(MainWindow main,params string[] ids){var selected=(HashSet<string>)Field(main,"selection");selected.Clear();foreach(var id in ids)selected.Add(id);Call(main,"UpdateInspector");}
  public static void Run(string root,Action<bool,string> check){
   string source=Path.Combine(root,"classic-originals"),libraryRoot=Path.Combine(root,"classic-library");Directory.CreateDirectory(source);
   string photo=Path.Combine(source,"参考图片.png"),model=Path.Combine(source,"装配.STEP"),document=Path.Combine(source,"项目说明.txt"),folder=Path.Combine(source,"设计项目");
   File.Copy(Path.Combine(root,"reference-photo.png"),photo);File.WriteAllText(model,"ISO-10303-21;fixture");File.WriteAllText(document,"fixture");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"不要导入.mp4"),"fixture");
   var bytes=File.ReadAllBytes(photo);var time=File.GetLastWriteTimeUtc(photo);
   using(var lib=new Library(libraryRoot)){
    var old=new ReferenceStore(lib.Db);string category=old.AddFolder("历史分类");old.Add(new[]{model},category);
    string managed=lib.Import(photo,"","图库图片","https://example.org/photo","tag","note").Item.Id;
    lib.ImportBookmark("https://example.org/","网站","");lib.EnableReferenceAssets();
    check(lib.Folders(false).Any(f=>f.Id==category),"classic preserves earlier local category");
    var history=new UndoHistory(lib.Db,"board");string[] ids=null;
    history.Record("drop",()=>ids=lib.AddReferenceAssets(new[]{folder,photo,document},""));
    check(lib.All().Count==6&&lib.All().Count(a=>a.IsReference)==4,"classic combines references with existing copies without duplication");
    check(lib.Db.Query("SELECT id FROM assets").Count==2,"folder drop never imports its files");
    check(lib.AddReferenceAssets(new[]{photo.ToUpperInvariant()},"").Single()==ids[1],"repeat drop reuses same local path");
    bool failed=false;try{lib.AddReferenceAssets(new[]{photo,Path.Combine(source,"missing")},"");}catch{failed=true;}check(failed&&lib.All().Count==6,"invalid drop is atomic");
    check(history.Undo()!=null&&lib.All().Count==3,"undo drop removes reference index only");
    ids=lib.AddReferenceAssets(new[]{folder,photo,document},"");string imageId=ids[1];
    var image=lib.All().Single(a=>a.Id==imageId);check(lib.FullPath(image)==photo,"reference address points at source");
    check(lib.All().Count(a=>MediaTypes.Contains(a,"assets"))==3,"all materials includes image and model refs but excludes folders");
    check(lib.All().Count(a=>MediaTypes.Contains(a,"documents"))==1,"documents remain separate");
    history.Record("edit",()=>{image.Name="显示别名";image.Tags="测试标签";image.Notes="测试备注";image.Favorite=true;lib.Update(image);});
    check(history.Undo()!=null&&lib.All().Single(a=>a.Id==imageId).Name=="参考图片.png","reference metadata uses ordinary undo");
    image=lib.All().Single(a=>a.Id==imageId);image.Tags="保留标签";lib.Update(image);
    history.Record("remove",()=>lib.Trash(new[]{imageId},true));check(!lib.All().Any(a=>a.Id==imageId)&&File.Exists(photo),"remove shortcut never deletes original");
    check(history.Undo()!=null&&lib.All().Single(a=>a.Id==imageId).Tags=="保留标签","undo removal restores reference metadata");
    history.Record("move",()=>lib.MoveAssets(new[]{managed,imageId},category,false));check(lib.Members(category,false).Contains(imageId)&&lib.Members(category,false).Contains(managed),"mixed move works in old category tree");
    history.Record("delete category",()=>lib.DeleteFolder(category));check(lib.AssetFolders(imageId).Count==0&&lib.AssetFolders(managed).Count==0,"delete category unfiles both types");
    check(history.Undo()!=null&&lib.AssetFolders(imageId).Single().Id==category,"category undo restores shortcuts");
    failed=false;try{lib.PermanentlyDelete(new[]{imageId});}catch{failed=true;}check(failed&&File.Exists(photo),"permanent delete cannot target external originals");
    using(var worker=new Library(libraryRoot))check(worker.All().Count==2&&!worker.IncludeReferences,"native worker remains managed-only");
    // Recreate old API after the new schema: explicit insert columns must still work.
    var oldApi=new ReferenceStore(lib.Db);string extra=Path.Combine(source,"外部资料");Directory.CreateDirectory(extra);check(oldApi.Add(new[]{extra},"")==1,"old reference import remains compatible with expanded schema");
    Entry.ClassicEnabled=true;Entry.BoardEnabled=false;var main=new MainWindow(lib){PreviewMode=true};
    try{
     check(Field(main,"boardPage")==null&&Field(main,"referencePage")==null,"classic startup never covers gallery with desktop board");
     check(((TreeView)main.Window.FindName("Folders")).Visibility==Visibility.Visible&&((Button)main.Window.FindName("Import")).Visibility==Visibility.Visible,"original category tree and toolbar restored");
     check(((TextBox)main.Window.FindName("Tags"))!=null&&((TextBox)main.Window.FindName("Notes"))!=null,"old inspector metadata fields retained");
     Select(main,managed);check(((TextBox)main.Window.FindName("LocalAddress")).Text==lib.FullPath(lib.All().Single(a=>a.Id==managed)),"download/copy inspector shows its local address");
     Select(main,imageId);check(((TextBox)main.Window.FindName("LocalAddress")).Text==photo&&((Button)main.Window.FindName("Delete")).Content.ToString()=="移除快捷收藏","shortcut inspector uses source address and clear remove action");
     main.SetSection("shortcuts");check(Visible(main).All(a=>a.IsReference)&&Visible(main).Count==5,"local navigation uses references only");
     var rows=GalleryLayout.BuildShortcuts(Visible(main),700,210);var folderTile=rows.SelectMany(r=>r.Items).First(t=>t.Asset.IsDirectory);var fileTile=rows.SelectMany(r=>r.Items).First(t=>!t.Asset.IsDirectory);
     check(folderTile.Width>fileTile.Width*1.5&&folderTile.ImageHeight>fileTile.ImageHeight,"large folders distinct from small files");
     check(rows.First().Heading=="文件夹"&&rows.Count(r=>r.Heading=="文件")==1&&rows.All(r=>r.Items.Count>0),"local groups separated and virtualizable");
     main.SetSection("bookmarks");check(((Button)Field(main,"referenceNav")).Background==main.Window.FindResource("Panel"),"local highlight clears on website page");
     int pendingCalls=0;main.PendingInformationChoice=()=>{pendingCalls++;return MessageBoxResult.Cancel;};
     string extraFile=Path.Combine(source,"新的模型.stl");File.WriteAllText(extraFile,"solid fixture\nendsolid fixture");
     // Exercise the actual routed external drop from another page, then pump only its queued handler.
     var data=new DataObject(DataFormats.FileDrop,new[]{extraFile});var args=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),Flags,null,new object[]{data,DragDropKeyStates.None,DragDropEffects.Copy,main.Window,new Point(10,10)},System.Globalization.CultureInfo.InvariantCulture);
     args.RoutedEvent=UIElement.PreviewDropEvent;main.Window.RaiseEvent(args);var frame=new DispatcherFrame();main.Window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);
     check(args.Handled&&lib.All().Any(a=>a.ReferencePath==extraFile)&&pendingCalls==0,"external drop immediately collects from website page without confirmation");
     main.SetSection("shortcuts");Select(main,imageId);((TextBox)main.Window.FindName("Notes")).Text="未保存的备注";main.CollectClassicPaths(new[]{folder},"");check(pendingCalls==0&&lib.All().Single(a=>a.Id==imageId).Notes=="未保存的备注","drop preserves inspector edits without confirmation");
     main.SetSection("shortcuts");main.SelectAllVisible();check(((Button)main.Window.FindName("Export")).IsEnabled==false,"folder shortcut selection cannot accidentally recurse during export");
     main.SetSection("assets");check(Visible(main).Any(a=>a.Id==managed)&&Visible(main).Any(a=>a.Id==imageId),"original main gallery shows both storage types");
    }finally{main.Window.Close();Entry.ClassicEnabled=false;ThumbnailImage.CurrentLibrary=null;}
    string target=Path.Combine(root,"classic-transfer");LibraryTransfer.Transfer(lib,target,null,()=>false);
    using(var moved=new Library(target)){moved.EnableReferenceAssets();check(moved.All().Count(a=>a.IsReference)==6&&moved.All().Any(a=>a.ReferencePath==photo),"whole-library migration preserves shortcut addresses");check(Directory.GetFiles(Path.Combine(target,"原图"),"*",SearchOption.AllDirectories).Length==2,"whole-library migration never copies external originals");}
    check(File.ReadAllBytes(photo).SequenceEqual(bytes)&&File.GetLastWriteTimeUtc(photo)==time&&File.Exists(Path.Combine(folder,"不要导入.mp4")),"all tests preserve source bytes and folder contents");
   }
  }
 }
}
