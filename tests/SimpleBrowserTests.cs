using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Mori;

namespace MoriDesk {
 internal static class SimpleBrowserTests {
  static IEnumerable<DependencyObject> All(DependencyObject root){
   yield return root;
   foreach(object child in LogicalTreeHelper.GetChildren(root)){
    var value=child as DependencyObject;
    if(value!=null)foreach(var nested in All(value))yield return nested;
   }
  }
  static T Find<T>(DependencyObject root,string name) where T:FrameworkElement{return All(root).OfType<T>().FirstOrDefault(control=>control.Name==name);}
  static bool Neutral(Brush brush){var solid=brush as SolidColorBrush;return solid!=null&&solid.Color.A==255&&solid.Color.R==solid.Color.G&&solid.Color.G==solid.Color.B;}
  static bool Token(Brush brush,params string[] names){var solid=brush as SolidColorBrush;return solid!=null&&names.Any(name=>solid.Color==((SolidColorBrush)Theme.Brush(name)).Color);}
  static string SearchQuery(Uri destination){
   foreach(string part in destination.Query.TrimStart('?').Split('&')){
    int split=part.IndexOf('=');
    if(split>=0&&Uri.UnescapeDataString(part.Substring(0,split))=="q")return Uri.UnescapeDataString(part.Substring(split+1).Replace("+"," "));
   }
   return null;
  }
  static void AcceptAddress(string input,string expectedHost,string expectedScheme,Action<bool,string> check){
   Uri destination;string error;bool accepted=SimpleBrowserPane.TryNormalizeAddress(input,out destination,out error);
   check(accepted&&destination!=null,"simple browser accepts "+input+" without performing navigation");
   if(destination==null)return;
   check(destination.IsAbsoluteUri&&destination.Host==expectedHost&&destination.Scheme==expectedScheme,"address normalization preserves the intended web host and protocol");
   check(SimpleBrowserPane.IsSafeWebAddress(destination.AbsoluteUri)&&String.IsNullOrEmpty(error),"accepted address is safe with no validation error");
  }
  static void RejectAddress(string input,string description,Action<bool,string> check){
   Uri destination;string error;bool accepted=SimpleBrowserPane.TryNormalizeAddress(input,out destination,out error);
   check(!accepted&&destination==null&&!String.IsNullOrWhiteSpace(error),"simple browser rejects "+description+" instead of searching or queuing it");
  }
  internal static void Run(Action<bool,string> check){
   // Pure URI and path checks only; none of these addresses are opened.
   AcceptAddress("https://example.invalid/","example.invalid","https",check);
   AcceptAddress("http://example.invalid:8080/path?q=sample#section","example.invalid","http",check);
   AcceptAddress("  HTTPS://EXAMPLE.INVALID/path  ","example.invalid","https",check);
   AcceptAddress("example.com","example.com","https",check);
   AcceptAddress("example.com/path?q=sample#section","example.com","https",check);
   AcceptAddress("127.0.0.1:8080/path","127.0.0.1","https",check);
   AcceptAddress("localhost:8080/path","localhost","https",check);
   Uri structured;string structuredError;
   bool structuredAccepted=SimpleBrowserPane.TryNormalizeAddress("example.com/path?q=sample#section",out structured,out structuredError);
   check(structuredAccepted&&structured!=null&&structured.AbsolutePath=="/path"&&structured.Query=="?q=sample"&&structured.Fragment=="#section","scheme completion retains path, query and fragment rather than turning an address into a keyword");
   foreach(string query in new[]{"紫色 3D 模型","cat & dog + 3D?","ordinary search words"}){
    Uri destination;string error;bool accepted=SimpleBrowserPane.TryNormalizeAddress(query,out destination,out error);
    check(accepted&&destination!=null,"ordinary search words become a queued destination");
    if(destination!=null){
     check(destination.Scheme=="https"&&destination.Host=="www.bing.com"&&destination.AbsolutePath=="/search"&&SimpleBrowserPane.IsSafeWebAddress(destination.AbsoluteUri),"keyword search uses only the safe HTTPS Bing endpoint");
     check(SearchQuery(destination)==query&&destination.Query.TrimStart('?').Split('&').Count(part=>part.StartsWith("q=",StringComparison.Ordinal))==1,"keyword escaping preserves Unicode, ampersand, plus and punctuation in one search parameter");
    }
   }
   foreach(string value in new[]{null,"","   "})RejectAddress(value,"an empty address",check);
   foreach(string value in new[]{"https://","http://","https://bad host.invalid/","https:///missing-host","https://example.invalid:99999/","https://[not-ipv6]/"})RejectAddress(value,"an explicitly malformed HTTP URL",check);
   foreach(string value in new[]{"file:///C:/private.txt","javascript:alert(1)"," JaVaScRiPt:alert(1) ","data:text/html,<script>alert(1)</script>","vbscript:MsgBox(1)","blob:https://example.invalid/id","about:blank","ftp://example.invalid/","mailto:test@example.invalid","chrome://extensions","edge://settings","custom:payload"}){
    RejectAddress(value,"a non-HTTP protocol",check);
    check(!SimpleBrowserPane.IsSafeWebAddress(value)&&!SimpleBrowserPane.ShouldOpenNewWindow(value,true),"page navigation and requested popups cannot bypass protocol validation");
   }
   foreach(string value in new[]{"https://user@example.invalid/","https://user:password@example.invalid/","https://%75ser@example.invalid/","https://@example.invalid/","user:password@example.invalid/"}){
    RejectAddress(value,"URL credentials",check);
    check(!SimpleBrowserPane.IsSafeWebAddress(value)&&!SimpleBrowserPane.ShouldOpenNewWindow(value,true),"web navigation rejects user-info even when the URL uses HTTPS");
   }
   foreach(string value in new[]{"https://example.invalid\\path","https:\\example.invalid","search\\words","https://example.invalid/\r\nInjected: yes","https://example.invalid/\tpath","https://example.invalid/\0path","\nhttps://example.invalid/","search\u0001words","https://example.invalid/%0a","https://example.invalid/%5c"}){
    RejectAddress(value,"control characters or a backslash",check);
    check(!SimpleBrowserPane.IsSafeWebAddress(value)&&!SimpleBrowserPane.ShouldOpenNewWindow(value,true),"navigation refuses ambiguous slashes and hidden control characters");
   }
   foreach(string value in new[]{"https://example.invalid/","http://localhost:8080/path","https://example.invalid/path?q=word%20word#part"}){
    check(SimpleBrowserPane.IsSafeWebAddress(value),"absolute HTTP/HTTPS page addresses are allowed");
    check(SimpleBrowserPane.ShouldOpenNewWindow(value,true)&&!SimpleBrowserPane.ShouldOpenNewWindow(value,false),"a safe new-window address still requires a user-initiated request");
   }
   foreach(string value in new[]{null,"","example.com","/relative/path","//example.invalid/path","ordinary words","https://"}){
    check(!SimpleBrowserPane.IsSafeWebAddress(value),"page navigation requires an absolute, valid HTTP/HTTPS address rather than normalizing input");
    check(!SimpleBrowserPane.ShouldOpenNewWindow(value,true)&&!SimpleBrowserPane.ShouldOpenNewWindow(value,false),"unsafe page destinations never open a new window regardless of initiation");
   }
   string dataRoot=Path.Combine(Path.GetTempPath(),"Nook-SimpleBrowser-tests-"+Guid.NewGuid().ToString("N"));
   string profile=SimpleBrowserPane.BrowserProfilePath(dataRoot);
   check(Path.GetFullPath(profile)==Path.GetFullPath(Path.Combine(dataRoot,"WebViewBrowser")),"browser profile has its own WebViewBrowser directory under the supplied data root");
   check(SimpleBrowserPane.BrowserProfilePath(dataRoot)==profile&&!Directory.Exists(dataRoot)&&!Directory.Exists(profile),"profile calculation is deterministic and does not create a directory");
   string otherRoot=Path.Combine(Path.GetTempPath(),"Nook-SimpleBrowser-tests-other-"+Guid.NewGuid().ToString("N"));
   check(Path.GetFullPath(SimpleBrowserPane.BrowserProfilePath(otherRoot))!=Path.GetFullPath(profile)&&!Directory.Exists(otherRoot),"separate test data roots cannot share a browser profile");
   CheckOffscreen(dataRoot,profile,check);
  }
  static void CheckOffscreen(string dataRoot,string profile,Action<bool,string> check){
   string previousTheme=Theme.Current;SimpleBrowserPane pane=null;Window host=null;int collected=0;
   try{
    Theme.Set("light");
    pane=new SimpleBrowserPane(dataRoot,(url,title)=>{collected++;},message=>{},false);
    host=new Window {Content=pane,Width=960,Height=600};
    check(ReferenceEquals(pane.Root,pane)&&!pane.InitializationStarted&&!pane.IsDisposed,"offscreen constructor exposes the root without starting WebView");
    check(!host.IsVisible&&!pane.IsVisible&&!Directory.Exists(dataRoot)&&!Directory.Exists(profile),"test construction never opens a window or creates a WebView profile");
    var address=Find<TextBox>(pane,"BrowserAddress");
    check(address!=null&&!String.IsNullOrWhiteSpace(AutomationProperties.GetName(address)),"browser address input has an accessible name");
    string[] buttonNames={"BrowserBack","BrowserForward","BrowserReload","BrowserHome","BrowserCollect","BrowserExternal"};
    foreach(string name in buttonNames){
     var button=Find<Button>(pane,name);check(button!=null,"simple browser exposes "+name+" as a WPF button");
     if(button==null)continue;
     check(button.Width==32&&button.Height==32&&button.FontSize==14&&button.FontFamily.Source=="Segoe MDL2 Assets","browser toolbar uses compact MDL2 icons with a 32-pixel hit area");
     check(!String.IsNullOrWhiteSpace(AutomationProperties.GetName(button))&&!String.IsNullOrWhiteSpace(Convert.ToString(button.ToolTip)),"icon-only browser action has a spoken name and explanatory tooltip");
    }
    foreach(string appearance in new[]{"light","dark","light"}){
     Theme.Set(appearance);
     check(DependencyPropertyHelper.GetValueSource(pane,Control.BackgroundProperty).IsExpression&&Neutral(pane.Background)&&Token(pane.Background,"Canvas","Panel","Surface"),"existing offscreen browser root resolves a dynamic neutral "+appearance+" background");
     check(Neutral(pane.Foreground)&&Token(pane.Foreground,"Text"),"browser chrome text follows the readable neutral "+appearance+" token");
     if(address!=null)check(Token(address.Background,"Input")&&Token(address.Foreground,"Text")&&Token(address.BorderBrush,"Border"),"browser address box follows "+appearance+" input, text and border resources");
     check(!pane.InitializationStarted&&!Directory.Exists(profile),"theme updates cannot initialize a hidden browser or create profile data");
    }
    pane.SetActive(false);check(!pane.IsActiveForBrowsing,"an inactive browser pane reports its paused state");
    pane.SetActive(true);check(pane.IsActiveForBrowsing&&!pane.InitializationStarted,"activation retains the test-only no-initialization guard");
    pane.Pause();check(!pane.IsActiveForBrowsing&&!pane.InitializationStarted,"pausing updates the active state without constructing WebView");
    pane.Resume();check(pane.IsActiveForBrowsing&&!pane.InitializationStarted,"resuming remains offscreen and respects the disabled automatic initialization");
    check(pane.Navigate("https://example.invalid/queued")&&!pane.InitializationStarted,"valid navigation can be queued without starting WebView or making a request");
    check(String.IsNullOrEmpty(pane.CurrentUrl)&&String.IsNullOrEmpty(pane.CurrentTitle)&&collected==0,"a queued address is not reported or collected as a successfully loaded page");
    foreach(string value in new[]{"javascript:alert(1)","https://user@example.invalid/","https://","https://example.invalid\\path"})check(!pane.Navigate(value)&&!pane.InitializationStarted,"invalid queued navigation cannot start the browser");
    check(!Directory.Exists(dataRoot)&&!Directory.Exists(profile),"offscreen lifecycle and validation leave the entire fake data root absent");
    pane.SetActive(false);check(!pane.IsActiveForBrowsing,"switching away marks the browser inactive");
    pane.Dispose();pane.Dispose();
    check(pane.IsDisposed&&!pane.InitializationStarted&&!pane.IsActiveForBrowsing,"browser disposal is idempotent and clears active state");
    check(!pane.Navigate("https://example.invalid/after-dispose"),"disposed browser panes reject navigation instead of reviving WebView");
    pane.SetActive(true);pane.Resume();pane.Pause();
    check(pane.IsDisposed&&!pane.IsActiveForBrowsing&&!pane.InitializationStarted&&collected==0,"disposed lifecycle calls cannot reactivate the pane or collect a URL");
    check(!host.IsVisible&&!Directory.Exists(dataRoot)&&!Directory.Exists(profile),"the complete WPF regression remains invisible and leaves no profile files");
   }finally{
    if(pane!=null)pane.Dispose();if(host!=null)host.Close();Theme.Set(previousTheme);
   }
  }
 }
}
