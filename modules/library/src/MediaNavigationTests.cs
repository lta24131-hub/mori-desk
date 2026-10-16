using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mori {
 public static class MediaNavigationTests {
  static object Field(MainWindow main,string name){return typeof(MainWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(main);}
  static List<Asset> Visible(MainWindow main){return (List<Asset>)Field(main,"visible");}
  static void Refresh(MainWindow main){typeof(MainWindow).GetMethod("Refresh",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(main,new object[]{true});}
  static bool Same(Brush a,Brush b){return a is SolidColorBrush&&b is SolidColorBrush&&((SolidColorBrush)a).Color==((SolidColorBrush)b).Color;}
  public static void Run(string output,Action<bool,string> check){
   Directory.CreateDirectory(output);
   foreach(string ext in Library.Extensions){var a=new Asset{Ext=ext.ToUpperInvariant()};bool picture=MediaTypes.Pictures.Contains(ext),video=MediaTypes.Videos.Contains(ext),audio=MediaTypes.Audio.Contains(ext),doc=MediaTypes.Documents.Contains(ext);
    check(MainWindow.SectionContains(a,"assets")== (picture||video||audio),"overview inclusion: "+ext);
    check(MainWindow.SectionContains(a,"pictures")==picture&&MainWindow.SectionContains(a,"videos")==video&&MainWindow.SectionContains(a,"audio")==audio&&MainWindow.SectionContains(a,"documents")==doc,"exclusive case-insensitive type classification: "+ext);
   }
   check(!MainWindow.SectionContains(new Asset{Ext=".unknown"},"pictures")&&!MainWindow.SectionContains(null,"assets"),"unknown types never masquerade as pictures");
   var app=Application.Current??new Application();var previous=app.ShutdownMode;app.ShutdownMode=ShutdownMode.OnExplicitShutdown;
   using(var lib=new Library(Path.Combine(output,"library"))){
    string folder=lib.CreateFolder("混合测试分类","");var fixtures=new List<Asset>();
    foreach(string ext in new[]{".png",".gif",".mp4",".pdf"}.Concat(MediaTypes.Audio)){
     string path=Path.Combine(output,"sample"+ext);File.WriteAllText(path,"isolated metadata fixture "+ext);
     fixtures.Add(lib.Import(path,folder,ext,"","","").Item);
    }
    var web=lib.ImportBookmark("https://example.org/media-test","网页","").Item;
    check(Library.ExpandFiles(new[]{output}).Any(p=>p.EndsWith(".mp3")),"file picker/folder enumeration accepts audio formats");
    var main=new MainWindow(lib){PreviewMode=true};
    try{
     var image=fixtures.First(a=>a.Ext==".png");var gif=fixtures.First(a=>a.Ext==".gif");var video=fixtures.First(a=>a.Ext==".mp4");var pdf=fixtures.First(a=>a.Ext==".pdf");var audio=fixtures.First(a=>a.Ext==".mp3");
     check((string)Field(main,"mediaSection")=="assets"&&Visible(main).Count==13&&Visible(main).All(a=>a.Ext!=".pdf"&&!a.IsBookmark),"startup overview combines 2 pictures + 1 video + 10 audio, excludes documents and URLs");
     var names=new[]{"All","Pictures","Videos","Audio","Bookmarks","Documents"};var parent=(Panel)((Button)main.Window.FindName("All")).Parent;
     var expectedNavigation=new[]{"All","Pictures","Videos","Audio","CanvasNav","Bookmarks","Documents"}.Select(name=>(Button)main.Window.FindName(name)).Concat(new[]{(Button)Field(main,"referenceNav"),(Button)main.Window.FindName("Unfiled"),(Button)main.Window.FindName("Favorites")});
     check(parent.Children.OfType<Button>().SequenceEqual(expectedNavigation),"complete navigation order is overview, pictures, videos, audio, projects, websites, documents, local shortcuts, unfiled and favorites");
     check(Convert.ToString(((Button)main.Window.FindName("CanvasNav")).Content)=="项目","canvas navigation is presented as projects without replacing a media filter");
     foreach(string name in names){var button=(Button)main.Window.FindName(name);button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));string section=name=="All"?"assets":name.ToLowerInvariant();
      check((string)Field(main,"mediaSection")==section&&Same(button.Background,Theme.Brush("AccentSoft")),"shortcut and active highlight: "+name);
      check(Visible(main).All(a=>section=="bookmarks"?a.IsBookmark:MainWindow.SectionContains(a,section)),"shortcut has no cross-type leakage: "+name);
     }
     main.SetSection("pictures");check(Visible(main).Count==2&&Visible(main).Any(a=>a.Id==gif.Id),"pictures includes GIF, never video or audio");
     main.SetSection("audio");check(Visible(main).Count==10,"audio section includes each supported local format");
     var selected=(HashSet<string>)Field(main,"selection");selected.Add(audio.Id);Refresh(main);
     check(((TextBlock)main.Window.FindName("SelectionInfo")).Text=="1 个音频已选中"&&((TextBlock)main.Window.FindName("DetailAudioIcon")).Visibility==Visibility.Visible&&!((Button)main.Window.FindName("Similar")).IsEnabled,"audio inspector has music icon and disables image-only operations");
     var audioMenu=main.CreateAssetMenu(audio).Items.OfType<MenuItem>().Select(m=>Convert.ToString(m.Header)).ToArray();check(audioMenu.Count(x=>x=="播放音频")==1&&audioMenu.Count(x=>x=="用默认程序打开")==1,"audio distinguishes built-in playback from one default-program action");
     lib.BuildThumbnail(audio);lib.BuildThumbnail(pdf);check(!File.Exists(lib.ThumbnailPath(audio))&&!File.Exists(lib.ThumbnailPath(pdf)),"audio has no bitmap thumbnail and invalid PDF never caches a placeholder");
     main.RequestNavigate(folder);check(Visible(main).Count==14,"existing project folders retain every local type including PDF");var filter=(ComboBox)main.Window.FindName("MediaFilter");filter.SelectedIndex=4;check(Visible(main).Count==10&&Visible(main).All(a=>a.IsAudio),"project audio filter works");
     ((Button)main.Window.FindName("All")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));check(filter.SelectedIndex==0&&filter.Visibility==Visibility.Collapsed&&Visible(main).Count==13,"overview resets old folder filters");
     main.RevealImported(new[]{audio.Id},"");Refresh(main);check((string)Field(main,"mediaSection")=="audio"&&Visible(main).Count==10,"audio-only imports reveal audio page");
     string hint=main.RevealImported(new[]{image.Id,video.Id,audio.Id,pdf.Id,web.Id},"");Refresh(main);check((string)Field(main,"mediaSection")=="assets"&&Visible(main).Count==13&&hint.Contains("网址")&&hint.Contains("文档"),"mixed imports reveal overview and explain separate website/document destinations");
     lib.Trash(new[]{audio.Id,pdf.Id},true);main.RequestNavigate("trash");check(Visible(main).Count==2,"audio and PDF remain recoverable in existing local recycle bin");lib.Trash(new[]{audio.Id,pdf.Id},false);
     main.SetSection("documents");check(Visible(main).Single().Id==pdf.Id,"documents stay directly accessible after mixed import");
     check(lib.All().Count==15&&lib.Db.Query("PRAGMA foreign_key_check").Count==0&&!main.Window.IsVisible,"navigation preserves all fixtures without opening the real library");
    }finally{main.Window.Close();ThumbnailImage.CurrentLibrary=null;app.ShutdownMode=previous;}
   }
  }
 }
}
