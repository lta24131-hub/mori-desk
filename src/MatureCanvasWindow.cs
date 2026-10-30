using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Mori;

namespace MoriDesk {
 // This host is exclusively for bundled, pinned, non-commercial canvas code.
 // It never shares a bridge or a browser profile with remote webpages.
 public sealed class MatureCanvasWindow : Window {
  internal const string Origin="https://nook-canvas.local",Protocol="nook-canvas-v1";
  readonly string dataRoot;
  readonly MatureCanvasStore store;
  readonly FreeCanvasStore legacy;
  readonly TextBlock feedback=new TextBlock();
  readonly TextBlock heading=new TextBlock();
  readonly List<Button> projectButtons=new List<Button>();
  Button pinButton,toolsButton,maximizeButton;
  readonly Grid viewport=new Grid();
  readonly SemaphoreSlim operations=new SemaphoreSlim(1,1);
  readonly object resourceGate=new object();
  readonly Dictionary<string,TaskCompletionSource<bool>> flushes=new Dictionary<string,TaskCompletionSource<bool>>();
  readonly Dictionary<string,TaskCompletionSource<int>> imageImports=new Dictionary<string,TaskCompletionSource<int>>();
  readonly TaskCompletionSource<bool> firstLoad=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
  readonly Action changed;
  readonly Func<Settings> preferencesProvider;
  readonly Action openSettings;
  readonly string[] startingPaths;
  MatureCanvasDocument document;
  TaskCompletionSource<bool> loadCompletion;
  WebView2 web;
  string session=Guid.NewGuid().ToString("N");
  int revision;
  bool ready,loaded,disposed,initializing,transitioning,startingImagesApplied,toolsVisible=true,defaultsPending;
  public bool PreparedForExit {get;private set;}
  public bool CanImportImages {get{return ready&&loaded&&!transitioning&&!disposed&&!PreparedForExit;}}
  public string ProjectId {get{return document.Id;}}
  internal bool ReadyForTests {get{return ready;}}
  internal int RevisionForTests {get{return revision;}}
  internal string SessionForTests {get{return session;}}
  internal MatureCanvasDocument DocumentForTests {get{return document;}}
  internal Action<string> PostForTests {get;set;}
  internal Func<Task> LoadNoticeDelayForTests {get;set;}
  public static bool IsAvailable {get{return File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"web","canvas","index.html"));}}
  static JavaScriptSerializer Json(){return new JavaScriptSerializer{MaxJsonLength=128*1024*1024,RecursionLimit=64};}
  static Dictionary<string,object> Map(object value){var map=value as Dictionary<string,object>;if(map==null)throw new InvalidDataException("画布消息格式无效。");return map;}
  static string Text(Dictionary<string,object> map,string name){object value;return map.TryGetValue(name,out value)?value as string:null;}
  internal static bool TrustedSource(string source){Uri uri;return Uri.TryCreate(source,UriKind.Absolute,out uri)&&uri.Scheme=="https"&&uri.Host=="nook-canvas.local"&&uri.IsDefaultPort&&uri.UserInfo.Length==0;}
  internal static bool ValidRequestId(string id){return id!=null&&Regex.IsMatch(id,"^[a-zA-Z0-9_-]{1,100}$",RegexOptions.CultureInvariant);}

  public MatureCanvasWindow(string dataRoot,string id=null,string[] paths=null,Action changed=null,Func<Settings> preferencesProvider=null,Action openSettings=null,bool isNewProject=false){
   if(!IsAvailable)throw new FileNotFoundException("成熟画布组件未打包，请使用完整的 Nook 安装包。");
   this.dataRoot=dataRoot;this.changed=changed;this.preferencesProvider=preferencesProvider??(()=>new Settings());this.openSettings=openSettings;startingPaths=paths==null?new string[0]:paths.ToArray();
   store=new MatureCanvasStore(dataRoot);legacy=new FreeCanvasStore(dataRoot);
   if(id==null)id=legacy.LastOpenedId;
   document=id==null?CreateDocument():(store.Exists(id)?store.Load(id):store.CreateFromLegacy(legacy.Load(id)));
   defaultsPending=defaultsPending||isNewProject;
   Title="Nook · "+document.Name;Width=1220;Height=820;MinWidth=820;MinHeight=480;
   WindowStartupLocation=WindowStartupLocation.CenterScreen;InitialWindowPlacement.Attach(this);Theme.Attach(this,null);Topmost=document.Pinned;UseLayoutRounding=true;
   WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResize;
   WindowChrome.SetWindowChrome(this,new WindowChrome{CaptionHeight=36,ResizeBorderThickness=new Thickness(5),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(0),UseAeroCaptionButtons=false});
   var root=new Grid();root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(36)});root.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   var toolbar=new DockPanel{Name="CanvasTitlebar",LastChildFill=true,AllowDrop=true};toolbar.SetResourceReference(Panel.BackgroundProperty,"Surface");
   var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Right);toolbar.Children.Add(actions);
   WindowChrome.SetIsHitTestVisibleInChrome(actions,true);
   AddButton(actions,"image","添加图片",()=>PickImages(),"添加图片 · 也可直接拖入画布");
   AddButton(actions,"plus","新建画布",()=>CreateNew(),"新建画布 · 当前项目会先保存");
   toolsButton=AddButton(actions,"eye","隐藏工具",()=>SetToolsVisible(!toolsVisible),"隐藏绘图工具和属性面板");
   AddButton(actions,"more","画布菜单",ShowProjectMenu,"保存、重命名、复制与画布设置");
   var divider=new Border{Width=1,Height=16,Margin=new Thickness(7,0,7,0),VerticalAlignment=VerticalAlignment.Center};divider.SetResourceReference(Border.BackgroundProperty,"Divider");actions.Children.Add(divider);
   pinButton=AddButton(actions,"pin","置顶画布",()=>SetPinned(!Topmost),"置顶画布");
   AddButton(actions,"minimize","最小化",()=>WindowState=WindowState.Minimized,"最小化",false,42);
   maximizeButton=AddButton(actions,"maximize","最大化",()=>WindowState=WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized,"最大化 / 还原",false,42);
   AddButton(actions,"close","关闭画布",Close,"关闭画布 · 自动保存",false,42);
   var title=new DockPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(12,0,12,0)};
   var brand=new Image{Source=Icon,Width=15,Height=15,Margin=new Thickness(0,0,8,0)};DockPanel.SetDock(brand,Dock.Left);title.Children.Add(brand);
   heading.Text="Nook · "+document.Name;heading.FontSize=12;heading.VerticalAlignment=VerticalAlignment.Center;heading.TextTrimming=TextTrimming.CharacterEllipsis;heading.SetResourceReference(TextBlock.ForegroundProperty,"Text");title.Children.Add(heading);toolbar.Children.Add(title);root.Children.Add(toolbar);
   // Native OLE data must be captured synchronously at Drop, before any await.
   toolbar.DragOver+=DropOver;toolbar.Drop+=DropIntoCanvas;
   Grid.SetRow(viewport,1);root.Children.Add(viewport);
   feedback.Visibility=Visibility.Collapsed;feedback.Margin=new Thickness(12,5,12,6);feedback.FontSize=11;feedback.TextWrapping=TextWrapping.Wrap;feedback.SetResourceReference(TextBlock.ForegroundProperty,"Muted");Grid.SetRow(feedback,2);root.Children.Add(feedback);
   var frame=new Border{Child=root,BorderThickness=new Thickness(1)};frame.SetResourceReference(Border.BorderBrushProperty,"Divider");Content=frame;
   StateChanged+=(s,e)=>{maximizeButton.Content=CanvasIcons.Create(WindowState==WindowState.Maximized?"restore":"maximize",maximizeButton,14);System.Windows.Automation.AutomationProperties.SetName(maximizeButton,WindowState==WindowState.Maximized?"还原窗口":"最大化");frame.Margin=WindowState==WindowState.Maximized?new Thickness(7):new Thickness(0);};
   RefreshControls();Status("正在启动画布… 可点击右上角 × 取消，稍后重新打开重试。");
   Loaded+=async(s,e)=>{await Initialize();};Closing+=ClosingCanvas;Closed+=(s,e)=>DisposeCanvas();Theme.Changed+=ThemeChanged;
  }
  Button AddButton(Panel panel,string icon,string title,Action action,string hint,bool project=true,double width=34){
   var button=new Button{Width=width,Height=34,Margin=new Thickness(1,1,1,1),Padding=new Thickness(0),ToolTip=hint,Style=CaptionButtonStyle()};button.Content=CanvasIcons.Create(icon,button,16);
   System.Windows.Automation.AutomationProperties.SetName(button,title);ToolTipService.SetInitialShowDelay(button,500);button.Click+=(s,e)=>action();panel.Children.Add(button);if(project)projectButtons.Add(button);return button;
  }
  static Style CaptionButtonStyle(){
   var style=new Style(typeof(Button));style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Text")));style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));var border=new FrameworkElementFactory(typeof(Border));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(5));border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(Control.BackgroundProperty));border.SetValue(Border.BorderBrushProperty,new TemplateBindingExtension(Control.BorderBrushProperty));border.SetValue(Border.BorderThicknessProperty,new TemplateBindingExtension(Control.BorderThicknessProperty));var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);style.Setters.Add(new Setter(Control.TemplateProperty,new ControlTemplate(typeof(Button)){VisualTree=border}));
   var selected=new Trigger{Property=FrameworkElement.TagProperty,Value=true};selected.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("AccentSoft")));selected.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("Accent")));style.Triggers.Add(selected);
   var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Control.BackgroundProperty,new DynamicResourceExtension("Hover")));style.Triggers.Add(hover);
   var focus=new Trigger{Property=UIElement.IsKeyboardFocusedProperty,Value=true};focus.Setters.Add(new Setter(Control.BorderBrushProperty,new DynamicResourceExtension("Accent")));focus.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));style.Triggers.Add(focus);
   var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.4));style.Triggers.Add(disabled);return style;
  }
  void RefreshControls(){foreach(var button in projectButtons)button.IsEnabled=CanImportImages;if(pinButton!=null){pinButton.Tag=Topmost;pinButton.ToolTip=Topmost?"取消置顶":"置顶画布";System.Windows.Automation.AutomationProperties.SetName(pinButton,Topmost?"取消置顶":"置顶画布");}if(toolsButton!=null){toolsButton.Tag=!toolsVisible;toolsButton.ToolTip=toolsVisible?"隐藏绘图工具和属性面板":"显示绘图工具和属性面板";System.Windows.Automation.AutomationProperties.SetName(toolsButton,toolsVisible?"隐藏工具":"显示工具");}}
  void SetToolsVisible(bool visible){if(!CanImportImages)return;toolsVisible=visible;RefreshControls();Post("toolVisibility",null,new{visible=visible});}
  MatureCanvasDocument CreateDocument(){var next=store.Create("画布 "+(legacy.List().Count+1));var preferences=preferencesProvider();if(preferences.CanvasDefaultTopmost){next.Pinned=true;store.Save(next);}defaultsPending=true;return next;}
  void ShowCanvasSettings(){if(openSettings!=null){openSettings();return;}var dialog=new Window{Owner=this,Title="Nook · 画布设置预览",Width=660,Height=650,WindowStartupLocation=WindowStartupLocation.CenterOwner};Theme.Attach(dialog,this);dialog.Content=new CanvasSettingsPanel(dialog,true,preferencesProvider());dialog.ShowDialog();}
  void Status(string message){feedback.Text=message;bool quiet=message=="已保存"||message.StartsWith("自动保存",StringComparison.Ordinal)||message.StartsWith("正在编辑",StringComparison.Ordinal)||message.StartsWith("已添加",StringComparison.Ordinal);feedback.Visibility=quiet?Visibility.Collapsed:Visibility.Visible;}
  void Transition(bool value){transitioning=value;RefreshControls();if(ready&&loaded)try{Post("interaction",null,new{enabled=!value});}catch(Exception error){Status("画布连接暂不可用："+error.Message+" 已保存内容保留。");}if(web!=null)web.IsEnabled=!value;}
  void NotifyChanged(){if(changed==null)return;try{changed();}catch{Status("画布已保存，但项目列表暂未刷新；可以重新打开项目列表。");}}
  async Task Initialize(){
   if(initializing||disposed)return;initializing=true;
   try{
    web=new WebView2{AllowExternalDrop=true};viewport.Children.Add(web);
    var options=new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update");options.AllowSingleSignOnUsingOSPrimaryAccount=false;options.AreBrowserExtensionsEnabled=false;
    var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(dataRoot,"canvas-webview-profile"),options);
    if(disposed)return;await web.EnsureCoreWebView2Async(environment);if(disposed)return;
    Configure(web.CoreWebView2);
    // Do not also install a virtual-folder mapping for this host. WebView2
    // resolves mapped hosts before WebResourceRequested, which would turn the
    // dynamic /resource/... URLs into nonexistent files and hide every image.
    // The same bounded local-only route serves both the app and its previews.
    web.CoreWebView2.Navigate(Origin+"/index.html");
   }catch(Exception error){EngineFailed("画布未启动，原项目完整保留："+error.Message);}
  }
  void EngineFailed(string message){ready=false;loaded=false;firstLoad.TrySetResult(false);RefreshControls();if(loadCompletion!=null)loadCompletion.TrySetResult(false);Status(message);foreach(var waiter in flushes.Values)waiter.TrySetResult(false);foreach(var waiter in imageImports.Values)waiter.TrySetResult(-1);}
  void Configure(CoreWebView2 core){
   core.Settings.AreHostObjectsAllowed=false;core.Settings.AreDevToolsEnabled=false;core.Settings.IsStatusBarEnabled=false;
   core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
   core.Settings.AreBrowserAcceleratorKeysEnabled=false;core.Settings.IsWebMessageEnabled=true;
   core.NavigationStarting+=(s,e)=>{if(!TrustedSource(e.Uri)||new Uri(e.Uri).AbsolutePath!="/index.html")e.Cancel=true;};
   core.FrameNavigationStarting+=(s,e)=>{e.Cancel=true;};core.NewWindowRequested+=(s,e)=>{e.Handled=true;Uri uri;if(!e.IsUserInitiated||!SafeExternalUrl(e.Uri,out uri))return;try{Process.Start(new ProcessStartInfo(uri.AbsoluteUri){UseShellExecute=true});}catch(Exception error){Status("网页未打开："+error.Message);}};
   core.LaunchingExternalUriScheme+=(s,e)=>{e.Cancel=true;};
   core.PermissionRequested+=(s,e)=>{e.State=CoreWebView2PermissionState.Deny;e.Handled=true;};
   core.DownloadStarting+=(s,e)=>{e.Cancel=true;e.Handled=true;Status("画布下载已停用，项目会自动保存到本机。");};
   core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
   core.WebResourceRequested+=ServeResource;
   core.WebMessageReceived+=Receive;
   core.NavigationCompleted+=(s,e)=>{if(!e.IsSuccess)EngineFailed("画布未载入，原项目完整保留；请关闭后重新打开。");};
   core.ProcessFailed+=(s,e)=>EngineFailed("画布进程停止，已保存项目不受影响；请关闭后重新打开。未保存内容没有被覆盖。");
  }
  internal static bool SafeExternalUrl(string source,out Uri uri){return Uri.TryCreate(source,UriKind.Absolute,out uri)&&(uri.Scheme=="https"||uri.Scheme=="http")&&uri.UserInfo.Length==0&&source.Length<=8192;}
  internal sealed class CanvasResourceReply {
   internal int StatusCode;internal byte[] Bytes;internal string ContentType,Error;internal bool Dynamic;
   internal string CacheControl {get{return StatusCode==200&&Dynamic?"private, max-age=86400":"no-store";}}
  }
  internal static string BundledCanvasFile(string root,string path,out string contentType){
   contentType=null;
   // The bundle has only known, ASCII asset names. Reject encoded separators,
   // dot segments, alternate streams and UNC paths before filesystem access.
   if(path==null||path.Length>1024||!Regex.IsMatch(path,@"^/(?:index\.html|(?:assets|tl-assets|licenses)/[A-Za-z0-9_./-]+)$",RegexOptions.CultureInvariant))return null;
   var parts=path.Substring(1).Split('/');if(parts.Any(part=>part.Length==0||part=="."||part==".."))return null;
   switch(Path.GetExtension(path).ToLowerInvariant()){
    case ".html":contentType="text/html; charset=utf-8";break;case ".js":contentType="application/javascript; charset=utf-8";break;case ".css":contentType="text/css; charset=utf-8";break;case ".json":contentType="application/json; charset=utf-8";break;
    case ".svg":contentType="image/svg+xml";break;case ".png":contentType="image/png";break;case ".jpg":case ".jpeg":contentType="image/jpeg";break;case ".webp":contentType="image/webp";break;case ".gif":contentType="image/gif";break;
    case ".woff2":contentType="font/woff2";break;case ".woff":contentType="font/woff";break;case ".ttf":contentType="font/ttf";break;case ".otf":contentType="font/otf";break;
    case ".txt":case ".md":contentType="text/plain; charset=utf-8";break;case "":if(!path.StartsWith("/licenses/",StringComparison.Ordinal))return null;contentType="text/plain; charset=utf-8";break;default:return null;
   }
   string folder=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   string file=Path.GetFullPath(Path.Combine(folder,path.Substring(1).Replace('/',Path.DirectorySeparatorChar)));
   if(!file.StartsWith(folder,StringComparison.OrdinalIgnoreCase))return null;
   for(string current=file;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("画布组件路径包含链接，不能安全读取。");
   return file;
  }
  internal CanvasResourceReply ReadCanvasResource(string source,string method="GET"){
   var reply=new CanvasResourceReply{StatusCode=404,ContentType="text/plain; charset=utf-8"};
   try{
    if(source==null||source.Length>8192)return reply;
    Uri uri;if(!Uri.TryCreate(source,UriKind.Absolute,out uri))return reply;if(!TrustedSource(source)){reply.StatusCode=403;return reply;}if(method!="GET"){reply.StatusCode=405;return reply;}if(uri.Query.Length>0||uri.Fragment.Length>0||source.IndexOf('%')>=0)return reply;
    int pathStart=source.IndexOf('/',source.IndexOf("://",StringComparison.Ordinal)+3);if(pathStart<0||source.Substring(pathStart)!=uri.AbsolutePath)return reply;
    reply.Dynamic=uri.AbsolutePath.StartsWith("/resource/",StringComparison.Ordinal);
    if(reply.Dynamic){
     var parts=uri.AbsolutePath.Split('/');lock(resourceGate){if(parts.Length!=4||parts[2]!=document.Id)return reply;reply.Bytes=store.ReadFile(document,parts[3]);}reply.ContentType="image/png";
    }else{
     string type,file=BundledCanvasFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"web","canvas"),uri.AbsolutePath,out type);if(file==null||!File.Exists(file))return reply;
     using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read)){
      if(input.Length<=0||input.Length>32L*1024*1024)throw new InvalidDataException("画布组件为空或过大。");reply.Bytes=new byte[(int)input.Length];int offset=0,read;while(offset<reply.Bytes.Length&&(read=input.Read(reply.Bytes,offset,reply.Bytes.Length-offset))>0)offset+=read;if(offset!=reply.Bytes.Length||input.ReadByte()!=-1)throw new IOException("画布组件读取时发生变化。");
     }reply.ContentType=type;
    }
    reply.StatusCode=200;return reply;
   }catch(Exception error){reply.Bytes=null;reply.Error=error.Message;return reply;}
  }
  async void ServeResource(object sender,CoreWebView2WebResourceRequestedEventArgs e){
   // Capture COM-backed request properties on the WebView UI thread before
   // entering the worker. Only local bytes/PNG decoding run in the background.
   string source=e.Request.Uri,method=e.Request.Method;Uri uri;if(Uri.TryCreate(source,UriKind.Absolute,out uri)&&(uri.Scheme=="data"||uri.Scheme=="blob"))return;
   var deferral=e.GetDeferral();
   try{
    var reply=await Task.Run(()=>ReadCanvasResource(source,method));if(disposed||web==null||web.CoreWebView2==null)return;
    string reason=reply.StatusCode==200?"OK":reply.StatusCode==403?"Forbidden":reply.StatusCode==405?"Method Not Allowed":"Not Found";
    string headers="Content-Type: "+reply.ContentType+"\r\nCache-Control: "+reply.CacheControl+"\r\nX-Content-Type-Options: nosniff";
    e.Response=web.CoreWebView2.Environment.CreateWebResourceResponse(reply.Bytes==null?null:new MemoryStream(reply.Bytes,false),reply.StatusCode,reason,headers);
    if(reply.Dynamic&&reply.StatusCode!=200&&reply.Error!=null&&!transitioning)Status("画布图片未能显示，原图和画布记录均保留；请关闭后重新打开。 "+reply.Error);
   }catch(Exception error){if(!disposed){try{if(web!=null&&web.CoreWebView2!=null)e.Response=web.CoreWebView2.Environment.CreateWebResourceResponse(null,500,"Internal Server Error","Content-Type: text/plain\r\nCache-Control: no-store");}catch{/* A disconnected WebView cannot consume a response. */}Status("画布资源读取未完成："+error.Message+" 原图和画布记录均保留。");}}
   finally{try{deferral.Complete();}catch{/* The WebView may have closed while a bounded file read was finishing. */}}
  }
  void Post(string type,string requestId,object payload){if(disposed)return;string json=Json().Serialize(new{protocol=Protocol,session=session,requestId=requestId,type=type,payload=payload});if(PostForTests!=null)PostForTests(json);if(web!=null&&web.CoreWebView2!=null)web.CoreWebView2.PostWebMessageAsJson(json);}
  async void Receive(object sender,CoreWebView2WebMessageReceivedEventArgs e){
   await HandleMessageAsync(e.Source,e.WebMessageAsJson);
  }
  internal async Task HandleMessageAsync(string source,string json){
   if(disposed||!TrustedSource(source))return;string requestId=null;string messageSession=null;
   try{
    if(json==null||json.Length>96*1024*1024)throw new InvalidDataException("画布消息过大。");object decoded=Json().DeserializeObject(json);if(decoded is string)decoded=Json().DeserializeObject((string)decoded);var message=Map(decoded);if(Text(message,"protocol")!=Protocol)return;
    string type=Text(message,"type");requestId=Text(message,"requestId");if(requestId!=null&&!ValidRequestId(requestId))return;
    if(type=="ready"){
     // A reload must not silently replace edits in the browser with older data.
     if(ready){Status("画布请求重复加载，当前画布不会被覆盖。");return;}ready=true;try{LoadIntoBrowser();legacy.RememberOpened(document.Id);}catch{EngineFailed("画布加载连接未完成，原项目保留；可关闭后重新打开重试。");throw;}return;
    }
    messageSession=Text(message,"session");if(messageSession!=session)return;
    object value;var payload=message.TryGetValue("payload",out value)&&value!=null?Map(value):new Dictionary<string,object>();
    if(type=="loaded"){
     if(loaded)return;try{loaded=true;defaultsPending=false;RefreshControls();Post("interaction",null,new{enabled=!transitioning});if(loadCompletion!=null)loadCompletion.TrySetResult(true);Status("已保存");}catch{EngineFailed("画布加载确认未完成，原项目保留；可关闭后重新打开重试。");throw;}
     // Startup images belong to this window's first operation. Do not release
     // readiness before their durable import, or apply them twice on late ACKs.
     if(!startingImagesApplied){startingImagesApplied=true;try{if(startingPaths.Length>0)await ImportImagesAsync(startingPaths);firstLoad.TrySetResult(true);}catch{firstLoad.TrySetResult(false);throw;}}return;
    }
    if(type=="toolVisibilityChanged"){if(payload.TryGetValue("visible",out value)&&value is bool){toolsVisible=(bool)value;RefreshControls();}return;}
    if(type=="imagesAdded"){TaskCompletionSource<int> imported;if(requestId!=null&&imageImports.TryGetValue(requestId,out imported)&&payload.TryGetValue("count",out value)&&value is int)imported.TrySetResult((int)value);return;}
    if(type=="thumbnail"){
     if(!loaded||!payload.TryGetValue("revision",out value)||!(value is int)||(int)value!=revision)return;string dataUrl=Text(payload,"dataUrl");if(dataUrl==null)return;
     var target=document;int expected=revision;await operations.WaitAsync();try{if(disposed||messageSession!=session||expected!=revision||!Object.ReferenceEquals(target,document))return;await Task.Run(()=>{lock(resourceGate){CanvasProjectThumbnail.SaveMature(store,target,dataUrl);}});}finally{operations.Release();}NotifyChanged();return;
    }
    if(type=="save"){
     if(requestId==null)throw new InvalidDataException("保存消息缺少编号。");
     var target=document;await operations.WaitAsync();try{if(disposed||messageSession!=session||!Object.ReferenceEquals(target,document))return;if(!loaded)throw new IOException("画布尚未载入，请稍后保存。");await SaveMessage(payload,requestId,target,messageSession);}finally{operations.Release();}return;
    }
    if(type=="error"){string detail=Text(payload,"message")??"画布操作没有完成。";if(!loaded)EngineFailed("画布未载入："+detail+" 原项目完整保留；可以关闭后重新打开。");else Status(detail+" 当前内容仍在窗口中。");TaskCompletionSource<bool> failed;if(requestId!=null&&flushes.TryGetValue(requestId,out failed))failed.TrySetResult(false);TaskCompletionSource<int> imported;if(requestId!=null&&imageImports.TryGetValue(requestId,out imported))imported.TrySetResult(-1);return;}
    if(type=="dirty"){Status("正在编辑 · 自动保存");return;}
    if(type=="pickImages"){PickImages();return;}
    if(type=="pin"){SetPinned(!Topmost);return;}
    if(type=="newProject"){CreateNew();return;}
   }catch(Exception error){if(messageSession!=null&&messageSession!=session)return;Status("未能保存 / 添加："+error.Message+" 当前内容仍在窗口中。");try{Post("error",requestId,new{message=error.Message,revision=revision});}catch{/* A disconnected renderer must not turn a handled save error into an async-void crash. */}TaskCompletionSource<bool> waiter;if(requestId!=null&&flushes.TryGetValue(requestId,out waiter))waiter.TrySetResult(false);}
  }
  internal static string InlineResourceId(string assetId,string source){using(var hash=SHA256.Create()){return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes((assetId??"")+"\n"+source)),0,16).Replace("-","").ToLowerInvariant();}}
  async Task SaveMessage(Dictionary<string,object> payload,string requestId,MatureCanvasDocument target,string targetSession){
   object value;if(!payload.TryGetValue("revision",out value)||!(value is int)||((int)value)!=revision)throw new IOException("保存序号已过期，当前项目没有被覆盖，请重试。");
   if(!payload.TryGetValue("scene",out value))throw new InvalidDataException("保存消息缺少画布。");var scene=Map(value);var snapshot=Map(scene["document"]);var records=Map(snapshot["store"]);
   var normalizations=new List<object>();
   foreach(var raw in records.Values){var record=Map(raw);if(Text(record,"typeName")!="asset")continue;var props=Map(record["props"]);string source=Text(props,"src");if(source==null||!source.StartsWith("data:",StringComparison.Ordinal))continue;
    if(source.Length>44*1024*1024)throw new InvalidDataException("图片过大。");int delimiter=source.IndexOf(';');if(delimiter<5||delimiter>40)throw new InvalidDataException("图片格式无效。");string mime=source.Substring(5,delimiter-5),assetId=Text(record,"id");
    var resource=await Task.Run(()=>{string key=InlineResourceId(assetId,source);lock(resourceGate){return store.RegisterDataUrl(target,key,source,mime);}});string url=MatureCanvasStore.ResourceUrl(target.Id,resource.FileId);props["src"]=url;normalizations.Add(new{id=assetId,src=url});
   }
   // This decoded snapshot belongs only to this serialized operation. Keep
   // large JSON work off the Dispatcher without moving WebView or UI state.
   string normalized=await Task.Run(()=>store.NormalizeScene(Json().Serialize(scene)));
   if(disposed||targetSession!=session||!Object.ReferenceEquals(target,document))throw new IOException("项目已切换或关闭，旧画布保存未写入新项目。");
   string before=target.SceneJson;target.SceneJson=normalized;
   try{await Task.Run(()=>{lock(resourceGate){store.Save(target);}});}catch{target.SceneJson=before;throw;}
   revision++;Post("saved",requestId,new{revision=revision,assets=normalizations});Status("已保存");
   TaskCompletionSource<bool> waiter;if(flushes.TryGetValue(requestId,out waiter))waiter.TrySetResult(true);
   NotifyChanged();
  }
  void LoadIntoBrowser(){loaded=false;loadCompletion=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);heading.Text="Nook · "+document.Name;Title="Nook · "+document.Name;Topmost=document.Pinned;RefreshControls();Status("正在载入画布… 可点击右上角 × 取消，稍后重新打开重试。");var preferences=preferencesProvider();Post("load",null,new{scene=Json().DeserializeObject(store.BrowserScene(document)),name=document.Name,pinned=document.Pinned,revision=revision,isNewProject=defaultsPending,canvasPreferences=new{gridDefault=preferences.CanvasGrid,toolsVisibleDefault=preferences.CanvasShowTools,snapMode=preferences.CanvasSnap,zoomSpeed=preferences.CanvasZoomSpeed,defaultDash=preferences.CanvasDash,defaultSize=preferences.CanvasSize,saveDelay=preferences.CanvasSaveDelay},theme=Theme.Current=="light"?"light":"dark"});}
  async Task WaitForLoad(Task<bool> pending,string progress){
   if(pending==null)throw new IOException("画布尚未开始载入，原素材保留。");
   if(!pending.IsCompleted){var notice=LoadNoticeDelayForTests==null?Task.Delay(15000):LoadNoticeDelayForTests();if(await Task.WhenAny(pending,notice)==notice&&!pending.IsCompleted&&!disposed)Status(progress+" 可继续等待，或点击右上角 × 取消后重新打开重试；不会自动重复添加。");}
   // A slow renderer is not a failed import. Its original request stays owned
   // here until an ACK, EngineFailed or DisposeCanvas completes this promise.
   if(!await pending||disposed)throw new IOException("画布已关闭或未能载入，本次添加已停止；原素材保留。");
  }
  void EnsureCanvasContext(MatureCanvasDocument target,string expectedSession){if(disposed||!ready||!loaded||session!=expectedSession||!Object.ReferenceEquals(target,document))throw new IOException("画布已关闭、切换或连接失效；没有向其他项目添加图片。");}
  async Task ConfirmLoaded(){var target=document;string expectedSession=session;var pending=loadCompletion;await WaitForLoad(pending==null?null:pending.Task,"画布载入比平时慢，图片仍在等待添加。");EnsureCanvasContext(target,expectedSession);}
  async Task<bool> Flush(){
   if(!ready||!loaded||disposed)return false;string id="flush_"+Guid.NewGuid().ToString("N");var waiter=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);flushes.Add(id,waiter);
   try{Post("flush",id,new{});if(await Task.WhenAny(waiter.Task,Task.Delay(15000))!=waiter.Task){Status("保存没有完成，窗口保持打开。请稍后重试，原项目不会被覆盖。");return false;}return await waiter.Task;}finally{flushes.Remove(id);}
  }
  async Task ReplaceProject(Func<MatureCanvasDocument> create){
   var previous=document;string previousSession=session;await operations.WaitAsync();try{EnsureCanvasContext(previous,previousSession);var next=await Task.Run(()=>{lock(resourceGate){return create();}});EnsureCanvasContext(previous,previousSession);document=next;revision=0;session=Guid.NewGuid().ToString("N");LoadIntoBrowser();}finally{operations.Release();}
   await ConfirmLoaded();legacy.RememberOpened(document.Id);
  }
  public async Task<bool> OpenProjectAsync(string id,string[] paths=null,bool isNewProject=false){
   if(!CanImportImages)return false;Transition(true);try{if((document.Id!=id||(paths!=null&&paths.Length>0))&&!await Flush())return false;if(document.Id!=id){defaultsPending=isNewProject;await ReplaceProject(()=>store.Exists(id)?store.Load(id):store.CreateFromLegacy(legacy.Load(id)));}if(paths!=null&&paths.Length>0)await ImportCore(paths);return true;}
   catch(Exception error){Status("项目未切换 / 未能添加："+error.Message);return false;}finally{Transition(false);}
  }
  public async Task WaitUntilReadyAsync(){if(CanImportImages)return;var target=document;string expectedSession=session;await WaitForLoad(firstLoad.Task,"画布启动比平时慢，图片仍在等待添加。");EnsureCanvasContext(target,expectedSession);}
  public async Task<bool> PrepareProjectChangeAsync(){if(!CanImportImages)return false;Transition(true);try{return await Flush();}finally{Transition(false);}}
  public async Task ImportImagesAsync(string[] paths){if(!CanImportImages)throw new IOException("请等待画布启动 / 保存完成。");Transition(true);try{if(!await Flush())throw new IOException("当前画布尚未保存，图片没有加入。");await ImportCore(paths);}finally{Transition(false);}}
  async Task ImportCore(string[] paths){
   var files=CanvasProjectsPage.ImageFiles(paths);if(files.Length==0)throw new InvalidDataException("请添加图片文件；文件夹不会展开导入。");if(files.Length>200)throw new InvalidDataException("每次请添加不超过 200 张图片。");
   var target=document;string expectedSession=session;string request="images_"+Guid.NewGuid().ToString("N");var imported=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);int count=0;imageImports.Add(request,imported);
   try{await operations.WaitAsync();try{
    EnsureCanvasContext(target,expectedSession);var images=new List<object>();foreach(string path in files){
     // Import and bounded preview decoding stay sequential on the worker;
     // only immutable dimensions and resource identity return to the UI.
     var image=await Task.Run(()=>{lock(resourceGate){int width,height;var resource=store.RegisterImage(target,path,out width,out height);return new{id=resource.FileId,src=MatureCanvasStore.ResourceUrl(target.Id,resource.FileId),width=width,height=height,mimeType="image/png",name=Path.GetFileName(path)};}});EnsureCanvasContext(target,expectedSession);images.Add(image);
    }
    // Resources are durable before the browser adds shapes. A failed canvas
    // edit can leave an unreferenced resource, never a dangling saved image.
    EnsureCanvasContext(target,expectedSession);await Task.Run(()=>{lock(resourceGate){store.Save(target);}});EnsureCanvasContext(target,expectedSession);count=images.Count;Post("addImages",request,new{images=images});Status("正在加入 "+images.Count+" 张图片…");
   }finally{operations.Release();}
   if(await Task.WhenAny(imported.Task,Task.Delay(15000))!=imported.Task||await imported.Task!=count)throw new IOException("图片添加未确认，请检查当前画布后再重试，避免重复添加。");
   EnsureCanvasContext(target,expectedSession);if(!await Flush())throw new IOException("图片已在当前窗口中，但尚未保存；请保留窗口并重试保存。");EnsureCanvasContext(target,expectedSession);Status("已保存");
   }finally{imageImports.Remove(request);}
  }
  async void PickImages(){if(!CanImportImages)return;var dialog=new Microsoft.Win32.OpenFileDialog{Title="添加图片到画布",Multiselect=true,Filter="图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.tif;*.tiff;*.svg;*.avif;*.psd;*.ai|所有文件|*.*"};if(dialog.ShowDialog(this)!=true)return;try{await ImportImagesAsync(dialog.FileNames);}catch(Exception error){Status("图片未添加："+error.Message);}}
  async void SetPinned(bool pinned){if(!CanImportImages)return;Transition(true);try{if(!await Flush())return;await operations.WaitAsync();try{bool previous=document.Pinned;document.Pinned=pinned;try{await Task.Run(()=>{lock(resourceGate){store.Save(document);}});}catch{document.Pinned=previous;throw;}}finally{operations.Release();}Topmost=pinned;RefreshControls();Post("pinState",null,new{pinned=pinned});NotifyChanged();}catch(Exception error){Status("置顶设置未保存："+error.Message);}finally{Transition(false);}}
  async void CreateNew(){if(!CanImportImages)return;Transition(true);try{if(!await Flush())return;await ReplaceProject(CreateDocument);NotifyChanged();}catch(Exception error){Status("新建未完成："+error.Message);}finally{Transition(false);}}
  async void SaveNow(){if(!CanImportImages)return;Transition(true);try{await Flush();}catch(Exception error){Status("保存未完成："+error.Message+" 当前内容仍在窗口中，请重试。");}finally{Transition(false);}}
  void ShowProjectMenu(){var menu=Theme.Menu(this);Action<string,Action> add=(label,action)=>{var item=new MenuItem{Header=label};item.Click+=(s,e)=>action();menu.Items.Add(item);};add("立即保存",SaveNow);add("重命名画布",()=>RenameProject());add("复制为新画布",()=>DuplicateProject());menu.Items.Add(new Separator());var tools=new MenuItem{Header="显示绘图工具",IsCheckable=true,IsChecked=toolsVisible};tools.Click+=(s,e)=>SetToolsVisible(tools.IsChecked);menu.Items.Add(tools);add("画布设置…",ShowCanvasSettings);menu.Items.Add(new Separator());add("删除当前画布…",DeleteProject);add("恢复上次删除的画布",RestoreProject);menu.PlacementTarget=projectButtons.FirstOrDefault(button=>System.Windows.Automation.AutomationProperties.GetName(button)=="画布菜单");menu.Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom;menu.IsOpen=true;}
  string Prompt(string title,string initial){var dialog=new Window{Title=title,Width=370,Height=145,ResizeMode=ResizeMode.NoResize,WindowStartupLocation=WindowStartupLocation.CenterOwner,Owner=this};Theme.Attach(dialog,this);var panel=new StackPanel{Margin=new Thickness(16)};var input=new TextBox{Text=initial,Margin=new Thickness(0,0,0,12)};panel.Children.Add(input);var ok=new Button{Content="保存",IsDefault=true,HorizontalAlignment=HorizontalAlignment.Right,Padding=new Thickness(16,4,16,4)};ok.Click+=(s,e)=>{dialog.DialogResult=true;};panel.Children.Add(ok);dialog.Content=panel;dialog.Loaded+=(s,e)=>{input.Focus();input.SelectAll();};return dialog.ShowDialog()==true?input.Text:null;}
  async void RenameProject(){if(!CanImportImages)return;var name=Prompt("重命名画布",document.Name);if(name==null)return;Transition(true);try{if(!await Flush())return;await operations.WaitAsync();try{string old=document.Name;document.Name=name;try{await Task.Run(()=>{lock(resourceGate){store.Save(document);}});}catch{document.Name=old;throw;}}finally{operations.Release();}heading.Text="Nook · "+document.Name;Title="Nook · "+document.Name;NotifyChanged();}catch(Exception error){Status("名称未更改："+error.Message);}finally{Transition(false);}}
  async void DuplicateProject(){if(!CanImportImages)return;var name=Prompt("复制为新画布",document.Name+" 副本");if(name==null)return;Transition(true);try{if(!await Flush())return;var source=document;await ReplaceProject(()=>store.CopyProject(source,name));NotifyChanged();}catch(Exception error){Status("复制未完成，原项目完整保留："+error.Message);}finally{Transition(false);}}
  void DeleteProject(){if(!CanImportImages)return;if(MessageBox.Show(this,"删除画布「"+document.Name+"」？\n仅将此画布移入可恢复的画布回收目录，不删除素材库或原文件。","删除画布",MessageBoxButton.OKCancel,MessageBoxImage.Warning)!=MessageBoxResult.OK)return;SaveAndRun(()=>{legacy.Trash(document.Id);NotifyChanged();Close();});}
  async void RestoreProject(){if(!CanImportImages)return;Transition(true);try{if(!await Flush())return;await ReplaceProject(()=>{string id=legacy.RestoreLastDeleted();if(id==null)throw new IOException("没有可恢复的画布。");return store.Exists(id)?store.Load(id):store.CreateFromLegacy(legacy.Load(id));});NotifyChanged();}catch(Exception error){Status("恢复未完成："+error.Message);}finally{Transition(false);}}
  void DropOver(object sender,DragEventArgs e){e.Effects=CanImportImages&&(CanvasProjectsPage.ImageFiles(e.Data.GetData(DataFormats.FileDrop) as string[]).Length>0||DesktopImageDrop.CanRead(e.Data))?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;}
  async void DropIntoCanvas(object sender,DragEventArgs e){if(!CanImportImages){e.Handled=true;e.Effects=DragDropEffects.None;return;}string[] paths=null;DesktopImageDrop captured=null;string staged=null;try{if(DesktopImageDrop.CanRead(e.Data))captured=DesktopImageDrop.Capture(e.Data);else paths=CanvasProjectsPage.ImageFiles(e.Data.GetData(DataFormats.FileDrop) as string[]);e.Handled=true;e.Effects=DragDropEffects.Copy;if(captured!=null){staged=await captured.Stage();paths=new[]{staged};}await ImportImagesAsync(paths);}catch(Exception error){Status("图片未能添加："+error.Message);}finally{DesktopImageDrop.Cleanup(staged);}}
  void ThemeChanged(){if(ready)Post("theme",null,new{theme=Theme.Current=="light"?"light":"dark"});}
  public async void SaveAndRun(Action action){if(transitioning||disposed)return;Transition(true);try{if(!ready||!loaded){PreparedForExit=true;action();return;}if(await Flush()){PreparedForExit=true;action();}}catch(Exception error){Status("退出前保存未完成："+error.Message+" 窗口保持打开。");}finally{if(!disposed)PreparedForExit=false;Transition(false);}}
  void ClosingCanvas(object sender,CancelEventArgs e){if(PreparedForExit||!ready||!loaded||(!firstLoad.Task.IsCompleted&&startingPaths.Length>0))return;e.Cancel=true;if(transitioning){Status("画布正在切换 / 保存，请稍后关闭。");return;}SaveAndRun(Close);}
  void DisposeCanvas(){disposed=true;ready=false;loaded=false;firstLoad.TrySetResult(false);if(loadCompletion!=null)loadCompletion.TrySetResult(false);Theme.Changed-=ThemeChanged;foreach(var waiter in flushes.Values)waiter.TrySetResult(false);foreach(var waiter in imageImports.Values)waiter.TrySetResult(-1);if(web!=null){web.Dispose();web=null;}}
 }
}
