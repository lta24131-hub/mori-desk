using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;

namespace MoriDesk {
 // Some desktop apps expose FILECONTENTS only as an indexed OLE stream.
 // Access it synchronously during Drop, copy bytes, and release the medium.
 internal static class VirtualImageFile {
  [DllImport("ole32.dll")]static extern void ReleaseStgMedium(ref STGMEDIUM medium);
  [DllImport("kernel32.dll")]static extern UIntPtr GlobalSize(IntPtr handle);
  [DllImport("kernel32.dll")]static extern IntPtr GlobalLock(IntPtr handle);
  [DllImport("kernel32.dll")]static extern bool GlobalUnlock(IntPtr handle);
  internal static byte[] Read(System.Windows.IDataObject data){
   var native=data as System.Runtime.InteropServices.ComTypes.IDataObject;if(native==null)return null;
   var format=new FORMATETC{cfFormat=(short)DataFormats.GetDataFormat("FileContents").Id,dwAspect=DVASPECT.DVASPECT_CONTENT,lindex=0,tymed=TYMED.TYMED_ISTREAM|TYMED.TYMED_HGLOBAL};
   if(native.QueryGetData(ref format)!=0)return null;
   STGMEDIUM medium;native.GetData(ref format,out medium);
   try{
    if(medium.tymed==TYMED.TYMED_HGLOBAL){
     ulong count=GlobalSize(medium.unionmember).ToUInt64();if(count>DesktopImageDrop.Maximum)throw new InvalidDataException("图片超过 20 MB，请先保存到电脑再导入。");
     IntPtr pointer=GlobalLock(medium.unionmember);if(pointer==IntPtr.Zero)return null;
     try{var bytes=new byte[(int)count];Marshal.Copy(pointer,bytes,0,bytes.Length);return bytes;}finally{GlobalUnlock(medium.unionmember);}
    }
    if(medium.tymed==TYMED.TYMED_ISTREAM){
     object stream=Marshal.GetObjectForIUnknown(medium.unionmember);
     try{return ReadStream((IStream)stream);}finally{if(Marshal.IsComObject(stream))Marshal.ReleaseComObject(stream);}
    }
    return null;
   }finally{ReleaseStgMedium(ref medium);}
  }
  internal static byte[] ReadStream(IStream stream){
   var count=Marshal.AllocHGlobal(4);
   try{using(var output=new MemoryStream()){
    var buffer=new byte[65536];while(true){Marshal.WriteInt32(count,0);stream.Read(buffer,buffer.Length,count);int read=Marshal.ReadInt32(count);if(read==0)break;if(read<0||read>buffer.Length||output.Length+read>DesktopImageDrop.Maximum)throw new InvalidDataException("图片过大或来源数据无效。");output.Write(buffer,0,read);}return output.ToArray();
   }}finally{Marshal.FreeHGlobal(count);}
  }
 }
}
