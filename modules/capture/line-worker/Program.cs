using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using QingJie;
using Tesseract;

[assembly:System.Reflection.AssemblyTitle("Nook single-line OCR")]
[assembly:System.Reflection.AssemblyProduct("Nook")]

internal static class LineWorker {
    const string ArgumentPrefix="--pipe=QingJie-OCR-";
    const int RequestMagic=0x514A4F32,ResultMagic=0x514A4F34;
    const int WatchdogMilliseconds=30000;

    static int Main(string[] args){
        Guid id;
        if(args.Length!=1||!args[0].StartsWith(ArgumentPrefix,StringComparison.Ordinal)||
            !Guid.TryParseExact(args[0].Substring(ArgumentPrefix.Length),"N",out id))return 2;
        string name=args[0].Substring("--pipe=".Length);
        try{
            using(var watchdog=new Timer(_=>Environment.Exit(3),null,WatchdogMilliseconds,Timeout.Infinite))
            using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.None,TokenImpersonationLevel.Identification)){
                pipe.Connect(10000);
                using(var reader=new BinaryReader(pipe,Encoding.UTF8,true))
                using(var writer=new BinaryWriter(pipe,Encoding.UTF8,true)){
                    while(true){
                        int magic;
                        try{magic=reader.ReadInt32();}catch(EndOfStreamException){return 0;}
                        if(magic!=RequestMagic)return 2;
                        watchdog.Change(WatchdogMilliseconds,Timeout.Infinite);
                        ReadFrame(reader,writer);
                    }
                }
            }
        }catch(Exception){return 1;}
    }

    // Connect before resolving the native library. A malformed or incomplete
    // frame cannot allocate a recognizer. Release the model and native pixels
    // after each response so prior screenshots cannot affect later readings.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void ReadFrame(BinaryReader reader,BinaryWriter writer){
        int w=reader.ReadInt32(),h=reader.ReadInt32(),dpiX=reader.ReadInt32(),dpiY=reader.ReadInt32();
        if(w<1||h<1||w>1536||h>224||(long)w*h>280000||
            dpiX<1||dpiX>10000||dpiY<1||dpiY>10000)throw new InvalidDataException();
        int stride=(w+3)&~3,length=checked(stride*h);
        byte[] samples=reader.ReadBytes(length);
        if(samples.Length!=length)throw new EndOfStreamException();
        List<LineSymbol> records;
        using(var bitmap=Pix.Create(w,h,8)){
            var storage=bitmap.GetData();
            if(storage.WordsPerLine*4!=stride)throw new InvalidDataException();
            Marshal.Copy(samples,0,storage.Data,length);
            if(BitConverter.IsLittleEndian)storage.EndianByteSwap();
            bitmap.XRes=dpiX;bitmap.YRes=dpiY;
            using(var engine=LineSymbolReader.Open())records=LineSymbolReader.Read(engine,bitmap);
        }
        using(var response=new MemoryStream())
        using(var fields=new BinaryWriter(response,Encoding.UTF8,true)){
            fields.Write(ResultMagic);fields.Write("");fields.Write("");fields.Write(records.Count);
            foreach(var row in records){
                fields.Write(row.Text);fields.Write(row.Group);fields.Write(row.X);fields.Write(row.Y);
                fields.Write(row.Width);fields.Write(row.Height);fields.Write(row.Confidence);
                fields.Write(1); // OcrConfidenceSource.Model in the parent's result protocol.
            }
            fields.Flush();writer.Write((int)response.Length);
            writer.Write(response.GetBuffer(),0,(int)response.Length);writer.Flush();
        }
    }
}
