using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Media.Imaging;

namespace Mori {
 public static class NativeBridge {
  const int Maximum=29*1024*1024;
  static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=Maximum};
  static byte[] Read(Stream stream,int count){byte[] b=new byte[count];int at=0,n;while(at<count&&(n=stream.Read(b,at,count-at))>0)at+=n;if(at!=count)throw new EndOfStreamException();return b;}
  public static int Run(string testRoot=null,string origin=null){try{using(var input=Console.OpenStandardInput())using(var output=Console.OpenStandardOutput()){while(true){byte[] header;try{header=Read(input,4);}catch(EndOfStreamException){break;}int length=BitConverter.ToInt32(header,0);if(length<1||length>Maximum)return 1;object response;try{var request=Json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(Read(input,length)));request["_extensionId"]=MoriDesk.BrowserConnection.IdFromOrigin(origin);response=HandleAtCurrentLibrary(request,testRoot);}catch(Exception ex){response=new{ok=false,error=ex.Message};}byte[] body=Encoding.UTF8.GetBytes(Json.Serialize(response));output.Write(BitConverter.GetBytes(body.Length),0,4);output.Write(body,0,body.Length);output.Flush();}}return 0;}catch{return 1;}}
  internal static object HandleAtCurrentLibrary(Dictionary<string,object> request,string testRoot,Func<Settings> loadSettings=null){
   // Connection/update messages are independent of library selection. In
   // particular they must not initialize an empty database when a disk is away.
   if(Get(request,"action")=="bridgeStatus"||Get(request,"action")=="extensionControl")return MoriDesk.BrowserExtensionManager.Handle(request);
   if(loadSettings==null)loadSettings=Settings.Load;
   for(int attempt=0;attempt<3;attempt++){
    string root=testRoot??loadSettings().LibraryPath;
    using(var lease=LibraryTransfer.TryAcquireWrite(root)){
     if(lease==null)throw new IOException("Nook 正在迁移图库或接收其他收藏，请完成后重试。");
     // A request may have loaded the old location just before migration finished.
     // Re-read after acquiring its lease rather than silently writing to that copy.
     if(testRoot==null){
      var current=loadSettings();
      if(!LibraryTransfer.SameRoot(root,current.LibraryPath))continue;
      Program.ValidateConfiguredLibrary(current);
     }
     using(var library=new Library(root))return Handle(library,request);
    }
   }
   throw new IOException("图库位置正在切换，请稍后重试收藏。");
  }
  static string Get(Dictionary<string,object> request,string key){object value;return request.TryGetValue(key,out value)?Convert.ToString(value):"";}
  static object SaveBookmark(Library library,Dictionary<string,object> request){
   string url=Library.BookmarkAddress(Get(request,"source")),title=Get(request,"name");if(string.IsNullOrWhiteSpace(title))title=new Uri(url).Host;if(title.Length>250)title=title.Substring(0,250);
   string choice=Get(request,"duplicateAction");if(choice=="")choice="ask";
   var result=library.ImportBookmark(url,title,Get(request,"folder"),"","",choice);
   if(result.ChoiceRequired){var a=result.Item;return new{ok=true,duplicate=true,choiceRequired=true,existing=new{id=a.Id,name=a.Name,bytes=a.Bytes,width=0,height=0,type="网址",source=a.Source,thumbnail="",deleted=a.Deleted},incoming=new{name=title,bytes=a.Bytes,width=0,height=0,type="网址",source=url,thumbnail=""}};}
   BookmarkIcons.Remember(library.Root,url,Get(request,"iconUrl"));
   return new{ok=true,duplicate=result.Duplicate,choiceRequired=false,keptCopy=result.KeptCopy,id=result.Item.Id,name=result.Item.Name};
  }
  public static object Handle(Library library,Dictionary<string,object> request){string action=Get(request,"action");if(action=="folders"){var categories=library.Folders(Get(request,"scope")=="bookmark");return new{ok=true,theme=Settings.Load().Theme,folders=categories.Select(c=>new{id=c.Id,name=Label(c,categories)}).Take(500).ToArray()};}if(action=="importBookmarks")return BrowserBookmarkImport.Handle(library,request);if(action=="bookmark")return SaveBookmark(library,request);if(action!="save")throw new InvalidDataException("未知请求");
   string duplicateAction=Get(request,"duplicateAction");if(duplicateAction=="")duplicateAction="ask";if(duplicateAction!="ask"&&duplicateAction!="reuse"&&duplicateAction!="keep")throw new InvalidDataException("无效的重复文件处理方式");
   string expectedKind=Get(request,"kind");if(expectedKind!=""&&expectedKind!="image"&&expectedKind!="video")throw new InvalidDataException("无效的素材类型");
   string data=Get(request,"data");if(data.Length>28*1024*1024)throw new InvalidDataException("素材超过 20 MB，请保存原文件后拖入 Nook");byte[] image=Convert.FromBase64String(data);if(image.Length>20*1024*1024)throw new InvalidDataException("素材超过 20 MB，请保存原文件后拖入 Nook");if(image.Length<12)throw new InvalidDataException("素材为空或不完整，本次没有收藏。");string ext=Detect(image);if(ext==null)throw new InvalidDataException("网页没有返回受支持的图片或视频（JPG / PNG / GIF / WebP / MP4 / WebM）；不支持流媒体播放列表");
   // A successful HTTP response or a video-looking URL is not proof of a video.
   // Validate before writing any temporary file, importing, or reusing a
   // duplicate. Missing kind remains compatible with older image collectors.
   if(expectedKind!=""){
    bool actualVideo=WebMediaFormat.DetectVideo(image)!=null;
    if(expectedKind=="video"&&!actualVideo)throw new InvalidDataException("网页返回了图片而不是可保存的视频，本次没有收藏，也没有改存为封面。");
    if(expectedKind=="image"&&actualVideo)throw new InvalidDataException("网页返回了视频而不是所选图片，本次没有收藏。");
   }
   // A brand/EBML header is only a classifier. Require bounded container,
   // video-track and media-block structure for both current and legacy clients.
   // No platform decoder is used: an unsupported codec is still collectable.
   if(WebMediaFormat.DetectVideo(image)!=null&&!WebMediaFormat.HasCompleteVideo(image))throw new InvalidDataException("网页返回的视频数据不完整或没有有效视频轨，本次没有收藏。请在原网页播放后重试，或下载原文件再拖入 Nook。");
   string source=Get(request,"source");Uri uri;if(source.Length>8000||source.Any(char.IsControl)||(!string.IsNullOrEmpty(source)&&!SourceLink.TryParse(source,out uri)))throw new InvalidDataException("来源网址须为不含账号密码的 HTTP / HTTPS 地址，本次没有收藏。");
   string title=Get(request,"name");if(title.Length>250)title=title.Substring(0,250);if(string.IsNullOrWhiteSpace(title))title="网页收藏";
   string temp=Path.Combine(Path.GetTempPath(),"Mori-web-"+Guid.NewGuid().ToString("N")+ext);try{
    File.WriteAllBytes(temp,image);var result=library.Import(temp,Get(request,"folder"),title,source,"","","",duplicateAction);
    if(result.ChoiceRequired){
     int width,height;string thumbnail=ComparisonThumbnail(temp,out width,out height);var existing=result.Item;
     return new{ok=true,duplicate=true,choiceRequired=true,
      existing=new{id=existing.Id,name=existing.Name,bytes=existing.Bytes,width=existing.Width>0?existing.Width:width,height=existing.Height>0?existing.Height:height,type=existing.TypeLabel,source=existing.Source,thumbnail=thumbnail,deleted=existing.Deleted},
      incoming=new{name=title,bytes=image.LongLength,width=width,height=height,type=ext.TrimStart('.').ToUpperInvariant(),thumbnail=thumbnail}};
    }
    return new{ok=true,duplicate=result.Duplicate,choiceRequired=false,keptCopy=result.KeptCopy,id=result.Item.Id,name=result.Item.Name};
   }finally{if(File.Exists(temp))File.Delete(temp);}
  }
  // The prompt preview is generated only in memory: asking about a duplicate
  // must not alter the original's thumbnail/index or create another library file.
  static string ComparisonThumbnail(string path,out int width,out int height){
   width=height=0;
   try{
    using(var stream=File.OpenRead(path)){var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.DelayCreation,BitmapCacheOption.None);width=decoder.Frames[0].PixelWidth;height=decoder.Frames[0].PixelHeight;}
    if(width<1||height<1||width>100000||height>100000)return "";
    double scale=Math.Min(1,240.0/Math.Max(width,height));var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;image.DecodePixelWidth=Math.Max(1,(int)(width*scale));image.DecodePixelHeight=Math.Max(1,(int)(height*scale));image.UriSource=new Uri(path);image.EndInit();image.Freeze();
    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var output=new MemoryStream()){encoder.Save(output);return output.Length<=256*1024?"data:image/png;base64,"+Convert.ToBase64String(output.ToArray()):"";}
   }catch(NotSupportedException){}catch(FileFormatException){}catch(IOException){}catch(ArgumentException){}catch(InvalidOperationException){}
   return "";
  }
  public static string Detect(byte[] b){if(b==null||b.Length<12)return null;if(b[0]==137&&b[1]==80&&b[2]==78&&b[3]==71&&b[4]==13&&b[5]==10&&b[6]==26&&b[7]==10)return ".png";if(b[0]==255&&b[1]==216&&b[2]==255)return ".jpg";string start=Encoding.ASCII.GetString(b,0,6);if(start=="GIF87a"||start=="GIF89a")return ".gif";if(Encoding.ASCII.GetString(b,0,4)=="RIFF"&&Encoding.ASCII.GetString(b,8,4)=="WEBP")return ".webp";return WebMediaFormat.DetectVideo(b);}
  static string Label(Category c,List<Category> categories){string label=c.Name;var seen=new HashSet<string>{c.Id};while(c.Parent!=""){c=categories.FirstOrDefault(f=>f.Id==c.Parent);if(c==null||!seen.Add(c.Id))break;label=c.Name+" / "+label;}return label;}
 }
}
