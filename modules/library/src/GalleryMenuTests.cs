using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Mori {
 public static class GalleryMenuTests {
  static List<T> Children<T>(DependencyObject root)where T:DependencyObject{var list=new List<T>();for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var c=VisualTreeHelper.GetChild(root,i);if(c is T)list.Add((T)c);list.AddRange(Children<T>(c));}return list;}
  static MenuItem Item(ItemsControl menu,string title){return menu.Items.OfType<MenuItem>().Single(i=>Convert.ToString(i.Header)==title);}
  static void Click(MenuItem item){item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);string settingsPath=Path.Combine(output,"preferences.json");
   var settings=new Settings{LibraryPath=output,WaterfallLayout=true,ShowAssetNames=false,ShowAssetDetails=false,LeftSidebarVisible=false,RightSidebarVisible=false,LeftSidebarWidth=330};settings.SaveTo(settingsPath);settings=Settings.LoadFrom(settingsPath);
   check(settings.WaterfallLayout&&!settings.ShowAssetNames&&!settings.ShowAssetDetails&&!settings.LeftSidebarVisible&&!settings.RightSidebarVisible&&settings.LeftSidebarWidth==330,"gallery view preferences round trip without losing widths");
   File.WriteAllText(settingsPath,new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{LibraryPath=output,WaterfallLayout="invalid",ShowAssetNames=(object)null}));settings=Settings.LoadFrom(settingsPath);check(!settings.WaterfallLayout&&settings.ShowAssetNames&&settings.ShowAssetDetails&&settings.LeftSidebarVisible&&settings.RightSidebarVisible,"legacy and malformed view preferences use safe visible defaults");
   var assets=Enumerable.Range(0,10000).Select(i=>new Asset{Id=i.ToString(),Ext=".png",Name="素材 "+i,Width=200+i%7*200,Height=200+i%5*300}).ToList();
   foreach(double width in new[]{40.0,540,1000,1920}){
    var watch=System.Diagnostics.Stopwatch.StartNew();var rows=GalleryLayout.Build(assets,width,210,true,true,true);watch.Stop();var tiles=rows.SelectMany(r=>r.Items).ToList();
    check(tiles.Select(t=>t.Asset.Id).Distinct().Count()==assets.Count&&rows.All(r=>r.Height>0&&r.Height<=600),"waterfall slices cover every asset with bounded virtual rows at "+width);
    check(tiles.All(t=>t.X>=0&&t.X+t.Width+12<=width+.001&&t.Y<600&&t.ImageHeight>0),"waterfall columns remain within viewport at "+width);
    var originals=tiles.Where(t=>t.Y>=0).ToList();check(originals.Count==assets.Count,"cross-slice duplicate fragments retain one logical tile per asset at "+width);
    check(watch.ElapsedMilliseconds<1500,"10000-item waterfall metadata layout is bounded at "+width);
    File.AppendAllText(Path.Combine(output,"timings.txt"),width+" px: "+watch.ElapsedMilliseconds+" ms\n");
    // Sum the visible pieces: no holes and no duplicate vertical coverage.
    var heights=new Dictionary<string,double>();double globalY=0;var placements=new Dictionary<string,Rect>();
    foreach(var row in rows){foreach(var tile in row.Items){double tileHeight=tile.ImageHeight+54;double fragment=Math.Max(0,Math.Min(row.Height,tile.Y+tileHeight)-Math.Max(0,tile.Y));double old;heights.TryGetValue(tile.Asset.Id,out old);heights[tile.Asset.Id]=old+fragment;if(tile.Y>=0)placements[tile.Asset.Id]=new Rect(tile.X,globalY+tile.Y,tile.Width+12,tileHeight);}globalY+=row.Height;}
    check(originals.All(t=>Math.Abs(heights[t.Asset.Id]-(t.ImageHeight+54))<.001),"waterfall virtual slices paint every tile exactly once at "+width);
    check(placements.Values.GroupBy(r=>r.X).All(g=>g.OrderBy(r=>r.Y).Zip(g.OrderBy(r=>r.Y).Skip(1),(a,b)=>a.Bottom<=b.Top+.001).All(ok=>ok)),"waterfall shortest columns have no overlap at "+width);
   }
   var names=GalleryLayout.Build(assets.Take(8).ToList(),900,210,false,true,true);var hidden=GalleryLayout.Build(assets.Take(8).ToList(),900,210,false,false,false);
   check(Math.Abs(names.Sum(r=>r.Height)-hidden.Sum(r=>r.Height)-names.Count*40)<.001&&hidden.SelectMany(r=>r.Items).All(t=>t.LabelHeight==0),"hiding information reclaims vertical space without changing image size");
   var app=Application.Current??new Application();var shutdown=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   using(var library=new Library(Path.Combine(output,"library"))){
    // Visible test fixtures are generated locally; never use personal originals.
    for(int i=0;i<16;i++){string path=Path.Combine(output,"sample-"+i+".png");using(var bmp=new System.Drawing.Bitmap(220+i%4*140,200+i%3*200)){using(var g=System.Drawing.Graphics.FromImage(bmp)){g.Clear(System.Drawing.Color.FromArgb(80+i*7,110+i*4,135+i*3));g.DrawEllipse(System.Drawing.Pens.White,20,20,bmp.Width-40,bmp.Height-40);}bmp.Save(path,System.Drawing.Imaging.ImageFormat.Png);}var a=library.Import(path,"","示例 "+i,"","","").Item;library.BuildThumbnail(a);}
    var main=new MainWindow(library){PreviewMode=true};
    try{
     var content=(FrameworkElement)main.Window.Content;content.Measure(new Size(1380,900));content.Arrange(new Rect(0,0,1380,900));content.UpdateLayout();
     var menu=main.CreateGalleryMenu();check(menu.Items.OfType<MenuItem>().Count()==7&&Item(menu,"刷新").InputGestureText=="F5","blank menu exposes sorting, information, two layouts, two panels and refresh");
     var sort=Item(menu,"排列方式");Click(sort.Items[2] as MenuItem);check(((ComboBox)main.Window.FindName("Sort")).SelectedIndex==2&&((MenuItem)Item(main.CreateGalleryMenu(),"排列方式").Items[2]).IsChecked,"context sorting stays synchronized with toolbar and menu checks");
     var gallery=(ListBox)main.Window.FindName("Gallery");var sidebar=(SidebarLayout)typeof(MainWindow).GetField("sidebars",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(main);int before=sidebar.Left;
     main.ToggleSidebar(true);content.UpdateLayout();check(!sidebar.LeftVisible&&((Grid)main.Window.FindName("Workspace")).ColumnDefinitions[0].ActualWidth==0&&sidebar.Left==before,"hide left panel reclaims width without forgetting user width");
     main.ToggleSidebar(true);main.ToggleSidebar(false);content.UpdateLayout();check(sidebar.LeftVisible&&!sidebar.RightVisible&&sidebar.Left==before,"both sidebar switches are independent");main.ToggleSidebar(false);
     main.SetGalleryLayout(true);main.SetGalleryInformation(false,false);content.UpdateLayout();check(Item(main.CreateGalleryMenu(),"瀑布布局").IsChecked&&!Item(Item(main.CreateGalleryMenu(),"显示信息"),"文件名").IsChecked,"layout and information controls update check state");
     gallery.ItemsSource=GalleryLayout.Build(assets,850,210,true,false,false);content.UpdateLayout();check(Children<Border>(gallery).Count(b=>b.Tag is Asset)<150&&VirtualizingPanel.GetIsVirtualizing(gallery),"10000 masonry assets materialize bounded visible fragments only");
     main.SetGalleryLayout(false);main.SetGalleryInformation(true,true);
     var cached=library.All().First();string thumbnail=library.ThumbnailPath(cached);byte[] thumbnailBytes=File.ReadAllBytes(thumbnail);DateTime thumbnailTime=File.GetLastWriteTimeUtc(thumbnail);string retryStamp=ThumbnailBuildGate.SourceStamp(library.FullPath(cached));
     ThumbnailBuildGate.RememberDecodeFailure(retryStamp);VideoThumbnail.FailureCache.Remember(retryStamp,DateTime.UtcNow);
     var busy=typeof(MainWindow).GetField("busy",BindingFlags.Instance|BindingFlags.NonPublic);busy.SetValue(main,true);try{main.RefreshGallery();}finally{busy.SetValue(main,false);}
     check(ThumbnailBuildGate.HasDecodeFailure(retryStamp)&&VideoThumbnail.FailureCache.Has(retryStamp,DateTime.UtcNow),"busy refresh does not invalidate caches or disturb in-progress thumbnail work");
     main.RefreshGallery();check(((TextBlock)main.Window.FindName("Status")).Text.StartsWith("已刷新")&&library.All().Count==16,"refresh uses real production path and preserves original assets");
     check(!ThumbnailBuildGate.HasDecodeFailure(retryStamp)&&!VideoThumbnail.FailureCache.Has(retryStamp,DateTime.UtcNow),"F5 production refresh explicitly allows missing picture and video thumbnails to retry");
     library.BuildThumbnail(cached);check(File.GetLastWriteTimeUtc(thumbnail)==thumbnailTime&&File.ReadAllBytes(thumbnail).SequenceEqual(thumbnailBytes),"retrying failed thumbnails preserves successful disk cache bytes and timestamps");
     foreach(string theme in new[]{"dark","light"}){Theme.Set(theme);var m=main.CreateGalleryMenu();var entry=Item(m,"排列方式");entry.Style=(Style)main.Window.Resources[typeof(MenuItem)];entry.ApplyTemplate();check(entry.Template.FindName("PART_Popup",entry) is Popup,theme+" context menu template supports actual nested submenus");}
     check(!main.Window.IsVisible,"gallery menu regression never brings a test window to foreground");
    }finally{main.Window.Close();ThumbnailImage.CurrentLibrary=null;Theme.Set("dark");app.ShutdownMode=shutdown;}
   }
  }
 }
}
