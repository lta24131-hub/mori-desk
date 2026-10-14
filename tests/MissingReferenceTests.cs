using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Mori;

namespace MoriDesk {
 internal static class MissingReferenceTests {
  const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
  static object Call(MainWindow main,string name,Asset item){return typeof(MainWindow).GetMethod(name,Flags,null,new[]{typeof(Asset)},null).Invoke(main,new object[]{item});}
  static void Select(MainWindow main,params string[] ids){var selected=(HashSet<string>)typeof(MainWindow).GetField("selection",Flags).GetValue(main);selected.Clear();foreach(var id in ids)selected.Add(id);typeof(MainWindow).GetMethod("UpdateInspector",Flags).Invoke(main,null);}
  static bool Rejected(Action action){try{action();return false;}catch{return true;}}
  internal static void Run(string testRoot,Action<bool,string> check){
   string root=Path.Combine(testRoot,"missing-reference");Directory.CreateDirectory(root);
   string source=Path.Combine(root,"originals");Directory.CreateDirectory(source);
   string file=Path.Combine(source,"原模型.step"),other=Path.Combine(source,"另一模型.step"),folder=Path.Combine(source,"项目文件夹");
   byte[] original=new byte[]{20,31,40,59,68,77};File.WriteAllBytes(file,original);File.WriteAllText(other,"other original");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"内部视频.mp4"),"not imported");
   DateTime modified=File.GetLastWriteTimeUtc(file);
   check(MissingLocalFile.Exists(file,false)&&!MissingLocalFile.Exists(file,true),"reference availability is file/folder aware");
   check(MissingLocalFile.Exists(folder,true)&&!MissingLocalFile.Exists(folder,false),"directory replacement cannot masquerade as a file");
   check(!MissingLocalFile.Exists(Path.Combine(source,"not-connected"),false),"missing path is reported unavailable without creating it");
   var dialog=MissingLocalFile.Create(null,file,false,result=>{});var body=(StackPanel)dialog.Content;var buttons=body.Children.OfType<StackPanel>().Single().Children.OfType<Button>().ToList();
   check(body.Children.OfType<TextBox>().Single().Text==file&&body.Children.OfType<TextBox>().Single().IsReadOnly,"missing prompt exposes a selectable, read-only original address");
   string previousTheme=Theme.Current;foreach(string theme in new[]{"dark","light"}){Theme.Set(theme);var address=body.Children.OfType<TextBox>().Single();check(address.Background==Theme.Brush("Input")&&address.BorderBrush==Theme.Brush("Border")&&address.Foreground==Theme.Brush("Muted"),"missing prompt original address follows "+theme+" theme");}Theme.Set(previousTheme);
   check(buttons.Select(b=>(MissingLocalFileAction)b.Tag).Distinct().Count()==3&&buttons.Single(b=>(MissingLocalFileAction)b.Tag==MissingLocalFileAction.Keep).IsCancel,"missing prompt has relocation, explicit shortcut removal and safe cancel");
   check(body.Children.OfType<TextBlock>().Any(t=>t.Text.Contains("不会删除原文件")&&t.Text.Contains("Ctrl+Z")),"missing prompt clearly limits removal and explains undo");dialog.Close();
   check(MissingLocalFile.Explanation(false).Contains("磁盘暂时不可用")&&MissingLocalFile.Explanation(true).Contains("原文件夹"),"missing copy does not assert that an unavailable disk was deleted");

