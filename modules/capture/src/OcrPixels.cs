using System;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Tesseract;
using Windows.Graphics.Imaging;

namespace QingJie {
    [ComImport,Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMemoryBufferByteAccess {void GetBuffer(out IntPtr buffer,out uint capacity);}
    // Lossless handoff of the exact prepared samples; no PNG compression/decode.
    internal sealed class OcrPixels {
        readonly BitmapSource source;
        readonly int width,height;
        readonly double dpiX,dpiY;
        public OcrPixels(BitmapSource source){
            if(source.Format!=System.Windows.Media.PixelFormats.Gray8)throw new ArgumentException("Expected Gray8 OCR pixels.");
            width=source.PixelWidth;height=source.PixelHeight;dpiX=source.DpiX;dpiY=source.DpiY;
            this.source=source;
        }
        public SoftwareBitmap WindowsBitmap(){
            var bitmap=new SoftwareBitmap(BitmapPixelFormat.Gray8,width,height,BitmapAlphaMode.Ignore);
            try{
                using(var buffer=bitmap.LockBuffer(BitmapBufferAccessMode.Write))using(var reference=buffer.CreateReference()){
                    IntPtr address;uint capacity;((IMemoryBufferByteAccess)reference).GetBuffer(out address,out capacity);
                    var plane=buffer.GetPlaneDescription(0);
                    if(plane.StartIndex<0||plane.Stride<width||(long)plane.StartIndex+(long)plane.Stride*(height-1)+width>capacity)throw new InvalidOperationException("Invalid OCR bitmap buffer.");
                    source.CopyPixels(new System.Windows.Int32Rect(0,0,width,height),IntPtr.Add(address,plane.StartIndex),checked((int)capacity-plane.StartIndex),plane.Stride);
                }return bitmap;
            }catch{bitmap.Dispose();throw;}
        }
        public Pix TesseractBitmap(){
            var pix=Pix.Create(width,height,8);
            try{
                var data=pix.GetData();int stride=checked(data.WordsPerLine*4);
                source.CopyPixels(new System.Windows.Int32Rect(0,0,width,height),data.Data,checked(stride*height),stride);
                // Leptonica stores bytes in big-endian word order; Windows is LE.
                if(BitConverter.IsLittleEndian)data.EndianByteSwap();
                pix.XRes=(int)Math.Round(dpiX);pix.YRes=(int)Math.Round(dpiY);return pix;
            }catch{pix.Dispose();throw;}
        }
    }
}
