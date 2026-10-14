using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QingJie;

namespace MoriDesk {
    static class ScreenColorTests {
        static byte[] Bytes(BitmapSource image){var pixels=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(pixels,image.PixelWidth*4,0);return pixels;}
        internal static void Run(Action<bool,string> check){
            check(!AdvancedScreenCapture.IsDesktopFrame(0,0)&&!AdvancedScreenCapture.IsDesktopFrame(0,1)&&!AdvancedScreenCapture.IsDesktopFrame(1,0)&&AdvancedScreenCapture.IsDesktopFrame(1,1),"new desktop duplication skips pointer-only metadata rather than judging valid black pixels");
            foreach(double white in new[]{1.0,2.5,5.0,12.0})foreach(byte gray in new byte[]{0,32,64,96,128,160,184,192,224,240,248,255}){
                double srgb=gray/255.0,linear=srgb<=.04045?srgb/12.92:Math.Pow((srgb+.055)/1.055,2.4);
                check(Math.Abs(AdvancedScreenCapture.LinearToSrgb(linear*white,white)-gray)<=1,"HDR SDR-white normalization restores gray "+gray+" at white multiplier "+white);
            }
            check(AdvancedScreenCapture.LinearToSrgb(2,2.5)>224&&AdvancedScreenCapture.LinearToSrgb(2,2.5)<240,"HDR highlights normalize before clipping so light-gray text is not forced to white");
            check(AdvancedScreenCapture.LinearToSrgb(-.1,2.5)==0&&AdvancedScreenCapture.LinearToSrgb(double.NaN,2.5)==0&&AdvancedScreenCapture.LinearToSrgb(double.PositiveInfinity,2.5)==255,"negative/NaN/infinite HDR colors clamp safely");
            check(AdvancedScreenCapture.HalfValue(0x3c00)==1&&AdvancedScreenCapture.HalfValue(0x4000)==2&&AdvancedScreenCapture.HalfValue(0xbc00)==-1&&AdvancedScreenCapture.HalfValue(1)>0,"FP16 conversion preserves reference white, extended range, sign and subnormal values");
            check(double.IsPositiveInfinity(AdvancedScreenCapture.HalfValue(0x7c00))&&double.IsNaN(AdvancedScreenCapture.HalfValue(0x7e00)),"FP16 special values remain explicit before SDR conversion");
            var raw=new byte[]{10,20,30,0,40,50,60,128,99,99,99,99,70,80,90,255,100,110,120,64,99,99,99,99};
            IntPtr buffer=Marshal.AllocHGlobal(raw.Length);
            try{
                Marshal.Copy(raw,0,buffer,raw.Length);
                var normal=AdvancedScreenCapture.PixelsForTest(buffer,12,2,2,87,1,5);
                check(normal.IsFrozen&&normal.Format==PixelFormats.Bgra32,"captured SDR bitmap is frozen and explicitly opaque");
                check(Bytes(normal).SequenceEqual(new byte[]{10,20,30,255,40,50,60,255,70,80,90,255,100,110,120,255}),"BGRA8 bypasses gamma conversion, honors row padding and ignores unused desktop alpha");
                check(Bytes(AdvancedScreenCapture.PixelsForTest(buffer,12,2,2,87,2,1)).Where((b,i)=>i%4==0).SequenceEqual(new byte[]{70,10,100,40}),"90-degree portrait monitor pixels rotate to desktop orientation");
                check(Bytes(AdvancedScreenCapture.PixelsForTest(buffer,12,2,2,87,3,1)).Where((b,i)=>i%4==0).SequenceEqual(new byte[]{100,70,40,10}),"180-degree monitor pixels rotate to desktop orientation");
                check(Bytes(AdvancedScreenCapture.PixelsForTest(buffer,12,2,2,87,4,1)).Where((b,i)=>i%4==0).SequenceEqual(new byte[]{40,100,10,70}),"270-degree portrait monitor pixels rotate to desktop orientation");
                bool bad=false;try{AdvancedScreenCapture.PixelsForTest(buffer,7,2,2,87,1,1);}catch(InvalidOperationException){bad=true;}check(bad,"truncated native row pitch is rejected before reading unmanaged memory");
                var surface=new ScreenshotSurface {Image=normal,Selection=new Rect(0,0,2,2)};surface.Measure(new Size(2,2));surface.Arrange(new Rect(0,0,2,2));check(Bytes(surface.Export(false,false)).SequenceEqual(Bytes(normal)),"screenshot export preserves corrected screen colors without shade, handles or labels");
            } finally {Marshal.FreeHGlobal(buffer);}
            var rectangular=new byte[]{1,0,0,255,2,0,0,255,3,0,0,255,4,0,0,255,5,0,0,255,6,0,0,255};buffer=Marshal.AllocHGlobal(rectangular.Length);
            try{Marshal.Copy(rectangular,0,buffer,rectangular.Length);var portrait=AdvancedScreenCapture.PixelsForTest(buffer,12,3,2,87,2,1);check(portrait.PixelWidth==2&&portrait.PixelHeight==3&&Bytes(portrait).Where((b,i)=>i%4==0).SequenceEqual(new byte[]{4,1,5,2,6,3}),"non-square 90-degree screen swaps width/height while preserving every pixel");portrait=AdvancedScreenCapture.PixelsForTest(buffer,12,3,2,87,4,1);check(portrait.PixelWidth==2&&portrait.PixelHeight==3&&Bytes(portrait).Where((b,i)=>i%4==0).SequenceEqual(new byte[]{3,6,2,5,1,4}),"non-square 270-degree screen swaps width/height while preserving every pixel");}finally{Marshal.FreeHGlobal(buffer);}
            short[] floats={unchecked((short)0x4100),unchecked((short)0x3c00),0,0};buffer=Marshal.AllocHGlobal(8);
            try {Marshal.Copy(floats,0,buffer,4);var pixel=Bytes(AdvancedScreenCapture.PixelsForTest(buffer,8,1,1,10,1,2.5));check(pixel[0]==0&&pixel[1]==170&&pixel[2]==255&&pixel[3]==255,"FP16 red/green/blue order, SDR white and opaque alpha are correct");}
            finally {Marshal.FreeHGlobal(buffer);}
        }
    }
}
