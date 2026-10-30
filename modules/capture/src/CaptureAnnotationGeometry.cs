using System;
using System.Collections.Generic;
using System.Windows;

namespace QingJie {
    // Geometry only: shared by pointer hit testing and synthetic, desktop-free tests.
    internal static class CaptureAnnotationGeometry {
        internal static bool Linear(Annotation mark){return mark.Kind=="line"||mark.Kind=="arrow";}
        internal static Point[] Handles(Annotation mark){return Linear(mark)?new[]{mark.Start,mark.End}:ScreenshotSurface.Handles(mark.Bounds);}
        internal static int HandleAt(Annotation mark,Point point){
            if(mark==null)return -1;
            var handles=Handles(mark);int found=-1;double nearest=81;
            for(int i=0;i<handles.Length;i++){double distance=(handles[i]-point).LengthSquared;if(distance<nearest){nearest=distance;found=i;}}
            return found;
        }
        internal static double SegmentDistance(Point p,Point a,Point b){
            var v=b-a;double length=v.LengthSquared;if(length<.001)return (p-a).Length;
            double t=Geometry.Clamp(Vector.Multiply(p-a,v)/length,0,1);return (p-(a+v*t)).Length;
        }
        internal static bool Hit(Annotation mark,Point p){
            double tolerance=Math.Max(6,mark.Width/2+3);var bounds=mark.Bounds;
            if(mark.Kind=="text"||mark.Kind=="mosaic"){bounds.Inflate(mark.Kind=="text"?4:2,mark.Kind=="text"?4:2);return bounds.Contains(p);}
            if(Linear(mark)){
                if(SegmentDistance(p,mark.Start,mark.End)<=tolerance)return true;
                if(mark.Kind=="line")return false;
                return CaptureArrowGeometry.Hit(mark,p,tolerance);
            }
            if(mark.Kind=="pen"){
                for(int i=1;i<mark.Points.Count;i++)if(SegmentDistance(p,mark.Points[i-1],mark.Points[i])<=tolerance)return true;
                return mark.Points.Count==1&&(mark.Points[0]-p).Length<=tolerance;
            }
            var outer=bounds;outer.Inflate(tolerance,tolerance);if(!outer.Contains(p))return false;
            if(mark.Kind=="rect"){
                var inner=bounds;inner.Inflate(-Math.Min(tolerance,bounds.Width/2),-Math.Min(tolerance,bounds.Height/2));
                return inner.Width<=0||inner.Height<=0||!inner.Contains(p);
            }
            if(mark.Kind=="ellipse"){
                double rx=bounds.Width/2,ry=bounds.Height/2;if(rx<1||ry<1)return SegmentDistance(p,mark.Start,mark.End)<=tolerance;
                double x=(p.X-bounds.Left-rx)/rx,y=(p.Y-bounds.Top-ry)/ry;
                return Math.Abs(Math.Sqrt(x*x+y*y)-1)*Math.Min(rx,ry)<=tolerance;
            }
            return false;
        }
        internal static Point Clamp(Point p,Rect area){return new Point(Geometry.Clamp(p.X,area.Left,area.Right),Geometry.Clamp(p.Y,area.Top,area.Bottom));}
        internal static void Move(Annotation mark,Annotation before,Vector delta,Rect area){
            var bounds=before.Bounds;
            delta=new Vector(Geometry.Clamp(delta.X,Math.Min(0,area.Left-bounds.Left),Math.Max(0,area.Right-bounds.Right)),Geometry.Clamp(delta.Y,Math.Min(0,area.Top-bounds.Top),Math.Max(0,area.Bottom-bounds.Bottom)));
            mark.Start=before.Start+delta;mark.End=before.End+delta;
            if(mark.Kind=="pen"){var points=new List<Point>(before.Points.Count);foreach(var point in before.Points)points.Add(point+delta);mark.Points=points;}
        }
        internal static void Resize(Annotation mark,Annotation before,int handle,Vector delta,Rect area){
            if(Linear(mark)){if(handle==0)mark.Start=Clamp(before.Start+delta,area);else mark.End=Clamp(before.End+delta,area);return;}
            var bounds=before.Bounds;double l=bounds.Left,r=bounds.Right,t=bounds.Top,b=bounds.Bottom;
            if(handle==0||handle==6||handle==7)l=Geometry.Clamp(l+delta.X,area.Left,r-1);
            if(handle==2||handle==3||handle==4)r=Geometry.Clamp(r+delta.X,l+1,area.Right);
            if(handle==0||handle==1||handle==2)t=Geometry.Clamp(t+delta.Y,area.Top,b-1);
            if(handle==4||handle==5||handle==6)b=Geometry.Clamp(b+delta.Y,t+1,area.Bottom);
            mark.Start=new Point(l,t);mark.End=new Point(r,b);
            if(mark.Kind=="pen"){
                var points=new List<Point>(before.Points.Count);
                foreach(var point in before.Points)points.Add(new Point(l+(point.X-bounds.Left)*(r-l)/Math.Max(1,bounds.Width),t+(point.Y-bounds.Top)*(b-t)/Math.Max(1,bounds.Height)));
                mark.Points=points;
            }
        }
    }
}
