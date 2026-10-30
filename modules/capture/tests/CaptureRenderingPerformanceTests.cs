using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Web.Script.Serialization;
using QingJie;

internal static class CaptureRenderingPerformanceTests {
    static Annotation Fresh(Annotation a){return new Annotation{Kind=a.Kind,Start=a.Start,End=a.End,Color=a.Color,Width=a.Width,Text=a.Text,TextWrapWidth=a.TextWrapWidth,ArrowStyle=a.ArrowStyle,Points=new List<Point>(a.Points)};}
    static byte[] Render(Annotation a){var visual=new DrawingVisual();using(var d=visual.RenderOpen())a.Draw(d);var image=new RenderTargetBitmap(640,320,96,96,PixelFormats.Pbgra32);image.Render(visual);var bytes=new byte[640*320*4];image.CopyPixels(bytes,640*4,0);return bytes;}
    static void Equivalent(Annotation a,Action<bool,string> check,string label){check(a.Bounds==Fresh(a).Bounds&&Render(a).SequenceEqual(Render(Fresh(a))),label);}
    static Rect TextBounds(Annotation a){var f=new FormattedText(a.Text??"",CultureInfo.CurrentUICulture,FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),a.TextFontSize,Brushes.Black);if(a.TextWrapWidth>0)f.MaxTextWidth=a.TextWrapWidth;return new Rect(a.Start,new Size(Math.Max(1,f.WidthIncludingTrailingWhitespace),Math.Max(1,f.Height)));}
    internal static void Run(Action<bool,string> check){
        var text=new Annotation{Kind="text",Start=new Point(30,30),Width=4,Text="文字 with spaces  \nSecond line",TextWrapWidth=300};
        Render(text);Equivalent(text,check,"warm text layout preserves pixels and bounds");
        text.Start=new Point(80,55);Equivalent(text,check,"moving caption changes its origin without stale pixels");
        text.Text="新的文字 and wrapped text with trailing spaces   ";Equivalent(text,check,"editing caption refreshes cached content");
        text.TextWrapWidth=120;check(text.Bounds==TextBounds(text),"wrapping matches independent WPF metrics including whitespace");Equivalent(text,check,"wrap width changes refresh layout");
        text.Width=8;Equivalent(text,check,"font resize refreshes layout");
        var before=Render(text);text.Color="#2563EB";var after=Render(text);check(!before.SequenceEqual(after),"changing cached caption color changes final pixels");Equivalent(text,check,"color mutation preserves current text brush");
        var culture=System.Threading.Thread.CurrentThread.CurrentUICulture;
        try{System.Threading.Thread.CurrentThread.CurrentUICulture=new CultureInfo("en-US");check(text.Bounds==TextBounds(text),"culture change matches fresh text measurement");Equivalent(text,check,"culture change invalidates text layout");}finally{System.Threading.Thread.CurrentThread.CurrentUICulture=culture;}
        text.Text=null;Equivalent(text,check,"empty caption invalidates previous glyphs");
        foreach(var kind in new[]{"rect","ellipse","line","arrow","pen"}){
            var a=new Annotation{Kind=kind,ArrowStyle="filled",Start=new Point(30,40),End=new Point(300,180),Width=4};a.Points.Add(a.Start);a.Points.Add(a.End);Render(a);
            a.Color="#2563EB";Equivalent(a,check,kind+" recolor refreshes cached ink");a.Width=15;Equivalent(a,check,kind+" resizing refreshes cached pen thickness");
            a.Start=new Point(60,70);a.End=new Point(400,100);a.Points[0]=a.Start;a.Points[1]=a.End;Equivalent(a,check,kind+" geometry remains live after resource reuse");
        }
        // The public model can change kinds too; a former shape's brush must
        // never leave a reused caption in its old color.
        text.Text="caption";text.Kind="rect";text.End=new Point(200,150);text.Color="#16A34A";Render(text);text.Kind="text";Equivalent(text,check,"kind changes cannot retain stale text ink");
        Benchmark();
    }
    static void Benchmark(){
        AppDomain.MonitoringIsEnabled=true;var results=new List<object>();
        var caption=new Annotation{Kind="text",Width=4,Text="截图性能检查：文字排版、空格与自动换行。 Caption performance measurement.",TextWrapWidth=260};
        double consumed=0;Action metrics=()=>{for(int i=0;i<2000;i++)consumed+=caption.Bounds.Height;};
        var marks=Enumerable.Range(0,100).Select(i=>new Annotation{Kind=i%2==0?"rect":"line",Color=i%2==0?"#EF4444":"#2563EB",Start=new Point(i,20),End=new Point(i+60,90),Width=4}).ToArray();
        Action drawing=()=>{for(int frame=0;frame<20;frame++){var group=new DrawingGroup();using(var d=group.Open())foreach(var a in marks)a.Draw(d);}};
        foreach(var work in new[]{new KeyValuePair<string,Action>("text-bounds-2000",metrics),new KeyValuePair<string,Action>("draw-100-marks-20-frames",drawing)}){
            work.Value();var milliseconds=new List<double>();var allocated=new List<long>();var collections=new List<int>();
            for(int sample=0;sample<3;sample++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();long bytes=AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;int gen0=GC.CollectionCount(0);var watch=Stopwatch.StartNew();work.Value();watch.Stop();milliseconds.Add(watch.Elapsed.TotalMilliseconds);allocated.Add(AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize-bytes);collections.Add(GC.CollectionCount(0)-gen0);}
            results.Add(new{name=work.Key,milliseconds=milliseconds,allocatedBytes=allocated,gen0Collections=collections});
        }
        File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"capture-performance.json"),new JavaScriptSerializer().Serialize(new{runtime=Environment.Version.ToString(),results=results,consumed=consumed}));
    }
}
