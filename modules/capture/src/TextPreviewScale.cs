using System;
using System.Windows;

namespace QingJie {
    // Zoom is a physical-pixel ratio: 1 means one image pixel per display
    // pixel, not one DIP. The OCR overlay and image share the same Viewbox.
    internal static class TextPreviewScale {
        static double Scale(double value){return value>0&&!double.IsNaN(value)&&!double.IsInfinity(value)?value:1;}
        internal static Size LogicalSize(Size pixels,double zoom,DpiScale dpi){return new Size(pixels.Width*zoom/Scale(dpi.DpiScaleX),pixels.Height*zoom/Scale(dpi.DpiScaleY));}
        internal static double Fit(Size pixels,Size viewport,DpiScale dpi){
            double width=Math.Max(1,viewport.Width)*Scale(dpi.DpiScaleX)/Math.Max(1,pixels.Width);
            double height=Math.Max(1,viewport.Height)*Scale(dpi.DpiScaleY)/Math.Max(1,pixels.Height);
            return Math.Max(.02,Math.Min(1,Math.Min(width,height)));
        }
    }
}
