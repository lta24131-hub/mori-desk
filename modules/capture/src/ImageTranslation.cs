using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        public readonly List<TranslationSourceLine> SourceLines=new List<TranslationSourceLine>();
    }
    public sealed class ImageTranslationResult {
        public BitmapSource Image;
        public string Transcript;
        public int UnplacedCount;
    }
    public static class ImageTranslation {
        public const string LayoutPolicy="complete-layout-device-context-v3";
        static TranslationRegion Line(IList<WordBox> words,Rect box){
            var text=Geometry.JoinWords(words);var line=new TranslationRegion{Text=text,Box=box};
            line.SourceLines.Add(new TranslationSourceLine{Text=text,Box=box});return line;
        }
        static bool Finite(double value){return !double.IsNaN(value)&&!double.IsInfinity(value);}
        static bool Valid(Rect box){return !box.IsEmpty&&Finite(box.X)&&Finite(box.Y)&&Finite(box.Width)&&Finite(box.Height)&&box.Width>0&&box.Height>0;}
        public static List<TranslationRegion> Regions(OcrPage page){
            if(page==null)throw new ArgumentNullException("page");
            var lines=new List<TranslationRegion>();
            foreach(var group in page.Words.GroupBy(w=>w.Line)){
                var words=new List<WordBox>();Rect box=Rect.Empty;
                // Keep the recognizer's logical word order, including RTL text.
                foreach(var word in group){
                    if(!Valid(word.Box)||string.IsNullOrWhiteSpace(word.Text))continue;
                    double gap=words.Count==0?0:Math.Max(word.Box.Left-box.Right,box.Left-word.Box.Right);
                    if(words.Count>0&&gap>Math.Max(24,box.Height*2.5)){lines.Add(Line(words,box));words.Clear();box=Rect.Empty;}
                    words.Add(word);box.Union(word.Box);
                }
                if(words.Count>0)lines.Add(Line(words,box));
            }
            var result=new List<TranslationRegion>();var active=new List<TranslationRegion>();
            foreach(var line in lines.OrderBy(l=>l.Box.Top).ThenBy(l=>l.Box.Left)){
                TranslationRegion previous=null;double nearest=double.MaxValue;string language=Translation.SourceLanguage(line.Text);
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
                        Translation.SourceLanguage(last.Text)!=language)continue;
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
            public Brush Background;
            public FormattedText Text;
            public Point Origin;
        }
        sealed class PaintRegion {
            public readonly List<PaintLine> Lines=new List<PaintLine>();
            public bool Accepted=true;
        }
        static bool Overlap(Rect a,Rect b){var intersection=Rect.Intersect(a,b);return !intersection.IsEmpty&&intersection.Width>.001&&intersection.Height>.001;}
        static bool Measure(string value,Rect rect,double fontSize,Brush ink,out FormattedText text,out Point origin){
            text=new FormattedText(value,CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),fontSize,ink,1);
            text.Trimming=TextTrimming.None;text.MaxTextWidth=Math.Max(1,rect.Width-2);
            var geometry=text.BuildGeometry(new Point());var bounds=geometry.Bounds;
            double overhang=bounds.IsEmpty?0:Math.Max(0,-bounds.Left);
            origin=new Point(rect.Left+1+overhang,rect.Top+1+Math.Max(0,(rect.Height-2-text.Height)/2));
            if(text.Height>rect.Height-2+.001||text.WidthIncludingTrailingWhitespace+overhang>rect.Width-2+.001)return false;
            if(!bounds.IsEmpty){bounds.Offset(origin.X,origin.Y);if(!rect.Contains(bounds))return false;}
            return true;
        }
        static PaintLine PlanLine(string value,Rect sourceBox,double sourceHeight,Rect image,byte[] pixels,int width,int height,Rect colorBox){
            if(string.IsNullOrWhiteSpace(value)||!Valid(sourceBox)||!image.Contains(sourceBox))return null;
            var rect=sourceBox;rect.Inflate(2,2);rect.Intersect(image);if(rect.Width<4||rect.Height<4)return null;
            bool simple;var color=Background(pixels,width,height,sourceBox,out simple);if(!simple)return null;
            var ink=Foreground(pixels,width,height,colorBox,color);
            // Do not force arbitrarily long translations into a six-pixel label.
            // Small source captions may retain a similarly small font; larger
            // text has a ten-image-pixel floor and always has a full transcript.
            double floor=Math.Max(8,Math.Min(10,sourceHeight*.85)),ceiling=Math.Max(floor,sourceHeight*1.05);
            FormattedText text;Point origin;
            if(!Measure(value,rect,ceiling,ink,out text,out origin)){
                if(!Measure(value,rect,floor,ink,out text,out origin))return null;
                double low=floor,high=ceiling;
                for(int n=0;n<8&&high-low>.1;n++){
                    double middle=(low+high)/2;FormattedText measured;Point at;
                    if(Measure(value,rect,middle,ink,out measured,out at)){low=middle;text=measured;origin=at;}else high=middle;
                }
            }
            return new PaintLine{Box=rect,Background=new SolidColorBrush(color),Text=text,Origin=origin};
        }
        static PaintRegion Plan(TranslationRegion region,string value,Rect image,byte[] pixels,int width,int height){
            var plan=new PaintRegion();if(region==null||!Valid(region.Box)||!image.Contains(region.Box)||string.IsNullOrWhiteSpace(value)){plan.Accepted=false;return plan;}
            var translatedLines=value.Replace("\r\n","\n").Replace('\r','\n').Split('\n');
            bool aligned=region.SourceLines.Count>1&&translatedLines.Length==region.SourceLines.Count;
            if(aligned){
                for(int n=0;n<translatedLines.Length;n++){
                    var source=region.SourceLines[n];var line=PlanLine(translatedLines[n],source.Box,source.Box.Height,image,pixels,width,height,source.Box);
                    if(line==null){plan.Accepted=false;return plan;}plan.Lines.Add(line);
                }
            }else{
                var colorBox=region.SourceLines.Count>0?region.SourceLines[0].Box:region.Box;
                var line=PlanLine(value,region.Box,region.Box.Height/Math.Max(1,region.Lines),image,pixels,width,height,colorBox);
                if(line==null)plan.Accepted=false;else plan.Lines.Add(line);
            }
            return plan;
        }
        public static BitmapSource Render(BitmapSource original,IList<TranslationRegion> regions,IList<string> translations){return RenderWithStatus(original,regions,translations).Image;}
        public static ImageTranslationResult RenderWithStatus(BitmapSource original,IList<TranslationRegion> regions,IList<string> translations){
            if(original==null)throw new ArgumentNullException("original");if(regions==null)throw new ArgumentNullException("regions");if(translations==null)throw new ArgumentNullException("translations");
            if(regions.Count!=translations.Count)throw new InvalidOperationException("译文与文字区域数量不一致。");
            int width=original.PixelWidth,height=original.PixelHeight;var image=new Rect(0,0,width,height);
            var source=new FormatConvertedBitmap(original,PixelFormats.Bgra32,null,0);var pixels=new byte[checked(width*height*4)];source.CopyPixels(pixels,width*4,0);
            var plans=new List<PaintRegion>();for(int i=0;i<regions.Count;i++)plans.Add(Plan(regions[i],translations[i],image,pixels,width,height));
            // Reject both neighbours rather than painting over a region whose
            // original pixels we promise to keep. Include unplaced source boxes.
            var conflicts=new bool[plans.Count];
            for(int i=0;i<plans.Count;i++)for(int j=i+1;j<plans.Count;j++){
                var a=regions[i];var b=regions[j];if(a==null||b==null||!Valid(a.Box)||!Valid(b.Box))continue;
                bool overlap=plans[i].Lines.Any(l=>Overlap(l.Box,b.Box))||plans[j].Lines.Any(l=>Overlap(l.Box,a.Box))||
                    plans[i].Lines.Any(aLine=>plans[j].Lines.Any(bLine=>Overlap(aLine.Box,bLine.Box)));
                if(overlap){conflicts[i]=true;conflicts[j]=true;}
            }
            int unplaced=0;for(int i=0;i<plans.Count;i++){if(conflicts[i])plans[i].Accepted=false;if(!plans[i].Accepted)unplaced++;}
            // If nothing fits, return the exact original rather than recomposing
            // it (which can alter transparent pixels during premultiplication).
            BitmapSource bitmap=original;
            if(plans.Any(p=>p.Accepted)){
                var visual=new DrawingVisual();using(var d=visual.RenderOpen()){
                    d.DrawImage(original,image);
                    foreach(var plan in plans.Where(p=>p.Accepted))foreach(var line in plan.Lines){d.PushClip(new RectangleGeometry(line.Box));d.DrawRectangle(line.Background,null,line.Box);d.Pop();}
                    foreach(var plan in plans.Where(p=>p.Accepted))foreach(var line in plan.Lines){d.PushClip(new RectangleGeometry(line.Box));d.DrawText(line.Text,line.Origin);d.Pop();}
                }
                var rendered=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);rendered.Render(visual);rendered.Freeze();bitmap=rendered;
            }
            return new ImageTranslationResult{Image=bitmap,Transcript=string.Join("\n\n",translations.Select(t=>t??string.Empty)),UnplacedCount=unplaced};
        }
    }
}
