using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    // Shortlist at most two captions by font metrics, then prove the entire
    // raster before returning text. Only the reusable label metrics are cached.
    internal static class ExactMenuGlyphOcr {
        sealed class Label {internal string Text;internal string[] Words;internal double[] Starts,Ends;internal bool[] Descends;internal double Left,Width;}
        sealed class Candidate {internal Label Label;internal int Size;internal Rect[] Boxes;}
        sealed class WordContext {internal string Caption;internal string[] Words;internal int Position;}
        sealed class SingleLabel {internal string Text;internal Rect[] Glyphs;internal Rect Bounds;internal bool Standalone;internal List<WordContext> Contexts=new List<WordContext>();}
        struct GlyphMetrics {internal double Width,Left,Right;}
        static readonly Typeface face=new Typeface("Segoe UI");
        static readonly Lazy<Label[]> labels=new Lazy<Label[]>(BuildLabels);
        static readonly Lazy<SingleLabel[]> singleLabels=new Lazy<SingleLabel[]>(BuildSingleLabels);
        static SingleLabel[] BuildSingleLabels(){
            GlyphTypeface font;if(!face.TryGetGlyphTypeface(out font))return new SingleLabel[0];
            var byText=new Dictionary<string,SingleLabel>(StringComparer.Ordinal);
            foreach(string text in Translation.StandaloneScreenshotWords())
                byText.Add(text,new SingleLabel{Text=text,Standalone=true});
            // Rasterizing a word inside a font run can distribute fractional
            // advances differently. A surrounding command provides alternative
            // font positions only. Its other words never become recognized text.
            foreach(string caption in Translation.CanonicalEnglishScreenshotCommands()){
                var words=caption.Split(' ');
                for(int i=1;i<words.Length;i++){
                    string text=words[i];if(text.Length<3||text.Length>12)continue;
                    SingleLabel entry;
                    if(!byText.TryGetValue(text,out entry)){entry=new SingleLabel{Text=text};byText.Add(text,entry);}
                    entry.Contexts.Add(new WordContext{Caption=caption,Words=words,Position=i});
                }
            }
            var outlines=new Dictionary<char,Rect>();
            foreach(var entry in byText.Values){
                var text=entry.Text;entry.Glyphs=new Rect[text.Length];entry.Bounds=Rect.Empty;double pen=0;
                for(int i=0;i<text.Length;i++){
                    ushort glyph=font.CharacterToGlyphMap[text[i]];Rect outline;
                    if(!outlines.TryGetValue(text[i],out outline)){
                        outline=font.GetGlyphOutline(glyph,100,100).Bounds;outlines.Add(text[i],outline);
                    }
                    outline.Offset(pen,0);entry.Glyphs[i]=outline;entry.Bounds.Union(outline);
                    pen+=100*font.AdvanceWidths[glyph];
                }
            }
            return new List<SingleLabel>(byText.Values).ToArray();
        }
        static Label[] BuildLabels(){
            GlyphTypeface font;if(!face.TryGetGlyphTypeface(out font))return new Label[0];
            var metrics=new Dictionary<char,GlyphMetrics>();
            var characters=font.CharacterToGlyphMap;
            var widths=font.AdvanceWidths;var leftBearings=font.LeftSideBearings;var rightBearings=font.RightSideBearings;
            double space=widths[characters[' ']];
            var list=new List<Label>();foreach(string text in Translation.CanonicalEnglishScreenshotCommands()){
                var words=text.Split(' ');if(words.Length<2||words.Length>5)continue;
                var label=new Label{Text=text,Words=words,Starts=new double[words.Length],Ends=new double[words.Length],Descends=new bool[words.Length]};
                double pen=0;bool supported=true;
                for(int i=0;i<words.Length;i++){
                    if(i!=0)pen+=space;
                    double min=double.MaxValue,max=double.MinValue;
                    foreach(char c in words[i]){
                        if("gjpqy".IndexOf(c)>=0)label.Descends[i]=true;
                        GlyphMetrics metric;
                        if(!metrics.TryGetValue(c,out metric)){
                            ushort glyph;if(!characters.TryGetValue(c,out glyph)){supported=false;break;}
                            metric=new GlyphMetrics{Width=widths[glyph],Left=leftBearings[glyph],Right=rightBearings[glyph]};
                            metrics.Add(c,metric);
                        }
                        min=Math.Min(min,pen+metric.Left);max=Math.Max(max,pen+metric.Width-metric.Right);pen+=metric.Width;
                    }
                    if(!supported)break;label.Starts[i]=min;label.Ends[i]=max;
                }
                if(!supported)continue;label.Left=label.Starts[0];label.Width=label.Ends[words.Length-1]-label.Left;
                if(label.Width>0)list.Add(label);
            }
            return list.ToArray();
        }
        internal static OcrPage TryRead(BitmapSource source,VerifiedLineOcr.Ink ink,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();
            if(ink==null||source==null||!source.IsFrozen||ink.Width!=source.PixelWidth||ink.Height!=source.PixelHeight)return null;
            return TryRead(ink,null,cancel);
        }
        internal static OcrPage TryReadDesktop(DesktopLineEvidence.Ink desktop,CancellationToken cancel){
            if(desktop==null||!desktop.HasColor)return null;
            var samples=new byte[desktop.W*desktop.H];int bg=desktop.Pixels[0];
            for(int y=desktop.Bounds.Y;y<desktop.Bounds.Y+desktop.Bounds.Height;y++)for(int x=desktop.Bounds.X;x<desktop.Bounds.X+desktop.Bounds.Width;x++){
                int i=y*desktop.W+x,p=i*4;
                if(desktop.Pixels[p]!=bg||desktop.Pixels[p+1]!=bg||desktop.Pixels[p+2]!=bg)samples[i]=1;
            }
            var ink=new VerifiedLineOcr.Ink{Width=desktop.W,Height=desktop.H,Dark=desktop.Dark,Bounds=desktop.Bounds,Samples=samples};
            return TryRead(ink,desktop,cancel)??TrySingleLabel(desktop,ink,cancel);
        }
        static OcrPage TrySingleLabel(DesktopLineEvidence.Ink desktop,VerifiedLineOcr.Ink ink,CancellationToken cancel){
            var bounds=ink.Bounds;
            if(bounds.Width<22||bounds.Width>225||bounds.Height<10||bounds.Height>36)return null;
            var top=new int[bounds.Width];var bottom=new int[bounds.Width];int gap=0;
            for(int x=0;x<bounds.Width;x++){
                top[x]=bounds.Height;bottom[x]=-1;
                for(int y=0;y<bounds.Height;y++)if(ink.Samples[(y+bounds.Y)*ink.Width+x+bounds.X]!=0){
                    top[x]=Math.Min(top[x],y);bottom[x]=y+1;
                }
                gap=bottom[x]<0?gap+1:0;if(gap>Math.Max(3,bounds.Height/4))return null;
            }
            return TryRankedWord(desktop,bounds,top,bottom,false,cancel)??TryRankedWord(desktop,bounds,top,bottom,true,cancel);
        }
        static OcrPage TryRankedWord(DesktopLineEvidence.Ink desktop,Int32Rect bounds,int[] top,int[] bottom,bool contextual,CancellationToken cancel){
            // Rank typeface outlines cheaply. At most two words compete in each pass.
            // Approximate geometry alone must never return an OCR result.
            SingleLabel first=null,second=null;double best=5,runnerUp=5;
            foreach(var label in singleLabels.Value){
                if(contextual?label.Contexts.Count==0:!label.Standalone)continue;
                cancel.ThrowIfCancellationRequested();double score=SingleScore(label,bounds,top,bottom);
                if(score<best){runnerUp=best;second=first;best=score;first=label;}
                else if(score<runnerUp){runnerUp=score;second=label;}
            }
            OcrPage match=null;
            foreach(var label in new[]{first,second}){
                if(label==null)continue;
                int size=(int)Math.Round(100*bounds.Width/label.Bounds.Width);
                OcrPage page=null;
                if(contextual){
                    foreach(var context in label.Contexts){
                        page=DisplayGlyphEvidence.ProveWordInCaption(desktop,context.Caption,context.Words,context.Position,size,cancel);
                        if(page!=null)break;
                    }
                }else page=DisplayGlyphEvidence.Prove(desktop,label.Text,cancel,size,true);
                if(page==null)continue;
                if(match!=null)return null;
                match=page;
            }
            return match;
        }
        static double SingleScore(SingleLabel label,Int32Rect ink,int[] top,int[] bottom){
            double scale=ink.Width/label.Bounds.Width;
            if(scale<.10||scale>.48||Math.Abs(scale*label.Bounds.Height-ink.Height)>3)return double.PositiveInfinity;
            double error=0;int start=0;
            for(int i=0;i<label.Glyphs.Length;i++){
                Rect glyph=label.Glyphs[i];int end=ink.Width;
                if(i+1<label.Glyphs.Length)end=(int)Math.Round(((glyph.Right+label.Glyphs[i+1].Left)*.5-label.Bounds.Left)*scale);
                end=Math.Max(start,Math.Min(ink.Width,end));int high=ink.Height,low=-1;
                for(int x=start;x<end;x++){high=Math.Min(high,top[x]);low=Math.Max(low,bottom[x]);}
                if(low<0)return double.PositiveInfinity;
                error+=Math.Abs(high-(glyph.Top-label.Bounds.Top)*scale)+Math.Abs(low-(glyph.Bottom-label.Bounds.Top)*scale);
                start=end;
            }
            return error/label.Glyphs.Length;
        }
        static OcrPage TryRead(VerifiedLineOcr.Ink ink,DesktopLineEvidence.Ink desktop,CancellationToken cancel){
            int w=ink.Bounds.Width,h=ink.Bounds.Height,left=ink.Bounds.X,top=ink.Bounds.Y;
            if(w<25||w>460||h<10||h>36)return null;
            int[] lo=new int[w],hi=new int[w];int valleys=0,run=0;
            for(int x=0;x<w;x++){
                lo[x]=h;hi[x]=-1;
                for(int y=0;y<h;y++)if(ink.Samples[(y+top)*ink.Width+x+left]!=0){if(lo[x]==h)lo[x]=y;hi[x]=y;}
                if(hi[x]<0)run++;else{if(run>=2)valleys++;run=0;}
            }
            if(valleys==0)return null;
            // None of the command labels ends in punctuation. A tiny isolated
            // mark on the baseline can skip the font lookup. The full line,
            // including that mark, continues to ordinary recognition.
            int lastStart=w-1,lastTop=lo[w-1];
            while(lastStart>0&&hi[lastStart-1]>=0){lastStart--;lastTop=Math.Min(lastTop,lo[lastStart]);}
            if(lastStart>0&&(w-lastStart)*3<=h&&lastTop*3>=h*2)return null;
            var candidates=new List<Candidate>(2);
            foreach(var item in labels.Value){
                cancel.ThrowIfCancellationRequested();var c=TryFit(item,lo,hi,ink.Bounds);
                if(c==null)continue;if(candidates.Count==2)return null;candidates.Add(c);
            }
            Candidate match=null;
            if(desktop!=null){
                OcrPage desktopPage=null;
                foreach(var candidate in candidates){
                    var current=DisplayGlyphEvidence.Prove(desktop,candidate.Label.Text,cancel,candidate.Size);
                    if(current==null)continue;
                    if(desktopPage!=null)return null;
                    desktopPage=current;
                }
                return desktopPage;
            }
            foreach(var c in candidates)for(int k=0;k<3;k++){
                cancel.ThrowIfCancellationRequested();int size=c.Size+(k==0?0:k==1?-1:1);
                if(size<12||size>48||!ExactRaster(ink,c.Label.Text,size))continue;
                if(match!=null)return null;match=c;break;
            }
            if(match==null)return null;
            var page=new OcrPage();for(int i=0;i<match.Boxes.Length;i++)page.Words.Add(new WordBox{Text=match.Label.Words[i],Line=0,Box=match.Boxes[i]});
            OcrService.IndexText(page);return page;
        }
        static Candidate TryFit(Label label,int[] lo,int[] hi,Int32Rect bounds){
            // Font advances merely shortlist labels. They can never produce a
            // result without an exact match of the actual complete ink raster.
            double size=bounds.Width/label.Width;
            if(size<11||size>49||bounds.Height<size*.55||bounds.Height>size*1.06)return null;
            var boxes=new Rect[label.Words.Length];int start=0;
            for(int i=0;i<boxes.Length;i++){
                int end=bounds.Width;
                if(i+1<boxes.Length){
                    double position=((label.Ends[i]+label.Starts[i+1])*.5-label.Left)*size;
                    int middle=(int)Math.Round(position);int best=-1;
                    for(int dx=-2;dx<=2;dx++){int at=middle+dx;if(at<=start||at>=bounds.Width||hi[at]>=0)continue;
                        if(best<0||Math.Abs(at-position)<Math.Abs(best-position))best=at;}
                    if(best<0)return null;end=best;
                }
                int xMin=-1,xMax=-1,yMin=bounds.Height,yMax=-1;
                for(int x=start;x<end;x++)if(hi[x]>=0){if(xMin<0)xMin=x;xMax=x;yMin=Math.Min(yMin,lo[x]);yMax=Math.Max(yMax,hi[x]);}
                if(xMin<0||Math.Abs(xMin-(label.Starts[i]-label.Left)*size)>2.5||Math.Abs((xMax+1)-(label.Ends[i]-label.Left)*size)>2.5)return null;
                boxes[i]=new Rect(bounds.X+xMin,bounds.Y+yMin,xMax-xMin+1,yMax-yMin+1);start=end;
            }
            // A word with a descender visibly crosses the shared baseline in
            // Segoe UI. Two non-descending words cannot explain a deep step.
            // Only drop impossible candidates; the full raster check remains
            // mandatory and several plausible readings must still be rejected.
            for(int i=1;i<boxes.Length;i++)for(int j=0;j<i;j++)
                if(!label.Descends[i]&&!label.Descends[j]&&Math.Abs(boxes[i].Bottom-boxes[j].Bottom)>2)return null;
            return new Candidate{Label=label,Size=(int)Math.Round(size),Boxes=boxes};
        }
        static bool ExactRaster(VerifiedLineOcr.Ink input,string text,int size){
            var line=new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,face,size,input.Dark?Brushes.White:Brushes.Black,1);
            int w=Math.Max(32,(int)Math.Ceiling(line.WidthIncludingTrailingWhitespace)+12),h=(int)Math.Ceiling(line.Height)+12;
            if(w>768||h>112||w*h>70000)return false;
            var canvas=new DrawingVisual();using(var dc=canvas.RenderOpen()){
                dc.DrawRectangle(input.Dark?Brushes.Black:Brushes.White,null,new Rect(0,0,w,h));dc.DrawText(line,new Point(5,5));
            }
            var bmp=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bmp.Render(canvas);bmp.Freeze();
            return VerifiedLineOcr.EqualInk(input,VerifiedLineOcr.AnalyzeReference(bmp,input.Dark));
        }
    }
}
