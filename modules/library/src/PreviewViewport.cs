using System;

namespace Mori {
 // Image-space viewport math is independent of either UI toolkit and allocates
 // nothing while dragging. Offsets are always clamped to the visible image.
 public sealed class PreviewViewport {
  public readonly double ImageWidth,ImageHeight;
  public double Width {get;private set;}
  public double Height {get;private set;}
  public double Zoom {get;private set;}
  public double X {get;private set;}
  public double Y {get;private set;}
  public bool IsFit {get;private set;}
  public double DisplayWidth {get{return Math.Max(1,ImageWidth*Zoom);}}
  public double DisplayHeight {get{return Math.Max(1,ImageHeight*Zoom);}}
  public bool CanPan {get{return DisplayWidth>Width+0.5||DisplayHeight>Height+0.5;}}
  public PreviewViewport(double imageWidth,double imageHeight){if(!Finite(imageWidth)||!Finite(imageHeight)||imageWidth<1||imageHeight<1)throw new ArgumentOutOfRangeException("imageWidth");ImageWidth=imageWidth;ImageHeight=imageHeight;Width=Height=1;Zoom=1;IsFit=true;Fit();}
  static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
  static double Bound(double value,double low,double high){return Math.Max(low,Math.Min(high,value));}
  double FitZoom(){return Math.Min(Math.Max(1,Width-4)/ImageWidth,Math.Max(1,Height-4)/ImageHeight);}
  void Clamp(){X=DisplayWidth<=Width?(Width-DisplayWidth)/2:Bound(X,Width-DisplayWidth,0);Y=DisplayHeight<=Height?(Height-DisplayHeight)/2:Bound(Y,Height-DisplayHeight,0);}
  public void Fit(){IsFit=true;Zoom=FitZoom();Clamp();}
  public void Resize(double width,double height){
   if(!Finite(width)||!Finite(height))return;
   double centerX=(Width/2-X)/Zoom,centerY=(Height/2-Y)/Zoom;
   Width=Math.Max(1,width);Height=Math.Max(1,height);
   if(IsFit)Fit();else{X=Width/2-centerX*Zoom;Y=Height/2-centerY*Zoom;Clamp();}
  }
  public void ZoomAt(double factor,double pointerX,double pointerY){
   if(!Finite(factor)||factor<=0||!Finite(pointerX)||!Finite(pointerY))return;
   pointerX=Bound(pointerX,0,Width);pointerY=Bound(pointerY,0,Height);
   double imageX=(pointerX-X)/Zoom,imageY=(pointerY-Y)/Zoom;
   double minimum=Math.Min(0.03,FitZoom()),maximum=Math.Max(12,FitZoom());
   // WinForms/GDI controls need bounded dimensions even for giant scans.
   maximum=Math.Max(minimum,Math.Min(maximum,60000/Math.Max(ImageWidth,ImageHeight)));
   Zoom=Bound(Zoom*factor,minimum,maximum);IsFit=false;X=pointerX-imageX*Zoom;Y=pointerY-imageY*Zoom;Clamp();
  }
  public void PanTo(double x,double y){if(!Finite(x)||!Finite(y))return;X=x;Y=y;Clamp();}
 }
}
