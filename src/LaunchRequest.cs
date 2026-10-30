using System;
using System.IO;
using System.Linq;
namespace MoriDesk {
 // Resolve Explorer/Open-with arguments before any library, tray or capture
 // initialization. A viewer must never create or change the current library.
 internal sealed class LaunchRequest {
  public string Mode,Path;
  static LaunchRequest ModeOnly(string mode){return new LaunchRequest{Mode=mode};}
  public static LaunchRequest Parse(string[] args){
   if(args==null)throw new ArgumentNullException("args");
   if(args.Length==0)return ModeOnly("library");
   string first=args[0];
   if(string.IsNullOrWhiteSpace(first))throw new ArgumentException("没有提供要打开的文件。");
   if(first=="--image"||first=="--model"||first=="--preview"){
    if(args.Length==1&&first!="--preview")return ModeOnly(first.Substring(2));
    if(args.Length!=2)throw new ArgumentException("请一次打开一个要预览的文件。");
    return FileRequest(args[1],first.Substring(2));
   }
   if(first=="--startup"&&args.Length==1)return ModeOnly("library");
   if(first=="--library"||first=="--ui-test"){
    if((args.Length!=2&&(args.Length!=4||args[2]!="--handoff"||string.IsNullOrWhiteSpace(args[3])))||string.IsNullOrWhiteSpace(args[1]))throw new ArgumentException("需要提供图库文件夹路径。");
    return ModeOnly("library");
   }
   // Browser native messaging and existing isolated media/test workers keep
   // their original dispatch path; they must not initialize screenshot tools.
   if(first.StartsWith("chrome-extension://",StringComparison.Ordinal))return ModeOnly("worker");
   if(new[]{"--svg-render","--video-thumbnail","--video-self-test","--benchmark","--native-test","--image-codec","--pdf-text-worker","--pdf-render-worker"}.Contains(first)){
    if(args.Length<2)throw new ArgumentException("后台任务缺少参数。");
    return ModeOnly("worker");
   }
   if(first=="--self-test")return ModeOnly("worker");
   if(first.StartsWith("--",StringComparison.Ordinal))throw new ArgumentException("无法识别启动选项："+first);
   if(args.Length!=1)throw new ArgumentException("请一次打开一个图片或 3D 文件。");
   return FileRequest(first,null);
  }
  static LaunchRequest FileRequest(string value,string requestedMode){
   if(string.IsNullOrWhiteSpace(value)||value.IndexOf("://",StringComparison.Ordinal)>=0)throw new ArgumentException("请打开本机上的图片或 3D 文件，不支持此链接。");
   string path=System.IO.Path.GetFullPath(value);
   string kind=FileRouter.Kind(path);
   if(kind=="external"||(requestedMode!=null&&kind!=requestedMode))throw new ArgumentException("此入口支持图片、3D 模型、视频、音频和常用文档。其他文件请用默认程序打开。");
   if(!File.Exists(path))throw new FileNotFoundException("文件不存在，可能已移动或磁盘未连接。",path);
   return new LaunchRequest{Mode=kind,Path=path};
  }
 }
}
