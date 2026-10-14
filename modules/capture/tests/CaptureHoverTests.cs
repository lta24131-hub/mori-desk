using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

static class CaptureHoverTests {
    static List<Rect> Choices(params Rect[] values){return values.ToList();}
    static Rect Scale(Rect rect,double scale){return new Rect(rect.X*scale,rect.Y*scale,rect.Width*scale,rect.Height*scale);}
    static Point Scale(Point point,double scale){return new Point(point.X*scale,point.Y*scale);}
    internal static void Run(Action<bool,string> check){
        var screen=new Rect(0,0,1000,700);var host=new Rect(50,40,800,600);var panel=new Rect(100,100,400,300);
        var button=new Rect(180,180,32,18);var field=new Rect(150,150,240,30);var card=new Rect(180,180,150,100);var beside=new Rect(500,100,300,300);
        var point=new Point(190,190);var hover=new CaptureSnapHover();
        check(hover.Update(Choices(button,field,panel,host,screen),host,point,1,1)==panel,"hover defaults to a content-sized panel instead of tiny buttons and thin fields");
        check(hover.Cycle(-1)==field&&hover.Cycle(-1)==button,"wheel inward still reaches the thin field and tiny native button");
        check(hover.Cycle(-1)==button,"wheel inward stops at the smallest candidate without wrapping unexpectedly");
        check(hover.Cycle(1)==field&&hover.Cycle(1)==panel&&hover.Cycle(1)==host&&hover.Cycle(1)==screen,"wheel outward expands through every retained candidate to the screen");
        check(hover.Cycle(1)==screen,"wheel outward stops at the screen instead of wrapping to a tiny control");
        foreach(var largeButton in new[]{new Rect(180,180,80,52),new Rect(180,180,120,48)}){
            hover.Clear();check(hover.Update(Choices(largeButton,panel,host,screen),host,point,1,1)==panel,"common large button does not steal default content selection: "+largeButton.Width+"x"+largeButton.Height);
            check(hover.Cycle(-1)==largeButton,"large button remains explicitly selectable with wheel inward: "+largeButton.Width+"x"+largeButton.Height);
        }
        hover.Clear();hover.Update(Choices(card,panel,host,screen),host,point,1,1);hover.Cycle(1);
        check(hover.Update(Choices(button,card,panel,host,screen),host,point,1,1)==panel,"explicit parent selection survives a newly encountered child candidate");
        check(hover.Update(Choices(field,panel,host,screen),host,new Point(160,160),1,1)==panel,"explicit parent selection survives moving between sibling controls");
        check(hover.Cycle(-1)==field,"after pointer movement wheel inward uses the current controls rather than stale candidate indices");
        check(hover.Update(Choices(panel,host,screen),host,new Point(450,250),1,1)==panel,"leaving an explicitly chosen child restores the applicable content target");
        hover.Clear();hover.Update(Choices(panel,host,screen),host,new Point(300,200),1,1);
        check(hover.Update(Choices(beside,host,screen),host,new Point(501,200),1,1)==panel,"one DIP of pointer jitter across adjacent panel borders does not flash another frame");
        check(hover.Update(Choices(beside,host,screen),host,new Point(506,200),1,1)==beside,"moving beyond the boundary tolerance switches immediately with no dwell delay");
        var nextButton=new Rect(500,190,32,18);var nextField=new Rect(500,180,240,30);var across=new Point(501,200);
        hover.Clear();hover.Update(Choices(panel,host,screen),host,new Point(300,200),1,1);
        check(hover.Update(Choices(nextButton,nextField,beside,host,screen),host,across,1,1)==panel&&hover.Index==-1,"edge tolerance may retain the old panel outside the new multi-control candidate path");
        check(hover.Cycle(1)==host,"edge-tolerance wheel outward skips smaller sibling cards and nested controls instead of shrinking");
        check(hover.Cycle(-1)==beside&&hover.Cycle(-1)==nextField&&hover.Cycle(-1)==nextButton,"after edge expansion wheel inward still reaches every nested control in order");
        hover.Clear();hover.Update(Choices(panel,host,screen),host,new Point(300,200),1,1);hover.Update(Choices(nextButton,nextField,beside,host,screen),host,across,1,1);
        check(hover.Cycle(-1)==beside,"edge-tolerance wheel inward uses the nearest smaller candidate rather than jumping to the smallest button");
        check(hover.Cycle(-1)==nextField&&hover.Cycle(-1)==nextButton,"edge-tolerance inward traversal retains the intermediate field and smallest button");
        hover.Clear();hover.Update(Choices(panel,host,screen),host,new Point(300,200),1,1);hover.Update(Choices(host,screen),host,across,1,1);
        check(hover.Cycle(-1)==panel&&hover.Index==-1,"edge-tolerance inward wheel with no smaller candidate keeps the old frame and never expands it");
        check(hover.Cycle(1)==host,"a retained frame with no inward target can still expand to its next larger parent");
        var equalSibling=new Rect(500,100,400,300);var equalHost=new Rect(50,40,900,600);
        hover.Clear();hover.Update(Choices(panel,equalHost,screen),equalHost,new Point(300,200),1,1);hover.Update(Choices(nextButton,equalSibling,equalHost,screen),equalHost,across,1,1);
        check(hover.Cycle(1)==equalHost,"edge-tolerance expansion skips an equal-area sibling and chooses a strictly larger frame");
        hover.Clear();hover.Update(Choices(panel,equalHost,screen),equalHost,new Point(300,200),1,1);hover.Update(Choices(nextButton,equalSibling,equalHost,screen),equalHost,across,1,1);
        check(hover.Cycle(-1)==nextButton,"edge-tolerance contraction skips an equal-area sibling and chooses a strictly smaller frame");
        hover.Clear();hover.Update(Choices(panel,host,screen),host,new Point(300,200),1,1);hover.Update(Choices(nextButton,beside),host,across,1,1);
        check(hover.Cycle(1)==panel&&hover.Index==-1,"edge-tolerance expansion with no larger candidate keeps the current frame rather than shrinking");
        hover.Clear();hover.Update(Choices(panel,host,screen),host,new Point(300,200),1,1);
        check(hover.Update(Choices(card,panel,host,screen),host,new Point(181,200),1,1)==panel,"touching a nested card edge does not prematurely steal its parent hover");
        check(hover.Update(Choices(card,panel,host,screen),host,new Point(188,200),1,1)==card,"entering the nested card interior immediately selects it");
        hover.Cycle(1);var otherHost=new Rect(480,80,300,340);
        check(hover.Update(Choices(beside,otherHost,screen),otherHost,new Point(501,200),1,1)==beside,"a foreground-window change overrides old parent and boundary stickiness");
        check(hover.Update(Choices(screen),screen,new Point(900,660),1,1)==screen,"leaving the application window falls back to the screen rather than a stale window");
        check(hover.Update(Choices(),Rect.Empty,new Point(-1,0),1,1).IsEmpty&&hover.Index==-1&&hover.Choices.Count==0,"moving off the captured monitor clears all hover state");
        var popup=new Rect(40,40,38,22);var tiny=new Rect(44,44,12,10);
        check(hover.Update(Choices(tiny,popup,screen),popup,new Point(45,45),1,1)==popup,"a small foreground popup defaults to the whole popup rather than the entire monitor");
        var snapshot=Choices(card,panel,host,screen);hover.Update(snapshot,host,point,1,1);hover.Clear();
        check(snapshot.Count==4,"resetting hover state does not mutate the caller's immutable target snapshot");
        foreach(double scale in new[]{1.0,1.5,2.0}){
            hover.Clear();var scaledHost=Scale(host,scale);var scaledPanel=Scale(panel,scale);var scaledBeside=Scale(beside,scale);var scaledScreen=Scale(screen,scale);
            check(hover.Update(Choices(Scale(button,scale),Scale(field,scale),scaledPanel,scaledHost,scaledScreen),scaledHost,Scale(point,scale),scale,scale)==scaledPanel,"content target preference is consistent across DPI scales: "+scale);
            check(hover.Update(Choices(scaledBeside,scaledHost,scaledScreen),scaledHost,Scale(new Point(501,200),scale),scale,scale)==scaledPanel,"edge jitter tolerance remains four DIP across DPI scales: "+scale);
            check(hover.Update(Choices(scaledBeside,scaledHost,scaledScreen),scaledHost,Scale(new Point(506,200),scale),scale,scale)==scaledBeside,"intentional cross-panel movement remains immediate across DPI scales: "+scale);
        }
        var snap=new SnapSelection {Candidate=panel};snap.Begin(point,false);hover.Update(Choices(card,panel,host,screen),host,point,1,1);snap.Candidate=hover.Candidate;
        check(snap.End(Rect.Empty)==panel,"mouse-down freezes the visible target even if later hover geometry differs");
        snap.Candidate=panel;snap.Begin(point,false);var moved=new Point(point.X+SystemParameters.MinimumHorizontalDragDistance,point.Y);var manual=new Rect(point,moved);snap.Move(moved);
        check(snap.HasDragged&&snap.End(manual)==manual,"manual drag takes over at the system drag threshold, not after a hover delay");
        var foreground=new CaptureTarget(host);foreground.Children.Add(new CaptureTarget(card));var targets=new CaptureTargets(new List<CaptureTarget>{foreground});Rect actualHost;
        var nativeChoices=targets.At(point,screen,null,out actualHost);check(actualHost==host&&nativeChoices.Contains(card),"hover receives the same exact frontmost window used by candidate collection");
        targets.At(new Point(900,660),screen,null,out actualHost);check(actualHost==screen,"desktop hover identity is the captured monitor");
        targets.At(new Point(-1,0),screen,null,out actualHost);check(actualHost.IsEmpty,"off-monitor lookup does not retain a frontmost host");
        var bitmap=new RenderTargetBitmap(1000,700,96,96,PixelFormats.Pbgra32);bitmap.Freeze();
        var window=new CaptureWindow(new Snapshot {Image=bitmap,Bounds=new System.Drawing.Rectangle(0,0,1000,700)},targets);
        var surface=((System.Windows.Controls.Grid)window.Content).Children.OfType<ScreenshotSurface>().Single();surface.Measure(new Size(1000,700));surface.Arrange(new Rect(0,0,1000,700));
        var update=typeof(CaptureWindow).GetMethod("UpdateSnap",BindingFlags.Instance|BindingFlags.NonPublic);update.Invoke(window,new object[]{point});window.CycleSnap(point,1);
        check(surface.HoverSelection==host&&surface.Selection.IsEmpty,"capture-window wheel expansion still changes only hover, not a confirmed crop");
        update.Invoke(window,new object[]{new Point(195,195)});check(surface.HoverSelection==host,"capture-window wiring retains the wheel-selected parent on slight cursor movement");
        typeof(CaptureWindow).GetField("dragging",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(window,true);update.Invoke(window,new object[]{point});
        check(surface.HoverSelection.IsEmpty,"starting a manual drag removes automatic hover immediately");
        typeof(CaptureWindow).GetField("dragging",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(window,false);window.Close();
        var state=(CaptureSnapHover)typeof(CaptureWindow).GetField("snapHover",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
        check(state.Candidate.IsEmpty&&state.Choices.Count==0,"closing capture releases all hover rectangles and explicit-choice state");
        var watch=Stopwatch.StartNew();for(int i=0;i<10000;i++){var choices=targets.At(new Point(190+i%10,190),screen,null,out actualHost);hover.Update(choices,actualHost,new Point(190+i%10,190),1,1);}watch.Stop();
        Console.WriteLine("stable hover 10,000 geometry lookups and updates: "+watch.ElapsedMilliseconds+" ms");
    }
}
