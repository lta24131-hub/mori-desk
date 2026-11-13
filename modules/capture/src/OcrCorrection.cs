using System;
using System.Linq;
using System.Windows;

namespace QingJie {
    // Corrections belong to this result window. They never rewrite the image,
    // shared OCR cache or other open results, and retain the source geometry.
    internal static class OcrCorrection {
        internal static bool Contains(TranslationRegion region,WordBox word){
            if(region.Box==word.Box)return true;
            var center=new Point(word.Box.Left+word.Box.Width/2,word.Box.Top+word.Box.Height/2);
            return region.Box.Contains(center)&&(region.SourceLines.Count==0||region.SourceLines.Any(line=>line.Box.Contains(center)));
        }
        internal static int FindRegion(OcrPage page,int start,int length){
            if(page==null)return -1;
            var selected=page.Words.Where(w=>w.TextStart<start+Math.Max(1,length)&&w.TextStart+w.Text.Length>start).ToArray();
            if(selected.Length==0)return -1;
            var regions=ImageTranslation.Regions(page);
            for(int i=0;i<regions.Count;i++)if(selected.All(w=>Contains(regions[i],w)))return i;
            return -1;
        }
        internal static OcrPage Apply(OcrPage source,int index,string text){
            if(source==null)throw new ArgumentNullException("source");
            if(string.IsNullOrWhiteSpace(text)||text.Length>16000)throw new ArgumentException("请输入文字，最多 16000 字。","text");
            var page=OcrResultCache.Copy(source);
            page.TranslationRegions=ImageTranslation.Regions(page);
            if(index<0||index>=page.TranslationRegions.Count)throw new ArgumentOutOfRangeException("index");
            var region=page.TranslationRegions[index];
            var indices=page.Words.Select((word,i)=>new{word,i}).Where(p=>Contains(region,p.word)).Select(p=>p.i).ToArray();
            if(indices.Length==0||indices.Last()-indices.First()+1!=indices.Length)throw new InvalidOperationException("这一段的位置有重叠，请选择较小的区域重试。");
            int first=indices[0];var old=page.Words[first];
            text=text.Trim().Replace("\r\n","\n").Replace('\r','\n');
            page.Words.RemoveRange(first,indices.Length);
            page.Words.Insert(first,new WordBox{Text=text,Box=region.Box,Line=old.Line,Prefix=old.Prefix,Confidence=1,ConfidenceSource=OcrConfidenceSource.User});
            region.Text=text;
            // Keep the physical line boxes for erasing the original lettering.
            OcrService.IndexText(page);return page;
        }
    }
}
