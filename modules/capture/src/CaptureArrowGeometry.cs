using System;
using System.Windows;
using System.Windows.Media;

namespace QingJie {
    // The preview, final image and pointer hit test share the same arrowheads.
    internal static class CaptureArrowGeometry {
        internal static string Style(string value){return value=="filled"||value=="double"?value:"open";}
        internal static Point[][] Heads(Annotation mark){
            var delta=mark.End-mark.Start;double distance=delta.Length;
            if(distance<.001)return new Point[0][];
            bool filled=Style(mark.ArrowStyle)=="filled";
            double length=Math.Min(Math.Max(filled?12:10,mark.Width*(filled?4.5:3.5)),distance*(Style(mark.ArrowStyle)=="double"?.45:.8));
            var direction=delta/distance;
            var end=Head(mark.End,direction,length);
            return Style(mark.ArrowStyle)=="double"?new[]{end,Head(mark.Start,-direction,length)}:new[]{end};
        }
        static Point[] Head(Point tip,Vector direction,double length){
            var center=tip-direction*(length*Math.Cos(.5));var wing=new Vector(-direction.Y,direction.X)*(length*Math.Sin(.5));
            return new[]{tip,center+wing,center-wing};
        }
        static StreamGeometry Triangle(Point[] points){
            var geometry=new StreamGeometry();using(var ctx=geometry.Open()){ctx.BeginFigure(points[0],true,true);ctx.LineTo(points[1],true,false);ctx.LineTo(points[2],true,false);}geometry.Freeze();return geometry;
        }
        static StreamGeometry TaperedArrow(Annotation mark,Point[] head){
            var center=new Point((head[1].X+head[2].X)/2,(head[1].Y+head[2].Y)/2);
            var wing=head[1]-center;double halfWidth=Math.Min(mark.Width*.65,wing.Length*.45);
            var neck=wing*(halfWidth/Math.Max(.001,wing.Length));
            // One filled outline keeps the tail genuinely tapered, without a
            // round line cap, a seam under the head, or a constant-width shaft.
            var geometry=new StreamGeometry();using(var ctx=geometry.Open()){
                ctx.BeginFigure(mark.Start,true,true);ctx.LineTo(center+neck,true,false);
                ctx.LineTo(head[1],true,false);ctx.LineTo(head[0],true,false);
                ctx.LineTo(head[2],true,false);ctx.LineTo(center-neck,true,false);
            }geometry.Freeze();return geometry;
        }
        internal static void Draw(DrawingContext drawing,Annotation mark,Pen pen){
            var heads=Heads(mark);bool filled=Style(mark.ArrowStyle)=="filled";
            if(filled){if(heads.Length>0)drawing.DrawGeometry(pen.Brush,null,TaperedArrow(mark,heads[0]));return;}
            drawing.DrawLine(pen,mark.Start,mark.End);
            foreach(var head in heads){drawing.DrawLine(pen,head[0],head[1]);drawing.DrawLine(pen,head[0],head[2]);}
        }
        internal static bool Hit(Annotation mark,Point point,double tolerance){
            foreach(var head in Heads(mark)){
                if(Style(mark.ArrowStyle)=="filled"&&Triangle(head).FillContains(point))return true;
                if(CaptureAnnotationGeometry.SegmentDistance(point,head[0],head[1])<=tolerance||CaptureAnnotationGeometry.SegmentDistance(point,head[0],head[2])<=tolerance)return true;
            }
            return false;
        }
    }
}
