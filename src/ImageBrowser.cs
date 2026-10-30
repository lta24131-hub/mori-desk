using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Forms.Integration;
using Mori;
using Forms=System.Windows.Forms;
namespace MoriDesk {
 public sealed partial class ImageBrowser:Window {
  readonly DockPanel root=new DockPanel();readonly TextBlock label=new TextBlock{Margin=new Thickness(12,8,12,8)};
  string[] siblings;int index;PreviewImage image;WindowsFormsHost host;PreviewDragController drag;PreviewViewport viewport;PreviewPicture picture;bool loading,closed;
  string currentPath;CancellationTokenSource scan;int directoryVersion;Button previous,next,pinButton;bool scanning,pinning,closeAfterPin;
  PreviewBackground backgroundMode=PreviewBackground.Automatic;
  readonly Settings previewPreferences;
  public static int Run(string path){if(path!=null&&FormatPreviewInfo.UsesPagedPreview(path))return FilePreviewHost.Run(path);try{var app=new Application();Theme.Apply(Settings.Load());var w=new ImageBrowser(path);app.Run(w);return 0;}catch(Exception ex){MessageBox.Show(ex.Message,"Nook · 看图");return 1;}}
  public ImageBrowser(string path):this(path,Settings.Load()){}
  internal ImageBrowser(string path,Settings preferences){if(preferences==null)throw new ArgumentNullException("preferences");previewPreferences=new Settings{AnimateImages=preferences.AnimateImages,PreviewBackdrop=preferences.PreviewBackdrop};backgroundMode=PreviewPicture.DefaultBackground(previewPreferences.PreviewBackdrop);Width=1100;Height=790;MinWidth=650;MinHeight=420;Title="Nook · 看图";Theme.Attach(this,null);WindowStartupLocation=WindowStartupLocation.CenterScreen;InitialWindowPlacement.Attach(this);Content=root;
   // The large image uses a native drawing surface. Keep only this window's
   // small WPF chrome in software rendering: virtual/remote display drivers can
   // otherwise leave blank toolbar regions around a WindowsFormsHost. Other
   // app windows retain their existing renderer.
   SourceInitialized+=delegate{UseCompatibleChrome(this);};InitializeViewerUi();
   Closing+=(s,e)=>{if(pinning){closeAfterPin=true;e.Cancel=true;Hide();}};
   AllowDrop=true;DragOver+=(s,e)=>{e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)?DragDropEffects.Copy:DragDropEffects.None;e.Handled=true;};Drop+=(s,e)=>{var paths=e.Data.GetData(DataFormats.FileDrop)as string[];if(paths!=null&&paths.Length>0)Open(paths[0]);e.Handled=true;};Theme.Changed+=UpdatePreviewTheme;IsVisibleChanged+=delegate{UpdatePlaybackVisibility();};StateChanged+=delegate{UpdatePlaybackVisibility();};Deactivated+=delegate{if(drag!=null)drag.Cancel();UpdateEdgeVisibility(-1);};Closed+=(s,e)=>{closed=true;Theme.Changed-=UpdatePreviewTheme;directoryVersion++;if(scan!=null)scan.Cancel();StopFilmstrip();stripCache.Clear();stripCacheOrder.Clear();Release();};Loaded+=(s,e)=>{if(path!=null)Open(path);else label.Text="拖入图片，或打开文件";};
  }
  async Task PinCurrentAsync(){if(image==null||loading||pinning)return;pinning=true;pinButton.IsEnabled=false;try{await DesktopImagePins.ShowAsync(this,image.Image);}finally{pinning=false;if(closeAfterPin&&!closed)Close();else if(!closed)UpdateInfo();}}
  void CopyCurrent(){if(closed||loading||image==null)return;string failure=PreviewClipboard.TryCopy(image.Image);label.Text=failure.Length==0?"已复制图片，可粘贴到其他应用。":failure;}
  void SafeReveal(){if(closed)return;string failure=RevealFailure(currentPath,FileRouter.Reveal);if(failure.Length>0)label.Text=failure;}
  internal static string RevealFailure(string path,Action<string> reveal){if(string.IsNullOrEmpty(path))return "请先打开图片，再查看文件位置。";try{reveal(path);return "";}catch(Exception error){return "无法打开文件位置："+error.Message;}}
  void UpdatePreviewTheme(){if(host==null||host.Child==null||host.Child.IsDisposed)return;host.Child.BackColor=Theme.DrawingColor("Canvas");if(picture!=null&&!picture.IsDisposed)picture.BackColor=host.Child.BackColor;foreach(var button in new[]{edgePrevious,edgeNext})if(button!=null&&!button.IsDisposed){button.BackColor=Theme.DrawingColor("Surface");button.ForeColor=Theme.DrawingColor("Text");}}
  void UpdatePlaybackVisibility(){bool visible=IsVisible&&WindowState!=WindowState.Minimized;if(picture!=null)picture.SetPlaybackVisible(visible);if(!visible&&drag!=null)drag.Cancel();}
  void Release(){pinButton.IsEnabled=false;edgePrevious=edgeNext=null;if(drag!=null){drag.Dispose();drag=null;}if(host!=null){root.Children.Remove(host);host.Dispose();host=null;}if(image!=null){image.Dispose();image=null;}viewport=null;picture=null;}
  CancellationTokenSource BeginRequest(){var previousRequest=scan;var request=new CancellationTokenSource();scan=request;directoryVersion++;if(previousRequest!=null)previousRequest.Cancel();return request;}
  bool IsCurrent(int stamp,CancellationToken token){return !closed&&stamp==directoryVersion&&!token.IsCancellationRequested;}
  void EndRequest(CancellationTokenSource request){if(ReferenceEquals(scan,request))scan=null;request.Dispose();}
  async void Open(string path){if(closed)return;CancellationTokenSource request=null;int stamp=directoryVersion;try{path=Path.GetFullPath(path);if(FileRouter.Kind(path)!="image")throw new NotSupportedException("请选择支持的图片格式。");stripCache.Clear();stripCacheOrder.Clear();request=BeginRequest();var token=request.Token;stamp=directoryVersion;currentPath=path;siblings=new[]{path};index=0;scanning=true;previous.IsEnabled=next.IsEnabled=false;
   // A newer drop/open cancels this request. Neither its decode nor its folder
   // discovery may replace the newest picture, status text or navigation list.
   await Load(path,token,stamp);if(!IsCurrent(stamp,token))return;
   try{var list=providedSequence!=null&&providedSequence.Contains(path,StringComparer.OrdinalIgnoreCase)?providedSequence:await Task.Run(()=>ImageDirectory.Read(path,token));if(!IsCurrent(stamp,token))return;siblings=list;index=Array.FindIndex(siblings,p=>string.Equals(p,currentPath,StringComparison.OrdinalIgnoreCase));}catch(OperationCanceledException){return;}catch(IOException){}catch(UnauthorizedAccessException){}
   if(IsCurrent(stamp,token)){scanning=false;UpdateInfo();RefreshFilmstrip();}
  }catch(OperationCanceledException){}catch(Exception ex){if(!closed&&stamp==directoryVersion&&(request!=null||!loading))label.Text="无法预览："+ex.Message;}finally{if(request!=null){if(IsCurrent(stamp,request.Token)){scanning=false;UpdateInfo();RefreshFilmstrip();}EndRequest(request);}}}
  async void Step(int step){if(closed||scanning||siblings==null||siblings.Length<2)return;var request=BeginRequest();int stamp=directoryVersion;index=(index+step+siblings.Length)%siblings.Length;currentPath=siblings[index];try{await Load(currentPath,request.Token,stamp);}finally{EndRequest(request);}}
  void UpdateInfo(){previous.IsEnabled=next.IsEnabled=!loading&&!scanning&&siblings!=null&&siblings.Length>1;pinButton.IsEnabled=!loading&&!pinning&&image!=null;if(image!=null){label.Text=Path.GetExtension(currentPath).TrimStart('.').ToUpperInvariant()+"  ·  "+image.Image.Width+" × "+image.Image.Height;label.ToolTip=FormatPreviewInfo.Notice(currentPath);}pageCount.Text=siblings==null?"—":(index+1)+" / "+siblings.Length;pageCount.ToolTip=scanning?"正在读取同目录图片…":currentPath;UpdateViewerControls();}  async Task Load(string path,CancellationToken token,int stamp){
   loading=true;previous.IsEnabled=next.IsEnabled=false;pinButton.IsEnabled=false;label.Text="正在打开…";UpdateViewerControls();
   try{
    if(FormatPreviewInfo.UsesPagedPreview(path)){Release();new DocumentPreviewWindow(this,path,Path.GetFileName(path)).Show();label.Text="AI 已打开连续预览；显示文件内全部导出的 PDF 页面。关闭预览后可继续切换同目录图片。";Title=DisplayName(path)+" · Nook";return;}
    var loaded=await PreviewImage.OpenAsync(path,token);if(!IsCurrent(stamp,token)){loaded.Dispose();return;}Release();image=loaded;var activeViewport=new PreviewViewport(image.Image.Width,image.Image.Height);viewport=activeViewport;
    var canvas=new PreviewCanvas{Dock=Forms.DockStyle.Fill,BackColor=Theme.DrawingColor("Canvas"),CopyImage=CopyCurrent};var activePicture=new PreviewPicture(image.Image,activeViewport,previewPreferences){BackColor=canvas.BackColor,BackgroundMode=backgroundMode};picture=activePicture;activePicture.PreviewBackgroundChanged+=delegate{backgroundMode=activePicture.BackgroundMode;};activePicture.AttachDesktopPin(PinCurrentAsync);activePicture.AttachCopy(CopyCurrent);canvas.Controls.Add(activePicture);AttachViewerSurface(canvas,activePicture);host=new WindowsFormsHost{Child=canvas};root.Children.Add(host);
    PreviewDragController activeDrag=null;Action draw=()=>{if(activePicture.IsDisposed)return;activePicture.Invalidate();if(activeDrag!=null)activeDrag.UpdateCursor();UpdateZoomLabel();};activeDrag=new PreviewDragController(canvas,activePicture,activeViewport,draw);drag=activeDrag;Forms.MouseEventHandler wheel=(s,e)=>{if(e.Delta==0)return;var handled=e as Forms.HandledMouseEventArgs;if(handled!=null)handled.Handled=true;var point=canvas.PointToClient(((Forms.Control)s).PointToScreen(e.Location));activeDrag.Cancel();activeViewport.ZoomAt(Math.Pow(1.15,e.Delta/120.0),point.X,point.Y);draw();};canvas.MouseWheel+=wheel;activePicture.MouseWheel+=wheel;activePicture.MouseEnter+=(s,e)=>canvas.Focus();canvas.Resize+=(s,e)=>{activeDrag.Cancel();activeViewport.Resize(canvas.ClientSize.Width,canvas.ClientSize.Height);draw();};canvas.Shortcut=HandleNativeKey;activePicture.MouseDoubleClick+=(s,e)=>{if(e.Button==Forms.MouseButtons.Left&&!activeDrag.EndDoubleClick())ToggleActualSize(e.X,e.Y);};UpdatePlaybackVisibility();UpdateLayout();activeViewport.Resize(canvas.ClientSize.Width,canvas.ClientSize.Height);draw();Title=DisplayName(path)+" · Nook";UpdateInfo();
   }catch(OperationCanceledException){}catch(Exception ex){if(IsCurrent(stamp,token)){Release();label.Text="无法预览："+ex.Message;}}finally{if(IsCurrent(stamp,token)){loading=false;UpdateInfo();RefreshFilmstrip();}}
  }
 }
}
