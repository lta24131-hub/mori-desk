using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace Mori {
 public static class MenuLogicTests {
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(main);}
  static void Set(MainWindow main,string name,object value){typeof(MainWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).SetValue(main,value);}
  static void Call(MainWindow main,string name,params object[] args){typeof(MainWindow).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance,null,args.Select(a=>a.GetType()).ToArray(),null).Invoke(main,args);}
  static List<Asset> Visible(MainWindow m){return (List<Asset>)Field(m,"visible");}
  static void Select(MainWindow m,params Asset[] assets){var ids=(HashSet<string>)Field(m,"selection");ids.Clear();foreach(var a in assets)ids.Add(a.Id);Call(m,"UpdateInspector");}
  static string[] Labels(ItemsControl menu){return menu.Items.OfType<MenuItem>().Select(i=>Convert.ToString(i.Header)).ToArray();}
  static MenuItem Item(ItemsControl menu,string title){return menu.Items.OfType<MenuItem>().Single(i=>Convert.ToString(i.Header)==title);}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);var app=Application.Current??new Application();var previous=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   using(var lib=new Library(Path.Combine(output,"library"))){
    string folder=lib.CreateFolder("同一个项目",""),webFolder=lib.CreateFolder("设计网站","",true);
    string path=Path.Combine(output,"image.png");using(var bmp=new System.Drawing.Bitmap(40,30)){using(var g=System.Drawing.Graphics.FromImage(bmp))g.Clear(System.Drawing.Color.Teal);bmp.Save(path,System.Drawing.Imaging.ImageFormat.Png);}
    var pic=lib.Import(path,folder,"图片","https://example.org/source","","").Item;
    path=Path.Combine(output,"video.mp4");File.WriteAllText(path,"synthetic video metadata fixture");var video=lib.Import(path,folder,"视频","","","").Item;
    path=Path.Combine(output,"document.pdf");File.WriteAllText(path,"%PDF-1.4 synthetic metadata fixture");var pdf=lib.Import(path,folder,"文档","","","").Item;
    var web=lib.ImportBookmark("https://example.org/","网站",webFolder).Item;
    var web2=lib.ImportBookmark("https://example.net/","网站二","").Item;
    var main=new MainWindow(lib){PreviewMode=true};
    try{
     main.SetSection("pictures");check(Visible(main).Single().Id==pic.Id,"type shortcut pictures excludes video PDF and URLs");
     main.SetSection("videos");check(Visible(main).Single().Id==video.Id,"type shortcut videos is whole-library video view");
     main.RequestNavigate(folder);check(Visible(main).Count==3&&Visible(main).All(a=>!a.IsBookmark),"project folder entered from video shows every local media type, never URLs");
     var filter=(ComboBox)main.Window.FindName("MediaFilter");check(filter.Visibility==Visibility.Visible&&filter.SelectedIndex==0,"folder type filtering is explicit and defaults to all types");
     filter.SelectedIndex=1;check(Visible(main).Single().Id==pic.Id,"project type selector can show only pictures");
     main.SetSection("documents");check(filter.Visibility==Visibility.Collapsed&&Visible(main).Single().Id==pdf.Id&&((TextBlock)main.Window.FindName("ViewTitle")).Text.Contains("PDF"),"document shortcut resets filters and declares PDF limitation");
     Select(main,pdf);check(!((Button)main.Window.FindName("Compare")).IsEnabled&&!((Button)main.Window.FindName("Similar")).IsEnabled,"PDF does not advertise image comparison or similarity");
     main.SetSection("bookmarks");check(Visible(main).Single().Id==web2.Id,"website home lists unfiled links without flattening nested folders");main.RequestNavigate("all");check(Visible(main).Count==2&&Visible(main).All(a=>a.IsBookmark),"explicit all websites view remains available and separate");
     check(((Button)main.Window.FindName("Trash")).Content.ToString()=="网址回收站"&&((Button)main.Window.FindName("ImportBookmarks")).Visibility==Visibility.Collapsed,"utility scope is labeled and duplicate bookmark import button removed");
     Select(main,web);var menu=main.CreateAssetMenu(web);var labels=Labels(menu);
     check(labels.Count(x=>x.Contains("打开"))==1&&!labels.Contains("用默认程序打开")&&!labels.Contains("在资源管理器中显示"),"website menu has one open action without duplicate local-file actions");
     check(labels.Contains("移动到文件夹…")&&Item(menu,"复制网址").InputGestureText=="Ctrl+C","bookmark move wording and copy shortcut match actual behavior");
     Select(main,web,web2);labels=Labels(main.CreateAssetMenu(web));check(!labels.Contains("打开网址")&&labels.Contains("复制选中网址"),"multi-select menu does not imply opening only one of multiple selected websites");
     check(MainWindow.BookmarkCopyText(new[]{web,web2})==web.Source+Environment.NewLine+web2.Source,"copy selected websites yields actual addresses in separate lines, not URL files");
     var more=main.CreateMoreMenu();labels=Labels(more);check(labels.Contains("导入")&&labels.Contains("图库管理")&&labels.Contains("显示")&&labels.Last()=="退出 Mori Desk","more menu groups functions with exit last");
     check(!labels.Any(l=>l.Contains("规划中"))&&Item(more,"导入").Items.Count==4,"more menu has no nonfunctional planned feature and preserves all four imports");
     check(!Labels(main.CreateGalleryMenu()).Contains("瀑布布局")&&!Labels(main.CreateGalleryMenu()).Contains("显示信息"),"website display menu omits irrelevant image layout settings");
     check(Item(more,"显示").Items.Count>4&&Labels(Item(more,"图库管理")).Contains("从备份恢复到新文件夹…"),"display recovery and backup restore remain discoverable");
     main.SetSection("pictures");Select(main,pic);((TextBox)main.Window.FindName("Name")).Text="尚未保存";main.PendingInformationChoice=()=>MessageBoxResult.Cancel;
     check(!main.RequestNavigate(folder)&&Visible(main).Single().Id==pic.Id&&((TextBox)main.Window.FindName("Name")).Text=="尚未保存","cancel navigation retains selected asset and unsaved editor text");
     main.PendingInformationChoice=()=>MessageBoxResult.Yes;check(main.RequestNavigate(folder)&&lib.All().Single(a=>a.Id==pic.Id).Name=="尚未保存","save before navigation commits information without losing folder destination");
     Select(main,lib.All().Single(a=>a.Id==pic.Id));((TextBox)main.Window.FindName("Name")).Text="不要保存";main.PendingInformationChoice=()=>MessageBoxResult.No;
     check(main.RequestNavigate("unfiled")&&lib.All().Single(a=>a.Id==pic.Id).Name=="尚未保存","discard choice navigates without changing saved metadata");main.PendingInformationChoice=null;
     lib.Trash(new[]{pic.Id,video.Id,pdf.Id,web.Id},true);main.SetSection("videos");main.RequestNavigate("trash");
     check(Visible(main).Count==3&&Visible(main).All(a=>!a.IsBookmark)&&((Button)main.Window.FindName("Trash")).Content.ToString()=="素材回收站","media recycle bin shows all three local types even entered from video");
     Select(main,pdf);labels=Labels(main.CreateAssetMenu(pdf));check(labels.Contains("恢复素材")&&labels.Contains("永久删除…")&&!labels.Contains("添加到分类…")&&!labels.Contains("添加星标"),"trash menu offers restore and confirmed delete, not live-asset organization");
     main.SetBookmarkMode(true);main.RequestNavigate("trash");check(Visible(main).Single().Id==web.Id&&((Button)main.Window.FindName("EmptyImport")).Visibility==Visibility.Collapsed,"website trash is independent and never prompts importing into trash");
     lib.Trash(new[]{pic.Id,video.Id,pdf.Id,web.Id},false);main.SetSection("pictures");main.RevealImported(new[]{video.Id},"");Call(main,"Refresh",true);check(Visible(main).Single().Id==video.Id,"imported video from picture page becomes visible automatically");
     main.SetBookmarkMode(true);main.RevealImported(new[]{pic.Id},webFolder);Call(main,"Refresh",true);check(!((bool)Field(main,"bookmarkMode"))&&Visible(main).Single().Id==pic.Id,"image pasted while browsing websites reveals in image page, not invisible bookmark folder");
     string hint=main.RevealImported(new[]{video.Id,web.Id},"");Call(main,"Refresh",true);check(hint.Contains("网址")&&Visible(main).Single().Id==video.Id,"mixed import reveals actual local type and reports website destination without mixing namespaces");
     main.RevealImported(new[]{web.Id},webFolder);Call(main,"Refresh",true);check(Visible(main).Single().Id==web.Id,"URL import reveals its website folder");
     Set(main,"busy",true);check(!main.RequestNavigate("all")&&!Item(main.CreateMoreMenu(),"导入").IsEnabled&&main.CreateAssetMenu(web).Items.OfType<MenuItem>().All(i=>!i.IsEnabled),"busy app disables conflicting navigation and context operations");Set(main,"busy",false);
     main.SetSection("pictures");Select(main,pic);lib.Trash(new[]{pic.Id},true);Call(main,"Refresh",false);check(((TextBox)main.Window.FindName("Name")).Text==""&&!((Button)main.Window.FindName("Export")).IsEnabled,"external deletion clears stale inspector and export state");lib.Trash(new[]{pic.Id},false);
     check(lib.All().Count==5&&lib.Folders().Count==2&&lib.Db.Query("PRAGMA foreign_key_check").Count==0,"menu and navigation regression preserves all fixture assets and categories");
     check(!main.Window.IsVisible,"menu logic tests never show or interact with user's library");
    }finally{main.Window.Close();ThumbnailImage.CurrentLibrary=null;app.ShutdownMode=previous;}
   }
  }
 }
}
