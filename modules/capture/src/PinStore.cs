using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace QingJie {
    public sealed class PinRecord {
        public string Id;
        public bool Active=true;
        public double Left=100,Top=100,Width=400,Opacity=1;
        public long ClosedAt;
        public bool IsTranslation,ShowTranslation=true;
        public string TranslationPolicy,TranslationProvider;
        public int QuarterTurns;
        public bool FlipHorizontal,FlipVertical;
    }
    public sealed class PinStore {
        public readonly string Folder;
        public List<PinRecord> Records=new List<PinRecord>();
        readonly Dictionary<string,PinWindow> windows=new Dictionary<string,PinWindow>();
        internal Action<PinWindow> ConfigureWindowForTests;
        bool loadedBackup;
        int newPlacement;
        public PinStore(string folder){Folder=folder;Directory.CreateDirectory(folder);Load();}
        string Manifest {get{return Path.Combine(Folder,"pins.json");}}
        public string ImagePath(PinRecord pin){Guid id;if(!Guid.TryParseExact(pin.Id,"N",out id))throw new InvalidDataException("贴图记录编号无效。");return Path.Combine(Folder,id.ToString("N")+".png");}
        public string TranslationPath(PinRecord pin){return Path.ChangeExtension(ImagePath(pin),"translated.png");}
        public void SaveTranslation(PinRecord pin,BitmapSource image,string provider=null){var path=TranslationPath(pin);ImageFiles.Write(image,path+".tmp");if(File.Exists(path))File.Replace(path+".tmp",path,null);else File.Move(path+".tmp",path);pin.ShowTranslation=true;pin.TranslationPolicy=ImageTranslation.LayoutPolicy;pin.TranslationProvider=provider;Save();}
        void Load(){
            foreach(var path in new[]{Manifest,Manifest+".bak"}){try{if(!File.Exists(path))continue;var rows=new JavaScriptSerializer().Deserialize<List<PinRecord>>(File.ReadAllText(path));if(rows==null)continue;
                foreach(var p in rows){try{if(p!=null&&File.Exists(ImagePath(p))&&!Records.Any(r=>r.Id==p.Id)){p.Left=Geometry.Clamp(p.Left,-100000,100000);p.Top=Geometry.Clamp(p.Top,-100000,100000);p.Width=Geometry.Clamp(p.Width,80,2500);p.Opacity=Geometry.Clamp(p.Opacity,.15,1);Records.Add(p);}}catch(InvalidDataException){}}
                loadedBackup=path!=Manifest;break;
            }catch{}}
            // If a power loss interrupted the index update, keep its PNG recoverable with F3.
            foreach(var path in Directory.GetFiles(Folder,"*.png")){Guid id;string name=Path.GetFileNameWithoutExtension(path);if(Guid.TryParseExact(name,"N",out id)&&!Records.Any(p=>p.Id==name))Records.Add(new PinRecord{Id=name,Active=false,ClosedAt=File.GetLastWriteTimeUtc(path).Ticks});}
        }
        public void Save(){var temp=Manifest+".tmp";using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){using(var writer=new StreamWriter(stream,System.Text.Encoding.UTF8,1024,true)){writer.Write(new JavaScriptSerializer().Serialize(Records));writer.Flush();}stream.Flush(true);}if(File.Exists(Manifest))File.Replace(temp,Manifest,loadedBackup?null:Manifest+".bak");else File.Move(temp,Manifest);loadedBackup=false;}
        public PinRecord Add(BitmapSource image){var record=new PinRecord{Id=Guid.NewGuid().ToString("N"),Width=Math.Min(720,image.PixelWidth)};var target=ImagePath(record);var temp=target+".tmp";ImageFiles.Write(image,temp);File.Move(temp,target);Records.Add(record);Save();return record;}
        // Only newly pasted/OCR pins follow the cursor monitor. Explicit capture
        // bounds and restored records keep their existing placement paths.
        public void New(BitmapSource image){
            var cursor=System.Windows.Forms.Cursor.Position;var screen=System.Windows.Forms.Screen.FromPoint(cursor);var dpi=MonitorDpi(cursor);
            // The existing bounds path may create its WPF handle on a different
            // monitor first. Reserve that frame's larger DPI too, without
            // changing NewAt or the persisted logical-coordinate format.
            foreach(var monitor in System.Windows.Forms.Screen.AllScreens){var other=MonitorDpi(monitor.Bounds.Location);dpi=new DpiScale(Math.Max(dpi.DpiScaleX,other.DpiScaleX),Math.Max(dpi.DpiScaleY,other.DpiScaleY));}
            var bounds=InitialBounds(image.PixelWidth,image.PixelHeight,screen.WorkingArea,dpi.DpiScaleX,dpi.DpiScaleY,newPlacement++);var record=Add(image);Show(record,bounds);
        }
        internal static Int32Rect InitialBounds(int width,int height,System.Drawing.Rectangle work,double dpiScaleX,double dpiScaleY,int cascade){
            if(width<1)throw new ArgumentOutOfRangeException("width");if(height<1)throw new ArgumentOutOfRangeException("height");if(work.Width<1||work.Height<1)throw new ArgumentOutOfRangeException("work");
            if(double.IsNaN(dpiScaleX)||double.IsInfinity(dpiScaleX)||dpiScaleX<=0)dpiScaleX=1;if(double.IsNaN(dpiScaleY)||double.IsInfinity(dpiScaleY)||dpiScaleY<=0)dpiScaleY=1;
            // All values below are physical pixels, including negative screen
            // origins. Leave a 12-DIP gap beyond the frame; never upscale pixels.
            int marginX=(int)Math.Min((work.Width-1)/2,Math.Ceiling((PinFrame.Inset+12)*dpiScaleX)),marginY=(int)Math.Min((work.Height-1)/2,Math.Ceiling((PinFrame.Inset+12)*dpiScaleY));
            double scale=Math.Min(1,Math.Min(720.0/width,Math.Min((work.Width-2*marginX)/(double)width,(work.Height-2*marginY)/(double)height)));
            int w=Math.Max(1,(int)Math.Floor(width*scale)),h=Math.Max(1,(int)Math.Floor(height*scale));int step=1+Math.Abs(cascade%8);
            int x=(int)Math.Min(work.Width-w-marginX,Math.Max(marginX,Math.Ceiling(24*dpiScaleX*step))),y=(int)Math.Min(work.Height-h-marginY,Math.Max(marginY,Math.Ceiling(24*dpiScaleY*step)));
            return new Int32Rect(work.Left+x,work.Top+y,w,h);
        }
        static DpiScale MonitorDpi(System.Drawing.Point point){
            try{uint x,y;if(GetDpiForMonitor(MonitorFromPoint(point,2),0,out x,out y)==0&&x>0&&y>0)return new DpiScale(x/96.0,y/96.0);}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
            return new DpiScale(1,1);
        }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(System.Drawing.Point point,uint flags);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor,int type,out uint dpiX,out uint dpiY);
        public void NewAt(BitmapSource image,Int32Rect bounds){var record=Add(image);Show(record,bounds);}
        public Task NewAsync(BitmapSource image,CancellationToken cancel){
            var cursor=System.Windows.Forms.Cursor.Position;var screen=System.Windows.Forms.Screen.FromPoint(cursor);var dpi=MonitorDpi(cursor);
            foreach(var monitor in System.Windows.Forms.Screen.AllScreens){var other=MonitorDpi(monitor.Bounds.Location);dpi=new DpiScale(Math.Max(dpi.DpiScaleX,other.DpiScaleX),Math.Max(dpi.DpiScaleY,other.DpiScaleY));}
            return NewAtAsync(image,InitialBounds(image.PixelWidth,image.PixelHeight,screen.WorkingArea,dpi.DpiScaleX,dpi.DpiScaleY,newPlacement++),cancel);
        }
        public Task NewAtAsync(BitmapSource image,Int32Rect bounds,CancellationToken cancel){return CreatePinAsync(image,bounds,cancel,(record,area)=>Show(record,area));}
        // Encoding is the heavy part. Keep it off the dispatcher, while index
        // changes and native window presentation remain serialized on the UI.
        // The injected presenter is also used by inert lifecycle regressions.
        internal async Task CreatePinAsync(BitmapSource image,Int32Rect bounds,CancellationToken cancel,Action<PinRecord,Int32Rect> present){
            if(image==null)throw new ArgumentNullException("image");if(present==null)throw new ArgumentNullException("present");
            var frozen=image.IsFrozen?image:(BitmapSource)image.GetCurrentValueAsFrozen();
            var record=new PinRecord{Id=Guid.NewGuid().ToString("N"),Width=Math.Min(720,image.PixelWidth)};string target=ImagePath(record);bool registered=false;
            try{
                await Task.Run(()=>ImageFiles.WriteAtomic(target,stream=>{var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(frozen));encoder.Save(stream);},cancel),cancel);
                cancel.ThrowIfCancellationRequested();Records.Add(record);registered=true;Save();
                cancel.ThrowIfCancellationRequested();present(record,bounds);
            }catch{
                bool canRemoveImage=true;
                if(registered){
                    Records.Remove(record);
                    try{Save();}catch(Exception rollback){
                        // Do not erase bytes if a failed rollback might leave a
                        // manifest pointing at them. The PNG remains recoverable.
                        canRemoveImage=false;LogFailure("Rollback new pin "+record.Id,rollback);
                    }
                }
                if(canRemoveImage)try{if(File.Exists(target))File.Delete(target);}catch(Exception cleanup){LogFailure("Remove failed new pin "+record.Id,cleanup);}
                throw;
            }
        }
        public void NewTranslation(BitmapSource image){var record=Add(image);record.IsTranslation=true;Save();Show(record).StartTranslation();}
        public void Restore(){int failed=0;foreach(var pin in Records.Where(p=>p.Active).ToList()){try{Show(pin,null,false);}catch(Exception error){failed++;LogFailure("Restore "+pin.Id,error);}}if(failed>0)AppState.Notify(failed+" 张贴图暂未恢复，图片仍保留。可使用“显示 / 隐藏全部贴图”重试。");}
        internal void LogFailure(string action,Exception error){try{string path=Path.Combine(Folder,"pin-errors.log");if(File.Exists(path)&&new FileInfo(path).Length>131072)File.Move(path,path+"."+DateTime.UtcNow.Ticks);File.AppendAllText(path,DateTime.UtcNow.ToString("O")+" "+action+Environment.NewLine+error+Environment.NewLine);}catch{}}
        PinWindow Show(PinRecord pin,Int32Rect? bounds=null,bool activate=true){PinWindow existing;if(windows.TryGetValue(pin.Id,out existing)){existing.ShowActivated=activate;if(!existing.IsVisible)existing.Show();if(activate)existing.Activate();return existing;}var w=new PinWindow(this,pin,Snapshot.Load(ImagePath(pin)),bounds){ShowActivated=activate};try{if(ConfigureWindowForTests!=null)ConfigureWindowForTests(w);windows[pin.Id]=w;w.Show();return w;}catch{windows.Remove(pin.Id);w.Discard();throw;}}
        // Hide is not Close: keep the active set, geometry and original PNGs.
        // A newly created pin remains visible even if the older group is hidden.
        public bool? ToggleVisibility(){var active=Records.Where(p=>p.Active).ToList();if(active.Count==0)return null;bool hide=windows.Values.Any(w=>w.IsVisible);if(hide){foreach(var w in windows.Values.ToList())w.Hide();return false;}Restore();return windows.Values.Any(w=>w.IsVisible)?(bool?)true:null;}
        public PinRecord LastClosed(){return Records.Where(p=>!p.Active).OrderByDescending(p=>p.ClosedAt).FirstOrDefault();}
        public bool Recover(){var p=LastClosed();if(p==null)return false;try{Show(p);p.Active=true;Save();return true;}catch{p.Active=windows.ContainsKey(p.Id);throw;}}
        public void Removed(string id){windows.Remove(id);}
        public void PersistAll(){foreach(var w in windows.Values.ToList())w.UpdateRecord();Save();}
    }
    public sealed class PinWindow : Window {
        readonly PinStore store;
        readonly PinRecord pin;
        readonly BitmapSource image;
        BitmapSource translated,displayed;
        OcrPage translatedPage;
        ImageTranslationResult translationResult;
        string translatedProvider,cacheWarning;
        System.Threading.CancellationTokenSource translationRequest;
        readonly Image picture=new Image {Name="PinPicture",Stretch=Stretch.Fill};
        readonly TextBlock translationStatus=new TextBlock {Foreground=Brushes.White,FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8)};
        Border statusCard;
        Button translationButton;
        bool translating,closed,discarded;
        readonly System.Threading.CancellationTokenSource cancel=new System.Threading.CancellationTokenSource();
        readonly DispatcherTimer saveTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(350)};
        bool initialized;
        public PinWindow(PinStore store,PinRecord pin,BitmapSource image,Int32Rect? bounds=null) {
            this.store=store;this.pin=pin;this.image=image;displayed=image;Title=pin.IsTranslation?"Nook · 翻译贴图":"Nook · 贴图";WindowStyle=WindowStyle.None;AllowsTransparency=true;ResizeMode=ResizeMode.NoResize;Topmost=true;ShowInTaskbar=false;Background=Brushes.Transparent;
            // Legacy raster caches can contain clipped or incomplete translations.
            // Keep their files recoverable, but do not display them as a new result.
            bool oldTranslation=pin.IsTranslation&&File.Exists(store.TranslationPath(pin))&&(pin.TranslationPolicy!=ImageTranslation.LayoutPolicy||pin.TranslationProvider!=Translation.Provider);
            if(pin.IsTranslation&&!oldTranslation&&File.Exists(store.TranslationPath(pin))){try{translated=Snapshot.Load(store.TranslationPath(pin));if(pin.ShowTranslation)displayed=translated;}catch{}}
            displayed=PinTransforms.Apply(displayed,pin.QuarterTurns,pin.FlipHorizontal,pin.FlipVertical);
            double contentWidth=Geometry.Clamp(pin.Width,80,2500);Width=contentWidth+2*PinFrame.Inset;Height=contentWidth*displayed.PixelHeight/displayed.PixelWidth+2*PinFrame.Inset;Opacity=Geometry.Clamp(pin.Opacity,.15,1);
            Left=pin.Left-PinFrame.Inset;Top=pin.Top-PinFrame.Inset;
            bool visible=System.Windows.Forms.Screen.AllScreens.Any(s=>s.WorkingArea.IntersectsWith(new System.Drawing.Rectangle((int)Left,(int)Top,(int)Width,(int)Height)));
            if(!visible){Left=100-PinFrame.Inset;Top=100-PinFrame.Inset;}
            var root=new Grid();picture.Source=displayed;root.Children.Add(picture);
            var frame=new PinFrame(root);Content=frame;var toolbar=new PinToolbar(root,frame.SetHovered);var tools=toolbar.Tools;
            if(pin.IsTranslation){translationButton=PinToolbar.Button("translate","原图 / 译图 · 未完成时点击重试",()=>{if(translated==null)StartTranslation();else{pin.ShowTranslation=!pin.ShowTranslation;RefreshImage(false);TranslationResultStatus();}});tools.Children.Add(translationButton);}
            tools.Children.Add(PinToolbar.Button("ocr","打开图片 / 全部文字",OpenText));
            var transforms=Ui.Menu(this);
            Action<string,Action> add=(title,action)=>{var item=new MenuItem {Header=title};item.Click+=(s,e)=>Try(()=>{action();Activate();});transforms.Items.Add(item);};
            add("顺时针旋转 90°",()=>{PinTransforms.Rotate(pin,1);RefreshImage(true);});add("逆时针旋转 90°",()=>{PinTransforms.Rotate(pin,-1);RefreshImage(true);});
            add("水平翻转",()=>{pin.FlipHorizontal=!pin.FlipHorizontal;RefreshImage(false);});add("垂直翻转",()=>{pin.FlipVertical=!pin.FlipVertical;RefreshImage(false);});
            transforms.Items.Add(new Separator());add("恢复原始方向",()=>{pin.QuarterTurns=0;pin.FlipHorizontal=pin.FlipVertical=false;RefreshImage(true);});
            var transformButton=PinToolbar.Button("rotate","旋转 / 翻转",()=>{});transformButton.ContextMenu=transforms;transformButton.Click+=(s,e)=>{toolbar.MenuOpen=true;transforms.PlacementTarget=transformButton;transforms.IsOpen=true;};
            transforms.Closed+=(s,e)=>{toolbar.MenuOpen=false;toolbar.ScheduleHide();};tools.Children.Add(transformButton);
            tools.Children.Add(PinToolbar.Button("copy","复制贴图",()=>Try(()=>ImageFiles.Copy(displayed))));tools.Children.Add(PinToolbar.Button("save","另存为",()=>Try(()=>ImageFiles.Save(displayed,this))));tools.Children.Add(PinToolbar.Button("close","收起贴图 · F3 找回",Close));
            statusCard=new Border {Name="PinTranslationStatus",Child=translationStatus,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(4),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(6),MaxWidth=340,Visibility=Visibility.Collapsed};Ui.Theme(translationStatus,TextBlock.ForegroundProperty,"Text");Ui.Theme(statusCard,Border.BackgroundProperty,"Surface");Ui.Theme(statusCard,Border.BorderBrushProperty,"Border");root.Children.Add(statusCard);
            if(oldTranslation)Status("翻译规则已更新，原图保留。点击翻译可重新生成。");
            root.MouseLeftButtonDown+=(s,e)=>{Activate();toolbar.Hide();try{DragMove();}catch{}if(root.IsMouseOver)toolbar.Show();};
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
            MouseWheel+=(s,e)=>{if((Keyboard.Modifiers&ModifierKeys.Control)!=0)Opacity=Geometry.Clamp(Opacity+(e.Delta>0?.05:-.05),.15,1);else{double width=Geometry.Clamp((Width-2*PinFrame.Inset)*(e.Delta>0?1.1:1/1.1),80,2500);Width=width+2*PinFrame.Inset;Height=width*displayed.PixelHeight/displayed.PixelWidth+2*PinFrame.Inset;}Changed();e.Handled=true;};
            LocationChanged+=(s,e)=>{Changed();toolbar.Reposition();};SizeChanged+=(s,e)=>{Changed();toolbar.Reposition();};
            Loaded+=(s,e)=>{if(bounds.HasValue){var b=bounds.Value;var dpi=VisualTreeHelper.GetDpi(this);int px=(int)Math.Round(PinFrame.Inset*dpi.DpiScaleX),py=(int)Math.Round(PinFrame.Inset*dpi.DpiScaleY);Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle,Native.Topmost,b.X-px,b.Y-py,b.Width+2*px,b.Height+2*py,0x0040);}initialized=true;Changed();if(ShowActivated){Activate();Focus();}};
            IsVisibleChanged+=(s,e)=>{if(!IsVisible){toolbar.MenuOpen=false;transforms.IsOpen=false;toolbar.Hide();}};
            saveTimer.Tick+=(s,e)=>{saveTimer.Stop();Try(()=>{UpdateRecord();store.Save();});};
            Closing+=(s,e)=>{saveTimer.Stop();if(discarded)return;UpdateRecord();if(!AppState.Quitting){pin.Active=false;pin.ClosedAt=DateTime.UtcNow.Ticks;}try{store.Save();}catch{pin.Active=true;e.Cancel=true;MessageBox.Show(this,"贴图状态暂时无法保存，已保留这张贴图。请检查磁盘空间。","Nook");}};
            AppState.TranslationProviderChanged+=RefreshTranslationProvider;
            Closed+=(s,e)=>{closed=true;AppState.TranslationProviderChanged-=RefreshTranslationProvider;toolbar.Dispose();transforms.IsOpen=false;cancel.Cancel();translatedPage=null;translationResult=null;store.Removed(pin.Id);};
        }
        void Status(string text){translationStatus.Text=text;statusCard.Visibility=Visibility.Visible;}
        void OpenText(){
            Try(()=>{
                if(translationResult!=null&&translatedPage!=null&&translatedProvider==Translation.Provider)new TextWindow(image,"zh",translatedPage,translationResult,translatedProvider).Show();
                else new TextWindow(PinTransforms.Apply(image,pin.QuarterTurns,pin.FlipHorizontal,pin.FlipVertical)).Show();
            });
        }
        void TranslationResultStatus(){
            if(translationResult==null||translationResult.UnplacedCount==0){if(!string.IsNullOrEmpty(cacheWarning))Status(cacheWarning);else statusCard.Visibility=Visibility.Collapsed;return;}
            Status("有 "+translationResult.UnplacedCount+" 处保留原文，未缓存不完整译图。 ");
            var link=new Hyperlink(new Run("查看完整译文"));link.Click+=(s,e)=>{e.Handled=true;OpenText();};translationStatus.Inlines.Add(link);
        }
        void RefreshTranslationProvider(){
            if(closed||!pin.IsTranslation)return;if(translationRequest!=null){translationRequest.Cancel();translationRequest=null;}
            translating=false;translationResult=null;translatedPage=null;translated=null;translatedProvider=null;cacheWarning=null;pin.ShowTranslation=false;
            if(translationButton!=null)translationButton.IsEnabled=true;RefreshImage(false);Status("翻译服务已改变，点击翻译重新生成。");
        }
        public async void StartTranslation(){
            if(translating||closed)return;var request=System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);translationRequest=request;string provider=Translation.Provider;translating=true;cacheWarning=null;if(translationButton!=null)translationButton.IsEnabled=false;Status("正在本机识别图片文字…");
            try{var page=await OcrService.Read(image,request.Token);request.Token.ThrowIfCancellationRequested();if(closed||translationRequest!=request)return;var regions=ImageTranslation.Regions(page);if(regions.Count==0){Status("未识别到文字，原图已保留。");return;}
                Status("正在翻译 · 只发送文字，不上传截图…");var texts=await Translation.TranslateImage(regions.Select(r=>r.Text).ToArray(),request.Token,"zh",provider);request.Token.ThrowIfCancellationRequested();if(closed||translationRequest!=request||provider!=Translation.Provider)return;
                translationResult=ImageTranslation.RenderWithStatus(image,regions,texts);translatedPage=page;translatedProvider=provider;translated=translationResult.Image;
                // A partial preview is useful in this session, but must not become
                // a silent, permanently cached mixed-language image after restart.
                pin.ShowTranslation=true;
                if(translationResult.UnplacedCount==0){try{store.SaveTranslation(pin,translated,provider);}catch(Exception error){cacheWarning="本次译图可用，但未能保存；重启后需重新翻译。 "+error.Message;}}
                RefreshImage(false);TranslationResultStatus();
            }catch(OperationCanceledException){if(!closed&&translationRequest==request&&!request.IsCancellationRequested)Status("翻译超时，点上方翻译按钮重试。原图已保留。");}catch(Exception ex){if(!closed&&translationRequest==request)Status(ex.Message+" 点击上方翻译按钮可重试。");}
            finally{if(translationRequest==request){translationRequest=null;translating=false;if(!closed&&translationButton!=null)translationButton.IsEnabled=true;}request.Dispose();}
        }
        void Try(Action action){try{action();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Nook");}}
        void RefreshImage(bool maintainScale){double scale=(Width-2*PinFrame.Inset)/displayed.PixelWidth;displayed=PinTransforms.Apply(pin.ShowTranslation&&translated!=null?translated:image,pin.QuarterTurns,pin.FlipHorizontal,pin.FlipVertical);picture.Source=displayed;if(maintainScale)Width=Geometry.Clamp(displayed.PixelWidth*scale,80,2500)+2*PinFrame.Inset;Height=(Width-2*PinFrame.Inset)*displayed.PixelHeight/displayed.PixelWidth+2*PinFrame.Inset;Changed();}
        void Changed(){if(!initialized)return;saveTimer.Stop();saveTimer.Start();}
        public void UpdateRecord(){pin.Left=Left+PinFrame.Inset;pin.Top=Top+PinFrame.Inset;pin.Width=Width-2*PinFrame.Inset;pin.Opacity=Opacity;}
        internal void Discard(){discarded=true;saveTimer.Stop();try{Close();}catch{}}
    }
}
