using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace MoriDesk {
 // This manager owns only its fixed installation, the explicitly confirmed
 // legacy installation, and sibling staging/backup directories it creates.
 // Browser requests never supply a filesystem path or an executable payload.
 internal static class BrowserExtensionManager {
  internal const string PublicKey="MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAjddSxVsBeHA9rnjVHgN42pIsavjBH8scM7Fxekyyecen8rotsw3QpVzGPAgFUTvBVOQPL+ftbIUm8JQRkKPB1BUh3/KI0OPM8QH8t7u+cyj1KQY3hEmPa/HvpAur3vBEv0Q6QhBDAzzEZLFff9hfaMkfuzLJYbHvOSLYANqLffeRmsXcGA4/qh5FZDYzflGyvic53nayI2wH5ui50oDM+WrrsjWd7c/0DfQcvf+XoquggvYylLq8lAC7l8PgLOhcgcL+uhkVs5wtJ/GpQpLWvjAmwY8zCd2hBXGSqqNfHu7Mfjvm7B6R0+Retdod7CeNrBwiACfzyzgxGAYMG5bTsQIDAQAB";
  internal static readonly string[] RuntimeFiles={"background.js","bookmarks.js","collector-text.js","content.js","drag-geometry.js","icon-16.png","icon-32.png","icon-48.png","icon-128.png","local-bridge.js","manifest.json","media-source.js","popup.css","popup.html","popup.js","preferences.js","runtime-client.js","updater.js"};
  static BrowserExtensionStore Store(){return new BrowserExtensionStore(Entry.DataRoot,BrowserConnection.ExtensionDirectory(typeof(Entry).Assembly.Location),Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Mori","browser-extension"),PublicKey,BrowserConnection.Register,null,null);}
  internal static BrowserExtensionStatus EnsureInstalled(){return Store().EnsureInstalled();}
  internal static BrowserExtensionStatus RequestUpdate(){return Store().RequestUpdate();}
  internal static BrowserExtensionStatus BootstrapLegacy(string extensionId){return Store().BootstrapLegacy(extensionId);}
  internal static BrowserExtensionStatus Status(){return Store().Status();}
  internal static void RegisterKnown(){Store().RegisterKnown();}
  internal static void RefreshPairing(){Store().RefreshPairing();}
  internal static object Handle(Dictionary<string,object> request){return Store().Handle(request);}
  internal static object BridgeStatus(Dictionary<string,object> request){return Store().BridgeStatus(request);}
 }

 internal sealed class BrowserExtensionStatus {
  public string summary="",version="",installedVersion="",bundledVersion="",availableVersion="",path="",legacyId="";
  public bool pending,connected,requiresBootstrap,retryUpdate,upToDate;
  public int profiles;
 }
 internal sealed class BrowserExtensionCatalog {public int schema=1;public List<BrowserExtensionInstall> installs=new List<BrowserExtensionInstall>();public BrowserExtensionRecovery recovery;}
 internal sealed class BrowserExtensionRecovery {public string installationId="",token="",stage="",backup="",client="";public long started;public bool hadOriginal;}
 internal sealed class BrowserExtensionInstall {
  public string installationId="",extensionId="",path="",version="",activeVersion="",manifestKey;
  public long lastSeen;
  public bool legacy,awaitingReload;
  public List<BrowserExtensionClient> clients=new List<BrowserExtensionClient>();
  public BrowserExtensionPending pending;
  public string lastToken="",lastClient="",lastVersion="",backup="";
  public long lastApplied;
 }
 internal sealed class BrowserExtensionClient {public string id="",version="";public long seen;}
 internal sealed class BrowserExtensionPending {public string token="",version="",path="";public long expires;public Dictionary<string,string> hashes=new Dictionary<string,string>();}

 // Injected paths/callbacks keep tests completely separate from real browser
 // profiles, registry entries, connection keys and the user's media library.
 internal sealed class BrowserExtensionStore {
  static readonly object Gate=new object();
  readonly string root,browser,bundle,legacy,key,fixedPath,catalogPath;
  readonly Action<string> register;
  readonly Func<string[]> allowedIds;
  readonly Action<string,string> move;
  readonly Func<DateTime> now;
  readonly Action catalogRead;
  BrowserExtensionCatalog leaseCatalog;
  readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=1024*1024};
  const long RecentTicks=90*TimeSpan.TicksPerSecond,TokenTicks=30*TimeSpan.TicksPerMinute;
  internal BrowserExtensionStore(string dataRoot,string bundleDirectory,string legacyDirectory,string publicKey,Action<string> registerId,Func<string[]> registeredIds=null,Action<string,string> moveDirectory=null,Func<DateTime> clock=null,Action onCatalogRead=null){
   root=Full(dataRoot);browser=Path.Combine(root,"browser");bundle=Full(bundleDirectory);legacy=Full(legacyDirectory);key=publicKey;fixedPath=Path.Combine(browser,"extension");catalogPath=Path.Combine(browser,"extension-installs.json");
   register=registerId??delegate{};allowedIds=registeredIds??ReadAllowedIds;move=moveDirectory??Directory.Move;now=clock??(()=>DateTime.UtcNow);catalogRead=onCatalogRead;
  }
  static string Full(string path){return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);}
  static bool Same(string a,string b){return string.Equals(Full(a),Full(b),StringComparison.OrdinalIgnoreCase);}
  static bool Id(string id){return Regex.IsMatch(id??"","\\A[a-p]{32}\\z");}
  static bool Hex(string value,int size){return value!=null&&Regex.IsMatch(value,"\\A[0-9a-f]{"+size+"}\\z");}
  static string RandomHex(){return Guid.NewGuid().ToString("N");}
  static string Token(){var bytes=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(bytes);return BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();}
  static string Value(Dictionary<string,object> request,string name){object value;return request!=null&&request.TryGetValue(name,out value)?value as string:null;}
  static object Failure(string message){return new{ok=false,error=message};}
  T Locked<T>(Func<T> action){lock(Gate){SafeAncestors(browser);Directory.CreateDirectory(browser);string lockPath=Path.Combine(browser,"extension-manager.lock");SafeFile(lockPath,false);FileStream lease=null;for(int attempt=0;attempt<50;attempt++){try{lease=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);break;}catch(IOException){if(attempt==49)throw new IOException("插件配置正在更新，请稍后重试。");Thread.Sleep(40);}}using(lease){try{leaseCatalog=ReadCatalog();RecoverInterruptedUpdate();return action();}finally{leaseCatalog=null;}}}}
  static void SafeAncestors(string path){var at=new DirectoryInfo(Full(path));while(at!=null){if(at.Exists&&(at.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("插件目录包含链接，已停止更新以保护原文件。");at=at.Parent;}}
  static void SafeFile(string path,bool mustExist){SafeAncestors(Path.GetDirectoryName(path));if(File.Exists(path)){if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("插件文件包含链接，已停止更新。");}else if(mustExist)throw new FileNotFoundException("插件文件不完整，请重新安装完整软件包。",Path.GetFileName(path));}
  static string Hash(string path){SafeFile(path,true);using(var input=File.OpenRead(path))using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();}
  static string HashId(byte[] bytes){byte[] hash;using(var sha=SHA256.Create())hash=sha.ComputeHash(bytes);var text=new StringBuilder(32);for(int i=0;i<16;i++){text.Append((char)('a'+(hash[i]>>4)));text.Append((char)('a'+(hash[i]&15)));}return text.ToString();}
  internal static string KeyId(string publicKey){return HashId(Convert.FromBase64String(publicKey));}
  // Chromium GenerateIdForPath hashes native wchar_t bytes on Windows after
  // capitalising the drive letter (components/crx_file/id_util.cc).
  internal static string PathId(string directory){string path=Full(directory);if(path.Length>=2&&path[1]==':')path=char.ToUpperInvariant(path[0])+path.Substring(1);return HashId(Encoding.Unicode.GetBytes(path));}
  Dictionary<string,object> ManifestAt(string directory){string file=Path.Combine(directory,"manifest.json");SafeFile(file,true);if(new FileInfo(file).Length>65536)throw new InvalidDataException("插件清单异常。");var result=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));object version,name,mv,background;
   if(result==null||!result.TryGetValue("name",out name)||!(name is string)||!((string)name).StartsWith("Mori",StringComparison.Ordinal)||!result.TryGetValue("manifest_version",out mv)||Convert.ToString(mv)!="3"||!result.TryGetValue("version",out version)||!ValidVersion(version as string)||!result.TryGetValue("background",out background))throw new InvalidDataException("该目录不是受支持的 Mori 插件。");
   var worker=background as Dictionary<string,object>;object script;if(worker==null||!worker.TryGetValue("service_worker",out script)||Convert.ToString(script)!="background.js")throw new InvalidDataException("插件后台清单异常。");return result;
  }
  static bool ValidVersion(string version){return Regex.IsMatch(version??"","\\A[0-9]{1,5}(\\.[0-9]{1,5}){1,3}\\z")&&version.Split('.').All(v=>int.Parse(v)<=65535);}
  internal static int CompareVersions(string left,string right){if(!ValidVersion(left)||!ValidVersion(right))throw new InvalidDataException("插件版本格式异常。");var a=left.Split('.').Select(int.Parse).ToArray();var b=right.Split('.').Select(int.Parse).ToArray();for(int i=0;i<Math.Max(a.Length,b.Length);i++){int x=i<a.Length?a[i]:0,y=i<b.Length?b[i]:0;if(x!=y)return x<y?-1:1;}return 0;}
  string MaximumKnownVersion(BrowserExtensionInstall item){string maximum=item.version;foreach(string version in item.clients.Select(c=>c.version).Concat(new[]{item.activeVersion}).Where(v=>!string.IsNullOrEmpty(v)))if(CompareVersions(version,maximum)>0)maximum=version;return maximum;}
  void GuardVersion(BrowserExtensionInstall item,string version){string known=MaximumKnownVersion(item);if(CompareVersions(version,known)<0)throw new InvalidDataException("本机软件提供的插件 "+version+" 比已安装或正在运行的 "+known+" 更旧。已保留现有插件，不会降级；请先更新 Mori Desk。");}
  string BundleVersion(){return Convert.ToString(ManifestAt(bundle)["version"]);}
  string[] ReadAllowedIds(){string file=Path.Combine(browser,BrowserConnection.Host+".json");if(!File.Exists(file))return new string[0];try{SafeFile(file,true);if(new FileInfo(file).Length>65536)return new string[0];var data=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(file));object host,origins;if(data==null||!data.TryGetValue("name",out host)||Convert.ToString(host)!=BrowserConnection.Host||!data.TryGetValue("allowed_origins",out origins)||!(origins is IEnumerable))return new string[0];return ((IEnumerable)origins).Cast<object>().Select(x=>BrowserConnection.IdFromOrigin(Convert.ToString(x))).Where(Id).Distinct().ToArray();}catch{return new string[0];}}
  string LegacyId(){if(!Directory.Exists(legacy))return "";try{var manifest=ManifestAt(legacy);object raw;string identity=manifest.TryGetValue("key",out raw)&&raw is string?KeyId((string)raw):PathId(legacy);return allowedIds().Contains(identity,StringComparer.Ordinal)?identity:"";}catch{return "";}}
  BrowserExtensionCatalog Load(){if(leaseCatalog==null)throw new InvalidOperationException("插件安装记录只能在持有配置锁时访问。");return leaseCatalog;}
  BrowserExtensionCatalog ReadCatalog(){if(catalogRead!=null)catalogRead();if(!File.Exists(catalogPath))return new BrowserExtensionCatalog();SafeFile(catalogPath,true);if(new FileInfo(catalogPath).Length>1024*1024)throw new InvalidDataException("插件安装记录异常，未修改原插件。");BrowserExtensionCatalog state;try{state=json.Deserialize<BrowserExtensionCatalog>(File.ReadAllText(catalogPath));}catch(Exception ex){throw new InvalidDataException("插件安装记录损坏，未修改原插件。",ex);}if(state==null||state.schema!=1||state.installs==null||state.installs.Count>2)throw new InvalidDataException("不支持此插件安装记录。");
   var ids=new HashSet<string>();var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var item in state.installs){if(item==null||!Hex(item.installationId,32)||!Id(item.extensionId)||!ValidVersion(item.version)||string.IsNullOrEmpty(item.path)||!Same(item.path,item.legacy?legacy:fixedPath)||!ids.Add(item.installationId)||!paths.Add(Full(item.path))||item.clients==null||item.clients.Count>32)throw new InvalidDataException("插件安装记录不安全，已停止更新。");SafeAncestors(item.path);if(item.legacy){string expected=item.manifestKey==null?PathId(legacy):KeyId(item.manifestKey);if(item.extensionId!=expected)throw new InvalidDataException("旧插件身份不匹配。");}else if(item.extensionId!=KeyId(key)||item.manifestKey!=key)throw new InvalidDataException("插件身份不匹配。");
    if(item.clients.Any(c=>c==null||!Hex(c.id,32)||!ValidVersion(c.version))||(!string.IsNullOrEmpty(item.activeVersion)&&!ValidVersion(item.activeVersion))||(!string.IsNullOrEmpty(item.lastToken)&&!Hex(item.lastToken,64))||(!string.IsNullOrEmpty(item.lastClient)&&!Hex(item.lastClient,32))||(!string.IsNullOrEmpty(item.lastVersion)&&!ValidVersion(item.lastVersion)))throw new InvalidDataException("插件连接记录异常。");
    if(!string.IsNullOrEmpty(item.backup))ValidateSibling(item.path,item.backup,".mori-backup-");
    if(item.pending!=null){var p=item.pending;if(!Hex(p.token,64)||!ValidVersion(p.version)||p.hashes==null||p.hashes.Count!=BrowserExtensionManager.RuntimeFiles.Length+1)throw new InvalidDataException("插件更新记录异常。");ValidateSibling(item.path,p.path,".mori-stage-");foreach(var pair in p.hashes)if(!KnownFile(pair.Key)||!Hex(pair.Value,64))throw new InvalidDataException("插件更新文件记录异常。");}
   }
   if(state.recovery!=null){var r=state.recovery;var item=state.installs.SingleOrDefault(i=>i.installationId==r.installationId);if(item==null||item.pending==null||!Hex(r.token,64)||r.token!=item.pending.token||string.IsNullOrEmpty(r.stage)||!Same(r.stage,item.pending.path)||(!string.IsNullOrEmpty(r.client)&&!Hex(r.client,32))||r.started<=0)throw new InvalidDataException("插件恢复记录与安装不匹配，未改动原文件。");ValidateSibling(item.path,r.stage,".mori-stage-");ValidateSibling(item.path,r.backup,".mori-backup-");}
   return state;
  }
  static bool KnownFile(string name){return name=="local-connection.js"||BrowserExtensionManager.RuntimeFiles.Contains(name,StringComparer.Ordinal);}
  static void ValidateSibling(string target,string candidate,string suffix){if(string.IsNullOrEmpty(candidate)||!Same(Path.GetDirectoryName(target),Path.GetDirectoryName(candidate))||!Full(candidate).StartsWith(Full(target)+suffix,StringComparison.OrdinalIgnoreCase)||!Hex(Full(candidate).Substring((Full(target)+suffix).Length),32))throw new InvalidDataException("插件更新路径异常。");SafeAncestors(candidate);}
  void Save(BrowserExtensionCatalog state){string temp=catalogPath+"."+RandomHex()+".tmp";try{byte[] bytes=new UTF8Encoding(false).GetBytes(json.Serialize(state));using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}if(File.Exists(catalogPath))File.Replace(temp,catalogPath,null);else File.Move(temp,catalogPath);}finally{if(File.Exists(temp))File.Delete(temp);}}
  void Pair(BrowserExtensionInstall item,string directory){SafeFile(Path.Combine(directory,"local-connection.js"),false);BrowserLocalBridge.Pair(browser,directory,item.installationId);}
  BrowserExtensionPending Stage(BrowserExtensionInstall item){SafeAncestors(bundle);var manifest=ManifestAt(bundle);GuardVersion(item,Convert.ToString(manifest["version"]));object shippedKey;if(!manifest.TryGetValue("key",out shippedKey)||Convert.ToString(shippedKey)!=key)throw new InvalidDataException("随软件提供的插件身份不匹配，未更新。");string stage=item.path+".mori-stage-"+RandomHex();ValidateSibling(item.path,stage,".mori-stage-");Directory.CreateDirectory(stage);
   foreach(string file in BrowserExtensionManager.RuntimeFiles){string source=Path.Combine(bundle,file);SafeFile(source,true);if(new FileInfo(source).Length>16*1024*1024)throw new InvalidDataException("插件文件异常，未更新。");File.Copy(source,Path.Combine(stage,file),false);}
   if(item.manifestKey==null)manifest.Remove("key");else manifest["key"]=item.manifestKey;
   File.WriteAllText(Path.Combine(stage,"manifest.json"),json.Serialize(manifest),new UTF8Encoding(false));Pair(item,stage);
   var pending=new BrowserExtensionPending{token=Token(),version=Convert.ToString(manifest["version"]),path=stage,expires=now().Ticks+TokenTicks};foreach(string file in BrowserExtensionManager.RuntimeFiles.Concat(new[]{"local-connection.js"}))pending.hashes.Add(file,Hash(Path.Combine(stage,file)));return pending;
  }
  bool InstalledMatchesBundle(BrowserExtensionInstall item,string version){if(item.pending!=null||item.awaitingReload||string.IsNullOrEmpty(item.activeVersion)||CompareVersions(item.version,version)!=0||CompareVersions(item.activeVersion,version)!=0||Recent(item).Any(c=>CompareVersions(c.version,version)!=0))return false;try{SafeAncestors(item.path);if(!Directory.Exists(item.path)||Directory.GetDirectories(item.path).Length!=0||Directory.GetFiles(item.path).Length!=BrowserExtensionManager.RuntimeFiles.Length+1)return false;var manifest=ManifestAt(bundle);object publicKey;if(!manifest.TryGetValue("key",out publicKey)||Convert.ToString(publicKey)!=key)return false;if(item.manifestKey==null)manifest.Remove("key");else manifest["key"]=item.manifestKey;string installedManifest=Path.Combine(item.path,"manifest.json");SafeFile(installedManifest,true);if(new FileInfo(installedManifest).Length>65536||File.ReadAllText(installedManifest)!=json.Serialize(manifest))return false;foreach(string file in BrowserExtensionManager.RuntimeFiles.Where(f=>f!="manifest.json"))if(Hash(Path.Combine(bundle,file))!=Hash(Path.Combine(item.path,file)))return false;string keyFile=Path.Combine(browser,"local-key.txt"),connection=Path.Combine(item.path,"local-connection.js");SafeFile(keyFile,true);SafeFile(connection,true);if(new FileInfo(keyFile).Length>1024||new FileInfo(connection).Length>4096)return false;string secret=File.ReadAllText(keyFile).Trim();return BrowserLocalBridge.ValidToken(secret)&&File.ReadAllText(connection)==BrowserLocalBridge.Configuration(secret,item.installationId);}catch(InvalidDataException){return false;}catch(IOException){return false;}}
  void VerifyFiles(BrowserExtensionInstall item,BrowserExtensionPending pending,string directory){GuardVersion(item,pending.version);SafeAncestors(directory);if(!Directory.Exists(directory)||Directory.GetDirectories(directory).Length!=0||Directory.GetFiles(directory).Length!=pending.hashes.Count)throw new InvalidDataException("插件暂存文件已改变，未替换原插件。");foreach(var pair in pending.hashes)if(Hash(Path.Combine(directory,pair.Key))!=pair.Value)throw new InvalidDataException("插件暂存文件校验失败，未替换原插件。");var manifest=ManifestAt(directory);object raw;string identity=manifest.TryGetValue("key",out raw)?KeyId(Convert.ToString(raw)):PathId(item.path);if(identity!=item.extensionId||Convert.ToString(manifest["version"])!=pending.version)throw new InvalidDataException("插件暂存身份不匹配。");}
  void VerifyStage(BrowserExtensionInstall item){var pending=item.pending;if(pending==null||pending.expires<now().Ticks)throw new InvalidDataException("本次更新已过期，请在 Mori 中重新点更新。");ValidateSibling(item.path,pending.path,".mori-stage-");VerifyFiles(item,pending,pending.path);}
  void CompleteMetadata(BrowserExtensionCatalog state,BrowserExtensionInstall item,BrowserExtensionRecovery record){var pending=item.pending;item.version=pending.version;item.pending=null;item.awaitingReload=record.hadOriginal;item.backup=record.hadOriginal&&Directory.Exists(record.backup)?record.backup:item.backup;item.lastToken=pending.token;item.lastClient=record.client;item.lastVersion=pending.version;item.lastApplied=record.started;state.recovery=null;}
  // The catalog is a write-ahead journal: it is committed before the first
  // directory rename. A later process can recover after a crash/power loss,
  // not only after an exception in the process performing the update.
  void RecoverInterruptedUpdate(){var state=Load();var record=state.recovery;if(record==null)return;var item=state.installs.Single(i=>i.installationId==record.installationId);bool target=Directory.Exists(item.path),stage=Directory.Exists(record.stage),backup=Directory.Exists(record.backup);
   if(target&&stage&&!backup){state.recovery=null;Save(state);return;}
   if(!target&&backup){move(record.backup,item.path);state.recovery=null;Save(state);return;}
   if(target){bool valid;try{VerifyFiles(item,item.pending,item.path);valid=true;}catch(InvalidDataException){valid=false;}catch(IOException){valid=false;}
    if(valid){CompleteMetadata(state,item,record);Save(state);return;}
    if(!stage&&(backup||!record.hadOriginal)){move(item.path,record.stage);if(backup)move(record.backup,item.path);state.recovery=null;Save(state);return;}
    throw new InvalidDataException("上次插件更新中断，现有文件未通过校验。原文件已保留，请重新安装插件。");
   }
   if(!record.hadOriginal){state.recovery=null;Save(state);return;}
   throw new InvalidDataException("上次插件更新中断，暂时找不到原目录及备份。未删除任何文件，请检查磁盘后重试。");
  }
  void Commit(BrowserExtensionCatalog state,BrowserExtensionInstall item,string client){VerifyStage(item);string target=item.path,stage=item.pending.path,backup=target+".mori-backup-"+RandomHex();ValidateSibling(target,backup,".mori-backup-");bool oldMoved=false,newMoved=false,oldAwaiting=item.awaitingReload;var pending=item.pending;string oldVersion=item.version,oldBackup=item.backup,oldToken=item.lastToken,oldClient=item.lastClient,oldLastVersion=item.lastVersion;long oldApplied=item.lastApplied;
   var recovery=new BrowserExtensionRecovery{installationId=item.installationId,token=pending.token,stage=stage,backup=backup,client=client,started=now().Ticks,hadOriginal=Directory.Exists(target)};state.recovery=recovery;Save(state);
   try{if(recovery.hadOriginal){SafeAncestors(target);move(target,backup);oldMoved=true;}move(stage,target);newMoved=true;CompleteMetadata(state,item,recovery);Save(state);}
   catch{item.version=oldVersion;item.pending=pending;item.awaitingReload=oldAwaiting;item.backup=oldBackup;item.lastToken=oldToken;item.lastClient=oldClient;item.lastVersion=oldLastVersion;item.lastApplied=oldApplied;state.recovery=recovery;
    // Never delete either copy during recovery. Leave generated staging and
    // backup directories in place if the filesystem itself prevents rollback.
    try{if(newMoved&&Directory.Exists(target)&&!Directory.Exists(stage))Directory.Move(target,stage);if(oldMoved&&Directory.Exists(backup)&&!Directory.Exists(target))Directory.Move(backup,target);}catch{}throw;
   }
  }
  List<BrowserExtensionClient> Recent(BrowserExtensionInstall item){long limit=now().Ticks-RecentTicks;return item.clients.Where(c=>c.seen>=limit&&c.seen<=now().Ticks+TimeSpan.TicksPerMinute).ToList();}
  BrowserExtensionStatus MakeStatus(BrowserExtensionCatalog state){string latest=BundleVersion(),legacyId=LegacyId();var item=state.installs.OrderByDescending(i=>Recent(i).Count>0).ThenByDescending(i=>i.legacy).ThenByDescending(i=>i.lastSeen).FirstOrDefault();var result=new BrowserExtensionStatus{bundledVersion=latest,availableVersion=latest,legacyId=legacyId,requiresBootstrap=!string.IsNullOrEmpty(legacyId)&&!state.installs.Any(i=>Same(i.path,legacy)),path=item==null?fixedPath:item.path};
   if(item==null){result.summary=result.requiresBootstrap?"检测到旧版插件，首次升级需要确认并重新加载一次。":"尚未安装浏览器插件。点“安装 / 修复”准备插件。";return result;}
   // The selected installation still controls the shown path and versions;
   // completion and safety must account for every tracked installation/profile.
   var clients=Recent(item);string known=latest;foreach(var installation in state.installs){string version=MaximumKnownVersion(installation);if(CompareVersions(version,known)>0)known=version;}
   var waiting=state.installs.Where(i=>i.pending!=null||i.awaitingReload).ToList();result.installedVersion=item.version;result.version=clients.Count>0?clients.OrderByDescending(c=>c.seen).First().version:"";result.profiles=state.installs.Max(i=>Recent(i).Count);result.connected=result.profiles>0;result.pending=waiting.Count>0;result.retryUpdate=result.pending&&waiting.All(i=>i.pending!=null?i.pending.expires<now().Ticks:now().Ticks-i.lastApplied>TokenTicks);result.availableVersion=known;
   result.upToDate=result.connected&&!result.pending&&state.installs.All(i=>CompareVersions(i.version,latest)==0&&!string.IsNullOrEmpty(i.activeVersion)&&CompareVersions(i.activeVersion,latest)==0&&Recent(i).All(c=>CompareVersions(c.version,latest)==0));
   result.summary=CompareVersions(known,latest)>0?"浏览器插件版本较新，已保留现有版本，不会降级。请先更新 Mori Desk 本机软件。":result.profiles>1&&!result.upToDate?"检测到多个浏览器或用户配置正在使用插件。请只保留一个后重试更新。":result.retryUpdate?"上次等待浏览器更新已超时，请点“一键更新”重试。":result.pending&&waiting.All(i=>i.pending==null)?"插件文件已更新，等待浏览器重新加载并确认新版本。":result.pending?(result.connected?"更新已准备好，等待当前收藏完成并关闭插件弹窗。":"更新已准备好。请打开已连接的浏览器；首次安装需先加载插件。"):result.connected?(result.upToDate?"浏览器已连接，插件已是最新版本。":"部分浏览器插件尚未确认最新版本，请点“一键更新”。"):"插件文件已准备好，等待浏览器连接。首次使用请在扩展管理页加载插件。";return result;
  }
  internal BrowserExtensionStatus Status(){return Locked(()=>MakeStatus(Load()));}
  internal BrowserExtensionStatus EnsureInstalled(){return Locked(()=>{var state=Load();var item=state.installs.FirstOrDefault(i=>!i.legacy);if(item==null&&state.installs.Count==0){if(Directory.Exists(fixedPath))throw new IOException("固定插件目录已存在，但安装记录缺失。为保护原文件，未覆盖该目录。");item=new BrowserExtensionInstall{installationId=RandomHex(),extensionId=KeyId(key),manifestKey=key,path=fixedPath,version=BundleVersion()};state.installs.Add(item);item.pending=Stage(item);Commit(state,item,"");}foreach(var known in state.installs){if(!Directory.Exists(known.path)){known.pending=Stage(known);Commit(state,known,"");}register(known.extensionId);Pair(known,known.path);}return MakeStatus(state);});}
  internal BrowserExtensionStatus RequestUpdate(){return Locked(()=>{var state=Load();if(state.installs.Count==0)throw new InvalidOperationException("请先点“安装 / 修复”。旧版插件首次升级需确认并重新加载一次。");string version=BundleVersion();foreach(var item in state.installs)GuardVersion(item,version);bool changed=false;foreach(var item in state.installs){if(InstalledMatchesBundle(item,version))continue;bool prepare=item.pending==null||item.pending.expires<now().Ticks||item.pending.version!=version;if(!prepare)try{VerifyStage(item);}catch(InvalidDataException){prepare=true;}catch(IOException){prepare=true;}if(prepare){item.pending=Stage(item);changed=true;}}if(changed)Save(state);return MakeStatus(state);});}
  internal BrowserExtensionStatus BootstrapLegacy(string extensionId){return Locked(()=>{var state=Load();string legacyId=LegacyId();if(!Id(extensionId)||legacyId!=extensionId)throw new InvalidOperationException("无法确认旧插件的安装身份，未修改旧插件。请使用固定目录重新安装。");var item=state.installs.FirstOrDefault(i=>i.legacy);if(item!=null&&Recent(item).Count>1)throw new InvalidOperationException("请关闭其他浏览器或用户配置后再更新。");if(item==null){var manifest=ManifestAt(legacy);object raw;item=new BrowserExtensionInstall{installationId=RandomHex(),extensionId=legacyId,path=legacy,legacy=true,manifestKey=manifest.TryGetValue("key",out raw)?raw as string:null,version=Convert.ToString(manifest["version"])};state.installs.Add(item);}item.pending=Stage(item);Commit(state,item,"");register(item.extensionId);Pair(item,item.path);return MakeStatus(state);});}
  internal void RegisterKnown(){Locked(()=>{var state=Load();foreach(var item in state.installs)register(item.extensionId);foreach(var item in state.installs)if(Directory.Exists(item.path))Pair(item,item.path);return true;});}
  internal void RefreshPairing(){Locked(()=>{var state=Load();foreach(var item in state.installs)if(Directory.Exists(item.path))Pair(item,item.path);return true;});}
  internal object BridgeStatus(Dictionary<string,object> request){try{return Locked<object>(()=>{string sender=Value(request,"_extensionId");var state=Load();if(!Id(sender)||!allowedIds().Contains(sender,StringComparer.Ordinal))return Failure("浏览器插件尚未连接本机程序。");return new{ok=true,protocol=1,appVersion=typeof(Entry).Assembly.GetName().Version.ToString(),extension=MakeStatus(state)};});}catch(Exception ex){return Failure(ex.Message);}}
  internal object Handle(Dictionary<string,object> request){if(Value(request,"action")=="bridgeStatus")return BridgeStatus(request);try{return Locked<object>(()=>{string sender=Value(request,"_extensionId"),identity=Value(request,"installationId"),client=Value(request,"clientId"),reported=Value(request,"version"),op=Value(request,"op");if(Value(request,"action")!="extensionControl"||!Id(sender)||!allowedIds().Contains(sender,StringComparer.Ordinal)||!Hex(identity,32)||!Hex(client,32)||!ValidVersion(reported)||(op!="poll"&&op!="apply"))return Failure("插件更新请求不完整，请重新加载插件。");var state=Load();var item=state.installs.SingleOrDefault(i=>i.installationId==identity&&i.extensionId==sender);if(item==null)return Failure("插件安装身份不匹配，请在 Mori 中重新连接。");string declared=Value(request,"extensionId");if(declared!=null&&declared!=sender)return Failure("插件身份不匹配。");
   item.clients=Recent(item);var session=item.clients.FirstOrDefault(c=>c.id==client);if(session==null){if(item.clients.Count>=32)return Failure("同时连接的浏览器过多，请关闭其他窗口后重试。");session=new BrowserExtensionClient{id=client};item.clients.Add(session);}session.seen=now().Ticks;session.version=reported;item.activeVersion=reported;item.lastSeen=session.seen;
   if(op=="poll"){if(reported==item.version)item.awaitingReload=false;Save(state);object offer=null;if(item.pending!=null&&item.pending.expires>=now().Ticks)offer=new{token=item.pending.token,version=item.pending.version};else if(item.awaitingReload&&item.lastVersion==item.version&&now().Ticks-item.lastApplied<=TokenTicks&&client==item.lastClient)offer=new{token=item.lastToken,version=item.lastVersion};return new{ok=true,update=offer,version=item.version,multipleClients=item.clients.Count>1};}
   string token=Value(request,"token");if(!Hex(token,64))return Failure("更新令牌无效，请重新检查更新。");if(item.pending==null){if(token==item.lastToken&&client==item.lastClient&&now().Ticks-item.lastApplied<=TokenTicks)return new{ok=true,reload=true,version=item.lastVersion};return Failure("本次更新已结束，请重新检查更新。");}
   if(token!=item.pending.token)return Failure("更新令牌不匹配，未修改插件。");if(item.clients.Count>1){Save(state);return Failure("多个浏览器或用户配置正在使用插件。请关闭其他浏览器，等待片刻后重试。");}Commit(state,item,client);return new{ok=true,reload=true,version=item.version};
  });}catch(Exception ex){return Failure(ex.Message);}}
 }
}
