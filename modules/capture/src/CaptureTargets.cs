using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace QingJie {
    // A short-lived geometry-only snapshot, taken BEFORE any screenshot overlays exist.
    // No UI Automation calls into other apps, image models, text reads or background service.
    internal sealed class CaptureTarget {
        internal Rect Bounds;
        internal Rect ClientBounds=Rect.Empty;
        internal readonly List<CaptureTarget> Children=new List<CaptureTarget>();
        internal CaptureTarget(Rect bounds){Bounds=bounds;}
    }
    internal sealed class CaptureTargets {
        readonly List<CaptureTarget> windows;
        internal CaptureTargets(List<CaptureTarget> orderedWindows){windows=orderedWindows;}
        internal static bool Contains(Rect rect,Point point){return !rect.IsEmpty&&point.X>=rect.Left&&point.Y>=rect.Top&&point.X<rect.Right&&point.Y<rect.Bottom;}
        internal static Rect Clip(Rect rect,Rect bounds){rect.Intersect(bounds);return rect.Width>=3&&rect.Height>=3?rect:Rect.Empty;}
        internal static Rect ToLocal(Rect rect,System.Drawing.Rectangle screen,double width,double height){
            rect=Clip(rect,new Rect(screen.X,screen.Y,screen.Width,screen.Height));
            if(rect.IsEmpty||screen.Width<=0||screen.Height<=0||width<=0||height<=0)return Rect.Empty;
            return new Rect((rect.X-screen.X)*width/screen.Width,(rect.Y-screen.Y)*height/screen.Height,rect.Width*width/screen.Width,rect.Height*height/screen.Height);
        }
        internal List<Rect> At(Point point,Rect screen){return At(point,screen,null);}
        internal List<Rect> At(Point point,Rect screen,CaptureVisualRegions visual){
            Rect visible;return At(point,screen,visual,out visible);
        }
        internal List<Rect> At(Point point,Rect screen,CaptureVisualRegions visual,out Rect visible){
            var result=new List<Rect>();
            visible=Rect.Empty;
            if(!Contains(screen,point))return result;
            AddUnique(result,screen);
            visible=screen;
            foreach(var window in windows){
                if(!Contains(window.Bounds,point))continue;
                visible=Clip(window.Bounds,screen);
                AddPath(window,point,screen,result);
                break; // Never snap to a window obscured by the frontmost hit.
            }
            if(visual!=null)foreach(var rect in visual.At(point,visible))AddUnique(result,rect);
            // Visual cards in WPF/Chromium do not have child HWNDs. Merge visible boxes
            // with the native ancestor chain; wheel/Tab can still expand to the window.
            result.Sort((a,b)=>(a.Width*a.Height).CompareTo(b.Width*b.Height));
            return result;
        }
        static void AddPath(CaptureTarget target,Point point,Rect screen,List<Rect> result){
            var rect=Clip(target.Bounds,screen);if(rect.IsEmpty)return;
            AddUnique(result,rect);
            if(Contains(target.ClientBounds,point))AddUnique(result,Clip(target.ClientBounds,rect));
            foreach(var child in target.Children){if(Contains(child.Bounds,point)){AddPath(child,point,rect,result);break;}}
        }
        static void AddUnique(List<Rect> result,Rect rect){
            // Keep the larger rectangle for near-identical native host/client boundaries.
            foreach(var current in result)if(Math.Abs(current.Left-rect.Left)<3&&Math.Abs(current.Top-rect.Top)<3&&Math.Abs(current.Right-rect.Right)<3&&Math.Abs(current.Bottom-rect.Bottom)<3)return;
            result.Add(rect);
        }
        internal static CaptureTargets Read(){
            var roots=new List<CaptureTarget>();var handles=new List<IntPtr>();var watch=Stopwatch.StartNew();
            EnumWindows((handle,data)=>{
                if(roots.Count>=256||watch.ElapsedMilliseconds>40)return false;
                if(!Usable(handle)||Desktop(handle))return true;
                NativeRect bounds;
                if(DwmGetWindowAttribute(handle,9,out bounds,Marshal.SizeOf(typeof(NativeRect)))!=0&&!GetWindowRect(handle,out bounds))return true;
                var rect=bounds.Rect;if(rect.Width<3||rect.Height<3)return true;
                var target=new CaptureTarget(rect);NativeRect client;NativePoint origin;
                if(GetClientRect(handle,out client)){origin=new NativePoint();if(ClientToScreen(handle,ref origin))target.ClientBounds=Clip(new Rect(origin.X,origin.Y,Math.Max(0,client.Right-client.Left),Math.Max(0,client.Bottom-client.Top)),rect);}
                roots.Add(target);handles.Add(handle);return true;
            },IntPtr.Zero);
            int total=roots.Count;
            for(int i=0;i<roots.Count&&watch.ElapsedMilliseconds<100;i++)ReadChildren(handles[i],roots[i],0,watch,watch.ElapsedMilliseconds+15,ref total);
            return new CaptureTargets(roots);
        }
        static void ReadChildren(IntPtr parent,CaptureTarget target,int depth,Stopwatch watch,long deadline,ref int total,Rect? clipping=null){
            if(depth>=20)return;
            int siblings=0;
            for(var handle=GetWindow(parent,5);handle!=IntPtr.Zero;handle=GetWindow(handle,2)){
                if(++siblings>512||total>=4096||watch.ElapsedMilliseconds>=100||watch.ElapsedMilliseconds>=deadline)return;
                // DWM cloaking is meaningful for top-level windows only. Avoid a COM/
                // DWM round trip for every child, so dense native interfaces stay quick.
                if(!IsWindowVisible(handle))continue;
                NativeRect bounds;if(!GetWindowRect(handle,out bounds))continue;
                var rect=Clip(bounds.Rect,clipping??target.Bounds);
                if(rect.IsEmpty)continue;
                if((GetWindowLongPtr(handle,-20).ToInt64()&0x20)!=0){ReadChildren(handle,target,depth+1,watch,deadline,ref total,rect);continue;}
                // Small native buttons used to be excluded entirely, also losing any
                // nested descendants. Retain normal controls without one-pixel borders.
                if(rect.Width<10||rect.Height<8){ReadChildren(handle,target,depth+1,watch,deadline,ref total,rect);continue;}
                var child=new CaptureTarget(rect);target.Children.Add(child);total++;
                ReadChildren(handle,child,depth+1,watch,deadline,ref total);
            }
        }
        static bool Usable(IntPtr handle){
            if(!IsWindowVisible(handle)||IsIconic(handle))return false;
            // Ignore click-through overlays and DWM-cloaked windows from other virtual desktops.
            if((GetWindowLongPtr(handle,-20).ToInt64()&0x20)!=0)return false;
            int cloaked;return DwmGetWindowAttribute(handle,14,out cloaked,4)!=0||cloaked==0;
        }
        static bool Desktop(IntPtr handle){var name=new StringBuilder(64);GetClassName(handle,name,name.Capacity);return name.ToString()=="Progman"||name.ToString()=="WorkerW";}
        [StructLayout(LayoutKind.Sequential)] struct NativeRect {
            public int Left,Top,Right,Bottom;
            public Rect Rect {get{return Right>Left&&Bottom>Top?new Rect(Left,Top,Right-Left,Bottom-Top):Rect.Empty;}}
        }
        [StructLayout(LayoutKind.Sequential)] struct NativePoint {public int X,Y;}
        delegate bool EnumWindowProc(IntPtr handle,IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowProc callback,IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr handle,uint command);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle,out NativeRect rect);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr handle,out NativeRect rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr handle,ref NativePoint point);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr handle,int index);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr handle,StringBuilder name,int maximum);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr handle,int attribute,out NativeRect value,int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr handle,int attribute,out int value,int size);
    }

    // Cursor movement is interpreted in DIP, while all snapshot rectangles remain
    // in physical screen coordinates. Small controls stay reachable by wheel/Tab;
    // they simply do not take over the default content-sized hover selection.
    internal sealed class CaptureSnapHover {
        internal Rect Candidate=Rect.Empty;
        internal List<Rect> Choices=new List<Rect>();
        internal int Index=-1;
        Rect host=Rect.Empty;
        bool explicitChoice;
        internal static int DefaultIndex(List<Rect> choices,double pixelsPerDipX,double pixelsPerDipY,Rect visible){
            pixelsPerDipX=Math.Max(0.01,pixelsPerDipX);pixelsPerDipY=Math.Max(0.01,pixelsPerDipY);
            for(int i=0;i<choices.Count;i++){
                double width=choices[i].Width/pixelsPerDipX,height=choices[i].Height/pixelsPerDipY;
                if((width>=96&&height>=64&&width*height>=9000)||choices[i]==visible)return i;
            }
            return choices.Count-1; // A small popup can still be selected as a whole.
        }
        internal void Clear(){Candidate=Rect.Empty;Choices=new List<Rect>();Index=-1;host=Rect.Empty;explicitChoice=false;}
        internal Rect Update(List<Rect> choices,Rect visible,Point point,double pixelsPerDipX,double pixelsPerDipY){
            if(choices.Count==0||visible.IsEmpty){Clear();return Candidate;}
            var previous=Candidate;bool sameHost=host==visible;
            Choices=choices;host=visible;
            int priorIndex=Choices.IndexOf(previous),preferred=DefaultIndex(choices,pixelsPerDipX,pixelsPerDipY,visible);
            double marginX=4*Math.Max(0.01,pixelsPerDipX),marginY=4*Math.Max(0.01,pixelsPerDipY);
            bool keep=false;
            if(sameHost&&!previous.IsEmpty){
                if(explicitChoice&&priorIndex>=0)keep=true;
                else if(!explicitChoice){
                    var nearby=previous;nearby.Inflate(marginX,marginY);
                    // Only a narrow boundary band is sticky. Deep movement into a
                    // new child/sibling switches immediately, with no waiting timer.
                    keep=CaptureTargets.Contains(nearby,point)&&visible.Contains(previous)
                        &&(priorIndex<0||!InsideMargin(choices[preferred],point,marginX,marginY));
                }
            }
            if(keep){Candidate=previous;Index=priorIndex;}
            else{Index=preferred;Candidate=choices[preferred];explicitChoice=false;}
            return Candidate;
        }
        static bool InsideMargin(Rect rect,Point point,double x,double y){
            return point.X>=rect.Left+x&&point.X<rect.Right-x&&point.Y>=rect.Top+y&&point.Y<rect.Bottom-y;
        }
        internal Rect Cycle(int direction){
            if(Choices.Count==0||direction==0)return Candidate;
            if(Index<0)Index=Choices.IndexOf(Candidate);
            if(Index<0){
                // A narrow boundary band may retain a rectangle just outside the
                // pointer's new path. Locate its area insertion point rather than
                // assuming index zero, which could make "expand" select a button.
                double area=Candidate.Width*Candidate.Height;
                if(direction>0){for(int i=0;i<Choices.Count;i++)if(Choices[i].Width*Choices[i].Height>area){Index=i;break;}}
                else{for(int i=Choices.Count-1;i>=0;i--)if(Choices[i].Width*Choices[i].Height<area){Index=i;break;}}
                if(Index<0)return Candidate; // No candidate in this direction: no reverse jump.
                Candidate=Choices[Index];explicitChoice=true;return Candidate;
            }
            Index=Math.Max(0,Math.Min(Choices.Count-1,Index+direction));Candidate=Choices[Index];explicitChoice=true;
            return Candidate;
        }
    }

    internal sealed class SnapSelection {
        internal Rect Candidate=Rect.Empty;
        internal Rect Pressed=Rect.Empty;
        internal bool HasDragged;
        Point origin;
        internal void Begin(Point point,bool manual){origin=point;Pressed=manual?Rect.Empty:Candidate;HasDragged=false;Candidate=Rect.Empty;}
        internal bool Move(Point point){if(Math.Abs(point.X-origin.X)>=SystemParameters.MinimumHorizontalDragDistance||Math.Abs(point.Y-origin.Y)>=SystemParameters.MinimumVerticalDragDistance)HasDragged=true;return HasDragged;}
        internal Rect End(Rect manual){var result=HasDragged?manual:Pressed;Pressed=Rect.Empty;Candidate=Rect.Empty;return result;}
    }
}
