using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    // Match every subpixel of a complete ClearType line. Set the visual's
    // rendering properties directly so an offscreen worker needs no UI controls.
    internal static class DisplayGlyphEvidence {
        static readonly Typeface[] faces={new Typeface("Segoe UI"),new Typeface("Arial"),new Typeface("Calibri")};
        sealed class DisplayVisual:DrawingVisual {
            internal DisplayVisual(){VisualClearTypeHint=ClearTypeHint.Enabled;VisualTextRenderingMode=TextRenderingMode.ClearType;}
        }
        static FormattedText Line(Typeface face,string text,double size){
            return new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,size,Brushes.Black,null,TextFormattingMode.Ideal,1);
        }
        internal static OcrPage Prove(DesktopLineEvidence.Ink source,string text,CancellationToken cancel,int approximateSize=0,bool subpixelOrigin=false,string competingText=null){
            if(source==null||!source.HasColor||string.IsNullOrEmpty(text)||text.Length>60)return null;
            var words=text.Split(' ');
            if(words.Length>12||words.Any(word=>!VerifiedLineOcr.SimpleWord(word)))return null;
            // A preselected menu carries Segoe UI design metrics. A native OCR
            // hypothesis also gets a chance to prove other common system faces.
            int families=approximateSize==0?faces.Length:1;
            for(int i=0;i<families;i++){
                cancel.ThrowIfCancellationRequested();var face=faces[i];
                double mid=approximateSize;
                if(mid==0){
                    var nominal=Line(face,text,24);
                    double advance=nominal.WidthIncludingTrailingWhitespace-nominal.OverhangLeading-nominal.OverhangTrailing;
                    if(advance<=0)continue;
                    mid=Math.Round(24*source.Bounds.Width/advance);
                }
                foreach(double delta in new[]{0d,-1,1,-.5,.5,-1.5,1.5,-2,2}){
                    cancel.ThrowIfCancellationRequested();double size=mid+delta;if(size<10||size>68)continue;
                    var line=Line(face,text,size);
                    if(Math.Abs(line.Width-source.Bounds.Width)>10)continue;
                    double horizontalOffset=0;var reference=Render(line,source.Dark);
                    if(reference==null)continue;
                    if(!Matches(source,reference)){
                        // A cropped word may start at any of ClearType's three
                        // horizontal subpixels. Each position must still explain
                        // every channel of the whole word with the same response.
                        if(!subpixelOrigin||words.Length!=1||reference.Bounds.Height!=source.Bounds.Height||
                            Math.Abs(reference.Bounds.Width-source.Bounds.Width)>1)continue;
                        bool matched=false;
                        for(int phase=1;phase<3;phase++){
                            cancel.ThrowIfCancellationRequested();
                            reference=Render(line,source.Dark,phase/3d);
                            if(reference!=null&&Matches(source,reference)){matched=true;horizontalOffset=phase/3d;break;}
                        }
                        if(!matched)continue;
                    }
                    if(competingText!=null){
                        cancel.ThrowIfCancellationRequested();
                        var other=Render(Line(face,competingText,size),source.Dark,horizontalOffset);
                        if(other==null||Matches(source,other))return null;
                    }
                    return DesktopLineEvidence.MapInkWords(source,words,(word,pos)=>{
                        var gap=line.BuildHighlightGeometry(new Point(10,0),pos,1);
                        return gap==null||gap.Bounds.IsEmpty?double.NaN:gap.Bounds.X+gap.Bounds.Width*.5-reference.Bounds.X+source.Bounds.X;
                    });
                }
            }return null;
        }
        internal static OcrPage ProveWordInCaption(DesktopLineEvidence.Ink source,string caption,string[] words,int position,int approximateSize,CancellationToken cancel){
            foreach(int offset in new[]{0,-1,1}){
                cancel.ThrowIfCancellationRequested();int size=approximateSize+offset;if(size<10||size>48)continue;
                var line=Line(faces[0],caption,size);var reference=Render(line,source.Dark);if(reference==null)continue;
                // Split the reference at real blank columns adjacent to the
                // font's space geometry. Keep all painted channels of the word.
                var layout=DesktopLineEvidence.MapInkWords(reference,words,(word,pos)=>{
                    var space=line.BuildHighlightGeometry(new Point(10,0),pos,1);
                    return space==null||space.Bounds.IsEmpty?double.NaN:space.Bounds.X+space.Bounds.Width*.5;
                });
                if(layout==null)continue;
                var box=layout.Words[position].Box;
                reference.Bounds=new Int32Rect((int)box.X,(int)box.Y,(int)box.Width,(int)box.Height);
                if(Matches(source,reference))return DesktopLineEvidence.MapInkWords(source,new[]{words[position]},null);
            }
            return null;
        }
        static DesktopLineEvidence.Ink Render(FormattedText line,bool dark,double horizontalOffset=0){
            int width=Math.Max(32,(int)Math.Ceiling(line.WidthIncludingTrailingWhitespace)+24);
            int height=Math.Max(62,(int)Math.Ceiling(line.Height)+16);
            if(width>768||height>112||width*height>70000)return null;
            var visual=new DisplayVisual();line.SetForegroundBrush(dark?Brushes.White:Brushes.Black);
            using(var dc=visual.RenderOpen()){
                dc.DrawRectangle(dark?Brushes.Black:Brushes.White,null,new Rect(0,0,width,height));
                dc.DrawText(line,new Point(10+horizontalOffset,5));
            }
            var bmp=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bmp.Render(visual);
            return DesktopLineEvidence.Analyze(VerifiedLineOcr.OpaqueReferencePixels(bmp,dark),width,height);
        }
        static bool Matches(DesktopLineEvidence.Ink source,DesktopLineEvidence.Ink reference){
            var a=source.Bounds;var b=reference.Bounds;
            if(!reference.HasColor||a.Width!=b.Width||a.Height!=b.Height)return false;
            int bg=source.Pixels[0];var levels=new short[256];for(int i=0;i<levels.Length;i++)levels[i]=-1;
            var occurrences=new int[256];
            levels[0]=0;levels[255]=(short)source.Contrast;
            // Monitor contrast and gamma may differ from an offscreen bitmap.
            // A single non-decreasing response must explain every R,G,B value.
            // No unpainted subpixel may gain ink, or a faint painted one disappear.
            for(int y=0;y<a.Height;y++){
                int si=4*((a.Y+y)*source.W+a.X),ri=4*((b.Y+y)*reference.W+b.X);
                for(int x=0;x<a.Width;x++,si+=4,ri+=4)for(int c=0;c<3;c++){
                    int raw=reference.Dark?reference.Pixels[ri+c]:255-reference.Pixels[ri+c];
                    int actual=source.Dark?source.Pixels[si+c]-bg:bg-source.Pixels[si+c];
                    if((raw==0)!=(actual==0)||(levels[raw]>=0&&levels[raw]!=actual))return false;
                    levels[raw]=(short)actual;
                    occurrences[raw]++;
                }
            }
            int distinct=0,tones=0,last=-1;bool repeated=true;
            for(int i=0;i<levels.Length;i++){
                int value=levels[i];if(value<0)continue;
                if(value<last)return false;
                if(value>last)tones++;
                if(occurrences[i]<3)repeated=false;
                last=value;distinct++;
            }
            if(distinct>=16&&tones>=7)return true;
            // Classic small ClearType has seven tones. An offscreen opaque
            // stroke can quantize to 254 or 255 while both reach the same
            // foreground on the monitor. Only that adjacent endpoint may merge.
            bool classic=distinct==7||distinct==8&&levels[254]==source.Contrast&&levels[255]==source.Contrast;
            return classic&&tones==7&&repeated;
        }
    }
}
