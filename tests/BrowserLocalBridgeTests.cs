using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;
namespace MoriDesk {
 internal static class BrowserLocalBridgeTests {
  static string Send(int port,string text){using(var client=new TcpClient("127.0.0.1",port)){client.ReceiveTimeout=10000;using(var stream=client.GetStream()){byte[] data=Encoding.UTF8.GetBytes(text);stream.Write(data,0,data.Length);var header=new StringBuilder();while(!header.ToString().EndsWith("\r\n\r\n",StringComparison.Ordinal)){int next=stream.ReadByte();if(next<0)throw new EndOfStreamException();header.Append((char)next);}string length=header.ToString().Split(new[]{"\r\n"},StringSplitOptions.None).Single(x=>x.StartsWith("Content-Length:")).Substring(15).Trim();byte[] body=new byte[int.Parse(length)];int at=0;while(at<body.Length){int n=stream.Read(body,at,body.Length-at);if(n==0)throw new EndOfStreamException();at+=n;}return header+Encoding.UTF8.GetString(body);}}}
  static string Request(int port,string origin,string key,string body,string extra="",string method="POST"){return method+" /mori/v1 HTTP/1.1\r\nHost: 127.0.0.1:"+port+"\r\nOrigin: "+origin+"\r\nX-Mori-Token: "+key+"\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(body)+"\r\n"+extra+"\r\n"+body;}
  public static void Run(string root,Action<bool,string> check){
   string data=Path.Combine(root,"local-bridge"),extension=Path.Combine(root,"paired-extension"),id=new string('a',32),origin="chrome-extension://"+id;
   string secret=BrowserLocalBridge.Pair(data,extension);check(BrowserLocalBridge.ValidToken(secret),"bridge generates 256-bit key");check(BrowserLocalBridge.Pair(data,extension)==secret,"bridge key persists across updates");
   string configuration=Path.Combine(extension,"local-connection.js");var unchanged=new DateTime(2001,2,3,4,5,6,DateTimeKind.Utc);File.SetLastWriteTimeUtc(configuration,unchanged);BrowserLocalBridge.Pair(data,extension);check(File.GetLastWriteTimeUtc(configuration)==unchanged,"unchanged legacy pairing avoids repeated disk writes");
   string installationId=new string('c',32);BrowserLocalBridge.Pair(data,extension,installationId);check(File.ReadAllText(configuration)==BrowserLocalBridge.Configuration(secret,installationId),"managed pairing atomically includes installation identity in initial config");File.SetLastWriteTimeUtc(configuration,unchanged);BrowserLocalBridge.Pair(data,extension,installationId);check(File.GetLastWriteTimeUtc(configuration)==unchanged&&File.ReadAllText(configuration).Contains(installationId),"unchanged managed pairing neither rewrites nor temporarily drops identity");
   check(BrowserConnection.IdFromOrigin(origin)==id&&BrowserConnection.IdFromOrigin(origin+"/")==id,"native origin maps to an exact extension identity");
   check(BrowserConnection.IdFromOrigin(origin+".evil")==""&&BrowserConnection.IdFromOrigin("https://example.com")=="","untrusted origin never supplies extension identity");
   using(var identity=new BrowserLocalBridge(0,secret,value=>value==origin,request=>new{ok=true,id=Convert.ToString(request["_extensionId"])})){
    string forged="{\"action\":\"bridgeStatus\",\"_extensionId\":\""+new string('b',32)+"\"}";
    string proof=Send(identity.ListeningPort,Request(identity.ListeningPort,origin,secret,forged));
    check(proof.Contains("\"id\":\""+id+"\"")&&!proof.Contains(new string('b',32)),"HTTP identity comes from authenticated origin not message body");
   }
   bool loadedSettings=false;string untouched=Path.Combine(root,"no-status-library");
   try{Mori.NativeBridge.HandleAtCurrentLibrary(new Dictionary<string,object>{{"action","bridgeStatus"},{"_extensionId",""}},untouched,()=>{loadedSettings=true;throw new Exception("Status touched settings");});}catch(UnauthorizedAccessException){}catch(InvalidDataException){}
   check(!loadedSettings&&!Directory.Exists(untouched),"connection health never loads or initializes a media library");
   Handoff(secret,origin,check);
   string manifest=BrowserConnection.WriteManifest(data,typeof(Entry).Assembly.Location,id);check(BrowserLocalBridge.OriginAllowed(manifest,origin),"only paired origin accepted");foreach(string value in new[]{"https://example.com","null",origin+"/",origin+".evil", "chrome-extension://"+new string('b',32)})check(!BrowserLocalBridge.OriginAllowed(manifest,value),"unpaired origin rejected");
   int handled=0;string library=Path.Combine(root,"bridge-library");
   using(var bridge=new BrowserLocalBridge(0,secret,value=>BrowserLocalBridge.OriginAllowed(manifest,value),request=>{handled++;return Mori.NativeBridge.HandleAtCurrentLibrary(request,library);})){int port=bridge.ListeningPort;string body="{\"action\":\"folders\"}";
    string response=Send(port,Request(port,origin,secret,body));check(response.StartsWith("HTTP/1.1 200")&&response.Contains("\"ok\":true")&&handled==1,"authenticated HTTP routes to native handler");check(response.Contains("Access-Control-Allow-Origin: "+origin)&&!response.Contains("Allow-Origin: *"),"CORS only grants paired origin");
    foreach(string bad in new[]{"https://example.com","null","chrome-extension://"+new string('b',32)})check(Send(port,Request(port,bad,secret,body)).StartsWith("HTTP/1.1 403"),"website and unrelated extensions cannot read library");
    check(Send(port,Request(port,origin,new string('0',64),body)).StartsWith("HTTP/1.1 403"),"wrong key rejected");check(Send(port,Request(port,origin,"",body)).StartsWith("HTTP/1.1 403"),"missing key rejected");
    check(Send(port,Request(port,origin,secret,body).Replace("Host: 127.0.0.1:","Host: evil.example:")).StartsWith("HTTP/1.1 403"),"DNS rebinding host rejected");
    check(Send(port,Request(port,origin,secret,body,"Transfer-Encoding: chunked\r\n")).StartsWith("HTTP/1.1 400"),"chunked requests rejected");check(Send(port,Request(port,origin,secret,body,"Content-Length: 0\r\n")).StartsWith("HTTP/1.1 400"),"duplicate headers rejected");
    check(Send(port,Request(port,origin,secret,body).Replace("Content-Length: "+Encoding.UTF8.GetByteCount(body),"Content-Length: 40000000")).StartsWith("HTTP/1.1 400"),"oversized request rejected before reading body");
    check(Send(port,Request(port,origin,"","","Access-Control-Request-Method: POST\r\n","OPTIONS")).StartsWith("HTTP/1.1 204"),"paired preflight works without leaking library data");check(handled==1,"unauthorized traffic never reaches library");
    check(Send(port,Request(port,origin,secret,"{\"action\":\"delete\"}")).Contains("\"ok\":false")&&handled==1,"only collector actions allowed");
    check(Send(port,Request(port,origin,secret,"null")).Contains("\"ok\":false")&&handled==1,"invalid JSON root cannot reach library");
    var bytes=File.ReadAllBytes(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"qingjie-icon.png"));var request=new Dictionary<string,object>{{"action","save"},{"data",Convert.ToBase64String(bytes)},{"name","本机备用收藏测试"},{"source","https://example.com/bridge"},{"duplicateAction","ask"}};
    body=new JavaScriptSerializer{MaxJsonLength=BrowserLocalBridge.Maximum}.Serialize(request);response=Send(port,Request(port,origin,secret,body));check(response.Contains("\"ok\":true")&&response.Contains("本机备用收藏测试"),"real image collection over backup transport");
    response=Send(port,Request(port,origin,secret,body));check(response.Contains("\"choiceRequired\":true"),"backup transport preserves duplicate confirmation");
    using(var lib=new Mori.Library(library)){check(lib.All().Count==1&&lib.All()[0].Source=="https://example.com/bridge","actual save retains source and does not silently duplicate");}
   }
  }
  static void Handoff(string secret,string origin,Action<bool,string> check){
   var previous=new BrowserLocalBridge(0,secret,value=>value==origin,request=>new{ok=true,owner="previous"});
   int port=previous.ListeningPort,attempts=0;
   using(var replacement=new BrowserBridgeHost(()=>{attempts++;return new BrowserLocalBridge(port,secret,value=>value==origin,request=>new{ok=true,owner="replacement"});})){
    try{
     check(!replacement.TryStart()&&!replacement.Ready,"new window keeps browser connection pending while old window owns port");
     previous.Dispose();previous=null;
     check(replacement.TryStart()&&replacement.Ready,"browser connection acquires released port without restarting window");
     check(replacement.TryStart()&&attempts==2,"running browser connection starts only once");
     check(Send(port,Request(port,origin,secret,"{\"action\":\"folders\"}")).Contains("\"owner\":\"replacement\""),"handoff requests reach replacement handler");
     replacement.Dispose();
     check(!replacement.Ready&&!replacement.TryStart()&&attempts==2,"disposed connection cannot restart from a queued retry");
     using(var next=new BrowserLocalBridge(port,secret,value=>value==origin,request=>new{ok=true}))check(next.ListeningPort==port,"closing replacement releases port for later launch");
    }finally{if(previous!=null)previous.Dispose();}
   }
   bool surfaced=false;using(var broken=new BrowserBridgeHost(()=>{throw new InvalidDataException("synthetic permanent failure");}))try{broken.TryStart();}catch(InvalidDataException){surfaced=true;}
   check(surfaced,"permanent browser startup errors are surfaced instead of retried silently");
  }
 }
}
