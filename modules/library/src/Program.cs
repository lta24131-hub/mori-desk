using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;

namespace Mori {
 public partial class Settings {
  public string LibraryPath {get;set;}
  internal bool LoadedFromFile {get;private set;}
  int thumbnailSize=GalleryLayout.DefaultSize;
  public int ThumbnailSize {get{return thumbnailSize;}set{thumbnailSize=GalleryLayout.Clamp(value);}}
  int leftSidebarWidth=SidebarLayout.DefaultLeft,rightSidebarWidth=SidebarLayout.DefaultRight;
  public int LeftSidebarWidth {get{return leftSidebarWidth;}set{leftSidebarWidth=SidebarLayout.ClampLeft(value);}}
  public int RightSidebarWidth {get{return rightSidebarWidth;}set{rightSidebarWidth=SidebarLayout.ClampRight(value);}}
  public bool WaterfallLayout {get;set;}
  public bool ShowAssetNames {get;set;}
  public bool ShowAssetDetails {get;set;}
  public bool LeftSidebarVisible {get;set;}
  public bool RightSidebarVisible {get;set;}
  string theme="dark";
  public string Theme {get{return theme;}set{theme=string.Equals(value,"light",StringComparison.OrdinalIgnoreCase)?"light":"dark";}}
  public static string ConfigDir {get{return MoriDesk.Entry.DataRoot;}}
  public Settings(){LibraryPath=Path.Combine(ConfigDir,"Library");ShowAssetNames=ShowAssetDetails=LeftSidebarVisible=RightSidebarVisible=true;}
  public static Settings Load(){return LoadFrom(Path.Combine(ConfigDir,"settings.json"));}
  public static Settings LoadFrom(string path){
   path=Path.GetFullPath(path);
   using(SettingsFileLock.Acquire(path))return LoadUnlocked(path);
  }
  static Settings LoadUnlocked(string path){
   if(!File.Exists(path)){var empty=new Settings();empty.RememberLoaded(path);return empty;}
   try{
    var values=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
    if(values==null)throw new InvalidDataException("设置内容为空。");
    var settings=new Settings();object value;
    if(!values.TryGetValue("LibraryPath",out value)||!(value is string)||string.IsNullOrWhiteSpace((string)value))throw new InvalidDataException("设置中的图库路径缺失或无效。为避免打开错误图库，已停止启动；请恢复原设置文件。");
    settings.LibraryPath=(string)value;
    if(values.TryGetValue("Theme",out value))settings.Theme=value as string;
    int size;if(values.TryGetValue("ThumbnailSize",out value)&&value!=null&&int.TryParse(Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture),out size))settings.ThumbnailSize=size;
    if(values.TryGetValue("LeftSidebarWidth",out value)&&value!=null&&int.TryParse(Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture),out size))settings.LeftSidebarWidth=size;
    if(values.TryGetValue("RightSidebarWidth",out value)&&value!=null&&int.TryParse(Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture),out size))settings.RightSidebarWidth=size;
    if(values.TryGetValue("WaterfallLayout",out value)&&value is bool)settings.WaterfallLayout=(bool)value;
    if(values.TryGetValue("ShowAssetNames",out value)&&value is bool)settings.ShowAssetNames=(bool)value;
    if(values.TryGetValue("ShowAssetDetails",out value)&&value is bool)settings.ShowAssetDetails=(bool)value;
    if(values.TryGetValue("LeftSidebarVisible",out value)&&value is bool)settings.LeftSidebarVisible=(bool)value;
    if(values.TryGetValue("RightSidebarVisible",out value)&&value is bool)settings.RightSidebarVisible=(bool)value;
    settings.LoadedFromFile=true;settings.RememberLoaded(path);return settings;
   }catch(Exception ex){throw new InvalidDataException("无法读取 Mori 设置，原设置文件已保留："+path+"\n"+ex.Message,ex);}
  }
  public void Save(){SaveTo(Path.Combine(ConfigDir,"settings.json"));}
  public void SaveTo(string path){
   if(string.IsNullOrWhiteSpace(LibraryPath))throw new InvalidDataException("请先选择图库路径。");
   path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path));
   using(SettingsFileLock.Acquire(path)){
   var values=ValuesToSave(path);
   string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{
    byte[] data=new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(values));
    using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.Write(data,0,data.Length);stream.Flush(true);}
    if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);
    ApplySaved(values);RememberLoaded(path);
   }finally{if(File.Exists(temp))File.Delete(temp);}
   }
  }
 }
 public static class Program {
  [STAThread] public static int Main(string[] args) {
   if(args.Length>0&&args[0]=="--svg-render")return SvgRaster.Worker(args);
   if(args.Length>0&&args[0]=="--video-thumbnail")return VideoThumbnail.Worker(args);
   if(args.Length>1&&args[0]=="--video-self-test")return Tests.Media(args[1]);
   if(args.Length>0&&args[0]=="--self-test")return Tests.Run(args.Length>1?args[1]:Path.Combine(Path.GetTempPath(),"Mori-tests-"+Guid.NewGuid().ToString("N")));
   if(args.Length>1&&args[0]=="--benchmark")return Tests.Benchmark(args[1]);
   if(args.Length>1&&args[0]=="--native-test")return NativeBridge.Run(args[1]);
   if(args.Length>0&&args[0].StartsWith("chrome-extension://",StringComparison.Ordinal))return NativeBridge.Run(null,args[0]);
   string handoff=args.Length==4&&args[2]=="--handoff"?args[3]:null;
   try {
    var settings=Settings.Load();bool startup=args.Any(a=>string.Equals(a,"--startup",StringComparison.Ordinal));
    bool preview=args.Length>1&&args[0]=="--ui-test";string root=args.Length>1&&(args[0]=="--library"||preview)?args[1]:settings.LibraryPath;
    if(preview)BookmarkIcons.NetworkEnabled=false;
    string instanceName=InstanceName(root);
    bool owned;using(var mutex=new Mutex(true,instanceName,out owned)){
     if(!owned){if(!startup&&handoff==null&&!TrayHost.ActivateExisting(instanceName+"-activate"))MessageBox.Show("此图库已在 Mori 中打开，请切换到已有窗口。","Mori");return handoff==null?0:1;}
     ValidateStartupLibrary(args,settings);
     var app=new Application();app.DispatcherUnhandledException+=delegate(object sender,System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e){e.Handled=true;MessageBox.Show("操作未完成，原文件没有改动。\n"+e.Exception.Message,"Mori");};
     using(var library=new Library(root)){if(ShouldRememberLibrary(args))Settings.RememberLibrary(root);var main=new MainWindow(library);app.SessionEnding+=delegate{main.AllowSessionEnd();};main.PreviewMode=preview;if(preview)main.Window.Title="Mori · 测试预览";if(handoff!=null)PrepareHandoff(main,handoff,instanceName+"-activate");else main.EnableTray(instanceName+"-activate",startup);app.Run(main.Window);}return 0;
    }
   } catch(Exception ex){if(handoff==null)MessageBox.Show("Mori 无法启动：\n"+ex.Message,"Mori");return 1;}
  }
  static void PrepareHandoff(MainWindow main,string name,string activationName){
   main.DeferDeskServices();
   var activate=EventWaitHandle.OpenExisting(name+"-commit");var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};var clock=System.Diagnostics.Stopwatch.StartNew();
   main.Window.IsEnabled=false;bool released=false;
   Action release=delegate{timer.Stop();if(!released){released=true;activate.Dispose();}};
   EventHandler rendered=null;rendered=delegate{main.Window.ContentRendered-=rendered;try{using(var ready=EventWaitHandle.OpenExisting(name))ready.Set();timer.Start();}catch(WaitHandleCannotBeOpenedException){main.Window.Close();}};main.Window.ContentRendered+=rendered;
   timer.Tick+=delegate{if(activate.WaitOne(0)){release();main.Window.IsEnabled=true;main.EnableTray(activationName,false);main.StartDeskServices();main.Window.Activate();}else if(clock.ElapsedMilliseconds>20000){release();main.CloseForReplacement();}};
   main.Window.Closed+=delegate{release();};
  }
  // Merely opening the current library must not re-save a stale selection from
  // before another window completed a library switch. Handoff commits itself.
  internal static bool ShouldRememberLibrary(string[] args){return args.Length==2&&args[0]=="--library";}
  // A remembered location must never silently turn into a new empty library.
  // First-run and explicit selection retain creation/migration behavior.
  internal static void ValidateStartupLibrary(string[] args,Settings settings){
   if(!(args.Length==0||(args.Length==1&&args[0]=="--startup")))return;
   ValidateConfiguredLibrary(settings);
  }
  internal static void ValidateConfiguredLibrary(Settings settings){
   if(!settings.LoadedFromFile)return;
   string root=Path.GetFullPath(settings.LibraryPath);
   if(!Directory.Exists(root))throw new DirectoryNotFoundException("无法找到或访问已设置的图库文件夹：\n"+root+"\n\n为防止生成空图库，本次操作已停止，现有文件未改动。请连接原磁盘或恢复原目录；若图库已迁移，请重新指定现有图库位置后重试。");
   string database=Path.Combine(root,".mori","library.db");
   if(!File.Exists(database))throw new FileNotFoundException("无法找到或访问图库索引：\n"+database+"\n\n为防止生成空图库，本次操作已停止，现有素材未改动。请从备份恢复此索引，或重新指定包含此索引的原图库后重试。",database);
  }
  internal static string InstanceName(string root){return "Local\\MoriDesk-"+StableHash(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).ToUpperInvariant());}
  static string StableHash(string s){using(var hash=System.Security.Cryptography.SHA256.Create())return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s))).Replace("-","").Substring(0,20);}
 }
}
