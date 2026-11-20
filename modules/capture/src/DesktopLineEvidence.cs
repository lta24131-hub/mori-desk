using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QingJie {
    // A separate verifier for RGB subpixel text. Do not flatten it to grayscale:
    // matching glyph bounds or luminance would not account for faint coloured dots.
    internal static class DesktopLineEvidence {
        internal sealed class Ink {internal byte[] Pixels;internal int W,H,Contrast;internal bool Dark,HasColor,HasDetachedDot;internal Int32Rect Bounds;}
        internal static bool MayBeDetachedDot(int width,int height,int gap){
            return width<=6&&height<=4&&gap<=4;
        }
        internal static Ink Analyze(byte[] p,int w,int h,bool allowDetachedDot=false){
            int bg=p[0];if((bg>50&&bg<205)||p[1]!=bg||p[2]!=bg)return null;
            bool dark=bg<=50;
            int l=w,t=h,r=-1,b=-1,n=0,previous=-1,contrast=0,bodyStart=-1;bool color=false;
            for(int y=0;y<h;y++){
                int previousLeft=l,previousRight=r;bool active=false;
                for(int x=0;x<w;x++){int i=(y*w+x)*4;
                    if(p[i+3]!=255)return null;
                    if(p[i]==bg&&p[i+1]==bg&&p[i+2]==bg)continue;
                    if(dark?(p[i]<bg||p[i+1]<bg||p[i+2]<bg):(p[i]>bg||p[i+1]>bg||p[i+2]>bg))return null;
                    if(x<2||x>=w-2||y<2||y>=h-2)return null;
                    n++;active=true;if(p[i]!=p[i+1]||p[i]!=p[i+2])color=true;
                    contrast=Math.Max(contrast,Math.Max(Math.Abs(bg-p[i]),Math.Max(Math.Abs(bg-p[i+1]),Math.Abs(bg-p[i+2]))));
                    l=Math.Min(l,x);r=Math.Max(r,x);t=Math.Min(t,y);b=Math.Max(b,y);
                }
                if(active){
                    if(previous>=0&&y>previous+1){
                        // A dot on i or j can be the only ascender in a word.
                        // This exception is opt-in for exhaustive image proof.
                        // Ordinary line recognition still rejects separate bands.
                        if(!allowDetachedDot||bodyStart>=0||!MayBeDetachedDot(previousRight-previousLeft+1,previous-t+1,y-previous-1))return null;
                        bodyStart=y;
                    }
                    previous=y;
                }
            }
            if(contrast<96||n<30||b-t+1<10||b-t+1>50||r-l+1<12||n>(b-t+1)*(r-l+1)*.80)return null;
            if(bodyStart>=0&&b-bodyStart+1<7)return null;
            return new Ink{Pixels=p,W=w,H=h,Contrast=contrast,Dark=dark,HasColor=color,HasDetachedDot=bodyStart>=0,Bounds=new Int32Rect(l,t,r-l+1,b-t+1)};
        }
        internal static Ink Analyze(BitmapSource image,bool allowDetachedDot=false){
            if(!VerifiedLineOcr.Small(image))return null;
            return Analyze(VerifiedLineOcr.CopyBgra(image),image.PixelWidth,image.PixelHeight,allowDetachedDot);
        }
        static bool Same(Ink a,Ink b){
            if(b==null||a.HasColor!=b.HasColor||a.Dark!=b.Dark||a.Bounds.Width!=b.Bounds.Width||a.Bounds.Height!=b.Bounds.Height)return false;
            for(int y=0;y<a.Bounds.Height;y++){
                int i=((y+a.Bounds.Y)*a.W+a.Bounds.X)*4,j=((y+b.Bounds.Y)*b.W+b.Bounds.X)*4;
                for(int x=0;x<a.Bounds.Width*4;x++)if(a.Pixels[i+x]!=b.Pixels[j+x])return false;
            }return true;
        }
        internal static bool Reproduces(Ink source,string text,CancellationToken cancel){OcrPage unused;return Reproduces(source,text,cancel,false,out unused);}
        internal static OcrPage Prove(Ink source,string text,CancellationToken cancel){
            var display=DisplayGlyphEvidence.Prove(source,text,cancel);if(display!=null)return display;
            if(source==null||(source.Pixels[0]!=0&&source.Pixels[0]!=255))return null;
            OcrPage page;return Reproduces(source,text,cancel,true,out page)?page:null;
        }
        static bool Reproduces(Ink source,string text,CancellationToken cancel,bool requireWords,out OcrPage page){
            page=null;
            if(source==null||string.IsNullOrEmpty(text)||text.Length>60)return false;
            int w=Math.Min(800,source.Bounds.Width+40),h=Math.Min(120,source.Bounds.Height+38);
            // All candidate fonts share this canvas. The scratch pixels are
            // compared and mapped synchronously before the next rasterisation.
            int stride=w*4;var bytes=new byte[stride*h];
            using(var bm=new Bitmap(w,h,System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using(var g=Graphics.FromImage(bm))using(var fmt=StringFormat.GenericTypographic){
                var bg=source.Dark?System.Drawing.Color.Black:System.Drawing.Color.White;var fg=source.Dark?System.Drawing.Brushes.White:System.Drawing.Brushes.Black;
                foreach(string family in new[]{"Segoe UI","Arial","Calibri"}){
                    g.TextRenderingHint=TextRenderingHint.ClearTypeGridFit;
                    float measure;using(var nominal=new Font(family,24,System.Drawing.FontStyle.Regular,GraphicsUnit.Pixel))measure=g.MeasureString(text,nominal,new PointF(0,0),fmt).Width;
                    if(measure<1)continue;double mid=Math.Round(24*source.Bounds.Width/measure*2)/2;
                    foreach(double offset in new[]{0,.5,-.5,1,-1,1.5,-1.5,2,-2}){
                        cancel.ThrowIfCancellationRequested();float size=(float)(mid+offset);if(size<10||size>68)continue;
                        using(var font=new Font(family,size,System.Drawing.FontStyle.Regular,GraphicsUnit.Pixel)){
                            g.Clear(bg);g.DrawString(text,font,fg,new PointF(5,5),fmt);g.Flush();
                            var d=bm.LockBits(new Rectangle(0,0,w,h),ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            try{
                                if(d.Stride==stride)Marshal.Copy(d.Scan0,bytes,0,bytes.Length);
                                else for(int y=0;y<h;y++)Marshal.Copy(IntPtr.Add(d.Scan0,d.Stride*y),bytes,y*stride,stride);
                            }finally{bm.UnlockBits(d);}
                            var reference=Analyze(bytes,w,h);
                            if(Same(source,reference)){
                                if(!requireWords)return true;
                                page=MapWords(source,reference,text,font,g,fmt);
                                return page!=null;
                            }
                        }
                    }
                }
            }return false;
        }
        static OcrPage MapWords(Ink s,Ink reference,string text,Font font,Graphics g,StringFormat fmt){
            var words=text.Split(' ');if(words.Length==0||words.Length>12)return null;
            var ranges=new CharacterRange[words.Length];int pos=0;
            for(int i=0;i<words.Length;i++){if(!VerifiedLineOcr.SimpleWord(words[i]))return null;ranges[i]=new CharacterRange(pos,words[i].Length);pos+=words[i].Length+1;}
            using(var measure=(StringFormat)fmt.Clone()){
                measure.SetMeasurableCharacterRanges(ranges);var layout=g.MeasureCharacterRanges(text,font,new RectangleF(5,5,900,150),measure);
                try{
                    return MapInkWords(s,words,(i,posInLine)=>(layout[i].GetBounds(g).Right+layout[i+1].GetBounds(g).Left)*.5-reference.Bounds.X+s.Bounds.X);
                }finally{foreach(var region in layout)region.Dispose();}
            }
        }
        internal static OcrPage MapInkWords(Ink source,string[] words,Func<int,int,double> gapCenter){
            var page=new OcrPage();var box=source.Bounds;
            int x0=box.X,last=box.X+box.Width,pos=0,bg=source.Pixels[0];
            for(int i=0;i<words.Length;i++){
                pos+=words[i].Length;int end=last;
                if(i+1<words.Length){
                    double center=gapCenter(i,pos);if(double.IsNaN(center))return null;
                    int mid=(int)Math.Round(center);end=-1;pos++;
                    foreach(int d in new[]{0,-1,1,-2,2}){
                        int x=mid+d;if(x<=x0||x>=last)continue;bool clear=true;
                        for(int y=box.Y;y<box.Y+box.Height;y++)if(InkAt(source,x,y,bg)){clear=false;break;}
                        if(clear){end=x;break;}
                    }
                    if(end<0)return null;
                }
                int l=end,r=-1,t=box.Y+box.Height,b=-1;
                for(int y=box.Y;y<box.Y+box.Height;y++)for(int x=x0;x<end;x++)if(InkAt(source,x,y,bg)){
                    l=Math.Min(l,x);r=Math.Max(r,x);t=Math.Min(t,y);b=Math.Max(b,y);
                }
                if(r<l)return null;
                page.Words.Add(new WordBox{Text=words[i],Line=0,Box=new System.Windows.Rect(l,t,r-l+1,b-t+1)});x0=end;
            }
            OcrService.IndexText(page);return page;
        }
        static bool InkAt(Ink s,int x,int y,int bg){
            int i=(y*s.W+x)*4;return s.Pixels[i]!=bg||s.Pixels[i+1]!=bg||s.Pixels[i+2]!=bg;
        }
    }
}
