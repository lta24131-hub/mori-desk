using System;
using System.Reflection;
using System.Windows;
using QingJie;
using Drawing=System.Drawing;

// Physical-pixel geometry only: no monitor query, window, image capture or pin
// persistence is needed to exercise negative origins, DPI margins and fitting.
internal static class CapturePinPlacementTests {
    internal static void Run(Action<bool,string> check){
        var method=typeof(PinStore).GetMethod("InitialBounds",BindingFlags.NonPublic|BindingFlags.Static);
        check(method!=null,"new capture pins have one physical-pixel initial-placement policy");
        Func<int,int,Drawing.Rectangle,double,double,int,Int32Rect> bounds=(width,height,work,sx,sy,cascade)=>(Int32Rect)method.Invoke(null,new object[]{width,height,work,sx,sy,cascade});
        var works=new[]{new Drawing.Rectangle(0,40,1920,1040),new Drawing.Rectangle(-1920,-120,1920,1080),new Drawing.Rectangle(0,-1440,2560,1400),new Drawing.Rectangle(2560,0,640,440)};
        var images=new[]{new Drawing.Size(200,100),new Drawing.Size(8000,12000),new Drawing.Size(12000,800),new Drawing.Size(1,20000),new Drawing.Size(20000,1)};
        foreach(var work in works)foreach(var image in images)foreach(double scale in new[]{1.0,1.5,2.0,3.0})foreach(int cascade in new[]{0,1,7,int.MinValue}){
            var result=bounds(image.Width,image.Height,work,scale,scale,cascade);
            int frame=(int)Math.Ceiling(PinFrame.Inset*scale);
            var outer=new Drawing.Rectangle(result.X-frame,result.Y-frame,result.Width+2*frame,result.Height+2*frame);
            string context=" / "+work+" / "+image+" / "+scale+" / "+cascade;
            check(result.Width>0&&result.Height>0&&result.Width<=720&&result.Width<=image.Width&&result.Height<=image.Height,"new pin retains positive bounded content without enlarging source pixels"+context);
            check(work.Contains(outer),"new pin image and DPI-scaled frame stay inside the selected monitor work area"+context);
            check(Math.Abs(result.Width/(double)image.Width-result.Height/(double)image.Height)<=1.0/image.Width+1.0/image.Height,"fitting a new pin preserves its aspect ratio within pixel rounding"+context);
        }
        var target=new Drawing.Rectangle(-1600,0,1600,900);
        var first=bounds(200,100,target,1.5,2,0);var next=bounds(200,100,target,1.5,2,1);
        check(first.X<0&&next.X<0&&first.X!=next.X&&first.Y!=next.Y,"successive pins cascade within the requested negative-coordinate monitor");
        check(first.X-target.Left>PinFrame.Inset*1.5&&first.Y-target.Top>PinFrame.Inset*2,"non-square DPI reserves each frame axis independently");
        var tiny=bounds(8000,12000,new Drawing.Rectangle(-50,-50,9,7),3,3,7);
        check(tiny.Width>0&&tiny.Height>0&&new Drawing.Rectangle(-50,-50,9,7).Contains(new Drawing.Rectangle(tiny.X,tiny.Y,tiny.Width,tiny.Height)),"even a temporarily tiny work area keeps positive image bounds inside its own origin");
        var safe=bounds(200,100,target,double.NaN,double.PositiveInfinity,0);
        check(safe==bounds(200,100,target,1,1,0),"unavailable DPI falls back to a finite 100 percent placement");
        foreach(var invalid in new[]{new object[]{0,100,target,1.0,1.0,0},new object[]{100,0,target,1.0,1.0,0},new object[]{100,100,Drawing.Rectangle.Empty,1.0,1.0,0}}){
            bool rejected=false;try{method.Invoke(null,invalid);}catch(TargetInvocationException error){rejected=error.InnerException is ArgumentOutOfRangeException;}
            check(rejected,"empty image or monitor dimensions cannot create invalid initial pin bounds");
        }
    }
}
