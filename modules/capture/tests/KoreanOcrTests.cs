using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

internal static class KoreanOcrTests {
    static OcrPage Line(string text,double score,Rect box){
        var p=new OcrPage();
        p.Words.Add(new WordBox{Text=text,Line=0,Box=box,Confidence=score,ConfidenceSource=OcrConfidenceSource.Model});
        OcrService.IndexText(p);return p;
    }
    static BitmapSource TestBand(bool blank){
        var pixels=new byte[160*72*4];
        for(int y=0;y<72;y++)for(int x=0;x<160;x++){
            int i=(y*160+x)*4;
            byte tone=(byte)(!blank&&y>=22&&y<36&&x>=12&&x<140&&x%7<3?25:245);
            pixels[i]=pixels[i+1]=pixels[i+2]=tone;pixels[i+3]=255;
        }
        var image=BitmapSource.Create(160,72,96,96,PixelFormats.Bgra32,null,pixels,160*4);
        image.Freeze();return image;
    }
    static BitmapSource Paragraph(int glyphWidth,int wordGap){
        const int width=180,height=60;
        var pixels=new byte[width*height];
        for(int i=0;i<pixels.Length;i++)pixels[i]=245;
        for(int line=0;line<2;line++){
            int left=7;
            for(int word=0;word<4;word++){
                for(int glyph=0;glyph<2;glyph++){
                    for(int y=8+line*24;y<20+line*24;y++){
                        for(int x=left;x<left+glyphWidth;x++)
                            if(x==left||x==left+glyphWidth-1||y%6==2)pixels[y*width+x]=30;
                    }
                    left+=glyphWidth+2;
                }
                left+=wordGap;
            }
        }
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Gray8,null,pixels,width);
        image.Freeze();return image;
    }
    internal static void Run(Action<bool,string> check){
        var image=TestBand(false);var box=new Rect(12,22,128,14);var token=CancellationToken.None;
        var english=Line("Open this document",.98,box);
        var chinese=Line("截图翻译与文字识别",.98,box);
        check(!KoreanOcr.NeedsSecondReading(image,english,token)&&!KoreanOcr.NeedsSecondReading(image,chinese,token),"readable English and Chinese rows do not start the Korean model");
        check(!KoreanOcr.NeedsSecondReading(TestBand(true),new OcrPage(),token),"empty image avoids a second OCR process");
        check(KoreanOcr.NeedsSecondReading(image,new OcrPage(),token),"a text band omitted by the primary alphabet receives local script review");
        var fragment=Line("A1",.42,new Rect(12,22,14,14));
        check(KoreanOcr.NeedsSecondReading(image,fragment,token),"a sparse fragment does not account for the whole visible row");
        var korean=Line("파일을 저장합니다",.96,box);
        check(object.ReferenceEquals(chinese,KoreanOcr.Merge(chinese,korean)),"a confident complete source row survives a conflicting Korean guess");
        check(object.ReferenceEquals(fragment,KoreanOcr.Merge(fragment,new OcrPage())),"a failed Korean reading preserves the primary result");
        fragment.Words.Add(new WordBox{Text="Title",Line=1,Box=new Rect(12,2,28,11),Confidence=.99,ConfidenceSource=OcrConfidenceSource.Model});
        var merged=KoreanOcr.Merge(fragment,korean);
        check(merged.Text=="Title"+Environment.NewLine+"파일을 저장합니다"&&merged.Words[1].Box==box,"recover Korean with original coordinates while keeping an adjacent English row");
        var spaced=KoreanOcr.FindSpacedParagraph(Paragraph(10,5),token);
        check(spaced!=null&&spaced.Count==2&&spaced[0].Words.Count==4,"spaced square characters can use a Korean first reading");
        check(KoreanOcr.FindSpacedParagraph(Paragraph(10,0),token)==null&&KoreanOcr.FindSpacedParagraph(Paragraph(4,5),token)==null,"unspaced characters and thin Latin-like strokes stay with the primary reader");
        var sample=new OcrPage();
        foreach(var band in spaced)foreach(var group in band.Words)
            sample.Words.Add(new WordBox{Text="그림",Line=band.Top,Box=group,Confidence=.94,ConfidenceSource=OcrConfidenceSource.Model});
        check(KoreanOcr.AccountsForParagraph(spaced,sample),"a complete Korean reading matches the source word groups");
        sample.Words[0].Box=new Rect(0,0,2,1);
        check(!KoreanOcr.AccountsForParagraph(spaced,sample),"unread ink in a mixed row requires the full primary reader");
        var confirmed=KoreanOcr.ConfirmedLines(spaced,sample);
        check(confirmed!=null&&confirmed.Words.Count==4&&confirmed.Words.TrueForAll(w=>w.Line==spaced[1].Top),"a primary timeout can keep only the independently verified row with its original coordinates");
        sample.Words[0].Box=spaced[0].Words[0];
        sample.Words[0].Text="English";
        check(!KoreanOcr.AccountsForParagraph(spaced,sample),"similar geometry with mixed words does not bypass the primary reader");
        foreach(var word in sample.Words)word.Text="中文";
        check(KoreanOcr.ConfirmedLines(spaced,sample)==null,"a general-reader timeout never upgrades a Chinese guess to confirmed Korean");
        var narrow=new KoreanOcr.InkLine(3,16){Pitch=13};
        var shortWords=new OcrPage();
        for(int i=0;i<4;i++){
            var box13=new Rect(i*18,3,13,13);narrow.Words.Add(box13);
            shortWords.Words.Add(new WordBox{Text="글",Line=0,Box=box13,Confidence=.94,ConfidenceSource=OcrConfidenceSource.Model});
        }
        var ink13=new System.Collections.Generic.List<KoreanOcr.InkLine>{narrow};
        shortWords.Words[0].Box=new Rect(0,3,28.0/3,13);
        check(KoreanOcr.AccountsForParagraph(ink13,shortWords),"a 13-pixel glyph with 9⅓ source-pixels covered tolerates the reader's subpixel rounding");
        shortWords.Words[0].Box=new Rect(0,3,6,13);
        check(!KoreanOcr.AccountsForParagraph(ink13,shortWords),"genuinely missing ink beyond coordinate precision still fails");
        string previousFolder=OcrLanguagePacks.TestFolder,previousSelection=OcrLanguagePacks.TestSelection;
        try{
            OcrLanguagePacks.TestFolder=Path.Combine(Path.GetTempPath(),"Nook-language-test-"+Guid.NewGuid().ToString("N"));
            OcrLanguagePacks.TestSelection="kor";
            string model=Path.Combine(OcrLanguagePacks.FolderForReading("kor"),"kor.traineddata");
            check(OcrLanguagePacks.HasBundledKorean&&OcrLanguagePacks.Verify(model,OcrLanguagePacks.Find("kor")),"bundled high-accuracy Korean model matches the pinned upstream file");
            OcrLanguagePacks.Install("kor",null,token).GetAwaiter().GetResult();
            OcrLanguagePacks.RemoveUserModel("kor");
            check(!Directory.Exists(OcrLanguagePacks.TestFolder)&&OcrLanguagePacks.EffectiveSelectionKey=="kor"&&File.Exists(model),"manual enable works offline and user cleanup never removes or copies the bundled model");
        }finally{OcrLanguagePacks.TestSelection=previousSelection;OcrLanguagePacks.TestFolder=previousFolder;}
    }
}
