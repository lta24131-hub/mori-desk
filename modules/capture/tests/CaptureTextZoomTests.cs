using System;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Scale geometry and an unshown viewer only; no desktop or OCR service calls.
static class CaptureTextZoomTests {
    static bool Near(double value,double expected){return Math.Abs(value-expected)<.00001;}
    internal static void Run(Action<bool,string> check){
        var pixels=new Size(1200,800);
        foreach(double factor in new[]{1,1.25,1.5,1.75,2,2.5,3}){
            var dpi=new DpiScale(factor,factor);
            foreach(double zoom in new[]{.25,.5,1,2,8}){
                var size=TextPreviewScale.LogicalSize(pixels,zoom,dpi);
                check(Near(size.Width*factor,1200*zoom)&&Near(size.Height*factor,800*zoom),"OCR preview magnification preserves physical image pixels at "+factor+" DPI scale and "+zoom+" zoom");
            }
            var viewport=new Size(400,250);double fit=TextPreviewScale.Fit(pixels,viewport,dpi);var fitted=TextPreviewScale.LogicalSize(pixels,fit,dpi);
            check(fitted.Width<=viewport.Width+.00001&&fitted.Height<=viewport.Height+.00001&&fit<=1,"fit view remains inside the logical viewport without physical upscaling at "+factor+" DPI scale");
        }
        var nonuniform=new DpiScale(1.5,2);var actual=TextPreviewScale.LogicalSize(pixels,1,nonuniform);
        check(actual==new Size(800,400),"physical image size uses both DPI axes instead of assuming primary-monitor scale");
        check(TextPreviewScale.Fit(new Size(120,80),new Size(500,500),new DpiScale(2,2))==1,"small previews do not silently upscale above actual physical size in fit mode");
        var image=BitmapSource.Create(1200,800,96,96,PixelFormats.Bgra32,null,new byte[1200*800*4],4800);image.Freeze();int requests=0;
        var window=new TextWindow(image,"zh",(source,token)=>{requests++;return Task.FromResult(new OcrPage());});
        try{
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            var preview=(Viewbox)typeof(TextWindow).GetField("preview",flags).GetValue(window);
            var change=typeof(TextWindow).GetMethod("ChangeZoom",flags);var dpiChanged=typeof(TextWindow).GetMethod("OnDpiChanged",flags);
            change.Invoke(window,new object[]{1.0});dpiChanged.Invoke(window,new object[]{new DpiScale(1,1),new DpiScale(2,2)});
            check(preview.Width==600&&preview.Height==400,"actual viewer DPI route keeps 1:1 at half the logical dimensions on a 200% monitor");
            change.Invoke(window,new object[]{2.0});dpiChanged.Invoke(window,new object[]{new DpiScale(2,2),new DpiScale(1.5,1.5)});
            check(preview.Width==1600&&Near(preview.Height,800*2/1.5),"manual zoom survives a second monitor DPI change through the real viewer route");
            check(requests==0,"unshown zoom fixture does not start recognition or online translation");
        }finally{window.Close();}
    }
}
