using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MoriDesk {
 // A small, on-demand web surface. It intentionally has no local-content bridge.
 public sealed class SimpleBrowserPane : UserControl, IDisposable {
  readonly string profilePath;
  readonly Action<string,string> collect;
  readonly Action<string> reportStatus;
  readonly bool autoInitialize;
  readonly Grid viewport=new Grid();
  readonly Border home;
  readonly TextBox address;
  readonly TextBlock feedback;
  readonly Button back,forward,reload,homeButton,collectButton,externalButton;
  WebView2 web;
  Uri pendingAddress,navigatingAddress;
  bool active=true,disposed,initializing,ready,loading,stopped;
  int browserGeneration;
  ulong navigationId;
  string currentUrl="",currentTitle="";

  public SimpleBrowserPane(string dataRoot,Action<string,string> collect,Action<string> status)
   :this(dataRoot,collect,status,true){}

  // False forbids engine creation even when loaded, activated, or navigated by a test.
  internal SimpleBrowserPane(string dataRoot,Action<string,string> collect,Action<string> status,bool autoInitialize){
   profilePath=BrowserProfilePath(dataRoot);this.collect=collect;reportStatus=status;this.autoInitialize=autoInitialize;
   SetResourceReference(BackgroundProperty,"Canvas");SetResourceReference(ForegroundProperty,"Text");
   UseLayoutRounding=true;Focusable=false;
   var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});layout.RowDefinitions.Add(new RowDefinition());layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
   var toolbar=new Grid{Margin=new Thickness(8,7,8,7)};
   for(int i=0;i<7;i++)toolbar.ColumnDefinitions.Add(new ColumnDefinition{Width=i==3?new GridLength(1,GridUnitType.Star):GridLength.Auto});
   back=IconButton("BrowserBack","\uE72B","后退（Alt+左方向键）",GoBack);
   forward=IconButton("BrowserForward","\uE72A","前进（Alt+右方向键）",GoForward);
   reload=IconButton("BrowserReload","\uE72C","刷新（F5）",ReloadOrStop);
   address=new TextBox{Name="BrowserAddress",MinWidth=80,Height=32,Margin=new Thickness(5,0,5,0),Padding=new Thickness(9,5,9,5),FontSize=12,VerticalContentAlignment=VerticalAlignment.Center};
   address.SetResourceReference(BackgroundProperty,"Input");address.SetResourceReference(ForegroundProperty,"Text");address.SetResourceReference(BorderBrushProperty,"Border");address.SetResourceReference(TextBox.CaretBrushProperty,"Text");
   address.Style=(Style)XamlReader.Parse("<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='TextBox'><Setter Property='BorderThickness' Value='1'/><Style.Triggers><Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger></Style.Triggers></Style>");
   address.ToolTip="输入网址或关键词，按 Enter 打开（Ctrl+L）";AutomationProperties.SetName(address,"网址或搜索关键词");
   address.KeyDown+=(s,e)=>{if(e.Key==Key.Enter){Navigate(address.Text);e.Handled=true;}};
   homeButton=IconButton("BrowserHome","\uE80F","回到浏览提示",ReturnHome);
   collectButton=IconButton("BrowserCollect","\uE734","收藏当前网址",CollectCurrent);
   externalButton=IconButton("BrowserExternal","\uE8A7","用默认浏览器打开当前网页",OpenExternal);
   FrameworkElement[] tools={back,forward,reload,address,homeButton,collectButton,externalButton};
   for(int i=0;i<tools.Length;i++){Grid.SetColumn(tools[i],i);toolbar.Children.Add(tools[i]);}
   var bar=new Border{Child=toolbar,BorderThickness=new Thickness(0,0,0,1)};bar.SetResourceReference(Border.BackgroundProperty,"Panel");bar.SetResourceReference(Border.BorderBrushProperty,"Divider");layout.Children.Add(bar);
   var hint=new StackPanel{MaxWidth=420,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(28)};
   var glyph=new TextBlock{Text="\uE774",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=25,Margin=new Thickness(0,0,0,15),HorizontalAlignment=HorizontalAlignment.Center};glyph.SetResourceReference(TextBlock.ForegroundProperty,"Muted");hint.Children.Add(glyph);
   var title=new TextBlock{Text="快速浏览收藏的网址",FontSize=17,TextAlignment=TextAlignment.Center,Margin=new Thickness(0,0,0,9)};title.SetResourceReference(TextBlock.ForegroundProperty,"Text");hint.Children.Add(title);
   var instructions=new TextBlock{Text="输入网址或关键词，按 Enter 打开。\n未打开网页时不会联网，也不会自动收藏。",FontSize=12,LineHeight=22,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap};instructions.SetResourceReference(TextBlock.ForegroundProperty,"Muted");hint.Children.Add(instructions);
   home=new Border{Child=hint};home.SetResourceReference(Border.BackgroundProperty,"Canvas");viewport.Children.Add(home);Grid.SetRow(viewport,1);layout.Children.Add(viewport);
   feedback=new TextBlock{Text="网页不会自动收藏 · 下载请使用默认浏览器",FontSize=11,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(10,5,10,6)};feedback.SetResourceReference(TextBlock.ForegroundProperty,"Muted");
   var footer=new Border{Child=feedback,BorderThickness=new Thickness(0,1,0,0)};footer.SetResourceReference(Border.BackgroundProperty,"Panel");footer.SetResourceReference(Border.BorderBrushProperty,"Divider");Grid.SetRow(footer,2);layout.Children.Add(footer);
   Content=layout;UpdateButtons();
   Loaded+=(s,e)=>ActivateIfNeeded();Unloaded+=(s,e)=>PauseCore();
   IsVisibleChanged+=(s,e)=>{if(IsVisible)ActivateIfNeeded();else PauseCore();};
   PreviewKeyDown+=HandleKeyboard;
  }

  public UserControl Root {get{return this;}}
  public string CurrentUrl {get{return currentUrl;}}
  public string CurrentTitle {get{return currentTitle;}}
  internal bool InitializationStarted {get;private set;}
  internal bool IsDisposed {get{return disposed;}}
  internal bool IsActiveForBrowsing {get{return active&&!disposed;}}
  bool CanRun {get{return active&&!disposed&&IsLoaded&&IsVisible;}}

  public bool Navigate(string addressOrQuery){
   if(disposed)return false;
   Uri target;string error;
   if(!TryNormalizeAddress(addressOrQuery,out target,out error)){SetFeedback(error);return false;}
   pendingAddress=target;address.Text=target.AbsoluteUri;UpdateButtons();
   SetFeedback("准备打开网页…");ActivateIfNeeded();return true;
  }

  public void SetActive(bool value){if(disposed)return;active=value;if(value)ActivateIfNeeded();else PauseCore();}
  public void Pause(){SetActive(false);}
  public void Resume(){SetActive(true);}

  void ActivateIfNeeded(){
   if(!CanRun||!autoInitialize)return;
   if(ready&&web!=null){
    try{web.CoreWebView2.Resume();web.CoreWebView2.IsMuted=false;web.Visibility=currentUrl.Length>0||pendingAddress!=null?Visibility.Visible:Visibility.Collapsed;NavigatePending();}
    catch(Exception ex){SetFeedback("无法恢复网页。请重新输入网址。 "+ex.Message);CloseBrowser();}
   }else if(!initializing&&pendingAddress!=null){var unused=InitializeAsync();}
  }

  async Task InitializeAsync(){
   if(disposed||initializing||!CanRun||!autoInitialize||pendingAddress==null)return;
   initializing=true;InitializationStarted=true;int generation=++browserGeneration;bool failed=false;
   var target=new WebView2{Visibility=Visibility.Collapsed,AllowExternalDrop=false};web=target;viewport.Children.Insert(0,target);
   try{
    SetFeedback("启动网页浏览…");
    var options=new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update");
    options.AllowSingleSignOnUsingOSPrimaryAccount=false;
    options.AreBrowserExtensionsEnabled=false;
    var environment=await CoreWebView2Environment.CreateAsync(null,profilePath,options);
    if(!Owns(target,generation)){target.Dispose();return;}
    if(!CanRun){CloseBrowser();return;}
    await target.EnsureCoreWebView2Async(environment);
    if(!Owns(target,generation)){target.Dispose();return;}
    Configure(target.CoreWebView2);ready=true;
    if(CanRun)ActivateIfNeeded();else PauseCore();
   }catch(Exception ex){
    failed=Owns(target,generation);if(failed){SetFeedback("无法启动网页浏览。需要 Windows WebView2 运行时。 "+ex.Message);CloseBrowser();}
   }finally{
    initializing=false;
    // A hidden/disposed controller may finish after a newer activation. Never keep it alive.
    if(!failed&&!disposed&&generation!=browserGeneration&&CanRun&&pendingAddress!=null)ActivateIfNeeded();
   }
  }

  bool Owns(WebView2 target,int generation){return !disposed&&generation==browserGeneration&&ReferenceEquals(web,target);}
  bool IsCurrentCore(CoreWebView2 core){return !disposed&&ready&&web!=null&&ReferenceEquals(web.CoreWebView2,core);}

  void Configure(CoreWebView2 core){
   core.Settings.AreHostObjectsAllowed=false;core.Settings.IsWebMessageEnabled=false;
   core.Settings.AreDevToolsEnabled=false;core.Settings.IsStatusBarEnabled=false;
   core.Settings.AreBrowserAcceleratorKeysEnabled=false;
   core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
   core.NavigationStarting+=(s,e)=>{
    if(!IsCurrentCore(core)||!CanRun){e.Cancel=true;return;}
    if(!IsSafeWebAddress(e.Uri)){e.Cancel=true;SetFeedback("已阻止非 HTTP/HTTPS 网页或不安全的网址。");return;}
    navigationId=e.NavigationId;navigatingAddress=new Uri(e.Uri);stopped=false;loading=true;currentTitle="";home.Visibility=Visibility.Collapsed;
    if(web!=null&&CanRun)web.Visibility=Visibility.Visible;
    SetFeedback("正在打开网页…");UpdateButtons();
   };
   core.FrameNavigationStarting+=(s,e)=>{if(!IsCurrentCore(core)||!CanRun||!IsSafeWebAddress(e.Uri))e.Cancel=true;};
   core.LaunchingExternalUriScheme+=(s,e)=>{e.Cancel=true;if(IsCurrentCore(core))SetFeedback("网页请求打开本机应用，已阻止。需要时请使用默认浏览器。");};
   core.SourceChanged+=(s,e)=>{
    if(!IsCurrentCore(core))return;
    currentUrl=IsSafeWebAddress(core.Source)?core.Source:"";
    if(!address.IsKeyboardFocusWithin&&currentUrl.Length>0)address.Text=currentUrl;UpdateButtons();
   };
   core.HistoryChanged+=(s,e)=>{if(IsCurrentCore(core))UpdateButtons();};
   core.DocumentTitleChanged+=(s,e)=>{if(IsCurrentCore(core))currentTitle=CleanTitle(core.DocumentTitle);};
   core.NavigationCompleted+=(s,e)=>{
    if(!IsCurrentCore(core)||e.NavigationId!=navigationId)return;
    loading=false;navigatingAddress=null;
    if(stopped)SetFeedback("已停止加载。");
    else if(e.IsSuccess)SetFeedback("网页不会自动收藏 · 下载请使用默认浏览器");
    else SetFeedback("网页未能打开（"+e.WebErrorStatus+"）。可重试或使用默认浏览器。");
    UpdateButtons();
   };
   core.NewWindowRequested+=(s,e)=>{
    e.Handled=true;
    if(!IsCurrentCore(core)||!CanRun)return;
    if(ShouldOpenNewWindow(e.Uri,e.IsUserInitiated)){
     string url=e.Uri;Dispatcher.BeginInvoke(new Action(()=>{if(IsCurrentCore(core)&&CanRun)Navigate(url);}));
    }else SetFeedback(e.IsUserInitiated?"已阻止不安全的网址。":"已阻止自动弹出的网页。");
   };
   core.PermissionRequested+=(s,e)=>{e.State=CoreWebView2PermissionState.Deny;e.Handled=true;};
   core.DownloadStarting+=(s,e)=>{e.Cancel=true;e.Handled=true;if(IsCurrentCore(core))SetFeedback("下载未开始。请使用默认浏览器下载文件。");};
   core.ServerCertificateErrorDetected+=(s,e)=>{e.Action=CoreWebView2ServerCertificateErrorAction.Cancel;if(IsCurrentCore(core))SetFeedback("网页证书未通过验证，已停止打开。");};
   core.ProcessFailed+=(s,e)=>{
    if(!IsCurrentCore(core))return;
    CloseBrowser();home.Visibility=Visibility.Visible;SetFeedback("网页浏览意外停止。素材收藏不受影响，请重新输入网址。");
   };
  }

  void NavigatePending(){
   if(disposed||!ready||web==null||pendingAddress==null||!CanRun)return;
   Uri target=pendingAddress;pendingAddress=null;
   try{web.CoreWebView2.Navigate(target.AbsoluteUri);}
   catch(Exception ex){loading=false;SetFeedback("网页未能打开。 "+ex.Message);UpdateButtons();}
  }

  async void PauseCore(){
   if(disposed||web==null)return;
   var target=web;int generation=browserGeneration;
   if(!ready){CloseBrowser();return;}
   try{
    target.Visibility=Visibility.Collapsed;target.CoreWebView2.IsMuted=true;
    if(loading){if(pendingAddress==null&&navigatingAddress!=null)pendingAddress=navigatingAddress;stopped=true;target.CoreWebView2.Stop();loading=false;UpdateButtons();}
    bool suspended=await target.CoreWebView2.TrySuspendAsync();
    if(!Owns(target,generation))return;
    if(CanRun){target.CoreWebView2.Resume();target.CoreWebView2.IsMuted=false;target.Visibility=currentUrl.Length>0?Visibility.Visible:Visibility.Collapsed;NavigatePending();}
    else if(!suspended){if(pendingAddress==null&&IsSafeWebAddress(currentUrl))pendingAddress=new Uri(currentUrl);CloseBrowser();}
   }catch(Exception){
    if(Owns(target,generation)&&!CanRun){if(pendingAddress==null&&IsSafeWebAddress(currentUrl))pendingAddress=new Uri(currentUrl);CloseBrowser();}
   }
  }

  void CloseBrowser(){
   ++browserGeneration;ready=false;loading=false;navigatingAddress=null;
   var previous=web;web=null;
   if(previous!=null){viewport.Children.Remove(previous);previous.Dispose();}
   UpdateButtons();
  }

  void ReturnHome(){
   if(disposed)return;
   pendingAddress=null;currentUrl="";currentTitle="";address.Text="";CloseBrowser();home.Visibility=Visibility.Visible;
   SetFeedback("网页不会自动收藏 · 下载请使用默认浏览器");address.Focus();
  }

  void GoBack(){if(!disposed&&ready&&CanRun&&web!=null&&web.CoreWebView2.CanGoBack)RunBrowserAction(()=>web.CoreWebView2.GoBack());}
  void GoForward(){if(!disposed&&ready&&CanRun&&web!=null&&web.CoreWebView2.CanGoForward)RunBrowserAction(()=>web.CoreWebView2.GoForward());}
  void ReloadOrStop(){
   if(disposed)return;
   if(ready&&web!=null&&CanRun){
    if(loading){stopped=true;RunBrowserAction(()=>web.CoreWebView2.Stop());loading=false;SetFeedback("已停止加载。");UpdateButtons();}
    else if(IsSafeWebAddress(currentUrl))RunBrowserAction(()=>web.CoreWebView2.Reload());
   }else if(pendingAddress!=null)ActivateIfNeeded();
   else if(IsSafeWebAddress(currentUrl))Navigate(currentUrl);
  }
  void RunBrowserAction(Action action){try{action();}catch(Exception ex){SetFeedback("浏览操作未完成。 "+ex.Message);}}

  void CollectCurrent(){
   if(disposed||collect==null||loading||!IsSafeWebAddress(currentUrl))return;
   try{collect(currentUrl,currentTitle.Length==0?currentUrl:currentTitle);}
   catch(Exception ex){SetFeedback("网址未能收藏。 "+ex.Message);}
  }
  void OpenExternal(){
   if(disposed||!IsSafeWebAddress(currentUrl))return;
   try{Process.Start(new ProcessStartInfo(currentUrl){UseShellExecute=true});}
   catch(Exception ex){SetFeedback("无法在默认浏览器打开网页。 "+ex.Message);}
  }

  void HandleKeyboard(object sender,KeyEventArgs e){
   if(disposed||e.Handled)return;
   Key key=e.Key==Key.System?e.SystemKey:e.Key;var modifiers=Keyboard.Modifiers;
   if(key==Key.L&&modifiers==ModifierKeys.Control){address.Focus();address.SelectAll();e.Handled=true;}
   else if(key==Key.F5&&modifiers==ModifierKeys.None){ReloadOrStop();e.Handled=true;}
   else if(key==Key.Left&&modifiers==ModifierKeys.Alt){GoBack();e.Handled=true;}
   else if(key==Key.Right&&modifiers==ModifierKeys.Alt){GoForward();e.Handled=true;}
  }

  void UpdateButtons(){
   if(back==null)return;
   bool usable=!disposed&&ready&&web!=null;
   back.IsEnabled=usable&&web.CoreWebView2!=null&&web.CoreWebView2.CanGoBack;
   forward.IsEnabled=usable&&web.CoreWebView2!=null&&web.CoreWebView2.CanGoForward;
   reload.IsEnabled=!disposed&&(usable||pendingAddress!=null||currentUrl.Length>0);
   ((TextBlock)reload.Content).Text=loading?"\uE71A":"\uE72C";
   reload.ToolTip=loading?"停止加载":"刷新（F5）";AutomationProperties.SetName(reload,loading?"停止加载":"刷新网页");
   homeButton.IsEnabled=!disposed;collectButton.IsEnabled=!disposed&&collect!=null&&!loading&&IsSafeWebAddress(currentUrl);
   externalButton.IsEnabled=!disposed&&IsSafeWebAddress(currentUrl);address.IsEnabled=!disposed;
  }

  void SetFeedback(string message){if(disposed)return;feedback.Text=message;feedback.ToolTip=message;if(reportStatus!=null)reportStatus(message);}
  static Button IconButton(string name,string glyph,string hint,Action action){
   var icon=new TextBlock{Text=glyph,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=14,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};
   var button=new Button{Name=name,Content=icon,Width=32,Height=32,FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=14,Margin=new Thickness(1,0,1,0),Padding=new Thickness(0),ToolTip=hint};
   button.SetResourceReference(BackgroundProperty,"Control");button.SetResourceReference(ForegroundProperty,"Text");button.SetResourceReference(BorderBrushProperty,"Border");
   button.Template=(ControlTemplate)XamlReader.Parse("<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'><Border x:Name='Surface' CornerRadius='4' BorderThickness='1' BorderBrush='{TemplateBinding BorderBrush}' Background='{TemplateBinding Background}'><ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Surface' Property='Background' Value='{DynamicResource Hover}'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Surface' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Surface' Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
   AutomationProperties.SetName(button,hint);button.Click+=(s,e)=>action();return button;
  }

  internal static string BrowserProfilePath(string dataRoot){
   if(String.IsNullOrWhiteSpace(dataRoot))throw new ArgumentException("浏览器需要独立的数据目录。","dataRoot");
   return Path.GetFullPath(Path.Combine(dataRoot,"WebViewBrowser"));
  }
  static bool ContainsUnsafeCharacters(string value){
   foreach(char ch in value)if(Char.IsControl(ch)||ch=='\\')return true;
   return Regex.IsMatch(value,@"%(?:0[0-9a-f]|1[0-9a-f]|7f|5c)",RegexOptions.IgnoreCase);
  }
  internal static bool IsSafeWebAddress(string value){
   if(String.IsNullOrWhiteSpace(value)||value.Length>8192||ContainsUnsafeCharacters(value))return false;
   Uri uri;
   if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||(uri.Scheme!=Uri.UriSchemeHttp&&uri.Scheme!=Uri.UriSchemeHttps)||uri.Host.Length==0||uri.UserInfo.Length>0)return false;
   string authority=uri.GetComponents(UriComponents.StrongAuthority,UriFormat.UriEscaped);
   var rawAuthority=Regex.Match(value,@"^https?://([^/?#]*)",RegexOptions.IgnoreCase);
   return authority.IndexOf('@')<0&&rawAuthority.Success&&rawAuthority.Groups[1].Value.IndexOf('@')<0&&uri.HostNameType!=UriHostNameType.Unknown;
  }
  internal static bool ShouldOpenNewWindow(string uri,bool userInitiated){return userInitiated&&IsSafeWebAddress(uri);}
  internal static bool TryNormalizeAddress(string input,out Uri destination,out string error){
   destination=null;error="请输入网址或搜索关键词。";
   if(String.IsNullOrWhiteSpace(input))return false;
   if(input.Length>8192||ContainsUnsafeCharacters(input)){error="网址或关键词包含不安全的字符，或长度过长。";return false;}
   string text=input.Trim();Uri candidate;
   bool webScheme=Regex.IsMatch(text,@"^https?:",RegexOptions.IgnoreCase);
   if(webScheme){
    if(!IsSafeWebAddress(text)){error="网址无效。仅支持不含账户密码的 HTTP/HTTPS 网页。";return false;}
    destination=new Uri(text);error="";return true;
   }
   string bare=text.StartsWith("//",StringComparison.Ordinal)?"https:"+text:"https://"+text;
   bool bareHost=Uri.TryCreate(bare,UriKind.Absolute,out candidate)&&IsSafeWebAddress(bare)&&!Regex.IsMatch(text,@"\s")&&(candidate.Host.Contains(".")||candidate.Host.Equals("localhost",StringComparison.OrdinalIgnoreCase)||candidate.HostNameType==UriHostNameType.IPv4||candidate.HostNameType==UriHostNameType.IPv6);
   if(bareHost){destination=candidate;error="";return true;}
   if(Regex.IsMatch(text,@"^[a-z][a-z0-9+.-]*\s*:",RegexOptions.IgnoreCase)||text.StartsWith("//",StringComparison.Ordinal)||text.IndexOf('@')>=0){error="仅支持不含账户密码的 HTTP/HTTPS 网页。";return false;}
   destination=new Uri("https://www.bing.com/search?q="+Uri.EscapeDataString(text));error="";return true;
  }
  static string CleanTitle(string value){
   var clean=new StringBuilder();foreach(char ch in value??""){if(!Char.IsControl(ch))clean.Append(ch);if(clean.Length>=200)break;}return clean.ToString().Trim();
  }

  public void Dispose(){
   if(disposed)return;disposed=true;active=false;pendingAddress=null;CloseBrowser();home.Visibility=Visibility.Visible;UpdateButtons();
  }
 }
}
