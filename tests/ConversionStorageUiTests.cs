using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Mori;

namespace MoriDesk {
 // Detached storage controls only. No Window, Application, picker, shell,
 // clipboard, running conversion, registry write or personal settings path.
 internal static class ConversionStorageUiTests {
  static void Drain(){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);}
  static IEnumerable<DependencyObject> Tree(DependencyObject root){
   yield return root;
   foreach(object child in LogicalTreeHelper.GetChildren(root)){var value=child as DependencyObject;if(value!=null)foreach(var nested in Tree(value))yield return nested;}
  }
  static Button Button(DependencyObject root,string content){return Tree(root).OfType<Button>().Single(button=>Convert.ToString(button.Content)==content);}
  static TextBox Output(DependencyObject root){return Tree(root).OfType<TextBox>().Single(box=>box.Name=="ConversionOutputLocation");}
  static TextBox LibraryPath(DependencyObject root){return Tree(root).OfType<TextBox>().Single(box=>AutomationProperties.GetName(box)=="默认保存位置");}
  static void Click(Button button){button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Drain();}
  static string Text(DependencyObject root){return string.Join("\n",Tree(root).OfType<TextBlock>().Select(block=>block.Text));}
  static Dictionary<string,object> Values(Settings settings){
   return typeof(Settings).GetProperties(BindingFlags.Public|BindingFlags.Instance).Where(p=>p.CanRead&&p.GetIndexParameters().Length==0).ToDictionary(p=>p.Name,p=>p.GetValue(settings,null));
  }
  static void PreferenceIsolation(string root,Action<bool,string> check){
   string original=Path.Combine(root,"original-results"),newer=Path.Combine(root,"newer-results"),requested=Path.Combine(root,"requested-results");
   var current=new Settings{LibraryPath=Path.Combine(root,"current-library"),FileConversionOutputPath=newer,Theme="light",CloseToTray=false,ConfirmTrash=true};
   SettingsWindow.ApplyConversionOutputPreference(current,original,original);
   check(current.FileConversionOutputPath==newer,"unchanged output field cannot overwrite a newer concurrently saved output preference");
   SettingsWindow.ApplyConversionOutputPreference(current,original,original.ToUpperInvariant());
   check(current.FileConversionOutputPath==newer,"case-only output edit cannot overwrite a newer output preference");
   SettingsWindow.ApplyConversionOutputPreference(current,original,original+Path.DirectorySeparatorChar);
   check(current.FileConversionOutputPath==newer,"adding only a trailing separator does not treat unchanged output as a new destination");
   var before=Values(current);
   SettingsWindow.ApplyConversionOutputPreference(current,original,"  "+requested+Path.DirectorySeparatorChar+"  ");
   var after=Values(current);
   check(current.FileConversionOutputPath==requested,"an explicitly changed output destination is normalized before it is applied");
   check(before.Where(pair=>pair.Key!="FileConversionOutputPath").All(pair=>object.Equals(pair.Value,after[pair.Key])),"changing output affects no library, theme, sidebar, startup or other preference");
   check(!Directory.Exists(original)&&!Directory.Exists(newer)&&!Directory.Exists(requested)&&!Directory.Exists(current.LibraryPath),"applying output preferences does not eagerly create or migrate any destination");
   string accepted=current.FileConversionOutputPath;bool rejected=false;
   try{SettingsWindow.ApplyConversionOutputPreference(current,accepted,"relative-output");}catch(IOException){rejected=true;}
   check(rejected&&current.FileConversionOutputPath==accepted,"invalid output edit leaves the current destination unchanged without a modal error surface");
  }
  static void PreviewAndRestore(string root,Library library,Action<bool,string> check){
   string output=Path.Combine(root,"initial-results"),pending=Path.Combine(root,"pending-results"),newer=Path.Combine(root,"concurrent-results"),next=Path.Combine(root,"later-results");
   string settingsPath=Path.Combine(Settings.ConfigDir,"settings.json");
   new Settings{LibraryPath=library.Root,FileConversionOutputPath=output,Theme="light",CloseToTray=true,ConfirmTrash=true}.Save();
   byte[] originalBytes=File.ReadAllBytes(settingsPath);int saved=0;
   var content=SettingsWindow.CreateContent(null,true,library,request=>saved++,true);
   var outputField=Output(content);var libraryField=LibraryPath(content);
   check(outputField.Text==output&&libraryField.Text==library.Root,"storage UI opens with separate current library and conversion output paths");
   check(AutomationProperties.GetName(outputField)=="转换结果保存位置"&&outputField.IsEnabled&&!outputField.IsReadOnly,"conversion output has a readable accessible name and editable full path");
   check(Text(content).Contains("正在进行的任务不改变位置")&&Text(content).Contains("素材库内的转换副本仍保存在当前图库")&&Text(content).Contains("不移动已有结果"),"storage explanation distinguishes future tool results, library copies and already running tasks");
   check(!Button(content,"保存设置").IsEnabled,"preview save is visibly disabled");
   check(originalBytes.SequenceEqual(File.ReadAllBytes(settingsPath))&&!Directory.Exists(output),"constructing preview does not write preferences or create the output directory");
   outputField.Text=pending;libraryField.Text=Path.Combine(root,"pending-library");
   Click(Button(content,"保存并迁移图库"));
   check(saved==0&&originalBytes.SequenceEqual(File.ReadAllBytes(settingsPath)),"even a synthetic preview save click cannot save or request library migration");
   check(!Directory.Exists(pending)&&!Directory.Exists(libraryField.Text),"editing and attempting preview save creates neither conversion output nor library destination");
   var concurrent=Settings.Load();concurrent.FileConversionOutputPath=newer;concurrent.CloseToTray=false;concurrent.ConfirmTrash=false;concurrent.Save();
   byte[] concurrentBytes=File.ReadAllBytes(settingsPath);
   Click(Button(content,"还原未保存更改"));
   check(outputField.Text==newer&&libraryField.Text==library.Root,"Restore discards pending path edits and rereads the latest concurrently saved output");
   check(Tree(content).OfType<CheckBox>().Single(box=>Convert.ToString(box.Content)=="关闭主窗口时收进托盘").IsChecked==false&&
    Tree(content).OfType<CheckBox>().Single(box=>Convert.ToString(box.Content)=="移到回收站 / 移除快捷收藏前确认").IsChecked==false,"Restore refreshes general preferences from the same latest settings");
   check(concurrentBytes.SequenceEqual(File.ReadAllBytes(settingsPath))&&saved==0,"Restore is read-only and does not request a migration or save");
   outputField.Text=pending;concurrent=Settings.Load();concurrent.FileConversionOutputPath=next;concurrent.Save();
   byte[] latestBytes=File.ReadAllBytes(settingsPath);
   Click(Button(content,"还原未保存更改"));Click(Button(content,"保存设置"));
   check(outputField.Text==next&&latestBytes.SequenceEqual(File.ReadAllBytes(settingsPath))&&saved==0,"repeated Restore rereads current output instead of retaining the first restore snapshot");
   check(!new[]{output,pending,newer,next}.Any(Directory.Exists),"preview and Restore leave all current, pending and future conversion directories uncreated");
   check(Application.Current==null&&PresentationSource.FromVisual(content)==null,"storage interaction checks remain detached with no Application or native window");
  }
  static void Theme(FrameworkElement content,bool dark){
   var resources=new Grid();
   typeof(FileConversionUiTests).GetMethod("ApplyActualTheme",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{resources,dark});
   content.Resources=resources.Resources;
  }
  static void Layout(FrameworkElement content,double width,double height){
   content.Width=width;content.Height=height;content.Measure(new Size(width,height));content.Arrange(new Rect(0,0,width,height));content.UpdateLayout();Drain();content.UpdateLayout();
  }
  static void ReadableLayouts(string root,Library library,Action<bool,string> check){
   string output=Path.Combine(root,"not-created-results","较长的项目名称与转换结果保存位置","exports");
   var preferences=Settings.Load();preferences.FileConversionOutputPath=output;preferences.Save();
   string settingsPath=Path.Combine(Settings.ConfigDir,"settings.json");byte[] before=File.ReadAllBytes(settingsPath);
   foreach(bool dark in new[]{false,true})foreach(int width in new[]{480,620}){
    var content=(ScrollViewer)SettingsWindow.CreateContent(null,true,library,request=>{throw new Exception("Detached rendering cannot request migration.");},true);
    Theme(content,dark);Layout(content,width,440);
    var field=Output(content);string state=width+"px "+(dark?"dark":"light");
    check(content.ScrollableHeight>0&&content.ViewportHeight>0&&content.ExtentWidth<=content.ViewportWidth+1,"storage settings remains vertically reachable without horizontal page clipping at "+state);
    check(field.ActualWidth>=200&&field.ActualHeight>=24&&field.Text==output,"conversion path remains a full, readable editable field at "+state);
    check(field.TranslatePoint(new Point(),content).X>=0&&field.TranslatePoint(new Point(field.ActualWidth,0),content).X<=width,"conversion field and browse layout stay within the available width at "+state);
    var foreground=field.Foreground as SolidColorBrush;var background=field.Background as SolidColorBrush;
    check(foreground!=null&&background!=null&&foreground.Color!=background.Color,"path foreground and background retain distinct real-theme colors at "+state);
    // Long editable paths scroll inside their field; no truncation of the value.
    field.CaretIndex=field.Text.Length;field.ScrollToHorizontalOffset(double.MaxValue);field.UpdateLayout();
    check(field.Text==output&&field.CaretIndex==output.Length,"long destination remains navigable to its final folder at "+state);
    content.ScrollToVerticalOffset(Math.Max(0,field.TranslatePoint(new Point(),content).Y-80));Drain();content.UpdateLayout();
    var bitmap=new RenderTargetBitmap(width,440,96,96,PixelFormats.Pbgra32);bitmap.Render(content);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using(var stream=File.Create(Path.Combine(root,"storage-paths-"+width+"-"+(dark?"dark":"light")+".png")))encoder.Save(stream);
    content.ScrollToEnd();Drain();content.UpdateLayout();
    check(content.VerticalOffset>=content.ScrollableHeight-1,"Restore and Save remain reachable at the bottom of storage settings at "+state);
    check(Application.Current==null&&PresentationSource.FromVisual(content)==null,"storage preview rendering creates no desktop surface at "+state);
   }
   check(before.SequenceEqual(File.ReadAllBytes(settingsPath))&&!Directory.Exists(output)&&!Directory.Exists(Path.Combine(root,"not-created-results")),"rendering and scrolling storage settings neither saves preferences nor creates future output directories");
  }
  internal static void Run(string root,Action<bool,string> check){
   root=Path.GetFullPath(root);string expected=Path.Combine(root,"isolated-user-data");
   if(!string.Equals(Path.GetFullPath(Settings.ConfigDir),expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Storage UI tests require their explicit isolated settings root.");
   if(Application.Current!=null)throw new InvalidOperationException("Storage UI tests must run without Application or desktop windows.");
   Directory.CreateDirectory(root);PreferenceIsolation(root,check);
   using(var library=new Library(Path.Combine(root,"fixture-library"))){PreviewAndRestore(root,library,check);ReadableLayouts(root,library,check);}
  }
 }
}
