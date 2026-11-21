using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    internal static class KoreanOcr {
        // This identifier is private to the local worker of the same application version.
        internal const string WorkerLanguage="auto-korean";

        sealed class Row {
            internal readonly List<WordBox> Words;
            internal readonly Rect Box;
            internal readonly bool Reliable;
            internal Row(IEnumerable<WordBox> words){
                Words=words.ToList();var box=Rect.Empty;
                double units=0,weightedScore=0;
                foreach(var word in Words){
                    box.Union(word.Box);
                    foreach(char c in word.Text)if(char.IsLetterOrDigit(c)){
                        double width=c<0x2e80?.55:1.0;
                        units+=width;
                        weightedScore+=width*(word.ConfidenceSource==OcrConfidenceSource.Unknown?1:word.Confidence);
                    }
                }
                Box=box;
                Reliable=units>0&&weightedScore/units>=.83&&units*box.Height>=box.Width*.40;
            }
        }
        static List<Row> Rows(OcrPage page){return page.Words.GroupBy(w=>w.Line).Select(g=>new Row(g)).ToList();}

        internal sealed class InkLine {
            internal readonly int Top,Bottom;
            internal readonly List<Rect> Words=new List<Rect>();
            internal double Pitch;
            internal InkLine(int top,int bottom){Top=top;Bottom=bottom;}
        }

        // A paragraph of square syllables separated into short, spaced words is
        // worth trying with the small Korean recognizer before the general model.
        // Geometry only determines reader order. Accepting a transcript also
        // requires high-confidence Hangul over the original ink of every row.
        internal static List<InkLine> FindSpacedParagraph(BitmapSource source,CancellationToken cancel){
            int width=source.PixelWidth,height=source.PixelHeight;
            if(width<72||height<20||width>2500||height>1800||(long)width*height>2500000)return null;
            var image=source.Format==PixelFormats.Bgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            var pixels=new byte[width*4];
            image.CopyPixels(new Int32Rect(0,0,width,1),pixels,pixels.Length,0);
            int background=Luma(pixels,0);
            var columns=new bool[width];
            var lines=new List<InkLine>();
            int start=-1,backgroundSamples=0,differentSamples=0;
            for(int y=0;y<=height;y++){
                cancel.ThrowIfCancellationRequested();
                int first=-1,active=0;
                if(y<height){
                    image.CopyPixels(new Int32Rect(0,y,width,1),pixels,pixels.Length,0);
                    if(y%8==0){
                        for(int x=0;x<width;x+=13){
                            backgroundSamples++;
                            if(Math.Abs(Luma(pixels,x*4)-background)>26)differentSamples++;
                        }
                    }
                    for(int x=0;x<width;x++)if(Math.Abs(Luma(pixels,x*4)-background)>=45){
                        if(active==0)first=x;
                        else columns[x]=true;
                        active++;
                    }
                }
                if(active>=2){
                    if(start<0)start=y;
                    if(y-start>74)return null;
                    columns[first]=true;
                }else if(start>=0){
                    var line=MeasureSpacedLine(columns,start,y);
                    if(line==null)return null;
                    lines.Add(line);
                    Array.Clear(columns,0,width);
                    start=-1;
                }
            }
            return lines.Count>=2&&differentSamples*3<backgroundSamples?lines:null;
        }
        static InkLine MeasureSpacedLine(bool[] columns,int top,int bottom){
            int h=bottom-top;
            if(h<6)return null;
            int gap=Math.Max(3,(int)Math.Round(h*.35)),begin=-1,last=-1,streak=0,broad=0,runs=0;
            var line=new InkLine(top,bottom);
            for(int x=0;x<=columns.Length;x++){
                if(x<columns.Length&&columns[x]){
                    if(begin<0)begin=x;
                    last=x;streak++;
                }else{
                    if(streak>0){runs++;if(streak>=h*.62)broad++;streak=0;}
                    if(begin>=0&&(x-last>=gap||x==columns.Length)){
                        line.Words.Add(new Rect(begin,top,last-begin+1,h));begin=-1;
                    }
                }
            }
            if(line.Words.Count<3||broad*3<runs)return null;
            int best=0;double bestDistance=double.MaxValue;
            for(double pitch=h*.90;pitch<=h*1.40;pitch+=.25){
                for(double inset=0;inset<=pitch*.26;inset+=.5){
                    int matches=0;double distance=0;
                    foreach(var word in line.Words){
                        double units=(word.Width+inset)/pitch;
                        double error=Math.Abs(units-Math.Max(1,Math.Round(units)));
                        if(error<=.15)matches++;
                        distance+=Math.Min(.5,error);
                    }
                    if(matches>best||matches==best&&distance<bestDistance){best=matches;bestDistance=distance;line.Pitch=pitch;}
                }
            }
            return best>=line.Words.Count*.69?line:null;
        }
        internal static bool AccountsForParagraph(List<InkLine> ink,OcrPage candidate){
            string reason;return AccountsForParagraph(ink,candidate,out reason);
        }
        internal static bool AccountsForParagraph(List<InkLine> ink,OcrPage candidate,out string reason){
            var rows=Rows(candidate).OrderBy(r=>r.Box.Top).ToList();
            reason="line-count";
            if(ink==null||ink.Count!=rows.Count)return false;
            for(int i=0;i<ink.Count;i++){
                reason=CompareLine(ink[i],rows[i]);
                if(reason!=null){reason="line-"+(i+1)+"-"+reason;return false;}
            }
            return true;
        }
        static string CompareLine(InkLine band,Row row){
            double h=band.Bottom-band.Top;
            if(!row.Reliable)return "score";
            if(Math.Min(band.Bottom,row.Box.Bottom)-Math.Max(band.Top,row.Box.Top)<h*.65)return "vertical-overlap";
            int letters=0,syllables=0;double confidence=0;
            foreach(var word in row.Words){
                int count=word.Text.Count(Geometry.IsHangul);
                if(word.ConfidenceSource!=OcrConfidenceSource.Model)return "unknown-confidence";
                letters+=word.Text.Count(char.IsLetter);
                syllables+=count;confidence+=count*word.Confidence;
            }
            if(syllables<4||syllables<letters*.88||confidence<syllables*.82)return "script-confidence";
            double cells=0,covered=0,total=0;
            foreach(var group in band.Words){
                cells+=Math.Max(1,Math.Round(group.Width/band.Pitch));
                double accounted=0,right=group.Left;
                foreach(var word in row.Words){
                    double from=Math.Max(right,word.Box.Left),to=Math.Min(group.Right,word.Box.Right);
                    if(to>from){accounted+=to-from;right=to;}
                }
                // Model glyph boxes can omit an antialiased edge or a detached
                // punctuation mark. Missing an entire word still disqualifies
                // the line even when the rest of a long paragraph is complete.
                if(accounted<=0||accounted+1<group.Width*.72)return "word-coverage";
                covered+=accounted;total+=group.Width;
            }
            if(covered+1<total*.72)return "line-coverage";
            return syllables<cells*.85||syllables>cells*1.14?"glyph-count":null;
        }
        internal static OcrPage ConfirmedLines(List<InkLine> ink,OcrPage candidate){
            if(ink==null||candidate==null)return null;
            var result=new OcrPage();
            foreach(var row in Rows(candidate).OrderBy(r=>r.Box.Top)){
                if(ink.Any(band=>CompareLine(band,row)==null))result.Words.AddRange(row.Words);
            }
            if(result.Words.Count==0)return null;
            OcrService.IndexText(result);return result;
        }

        // Compare visible rows of ink with the primary reader's spatial coverage.
        // Sparse, unreliable transcripts are common when the primary alphabet lacks
        // the source script. Uniform images and well-accounted-for strokes stop here.
        internal static bool NeedsSecondReading(BitmapSource source,OcrPage page,CancellationToken cancel){
            if(source.PixelWidth<12||source.PixelHeight<6)return false;
            var rows=Rows(page);
            var image=source.Format==PixelFormats.Bgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            int width=image.PixelWidth,height=image.PixelHeight;
            int dx=Math.Max(1,(width+1535)/1536),dy=Math.Max(1,(height+767)/768);
            var pixels=new byte[checked(width*4)];
            int top=-1,left=width,right=-1,activeRows=0;
            for(int y=0;y<height+dy;y+=dy){
                cancel.ThrowIfCancellationRequested();
                int edges=0,first=width,last=-1;
                if(y<height){
                    image.CopyPixels(new Int32Rect(0,y,width,1),pixels,pixels.Length,0);
                    int prior=Luma(pixels,0);
                    for(int x=dx;x<width;x+=dx){
                        int value=Luma(pixels,x*4);
                        if(Math.Abs(value-prior)>=30){edges++;first=Math.Min(first,x-dx);last=x;}
                        prior=value;
                    }
                }
                if(edges>=6){
                    if(top<0)top=y;
                    left=Math.Min(left,first);right=Math.Max(right,last);activeRows++;
                }else if(top>=0){
                    int h=y-top;
                    if(activeRows>=2&&h>=4&&h<=96&&right-left>=Math.Max(14,h*1.7)
                        &&!Covered(rows,left,right,top,y))return true;
                    top=-1;left=width;right=-1;activeRows=0;
                }
            }
            return false;
        }
        static int Luma(byte[] p,int i){
            int value=(29*p[i]+150*p[i+1]+77*p[i+2])>>8;
            return 255-(255-value)*p[i+3]/255;
        }
        static bool Covered(List<Row> rows,int left,int right,int top,int bottom){
            double width=right-left,height=bottom-top,coverage=0;
            foreach(var row in rows){
                if(!row.Reliable||Math.Min(row.Box.Bottom,bottom)-Math.Max(row.Box.Top,top)<height*.45)continue;
                coverage+=Math.Max(0,Math.Min(row.Box.Right,right)-Math.Max(row.Box.Left,left));
                if(coverage>=width*.65)return true;
            }
            return false;
        }

        internal static OcrPage Read(BitmapSource image){
            var paragraph=FindSpacedParagraph(image,CancellationToken.None);
            // Choose scale from the source glyph height. Enlarging already-large
            // text produces more pixels without adding evidence for the reader.
            double ratio=3;
            if(paragraph!=null){
                int typicalHeight=paragraph.Select(line=>line.Bottom-line.Top).OrderBy(h=>h).ElementAt(paragraph.Count/2);
                ratio=Math.Max(1,Math.Min(4,Math.Round(40.0/typicalHeight*2)/2));
            }
            ratio=Math.Min(ratio,Math.Sqrt(14000000.0/((long)image.PixelWidth*image.PixelHeight)));
            var input=OcrService.Prepare(image,ratio);
            return EnglishOcr.ReadKorean(new OcrPixels(input),ratio);
        }
        // Keep labels outside the recovered rows. A partial or unconvincing
        // candidate never replaces a longer existing line or a confident reading.
        internal static OcrPage Merge(OcrPage original,OcrPage candidate){
            if(candidate.Words.Count==0)return original;
            var rows=Rows(original);bool changed=false;
            foreach(var korean in Rows(candidate)){
                var matches=rows.Where(r=>SameLine(korean.Box,r.Box)).ToList();
                if(matches.Any(r=>r.Reliable&&r.Box.Width>=korean.Box.Width*.65
                    ||Rect.Intersect(r.Box,korean.Box).Width<r.Box.Width*.65))continue;
                foreach(var match in matches)rows.Remove(match);
                rows.Add(korean);changed=true;
            }
            if(!changed)return original;
            var result=new OcrPage{Notice=original.Notice};int line=0;
            foreach(var row in rows.OrderBy(r=>r.Box.Top).ThenBy(r=>r.Box.Left)){
                foreach(var word in row.Words){word.Line=line;result.Words.Add(word);}
                line++;
            }
            OcrService.IndexText(result);return result;
        }
        static bool SameLine(Rect a,Rect b){
            var common=Rect.Intersect(a,b);
            return !common.IsEmpty&&common.Height>=Math.Min(a.Height,b.Height)*.45
                &&common.Width>=Math.Min(a.Width,b.Width)*.45;
        }
    }
}
