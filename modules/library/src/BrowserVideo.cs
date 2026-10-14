using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Mori {
 // The embedded page has access to exactly one file, not a library directory.
 // Byte ranges preserve seeking without buffering whole videos in memory.
 internal sealed class VideoRange {
  internal long Start,Length;internal int Status;
  internal static VideoRange Parse(long length,string range){
   var value=new VideoRange{Start=0,Length=length,Status=200};if(string.IsNullOrEmpty(range))return value;
   value.Status=416;value.Length=0;if(length<=0||!range.StartsWith("bytes=",StringComparison.OrdinalIgnoreCase)||range.Contains(","))return value;
   var parts=range.Substring(6).Split('-');if(parts.Length!=2)return value;long start,end;
   if(parts[0].Length==0){long tail;if(!long.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out tail)||tail<=0)return value;start=Math.Max(0,length-tail);end=length-1;}
   else{if(!long.TryParse(parts[0],NumberStyles.None,CultureInfo.InvariantCulture,out start)||start>=length)return value;if(parts[1].Length==0)end=length-1;else if(!long.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out end)||end<start)return value;end=Math.Min(end,length-1);}
   value.Start=start;value.Length=end-start+1;value.Status=206;return value;
  }
 }
 internal sealed class VideoSlice : Stream {
  readonly string file;readonly long start,length;long position;bool closed;
  internal bool Closed {get{return closed;}}
  internal VideoSlice(string file,long offset,long size){this.file=file;start=offset;length=size;}
  public override bool CanRead{get{return !closed;}}public override bool CanSeek{get{return !closed;}}public override bool CanWrite{get{return false;}}
  public override long Length{get{return length;}}public override long Position{get{return position;}set{Seek(value,SeekOrigin.Begin);}}
  public override int Read(byte[] buffer,int offset,int count){if(closed)throw new ObjectDisposedException("VideoSlice");if(position==length)return 0;using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){input.Position=start+position;int n=input.Read(buffer,offset,(int)Math.Min(count,length-position));position+=n;return n;}}
  public override long Seek(long offset,SeekOrigin origin){if(closed)throw new ObjectDisposedException("VideoSlice");long next=origin==SeekOrigin.Begin?offset:origin==SeekOrigin.Current?position+offset:length+offset;if(next<0||next>length)throw new IOException("Invalid video range");return position=next;}
  public override void Flush(){}public override void SetLength(long value){throw new NotSupportedException();}public override void Write(byte[] buffer,int offset,int count){throw new NotSupportedException();}
  protected override void Dispose(bool disposing){closed=true;base.Dispose(disposing);}
 }
 internal sealed class BrowserVideo : IDisposable {
  internal const string PageUrl="https://mori-media.invalid/player",ClipUrl="https://mori-media.invalid/clip";
  internal readonly WebView2 View=new WebView2{DefaultBackgroundColor=System.Drawing.Color.FromArgb(23,25,28),AllowExternalDrop=false};
  readonly string file;readonly bool audio;readonly List<Stream> responses=new List<Stream>();readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=3*1024*1024};
  CoreWebView2Environment environment;bool closed;internal bool Ready {get;private set;}
  internal int BrowserProcessId {get;private set;}internal int DeniedRequests {get;private set;}
  internal string LastError {get;private set;}
  internal event Action<double,int,int> Opened;internal event Action<double> PositionChanged;internal event Action Ended,Failed,CloseRequested;internal event Action<string> Key;
  internal event Action<byte[],int,int> Thumbnail;
  internal BrowserVideo(string path){file=Path.GetFullPath(path);audio=MediaTypes.Audio.Contains(Path.GetExtension(file));}
  internal static string MimeType(string path){switch(Path.GetExtension(path).ToLowerInvariant()){
   case ".webm":return "video/webm";case ".mov":return "video/quicktime";case ".avi":return "video/x-msvideo";case ".mkv":return "video/x-matroska";case ".wmv":return "video/x-ms-wmv";case ".mpeg":case ".mpg":return "video/mpeg";case ".mts":case ".m2ts":case ".ts":return "video/mp2t";case ".3gp":return "video/3gpp";case ".ogv":return "video/ogg";
   case ".mp3":return "audio/mpeg";case ".wav":return "audio/wav";case ".m4a":return "audio/mp4";case ".aac":return "audio/aac";case ".flac":return "audio/flac";case ".ogg":case ".opus":return "audio/ogg";case ".wma":return "audio/x-ms-wma";case ".aif":case ".aiff":return "audio/aiff";default:return "video/mp4";
  }}
  internal static bool Available {get{try{return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());}catch{return false;}}}
  internal async Task Start(){
   try{
    if(closed)return;if(!Available)throw new NotSupportedException("WebView2 Runtime not installed");
    string profile=Environment.GetEnvironmentVariable("MORI_TEST_MEDIA_PROFILE");if(string.IsNullOrEmpty(profile))profile=Path.Combine(Settings.ConfigDir,"video-runtime");
    environment=await CoreWebView2Environment.CreateAsync(null,profile,new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --disable-background-networking --disk-cache-size=16777216"));if(closed)return;
    await View.EnsureCoreWebView2Async(environment);if(closed)return;var core=View.CoreWebView2;BrowserProcessId=(int)core.BrowserProcessId;
    core.Settings.AreDefaultContextMenusEnabled=false;core.Settings.AreDevToolsEnabled=false;core.Settings.AreBrowserAcceleratorKeysEnabled=false;core.Settings.AreDefaultScriptDialogsEnabled=false;core.Settings.IsStatusBarEnabled=false;core.Settings.IsZoomControlEnabled=false;core.Settings.AreHostObjectsAllowed=false;core.Settings.IsGeneralAutofillEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;
    core.NavigationStarting+=delegate(object sender,CoreWebView2NavigationStartingEventArgs e){if(e.Uri!=PageUrl)e.Cancel=true;};
    core.NewWindowRequested+=delegate(object sender,CoreWebView2NewWindowRequestedEventArgs e){e.Handled=true;};
    core.PermissionRequested+=delegate(object sender,CoreWebView2PermissionRequestedEventArgs e){e.State=CoreWebView2PermissionState.Deny;};
    core.DownloadStarting+=delegate(object sender,CoreWebView2DownloadStartingEventArgs e){e.Cancel=true;};
    core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);core.WebResourceRequested+=Resource;
    core.WebMessageReceived+=Message;core.ProcessFailed+=delegate{Failure();};core.Navigate(PageUrl);
   }catch(Exception ex){LastError=ex.GetType().Name+": "+ex.Message;if(!closed)Failure();}
  }
  internal static bool AllowedRequest(string uri,string method){return method=="GET"&&(uri==PageUrl||uri==ClipUrl);}
  void Resource(object sender,CoreWebView2WebResourceRequestedEventArgs e){
   try{
    if(closed||!AllowedRequest(e.Request.Uri,e.Request.Method)){DeniedRequests++;e.Response=environment.CreateWebResourceResponse(null,403,"Forbidden","");return;}
    if(e.Request.Uri==PageUrl){var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("Mori.Video.html");responses.Add(stream);e.Response=environment.CreateWebResourceResponse(stream,200,"OK","Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");return;}
    long size=new FileInfo(file).Length;string header=e.Request.Headers.Contains("Range")?e.Request.Headers.GetHeader("Range"):null;var range=VideoRange.Parse(size,header);
    if(range.Status==416){e.Response=environment.CreateWebResourceResponse(null,416,"Range Not Satisfiable","Content-Range: bytes */"+size);return;}
    // A canceled seek leaves no open file handle; WebView2 owns this stream's lifetime.
    var body=new VideoSlice(file,range.Start,range.Length);
    string mime=MimeType(file);
    string headers="Content-Type: "+mime+"\r\nAccept-Ranges: bytes\r\nCache-Control: no-store\r\nContent-Length: "+range.Length;
    if(range.Status==206)headers+="\r\nContent-Range: bytes "+range.Start+"-"+(range.Start+range.Length-1)+"/"+size;
    e.Response=environment.CreateWebResourceResponse(body,range.Status,range.Status==206?"Partial Content":"OK",headers);
   }catch{e.Response=environment.CreateWebResourceResponse(null,404,"Not Found","");}
  }
  void Message(object sender,CoreWebView2WebMessageReceivedEventArgs e){
   if(closed||e.Source!=PageUrl)return;
   try{var data=json.Deserialize<Dictionary<string,object>>(e.WebMessageAsJson);object kind;if(data==null||!data.TryGetValue("type",out kind))return;string type=kind as string;
    if(type=="ready"){double duration=Convert.ToDouble(data["duration"],CultureInfo.InvariantCulture);int width=Convert.ToInt32(data["width"]),height=Convert.ToInt32(data["height"]);if(duration<=0||double.IsInfinity(duration)||double.IsNaN(duration)||(!audio&&(width<=0||height<=0)))throw new InvalidDataException();Ready=true;if(Opened!=null)Opened(duration,width,height);}
    else if(type=="position"&&PositionChanged!=null)PositionChanged(Convert.ToDouble(data["position"],CultureInfo.InvariantCulture));
    else if(type=="ended"&&Ended!=null)Ended();else if(type=="close"&&CloseRequested!=null)CloseRequested();else if(type=="error")Failure();
    else if(type=="key"&&Key!=null)Key(data["key"] as string);
    else if(type=="thumbnail"&&!audio&&Thumbnail!=null)Thumbnail(Convert.FromBase64String((string)data["png"]),Convert.ToInt32(data["width"]),Convert.ToInt32(data["height"]));
   }catch{Failure();}
  }
  internal void Command(string type,double position=0){if(closed||!Ready)return;try{View.CoreWebView2.PostWebMessageAsJson(json.Serialize(new{type=type,position=position}));}catch{Failure();}}
  internal void SetVolume(double volume){if(closed||!Ready)return;try{View.CoreWebView2.PostWebMessageAsJson(json.Serialize(new{type="volume",volume=VideoVolume.Clamp(volume)}));}catch{Failure();}}
  void Failure(){if(closed)return;Ready=false;if(Failed!=null)Failed();}
  public void Dispose(){if(closed)return;closed=true;Ready=false;View.Dispose();foreach(var stream in responses)stream.Dispose();responses.Clear();environment=null;}
 }
}
