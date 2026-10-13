using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace MoriDesk {
 // Read the OLE payload while Drop is still active. Never retain IDataObject
 // across an await: the source app can release its virtual file at drag-end.
 internal sealed class DesktopImageDrop {
  internal const int Maximum=20*1024*1024;
  internal byte[] Bytes;
  internal BitmapSource Bitmap;
  internal string Address,Name="拖入图片",Source="";
  static readonly string[] Encoded={"PNG","image/png","image/jpeg","JFIF","image/gif","image/webp"};
  internal static bool Internal(IDataObject data){return new[]{"Mori.AssetIds","MoriDesk.ReferenceIds",BoardPage.DragFormat}.Any(f=>data.GetDataPresent(f,false));}
  static bool Has(IDataObject data,string format){try{return data.GetDataPresent(format,false);}catch(System.Runtime.InteropServices.ExternalException){return false;}}
  internal static bool TemporaryPictures(IDataObject data){
   if(!Has(data,DataFormats.FileDrop))return false;
   var paths=data.GetData(DataFormats.FileDrop) as string[];
   return paths!=null&&paths.Length==1&&FileRouter.Kind(paths[0])=="image"&&InTemporaryFolder(paths[0]);
  }
  internal static bool InTemporaryFolder(string path){try{return Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}catch{return false;}}
  internal static bool CanRead(IDataObject data){
   try{
    if(data==null||Internal(data))return false;
    if(Has(data,DataFormats.FileDrop))return TemporaryPictures(data);
    return Encoded.Any(f=>Has(data,f))||data.GetDataPresent(DataFormats.Bitmap)||Has(data,"FileGroupDescriptorW")||Has(data,"FileGroupDescriptor")||(Has(data,DataFormats.Html)&&ImageAddress(Text(data,DataFormats.Html))!="")||Has(data,"UniformResourceLocatorW")||Has(data,"UniformResourceLocator");
   }catch{return false;}
  }
  internal static byte[] ReadBounded(Stream input,int limit){
   if(input==null)throw new InvalidDataException("来源软件没有提供图片数据。");
   long position=input.CanSeek?input.Position:0;
   try{
    if(input.CanSeek){if(input.Length>limit)throw new InvalidDataException("图片超过 20 MB，请先保存到电脑再导入。");input.Position=0;}
    using(var output=new MemoryStream()){byte[] block=new byte[65536];int count;while((count=input.Read(block,0,block.Length))>0){if(output.Length+count>limit)throw new InvalidDataException("图片超过 20 MB，请先保存到电脑再导入。");output.Write(block,0,count);}return output.ToArray();}
   }finally{if(input.CanSeek)input.Position=position;}
  }
  static byte[] DataBytes(object value,int limit){
   var bytes=value as byte[];if(bytes!=null){if(bytes.Length>limit)throw new InvalidDataException("图片数据过大。");return (byte[])bytes.Clone();}
   var stream=value as Stream;if(stream!=null)return ReadBounded(stream,limit);
   return null;
  }
  static string Text(IDataObject data,string format,bool unicode=false){
   if(!Has(data,format))return "";
   var value=data.GetData(format,false);var text=value as string;
   if(text!=null)return text.Length<=2*1024*1024?text.TrimEnd('\0'):"";
   var bytes=DataBytes(value,2*1024*1024);return bytes==null?"":(unicode?Encoding.Unicode:Encoding.UTF8).GetString(bytes).TrimEnd('\0');
  }
  internal static string ImageAddress(string html){
   if(string.IsNullOrEmpty(html)||html.Length>2*1024*1024)return "";
   var image=Regex.Match(html,@"<img\b[^>]*\s+src\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(200));
   return image.Success?WebUtility.HtmlDecode(image.Groups[1].Success?image.Groups[1].Value:image.Groups[2].Success?image.Groups[2].Value:image.Groups[3].Value):"";
  }
  internal static string SafeSource(string html){
   var match=Regex.Match(html??"",@"(?m)^SourceURL:([^\r\n]+)",RegexOptions.None,TimeSpan.FromMilliseconds(100));
   Uri source;return match.Success&&Mori.SourceLink.TryParse(match.Groups[1].Value,out source)?source.AbsoluteUri:"";
  }
  internal static DesktopImageDrop Capture(IDataObject data){
   if(!CanRead(data))throw new InvalidDataException("来源软件没有传出可用图片。请右键复制图片，再到 Mori 粘贴。");
   var result=new DesktopImageDrop();Exception failure=null;
   if(TemporaryPictures(data)){
    string path=((string[])data.GetData(DataFormats.FileDrop))[0];
    using(var input=File.OpenRead(path))result.Bytes=ReadBounded(input,Maximum);
    result.Name=Path.GetFileNameWithoutExtension(path);return result;
   }
   // A source can advertise formats it cannot actually render. Do not let
   // optional HTML or a broken preferred format discard another valid copy.
   foreach(string format in Encoded)if(Has(data,format)){
    if(TryCapture(()=>{var bytes=DataBytes(data.GetData(format,false),Maximum);if(bytes==null||bytes.Length==0)return false;ImageExtension(bytes);result.Bytes=bytes;return true;},ref failure))return WithSource(result,data);
   }
   // FILEGROUPDESCRIPTOR contains a virtual filename, not a disk path. Only
   // the first/sole image is accepted; never create arbitrary named files.
   if(Has(data,"FileGroupDescriptorW")||Has(data,"FileGroupDescriptor"))if(TryCapture(()=>{
    bool wide=Has(data,"FileGroupDescriptorW");byte[] descriptor=DataBytes(data.GetData(wide?"FileGroupDescriptorW":"FileGroupDescriptor",false),65536);
    if(descriptor!=null){
     int record=wide?592:332;
     if(descriptor.Length<4+record||BitConverter.ToUInt32(descriptor,0)!=1)throw new InvalidDataException("请一次拖入一张软件内的图片。");
     string name=(wide?Encoding.Unicode:Encoding.Default).GetString(descriptor,76,wide?520:260).Split('\0')[0];
     if(FileRouter.Kind(name)!="image")throw new InvalidDataException("拖入的虚拟文件不是受支持的图片。");
     result.Name=Path.GetFileNameWithoutExtension(name);
     // WPF may advertise FileContents but fail to marshal an indexed IStream.
     try{if(Has(data,"FileContents"))result.Bytes=DataBytes(data.GetData("FileContents",false),Maximum);}catch(System.Runtime.InteropServices.COMException){}catch(NotSupportedException){}
     if(result.Bytes==null||result.Bytes.Length==0)result.Bytes=VirtualImageFile.Read(data);
     if(result.Bytes!=null&&result.Bytes.Length>0){ImageExtension(result.Bytes);return true;}
    }
    return false;
   },ref failure))return WithSource(result,data);
   // Clear a rejected virtual payload before trying the bitmap or address.
   result.Bytes=null;
   if(TryCapture(()=>{
    var value=data.GetDataPresent(DataFormats.Bitmap)?data.GetData(DataFormats.Bitmap):null;
    var bitmap=value as BitmapSource;
    if(bitmap!=null){if(bitmap.PixelWidth<1||bitmap.PixelHeight<1||(long)bitmap.PixelWidth*bitmap.PixelHeight>40000000)throw new InvalidDataException("图片尺寸过大，请先保存到电脑再导入。");result.Bitmap=(BitmapSource)bitmap.GetCurrentValueAsFrozen();return true;}
    return false;
   },ref failure))return WithSource(result,data);
   string html="";TryCapture(()=>{html=Text(data,DataFormats.Html);return false;},ref failure);
   result.Source=SafeSource(html);var addresses=new List<string>();
   TryCapture(()=>{addresses.Add(ImageAddress(html));return false;},ref failure);
   TryCapture(()=>{addresses.Add(Text(data,"UniformResourceLocatorW",true).Trim());return false;},ref failure);
   TryCapture(()=>{addresses.Add(Text(data,"UniformResourceLocator").Trim());return false;},ref failure);
   foreach(string address in addresses.Where(a=>!string.IsNullOrWhiteSpace(a)).Distinct()){
    if(TryCapture(()=>{if(!address.StartsWith("data:image/",StringComparison.OrdinalIgnoreCase))DownloadAddress(address);result.Address=address;return true;},ref failure))return result;
   }
   throw new InvalidDataException(failure==null?"来源软件没有提供可读取的图片文件。请复制图片后在 Mori 按 Ctrl+V。":failure.Message,failure);
  }
  static bool TryCapture(Func<bool> read,ref Exception failure){
   try{return read();}
   catch(Exception ex){if(!(ex is IOException)&&!(ex is InvalidDataException)&&!(ex is System.Runtime.InteropServices.ExternalException)&&!(ex is NotSupportedException)&&!(ex is ArgumentException)&&!(ex is InvalidOperationException)&&!(ex is UnauthorizedAccessException)&&!(ex is System.Security.SecurityException))throw;failure=ex;return false;}
  }
  static DesktopImageDrop WithSource(DesktopImageDrop result,IDataObject data){Exception ignored=null;TryCapture(()=>{result.Source=SafeSource(Text(data,DataFormats.Html));return true;},ref ignored);return result;}
  internal static string ImageExtension(byte[] bytes){
   var ext=Mori.NativeBridge.Detect(bytes);if(ext!=null&&FileRouter.Pictures.Contains(ext))return ext;
   if(bytes!=null&&bytes.Length>12){if(bytes[0]=='B'&&bytes[1]=='M')return ".bmp";if((bytes[0]=='I'&&bytes[1]=='I'&&bytes[2]==42&&bytes[3]==0)||(bytes[0]=='M'&&bytes[1]=='M'&&bytes[2]==0&&bytes[3]==42))return ".tif";}
   throw new InvalidDataException("收到的内容不是受支持的图片，未保存为网址或空文件。");
  }
  internal static Uri DownloadAddress(string value){
   Uri uri;
   if(!Mori.SourceLink.TryParse(value,out uri)||!Mori.BookmarkIcons.Allowed(uri))throw new InvalidDataException("没有可用的图片下载地址。请在来源软件复制图片后粘贴到 Mori。");
   return uri;
  }
  internal async Task<byte[]> Materialize(){
   if(Bytes!=null)return Bytes;
   if(Bitmap!=null){var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(Bitmap));using(var output=new MemoryStream()){encoder.Save(output);if(output.Length>Maximum)throw new InvalidDataException("图片超过 20 MB，请先保存到电脑再导入。");return output.ToArray();}}
   if(Address.StartsWith("data:",StringComparison.OrdinalIgnoreCase)){
    int comma=Address.IndexOf(',');if(comma<0||comma>100||!Regex.IsMatch(Address.Substring(0,comma),@"\Adata:image/(png|jpeg|gif|webp);base64\z",RegexOptions.IgnoreCase)||Address.Length-comma>28*1024*1024)throw new InvalidDataException("图片内嵌数据无效或过大。");
    var bytes=Convert.FromBase64String(Address.Substring(comma+1));if(bytes.Length>Maximum)throw new InvalidDataException("图片超过 20 MB。");return bytes;
   }
   Uri address=DownloadAddress(Address);
   // No credentials, cookie extraction, page scraping, or login workarounds.
   using(var handler=new HttpClientHandler{UseCookies=false,UseDefaultCredentials=false,AllowAutoRedirect=false})
   using(var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(20)})
   using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20))){
    for(int hop=0;hop<5;hop++){
     var lookup=Dns.GetHostAddressesAsync(address.DnsSafeHost);
     if(await Task.WhenAny(lookup,Task.Delay(3000,timeout.Token)).ConfigureAwait(false)!=lookup)throw new IOException("图片地址解析超时，请复制图片后在 Mori 粘贴。");
     var addresses=await lookup.ConfigureAwait(false);if(addresses.Length==0||addresses.Any(ip=>!Mori.BookmarkIcons.Public(ip)))throw new IOException("未访问本地或私有网络图片地址，请直接复制图片后粘贴。");
     using(var response=await client.GetAsync(address,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false)){
     if((int)response.StatusCode>=300&&(int)response.StatusCode<400&&response.Headers.Location!=null){var next=DownloadAddress(new Uri(address,response.Headers.Location).AbsoluteUri);if(address.Scheme=="https"&&next.Scheme!="https")throw new IOException("图片地址不安全，未继续下载。");address=next;continue;}
     if(!response.IsSuccessStatusCode)throw new IOException("来源软件只传出了图片链接，但下载被拒绝（HTTP "+(int)response.StatusCode+"）。请在 GPT 复制图片后粘贴到 Mori。");
     if(response.Content.Headers.ContentLength>Maximum)throw new IOException("图片超过 20 MB，请先保存到电脑再导入。");
     using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new MemoryStream()){
      byte[] block=new byte[65536];int count;while((count=await input.ReadAsync(block,0,block.Length,timeout.Token).ConfigureAwait(false))>0){if(output.Length+count>Maximum)throw new IOException("图片超过 20 MB。");output.Write(block,0,count);}return output.ToArray();
     }
    }
    }
   }
   throw new IOException("图片下载重定向次数过多，未保存。");
  }
  internal async Task<string> Stage(){
   byte[] bytes=await Materialize().ConfigureAwait(false);string extension=ImageExtension(bytes);
   string path=Path.Combine(Path.GetTempPath(),"MoriDesk-drop-"+Guid.NewGuid().ToString("N")+extension);
   try{using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))stream.Write(bytes,0,bytes.Length);return path;}
   catch{Cleanup(path);throw;}
  }
  internal static void Cleanup(string path){try{if(path!=null&&File.Exists(path))File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
 }
}
