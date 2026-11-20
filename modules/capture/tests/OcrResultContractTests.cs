using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

// Safe runner group: only synthetic in-memory protocol data and an unshown WPF
// tree with a known transcript. Never start OCR, a process or native window.
internal static class OcrResultContractTests {
    static bool AcceptsManifest(byte[] actual,byte[] paired){
        try{OcrWorker.ValidateManifestProtocol(actual,paired);return true;}
        catch(InvalidDataException){return false;}
        catch(ArgumentException){return false;}
    }
    public static void Run(Action<bool,string> check){
        const string manifest="{\"schemaVersion\":1,\"component\":\"ocr-engine\",\"engineIdentity\":\"rapidocrnet-4.2.0-ppocrv5-ch-screen2x-trim1-layout5\",\"protocol\":3}";
        var utf8=System.Text.Encoding.UTF8;
        var paired=utf8.GetBytes(manifest);
        check(AcceptsManifest(utf8.GetBytes(manifest),paired),"byte-identical manifest verified with the bundled component is accepted");
        check(!AcceptsManifest(utf8.GetBytes(manifest.Replace("3}","2}")),paired),"a one-byte protocol downgrade at the end of a same-size manifest is rejected");
        check(!AcceptsManifest(utf8.GetBytes(manifest.TrimEnd('}')+","),paired),"a damaged final byte of a same-size manifest is rejected");
        check(AcceptsManifest(utf8.GetPreamble().Concat(utf8.GetBytes(manifest.Replace(",",",\n"))).ToArray(),paired),"differently formatted compatible component with UTF-8 BOM is validated normally");
        check(AcceptsManifest(paired,null),"unpaired development build still parses a valid component");
        var page=new OcrPage {Notice="English only; Chinese unavailable",Words=new System.Collections.Generic.List<WordBox>{new WordBox {Text="Hello",Line=3,Box=new Rect(1,2,30,10)},new WordBox {Text="world",Line=3,Box=new Rect(32,2,30,10)}}};
        using(var stream=new MemoryStream()){
            using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))OcrWorker.WriteResult(writer,page);stream.Position=0;
            OcrPage result;using(var reader=new BinaryReader(stream,System.Text.Encoding.UTF8,true))result=OcrWorker.ReadResult(reader);
            check(result.Notice==page.Notice&&result.Text=="Hello world"&&result.Words.Count==2&&result.Words[1].TextStart==6&&result.Words[1].Box==page.Words[1].Box,"OCR worker protocol preserves capability separately from transcript, offsets and geometry");
            bool truncated=false;using(var partial=new MemoryStream(stream.ToArray().Take((int)stream.Length-1).ToArray()))using(var reader=new BinaryReader(partial))try{OcrWorker.ReadResult(reader);}catch(EndOfStreamException){truncated=true;}check(truncated,"truncated OCR result cannot be presented as a complete successful transcript");
        }
        using(var stream=new MemoryStream()){
            page.Notice=null;using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))OcrWorker.WriteResult(writer,page);stream.Position=0;
            using(var reader=new BinaryReader(stream))check(OcrWorker.ReadResult(reader).Notice==null,"worker protocol retains default-null notice for ordinary results");
        }
        using(var stream=new MemoryStream()){
            using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true))writer.Write(0x514A4F31);stream.Position=0;bool oldProtocol=false;
            using(var reader=new BinaryReader(stream))try{OcrWorker.ReadResult(reader);}catch(InvalidDataException){oldProtocol=true;}check(oldProtocol,"old worker protocol is rejected explicitly rather than silently misreading notice as a word count");
        }
        using(var stream=new MemoryStream()){
            using(var writer=new BinaryWriter(stream,System.Text.Encoding.UTF8,true)){writer.Write(0x514A4F32);writer.Write("Missing engine guidance");}stream.Position=0;bool failed=false;
            using(var reader=new BinaryReader(stream))try{OcrWorker.ReadResult(reader);}catch(InvalidOperationException error){failed=error.Message=="Missing engine guidance";}check(failed,"worker error frame surfaces repair guidance without attempting to decode a success result");
        }
        var image=BitmapSource.Create(4,4,96,96,PixelFormats.Bgra32,null,new byte[64],16);image.Freeze();
        string fingerprint=OcrResultCache.Fingerprint(image,CancellationToken.None);
        check(object.ReferenceEquals(fingerprint,OcrResultCache.Fingerprint(image,CancellationToken.None)),"frozen screenshots reuse their pixel digest without retaining the image");
        var editable=new WriteableBitmap(image);string before=OcrResultCache.Fingerprint(editable,CancellationToken.None);
        editable.WritePixels(new Int32Rect(0,0,1,1),new byte[]{11,22,33,255},4,0);
        check(before==fingerprint&&OcrResultCache.Fingerprint(editable,CancellationToken.None)!=before,"editing an unfrozen screenshot invalidates its pixel match");
        using(var cancel=new CancellationTokenSource()){
            cancel.Cancel();bool hashCancelled=false;try{OcrResultCache.Fingerprint(image,cancel.Token);}catch(OperationCanceledException){hashCancelled=true;}check(hashCancelled,"even a warm fingerprint lookup respects cancellation");
            cancel.Cancel();bool cancelled=false;try{OcrService.Read(image,cancel.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancelled=true;}check(cancelled,"pre-cancelled public OCR entry returns cancellation before starting worker or cache work");
        }
        page.Notice="English only; Chinese unavailable";OcrService.IndexText(page);
        long seconds=0;var lines=new OcrResultCache(()=>seconds,12);
        for(int i=0;i<12;i++)lines.Put(i.ToString(),page);
        check(lines.Get("0")!=null,"revisiting the first of twelve short readings can reuse the exact transcript");
        lines.Put("12",page);seconds=119;
        check(lines.Get("1")==null&&lines.Get("0")!=null&&lines.Get("12")!=null,"a thirteenth short reading drops the least recently used one");
        seconds=120;
        check(lines.Get("0")==null&&lines.Get("12")==null,"reading cached words does not extend their two-minute lifetime");
        var window=new TextWindow(image,"zh",page,null,null,allowTranslation:false,allowPin:false);
        try{
            var status=(TextBlock)typeof(TextWindow).GetField("status",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
            var card=(System.Windows.Controls.Border)typeof(TextWindow).GetField("statusCard",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);
            check(status.Text==page.Notice&&card.Visibility==Visibility.Visible&&window.EnsureOcr().GetAwaiter().GetResult().Text=="Hello world","reused OCR opens with capability warning without another engine or transcript pollution");
            typeof(TextWindow).GetMethod("ClearStatus",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,null);check(status.Text==page.Notice&&card.Visibility==Visibility.Visible,"clearing progress never hides the OCR capability limitation");
            typeof(TextWindow).GetMethod("Status",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[]{"Image saved"});check(status.Text=="Image saved"+Environment.NewLine+page.Notice,"image output feedback retains OCR capability warning in existing card");
        }finally{window.Close();}
        page.Notice=null;window=new TextWindow(image,"zh",page,null,null,allowTranslation:false,allowPin:false);
        try{var card=(System.Windows.Controls.Border)typeof(TextWindow).GetField("statusCard",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);check(card.Visibility==Visibility.Collapsed,"normal known OCR result adds no new status card");}finally{window.Close();}
    }
}
