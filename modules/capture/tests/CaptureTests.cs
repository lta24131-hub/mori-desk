using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;
using Forms=System.Windows.Forms;
using Drawing=System.Drawing;

static class CaptureTests {
    static CaptureTargets Targets(params CaptureTarget[] roots){return new CaptureTargets(roots.ToList());}
    static bool Pixels(BitmapSource a,BitmapSource b){var x=new byte[a.PixelWidth*a.PixelHeight*4];var y=new byte[b.PixelWidth*b.PixelHeight*4];a.CopyPixels(x,a.PixelWidth*4,0);b.CopyPixels(y,b.PixelWidth*4,0);return a.PixelWidth==b.PixelWidth&&a.PixelHeight==b.PixelHeight&&x.SequenceEqual(y);}
    static RenderTargetBitmap Render(ScreenshotSurface surface){surface.UpdateLayout();var image=new RenderTargetBitmap(600,400,96,96,PixelFormats.Pbgra32);image.Render(surface);return image;}
    internal static void Run(Action<bool,string> check){
        var prefs=new Preferences {Stroke=7,OcrLanguage="kor+jpn"};prefs.SaveChecked();var restored=Preferences.Load();check(restored.Stroke==7&&restored.OcrLanguage=="kor+jpn","test settings are read and saved only through the isolated test path");
        var screen=new Rect(0,0,1920,1080);var front=new CaptureTarget(new Rect(100,100,800,600));var panel=new CaptureTarget(new Rect(150,150,500,400));var leaf=new CaptureTarget(new Rect(180,180,300,200));panel.Children.Add(leaf);front.Children.Add(panel);
        var behind=new CaptureTarget(new Rect(0,0,1000,900));behind.Children.Add(new CaptureTarget(new Rect(195,195,30,30)));
        var targets=Targets(front,behind);var point=new Point(200,200);
        check(targets.At(point,screen).SequenceEqual(new[]{leaf.Bounds,panel.Bounds,front.Bounds,screen}),"snap order is inner region, parent, foreground window, screen (never obscured window)");
        var sibling=new CaptureTarget(new Rect(190,190,80,80));front.Children.Add(sibling);
        check(targets.At(point,screen)[0]==leaf.Bounds,"overlapping sibling behind foreground panel cannot steal selection");
        check(targets.At(new Point(800,700),screen).SequenceEqual(new[]{behind.Bounds,screen}),"half-open window edge falls through to the visible window underneath");
        check(Targets().At(point,screen).SequenceEqual(new[]{screen}),"empty native snapshot still offers screen selection and manual drag");
        check(targets.At(new Point(1920,200),screen).Count==0&&targets.At(new Point(-1,0),screen).Count==0,"cursor outside a monitor has no candidate");
        var maximized=new CaptureTarget(screen);maximized.Children.Add(new CaptureTarget(new Rect(1,1,1918,1078)));
        check(Targets(maximized).At(point,screen).SequenceEqual(new[]{screen}),"nearly identical screen and host bounds collapse to one outer candidate");
        var clipped=new CaptureTarget(new Rect(-100,-100,1000,700));clipped.Children.Add(new CaptureTarget(new Rect(-200,-200,1400,1000)));
        check(Targets(clipped).At(point,screen).SequenceEqual(new[]{new Rect(0,0,900,600),screen}),"child and cross-screen bounds are clipped to visible ancestor and current monitor");
        check(CaptureTargets.Clip(new Rect(0,0,1,20),screen).IsEmpty&&CaptureTargets.Clip(new Rect(2000,0,50,50),screen).IsEmpty,"empty or sub-three-pixel intersections are not targets");
        var monitor=new Drawing.Rectangle(-1920,-300,1920,1080);var physical=new Rect(-1800,-150,900,600);
        foreach(double scale in new[]{1.0,1.5,2.0})check(CaptureTargets.ToLocal(physical,monitor,1920/scale,1080/scale)==new Rect(120/scale,150/scale,900/scale,600/scale),"negative monitor coordinates map correctly at DPI scale "+scale);
        check(CaptureTargets.ToLocal(new Rect(-100,-200,400,500),monitor,1280,720)==new Rect(1820/1.5,100/1.5,100/1.5,500/1.5),"straddling window maps only its visible part on the negative-coordinate monitor");
        check(CaptureTargets.ToLocal(physical,monitor,0,720).IsEmpty,"unlaid-out overlay never creates an invalid selection");
        var snap=new SnapSelection {Candidate=leaf.Bounds};snap.Begin(point,false);snap.Move(new Point(201,201));check(snap.End(Rect.Empty)==leaf.Bounds,"single click with slight jitter selects the candidate pressed at mouse-down");
        snap.Candidate=leaf.Bounds;snap.Begin(point,false);snap.Candidate=panel.Bounds;check(snap.End(Rect.Empty)==leaf.Bounds,"hover changes after mouse-down cannot switch the clicked candidate");
        snap.Candidate=leaf.Bounds;snap.Begin(point,false);var manual=new Rect(200,200,200,100);snap.Move(new Point(400,300));check(snap.End(manual)==manual,"intentional drag takes precedence over automatic region");
        snap.Candidate=leaf.Bounds;snap.Begin(point,false);snap.Move(new Point(400,300));snap.Move(point);check(snap.End(Rect.Empty).IsEmpty,"dragging back to origin cannot unexpectedly capture the whole window");
        snap.Candidate=leaf.Bounds;snap.Begin(point,true);check(snap.End(Rect.Empty).IsEmpty,"Ctrl plus click cannot commit a stale automatic candidate");
        snap.Candidate=leaf.Bounds;snap.Begin(point,true);snap.Move(new Point(400,300));check(snap.End(manual)==manual,"Ctrl plus drag keeps normal manual selection");
        var raw=new byte[600*400*4];new Random(36).NextBytes(raw);for(int i=3;i<raw.Length;i+=4)raw[i]=255;
        var source=BitmapSource.Create(600,400,96,96,PixelFormats.Bgra32,null,raw,2400);source.Freeze();
        var surface=new ScreenshotSurface {Image=source,HoverSelection=new Rect(50,50,300,220)};surface.Measure(new Size(600,400));surface.Arrange(new Rect(0,0,600,400));
        var handlesOn=Render(surface);surface.ShowHandles=false;surface.InvalidateVisual();check(Pixels(handlesOn,Render(surface)),"hover preview never displays resize handles");
        bool rejected=false;try{surface.Export();}catch(InvalidOperationException){rejected=true;}check(rejected&&surface.Selection.IsEmpty,"hover is not a confirmed selection and cannot export an unintended screenshot");
        surface.Selection=surface.HoverSelection;var expected=new CroppedBitmap(source,new Int32Rect(50,50,300,220));check(Pixels(surface.Export(),expected),"confirmed automatic crop preserves original pixels without outline, shade or hints");
        var capture=new CaptureWindow(new Snapshot {Image=source,Bounds=new Drawing.Rectangle(0,0,600,400)},targets);
        var captureSurface=((System.Windows.Controls.Grid)capture.Content).Children.OfType<ScreenshotSurface>().Single();captureSurface.Measure(new Size(600,400));captureSurface.Arrange(new Rect(0,0,600,400));
        var update=typeof(CaptureWindow).GetMethod("UpdateSnap",BindingFlags.Instance|BindingFlags.NonPublic);update.Invoke(capture,new object[]{point});check(!captureSurface.HoverSelection.IsEmpty&&captureSurface.Selection.IsEmpty,"capture routes cursor to preselection without changing confirmed state");
        captureSurface.Selection=new Rect(10,10,200,100);var mark=new Annotation {Kind="arrow",Start=new Point(20,20),End=new Point(90,60)};captureSurface.Marks.Add(mark);update.Invoke(capture,new object[]{point});
        check(captureSurface.HoverSelection.IsEmpty&&captureSurface.Selection==new Rect(10,10,200,100)&&captureSurface.Marks.Single()==mark,"hover does not resnap or disturb confirmed selection and annotations");
        capture.Close();check(typeof(CaptureWindow).GetField("snapTargets",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(capture)==null&&captureSurface.Image==null,"closing capture releases native target snapshot and screenshot");
        var perf=Stopwatch.StartNew();for(int i=0;i<10000;i++)targets.At(new Point(200+i%10,200),screen);perf.Stop();Console.WriteLine("snap 10,000 geometry lookups: "+perf.ElapsedMilliseconds+" ms");
    }
    // Opt-in real HWND fixture. Creates only synthetic UI; never reads or saves private pixels/text.
    internal static void Native(Action<bool,string> check){
        using(var form=new Forms.Form {Text="Luma native boundary test",Bounds=new Drawing.Rectangle(80,80,760,520),TopMost=true})
        using(var panel=new Forms.Panel {Bounds=new Drawing.Rectangle(30,40,500,320)})
        using(var inner=new Forms.Panel {Bounds=new Drawing.Rectangle(40,50,320,180)}){
            panel.Controls.Add(inner);form.Controls.Add(panel);form.Show();Forms.Application.DoEvents();
            var bounds=inner.RectangleToScreen(inner.ClientRectangle);var point=new Point(bounds.X+100,bounds.Y+80);var screen=Forms.Screen.FromControl(form).Bounds;var area=new Rect(screen.X,screen.Y,screen.Width,screen.Height);
            var elapsed=new List<double>();List<Rect> choices=null;
            for(int i=0;i<6;i++){var watch=Stopwatch.StartNew();choices=CaptureTargets.Read().At(point,area);watch.Stop();elapsed.Add(watch.Elapsed.TotalMilliseconds);}
            check(choices[0]==new Rect(bounds.X,bounds.Y,bounds.Width,bounds.Height),"real Win32 nested child HWND is detected without UI Automation or text reads");
            var outer=panel.RectangleToScreen(panel.ClientRectangle);check(choices[1]==new Rect(outer.X,outer.Y,outer.Width,outer.Height)&&choices.Last()==area,"real native inner/outer window hierarchy and screen are ordered correctly");
            inner.Hide();Forms.Application.DoEvents();check(CaptureTargets.Read().At(point,area)[0]==choices[1],"hidden native child is ignored");
            form.Hide();Forms.Application.DoEvents();check(!CaptureTargets.Read().At(point,area).Contains(choices[1]),"hidden native parent and children are ignored");
            Console.WriteLine("native snapshot ms: "+string.Join(", ",elapsed.Select(v=>v.ToString("0.00"))));
        }
    }
}
