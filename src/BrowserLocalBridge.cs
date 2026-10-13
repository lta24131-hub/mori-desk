using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace MoriDesk {
 // An authenticated, loopback-only backup for native messaging. Never a web
 // server for library files, and never enabled in a preview/test UI implicitly.
 internal sealed class BrowserLocalBridge : IDisposable {
  internal const int Port=47891, Maximum=29*1024*1024;
  internal const string Endpoint="http://127.0.0.1:47891/mori/v1";
  readonly TcpListener listener;
  readonly string token;
  readonly Func<string,bool> allowedOrigin;
  readonly Func<Dictionary<string,object>,object> handle;
  readonly SemaphoreSlim slots=new SemaphoreSlim(2,2);
  readonly HashSet<TcpClient> clients=new HashSet<TcpClient>();
  volatile bool stopped;
  internal int ListeningPort {get{return ((IPEndPoint)listener.LocalEndpoint).Port;}}
  internal BrowserLocalBridge(int port,string secret,Func<string,bool> allowed,Func<Dictionary<string,object>,object> handler){
   if(!ValidToken(secret))throw new ArgumentException("Invalid local connection key");
   token=secret;allowedOrigin=allowed;handle=handler;
   listener=new TcpListener(IPAddress.Loopback,port);listener.Server.ExclusiveAddressUse=true;listener.Start(8);
   new Thread(Accept){IsBackground=true,Name="Mori browser connection"}.Start();
  }
  internal static bool ValidToken(string value){return value!=null&&value.Length==64&&value.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f'));}
  internal static string Pair(string dataFolder,string extensionFolder,string installationId=null){
   if(installationId!=null&&!System.Text.RegularExpressions.Regex.IsMatch(installationId,"\\A[0-9a-f]{32}\\z"))throw new ArgumentException("Invalid extension installation identity");
   Directory.CreateDirectory(dataFolder);string file=Path.Combine(dataFolder,"local-key.txt"),secret;
   if(File.Exists(file)){secret=File.ReadAllText(file).Trim();if(!ValidToken(secret))throw new InvalidDataException("本机备用连接密钥损坏，未覆盖。");}
   else {var bytes=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);secret=BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();try{using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None)){byte[] text=Encoding.ASCII.GetBytes(secret);output.Write(text,0,text.Length);output.Flush(true);}}catch(IOException){secret=File.ReadAllText(file).Trim();if(!ValidToken(secret))throw new InvalidDataException("本机连接正在配置，请稍后重试。");}}
   Directory.CreateDirectory(extensionFolder);
   WriteAtomic(Path.Combine(extensionFolder,"local-connection.js"),Configuration(secret,installationId));
   return secret;
  }
  internal static string Configuration(string secret,string installationId=null){var values=new Dictionary<string,object>{{"endpoint",Endpoint},{"token",secret}};if(installationId!=null)values.Add("installationId",installationId);return "// Generated on this computer. Private pairing key; do not publish.\nself.MoriLocalConnection = "+new JavaScriptSerializer().Serialize(values)+";\n";}
  static void WriteAtomic(string path,string value){if(File.Exists(path)&&new FileInfo(path).Length<=4096&&File.ReadAllText(path)==value)return;string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";try{File.WriteAllText(temp,value,new UTF8Encoding(false));if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}finally{if(File.Exists(temp))File.Delete(temp);}}
  internal static bool OriginAllowed(string manifest,string origin){
   if(string.IsNullOrEmpty(origin)||!System.Text.RegularExpressions.Regex.IsMatch(origin,"\\Achrome-extension://[a-p]{32}\\z"))return false;
   try{var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(manifest));object name,origins;if(!data.TryGetValue("name",out name)||Convert.ToString(name)!=BrowserConnection.Host||!data.TryGetValue("allowed_origins",out origins)||!(origins is IEnumerable))return false;return ((IEnumerable)origins).Cast<object>().Any(x=>Convert.ToString(x)==origin+"/");}catch{return false;}
  }
  static bool EqualKey(string a,string b){if(a==null||b==null||a.Length!=b.Length)return false;int different=0;for(int i=0;i<a.Length;i++)different|=a[i]^b[i];return different==0;}
  void Accept(){while(!stopped){TcpClient client;try{client=listener.AcceptTcpClient();}catch(SocketException){return;}catch(ObjectDisposedException){return;}if(!slots.Wait(0)){client.Close();continue;}lock(clients){if(stopped){client.Close();slots.Release();return;}clients.Add(client);}ThreadPool.QueueUserWorkItem(delegate{try{Serve(client);}catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}finally{client.Close();lock(clients)clients.Remove(client);slots.Release();}});}}
  // Strict framing: no chunked bodies, pipelining, duplicate headers, redirects,
  // cookies, arbitrary paths or Host names. Authentication precedes allocation.
  void Serve(TcpClient client){
   client.ReceiveTimeout=3000;client.SendTimeout=5000;
   var stream=client.GetStream();try{
    var clock=System.Diagnostics.Stopwatch.StartNew();var header=new List<byte>();
    while(header.Count<16384){int c=stream.ReadByte();if(c<0)return;header.Add((byte)c);int n=header.Count;if(n>=4&&header[n-4]==13&&header[n-3]==10&&header[n-2]==13&&header[n-1]==10)break;if(clock.ElapsedMilliseconds>10000)return;}
    if(header.Count>=16384){Reply(stream,431,null,new{ok=false,error="Request headers too large"});return;}
    var lines=Encoding.ASCII.GetString(header.ToArray()).Split(new[]{"\r\n"},StringSplitOptions.None);var parts=lines[0].Split(' ');
    if(parts.Length!=3||parts[1]!="/mori/v1"||parts[2]!="HTTP/1.1"||(parts[0]!="POST"&&parts[0]!="OPTIONS")){Reply(stream,404,null,new{ok=false});return;}
    var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    for(int i=1;i<lines.Length&&lines[i]!="";i++){int colon=lines[i].IndexOf(':');if(colon<=0||char.IsWhiteSpace(lines[i][0])||headers.ContainsKey(lines[i].Substring(0,colon))){Reply(stream,400,null,new{ok=false});return;}headers.Add(lines[i].Substring(0,colon),lines[i].Substring(colon+1).Trim());}
    Func<string,string> get=name=>headers.ContainsKey(name)?headers[name]:"";string origin=get("Origin");
    if(get("Host")!="127.0.0.1:"+ListeningPort||!allowedOrigin(origin)){Reply(stream,403,null,new{ok=false,error="Connection not paired"});return;}
    if(parts[0]=="OPTIONS"){if(get("Access-Control-Request-Method")!="POST"){Reply(stream,403,null,new{ok=false});return;}Reply(stream,204,origin,null);return;}
    if(!EqualKey(token,get("X-Mori-Token"))){Reply(stream,403,origin,new{ok=false,error="Connection not paired"});return;}
    int length;
    if(headers.ContainsKey("Transfer-Encoding")||!int.TryParse(get("Content-Length"),out length)||length<2||length>Maximum||!get("Content-Type").Split(';')[0].Trim().Equals("application/json",StringComparison.OrdinalIgnoreCase)){Reply(stream,400,origin,new{ok=false,error="Invalid request"});return;}
    byte[] body=new byte[length];int at=0;
    while(at<length){if(clock.ElapsedMilliseconds>15000)return;int n=stream.Read(body,at,Math.Min(8192,length-at));if(n==0)return;at+=n;}
    object result;
    try{var json=new JavaScriptSerializer{MaxJsonLength=Maximum};var request=json.Deserialize<Dictionary<string,object>>(new UTF8Encoding(false,true).GetString(body));object action;if(request==null||!request.TryGetValue("action",out action)||!new[]{"folders","save","bookmark","importBookmarks","bridgeStatus","extensionControl"}.Contains(action as string))throw new InvalidDataException("不支持此收藏请求");request["_extensionId"]=BrowserConnection.IdFromOrigin(origin);result=handle(request);}catch(Exception ex){result=new{ok=false,error=ex.Message};}
    Reply(stream,200,origin,result);
   }finally{
    // Deliver an early rejection before closing a socket with an unread body.
    // Drain only already-arrived bytes, bounded; never wait for attacker input.
    try{byte[] discard=new byte[1024];int total=0;while(stream.DataAvailable&&total<65536){int n=stream.Read(discard,0,discard.Length);if(n==0)break;total+=n;}client.Client.Shutdown(SocketShutdown.Send);}catch(IOException){}catch(SocketException){}catch(ObjectDisposedException){}
   }
  }
  static void Reply(Stream stream,int status,string origin,object value){
   byte[] body=value==null?new byte[0]:Encoding.UTF8.GetBytes(new JavaScriptSerializer{MaxJsonLength=Maximum}.Serialize(value));
   string header="HTTP/1.1 "+status+" "+(status==200?"OK":status==204?"No Content":"Error")+"\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\nCache-Control: no-store\r\n";
   if(origin!=null)header+="Access-Control-Allow-Origin: "+origin+"\r\nVary: Origin\r\nAccess-Control-Allow-Methods: POST\r\nAccess-Control-Allow-Headers: Content-Type, X-Mori-Token\r\n";
   byte[] prefix=Encoding.ASCII.GetBytes(header+"\r\n");stream.Write(prefix,0,prefix.Length);stream.Write(body,0,body.Length);
  }
  public void Dispose(){stopped=true;listener.Stop();lock(clients)foreach(var client in clients)client.Close();}
 }
}
