using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Pixel preparation is checked independently of the wall-clock geometry budget:
// a slow VM must not hide a wrong channel layout behind an empty timed-out scan.
static class CaptureVisualPixelTests {
    internal static string LastScanDiagnostic {get;private set;}
    static BitmapSource Image(PixelFormat format,bool opaque){
        var pixels=new byte[8*6*4];
        for(int y=0;y<6;y++)for(int x=0;x<8;x++){
            int at=(y*8+x)*4;byte alpha=opaque?(byte)255:(byte)(32+(x+y)%4*64);
            pixels[at]=(byte)(23+x*19);pixels[at+1]=(byte)(31+y*27);pixels[at+2]=(byte)(51+(x+y)*13);pixels[at+3]=alpha;
            if(format==PixelFormats.Pbgra32){pixels[at]=(byte)(pixels[at]*alpha/255);pixels[at+1]=(byte)(pixels[at+1]*alpha/255);pixels[at+2]=(byte)(pixels[at+2]*alpha/255);}
            if(format==PixelFormats.Bgr32)pixels[at+3]=(byte)(x*23+y); // Undefined padding must never affect RGB.
        }
        var image=BitmapSource.Create(8,6,96,96,format,null,pixels,8*4);image.Freeze();return image;
    }
    static BitmapSource OriginalSamplingSource(BitmapSource image,double scale){
        BitmapSource source=image;if(scale<1)source=new TransformedBitmap(source,new ScaleTransform(scale,scale));
        if(source.Format!=PixelFormats.Bgra32)source=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        return source;
    }
    static byte[] Pixels(BitmapSource image){var data=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(data,image.PixelWidth*4,0);return data;}
    static bool SameRgb(BitmapSource actual,BitmapSource expected){
        if(actual.PixelWidth!=expected.PixelWidth||actual.PixelHeight!=expected.PixelHeight)return false;
        var a=Pixels(actual);var b=Pixels(expected);
        for(int i=0;i<a.Length;i+=4)for(int channel=0;channel<3;channel++)if(a[i+channel]!=b[i+channel])return false;
        return true;
    }
    internal static void Run(Action<bool,string> check){
        foreach(var format in new[]{PixelFormats.Bgra32,PixelFormats.Bgr32,PixelFormats.Pbgra32})
            foreach(bool opaque in new[]{true,false})foreach(double scale in new[]{1.0,.5}){
                var image=Image(format,opaque);bool converted;
                var sampled=CaptureVisualRegions.SamplingSource(image,scale,out converted);var original=OriginalSamplingSource(image,scale);
                string context="format="+format+", opaque="+opaque+", scale="+scale;
                check(SameRgb(sampled,original),"visual sampling preserves every edge-relevant RGB channel: "+context);
                if(scale==1){
                    bool direct=format==PixelFormats.Bgra32||format==PixelFormats.Bgr32;
                    check(converted!=direct&&ReferenceEquals(sampled,image)==direct,"only byte-compatible non-premultiplied formats bypass conversion: "+context);
                }
                if(format==PixelFormats.Pbgra32){
                    check(sampled.Format==PixelFormats.Bgra32&&Pixels(sampled).SequenceEqual(Pixels(original)),"premultiplied RGB and alpha retain the previous conversion exactly: "+context);
                }
            }
        bool transparentConverted;var premultiplied=Image(PixelFormats.Pbgra32,false);var straight=CaptureVisualRegions.SamplingSource(premultiplied,1,out transparentConverted);
        check(transparentConverted&&Pixels(straight)[0]>Pixels(premultiplied)[0],"transparent premultiplied pixels are unpremultiplied, never treated as Bgr32 padding");
        var blank=new byte[64*48*4];var source=BitmapSource.Create(64,48,96,96,PixelFormats.Bgr32,null,blank,64*4);source.Freeze();
        var result=CaptureVisualRegions.Read(source,new System.Drawing.Rectangle(-64,-48,64,48));
        LastScanDiagnostic="format=Bgr32; prepare="+result.PrepareMilliseconds+"ms; scan="+result.ScanMilliseconds+"ms; rows="+result.ProcessedRows+"; timedOut="+result.TimedOut+"; converted="+result.PixelFormatConverted+"; readFailed="+result.ReadFailed+"; preparationCompleted="+result.PreparationCompleted+"; regions="+result.Count;
        check(result.PreparationCompleted&&!result.PixelFormatConverted&&!result.ReadFailed&&result.PrepareMilliseconds>=0&&result.ScanMilliseconds>=0&&result.ProcessedRows>=0&&result.ProcessedRows<=47,"visual scan exposes bounded per-result diagnostics without changing the wall-clock budget");
        Console.WriteLine("visual Bgr32 diagnostics: "+LastScanDiagnostic);
    }
}

