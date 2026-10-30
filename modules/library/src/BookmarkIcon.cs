using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mori {
 // Optional bounded cache. Discovery reads only the public origin's home page,
 // never the saved path/query, cookies, or a third-party favicon service.
 internal static class BookmarkIcons {
  internal const int MaximumBytes=128*1024,MaximumFiles=768;
  internal static bool NetworkEnabled=true;
  static readonly object sync=new object();
  static readonly SemaphoreSlim workers=new SemaphoreSlim(2);
  static readonly Dictionary<string,Task<BitmapSource>> pending=new Dictionary<string,Task<BitmapSource>>();
  internal static string Key(string address){
   var uri=SourceLink.Parse(address);string origin=uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
   using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(origin))).Replace("-","");
  }
  internal static string PathFor(string root,string address){return Path.Combine(root,".mori","site-icons",Key(address));}
  internal static bool Allowed(Uri uri){
   if(uri==null||(uri.Scheme!="https"&&uri.Scheme!="http")||!uri.IsDefaultPort||!string.IsNullOrEmpty(uri.UserInfo)||uri.AbsoluteUri.Length>2048)return false;
   string host=uri.DnsSafeHost.TrimEnd('.').ToLowerInvariant();IPAddress ip;
   if(IPAddress.TryParse(host,out ip))return Public(ip);
   return host.Contains('.')&&!host.EndsWith(".localhost")&&!host.EndsWith(".local")&&!host.EndsWith(".internal")&&!host.EndsWith(".test")&&!host.EndsWith(".invalid");
  }
  internal static bool Public(IPAddress ip){
   if(ip.IsIPv4MappedToIPv6)return Public(ip.MapToIPv4());
   var b=ip.GetAddressBytes();
   if(ip.AddressFamily==AddressFamily.InterNetworkV6)return (b[0]&0xe0)==0x20; // global unicast only
   return b.Length==4&&b[0]!=0&&b[0]!=10&&b[0]!=127&&b[0]<224&&!(b[0]==169&&b[1]==254)&&!(b[0]==172&&b[1]>=16&&b[1]<=31)&&!(b[0]==192&&b[1]==168)&&!(b[0]==100&&b[1]>=64&&b[1]<=127)&&!(b[0]==198&&(b[1]==18||b[1]==19));
  }
  internal static void Remember(string root,string address,string hint){
   try{
    Uri page=SourceLink.Parse(address),icon;if(!Allowed(page)||string.IsNullOrWhiteSpace(hint)||!SourceLink.TryParse(hint,out icon)||!Allowed(icon))return;
    string path=PathFor(root,address)+".hint";
    if(File.Exists(path)&&File.ReadAllText(path)==icon.AbsoluteUri)return;
    Write(path,Encoding.UTF8.GetBytes(icon.AbsoluteUri));
   }catch{ /* Cache failure never changes the collection result. */ }
  }
  internal static Task<BitmapSource> GetAsync(string root,string address){
   string key;try{key=PathFor(root,address);}catch{return Task.FromResult<BitmapSource>(null);}
   lock(sync){
    Task<BitmapSource> existing;if(pending.TryGetValue(key,out existing))return existing;
    if(pending.Count>=48)return Task.FromResult<BitmapSource>(null);
    var task=LoadQueuedAsync(root,address);pending.Add(key,task);
    // Register after insertion, under the same lock: even an immediately
    // completed cache read cannot leave a completed task in the pending map.
    task.ContinueWith(completed=>{lock(sync)pending.Remove(key);},TaskScheduler.Default);return task;
   }
  }
  static async Task<BitmapSource> LoadQueuedAsync(string root,string address){
   // Only the two active reads use pool threads. Queued favicon requests must
   // not occupy threads needed by image previews, conversions or DNS replies.
   await workers.WaitAsync().ConfigureAwait(false);
   try{return await Task.Run(()=>Load(root,address,NetworkEnabled?(Func<Uri,byte[]>)Download:null)).ConfigureAwait(false);}
   catch{return null;}
   finally{workers.Release();}
  }
  internal static BitmapSource Load(string root,string address,Func<Uri,byte[]> fetch){
   string stem=PathFor(root,address),png=stem+".png",hint=stem+".hint",miss=stem+".miss2";BitmapSource cached=null;
   if(File.Exists(png)&&new FileInfo(png).Length<=MaximumBytes){try{cached=Decode(File.ReadAllBytes(png));}catch{}}
   bool newerHint=File.Exists(hint)&&(!File.Exists(png)||File.GetLastWriteTimeUtc(hint)>File.GetLastWriteTimeUtc(png));
   if(cached!=null&&!newerHint&&File.GetLastWriteTimeUtc(png)>DateTime.UtcNow.AddDays(-30))return cached;
   if(fetch==null)return cached;
   if(File.Exists(miss)&&File.GetLastWriteTimeUtc(miss)>DateTime.UtcNow.AddDays(-1)&&(!File.Exists(hint)||File.GetLastWriteTimeUtc(hint)<=File.GetLastWriteTimeUtc(miss)))return cached;
   var page=SourceLink.Parse(address);if(!Allowed(page))return cached;
   var candidates=new List<Uri>();Uri preferred;
   if(File.Exists(hint)&&new FileInfo(hint).Length<=2048&&SourceLink.TryParse(File.ReadAllText(hint),out preferred)&&Allowed(preferred))candidates.Add(preferred);
   var fallback=new Uri(page.GetLeftPart(UriPartial.Authority)+"/favicon.ico");if(!candidates.Any(u=>u==fallback))candidates.Add(fallback);
   Func<Uri,BitmapSource> attempt=candidate=>{try{
    var result=DecodeCandidate(fetch(candidate),candidate);try{var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(result));using(var bytes=new MemoryStream()){encoder.Save(bytes);Write(png,bytes.ToArray());}}catch{}return result;
   }catch{return null;}};
   foreach(var candidate in candidates){var result=attempt(candidate);if(result!=null)return result;}
   var origin=new Uri(page.GetLeftPart(UriPartial.Authority)+"/");
   try{foreach(var candidate in Discover(origin,fetch(origin)).Where(u=>!candidates.Contains(u)).Take(4)){var result=attempt(candidate);if(result!=null)return result;}}catch{}
   var apple=new Uri(origin,"apple-touch-icon.png");var touch=attempt(apple);if(touch!=null)return touch;
   try{Write(miss,new byte[]{0});}catch{}return cached;
  }
  internal static IEnumerable<Uri> Discover(Uri origin,byte[] data){
   if(data==null||data.Length>MaximumBytes)return new Uri[0];string html=Encoding.UTF8.GetString(data);int end=html.IndexOf("</head",StringComparison.OrdinalIgnoreCase);if(end>=0)html=html.Substring(0,end);
   var result=new List<Uri>();foreach(Match tag in Regex.Matches(html,@"<link\b[^>]{1,4096}>",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(250))){string rel=BookmarkFileImport.Attribute(tag.Value,"rel");if(!rel.Split(' ').Any(p=>p.Equals("icon",StringComparison.OrdinalIgnoreCase)||p.Equals("apple-touch-icon",StringComparison.OrdinalIgnoreCase)))continue;Uri candidate;if(Uri.TryCreate(origin,BookmarkFileImport.Attribute(tag.Value,"href"),out candidate)&&Allowed(candidate)&&!result.Contains(candidate))result.Add(candidate);if(result.Count==8)break;}
   return result;
  }
  internal static BitmapSource DecodeCandidate(byte[] data,Uri candidate){
   if(data==null||data.Length>MaximumBytes)throw new InvalidDataException("Icon too large");
   if(!candidate.AbsolutePath.EndsWith(".svg",StringComparison.OrdinalIgnoreCase))return Decode(data);
   string path=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Mori-icon-"+Guid.NewGuid().ToString("N")+".svg");
   try{File.WriteAllBytes(path,data);int width,height;return SvgRaster.Render(path,64,out width,out height);}finally{if(File.Exists(path))File.Delete(path);}
  }
  internal static BitmapSource Decode(byte[] data){
   if(data==null||data.Length<12||data.Length>MaximumBytes)throw new InvalidDataException("Invalid icon size");
   bool ico=data[0]==0&&data[1]==0&&data[2]==1&&data[3]==0;
   string format=NativeBridge.Detect(data);if(!ico&&format!=".png"&&format!=".jpg"&&format!=".gif")throw new InvalidDataException("Unsupported icon format");
   using(var stream=new MemoryStream(data)){
    var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);
    var frame=decoder.Frames[0];int w=frame.PixelWidth,h=frame.PixelHeight;
    if(w<1||h<1||w>1024||h>1024)throw new InvalidDataException("Icon dimensions too large");
    stream.Position=0;var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.StreamSource=stream;bitmap.DecodePixelWidth=Math.Max(1,(int)Math.Round(w*Math.Min(1,64.0/Math.Max(w,h))));bitmap.EndInit();bitmap.Freeze();return bitmap;
   }
  }
  internal static void Write(string path,byte[] data){
   string directory=Path.GetDirectoryName(path);Directory.CreateDirectory(directory);string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{File.WriteAllBytes(temp,data);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
   finally{if(File.Exists(temp))File.Delete(temp);}
   // This directory contains only disposable website-icon cache files.
   var files=new DirectoryInfo(directory).GetFiles().Where(f=>f.Extension==".png"||f.Extension==".hint"||f.Extension==".miss"||f.Extension==".miss2").OrderByDescending(f=>f.LastWriteTimeUtc).ToArray();
   foreach(var file in files.Skip(MaximumFiles)){try{file.Delete();}catch(IOException){}}
  }
  internal static byte[] Download(Uri start){
   Uri uri=start;
   for(int redirect=0;redirect<3;redirect++){
    if(!Allowed(uri))throw new IOException("Private icon address not fetched");
    var lookup=Dns.GetHostAddressesAsync(uri.DnsSafeHost);if(!lookup.Wait(1500)||lookup.Result.Length==0||lookup.Result.Any(ip=>!Public(ip)))throw new IOException("Icon host unavailable");
    var request=(HttpWebRequest)WebRequest.Create(uri);request.AllowAutoRedirect=false;request.UseDefaultCredentials=false;request.Credentials=null;request.CookieContainer=null;request.Timeout=3000;request.ReadWriteTimeout=3000;request.UserAgent="Mori/0.2";request.Accept="image/png,image/x-icon,image/vnd.microsoft.icon,image/jpeg,image/gif";
    using(var deadline=new Timer(delegate{request.Abort();},null,4000,Timeout.Infinite))using(var response=(HttpWebResponse)request.GetResponse()){
     if((int)response.StatusCode>=300&&(int)response.StatusCode<400){Uri next;if(!Uri.TryCreate(uri,response.Headers["Location"],out next)||uri.Scheme=="https"&&next.Scheme!="https")throw new IOException("Unsafe icon redirect");uri=next;continue;}
     if(response.StatusCode!=HttpStatusCode.OK||response.ContentLength>MaximumBytes)throw new IOException("Invalid icon response");
     using(var input=response.GetResponseStream())using(var output=new MemoryStream()){
      byte[] buffer=new byte[4096];int count;while((count=input.Read(buffer,0,buffer.Length))>0){if(output.Length+count>MaximumBytes)throw new IOException("Icon response too large");output.Write(buffer,0,count);}return output.ToArray();
     }
    }
   }
   throw new IOException("Too many icon redirects");
  }
 }
 public sealed class BookmarkIcon:Grid {
  readonly TextBlock fallback;readonly Image icon;int generation;
  public BookmarkIcon(){
   Width=24;Height=24;IsHitTestVisible=false;
   fallback=new TextBlock{Text="\uE774",FontFamily=new FontFamily("Segoe MDL2 Assets"),FontSize=16,VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Center};fallback.SetResourceReference(TextBlock.ForegroundProperty,"Muted");Children.Add(fallback);
   icon=new Image{Stretch=Stretch.Uniform};Children.Add(icon);
   Loaded+=delegate{Load();};Unloaded+=delegate{generation++;icon.Source=null;fallback.Visibility=Visibility.Visible;};
   IsVisibleChanged+=delegate{if(IsVisible&&IsLoaded)Load();};
   DataContextChanged+=delegate{generation++;icon.Source=null;fallback.Visibility=Visibility.Visible;if(IsLoaded)Load();};
  }
  async void Load(){
   var asset=DataContext as Asset;var library=ThumbnailImage.CurrentLibrary;if(asset==null||!asset.IsBookmark||library==null||!IsVisible)return;
   int version=++generation;try{var bitmap=await BookmarkIcons.GetAsync(library.Root,asset.Source);if(!IsLoaded||version!=generation)return;icon.Source=bitmap;fallback.Visibility=bitmap==null?Visibility.Visible:Visibility.Collapsed;}catch{}
  }
 }
}