   bool classic=Entry.ClassicEnabled,board=Entry.BoardEnabled;Entry.ClassicEnabled=true;Entry.BoardEnabled=false;
   using(var library=new Library(Path.Combine(root,"library"))){
    library.EnableReferenceAssets();string category=library.CreateFolder("保留分类","");var ids=library.AddReferenceAssets(new[]{file,other,folder},category);string id=ids[0],otherId=ids[1],folderId=ids[2];
    var item=library.All().Single(a=>a.Id==id);item.Name="保留显示名称";item.Tags="标签";item.Notes="已保存备注";item.Favorite=true;library.Update(item);library.SetHiddenFromAll(new[]{id},true);string collection=library.CreateLocalCollection("保留本地分类");library.MoveToLocalCollection(new[]{id},collection);library.Db.Exec("UPDATE desk_refs SET thumb_stamp='old-valid-thumbnail-source' WHERE id="+Database.Q(id.Substring(4)));
    var main=new MainWindow(library){PreviewMode=true};
    try{
     Select(main,id);var menu=main.CreateAssetMenu(library.All().Single(a=>a.Id==id));check(menu.Items.OfType<MenuItem>().Any(i=>Convert.ToString(i.Header)=="重新定位原文件…"),"classic material menu now exposes relocation");
     Select(main,id);((TextBox)main.Window.FindName("Notes")).Text="尚未保存的备注";string moved=Path.Combine(source,"移动并换扩展名.mp4");File.Move(file,moved);int prompts=0,pending=0;
     main.MissingReferenceChoice=a=>{prompts++;return MissingLocalFileAction.Keep;};main.PendingInformationChoice=()=>{pending++;return MessageBoxResult.Cancel;};
     item=library.All().Single(a=>a.Id==id);object resolved=Call(main,"ResolveReferenceForOpen",item);
     check(resolved==null&&prompts==1&&pending==0,"opening moved source has explicit recovery rather than silent status-only failure");
     check(((TextBox)main.Window.FindName("Notes")).Text=="尚未保存的备注"&&((HashSet<string>)typeof(MainWindow).GetField("selection",Flags).GetValue(main)).Contains(id),"keeping missing shortcut preserves selected item and unsaved draft");
     check(library.All().Single(a=>a.Id==id).ReferencePath==file&&library.All().Count==3,"cancel does not relink or remove missing reference automatically");
     Call(main,"Preview",item);Call(main,"OpenFile",item);Call(main,"RevealAsset",item);check(prompts==4,"preview, default-program and Explorer commands all use the same missing-reference feedback");
     check(pending==0&&((TextBox)main.Window.FindName("Notes")).Text=="尚未保存的备注","passive failed-open commands do not discard drafts or ask to save");
     main.MissingReferenceChoice=a=>MissingLocalFileAction.Relocate;main.ReferencePathChoice=a=>null;Call(main,"ResolveReferenceForOpen",item);check(pending==0&&library.All().Single(a=>a.Id==id).ReferencePath==file,"canceling relocation picker leaves path and drafts unchanged");
     main.ReferencePathChoice=a=>moved;Call(main,"ResolveReferenceForOpen",item);check(pending==1&&library.All().Single(a=>a.Id==id).ReferencePath==file,"existing unsaved-information Cancel also cancels relocation safely");
     main.PendingInformationChoice=()=>MessageBoxResult.Yes;var relocated=(Asset)Call(main,"ResolveReferenceForOpen",item);
     check(relocated!=null&&relocated.ReferencePath==moved&&relocated.Id==id,"recovered open receives fresh asset for the newly chosen address");
     check(relocated.Ext==".mp4"&&relocated.IsVideo&&!MediaTypes.Contains(relocated,"pictures")&&relocated.ReferenceSection=="videos"&&!relocated.IsDirectory,"relink immediately derives route and type filters from a changed file extension");
     check(relocated.ThumbnailSourceStamp=="old-valid-thumbnail-source","relink preserves last good thumbnail stamp until fresh rendering succeeds; new path will invalidate it");
     var saved=library.All().Single(a=>a.Id==id);check(saved.Name=="保留显示名称"&&saved.Tags=="标签"&&saved.Notes=="尚未保存的备注"&&saved.Favorite&&saved.HiddenFromAll&&saved.LocalCollection==collection,"relocation preserves metadata, visibility, local folder and saved draft");
     check(library.AssetFolders(id).Single().Id==category,"relocation retains ordinary category memberships");
     main.UndoLast();check(library.All().Single(a=>a.Id==id).ReferencePath==file,"Ctrl+Z restores original shortcut address without moving the source back");
     var restoredType=library.All().Single(a=>a.Id==id);check(restoredType.Ext==".step"&&!restoredType.IsVideo&&restoredType.ReferenceSection=="models"&&restoredType.ThumbnailSourceStamp=="old-valid-thumbnail-source","undo restores derived original format and retains its last valid preview fingerprint");
     check(File.Exists(moved)&&!File.Exists(file)&&File.ReadAllBytes(moved).SequenceEqual(original)&&File.GetLastWriteTimeUtc(moved)==modified,"relocation and undo never alter original bytes, location or timestamp");
     check(library.All().Single(a=>a.Id==id).Notes=="尚未保存的备注","undoing relocation does not undo the separate saved draft");
     check(Rejected(()=>library.RelinkReferenceAsset(id,folder))&&library.All().Single(a=>a.Id==id).ReferencePath==file,"relink rejects file-to-directory type mismatch atomically");
     check(Rejected(()=>library.RelinkReferenceAsset(id,other))&&library.All().Single(a=>a.Id==id).ReferencePath==file,"relink does not merge or overwrite another shortcut at the chosen path");
     check(Rejected(()=>library.RelinkReferenceAsset(folderId,moved))&&library.All().Single(a=>a.Id==folderId).ReferencePath==folder,"relink rejects folder-to-file type mismatch atomically");
     check(Rejected(()=>library.RelinkReferenceAsset(id,"relative.step")),"relink refuses relative paths");
     check(Rejected(()=>FileRouter.Reveal(file))&&Rejected(()=>FileRouter.Open(null,file,"missing")),"missing Explorer and routed-file commands fail before launching anything");

     Select(main,id,otherId);main.MissingReferenceChoice=a=>MissingLocalFileAction.RemoveShortcut;item=library.All().Single(a=>a.Id==id);Call(main,"ResolveReferenceForOpen",item);
     check(!library.All().Any(a=>a.Id==id)&&library.All().Any(a=>a.Id==otherId)&&File.Exists(other)&&File.Exists(moved),"explicit missing removal affects only the opened shortcut, not other selected items or sources");
     main.UndoLast();check(library.All().Single(a=>a.Id==id).ReferencePath==file&&library.All().Single(a=>a.Id==id).Tags=="标签"&&library.All().Single(a=>a.Id==id).LocalCollection==collection,"Ctrl+Z restores removed shortcut and its metadata/folder");
     Select(main,otherId);File.Delete(other);int deletedPrompts=0;main.MissingReferenceChoice=a=>{deletedPrompts++;return MissingLocalFileAction.Keep;};item=library.All().Single(a=>a.Id==otherId);Call(main,"ResolveReferenceForOpen",item);check(deletedPrompts==1&&library.All().Any(a=>a.Id==otherId),"deleted source stays discoverable and gives recovery options");
     string movedFolder=Path.Combine(source,"重命名项目");Directory.Move(folder,movedFolder);Select(main,folderId);item=library.All().Single(a=>a.Id==folderId);Call(main,"ResolveReferenceForOpen",item);check(deletedPrompts==2,"renamed folder shortcut also gives recovery options");
     main.MissingReferenceChoice=a=>MissingLocalFileAction.Relocate;main.ReferencePathChoice=a=>movedFolder;var repairedFolder=(Asset)Call(main,"ResolveReferenceForOpen",item);check(repairedFolder!=null&&repairedFolder.IsDirectory&&repairedFolder.ReferencePath==movedFolder,"folder shortcut can be reconnected without importing children");
     check(File.ReadAllText(Path.Combine(movedFolder,"内部视频.mp4"))=="not imported"&&library.Db.Query("SELECT id FROM assets").Count==0,"folder recovery never scans or imports original contents");
     check(Directory.GetFiles(Path.Combine(library.Root,"原图"),"*",SearchOption.AllDirectories).Length==0,"all recovery operations preserve reference-only lightweight storage");
    }finally{main.Window.Close();ThumbnailImage.CurrentLibrary=null;Entry.ClassicEnabled=classic;Entry.BoardEnabled=board;}
   }
  }
 }
}
