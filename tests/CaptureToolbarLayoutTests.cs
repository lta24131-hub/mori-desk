using System;
using System.Collections.Generic;
using System.Windows;
using QingJie;

namespace MoriDesk {
    // Pure Size/Rect tests: no application, window, native calls, settings,
    // clipboard, image capture or user-library access.
    internal static class CaptureToolbarLayoutTests {
        static bool Near(double a,double b){return Math.Abs(a-b)<0.001;}
        static bool Inside(Rect bounds,Size viewport){return bounds.IsEmpty||bounds.Left>=-0.001&&bounds.Top>=-0.001&&bounds.Right<=viewport.Width+0.001&&bounds.Bottom<=viewport.Height+0.001;}
        static bool Separate(Rect a,Rect b){return a.IsEmpty||b.IsEmpty||a.Right<=b.Left+0.001||b.Right<=a.Left+0.001||a.Bottom<=b.Top+0.001||b.Bottom<=a.Top+0.001;}
        internal static void Run(Action<bool,string> check) {
            var sizes=new List<Size>();var groups=new List<int>();
            for(int i=0;i<7;i++){sizes.Add(new Size(38,36));groups.Add(0);}
            for(int i=0;i<7;i++){sizes.Add(new Size(i==3?20:38,36));groups.Add(1);}
            for(int i=0;i<2;i++){sizes.Add(new Size(38,36));groups.Add(2);}
            var wide=CaptureToolbarLayout.Arrange(sizes.ToArray(),groups.ToArray(),900);
            check(wide.Rows==1&&Near(wide.Size.Height,36),"capture toolbar stays one compact row when all groups fit");
            check(wide.Dividers.Length==2,"capture toolbar separates groups without labelling every action");
            var narrow=CaptureToolbarLayout.Arrange(sizes.ToArray(),groups.ToArray(),480);
            check(narrow.Rows==2&&Near(narrow.Items[0].Top,narrow.Items[6].Top)&&Near(narrow.Items[7].Top,narrow.Items[13].Top),"capture toolbar wraps complete tool groups before splitting buttons");
            check(narrow.Dividers.Length==1,"capture toolbar has no stranded divider at a line break");
            var tiny=CaptureToolbarLayout.Arrange(sizes.ToArray(),groups.ToArray(),180);
            check(tiny.Rows>2&&tiny.Size.Width<=180,"capture toolbar can wrap inside groups on an unusually narrow screen");
            foreach(var flow in new[]{wide,narrow,tiny}){
                check(Array.TrueForAll(flow.Items,r=>r.IsEmpty||Inside(r,flow.Size)),"all capture buttons remain inside their measured flow bounds");
                for(int i=0;i<flow.Items.Length;i++)for(int j=i+1;j<flow.Items.Length;j++)check(Separate(flow.Items[i],flow.Items[j]),"capture button hit targets do not overlap across rows");
            }
            var mixed=CaptureToolbarLayout.Arrange(new[]{new Size(28,28),new Size(38,36),new Size(),new Size(18,18)},new[]{0,0,1,1},200);
            check(Near(mixed.Items[0].Top,4)&&Near(mixed.Items[3].Top,9)&&mixed.Items[2].IsEmpty,"capture flow centers mixed icon sizes and ignores collapsed controls");
            check(mixed.Dividers.Length==1,"collapsed controls do not create a redundant group gap");
            var empty=CaptureToolbarLayout.Arrange(new Size[0],new int[0],100);
            check(empty.Rows==0&&empty.Size.Width==0&&empty.Size.Height==0,"empty capture flow has no phantom row");
            bool invalid=false;try{CaptureToolbarLayout.Arrange(new[]{new Size(10,10)},new int[0],100);}catch(ArgumentException){invalid=true;}check(invalid,"capture flow rejects mismatched item groups");
            var below=CaptureToolbarLayout.Place(new Size(1000,800),new Rect(100,100,300,200),new Size(650,48),new Size(340,42),new Size());
            check(below.Toolbar.Top>300&&below.Properties.Top>=below.Toolbar.Bottom+4.9,"capture attributes stay together below a selection when there is room");
            var above=CaptureToolbarLayout.Place(new Size(1000,800),new Rect(100,650,300,120),new Size(650,48),new Size(340,42),new Size());
            check(above.Properties.Bottom<650&&above.Toolbar.Top<above.Properties.Top,"capture toolbar and attributes move above the selection together near the screen bottom");
            var noProperties=CaptureToolbarLayout.Place(new Size(800,600),new Rect(200,200,150,150),new Size(500,48),new Size(),new Size());
            check(noProperties.Properties.IsEmpty&&noProperties.Status.IsEmpty,"selection-only tools do not reserve an empty attributes row");
            foreach(double width in new[]{64d,180d,320d,480d,640d,1024d,1920d})foreach(double height in new[]{40d,120d,300d,600d,1080d}){
                var viewport=new Size(width,height);
                var toolbar=CaptureToolbarLayout.Arrange(sizes.ToArray(),groups.ToArray(),Math.Max(1,width-28)).Size;toolbar=new Size(toolbar.Width+12,toolbar.Height+12);
                foreach(var selection in new[]{new Rect(0,0,width,height),new Rect(0,0,10,10),new Rect(width-10,height-10,10,10),new Rect(-30,-30,20,20),Rect.Empty})foreach(bool status in new[]{false,true}){
                    var placement=CaptureToolbarLayout.Place(viewport,selection,toolbar,new Size(350,70),status?new Size(420,54):new Size());
                    check(Inside(placement.Toolbar,viewport)&&Inside(placement.Properties,viewport)&&Inside(placement.Status,viewport),"capture tools attributes and feedback stay on screen at every tested edge and resolution");
                    check(Separate(placement.Toolbar,placement.Properties)&&Separate(placement.Toolbar,placement.Status)&&Separate(placement.Properties,placement.Status),"capture attributes and feedback never cover another tool card");
                }
            }
            var defensive=CaptureToolbarLayout.Place(new Size(),Rect.Empty,new Size(20,20),new Size(20,20),new Size(20,20));
            check(Inside(defensive.Toolbar,new Size(1,1))&&Inside(defensive.Properties,new Size(1,1))&&Inside(defensive.Status,new Size(1,1)),"capture placement handles uninitialized viewport dimensions without negative rectangles");
        }
#if CAPTURE_TOOLBAR_LAYOUT_ONLY
        static int Main(){int count=0;try{Run((passed,message)=>{count++;if(!passed)throw new InvalidOperationException(message);});Console.WriteLine("Capture toolbar pure layout checks passed: "+count);return 0;}catch(Exception error){Console.Error.WriteLine(error);return 1;}}
#endif
    }
}
