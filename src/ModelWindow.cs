using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace MoriDesk {
 public sealed class ModelWindow:Window {
  const string Origin="https://mori-model.local";
  readonly WebView2 web=new WebView2();readonly TextBlock status=new TextBlock{Margin=new Thickness(10,8,10,8)};readonly Dictionary<string,string> pendingFiles=new Dictionary<string,string>();string path;bool ready,closed;
  public static int Run(string file){try{var app=new Application();Mori.Theme.Set(Mori.Settings.Load().Theme);app.Run(new ModelWindow(file));return 0;}catch(Exception ex){MessageBox.Show(ex.Message,"Mori Desk · 3D 查看");return 1;}}
  public ModelWindow(string file){path=file;Title="Mori Desk · 3D 查看（本地处理）";Width=1200;Height=850;MinWidth=720;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterScreen;Mori.Theme.Attach(this,null);var root=new DockPanel();var bar=new StackPanel{Orientation=Orientation.Horizontal};bar.Children.Add(ReferencePage.Button("打开模型…",Choose));bar.Children.Add(status);DockPanel.SetDock(bar,Dock.Top);root.Children.Add(bar);root.Children.Add(web);Content=root;Loaded+=async(s,e)=>await Initialize();Closed+=(s,e)=>{closed=true;web.Dispose();};}
  async Task Initialize(){try{status.Text="启动本地 3D 引擎…";string folder=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"web","model");if(!File.Exists(Path.Combine(folder,"index.html")))throw new FileNotFoundException("3D 模块不完整，请重新解压完整安装包。");
   var environment=await CoreWebView2Environment.CreateAsync(null,Path.Combine(Entry.DataRoot,"WebView3D"),new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update"));if(closed)return;await web.EnsureCoreWebView2Async(environment);if(closed)return;
   var core=web.CoreWebView2;core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDevToolsEnabled=false;core.Settings.IsStatusBarEnabled=false;core.Settings.AreHostObjectsAllowed=false;
   core.SetVirtualHostNameToFolderMapping("mori-model.local",folder,CoreWebView2HostResourceAccessKind.DenyCors);
   core.NavigationStarting+=(s,e)=>{if(!e.Uri.StartsWith(Origin+"/",StringComparison.OrdinalIgnoreCase))e.Cancel=true;};core.NewWindowRequested+=(s,e)=>e.Handled=true;core.DownloadStarting+=(s,e)=>e.Cancel=true;
   core.PermissionRequested+=(s,e)=>e.State=CoreWebView2PermissionState.Deny;
   core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);core.WebResourceRequested+=(s,e)=>{
    var uri=new Uri(e.Request.Uri);if(uri.Scheme=="data"||uri.Scheme=="blob")return;
    string selectedPath;if(pendingFiles.TryGetValue(uri.AbsoluteUri,out selectedPath)){pendingFiles.Remove(uri.AbsoluteUri);try{if(!File.Exists(selectedPath))throw new FileNotFoundException();var stream=new FileStream(selectedPath,FileMode.Open,FileAccess.Read,FileShare.Read);e.Response=core.Environment.CreateWebResourceResponse(stream,200,"OK","Content-Type: application/octet-stream\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: "+Origin+"\r\nContent-Length: "+stream.Length);}catch{e.Response=core.Environment.CreateWebResourceResponse(null,404,"Model unavailable","");}return;}
    if(uri.GetLeftPart(UriPartial.Authority)!=Origin)e.Response=core.Environment.CreateWebResourceResponse(null,403,"Offline viewer","Content-Type: text/plain");
   };
   core.ProcessFailed+=(s,e)=>{status.Text="3D 引擎意外停止，可能内存不足。可关闭此窗口后重试；素材库不受影响。";ready=false;};
   core.WebMessageReceived+=(s,e)=>{if(!e.Source.StartsWith(Origin+"/",StringComparison.Ordinal))return;string message=e.TryGetWebMessageAsString();if(message=="desk-ready"){ready=true;status.Text="本地处理 · 不上传 · 不自动收藏";if(path!=null)LoadSelected();}else if(message.StartsWith("desk-error:"))status.Text=message.Substring(11);};
   await core.AddScriptToExecuteOnDocumentCreatedAsync("if(navigator.serviceWorker){navigator.serviceWorker.register=()=>Promise.reject(Error('Desktop bundled viewer'));}window.addEventListener('DOMContentLoaded',()=>{const style=document.createElement('style');style.textContent='.offline-pill,.gesture-hint{display:none!important}';document.head.append(style);let n=0;const t=setInterval(()=>{if(document.querySelector('input[type=file]')){clearInterval(t);setTimeout(()=>chrome.webview.postMessage('desk-ready'),700);}else if(++n>100){clearInterval(t);chrome.webview.postMessage('desk-error:查看器初始化超时，请重新打开');}},100);});");
   core.Navigate(Origin+"/index.html");
  }catch(Exception ex){if(!closed)status.Text="无法启动 3D 查看器。需要 Windows WebView2 运行时。 "+ex.Message;}}
  void Choose(){var dialog=new Microsoft.Win32.OpenFileDialog{Filter="3D 模型|*.stp;*.step;*.stl;*.obj;*.glb;*.3mf"};if(dialog.ShowDialog(this)==true){path=dialog.FileName;if(ready)LoadSelected();}}
  async void LoadSelected(){if(!ready||closed)return;try{string selectedPath=path;if(!File.Exists(selectedPath))throw new FileNotFoundException("模型已移动或磁盘不可用。");if(!FileRouter.Models.Contains(Path.GetExtension(selectedPath).ToLowerInvariant()))throw new NotSupportedException("暂不支持此模型格式。");string name=new JavaScriptSerializer().Serialize(Path.GetFileName(selectedPath));string modelUrl="https://mori-input.local/"+Guid.NewGuid().ToString("N");if(pendingFiles.Count>=8)pendingFiles.Clear();pendingFiles[modelUrl]=selectedPath;status.Text=Path.GetFileName(selectedPath)+" · 本地处理，不上传";string script="(async()=>{const token='"+modelUrl+"';if(window.__deskLoadAbort)window.__deskLoadAbort.abort();const abort=new AbortController();window.__deskLoadAbort=abort;window.__deskLoadToken=token;try{const r=await fetch(token,{cache:'no-store',signal:abort.signal});if(!r.ok)throw Error('无法读取选择的模型');const f=new File([await r.blob()],"+name+",{type:'application/octet-stream'});if(window.__deskLoadToken!==token)return;const d=new DataTransfer();d.items.add(f);const i=document.querySelector('input[type=file]');i.files=d.files;i.dispatchEvent(new Event('change',{bubbles:true}));}catch(e){if(window.__deskLoadToken===token)chrome.webview.postMessage('desk-error:'+e.message)}finally{if(window.__deskLoadAbort===abort)window.__deskLoadAbort=null}})();";await web.ExecuteScriptAsync(script);}catch(Exception ex){if(!closed)status.Text=ex.Message;}}
 }
}
