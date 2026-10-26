using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

// Bitmap.FromHbitmap discards alpha. That turns a black SVG on a transparent
// background into an all-black opaque rectangle, hiding successful rendering.
internal static class ShellBitmapEvidence {
 [StructLayout(LayoutKind.Sequential)]struct Header{public uint size;public int width,height;public ushort planes,bits;public uint compression,imageSize;public int x,y;public uint used,important;}
 [StructLayout(LayoutKind.Sequential)]struct Info{public Header header;public uint color;}
 [StructLayout(LayoutKind.Sequential)]struct NativeBitmap{public int type,width,height,widthBytes;public ushort planes,bits;public IntPtr pixels;}
 [DllImport("gdi32.dll",EntryPoint="GetObjectW")]static extern int GetObject(IntPtr bitmap,int size,out NativeBitmap value);
 [DllImport("gdi32.dll")]static extern IntPtr CreateCompatibleDC(IntPtr dc);
 [DllImport("gdi32.dll")]static extern bool DeleteDC(IntPtr dc);
 [DllImport("gdi32.dll")]static extern int GetDIBits(IntPtr dc,IntPtr bitmap,uint first,uint count,byte[] pixels,ref Info info,uint usage);
 public static Bitmap Read(IntPtr handle){NativeBitmap value;if(GetObject(handle,Marshal.SizeOf(typeof(NativeBitmap)),out value)==0||value.width<=0||value.height<=0||value.width>2048||value.height>2048)throw new InvalidOperationException("Bounded Shell bitmap expected.");int stride=value.width*4;byte[] pixels=new byte[checked(stride*value.height)];var info=new Info{header=new Header{size=(uint)Marshal.SizeOf(typeof(Header)),width=value.width,height=-value.height,planes=1,bits=32}};IntPtr dc=CreateCompatibleDC(IntPtr.Zero);try{if(GetDIBits(dc,handle,0,(uint)value.height,pixels,ref info,0)!=value.height)throw new InvalidOperationException("Shell bitmap pixels unavailable.");}finally{if(dc!=IntPtr.Zero)DeleteDC(dc);}var result=new Bitmap(value.width,value.height,PixelFormat.Format32bppPArgb);var target=result.LockBits(new Rectangle(0,0,value.width,value.height),ImageLockMode.WriteOnly,PixelFormat.Format32bppPArgb);try{for(int row=0;row<value.height;row++)Marshal.Copy(pixels,row*stride,IntPtr.Add(target.Scan0,row*target.Stride),stride);}finally{result.UnlockBits(target);}return result;}
 public static void SaveVisible(Bitmap bitmap,string path){using(var visible=new Bitmap(bitmap.Width,bitmap.Height,PixelFormat.Format24bppRgb))using(var graphics=Graphics.FromImage(visible)){graphics.Clear(Color.White);graphics.DrawImageUnscaled(bitmap,0,0);visible.Save(path,ImageFormat.Png);}}
}
