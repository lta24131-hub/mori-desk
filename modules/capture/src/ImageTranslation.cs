using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    public sealed class TranslationSourceLine {
        public string Text;
        public Rect Box;
    }
    public sealed class TranslationRegion {
        public string Text;
        public Rect Box;
        public int Lines=1;
        public List<TranslationSourceLine> SourceLines=new List<TranslationSourceLine>();
    }
    public sealed class ImageTranslationResult {
        public BitmapSource Image;
        public string Transcript;
        public int UnplacedCount;
    }
    public static class ImageTranslation {
        public const string LayoutPolicy="readable-overlays-v9-safe-fallback";
        // One full-size render at a time bounds scratch bitmap memory across
        // capture, recognition and pins. Waiting requests remain cancellable.
        static readonly SemaphoreSlim renderSlot=new SemaphoreSlim(1,1);
        static TranslationRegion Line(IList<WordBox> words,Rect box){
            var text=Geometry.JoinWords(words);var line=new TranslationRegion{Text=text,Box=box};
            line.SourceLines.Add(new TranslationSourceLine{Text=text,Box=box});return line;
        }
        static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
        static bool Valid(Rect box){return !box.IsEmpty&&Finite(box.X)&&Finite(box.Y)&&Finite(box.Width)&&Finite(box.Height)&&box.Width>0&&box.Height>0;}
        static string RegionLanguage(string text){
            int han=text.Count(c=>c>='\u3400'&&c<='\u9fff'),latin=text.Count(c=>(c>='a'&&c<='z')||(c>='A'&&c<='Z'));
            return han>=2&&han>=latin?"zh":Translation.SourceLanguage(text);
        }
        static bool DetachedSymbol(WordBox word,IList<WordBox> line){
            if(word.Text.Any(char.IsLetterOrDigit))return false;
            // A detached icon can share an OCR line with its title, but its
            // taller bounds must not erase the adjacent description. Ordinary
            // inline punctuation and operators stay in the sentence.
            var neighbours=line.Where(w=>w!=word&&Valid(w.Box)&&w.Text.Any(char.IsLetterOrDigit)).ToList();
            if(neighbours.Count==0)return true;
            return neighbours.All(w=>Math.Max(w.Box.Left-word.Box.Right,word.Box.Left-w.Box.Right)>Math.Max(12,Math.Min(word.Box.Height,w.Box.Height)));
        }
        public static List<TranslationRegion> Regions(OcrPage page){
            if(page==null)throw new ArgumentNullException("page");
            if(page.TranslationRegions.Count>0)return page.TranslationRegions.Select(r=>new TranslationRegion{Text=r.Text,Box=r.Box,Lines=r.Lines,SourceLines=r.SourceLines.Select(l=>new TranslationSourceLine{Text=l.Text,Box=l.Box}).ToList()}).ToList();
            var lines=new List<TranslationRegion>();
            foreach(var group in page.Words.GroupBy(w=>w.Line)){
                var sourceWords=group.Where(w=>Valid(w.Box)&&!string.IsNullOrWhiteSpace(w.Text)).ToList();
                var words=new List<WordBox>();Rect box=Rect.Empty;
                // Keep the recognizer's logical word order, including RTL text.
                foreach(var word in sourceWords){
                    if(!Valid(word.Box)||string.IsNullOrWhiteSpace(word.Text))continue;
                    if(DetachedSymbol(word,sourceWords))continue;
                    double gap=words.Count==0?0:Math.Max(word.Box.Left-box.Right,box.Left-word.Box.Right);
                    if(words.Count>0&&gap>Math.Max(24,box.Height*2.5)){lines.Add(Line(words,box));words.Clear();box=Rect.Empty;}
                    words.Add(word);box.Union(word.Box);
                }
                if(words.Count>0)lines.Add(Line(words,box));
            }
            var result=new List<TranslationRegion>();var active=new List<TranslationRegion>();
            foreach(var line in lines.OrderBy(l=>l.Box.Top).ThenBy(l=>l.Box.Left)){
                TranslationRegion previous=null;double nearest=double.MaxValue;string language=RegionLanguage(line.Text);
                // Only recent line ends are candidates. Another column interleaved
                // by top coordinate must not split an otherwise adjacent paragraph.
                for(int i=active.Count-1;i>=0;i--){
                    var candidate=active[i];var last=candidate.SourceLines[candidate.SourceLines.Count-1];double height=last.Box.Height,gap=line.Box.Top-last.Box.Bottom;
                    if(gap>=height*.65){active.RemoveAt(i);continue;}
                    // Ink boxes may have very different ascenders/descenders
                    // even at the same font size; tolerate a little overlap,
                    // but never join same-baseline labels or title/body extremes.
                    double smaller=Math.Min(height,line.Box.Height),larger=Math.Max(height,line.Box.Height);
                    if(gap< -smaller*.35||line.Box.Top-last.Box.Top<height*.45||
                        Math.Abs(last.Box.Left-line.Box.Left)>=smaller*.7||larger>=smaller*1.9||
                        RegionLanguage(last.Text)!=language)continue;
                    double distance=Math.Abs(gap)+Math.Abs(last.Box.Left-line.Box.Left);
                    if(distance<nearest){nearest=distance;previous=candidate;}
                }
                if(previous!=null){previous.Box.Union(line.Box);previous.Text+="\n"+line.Text;previous.Lines++;previous.SourceLines.AddRange(line.SourceLines);}
                else {result.Add(line);active.Add(line);}
            }
            return result;
        }
        static Color Background(byte[] pixels,int width,int height,Rect box,out bool simple){
            var counts=new Dictionary<int,int>();var colors=new Dictionary<int,Color>();
            Action<int,int> sample=(x,y)=>{if(x<0||y<0||x>=width||y>=height)return;int i=(y*width+x)*4;var c=Color.FromRgb(pixels[i+2],pixels[i+1],pixels[i]);int key=(c.R/16)*256+(c.G/16)*16+c.B/16;counts[key]=counts.ContainsKey(key)?counts[key]+1:1;colors[key]=c;};
            int left=(int)box.Left,right=(int)Math.Ceiling(box.Right),top=(int)box.Top,bottom=(int)Math.Ceiling(box.Bottom);
            for(int x=left-2;x<=right+2;x+=2){sample(x,top-2);sample(x,bottom+2);}
            for(int y=top-2;y<=bottom+2;y+=2){sample(left-2,y);sample(right+2,y);}
            if(counts.Count==0){simple=false;return Colors.White;}
            var background=colors[counts.OrderByDescending(k=>k.Value).First().Key];int samples=0,similar=0;
            foreach(var pair in counts){samples+=pair.Value;var c=colors[pair.Key];if(Math.Abs(c.R-background.R)+Math.Abs(c.G-background.G)+Math.Abs(c.B-background.B)<72)similar+=pair.Value;}
            // Flat painting cannot reconstruct a photograph, illustration or
            // product detail behind the letters. Leave visibly mixed boundary
            // regions untouched instead of pretending the dominant color is a
            // faithful background. Complete text remains available separately.
            simple=similar>=samples*.85;return background;
        }
        static Brush Foreground(byte[] pixels,int width,int height,Rect box,Color background){
            var counts=new Dictionary<int,int>();var colors=new Dictionary<int,Color>();
            int left=(int)Math.Max(0,box.Left),right=(int)Math.Min(width,Math.Ceiling(box.Right)),top=(int)Math.Max(0,box.Top),bottom=(int)Math.Min(height,Math.Ceiling(box.Bottom));
            int step=Math.Max(1,(int)Math.Sqrt(Math.Max(1,(right-left)*(bottom-top))/60000.0));
            for(int y=top;y<bottom;y+=step)for(int x=left;x<right;x+=step){int i=(y*width+x)*4;var c=Color.FromRgb(pixels[i+2],pixels[i+1],pixels[i]);if(Math.Abs(c.R-background.R)+Math.Abs(c.G-background.G)+Math.Abs(c.B-background.B)<125)continue;int key=(c.R/16)*256+(c.G/16)*16+c.B/16;counts[key]=counts.ContainsKey(key)?counts[key]+1:1;colors[key]=c;}
            if(counts.Count>0)return new SolidColorBrush(colors[counts.OrderByDescending(k=>k.Value).First().Key]);
            return .2126*background.R+.7152*background.G+.0722*background.B>145?Ui.Ink:Brushes.White;
        }
        sealed class PaintLine {
            public Rect Box;
            public Rect EraseBox;
            public Brush Background;
            public FormattedText Text;
            public Point Origin;
            public double FontSize;
        }
        sealed class PaintRegion {
            public readonly List<PaintLine> Lines=new List<PaintLine>();
            public bool Accepted=true;
        }
        static bool Overlap(Rect a,Rect b){var intersection=Rect.Intersect(a,b);return !intersection.IsEmpty&&intersection.Width>.001&&intersection.Height>.001;}
        static bool Measure(string value,Rect rect,double fontSize,Brush ink,out FormattedText text,out Point origin){
            text=new FormattedText(value,CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),fontSize,ink,1);
            text.Trimming=TextTrimming.None;text.MaxTextWidth=Math.Max(1,rect.Width-2);
            // Reject an oversized candidate before extracting every glyph's
            // outline. Dense screenshots try several font sizes per region.
            origin=new Point();
            if(text.Height>rect.Height-2+.001||text.WidthIncludingTrailingWhitespace>rect.Width-2+.001)return false;
            var geometry=text.BuildGeometry(new Point());var bounds=geometry.Bounds;
            double overhang=bounds.IsEmpty?0:Math.Max(0,-bounds.Left);
            origin=new Point(rect.Left+1+overhang,rect.Top+1+Math.Max(0,(rect.Height-2-text.Height)/2));
            if(text.Height>rect.Height-2+.001||text.WidthIncludingTrailingWhitespace+overhang>rect.Width-2+.001)return false;
            if(!bounds.IsEmpty){bounds.Offset(origin.X,origin.Y);if(!rect.Contains(bounds))return false;}
            return true;
        }
        // Layout follows STranslate's measured overlay approach (MIT): fit the
        // complete text, use compact line height, then expand into nearby space.
        // Nook additionally partitions adjacent OCR boxes so padding/ascenders
        // cannot cause two otherwise-readable labels to reject one another.
        static Rect Available(int index,IList<TranslationRegion> regions,Rect image,bool expand,bool plainBackground){
            var source=regions[index].Box;var rect=source;
            // OCR can end a few pixels inside the final glyph. Scale the modest
            // horizontal ink allowance with row height, then partition against
            // adjacent regions below so their content is never painted over.
            double rowHeight=regions[index].SourceLines.Count>0?regions[index].SourceLines.Min(l=>l.Box.Height):source.Height/Math.Max(1,regions[index].Lines);
            rect.Inflate(expand?Math.Min(160,source.Width*.4):plainBackground?Math.Max(2,Math.Min(8,rowHeight*.2)):2,expand?Math.Min(40,source.Height*.6):2);rect.Intersect(image);
            if(!Valid(rect))return Rect.Empty;
            double left=rect.Left,right=rect.Right,top=rect.Top,bottom=rect.Bottom;
            double cx=source.Left+source.Width/2,cy=source.Top+source.Height/2;
            for(int n=0;n<regions.Count;n++){
                if(n==index||regions[n]==null||!Valid(regions[n].Box))continue;var other=regions[n].Box;
                var protectedOther=other;protectedOther.Inflate(2,2);
                if(!Overlap(rect,protectedOther))continue;
                double ox=other.Left+other.Width/2,oy=other.Top+other.Height/2;
                double horizontal=Math.Min(source.Right,other.Right)-Math.Max(source.Left,other.Left);
                if(horizontal>Math.Min(source.Width,other.Width)*.15&&Math.Abs(cy-oy)>Math.Min(source.Height,other.Height)*.25){
                    // OCR rows can overlap slightly. Partition their touching
                    // edges, not their centres: unequal paragraph heights would
                    // otherwise leave a whole source line unerased.
                    if(cy<oy)bottom=Math.Min(bottom,(source.Bottom+other.Top)/2);
                    else top=Math.Max(top,(other.Bottom+source.Top)/2);
                }else if(Math.Abs(cx-ox)>Math.Min(source.Width,other.Width)*.25){
                    if(cx<ox)right=Math.Min(right,(source.Right+other.Left)/2);
                    else left=Math.Max(left,(other.Right+source.Left)/2);
                }else return Rect.Empty; // truly duplicated/ambiguous geometry
            }
            return right>left&&bottom>top?new Rect(left,top,right-left,bottom-top):Rect.Empty;
        }
        static PaintLine PlanLine(string value,Rect rect,double sourceHeight,byte[] pixels,int width,int height,Rect colorBox){
            if(string.IsNullOrWhiteSpace(value)||!Valid(rect)||rect.Width<5||rect.Height<5)return null;
            bool simple;var color=Background(pixels,width,height,colorBox,out simple);
            // On a photograph use a readable caption surface; never silently
            // omit the translation merely because the source has mixed colors.
            if(!simple)color=.2126*color.R+.7152*color.G+.0722*color.B>145?Colors.White:Color.FromRgb(28,28,30);
            var ink=simple?Foreground(pixels,width,height,colorBox,color):new SolidColorBrush(color.R>145?Colors.Black:Colors.White);
            double floor=Math.Max(7,Math.Min(10,sourceHeight*.65)),ceiling=Math.Max(floor,Math.Min(128,sourceHeight*.95));
            FormattedText text;Point origin;double chosen=ceiling;
            if(!Measure(value,rect,ceiling,ink,out text,out origin)){
                if(!Measure(value,rect,floor,ink,out text,out origin))return null;
                double low=floor,high=ceiling;chosen=floor;
                for(int n=0;n<8&&high-low>.1;n++){
                    double middle=(low+high)/2;FormattedText measured;Point at;
                    if(Measure(value,rect,middle,ink,out measured,out at)){low=middle;chosen=middle;text=measured;origin=at;}else high=middle;
                }
            }
            return new PaintLine{Box=rect,EraseBox=rect,Background=new SolidColorBrush(color),Text=text,Origin=origin,FontSize=chosen};
        }
        static Rect ReadingSpace(int index,IList<TranslationRegion> regions,Rect image,Rect rect,Rect inkRect,Color background,byte[] pixels,int width,int height){
            if(!Valid(rect)||!Valid(inkRect))return rect;
            // Only grow rightwards on verified blank pixels. Keep the source's
            // left edge and row height; a missed icon/photo must remain intact.
            var available=Available(index,regions,image,true,false);
            if(!Valid(available))return rect;
            int start=(int)Math.Ceiling(inkRect.Right),limit=(int)Math.Floor(available.Right);
            int top=Math.Max(0,(int)Math.Floor(rect.Top)),bottom=Math.Min(height,(int)Math.Ceiling(rect.Bottom));
            for(int n=0;n<regions.Count;n++){
                if(n==index||regions[n]==null||!Valid(regions[n].Box))continue;
                var other=regions[n].Box;other.Inflate(2,2);
                if(other.Bottom>rect.Top&&other.Top<rect.Bottom&&other.Right>rect.Right)limit=Math.Min(limit,(int)Math.Floor(other.Left));
            }
            int right=start;
            for(int x=start;x<limit;x++){
                bool blank=true;
                for(int y=top;y<bottom;y++){
                    int p=(y*width+x)*4;
                    if(pixels[p+3]<250||Math.Abs(pixels[p]-background.B)+Math.Abs(pixels[p+1]-background.G)+Math.Abs(pixels[p+2]-background.R)>24){blank=false;break;}
                }
                if(!blank)break;right=x+1;
            }
            // Leave a small gutter before any unrecognized content.
            right-=2;
            return right>rect.Right+8?new Rect(rect.Left,rect.Top,right-rect.Left,rect.Height):rect;
        }
        static Rect FallbackSpace(int index,IList<TranslationRegion> regions,Rect image,Rect rect,Rect inkRect,Color background,byte[] pixels,int width,int height,bool widen){
            if(!Valid(rect)||!Valid(inkRect))return rect;
            var expanded=widen?ReadingSpace(index,regions,image,rect,inkRect,background,pixels,width,height):rect;
            var available=Available(index,regions,image,true,false);
            if(!Valid(available))return expanded;
            int left=Math.Max(0,(int)Math.Floor(Math.Min(expanded.Left,inkRect.Left)));
            int right=Math.Min(width,(int)Math.Ceiling(Math.Max(expanded.Right,inkRect.Right)));
            int start=(int)Math.Ceiling(inkRect.Bottom),limit=(int)Math.Floor(available.Bottom);
            for(int n=0;n<regions.Count;n++){
                if(n==index||regions[n]==null||!Valid(regions[n].Box))continue;
                var other=regions[n].Box;other.Inflate(2,2);
                if(other.Right>left&&other.Left<right&&other.Bottom>rect.Bottom)limit=Math.Min(limit,(int)Math.Floor(other.Top));
            }
            int bottom=start;
            for(int y=start;y<limit;y++){
                bool blank=true;
                for(int x=left;x<right;x++){
                    int p=(y*width+x)*4;
                    if(pixels[p+3]<250||Math.Abs(pixels[p]-background.B)+Math.Abs(pixels[p+1]-background.G)+Math.Abs(pixels[p+2]-background.R)>24){blank=false;break;}
                }
                if(!blank)break;bottom=y+1;
            }
            bottom-=2;
            return bottom>expanded.Bottom?new Rect(expanded.Left,expanded.Top,expanded.Width,bottom-expanded.Top):expanded;
        }
        static bool CanPlace(TranslationRegion region,string value,Rect image){
            return region!=null&&Valid(region.Box)&&image.IntersectsWith(region.Box)&&region.Box.Width>=4&&region.Box.Height>=4&&!string.IsNullOrWhiteSpace(value);
        }
        static PaintRegion Plan(int index,IList<TranslationRegion> regions,string value,Rect image,byte[] pixels,int width,int height){
            var region=regions[index];var plan=new PaintRegion();
            if(!CanPlace(region,value,image)){plan.Accepted=false;return plan;}
            if(string.Equals(region.Text,value,StringComparison.Ordinal))return plan;
            var lineHeights=region.SourceLines.Select(l=>l.Box.Height).OrderBy(h=>h).ToArray();
            double lineHeight=lineHeights.Length>0?lineHeights[lineHeights.Length/2]:region.Box.Height/Math.Max(1,region.Lines);
            var sourceBox=region.SourceLines.Count>0?region.SourceLines[0].Box:region.Box;
            bool plainBackground;Background(pixels,width,height,sourceBox,out plainBackground);
            // Cleanup padding must not change line wrapping, font size or origin.
            // Keep the text layout stable; extend only the background erase area.
            var rect=Available(index,regions,image,false,false);
            var inkRect=Available(index,regions,image,false,plainBackground);
            var explicitLines=value.Replace("\r\n","\n").Replace('\r','\n').Split('\n');
            bool physicalBreaks=region.SourceLines.Count>1&&explicitLines.Length==region.SourceLines.Count;
            // OCR line breaks describe wrapping, not separate paragraphs. Fit
            // ordinary text as one block so every line shares the same font size
            // and its complete background is cleared. Preserve per-line color
            // only when the source really has distinctly colored lines.
            var colors=physicalBreaks?region.SourceLines.Select(l=>{
                bool simple;var background=Background(pixels,width,height,l.Box,out simple);
                return ((SolidColorBrush)Foreground(pixels,width,height,l.Box,background)).Color;
            }).ToArray():new Color[0];
            bool distinctColors=colors.Length>1&&colors.Any(c=>Math.Abs(c.R-colors[0].R)+Math.Abs(c.G-colors[0].G)+Math.Abs(c.B-colors[0].B)>125);
            if(physicalBreaks&&distinctColors&&Valid(rect)){
                for(int n=0;n<explicitLines.Length;n++){
                    var source=region.SourceLines[n];var slot=source.Box;slot.Inflate(2,2);slot.Intersect(rect);
                    foreach(var other in region.SourceLines.Where(l=>l!=source)){
                        if(!Valid(slot)||!Overlap(slot,other.Box))continue;
                        double a=source.Box.Top+source.Box.Height/2,b=other.Box.Top+other.Box.Height/2;
                        if(a<b){double bottom=Math.Min(slot.Bottom,(source.Box.Bottom+other.Box.Top)/2);slot=bottom>slot.Top?new Rect(slot.Left,slot.Top,slot.Width,bottom-slot.Top):Rect.Empty;}
                        else if(a>b){double top=Math.Max(slot.Top,(other.Box.Bottom+source.Box.Top)/2);slot=top<slot.Bottom?new Rect(slot.Left,top,slot.Width,slot.Bottom-top):Rect.Empty;}
                    }
                    var fitted=PlanLine(explicitLines[n],slot,source.Box.Height,pixels,width,height,source.Box);
                    if(fitted==null){plan.Lines.Clear();break;}
                    if(plainBackground&&Valid(inkRect)){var erase=source.Box;erase.Inflate(Math.Max(2,Math.Min(8,source.Box.Height*.2)),2);erase.Intersect(inkRect);if(Valid(erase))fitted.EraseBox=new Rect(erase.Left,slot.Top,erase.Width,slot.Height);}
                    plan.Lines.Add(fitted);
                }
                if(plan.Lines.Count==explicitLines.Length)return plan;
            }
            if(physicalBreaks)value=string.Join(" ",explicitLines.Select(l=>l.Trim()));
            var line=PlanLine(value,rect,lineHeight,pixels,width,height,sourceBox);
            if(plainBackground&&explicitLines.Length==1&&region.Lines==1&&region.SourceLines.Count<=1&&line!=null&&line.FontSize<lineHeight*.68){
                var reading=ReadingSpace(index,regions,image,rect,inkRect,((SolidColorBrush)line.Background).Color,pixels,width,height);
                if(reading.Width>rect.Width){
                    var larger=PlanLine(value,reading,lineHeight,pixels,width,height,sourceBox);
                    if(larger!=null&&larger.FontSize>=line.FontSize+1.5)line=larger;
                }
            }
            if(line==null&&plainBackground){
                bool simple;var background=Background(pixels,width,height,sourceBox,out simple);
                var wide=FallbackSpace(index,regions,image,rect,inkRect,background,pixels,width,height,true);
                line=PlanLine(value,wide,lineHeight,pixels,width,height,sourceBox);
                // A picture beside the next row may block the wider candidate
                // while leaving room directly below the original text column.
                if(line==null&&wide.Width>rect.Width){
                    var narrow=FallbackSpace(index,regions,image,rect,inkRect,background,pixels,width,height,false);
                    line=PlanLine(value,narrow,lineHeight,pixels,width,height,sourceBox);
                }
            }
            if(line==null)plan.Accepted=false;else {if(Valid(inkRect))line.EraseBox=Rect.Union(line.Box,inkRect);plan.Lines.Add(line);}
            return plan;
        }
        public static BitmapSource Render(BitmapSource original,IList<TranslationRegion> regions,IList<string> translations){return RenderWithStatus(original,regions,translations).Image;}
        public static ImageTranslationResult RenderWithStatus(BitmapSource original,IList<TranslationRegion> regions,IList<string> translations){
            return RenderCore(original,regions,translations,CancellationToken.None);
        }
        public static async Task<ImageTranslationResult> RenderAsync(BitmapSource original,IList<TranslationRegion> regions,IList<string> translations,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();ValidateRender(original,regions,translations);
            // Snapshot on the caller's thread. Editing OCR or closing a viewer
            // while queued must not mutate a render already in progress.
            var frozen=original.IsFrozen?original:(BitmapSource)original.GetCurrentValueAsFrozen();
            var snapshot=regions.Select(r=>r==null?null:new TranslationRegion{Text=r.Text,Box=r.Box,Lines=r.Lines,SourceLines=r.SourceLines.Select(l=>new TranslationSourceLine{Text=l.Text,Box=l.Box}).ToList()}).ToArray();
            var values=translations.ToArray();
            await renderSlot.WaitAsync(cancel).ConfigureAwait(false);
            try{return await Task.Run(()=>RenderCore(frozen,snapshot,values,cancel),cancel).ConfigureAwait(false);}
            finally{renderSlot.Release();}
        }
        static void ValidateRender(BitmapSource original,IList<TranslationRegion> regions,IList<string> translations){
            if(original==null)throw new ArgumentNullException("original");if(regions==null)throw new ArgumentNullException("regions");if(translations==null)throw new ArgumentNullException("translations");
            if(regions.Count!=translations.Count)throw new InvalidOperationException("译文与文字区域数量不一致。");
        }
        static ImageTranslationResult RenderCore(BitmapSource original,IList<TranslationRegion> regions,IList<string> translations,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();ValidateRender(original,regions,translations);
            int width=original.PixelWidth,height=original.PixelHeight;var image=new Rect(0,0,width,height);
            // No-op translations (Chinese labels, names, empty OCR) need no
            // scratch copy of a potentially 8K bitmap. Retain invalid-region
            // reporting exactly as Plan would, before returning the original.
            bool changes=false;int invalid=0;
            for(int i=0;i<regions.Count;i++){
                cancel.ThrowIfCancellationRequested();
                if(!CanPlace(regions[i],translations[i],image))invalid++;
                else if(!string.Equals(regions[i].Text,translations[i],StringComparison.Ordinal))changes=true;
            }
            if(!changes)return new ImageTranslationResult{Image=original,Transcript=string.Join("\n\n",translations.Select(t=>t??string.Empty)),UnplacedCount=invalid};
            var source=new FormatConvertedBitmap(original,PixelFormats.Bgra32,null,0);var pixels=new byte[checked(width*height*4)];source.CopyPixels(pixels,width*4,0);
            var plans=new List<PaintRegion>();for(int i=0;i<regions.Count;i++){cancel.ThrowIfCancellationRequested();plans.Add(Plan(i,regions,translations[i],image,pixels,width,height));}
            // Allocations are separated before drawing. If exceptional expanded
            // regions still overlap, keep one readable result and show the other
            // in the complete transcript, rather than blanking both neighbours.
            for(int i=0;i<plans.Count;i++){
                cancel.ThrowIfCancellationRequested();if(plans[i].Accepted)for(int j=i+1;j<plans.Count;j++)
                    if(plans[j].Accepted&&plans[i].Lines.Any(a=>plans[j].Lines.Any(b=>Overlap(a.EraseBox,b.EraseBox))))plans[j].Accepted=false;
            }
            int unplaced=plans.Count(p=>!p.Accepted);
            // If nothing fits, return the exact original rather than recomposing
            // it (which can alter transparent pixels during premultiplication).
            BitmapSource bitmap=original;
            if(plans.Any(p=>p.Accepted&&p.Lines.Count>0)){
                var visual=new DrawingVisual();using(var d=visual.RenderOpen()){
                    d.DrawImage(original,image);
                    foreach(var plan in plans.Where(p=>p.Accepted))foreach(var line in plan.Lines){cancel.ThrowIfCancellationRequested();d.PushClip(new RectangleGeometry(line.EraseBox));d.DrawRectangle(line.Background,null,line.EraseBox);d.Pop();}
                    foreach(var plan in plans.Where(p=>p.Accepted))foreach(var line in plan.Lines){cancel.ThrowIfCancellationRequested();d.PushClip(new RectangleGeometry(line.Box));d.DrawText(line.Text,line.Origin);d.Pop();}
                }
                cancel.ThrowIfCancellationRequested();
                var rendered=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);rendered.Render(visual);rendered.Freeze();bitmap=rendered;
            }
            cancel.ThrowIfCancellationRequested();
            return new ImageTranslationResult{Image=bitmap,Transcript=string.Join("\n\n",translations.Select(t=>t??string.Empty)),UnplacedCount=unplaced};
        }
    }
}
