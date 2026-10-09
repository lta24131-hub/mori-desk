using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
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
        public int QuarterTurns;
        public bool FlipHorizontal,FlipVertical;
    }
    public sealed class PinStore {
        public readonly string Folder;
        public List<PinRecord> Records=new List<PinRecord>();
        readonly Dictionary<string,PinWindow> windows=new Dictionary<string,PinWindow>();
        bool loadedBackup;
        public PinStore(string folder){Folder=folder;Directory.CreateDirectory(folder);Load();}
        string Manifest {get{return Path.Combine(Folder,"pins.json");}}
        public string ImagePath(PinRecord pin){Guid id;if(!Guid.TryParseExact(pin.Id,"N",out id))throw new InvalidDataException("贴图记录编号无效。");return Path.Combine(Folder,id.ToString("N")+".png");}
        public string TranslationPath(PinRecord pin){return Path.ChangeExtension(ImagePath(pin),"translated.png");}
        public void SaveTranslation(PinRecord pin,BitmapSource image){var path=TranslationPath(pin);ImageFiles.Write(image,path+".tmp");if(File.Exists(path))File.Replace(path+".tmp",path,null);else File.Move(path+".tmp",path);pin.ShowTranslation=true;Save();}
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
        public void New(BitmapSource image){var record=Add(image);Show(record);}
        public void NewAt(BitmapSource image,Int32Rect bounds){var record=Add(image);Show(record,bounds);}
        public void NewTranslation(BitmapSource image){var record=Add(image);record.IsTranslation=true;Save();Show(record).StartTranslation();}
        public void Restore(){foreach(var pin in Records.Where(p=>p.Active).ToList()){try{Show(pin);}catch{AppState.Notify("一张贴图恢复失败，原始图片仍保留在本机贴图文件夹。");}}}
        PinWindow Show(PinRecord pin,Int32Rect? bounds=null){if(windows.ContainsKey(pin.Id)){windows[pin.Id].Activate();return windows[pin.Id];}var w=new PinWindow(this,pin,Snapshot.Load(ImagePath(pin)),bounds);windows[pin.Id]=w;w.Show();return w;}
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
        readonly Image picture=new Image {Stretch=Stretch.Fill};
        readonly TextBlock translationStatus=new TextBlock {Foreground=Brushes.White,FontSize=12,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(8)};
        Border statusCard;
        Button translationButton;
        bool translating,closed;
        readonly System.Threading.CancellationTokenSource cancel=new System.Threading.CancellationTokenSource();
        readonly DispatcherTimer saveTimer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(350)};
        bool initialized;
        public PinWindow(PinStore store,PinRecord pin,BitmapSource image,Int32Rect? bounds=null) {
            this.store=store;this.pin=pin;this.image=image;displayed=image;Title=pin.IsTranslation?"Luma · 翻译贴图":"Luma · 贴图";WindowStyle=WindowStyle.None;AllowsTransparency=true;ResizeMode=ResizeMode.NoResize;Topmost=true;ShowInTaskbar=false;Background=Brushes.Transparent;
            if(pin.IsTranslation&&File.Exists(store.TranslationPath(pin))){try{translated=Snapshot.Load(store.TranslationPath(pin));if(pin.ShowTranslation)displayed=translated;}catch{}}
            displayed=PinTransforms.Apply(displayed,pin.QuarterTurns,pin.FlipHorizontal,pin.FlipVertical);
            double contentWidth=Geometry.Clamp(pin.Width,80,2500);Width=contentWidth+2*PinFrame.Inset;Height=contentWidth*displayed.PixelHeight/displayed.PixelWidth+2*PinFrame.Inset;Opacity=Geometry.Clamp(pin.Opacity,.15,1);
            Left=pin.Left-PinFrame.Inset;Top=pin.Top-PinFrame.Inset;
            bool visible=System.Windows.Forms.Screen.AllScreens.Any(s=>s.WorkingArea.IntersectsWith(new System.Drawing.Rectangle((int)Left,(int)Top,(int)Width,(int)Height)));
            if(!visible){Left=100-PinFrame.Inset;Top=100-PinFrame.Inset;}
            var root=new Grid();picture.Source=displayed;root.Children.Add(picture);
            var frame=new PinFrame(root);Content=frame;var toolbar=new PinToolbar(root,frame.SetHovered);var tools=toolbar.Tools;
            if(pin.IsTranslation){translationButton=PinToolbar.Button("translate","原图 / 译图 · 未完成时点击重试",()=>{if(translated==null)StartTranslation();else{pin.ShowTranslation=!pin.ShowTranslation;RefreshImage(false);}});tools.Children.Add(translationButton);}
            tools.Children.Add(PinToolbar.Button("ocr","打开图片 / 全部文字",()=>new TextWindow(PinTransforms.Apply(image,pin.QuarterTurns,pin.FlipHorizontal,pin.FlipVertical)).Show()));
            var transforms=new ContextMenu();
            Action<string,Action> add=(title,action)=>{var item=new MenuItem {Header=title};item.Click+=(s,e)=>Try(()=>{action();Activate();});transforms.Items.Add(item);};
            add("顺时针旋转 90°",()=>{PinTransforms.Rotate(pin,1);RefreshImage(true);});add("逆时针旋转 90°",()=>{PinTransforms.Rotate(pin,-1);RefreshImage(true);});
            add("水平翻转",()=>{pin.FlipHorizontal=!pin.FlipHorizontal;RefreshImage(false);});add("垂直翻转",()=>{pin.FlipVertical=!pin.FlipVertical;RefreshImage(false);});
            transforms.Items.Add(new Separator());add("恢复原始方向",()=>{pin.QuarterTurns=0;pin.FlipHorizontal=pin.FlipVertical=false;RefreshImage(true);});
            var transformButton=PinToolbar.Button("rotate","旋转 / 翻转",()=>{});transformButton.ContextMenu=transforms;transformButton.Click+=(s,e)=>{toolbar.MenuOpen=true;transforms.PlacementTarget=transformButton;transforms.IsOpen=true;};
            transforms.Closed+=(s,e)=>{toolbar.MenuOpen=false;toolbar.ScheduleHide();};tools.Children.Add(transformButton);
            tools.Children.Add(PinToolbar.Button("copy","复制贴图",()=>Try(()=>ImageFiles.Copy(displayed))));tools.Children.Add(PinToolbar.Button("save","另存为",()=>Try(()=>ImageFiles.Save(displayed,this))));tools.Children.Add(PinToolbar.Button("close","收起贴图 · F3 找回",Close));
            statusCard=new Border {Child=translationStatus,Background=Ui.Brush("#DD25322B"),CornerRadius=new CornerRadius(4),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(6),MaxWidth=340,Visibility=Visibility.Collapsed};root.Children.Add(statusCard);
            root.MouseLeftButtonDown+=(s,e)=>{Activate();toolbar.Hide();try{DragMove();}catch{}if(root.IsMouseOver)toolbar.Show();};
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}};
            MouseWheel+=(s,e)=>{if((Keyboard.Modifiers&ModifierKeys.Control)!=0)Opacity=Geometry.Clamp(Opacity+(e.Delta>0?.05:-.05),.15,1);else{double width=Geometry.Clamp((Width-2*PinFrame.Inset)*(e.Delta>0?1.1:1/1.1),80,2500);Width=width+2*PinFrame.Inset;Height=width*displayed.PixelHeight/displayed.PixelWidth+2*PinFrame.Inset;}Changed();e.Handled=true;};
            LocationChanged+=(s,e)=>{Changed();toolbar.Reposition();};SizeChanged+=(s,e)=>{Changed();toolbar.Reposition();};
            Loaded+=(s,e)=>{if(bounds.HasValue){var b=bounds.Value;var dpi=VisualTreeHelper.GetDpi(this);int px=(int)Math.Round(PinFrame.Inset*dpi.DpiScaleX),py=(int)Math.Round(PinFrame.Inset*dpi.DpiScaleY);Native.SetWindowPos(new System.Windows.Interop.WindowInteropHelper(this).Handle,Native.Topmost,b.X-px,b.Y-py,b.Width+2*px,b.Height+2*py,0x0040);}initialized=true;Changed();Activate();Focus();};
            saveTimer.Tick+=(s,e)=>{saveTimer.Stop();Try(()=>{UpdateRecord();store.Save();});};
            Closing+=(s,e)=>{saveTimer.Stop();UpdateRecord();if(!AppState.Quitting){pin.Active=false;pin.ClosedAt=DateTime.UtcNow.Ticks;}try{store.Save();}catch{pin.Active=true;e.Cancel=true;MessageBox.Show(this,"贴图状态暂时无法保存，已保留这张贴图。请检查磁盘空间。","Luma");}};
            Closed+=(s,e)=>{closed=true;toolbar.Dispose();transforms.IsOpen=false;cancel.Cancel();store.Removed(pin.Id);};
        }
        void Status(string text){translationStatus.Text=text;statusCard.Visibility=Visibility.Visible;}
        public async void StartTranslation(){
            if(translating||closed)return;translating=true;if(translationButton!=null)translationButton.IsEnabled=false;Status("正在本机识别图片文字…");
            try{var page=await OcrService.Read(image,cancel.Token);if(closed)return;var regions=ImageTranslation.Regions(page);if(regions.Count==0){Status("未识别到文字，原图已保留。");return;}
                Status("正在翻译 · 只发送文字，不上传截图…");var texts=await Translation.TranslateImage(regions.Select(r=>r.Text).ToArray(),cancel.Token);if(closed)return;
                translated=ImageTranslation.Render(image,regions,texts);store.SaveTranslation(pin,translated);RefreshImage(false);statusCard.Visibility=Visibility.Collapsed;
            }catch(OperationCanceledException){if(!closed)Status("翻译超时，点上方翻译按钮重试。原图已保留。");}catch(Exception ex){if(!closed)Status(ex.Message+" 点击上方翻译按钮可重试。");}
            finally{translating=false;if(!closed&&translationButton!=null)translationButton.IsEnabled=true;}
        }
        void Try(Action action){try{action();}catch(Exception ex){MessageBox.Show(this,ex.Message,"Luma");}}
        void RefreshImage(bool maintainScale){double scale=(Width-2*PinFrame.Inset)/displayed.PixelWidth;displayed=PinTransforms.Apply(pin.ShowTranslation&&translated!=null?translated:image,pin.QuarterTurns,pin.FlipHorizontal,pin.FlipVertical);picture.Source=displayed;if(maintainScale)Width=Geometry.Clamp(displayed.PixelWidth*scale,80,2500)+2*PinFrame.Inset;Height=(Width-2*PinFrame.Inset)*displayed.PixelHeight/displayed.PixelWidth+2*PinFrame.Inset;Changed();}
        void Changed(){if(!initialized)return;saveTimer.Stop();saveTimer.Start();}
        public void UpdateRecord(){pin.Left=Left+PinFrame.Inset;pin.Top=Top+PinFrame.Inset;pin.Width=Width-2*PinFrame.Inset;pin.Opacity=Opacity;}
    }
}