static class CaptureVisualRegionTests {
    static BitmapSource Boxes(int width,int height,bool dark){
        var drawing=new DrawingVisual();
        using(var d=drawing.RenderOpen()){
            d.DrawRectangle(dark?Brushes.DarkSlateGray:Brushes.WhiteSmoke,null,new Rect(0,0,width,height));
            d.DrawRectangle(dark?Brushes.DimGray:Brushes.White,new Pen(dark?Brushes.Silver:Brushes.DarkGray,1),new Rect(40,30,width-80,height-60));
            d.DrawRectangle(dark?Brushes.Black:Brushes.LightGray,new Pen(dark?Brushes.Gray:Brushes.DimGray,1),new Rect(100,80,180,130));
            d.DrawRectangle(dark?Brushes.SlateGray:Brushes.White,new Pen(dark?Brushes.Silver:Brushes.Gray,1),new Rect(330,80,100,70));
            // Text-like short strokes should not turn into dozens of selection boxes.
            for(int i=0;i<12;i++)d.DrawLine(new Pen(dark?Brushes.White:Brushes.Black,1),new Point(115+i*12,100),new Point(119+i*12,108));
        }
        var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);image.Render(drawing);image.Freeze();return image;
    }
    static bool Near(Rect a,Rect b,double tolerance){return Math.Abs(a.Left-b.Left)<=tolerance&&Math.Abs(a.Top-b.Top)<=tolerance&&Math.Abs(a.Right-b.Right)<=tolerance&&Math.Abs(a.Bottom-b.Bottom)<=tolerance;}
    static BitmapSource Rounded(bool dark){
        var drawing=new DrawingVisual();using(var d=drawing.RenderOpen()){
            var backdrop=new SolidColorBrush(dark?Color.FromRgb(32,32,32):Color.FromRgb(248,248,248));
            var panel=new SolidColorBrush(dark?Color.FromRgb(48,48,48):Color.FromRgb(232,232,232));
            d.DrawRectangle(backdrop,null,new Rect(0,0,600,400));d.DrawRoundedRectangle(panel,null,new Rect(80,70,340,230),12,12);
        }
        var image=new RenderTargetBitmap(600,400,96,96,PixelFormats.Pbgra32);image.Render(drawing);image.Freeze();return image;
    }
    static BitmapSource Thin4k(){
        var drawing=new DrawingVisual();using(var d=drawing.RenderOpen()){
            var backdrop=new SolidColorBrush(Color.FromRgb(245,245,245));d.DrawRectangle(backdrop,null,new Rect(0,0,3840,2160));
            d.DrawRoundedRectangle(backdrop,new Pen(new SolidColorBrush(Color.FromRgb(207,207,207)),1),new Rect(500,600,720,500),16,16);
            d.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(230,230,230)),null,new Rect(1800,1200,800,650),12,12);
        }
        var image=new RenderTargetBitmap(3840,2160,96,96,PixelFormats.Pbgra32);image.Render(drawing);image.Freeze();return image;
    }
    static BitmapSource RoundedButton(int height,int radius,bool dark){
        var drawing=new DrawingVisual();using(var d=drawing.RenderOpen()){
            d.DrawRectangle(dark?Brushes.Black:Brushes.White,null,new Rect(0,0,600,400));
            d.DrawRoundedRectangle(new SolidColorBrush(dark?Color.FromRgb(24,24,24):Color.FromRgb(210,210,210)),null,new Rect(100,80,240,height),radius,radius);
        }
        var image=new RenderTargetBitmap(600,400,96,96,PixelFormats.Pbgra32);image.Render(drawing);image.Freeze();return image;
    }
    static BitmapSource Rows(int rows,bool dense){
        int height=dense?1000:400;var panel=dense?new Rect(30,30,300,820):new Rect(100,40,300,300);
        var drawing=new DrawingVisual();using(var d=drawing.RenderOpen()){
            d.DrawRectangle(Brushes.White,null,new Rect(0,0,640,height));d.DrawRectangle(Brushes.White,new Pen(Brushes.DarkGray,1),panel);
            for(int i=1;i<rows;i++){double y=panel.Top+panel.Height*i/rows;d.DrawLine(new Pen(Brushes.DarkGray,1),new Point(panel.Left,y),new Point(panel.Right,y));}
            if(dense)d.DrawRoundedRectangle(Brushes.LightGray,null,new Rect(380,740,220,80),12,12);
        }
        var image=new RenderTargetBitmap(640,height,96,96,PixelFormats.Pbgra32);image.Render(drawing);image.Freeze();return image;
    }
    internal static void Run(Action<bool,string> check){
        var bounds=new System.Drawing.Rectangle(-600,-100,600,400);var screen=new Rect(bounds.X,bounds.Y,bounds.Width,bounds.Height);
        foreach(bool dark in new[]{false,true}){
            var image=Boxes(600,400,dark);var regions=CaptureVisualRegions.Read(image,bounds);var point=new Point(-450,20);var hits=regions.At(point,screen).ToList();
            string scanEvidence="; prepare="+regions.PrepareMilliseconds+"ms, scan="+regions.ScanMilliseconds+"ms, rows="+regions.ProcessedRows+", timedOut="+regions.TimedOut+", converted="+regions.PixelFormatConverted+", failed="+regions.ReadFailed+", regions="+regions.Count;
            check(hits.Any(r=>Near(r,new Rect(-500,-20,180,130),3)),"visible custom card has a candidate without any native child HWND; dark="+dark+scanEvidence);
            check(hits.Any(r=>Near(r,new Rect(-560,-70,520,340),3)),"large custom panel remains an outward selection choice; dark="+dark);
            check(regions.Count<30,"text-like strokes do not flood rectangle candidates; dark="+dark);
            var native=new CaptureTarget(new Rect(-570,-80,540,360));native.ClientBounds=new Rect(-570,-55,540,335);
            var nativeTargets=new CaptureTargets(new System.Collections.Generic.List<CaptureTarget>{native});var choices=nativeTargets.At(point,screen,regions);
            check(choices.Any(r=>Near(r,new Rect(-500,-20,180,130),3))&&choices.Contains(native.ClientBounds)&&choices.Contains(native.Bounds)&&choices.Last()==screen,"visual cards, native client area, outer window and monitor remain reachable; dark="+dark);
            check(choices.Zip(choices.Skip(1),(a,b)=>a.Width*a.Height<=b.Width*b.Height).All(v=>v),"wheel choices expand from smaller card to larger window; dark="+dark);
            var obscuring=new CaptureTarget(new Rect(-460,0,70,70));var foreground=new CaptureTargets(new System.Collections.Generic.List<CaptureTarget>{obscuring,native});
            check(!foreground.At(point,screen,regions).Any(r=>Near(r,new Rect(-500,-20,180,130),3)),"screenshot rectangle cannot snap outside a foreground obscuring window; dark="+dark);
            var rounded=CaptureVisualRegions.Read(Rounded(dark),bounds);check(rounded.At(new Point(-400,50),screen).Any(r=>Near(r,new Rect(-520,-30,340,230),4)),"low-contrast rounded custom panel retains its full outline; dark="+dark);
            foreach(var shape in new[]{new[]{40,12},new[]{48,20},new[]{180,32}}){
                var roundedButton=CaptureVisualRegions.Read(RoundedButton(shape[0],shape[1],dark),new System.Drawing.Rectangle(0,0,600,400));
                check(roundedButton.At(new Point(200,80+shape[0]/2),new Rect(0,0,600,400)).Any(r=>Near(r,new Rect(100,80,240,shape[0]),3)),"short/large-radius rounded control retains full bounds: height="+shape[0]+", radius="+shape[1]+", dark="+dark);
            }
        }
        var rows=CaptureVisualRegions.Read(Rows(6,false),new System.Drawing.Rectangle(0,0,640,400));var rowHits=rows.At(new Point(150,65),new Rect(0,0,640,400)).ToArray();
        check(rows.Count<=8&&rowHits.Any(r=>Near(r,new Rect(100,40,300,300),3)),"six-row list retains atomic rows and outer container without 21 cross-row combinations");
        check(rowHits.All(r=>r.Height<=55||Near(r,new Rect(100,40,300,300),3)),"intermediate multi-row prefixes do not clutter list hover choices");
        var denseWatch=Stopwatch.StartNew();var dense=CaptureVisualRegions.Read(Rows(26,true),new System.Drawing.Rectangle(0,0,640,1000));denseWatch.Stop();
        check(dense.Count<=32,"26-row list stays linear in real regions rather than filling the 256-candidate budget");
        check(dense.At(new Point(480,775),new Rect(0,0,640,1000)).Any(r=>Near(r,new Rect(380,740,220,80),4)),"real card after a dense separator list remains available");
        Console.WriteLine("dense 26-row list plus later card: "+denseWatch.ElapsedMilliseconds+" ms including fixture generation, "+dense.Count+" regions");
        var blank=new RenderTargetBitmap(600,400,96,96,PixelFormats.Pbgra32);blank.Freeze();check(CaptureVisualRegions.Read(blank,bounds).Count==0,"blank screenshot produces no invented visual rectangle");
        check(CaptureVisualRegions.Read(null,bounds).Count==0&&CaptureVisualRegions.Read(blank,new System.Drawing.Rectangle()).Count==0,"missing screenshot or monitor keeps native and manual selection available");
        var noise=new byte[600*400*4];new Random(99).NextBytes(noise);for(int i=3;i<noise.Length;i+=4)noise[i]=255;
        var random=BitmapSource.Create(600,400,96,96,PixelFormats.Bgra32,null,noise,2400);random.Freeze();check(CaptureVisualRegions.Read(random,bounds).Count==0,"photo-like random gradients cannot manufacture full-sided cards");
        var large=Boxes(3840,2160,true);var watch=Stopwatch.StartNew();var largeRegions=CaptureVisualRegions.Read(large,new System.Drawing.Rectangle(0,0,3840,2160));watch.Stop();
        check(largeRegions.Count<=256,"4K screenshot keeps a bounded rectangle-only snapshot");Console.WriteLine("4K visual region read: "+watch.ElapsedMilliseconds+" ms, "+largeRegions.Count+" regions");
        var thinWatch=Stopwatch.StartNew();var thin=CaptureVisualRegions.Read(Thin4k(),new System.Drawing.Rectangle(2560,-100,3840,2160));thinWatch.Stop();var secondScreen=new Rect(2560,-100,3840,2160);
        check(thin.At(new Point(3300,700),secondScreen).Any(r=>Near(r,new Rect(3060,500,720,500),6)),"one-pixel rounded outline survives 4K downsampling on a second monitor");
        check(thin.At(new Point(4600,1400),secondScreen).Any(r=>Near(r,new Rect(4360,1100,800,650),6)),"weak filled border near screen bottom remains selectable on a second monitor");
        check(thin.At(new Point(3300,700),secondScreen).Count()==1,"4K sampling-scale duplicate outlines collapse to one card candidate");
        check(!thin.At(new Point(2560,0),secondScreen).Any(),"rectangles on another monitor cannot appear under unrelated cursor coordinates");
        Console.WriteLine("4K weak/thin region read including fixture generation: "+thinWatch.ElapsedMilliseconds+" ms, "+thin.Count+" regions");
        var perf=Stopwatch.StartNew();var steady=CaptureVisualRegions.Read(Boxes(600,400,false),bounds);for(int i=0;i<10000;i++)steady.At(new Point(-450,20),screen).ToArray();perf.Stop();Console.WriteLine("visual 10,000 geometry lookups including scan: "+perf.ElapsedMilliseconds+" ms");
    }
}
