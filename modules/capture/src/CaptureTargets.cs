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
        internal List<Rect> At(Point point,Rect screen){
            var result=new List<Rect>();
            if(!Contains(screen,point))return result;
            AddUnique(result,screen);
            foreach(var window in windows){
                if(!Contains(window.Bounds,point))continue;
                AddPath(window,point,screen,result);
                break; // Never snap to a window obscured by the frontmost hit.
            }
            result.Reverse(); // Most specific reliable native region first; wheel expands outward.
            return result;
        }
        static void AddPath(CaptureTarget target,Point point,Rect screen,List<Rect> result){
            var rect=Clip(target.Bounds,screen);if(rect.IsEmpty)return;
            AddUnique(result,rect);
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
                roots.Add(new CaptureTarget(rect));handles.Add(handle);return true;
            },IntPtr.Zero);
            int total=roots.Count;
            for(int i=0;i<roots.Count&&watch.ElapsedMilliseconds<80;i++)ReadChildren(handles[i],roots[i],0,watch,ref total);
            return new CaptureTargets(roots);
        }
        static void ReadChildren(IntPtr parent,CaptureTarget target,int depth,Stopwatch watch,ref int total){
            if(depth>=12)return;
            int siblings=0;
            for(var handle=GetWindow(parent,5);handle!=IntPtr.Zero;handle=GetWindow(handle,2)){
                if(++siblings>256||total>=2048||watch.ElapsedMilliseconds>=80)return;
                if(!Usable(handle))continue;
                NativeRect bounds;if(!GetWindowRect(handle,out bounds))continue;
                var rect=Clip(bounds.Rect,target.Bounds);
                // Ignore tiny buttons/scrollbars: useful content panels, not noisy per-icon boxes.
                if(rect.IsEmpty||rect.Width<48||rect.Height<32)continue;
                var child=new CaptureTarget(rect);target.Children.Add(child);total++;
                ReadChildren(handle,child,depth+1,watch,ref total);
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
        delegate bool EnumWindowProc(IntPtr handle,IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowProc callback,IntPtr data);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr handle,uint command);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle,out NativeRect rect);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr handle);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr handle);
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr handle,int index);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr handle,StringBuilder name,int maximum);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr handle,int attribute,out NativeRect value,int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr handle,int attribute,out int value,int size);
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
