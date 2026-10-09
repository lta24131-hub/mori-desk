using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 public static class BatchTests {
  static object Field(MainWindow m,string n){return typeof(MainWindow).GetField(n,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(m);}
  static void Call(MainWindow m,string n,params object[] args){typeof(MainWindow).GetMethod(n,BindingFlags.NonPublic|BindingFlags.Instance,null,args.Select(a=>a.GetType()).ToArray(),null).Invoke(m,args);}
  static List<Asset> Visible(MainWindow m){return (List<Asset>)Field(m,"visible");}
  static HashSet<string> Selected(MainWindow m){return (HashSet<string>)Field(m,"selection");}
  static T Find<T>(MainWindow m,string name)where T:class{return m.Window.FindName(name)as T;}
  static void Click(MainWindow m,Asset a,ModifierKeys keys){m.SelectTile(a,keys);m.FinishTileSelection(a,false);}
  static bool Fails(Action action){try{action();return false;}catch(InvalidOperationException){return true;}}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   using(var lib=new Library(Path.Combine(output,"library"))){
    string first=lib.CreateFolder("项目 A",""),other=lib.CreateFolder("备用分类",""),target=lib.CreateFolder("项目 B",""),child=lib.CreateFolder("细节",target),webFolder=lib.CreateFolder("网站","",true);
    var assets=new List<Asset>();int index=0;
    foreach(string ext in new[]{".png",".gif",".mp4",".mp3",".pdf"}){
     string file=Path.Combine(output,"fixture-"+(index++)+ext);
     using(var bmp=new System.Drawing.Bitmap(120,90)){using(var g=System.Drawing.Graphics.FromImage(bmp))g.Clear(System.Drawing.Color.FromArgb(50+index*30,100,130));bmp.Save(file,System.Drawing.Imaging.ImageFormat.Png);}
     var a=lib.Import(file,first,"批量示例 "+index,"https://example.org/","标签","").Item;assets.Add(a);File.Copy(file,lib.ThumbnailPath(a));
    }
    var web=lib.ImportBookmark("https://example.org/batch","网址示例",webFolder).Item;var web2=lib.ImportBookmark("https://example.net/batch","第二个网址","").Item;
    var originals=lib.All().ToDictionary(a=>a.Id,a=>File.ReadAllBytes(lib.FullPath(a)));
    string[] ids=assets.Select(a=>a.Id).ToArray();lib.AddToFolder(ids,other);var history=new UndoHistory(lib.Db);
    check(history.Record("批量移动",()=>lib.MoveAssets(ids,target,false)),"batch move creates one undo step");
    check(assets.All(a=>lib.AssetFolders(a.Id).Select(f=>f.Id).SequenceEqual(new[]{target})),"move replaces old categories for images GIF video audio and PDF");
    check(lib.AssetFolders(web.Id).Single().Id==webFolder,"media move does not affect bookmarks");
    history.Undo();check(assets.All(a=>lib.AssetFolders(a.Id).Count==2),"one undo restores every original category membership");
    check(Fails(()=>lib.MoveAssets(new[]{ids[0],web.Id},target,false))&&lib.AssetFolders(ids[0]).Count==2,"mixed namespace move fails atomically");
    check(Fails(()=>lib.MoveAssets(ids,webFolder,false))&&lib.AssetFolders(ids[0]).Count==2,"bookmark destination rejected for media without altering memberships");
    check(Fails(()=>lib.MoveAssets(new[]{ids[0],"missing"},target,false))&&lib.AssetFolders(ids[0]).Count==2,"missing selected item prevents partial move");
    check(Fails(()=>lib.MoveAssets(ids,"missing",false)),"deleted destination cannot strand selected assets");
    lib.Trash(new[]{ids[1]},true);check(Fails(()=>lib.MoveAssets(ids,target,false))&&lib.AssetFolders(ids[0]).Count==2,"deleted asset prevents moving partial live selection");lib.Trash(new[]{ids[1]},false);
    history.Record("未分类",()=>lib.MoveAssets(ids,"",false));check(assets.All(a=>lib.AssetFolders(a.Id).Count==0),"moving to unfiled clears memberships only");history.Undo();
    lib.MoveAssets(new[]{web.Id,web2.Id,web.Id},webFolder,true);check(lib.AssetFolders(web2.Id).Single().Id==webFolder,"bookmark batch moves accept duplicate IDs safely");
    lib.MoveAssets(new[]{web2.Id},"",true);
    var choices=CategoryPicker.Choices(lib.Folders(false),true);
    check(choices[0].Id==""&&choices.Any(c=>c.Id==child&&c.Label=="项目 B / 细节")&&!choices.Any(c=>c.Id==webFolder),"category choices include unfiled and full paths without mixing bookmark folders");
    check(!CategoryPicker.Choices(lib.Folders(false),false).Any(c=>c.Id==""),"add category never offers clearing memberships");
    var main=new MainWindow(lib){PreviewMode=true};
    try{
     main.SetSection("assets");var visible=Visible(main).ToArray();
     check(visible.Length==4&&Find<Border>(main,"BatchBar").Visibility==Visibility.Collapsed,"normal browsing does not reserve space for batch toolbar");
     main.SetBatchSelecting(true);check(Find<Border>(main,"BatchBar").Visibility==Visibility.Visible&&visible.All(a=>a.Selecting),"multi-select mode reveals toolbar and all tile checkmarks");
     check(!Find<Button>(main,"BatchDelete").IsEnabled&&!Find<Button>(main,"BatchMove").IsEnabled,"empty selection disables batch mutations");
     Click(main,visible[0],ModifierKeys.None);Click(main,visible[1],ModifierKeys.None);
     check(Selected(main).Count==2&&Find<TextBlock>(main,"BatchCount").Text=="已选 2 项","ordinary clicks toggle multiple items without modifier keys");
     Click(main,visible[0],ModifierKeys.None);check(Selected(main).Count==1&&Selected(main).Contains(visible[1].Id),"clicking selected item again only unchecks that item");
     Click(main,visible[0],ModifierKeys.None);main.SelectTile(visible[1],ModifierKeys.None);main.RestoreDragSelection();main.FinishTileSelection(visible[1],true);check(Selected(main).Count==2,"dragging checked item in batch mode retains the complete drag selection");
     main.SetBatchSelecting(false);check(Selected(main).Count==0&&visible.All(a=>!a.Selecting),"done clears selection and returns to ordinary browsing");
     Click(main,visible[0],ModifierKeys.None);Click(main,visible[1],ModifierKeys.Control);
     check(Selected(main).Count==2&&Find<Border>(main,"BatchBar").Visibility==Visibility.Visible,"Ctrl click exposes same toolbar and retains selection on mouse-up");
     main.SelectTile(visible[0],ModifierKeys.None);main.FinishTileSelection(visible[0],true);check(Selected(main).Count==2,"dragging an already selected tile preserves multiple files");
     Click(main,visible[0],ModifierKeys.None);check(Selected(main).Count==1,"plain click in normal mode still selects one item");
     Click(main,visible[3],ModifierKeys.Shift);check(Selected(main).Count==4,"Shift range includes endpoints and intervening tiles");
     var menu=main.CreateAssetMenu(visible[0]);check(menu.Items.OfType<MenuItem>().Any(i=>Convert.ToString(i.Header)=="移动到分类…")&&menu.Items.OfType<MenuItem>().Any(i=>Convert.ToString(i.Header)=="添加到分类…"),"context menu distinguishes moving and adding categories");
     main.ApplyCategory(Selected(main).ToArray(),target,true,false);check(visible.All(a=>lib.AssetFolders(a.Id).Single().Id==target),"batch toolbar production move changes all selected items");main.UndoLast();
     main.RequestNavigate(first);main.SelectAllVisible();check(Selected(main).Count==5,"select all category includes documents as well as media");
     main.ApplyCategory(Selected(main).ToArray(),target,true,false);check(Visible(main).Count==0&&Selected(main).Count==0,"moving out of category refreshes gallery and clears now-hidden selection");main.UndoLast();check(Visible(main).Count==5,"undo restores items in original category view");
     main.SetSection("pictures");main.SelectAllVisible();check(Selected(main).Count==2,"select all is limited to current filtered page, never whole library");
     string[] deleting=Selected(main).ToArray();Call(main,"Trash");check(lib.All().Count(a=>a.Deleted)==2&&lib.All().All(a=>File.Exists(lib.FullPath(a))),"bulk delete soft-deletes only selected items and preserves every original");main.UndoLast();check(lib.All().All(a=>!a.Deleted),"one undo restores entire bulk deletion");
     lib.Trash(deleting,true);main.RequestNavigate("trash");main.SelectAllVisible();check(Find<Button>(main,"BatchMove").Visibility==Visibility.Collapsed&&Find<Button>(main,"BatchRestore").Visibility==Visibility.Visible&&Find<Button>(main,"BatchDelete").Content.ToString()=="永久删除…","trash offers batch restore and explicitly permanent deletion, not moving");
     int confirmed=0;check(TrashAction.ConfirmPermanent(lib,Selected(main),n=>{confirmed=n;return false;})==null&&confirmed==2&&lib.All().Count==7,"cancel permanent-delete confirmation preserves all selected data");
     Call(main,"RestoreTrash");check(lib.All().All(a=>!a.Deleted),"batch restore returns all selected trash items");
     main.SetSection("bookmarks");main.RequestNavigate("all");main.SelectAllVisible();check(Selected(main).Count==2,"bookmark selection uses independent namespace");main.ApplyCategory(Selected(main).ToArray(),webFolder,true,true);check(lib.AssetFolders(web2.Id).Single().Id==webFolder,"batch move works for bookmarks too");main.UndoLast();
     main.SetSection("pictures");visible=Visible(main).ToArray();Click(main,visible[0],ModifierKeys.None);Find<TextBox>(main,"Name").Text="未保存的信息";main.PendingInformationChoice=()=>MessageBoxResult.Cancel;
     check(!main.SelectTile(visible[1],ModifierKeys.Control)&&Selected(main).Count==1&&Find<TextBox>(main,"Name").Text=="未保存的信息","canceling pending metadata keeps text and original selection intact");
     main.PendingInformationChoice=()=>MessageBoxResult.No;main.SetBatchSelecting(true);main.SelectAllVisible();main.PendingInformationChoice=null;
     typeof(MainWindow).GetField("busy",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(main,true);check(!main.SelectTile(visible[0],ModifierKeys.None),"busy processing blocks selection mutations");typeof(MainWindow).GetField("busy",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(main,false);
     var content=(FrameworkElement)main.Window.Content;content.SetResourceReference(Panel.BackgroundProperty,"Canvas");content.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,"Text");
     foreach(string theme in new[]{"dark","light"})foreach(int width in new[]{1040,1380}){
      Theme.Set(theme);content.Measure(new Size(width,760));content.Arrange(new Rect(0,0,width,760));content.UpdateLayout();Call(main,"BuildRows",true);content.UpdateLayout();
      var bar=Find<Border>(main,"BatchBar");var button=Find<Button>(main,"BatchDelete");var point=button.TranslatePoint(new Point(button.ActualWidth,button.ActualHeight),bar);
      check(point.X<=bar.ActualWidth+1&&point.Y<=bar.ActualHeight+1,theme+" / "+width+" batch actions fit within their toolbar");
      var bitmap=new RenderTargetBitmap(width,760,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(Path.Combine(output,theme+"-"+width+".png")))encoder.Save(file);
     }
     check(lib.All().All(a=>originals[a.Id].SequenceEqual(File.ReadAllBytes(lib.FullPath(a))))&&lib.Db.Query("PRAGMA foreign_key_check").Count==0,"all batch paths preserve original bytes and database integrity");
     check(!main.Window.IsVisible,"batch tests never open or modify the user's actual library");
    }finally{main.Window.Close();ThumbnailImage.CurrentLibrary=null;Theme.Set("dark");app.ShutdownMode=shutdown;}
   }
  }
 }
}
