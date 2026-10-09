using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QingJie {
    // A few bounded transcripts, never screenshots or live OCR engines.
    internal sealed class OcrResultCache {
        sealed class Entry {public string Key;public OcrPage Page;public long Expires;}
        readonly LinkedList<Entry> entries=new LinkedList<Entry>();
        readonly Func<long> clock;
        internal OcrResultCache(Func<long> clock=null){this.clock=clock??(()=>Now);}
        static long Now{get{return System.Diagnostics.Stopwatch.GetTimestamp()/System.Diagnostics.Stopwatch.Frequency;}}
        public static string Fingerprint(BitmapSource source,CancellationToken cancel){
            var pixels=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            int stride=checked(source.PixelWidth*4);var rows=new byte[checked(stride*Math.Min(16,source.PixelHeight))];
            using(var hash=SHA256.Create()){
                for(int y=0;y<source.PixelHeight;y+=16){cancel.ThrowIfCancellationRequested();int count=Math.Min(16,source.PixelHeight-y);pixels.CopyPixels(new Int32Rect(0,y,source.PixelWidth,count),rows,stride,0);hash.TransformBlock(rows,0,stride*count,null,0);}
                hash.TransformFinalBlock(new byte[0],0,0);return source.PixelWidth+"x"+source.PixelHeight+":"+Convert.ToBase64String(hash.Hash);
            }
        }
        static OcrPage Copy(OcrPage source){return new OcrPage{Text=source.Text,Words=source.Words.Select(w=>new WordBox{Text=w.Text,Line=w.Line,Box=w.Box,TextStart=w.TextStart}).ToList()};}
        // Calls are serialized by OcrWorker's existing queue.
        void Purge(){long now=clock();for(var node=entries.First;node!=null;){var next=node.Next;if(now>=node.Value.Expires)entries.Remove(node);node=next;}}
        public OcrPage Get(string fingerprint){Purge();for(var node=entries.First;node!=null;node=node.Next)if(node.Value.Key==fingerprint){entries.Remove(node);entries.AddLast(node);return Copy(node.Value.Page);}return null;}
        public void Put(string fingerprint,OcrPage result){
            Purge();for(var node=entries.First;node!=null;){var next=node.Next;if(node.Value.Key==fingerprint)entries.Remove(node);node=next;}
            if(result.Words.Count==0||result.Words.Count>500||result.Text.Length>16000)return;
            while(entries.Count>0&&(entries.Count>=3||entries.Sum(e=>e.Page.Text.Length)+result.Text.Length>32000||entries.Sum(e=>e.Page.Words.Count)+result.Words.Count>1000))entries.RemoveFirst();
            entries.AddLast(new Entry {Key=fingerprint,Page=Copy(result),Expires=clock()+120});
        }
    }
}
