using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
namespace MoriDesk {
 public static class BrowserConnection {
  static BrowserBridgeHost local;
  static System.Windows.Threading.DispatcherTimer retry;
  static bool waiting;
  static bool registrationsRefreshed;
  public static string LocalState {get;private set;}
  static void RecordLocalState(string state,Exception error=null){
   LocalState=state;
   // Record only lifecycle/error codes: never the pairing key, request data,
   // URLs, media names or library paths.
   try{string folder=Path.Combine(Entry.DataRoot,"browser");Directory.CreateDirectory(folder);File.WriteAllText(Path.Combine(folder,"connection-status.log"),DateTime.UtcNow.ToString("O")+" "+state+(error==null?"":" "+error.GetType().Name+" 0x"+error.HResult.ToString("X8")),new UTF8Encoding(false));}catch{}
  }
  public const string Host="app.mori.desk";
  public static string ExtensionDirectory(string executable){return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable)),"browser-extension");}
  public static string ValidateId(string id){id=(id??"").Trim();if(!Regex.IsMatch(id,"\\A[a-p]{32}\\z"))throw new ArgumentException("请粘贴扩展管理页面中 32 位小写字母的 ID，不是网址或插件名称。");return id;}
  internal static string IdFromOrigin(string origin){return Regex.IsMatch(origin??"","\\Achrome-extension://[a-p]{32}/?\\z")?origin.Substring(19,32):"";}
  public static string Manifest(string executable,string id,string existing){
   id=ValidateId(id);var origins=new HashSet<string>(StringComparer.Ordinal);var json=new JavaScriptSerializer();
   if(!string.IsNullOrWhiteSpace(existing)){
    var old=json.Deserialize<Dictionary<string,object>>(existing);object values,name;
    if(old==null||!old.TryGetValue("name",out name)||Convert.ToString(name)!=Host)throw new InvalidDataException("已有连接配置不属于 Mori Desk，未覆盖。请检查配置文件。");
    if(old.TryGetValue("allowed_origins",out values)&&values is IEnumerable)foreach(object value in (IEnumerable)values){var origin=Convert.ToString(value);if(Regex.IsMatch(origin,"\\Achrome-extension://[a-p]{32}/\\z"))origins.Add(origin);}
   }
   origins.Add("chrome-extension://"+id+"/");
   return json.Serialize(new Dictionary<string,object>{{"name",Host},{"description","Mori Desk local library"},{"path",Path.GetFullPath(executable)},{"type","stdio"},{"allowed_origins",origins.OrderBy(x=>x).ToArray()}});
  }
  public static string WriteManifest(string folder,string executable,string id){
   if(!File.Exists(executable))throw new FileNotFoundException("Mori Desk 程序不存在，请从完整安装目录运行。",executable);
   string path=Path.Combine(folder,Host+".json");string data=Manifest(executable,id,File.Exists(path)?File.ReadAllText(path):null);
   Directory.CreateDirectory(folder);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{File.WriteAllText(temp,data,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}return path;
  }
  public static void Register(string id){
   string path=WriteManifest(Path.Combine(Entry.DataRoot,"browser"),typeof(Entry).Assembly.Location,id);
   foreach(string browser in new[]{"Google\\Chrome","Microsoft\\Edge"})using(var key=Registry.CurrentUser.CreateSubKey("Software\\"+browser+"\\NativeMessagingHosts\\"+Host))key.SetValue("",path);
   BrowserLocalBridge.Pair(Path.GetDirectoryName(path),ExtensionDirectory(typeof(Entry).Assembly.Location));
  }
  public static void StartLocal(){
   if(local!=null&&local.Ready)return;
   string folder=Path.Combine(Entry.DataRoot,"browser"),manifest=Path.Combine(folder,Host+".json");
   try{
    // Existing managed IDs may keep their permission while the desktop
    // executable moves to a new version directory. Refresh that known path
    // once; never infer or authorize arbitrary IDs from a browser request.
    if(!registrationsRefreshed){BrowserExtensionManager.RegisterKnown();registrationsRefreshed=true;}
    if(!File.Exists(manifest)){StopLocal();RecordLocalState("unpaired");return;}
    if(local==null){RecordLocalState("starting");string secret=BrowserLocalBridge.Pair(folder,ExtensionDirectory(typeof(Entry).Assembly.Location));BrowserExtensionManager.RefreshPairing();local=new BrowserBridgeHost(()=>new BrowserLocalBridge(BrowserLocalBridge.Port,secret,origin=>BrowserLocalBridge.OriginAllowed(manifest,origin),request=>Mori.NativeBridge.HandleAtCurrentLibrary(request,null)));}
    if(local.TryStart()){
     if(retry!=null){retry.Stop();retry=null;}
     bool recovered=waiting;waiting=false;
     RecordLocalState("listening");
     if(recovered&&DeskRuntime.Notify!=null)DeskRuntime.Notify("浏览器收藏连接已恢复。");
     return;
    }
    if(retry==null){retry=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};retry.Tick+=(s,e)=>StartLocal();retry.Start();}
    if(!waiting){waiting=true;RecordLocalState("waiting-for-port");if(DeskRuntime.Notify!=null)DeskRuntime.Notify("浏览器收藏连接正在等待旧连接释放，会自动恢复。");}
   }
   catch(Exception ex){StopLocal();RecordLocalState("failed",ex);if(DeskRuntime.Notify!=null)DeskRuntime.Notify("浏览器备用连接暂不可用："+ex.Message);}
  }
  public static void StopLocal(){if(retry!=null){retry.Stop();retry=null;}if(local!=null){local.Dispose();local=null;}waiting=false;}
  public static void Show(Window owner,bool preview){
   var w=new Window{Title="Mori Desk · 浏览器收藏",Owner=owner,Width=600,Height=570,MinWidth=480,MinHeight=480,WindowStartupLocation=WindowStartupLocation.CenterOwner};Mori.Theme.Attach(w,owner);
   w.Content=CreateContent(w,preview);w.PreviewKeyDown+=(s,e)=>{if(e.Key==System.Windows.Input.Key.Escape){w.Close();e.Handled=true;}};w.ShowDialog();
  }
  internal static FrameworkElement CreateContent(Window w,bool preview,Func<BrowserExtensionStatus> readStatus=null){
   var p=new StackPanel{Margin=new Thickness(24)};var content=new ScrollViewer{Content=p,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
   Func<string,double,TextBlock> label=(text,size)=>{var t=new TextBlock{Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4,6,4,8),LineHeight=size+8};p.Children.Add(t);return t;};
   label("浏览器收藏",23).FontWeight=FontWeights.SemiBold;
   label("拖拽图片或右键收藏，直接保存到本机图库。",13).SetResourceReference(TextBlock.ForegroundProperty,"Muted");
   var status=label(preview?"测试预览 · 不会更改浏览器":"正在检查插件…",16);status.Margin=new Thickness(4,20,4,4);
   var versions=label("",12);versions.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
   var result=label("",13);result.Visibility=Visibility.Collapsed;
   Action<string> message=text=>{result.Text=text;result.Visibility=string.IsNullOrEmpty(text)?Visibility.Collapsed:Visibility.Visible;};
   bool updateRequested=false;Button updateButton=null;
   string dir=Path.Combine(Entry.DataRoot,"browser","extension");
   var location=new TextBox{Text=dir,IsReadOnly=true,Margin=new Thickness(4,8,4,8),Padding=new Thickness(8),ToolTip="首次加载插件时选择此文件夹"};
   Action refresh=()=>{if(preview&&readStatus==null){versions.Text="连接状态与版本会在这里显示";return;}try{var s=readStatus==null?BrowserExtensionManager.Status():readStatus();status.Text=s.summary;versions.Text=BrowserSettingsPresentation.Versions(s);status.SetResourceReference(TextBlock.ForegroundProperty,BrowserSettingsPresentation.Current(s)?"Accent":"Text");if(updateButton!=null){updateButton.IsEnabled=!preview&&BrowserSettingsPresentation.CanUpdate(s);updateButton.ToolTip=BrowserSettingsPresentation.UpdateHint(s);}if(updateRequested&&BrowserSettingsPresentation.Current(s)){updateRequested=false;message("插件已更新，可以继续收藏，原网页无需刷新。");}if(!string.IsNullOrEmpty(s.path)){dir=s.path;location.Text=dir;}}catch(Exception ex){status.Text="暂时无法检查插件";if(updateButton!=null)updateButton.IsEnabled=false;message(ex.Message);}};
   var actions=new WrapPanel{Margin=new Thickness(0,4,0,12)};p.Children.Add(actions);
   actions.Children.Add(ReferencePage.Button("安装 / 修复",()=>{if(preview){message("测试预览不会安装插件或修改连接。");return;}try{var previous=BrowserExtensionManager.Status();BrowserExtensionStatus s;
    if(previous.requiresBootstrap&&!string.IsNullOrEmpty(previous.legacyId)){
     if(MessageBox.Show(w,"检测到旧版 Mori 插件。首次升级会保留原安装位置和偏好设置，但还需要在扩展管理页手动重新加载一次。\n\n请先完成正在进行的收藏、编辑和书签导入，并关闭其他浏览器。现在升级插件文件吗？","首次升级浏览器插件",MessageBoxButton.OKCancel,MessageBoxImage.Information)!=MessageBoxResult.OK)return;
     s=BrowserExtensionManager.BootstrapLegacy(previous.legacyId);message("插件文件已升级。请在浏览器扩展管理页重新加载 Mori 一次；后续可使用“一键更新”。");
    }else{if(!string.IsNullOrEmpty(previous.installedVersion))BrowserExtensionManager.RequestUpdate();s=BrowserExtensionManager.EnsureInstalled();if(s.pending)updateRequested=true;message(BrowserSettingsPresentation.Current(s)?"连接与文件已检查，插件已是最新版本。":s.pending?"修复更新已准备好，当前收藏完成后会自动应用。请关闭浏览器里的插件弹窗。":"插件已准备好。首次安装请在下面展开安装说明，由浏览器确认加载。");}
    StartLocal();dir=s.path;location.Text=dir;refresh();}catch(Exception ex){message("未完成："+ex.Message);}}));
   updateButton=ReferencePage.Button("一键更新",()=>{if(preview){message("测试预览不会更新插件。");return;}try{var previous=BrowserExtensionManager.Status();if(!BrowserSettingsPresentation.CanUpdate(previous)){message(BrowserSettingsPresentation.UpdateHint(previous));refresh();return;}BrowserExtensionManager.RequestUpdate();StartLocal();updateRequested=true;message("已请求更新。请关闭浏览器里的插件弹窗；当前收藏完成后会自动更新。");refresh();}catch(Exception ex){message("未完成："+ex.Message);}});ToolTipService.SetShowOnDisabled(updateButton,true);actions.Children.Add(updateButton);
   var hint=label("更新只使用当前 Mori 随附的插件，不会下载陌生程序。正在使用的收藏窗口不会被强制关闭。",12);hint.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
   var installBody=new StackPanel{Margin=new Thickness(0,8,0,12)};
   installBody.Children.Add(new TextBlock{Text="首次安装需要浏览器确认一次。\n打开扩展管理，开启开发者模式，点击“加载已解压的扩展程序”，选择下面的文件夹。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4),LineHeight=22});installBody.Children.Add(location);
   var installActions=new WrapPanel();installBody.Children.Add(installActions);
   installActions.Children.Add(ReferencePage.Button("打开插件文件夹",()=>{if(preview)return;try{if(!Directory.Exists(dir))throw new DirectoryNotFoundException("请先点“安装 / 修复”。");Process.Start("explorer.exe",FileRouter.Quote(dir));}catch(Exception ex){message(ex.Message);}}));
   installActions.Children.Add(ReferencePage.Button("打开扩展管理",()=>{if(preview)return;try{Process.Start("chrome.exe","chrome://extensions/");}catch{try{Process.Start("msedge.exe","edge://extensions/");}catch{message("请在 Chrome 或 Edge 菜单中打开“扩展程序”。");}}}));
   p.Children.Add(new Expander{Header="首次安装说明",Content=installBody,Margin=new Thickness(4,16,4,6)});
   var advanced=new StackPanel{Margin=new Thickness(0,8,0,8)};
   advanced.Children.Add(new TextBlock{Text="仅旧版插件需要手动连接。新安装的插件会自动连接。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(4),LineHeight=22});
   var input=new TextBox{Margin=new Thickness(4),Padding=new Thickness(10),MaxLength=64};System.Windows.Automation.AutomationProperties.SetName(input,"Mori Desk 扩展 ID");advanced.Children.Add(input);
   advanced.Children.Add(ReferencePage.Button("连接旧版插件",()=>{try{ValidateId(input.Text);if(preview){message("ID 格式正确。测试未修改连接。");return;}Register(input.Text);BrowserExtensionManager.RefreshPairing();StartLocal();message("连接已保存。旧版首次升级仍需手动重新加载一次，之后可在这里更新。");refresh();}catch(Exception ex){message("未连接："+ex.Message);}}));
   p.Children.Add(new Expander{Header="旧版连接与高级选项",Content=advanced,Margin=new Thickness(4,6,4,6)});
   refresh();
   if(!preview){var timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(2)};timer.Tick+=(s,e)=>refresh();content.Loaded+=(s,e)=>timer.Start();content.Unloaded+=(s,e)=>timer.Stop();w.Closed+=(s,e)=>timer.Stop();}
   return content;
  }
 }
}
