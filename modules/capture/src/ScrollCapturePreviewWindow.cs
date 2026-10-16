using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    internal sealed class ScrollCapturePreviewWindow : Window {
        BitmapSource image;
        readonly Image preview=new Image {Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top};
        readonly TextBlock status=new TextBlock {Margin=new Thickness(12,5,12,5),VerticalAlignment=VerticalAlignment.Center};
        readonly StackPanel actions=new StackPanel {Orientation=Orientation.Horizontal};
        bool collecting,closed;
        internal ScrollCapturePreviewWindow(BitmapSource image){
            if(image==null)throw new ArgumentNullException("image");this.image=image;
            Title="Nook · 长截图预览";Icon=Ui.AppIcon;Width=900;Height=720;MinWidth=420;MinHeight=260;WindowStartupLocation=WindowStartupLocation.CenterScreen;FontFamily=new FontFamily("Microsoft YaHei UI");
            Ui.Theme(this,Control.BackgroundProperty,"Panel");Ui.Theme(status,TextBlock.ForegroundProperty,"Muted");
            var root=new DockPanel();var bar=new DockPanel();DockPanel.SetDock(bar,Dock.Top);
            actions.Children.Add(Ui.TextButton("复制",()=>Run(()=>{ImageFiles.Copy(this.image);Close();})));
            actions.Children.Add(Ui.TextButton("保存…",()=>Run(()=>{if(ImageFiles.Save(this.image,this))Close();}),true));
            if(AppState.Pins!=null)actions.Children.Add(Ui.Tool("pin","贴到屏幕",()=>Run(()=>{var pixels=this.image;AppState.Pins.New(pixels);Close();})));
            if(MoriDesk.DeskRuntime.CollectImage!=null)actions.Children.Add(Ui.Tool("library","收藏到素材库",async ()=>await Collect()));
            DockPanel.SetDock(actions,Dock.Right);bar.Children.Add(actions);status.Text=image.PixelWidth+" × "+image.PixelHeight+" · 滚动查看，可复制或保存";bar.Children.Add(status);root.Children.Add(bar);
            var viewport=new ScrollViewer {VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,CanContentScroll=false,Padding=new Thickness(16)};
            preview.Source=image;preview.Width=image.PixelWidth;preview.MaxWidth=820;viewport.Content=preview;root.Children.Add(viewport);Content=root;
            // ViewportWidth is still zero during the first SizeChanged event.
            // Fit only after ScrollViewer has measured its actual content port;
            // its width already excludes padding and the vertical scrollbar.
            viewport.ScrollChanged+=(s,e)=>{if(e.ViewportWidthChange!=0&&viewport.ViewportWidth>0)preview.MaxWidth=viewport.ViewportWidth;};
            PreviewKeyDown+=(s,e)=>{if(e.Key==Key.Escape){e.Handled=true;Close();}else if(!collecting&&Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.C){e.Handled=true;Run(()=>{ImageFiles.Copy(this.image);Close();});}else if(!collecting&&Keyboard.Modifiers==ModifierKeys.Control&&e.Key==Key.S){e.Handled=true;Run(()=>{if(ImageFiles.Save(this.image,this))Close();});}};
            Closed+=(s,e)=>{closed=true;preview.Source=null;this.image=null;};
        }
        void Run(Action action){if(closed||collecting)return;try{action();}catch(Exception ex){status.Text="未完成："+ex.Message;}}
        async System.Threading.Tasks.Task Collect(){
            if(closed||collecting)return;var collect=MoriDesk.DeskRuntime.CollectImage;if(collect==null)return;collecting=true;actions.IsEnabled=false;status.Text="正在收藏到素材库…";
            try{bool saved=await collect(image);if(!closed){if(saved)Close();else status.Text="尚未收藏，长截图已保留，可重试或保存。";}}
            catch(Exception ex){if(!closed)status.Text="收藏失败："+ex.Message;}
            finally{collecting=false;if(!closed)actions.IsEnabled=true;}
        }
    }
}
