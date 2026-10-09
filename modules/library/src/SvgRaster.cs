using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Windows.Media.Imaging;

namespace Mori {
 internal static class SvgRaster {
  internal const long MaximumInput=8*1024*1024;
  static readonly SemaphoreSlim Workers=new SemaphoreSlim(2);
  static readonly object failureLock=new object();
  static readonly Dictionary<string,DateTime> failures=new Dictionary<string,DateTime>();
  // Rendering is isolated: a malformed/recursive/expensive SVG cannot crash the
  // gallery or keep a render worker indefinitely. No external resources execute.
  internal static BitmapSource Render(string path,int longest,out int width,out int height){
   string key=Path.GetFullPath(path)+"|"+File.GetLastWriteTimeUtc(path).Ticks;DateTime failed;
   lock(failureLock)if(longest<=512&&failures.TryGetValue(key,out failed)&&failed>DateTime.UtcNow.AddMinutes(-10))throw new NotSupportedException("此 SVG 暂时无法生成缩略图。");
   Workers.Wait();
   try{
    var info=new FileInfo(path);if(!info.Exists||info.Length>MaximumInput)throw new NotSupportedException("SVG 文件过大，内置预览上限为 8 MB。");
    var start=new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--svg-render \""+info.FullName+"\" "+Math.Max(1,Math.Min(2048,longest))){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardOutput=true};
    using(var process=Process.Start(start)){
     var bytes=Task.Run(delegate{using(var data=new MemoryStream()){byte[] buffer=new byte[16384];int n;while((n=process.StandardOutput.BaseStream.Read(buffer,0,buffer.Length))>0){if(data.Length+n>20*1024*1024)throw new InvalidDataException("SVG 预览输出过大。");data.Write(buffer,0,n);}return data.ToArray();}});
     bool stopped=false;var clock=Stopwatch.StartNew();
     try{while(!process.WaitForExit(50)){long memory=0;try{process.Refresh();memory=process.PrivateMemorySize64;}catch(InvalidOperationException){if(process.HasExited)break;throw;}if(clock.ElapsedMilliseconds>8000||memory>256L*1024*1024){stopped=true;process.Kill();process.WaitForExit(2000);break;}}if(stopped||process.ExitCode!=0)throw new NotSupportedException("SVG 过于复杂或格式不受支持，无法安全预览。");
      var result=bytes.GetAwaiter().GetResult();using(var data=new MemoryStream(result))using(var reader=new BinaryReader(data)){
       width=reader.ReadInt32();height=reader.ReadInt32();if(width<1||height<1||width>1000000||height>1000000)throw new InvalidDataException("SVG 尺寸无效。");
       using(var png=new MemoryStream(result,8,result.Length-8)){var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=png;bitmap.EndInit();bitmap.Freeze();if(bitmap.PixelWidth>2048||bitmap.PixelHeight>2048)throw new InvalidDataException("SVG 输出尺寸无效。");return bitmap;}
      }
     }finally{if(!process.HasExited){process.Kill();process.WaitForExit(2000);}try{bytes.Wait(2000);}catch(AggregateException){}}
    }
   }catch{if(longest<=512)lock(failureLock){if(failures.Count>=128)failures.Clear();failures[key]=DateTime.UtcNow;}throw;}finally{Workers.Release();}
  }
  internal static XmlDocument SafeDocument(string path){
   var document=new XmlDocument{XmlResolver=null};
   using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(input.Length>MaximumInput)throw new InvalidDataException("SVG 文件过大。");
    // Ignore ordinary Illustrator SVG doctypes, but never resolve a DTD or its entities.
    using(var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null,MaxCharactersInDocument=MaximumInput,MaxCharactersFromEntities=1,IgnoreProcessingInstructions=true})){
     int count=0;while(reader.Read()){if(reader.Depth>96||++count>100000)throw new InvalidDataException("SVG 结构过于复杂。");}
    }
   }
   using(var input=File.OpenRead(path))using(var reader=XmlReader.Create(input,new XmlReaderSettings{DtdProcessing=DtdProcessing.Ignore,XmlResolver=null,MaxCharactersInDocument=MaximumInput,MaxCharactersFromEntities=1,IgnoreProcessingInstructions=true}))document.Load(reader);
   if(document.DocumentElement==null||document.DocumentElement.LocalName!="svg")throw new InvalidDataException("不是 SVG 图片。");
   Clean(document.DocumentElement);return document;
  }
  static readonly Regex Url=new Regex(@"url\s*\(([^)]*)\)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
  static string SafeStyle(string value){if(value.IndexOf('\\')>=0||value.IndexOf('@')>=0)return "";return Url.Replace(value,m=>{string target=m.Groups[1].Value.Trim().Trim('\'', '"');return target.StartsWith("#",StringComparison.Ordinal)?"url("+target+")":"none";});}
  static void Clean(XmlElement element){
   foreach(XmlAttribute attribute in element.Attributes.Cast<XmlAttribute>().ToArray()){
    string name=attribute.LocalName;
    if(name.StartsWith("on",StringComparison.OrdinalIgnoreCase)||name=="base"||name=="src"||(name=="href"&&!attribute.Value.TrimStart().StartsWith("#",StringComparison.Ordinal))){element.RemoveAttributeNode(attribute);continue;}
    if(!attribute.Name.StartsWith("xmlns",StringComparison.Ordinal))attribute.Value=SafeStyle(attribute.Value);
   }
   if(element.LocalName=="style")element.InnerText=SafeStyle(element.InnerText);
   foreach(XmlElement child in element.ChildNodes.OfType<XmlElement>().ToArray()){
    if(new[]{"script","foreignObject","iframe","object","embed","audio","video","animate","animateMotion","animateTransform","set","image","feImage"}.Contains(child.LocalName)){element.RemoveChild(child);continue;}
    Clean(child);
   }
  }
  internal static Bitmap RenderWorker(string path,int longest,out int width,out int height){
   Svg.SvgDocument.DisableDtdProcessing=true;
   Svg.SvgDocument.ResolveExternalXmlEntites=Svg.ExternalType.None;
   Svg.SvgDocument.ResolveExternalImages=Svg.ExternalType.None;
   Svg.SvgDocument.ResolveExternalElements=Svg.ExternalType.None;
   Svg.SvgDocument.PointsPerInch=96;
   var doc=Svg.SvgDocument.Open(SafeDocument(path));var size=doc.GetDimensions();
   if(float.IsNaN(size.Width)||float.IsNaN(size.Height)||float.IsInfinity(size.Width)||float.IsInfinity(size.Height)||size.Width<=0||size.Height<=0||size.Width>1000000||size.Height>1000000)throw new InvalidDataException("SVG 尺寸无效。");
   width=Math.Max(1,(int)Math.Ceiling(size.Width));height=Math.Max(1,(int)Math.Ceiling(size.Height));
   double scale=longest/Math.Max(size.Width,size.Height);int w=Math.Max(1,(int)Math.Round(size.Width*scale)),h=Math.Max(1,(int)Math.Round(size.Height*scale));
   var result=doc.Draw(w,h);if(result==null)throw new InvalidDataException("无法绘制 SVG。");return result;
  }
  internal static int Worker(string[] args){try{int longest;if(args.Length!=3||!int.TryParse(args[2],out longest)||longest<1||longest>2048)return 2;int width,height;using(var bitmap=RenderWorker(args[1],longest,out width,out height))using(var output=Console.OpenStandardOutput())using(var writer=new BinaryWriter(output)){writer.Write(width);writer.Write(height);bitmap.Save(output,ImageFormat.Png);}return 0;}catch{return 2;}}
 }
}
