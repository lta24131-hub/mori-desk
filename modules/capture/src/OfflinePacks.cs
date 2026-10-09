using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QingJie {
    public sealed class OfflineArtifact {public string Id,Url,Sha256;public long Bytes;}
    public sealed class OfflineCatalog {
        public int Schema,Revision;public string Version;public OfflineArtifact[] Artifacts;
        public long DownloadBytes {get{return Artifacts.Sum(a=>a.Bytes);}}
        public string Identity {get{using(var hash=SHA256.Create())return "pack-"+Revision+"-"+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(string.Join(";",Artifacts.Select(a=>a.Id+":"+a.Sha256.ToLowerInvariant()))))).Replace("-","").Substring(0,16);}}
        public void Validate(){
            string[] ids={"python","ctranslate2","sentencepiece","numpy","pyyaml","en_zh","zh_en"};
            if(Schema!=1||Revision<1||string.IsNullOrWhiteSpace(Version)||Version.Length>100||Artifacts==null||Artifacts.Length!=ids.Length||!Artifacts.Select(a=>a==null?null:a.Id).OrderBy(s=>s).SequenceEqual(ids.OrderBy(s=>s)))throw new InvalidDataException("离线包清单不兼容，请更新Luma。");
            foreach(var item in Artifacts){Uri uri;if(item.Bytes<1||item.Bytes>160*1048576L||!Regex.IsMatch(item.Sha256??"",@"\A[0-9a-fA-F]{64}\z")||!Uri.TryCreate(item.Url,UriKind.Absolute,out uri)||uri.Scheme!="https"||!string.IsNullOrEmpty(uri.UserInfo)||uri.Port!=443||!string.IsNullOrEmpty(uri.Fragment)||!new[]{"www.python.org","files.pythonhosted.org","argos-net.com"}.Contains(uri.Host))throw new InvalidDataException("离线包下载源或校验信息不正确。");}
            if(DownloadBytes>400*1048576L)throw new InvalidDataException("离线包超过本版本允许的大小。");
        }
    }
    public sealed class OfflineInstall {public OfflineCatalog Catalog;public long Bytes;}
    // Pack files only. Pins, OCR languages and preferences are never deletion targets.
    public static class OfflinePacks {
        const string Feed="https://raw.githubusercontent.com/lta24131-hub/qingjie-screenshot/main/offline/catalog.json";
        internal static string TestFolder;
        internal static readonly SemaphoreSlim Gate=new SemaphoreSlim(1,1);
        static readonly SemaphoreSlim management=new SemaphoreSlim(1,1);
        static readonly Lazy<OfflineCatalog> bundled=new Lazy<OfflineCatalog>(()=>Parse(Resource("QingJie.OfflineCatalog.json")));
        public static string Folder {get{return TestFolder??Path.Combine(MoriDesk.Entry.DataRoot,"capture","offline-translation");}}
        public static OfflineCatalog Bundled {get{return bundled.Value;}}
        internal static string Resource(string name){using(var stream=typeof(OfflinePacks).Assembly.GetManifestResourceStream(name))using(var reader=new StreamReader(stream,Encoding.UTF8))return reader.ReadToEnd();}
        internal static OfflineCatalog Parse(string json){if(json==null||json.Length>32768)throw new InvalidDataException("离线包清单异常。");var catalog=new JavaScriptSerializer().Deserialize<OfflineCatalog>(json);if(catalog==null)throw new InvalidDataException("离线包清单为空。");catalog.Validate();return catalog;}
        public static OfflineInstall Installed {
            get{try{string path=Path.Combine(Folder,"current.json");if(!File.Exists(path)||new FileInfo(path).Length>65536)return null;var installed=new JavaScriptSerializer().Deserialize<OfflineInstall>(File.ReadAllText(path));installed.Catalog.Validate();string root=PackPath(installed.Catalog);return RequiredFiles(root)?installed:null;}catch{return null;}}
        }
        internal static string CacheIdentity {get{var installed=Installed;return installed==null?"missing":installed.Catalog.Identity;}}
        internal static string PackPath(OfflineCatalog catalog){catalog.Validate();return Path.Combine(Folder,catalog.Identity);}
        internal static bool RequiredFiles(string root){return new[]{"runtime/python.exe","runtime/python313.dll","worker.py","models/en_zh/model/model.bin","models/en_zh/sentencepiece.model","models/zh_en/model/model.bin","models/zh_en/sentencepiece.model"}.All(p=>File.Exists(Path.Combine(root,p)));}
        public static async Task<OfflineCatalog> CheckUpdates(CancellationToken cancel){
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            using(var client=new HttpClient {Timeout=TimeSpan.FromSeconds(15),MaxResponseContentBufferSize=32768}){
                using(var response=await client.GetAsync(Feed,cancel)){
                    if(response.StatusCode==HttpStatusCode.NotFound)throw new InvalidOperationException("更新列表尚未发布；可以下载当前应用内置的兼容版本。");
                    response.EnsureSuccessStatusCode();var remote=Parse(await response.Content.ReadAsStringAsync());return remote.Revision>Bundled.Revision?remote:Bundled;
                }
            }
        }
        internal static bool Verify(string file,OfflineArtifact artifact){if(!File.Exists(file)||new FileInfo(file).Length!=artifact.Bytes)return false;using(var stream=File.OpenRead(file))using(var hash=SHA256.Create())return string.Equals(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-",""),artifact.Sha256,StringComparison.OrdinalIgnoreCase);}
        static async Task Download(OfflineArtifact artifact,string destination,long completed,long total,IProgress<string> progress,CancellationToken cancel){
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancel))using(var client=new HttpClient {Timeout=TimeSpan.FromMinutes(15)}){
                timeout.CancelAfter(TimeSpan.FromMinutes(15));
                using(var response=await client.GetAsync(artifact.Url,HttpCompletionOption.ResponseHeadersRead,timeout.Token))using(timeout.Token.Register(()=>response.Dispose())){
                    response.EnsureSuccessStatusCode();if(response.RequestMessage.RequestUri.Scheme!="https")throw new InvalidDataException("下载连接不安全。");
                    if(response.Content.Headers.ContentLength.HasValue&&response.Content.Headers.ContentLength.Value!=artifact.Bytes)throw new InvalidDataException("离线包下载大小与清单不符。");
                    using(var input=await response.Content.ReadAsStreamAsync())using(var output=File.Create(destination)){
                        var buffer=new byte[65536];long bytes=0;int read,lastPercent=-1;
                        while((read=await input.ReadAsync(buffer,0,buffer.Length,timeout.Token))>0){bytes+=read;if(bytes>artifact.Bytes)throw new InvalidDataException("离线包大小异常。");await output.WriteAsync(buffer,0,read,timeout.Token);int percent=(int)((completed+bytes)*100/total);if(progress!=null&&percent!=lastPercent){lastPercent=percent;progress.Report("下载中 · "+percent+"%（"+((completed+bytes)/1048576.0).ToString("0.0")+" / "+(total/1048576.0).ToString("0.0")+" MB）");}}
                    }
                }
                timeout.Token.ThrowIfCancellationRequested();
            }
        }
        internal static string ArchivePath(string root,string name){
            if(string.IsNullOrEmpty(name)||name.IndexOf(':')>=0||name.IndexOf('\0')>=0||name.StartsWith("/")||name.StartsWith("\\")||name.Split('/','\\').Any(p=>p==".."||p=="."||p.EndsWith(" ")||p.EndsWith(".")))throw new InvalidDataException("离线包包含不安全路径。");
            string parent=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string target=Path.GetFullPath(Path.Combine(root,name));if(!target.StartsWith(parent,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("离线包路径越界。");return target;
        }
        internal static void Extract(string archive,string root,string kind,CancellationToken cancel){
            bool model=kind=="en_zh"||kind=="zh_en";string destination=Path.Combine(root,model?"models/"+kind:kind=="python"?"runtime":"runtime/packages");
            using(var zip=ZipFile.OpenRead(archive)){
                if(zip.Entries.Count>16000)throw new InvalidDataException("离线包文件数量异常。");long expanded=0;
                foreach(var entry in zip.Entries){
                    cancel.ThrowIfCancellationRequested();string name=entry.FullName.Replace('\\','/');ArchivePath(destination,name);
                    if(((entry.ExternalAttributes>>16)&0xF000)==0xA000)throw new InvalidDataException("离线包包含符号链接。");
                    expanded+=entry.Length;if(expanded>600*1048576L)throw new InvalidDataException("离线包解压大小异常。");
                    if(model){int slash=name.IndexOf('/');if(slash<0)throw new InvalidDataException("模型布局不兼容。");name=name.Substring(slash+1);if(name.Length==0||name.StartsWith("stanza/",StringComparison.Ordinal))continue;}
                    if(name.EndsWith("/",StringComparison.Ordinal))continue;
                    string target=ArchivePath(destination,name);Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using(var input=entry.Open())using(var output=new FileStream(target,FileMode.CreateNew)){byte[] buffer=new byte[65536];int read;while((read=input.Read(buffer,0,buffer.Length))>0){cancel.ThrowIfCancellationRequested();output.Write(buffer,0,read);}}
                }
            }
        }
        internal static void SafeDelete(string target){
            string root=Path.GetFullPath(Folder).TrimEnd(Path.DirectorySeparatorChar),full=Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
            if(!string.Equals(Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase)||!Regex.IsMatch(Path.GetFileName(full),@"\A(?:pack-[0-9]+-[A-F0-9]{16}|stage-[a-f0-9]{32})\z"))throw new InvalidOperationException("拒绝删除非离线包目录。");
            if(!Directory.Exists(full))return;
            if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("离线包目录不能是链接。");
            var pending=new Stack<string>();pending.Push(full);while(pending.Count>0){string current=pending.Pop();if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new IOException("离线包中存在链接，停止删除。");foreach(var child in Directory.GetDirectories(current))pending.Push(child);foreach(var file in Directory.GetFiles(current))if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("离线包中存在链接，停止删除。");}
            Directory.Delete(full,true);
        }
        static void Clean(string path){try{SafeDelete(path);}catch(IOException){}catch(UnauthorizedAccessException){}}
        internal static void Activate(string stage,OfflineCatalog catalog,long bytes,CancellationToken cancel){
            string destination=PackPath(catalog);var previous=Installed;
            if(Directory.Exists(destination)){if(previous!=null&&previous.Catalog.Identity==catalog.Identity)throw new InvalidOperationException("该版本已经安装。");SafeDelete(destination);}
            cancel.ThrowIfCancellationRequested();Directory.Move(stage,destination);
            string pointer=Path.Combine(Folder,"current.json"),pending=Path.Combine(Folder,"current-"+Guid.NewGuid().ToString("N")+".tmp");
            try{File.WriteAllText(pending,new JavaScriptSerializer().Serialize(new OfflineInstall {Catalog=catalog,Bytes=bytes}),new UTF8Encoding(false));cancel.ThrowIfCancellationRequested();if(File.Exists(pointer))File.Replace(pending,pointer,null);else File.Move(pending,pointer);}
            catch{Clean(destination);throw;}finally{if(File.Exists(pending))File.Delete(pending);}
            if(previous!=null&&previous.Catalog.Identity!=catalog.Identity)Clean(PackPath(previous.Catalog));
        }
        public static async Task Install(OfflineCatalog catalog,IProgress<string> progress,CancellationToken cancel){
            catalog.Validate();await management.WaitAsync(cancel);string stage=null;
            try{
                var current=Installed;if(current!=null&&current.Catalog.Revision>=catalog.Revision)throw new InvalidOperationException("当前已安装此版本或更新版本。");
                Directory.CreateDirectory(Folder);if((File.GetAttributes(Folder)&FileAttributes.ReparsePoint)!=0)throw new IOException("离线包目录不能是链接。");
                if(new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Folder))).AvailableFreeSpace<800*1048576L)throw new IOException("请至少预留 800 MB 空间用于安全安装 / 更新。");
                stage=Path.Combine(Folder,"stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);long completed=0;
                foreach(var artifact in catalog.Artifacts){string archive=Path.Combine(stage,artifact.Id+".download");await Download(artifact,archive,completed,catalog.DownloadBytes,progress,cancel);if(progress!=null)progress.Report("校验并解压 · "+artifact.Id);await Task.Run(()=>{if(!Verify(archive,artifact))throw new InvalidDataException("离线包校验失败，未启用下载内容。");Extract(archive,stage,artifact.Id,cancel);},cancel);File.Delete(archive);completed+=artifact.Bytes;}
                File.WriteAllText(Path.Combine(stage,"worker.py"),Resource("QingJie.OfflineWorker.py"),new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(stage,"runtime","python313._pth"),"python313.zip\r\n.\r\npackages\r\n",new UTF8Encoding(false));
                if(!RequiredFiles(stage))throw new InvalidDataException("离线包缺少运行文件。");
                if(progress!=null)progress.Report("正在验证中英双向翻译，旧包仍保留…");
                await Gate.WaitAsync(cancel);try{
                    await Task.Run(()=>{var en=OfflineTranslation.RunProcess(stage,new[]{"请确认日期。"},"en",cancel);var zh=OfflineTranslation.RunProcess(stage,new[]{"Please save the image."},"zh",cancel);if(!en[0].Any(c=>c>='A'&&c<='z')||!zh[0].Any(c=>c>='\u3400'&&c<='\u9fff'))throw new InvalidDataException("离线翻译自检未通过，旧包未改变。");long bytes=Directory.GetFiles(stage,"*",SearchOption.AllDirectories).Sum(p=>new FileInfo(p).Length);Activate(stage,catalog,bytes,cancel);},cancel);
                }finally{Gate.Release();}
            }finally{if(stage!=null)Clean(stage);management.Release();}
        }
        public static async Task Uninstall(CancellationToken cancel){
            await management.WaitAsync(cancel);try{await Gate.WaitAsync(cancel);try{await Task.Run(()=>{
                if(!Directory.Exists(Folder))return;cancel.ThrowIfCancellationRequested();
                // Delete content before retiring the pointer. A partial filesystem failure
                // is reported as a failure; it is never treated as a completed uninstall.
                foreach(var dir in Directory.GetDirectories(Folder))if(Regex.IsMatch(Path.GetFileName(dir),@"\A(?:pack-[0-9]+-[A-F0-9]{16}|stage-[a-f0-9]{32})\z"))SafeDelete(dir);
                string pointer=Path.Combine(Folder,"current.json");if(File.Exists(pointer))File.Delete(pointer);
            },cancel);}finally{Gate.Release();}}finally{management.Release();}
        }
    }
}
