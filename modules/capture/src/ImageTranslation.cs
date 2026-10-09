using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    public sealed class TranslationRegion {
        public string Text;
        public Rect Box;
        public int Lines=1;
    }
    public static class ImageTranslation {
        public static List<TranslationRegion> Regions(OcrPage page){
            var lines=new List<TranslationRegion>();
            foreach(var group in page.Words.GroupBy(w=>w.Line)){
                var words=new List<WordBox>();Rect box=Rect.Empty;
                foreach(var word in group){
                    if(words.Count>0&&word.Box.Left-box.Right>Math.Max(24,box.Height*2.5)){lines.Add(new TranslationRegion{Text=Geometry.JoinWords(words),Box=box});words.Clear();box=Rect.Empty;}
                    words.Add(word);box.Union(word.Box);
                }
                if(words.Count>0)lines.Add(new TranslationRegion{Text=Geometry.JoinWords(words),Box=box});
            }
            var result=new List<TranslationRegion>();
            foreach(var line in lines.OrderBy(l=>l.Box.Top).ThenBy(l=>l.Box.Left)){
                var previous=result.LastOrDefault();double gap=previous==null?double.MaxValue:line.Box.Top-previous.Box.Bottom;
                double height=previous==null?line.Box.Height:previous.Box.Height/previous.Lines;
                if(previous!=null&&Translation.SourceLanguage(previous.Text)==Translation.SourceLanguage(line.Text)&&gap>=0&&gap<height*.65&&Math.Abs(previous.Box.Left-line.Box.Left)<height*.7&&Math.Abs(height-line.Box.Height)<height*.35){previous.Box.Union(line.Box);previous.Text+="\n"+line.Text;previous.Lines++;}
                else result.Add(line);
            }
            return result;
        }
        static Color Background(byte[] pixels,int width,int height,Rect box){
            var counts=new Dictionary<int,int>();var colors=new Dictionary<int,Color>();
            Action<int,int> sample=(x,y)=>{if(x<0||y<0||x>=width||y>=height)return;int i=(y*width+x)*4;var c=Color.FromRgb(pixels[i+2],pixels[i+1],pixels[i]);int key=(c.R/16)*256+(c.G/16)*16+c.B/16;counts[key]=counts.ContainsKey(key)?counts[key]+1:1;colors[key]=c;};
            int left=(int)box.Left,right=(int)Math.Ceiling(box.Right),top=(int)box.Top,bottom=(int)Math.Ceiling(box.Bottom);
            for(int x=left-2;x<=right+2;x+=2){sample(x,top-2);sample(x,bottom+2);}
            for(int y=top-2;y<=bottom+2;y+=2){sample(left-2,y);sample(right+2,y);}
            return counts.Count==0?Colors.White:colors[counts.OrderByDescending(k=>k.Value).First().Key];
        }
        static Brush Foreground(byte[] pixels,int width,int height,Rect box,Color background){
            var counts=new Dictionary<int,int>();var colors=new Dictionary<int,Color>();
            int left=(int)Math.Max(0,box.Left),right=(int)Math.Min(width,Math.Ceiling(box.Right)),top=(int)Math.Max(0,box.Top),bottom=(int)Math.Min(height,Math.Ceiling(box.Bottom));
            int step=Math.Max(1,(int)Math.Sqrt(Math.Max(1,(right-left)*(bottom-top))/60000.0));
            for(int y=top;y<bottom;y+=step)for(int x=left;x<right;x+=step){int i=(y*width+x)*4;var c=Color.FromRgb(pixels[i+2],pixels[i+1],pixels[i]);if(Math.Abs(c.R-background.R)+Math.Abs(c.G-background.G)+Math.Abs(c.B-background.B)<125)continue;int key=(c.R/16)*256+(c.G/16)*16+c.B/16;counts[key]=counts.ContainsKey(key)?counts[key]+1:1;colors[key]=c;}
            if(counts.Count>0)return new SolidColorBrush(colors[counts.OrderByDescending(k=>k.Value).First().Key]);
            return .2126*background.R+.7152*background.G+.0722*background.B>145?Ui.Ink:Brushes.White;
        }
        public static BitmapSource Render(BitmapSource original,IList<TranslationRegion> regions,IList<string> translations){
            if(regions.Count!=translations.Count)throw new InvalidOperationException("译文与文字区域数量不一致。");
            int width=original.PixelWidth,height=original.PixelHeight;
            var source=new FormatConvertedBitmap(original,PixelFormats.Bgra32,null,0);var pixels=new byte[checked(width*height*4)];source.CopyPixels(pixels,width*4,0);
            var visual=new DrawingVisual();using(var d=visual.RenderOpen()){
                d.DrawImage(original,new Rect(0,0,width,height));
                for(int i=0;i<regions.Count;i++){
                    var region=regions[i];var rect=region.Box;rect.Inflate(2,2);rect.Intersect(new Rect(0,0,width,height));if(rect.IsEmpty||rect.Width<2||rect.Height<2)continue;
                    var color=Background(pixels,width,height,region.Box);var background=new SolidColorBrush(color);var ink=Foreground(pixels,width,height,region.Box,color);
                    double fontSize=Math.Max(6,region.Box.Height/Math.Max(1,region.Lines)*1.05);FormattedText text;
                    do{text=new FormattedText(translations[i],CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface("Microsoft YaHei UI"),fontSize,ink,1);text.MaxTextWidth=Math.Max(1,rect.Width-2);if(text.Height<=rect.Height||fontSize<=6)break;fontSize=Math.Max(6,fontSize-1);}while(true);
                    d.PushClip(new RectangleGeometry(rect));d.DrawRectangle(background,null,rect);d.DrawText(text,new Point(rect.Left+1,rect.Top+Math.Max(0,(rect.Height-text.Height)/2)));d.Pop();
                }
            }
            var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);bitmap.Freeze();return bitmap;
        }
    }
}
