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
        readonly int capacity;
        internal OcrResultCache(Func<long> clock=null,int capacity=3){
            if(capacity<1)throw new ArgumentOutOfRangeException("capacity");
            this.clock=clock??(()=>Now);this.capacity=capacity;
        }
        static long Now{get{return System.Diagnostics.Stopwatch.GetTimestamp()/System.Diagnostics.Stopwatch.Frequency;}}
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource,string> frozenFingerprints=new System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource,string>();
        public static string Fingerprint(BitmapSource source,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();
            // Weak keys retain no screenshot. Never reuse this hint for editable bitmap
            // objects, and never use object identity instead of content across images.
            return source.IsFrozen?frozenFingerprints.GetValue(source,s=>HashPixels(s,cancel)):HashPixels(source,cancel);
        }
        static string HashPixels(BitmapSource source,CancellationToken cancel){
            var pixels=source.Format==PixelFormats.Bgra32?source:new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            int stride=checked(source.PixelWidth*4);var rows=new byte[checked(stride*Math.Min(16,source.PixelHeight))];
            using(var hash=SHA256.Create()){
                for(int y=0;y<source.PixelHeight;y+=16){cancel.ThrowIfCancellationRequested();int count=Math.Min(16,source.PixelHeight-y);pixels.CopyPixels(new Int32Rect(0,y,source.PixelWidth,count),rows,stride,0);hash.TransformBlock(rows,0,stride*count,null,0);}
                hash.TransformFinalBlock(new byte[0],0,0);return source.PixelWidth+"x"+source.PixelHeight+":"+Convert.ToBase64String(hash.Hash);
            }
        }
        internal static OcrPage Copy(OcrPage source){return new OcrPage{Text=source.Text,Notice=source.Notice,Words=source.Words.Select(w=>new WordBox{Text=w.Text,Line=w.Line,Box=w.Box,TextStart=w.TextStart,Prefix=w.Prefix,Confidence=w.Confidence,ConfidenceSource=w.ConfidenceSource}).ToList(),TranslationRegions=source.TranslationRegions.Select(r=>new TranslationRegion{Text=r.Text,Box=r.Box,Lines=r.Lines,SourceLines=r.SourceLines.Select(l=>new TranslationSourceLine{Text=l.Text,Box=l.Box}).ToList()}).ToList()};}
        void Purge(){long now=clock();for(var node=entries.First;node!=null;){var next=node.Next;if(now>=node.Value.Expires)entries.Remove(node);node=next;}}
        public OcrPage Get(string fingerprint){
            OcrPage page=null;
            lock(entries){
                Purge();
                for(var node=entries.First;node!=null;node=node.Next){
                    if(node.Value.Key!=fingerprint)continue;
                    entries.Remove(node);entries.AddLast(node);page=node.Value.Page;break;
                }
            }
            // Only private snapshots enter the cache. Copy outside the lock so
            // another window can fetch a short line while this page is copied.
            return page==null?null:Copy(page);
        }
        public void Put(string fingerprint,OcrPage result){
            var page=result.Words.Count>0&&result.Words.Count<=500&&result.Text.Length<=16000?Copy(result):null;
            lock(entries){
                Purge();for(var node=entries.First;node!=null;){var next=node.Next;if(node.Value.Key==fingerprint)entries.Remove(node);node=next;}
                if(page==null)return;
                while(entries.Count>0&&(entries.Count>=capacity||entries.Sum(e=>e.Page.Text.Length)+page.Text.Length>32000||entries.Sum(e=>e.Page.Words.Count)+page.Words.Count>1000))entries.RemoveFirst();
                entries.AddLast(new Entry {Key=fingerprint,Page=page,Expires=clock()+120});
            }
        }
    }
}
