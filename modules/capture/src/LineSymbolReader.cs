using System;
using System.Collections.Generic;
using System.IO;
using Tesseract;

namespace QingJie {
    // Shared with the stand-alone symbol worker. Coordinates are in the
    // original single-line crop; both recognizers receive a 2x gray image.
    internal sealed class LineSymbol {
        internal int Group;
        internal string Text;
        internal double X,Y,Width,Height,Confidence;
    }

    internal static class LineSymbolReader {
        internal static TesseractEngine Open(){
            return new TesseractEngine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tessdata"),"eng",EngineMode.LstmOnly);
        }
        internal static List<LineSymbol> Read(TesseractEngine engine,Pix bitmap){
            using(var page=engine.Process(bitmap,PageSegMode.SingleLine))
            using(var iterator=page.GetIterator()){
                var records=new List<LineSymbol>();
                int lines=0,words=0;
                iterator.Begin();
                do{
                    if(iterator.IsAtBeginningOf(PageIteratorLevel.TextLine)&&++lines>1)return new List<LineSymbol>();
                    if(iterator.IsAtBeginningOf(PageIteratorLevel.Word)){
                        var word=Get(iterator,PageIteratorLevel.Word,2*words);
                        if(word==null||!LineWordRules.SimpleWord(word.Text)||++words>12)return new List<LineSymbol>();
                        records.Add(word);
                    }
                    var symbol=Get(iterator,PageIteratorLevel.Symbol,2*words-1);
                    if(words==0||symbol==null||symbol.Text.Length!=1||records.Count>=72)return new List<LineSymbol>();
                    records.Add(symbol);
                }while(iterator.Next(PageIteratorLevel.Symbol));
                return lines==1?records:new List<LineSymbol>();
            }
        }
        static LineSymbol Get(ResultIterator it,PageIteratorLevel level,int group){
            const double ratio=2;
            string text=it.GetText(level);
            Tesseract.Rect b;
            if(string.IsNullOrWhiteSpace(text)||!it.TryGetBoundingBox(level,out b)||b.Width<=0||b.Height<=0)return null;
            return new LineSymbol{Group=group,Text=text,X=b.X1/ratio,Y=b.Y1/ratio,Width=b.Width/ratio,Height=b.Height/ratio,
                Confidence=it.GetConfidence(level)/100.0};
        }
    }

    internal static class LineWordRules {
        internal static bool SimpleWord(string text){
            if(string.IsNullOrEmpty(text)||text.Length>22)return false;
            int len=text.Length;char last=text[len-1];if(last=='.'||last=='!'||last=='?'||last==',')len--;
            int quote=text.IndexOf('\'');if(quote<0)quote=text.IndexOf('\u2019');
            if(quote>=0){
                if(quote<1||quote+1>=len)return false;
                string suffix=text.Substring(quote+1,len-quote-1);
                if(suffix=="t"){if(text[quote-1]!='n')return false;}
                else if(suffix!="s"&&suffix!="m"&&suffix!="d"&&suffix!="ll"&&suffix!="re"&&suffix!="ve")return false;
                // Preserve the observed apostrophe. Ambiguous names like Al's
                // use the main recognizer.
                len=quote;
            }
            if(len<1||(len==1&&text[0]!='a'&&text[0]!='A'&&!(quote>0&&text[0]=='I'))||
                (len==2&&(text[0]=='A'||text[0]=='I')&&text[1]=='l'))return false;
            for(int i=0;i<len;i++){char c=text[i];if((c<'a'||c>'z')&&(i!=0||c<'A'||c>'Z'))return false;}
            return true;
        }
    }
}
