using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MoriDesk {
 // Optional, post-commit integration. The stable launcher is never rebuilt or
 // replaced for this feature. Explorer loads immutable files outside app
 // version folders, so an open Explorer cannot retain a superseded app version.
 public static class ShellVisualRegistration {
  public const string IconHandlerClsid="{612E3A5A-3C2C-4709-8C54-9C3EA927C086}";
  public const string SvgThumbnailClsid="{FA45A458-130E-47BA-AAE0-1202D6930A79}";
  internal const string ThumbnailCategory="{e357fccd-a995-4576-b01f-234630154e96}";
  internal const string ApplicationKey="Software\\Classes\\Applications\\Mori.exe";
  internal const string IconHandlerKey=ApplicationKey+"\\shellex\\IconHandler";
  internal const string SvgHandlerKey="Software\\Classes\\SystemFileAssociations\\.svg\\shellex\\"+ThumbnailCategory;
  internal const string PayloadDirectory="shell-preview", ManifestName="manifest.json", DllName="Nook.ShellPreview.dll";
  const string Owner="Nook shell visuals v1";
  const int MaximumManifest=128*1024,MaximumFiles=512;
  const long MaximumFile=128L*1024*1024,MaximumTotal=256L*1024*1024;
  static readonly StringComparer Names=StringComparer.OrdinalIgnoreCase;
  static readonly Regex Digest=new Regex("\\A[0-9a-f]{64}\\z",RegexOptions.CultureInvariant);
  static readonly Regex Segment=new Regex("\\A[a-zA-Z0-9][a-zA-Z0-9_.-]*\\z",RegexOptions.CultureInvariant);
  [DllImport("shell32.dll")]static extern void SHChangeNotify(uint eventId,uint flags,IntPtr first,IntPtr second);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder name,uint size,uint flags);

  public static bool RepairCommittedInstallation(string executable){
   PhysicalUserContext.RequireInstalledContext();
   if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")))return false;
   // A candidate installer or auxiliary executable cannot register its payload.
   if(!Names.Equals(Path.GetFullPath(executable),Path.GetFullPath(Assembly.GetExecutingAssembly().Location)))return false;
   return RepairInScope(Registry.CurrentUser,Registry.LocalMachine,executable,InstalledApplicationState.ProgramRoot,()=>SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero),null);
  }

  static string ClassKey(string id){return "Software\\Classes\\CLSID\\"+id;}
  static string Root(string value){return InstalledApplicationState.Root(value);}
  static bool Same(string a,string b){return a!=null&&b!=null&&Names.Equals(a,b);}
  static object Value(RegistryKey scope,string path,string name){using(var key=scope.OpenSubKey(path))return key==null?null:key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);}
  static string Text(RegistryKey scope,string path,string name){return Value(scope,path,name) as string;}
  static bool HasKey(RegistryKey scope,string path){using(var key=scope.OpenSubKey(path))return key!=null;}
  static void Plain(string path){
   for(string part=Path.GetFullPath(path);!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part))
    if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new IOException("文件图标组件路径不能含链接。");
  }
  static bool ComponentPath(string directory,string programRoot){
   if(string.IsNullOrWhiteSpace(directory))return false;
   string folder=Path.GetFullPath(directory),parent=Path.Combine(Root(programRoot),"Launcher","Shell");
   return Names.Equals(Path.GetDirectoryName(folder),parent)&&Digest.IsMatch(Path.GetFileName(folder));
  }
  static bool OwnClass(RegistryKey scope,string id,string programRoot,bool requireFiles){
   string key=ClassKey(id),folder=Text(scope,key,"NookComponentDirectory");
   if(Text(scope,key,"NookOwner")!=Owner||!Same(Text(scope,key,"NookProgramRoot"),Root(programRoot))||!ComponentPath(folder,programRoot))return false;
   if(!Same(Text(scope,key+"\\InprocServer32",""),Path.Combine(folder,DllName))||Text(scope,key+"\\InprocServer32","ThreadingModel")!="Apartment")return false;
   if(requireFiles){Plain(Path.Combine(folder,DllName));if(!File.Exists(Path.Combine(folder,ManifestName)))return false;}
   return true;
  }
  // A cheap startup check used by regular Open-with repair. Full inventory
  // validation happens in RepairInScope, not every time an icon is requested.
  public static bool IsOwnedIconHandler(RegistryKey scopeRoot,string programRoot){
   if(scopeRoot==null)return false;
   try{return Same(Text(scopeRoot,IconHandlerKey,""),IconHandlerClsid)&&OwnClass(scopeRoot,IconHandlerClsid,programRoot,true);}
   catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(ArgumentException){return false;}catch(System.Security.SecurityException){return false;}
  }

  internal sealed class ManifestFile {public string path,sha256;public long length;}
  internal sealed class Manifest {public int schemaVersion;public string componentVersion;public ManifestFile[] files;}
  static string Relative(string path){
   if(string.IsNullOrWhiteSpace(path)||path.Length>240||path.IndexOf('\\')>=0||path.IndexOf(':')>=0)throw new InvalidDataException("文件图标清单路径无效。");
   string[] parts=path.Split('/');
   if(parts.Length>8||parts.Any(p=>!Segment.IsMatch(p)||p=="."||p==".."||p.EndsWith(".",StringComparison.Ordinal)||Regex.IsMatch(p,"\\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|\\z)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)))throw new InvalidDataException("文件图标清单路径不明确。");
   return path.Replace('/',Path.DirectorySeparatorChar);
  }
  static string Resolve(string root,string path){return Path.Combine(root,Relative(path));}
  static string Hash(Stream stream){stream.Position=0;using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
  static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
  static FileStream OpenFile(string path){
   Plain(path);var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
   try{var final=new StringBuilder(32768);uint size=GetFinalPathNameByHandle(stream.SafeFileHandle,final,(uint)final.Capacity,0);if(size==0||size>=final.Capacity||!PhysicalUserContext.SamePhysicalFile(path,final.ToString()))throw new IOException("文件图标组件的实际路径不匹配。");return stream;}catch{stream.Dispose();throw;}
  }
  static byte[] Read(FileStream input,int maximum){
   if(input.Length<1||input.Length>maximum)throw new InvalidDataException("文件图标清单大小无效。");input.Position=0;byte[] result=new byte[(int)input.Length];int at=0;while(at<result.Length){int n=input.Read(result,at,result.Length-at);if(n==0)throw new EndOfStreamException();at+=n;}return result;
  }
  static void Fields(Dictionary<string,object> value,params string[] expected){if(value==null||value.Count!=expected.Length||expected.Any(k=>!value.ContainsKey(k)))throw new InvalidDataException("文件图标清单字段不完整。");}
  // Validate duplicate property names, including JSON-escaped spellings,
  // before JavaScriptSerializer can silently keep the final value.
  sealed class JsonKeys {
   readonly string text;int at;readonly JavaScriptSerializer json=new JavaScriptSerializer();
   internal JsonKeys(string value){text=value;}
   void Space(){while(at<text.Length&&char.IsWhiteSpace(text[at]))at++;}
   string Token(){int begin=at;if(at>=text.Length||text[at++]!='"')throw new InvalidDataException("清单字符串无效。");bool end=false;while(at<text.Length){char c=text[at++];if(c=='\\'){if(at<text.Length)at++;}else if(c=='"'){end=true;break;}}if(!end)throw new InvalidDataException("清单字符串不完整。");return json.Deserialize<string>(text.Substring(begin,at-begin));}
   void Item(int depth){
    if(depth>8)throw new InvalidDataException("清单层数过多。");Space();if(at>=text.Length)throw new InvalidDataException("清单不完整。");char c=text[at];
    if(c=='{'){at++;Space();var names=new HashSet<string>(StringComparer.Ordinal);if(at<text.Length&&text[at]=='}'){at++;return;}while(true){Space();if(!names.Add(Token()))throw new InvalidDataException("清单含重复字段。");Space();if(at>=text.Length||text[at++]!=':')throw new InvalidDataException("清单字段无效。");Item(depth+1);Space();if(at>=text.Length)throw new InvalidDataException("清单不完整。");char end=text[at++];if(end=='}')return;if(end!=',')throw new InvalidDataException("清单字段无效。");}}
    if(c=='['){at++;Space();if(at<text.Length&&text[at]==']'){at++;return;}while(true){Item(depth+1);Space();if(at>=text.Length)throw new InvalidDataException("清单不完整。");char end=text[at++];if(end==']')return;if(end!=',')throw new InvalidDataException("清单数组无效。");}}
    if(c=='"'){Token();return;}int begin=at;while(at<text.Length&&text[at]!=','&&text[at]!='}'&&text[at]!=']'&&!char.IsWhiteSpace(text[at]))at++;if(begin==at)throw new InvalidDataException("清单值无效。");
   }
   internal void Check(){Item(0);Space();if(at!=text.Length)throw new InvalidDataException("清单含多余内容。");}
  }
  internal static Manifest ParseManifest(byte[] bytes){
   if(bytes==null||bytes.Length<1||bytes.Length>MaximumManifest)throw new InvalidDataException("文件图标清单大小无效。");
   try{
    string text=new UTF8Encoding(false,true).GetString(bytes);new JsonKeys(text).Check();var json=new JavaScriptSerializer{MaxJsonLength=MaximumManifest,RecursionLimit=8};var data=json.DeserializeObject(text) as Dictionary<string,object>;Fields(data,"schemaVersion","componentVersion","files");
    if(!(data["schemaVersion"] is int)||(int)data["schemaVersion"]!=1||!(data["componentVersion"] is string)||!Regex.IsMatch((string)data["componentVersion"],"\\A[0-9][a-zA-Z0-9_.-]{0,39}\\z",RegexOptions.CultureInvariant))throw new InvalidDataException("文件图标组件版本无效。");
    var entries=data["files"] as object[];if(entries==null||entries.Length<4||entries.Length>MaximumFiles)throw new InvalidDataException("文件图标组件清单数量无效。");
    var files=new List<ManifestFile>();var names=new HashSet<string>(Names);long total=0;
    foreach(object raw in entries){var entry=raw as Dictionary<string,object>;Fields(entry,"path","sha256","length");if(!(entry["path"] is string)||!(entry["sha256"] is string)||!(entry["length"] is int||entry["length"] is long))throw new InvalidDataException("文件图标组件文件记录无效。");string path=(string)entry["path"];Relative(path);long length=Convert.ToInt64(entry["length"]);if(length<1||length>MaximumFile||!Digest.IsMatch((string)entry["sha256"])||!names.Add(path)||Names.Equals(path,ManifestName))throw new InvalidDataException("文件图标组件清单含重复或无效文件。");total+=length;if(total>MaximumTotal)throw new InvalidDataException("文件图标组件过大。");files.Add(new ManifestFile{path=path,sha256=(string)entry["sha256"],length=length});}
    foreach(string required in new[]{DllName,"resvg.exe","icons/app.ico","icons/file.ico"})if(!names.Contains(required))throw new InvalidDataException("文件图标组件缺少必要文件："+required);
    // A file may not also serve as the parent directory of another entry.
    foreach(string path in names)for(string parent=path.Contains('/')?path.Substring(0,path.LastIndexOf('/')):null;parent!=null;parent=parent.Contains('/')?parent.Substring(0,parent.LastIndexOf('/')):null)if(names.Contains(parent))throw new InvalidDataException("清单文件与文件夹冲突。");
    return new Manifest{schemaVersion=1,componentVersion=(string)data["componentVersion"],files=files.ToArray()};
   }catch(InvalidDataException){throw;}catch(ArgumentException error){throw new InvalidDataException("文件图标组件清单无法读取。",error);}catch(InvalidOperationException error){throw new InvalidDataException("文件图标组件清单无法读取。",error);}
  }

  sealed class Component:IDisposable {
   internal readonly Dictionary<string,FileStream> Files=new Dictionary<string,FileStream>(Names);
   readonly List<IDisposable> paths=new List<IDisposable>();readonly HashSet<string> directories=new HashSet<string>(Names);
   internal readonly string DirectoryPath;internal Manifest Manifest;internal byte[] Bytes;internal string Digest;
   internal Component(string folder){DirectoryPath=folder;try{Scan(folder,"");FileStream manifest;if(!Files.TryGetValue(ManifestName,out manifest))throw new InvalidDataException("文件图标组件清单缺失。");Bytes=Read(manifest,MaximumManifest);Manifest=ParseManifest(Bytes);Digest=Hash(Bytes);var expected=new HashSet<string>(Manifest.files.Select(f=>f.path),Names);expected.Add(ManifestName);if(!expected.SetEquals(Files.Keys))throw new InvalidDataException("文件图标组件含缺失或清单外文件。");var expectedDirs=new HashSet<string>(Names);foreach(string path in Manifest.files.Select(f=>f.path))for(int slash=path.IndexOf('/');slash>=0;slash=path.IndexOf('/',slash+1))expectedDirs.Add(path.Substring(0,slash));if(!expectedDirs.SetEquals(directories))throw new InvalidDataException("文件图标组件含清单外文件夹。");foreach(var file in Manifest.files)if(Files[file.path].Length!=file.length||Hash(Files[file.path])!=file.sha256)throw new InvalidDataException("文件图标组件校验失败。");}catch{Dispose();throw;}}
   void Scan(string folder,string relative){
    if(Files.Count+directories.Count>MaximumFiles*2)throw new InvalidDataException("文件图标组件文件过多。");Plain(folder);paths.Add(new InstalledApplicationState.PathLocks(folder));
    foreach(string file in Directory.EnumerateFileSystemEntries(folder)){if(Files.Count+directories.Count>MaximumFiles*2)throw new InvalidDataException("文件图标组件文件过多。");string name=(relative.Length==0?"":relative+"/")+Path.GetFileName(file);Relative(name);var attributes=File.GetAttributes(file);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("文件图标组件不能含链接。");if((attributes&FileAttributes.Directory)!=0){directories.Add(name);Scan(file,name);}else{Files.Add(name,OpenFile(file));}}
   }
   public void Dispose(){foreach(var file in Files.Values)file.Dispose();for(int i=paths.Count-1;i>=0;i--)paths[i].Dispose();Files.Clear();paths.Clear();}
  }
  static void WriteCopy(string path,FileStream input){input.Position=0;using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}}
  static string PublishComponent(string programRoot,Component source){
   string parent=Path.Combine(programRoot,"Launcher","Shell"),target=Path.Combine(parent,source.Digest);Directory.CreateDirectory(parent);
   using(new InstalledApplicationState.PathLocks(parent)){
    // The caller validates and pins the final published component before any
    // registry write. Avoid hashing the same existing payload twice here.
    if(Directory.Exists(target))return target;
    string staging=Path.Combine(parent,".stage-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);var created=new List<string>();var directories=new HashSet<string>(Names);
    try{
     using(new InstalledApplicationState.PathLocks(staging))foreach(var file in source.Files){string output=Resolve(staging,file.Key),directory=Path.GetDirectoryName(output);Directory.CreateDirectory(directory);for(string p=directory;!Names.Equals(p,staging);p=Path.GetDirectoryName(p))directories.Add(p);using(new InstalledApplicationState.PathLocks(directory)){WriteCopy(output,file.Value);created.Add(output);}}
     using(var check=new Component(staging)){if(check.Digest!=source.Digest)throw new InvalidDataException("文件图标组件复制校验失败。");}
     Directory.Move(staging,target);return target;
    }finally{
     // Staging folders were never registered with Explorer. Only our exact,
     // newly-created files may be removed; immutable published folders stay.
     if(Directory.Exists(staging)){Plain(staging);foreach(string path in created){Plain(path);File.Delete(path);}foreach(string path in directories.OrderByDescending(p=>p.Length)){Plain(path);if(!Directory.EnumerateFileSystemEntries(path).Any())Directory.Delete(path);}if(!Directory.EnumerateFileSystemEntries(staging).Any())Directory.Delete(staging);}
    }
   }
  }

  sealed class Snapshot {
   internal bool Exists;internal RegistryValueKind Kind;internal object Value;
   internal static Snapshot Read(RegistryKey scope,string path,string name){using(var key=scope.OpenSubKey(path)){if(key==null||!key.GetValueNames().Contains(name,StringComparer.OrdinalIgnoreCase))return new Snapshot();return new Snapshot{Exists=true,Kind=key.GetValueKind(name),Value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames)};}}
   internal bool EqualsValue(Snapshot other){if(Exists!=other.Exists)return false;if(!Exists)return true;if(Kind!=other.Kind)return false;var a=Value as Array;var b=other.Value as Array;if(a!=null||b!=null)return a!=null&&b!=null&&a.Cast<object>().SequenceEqual(b.Cast<object>());return object.Equals(Value,other.Value);}
  }
  sealed class Mutation {internal string Path,Name;internal Snapshot Before,After;}
  sealed class Transaction {
   readonly RegistryKey scope;readonly Action<int> fault;readonly List<Mutation> changes=new List<Mutation>();readonly HashSet<string> created=new HashSet<string>(Names);
   internal Transaction(RegistryKey root,Action<int> afterWrite){scope=root;fault=afterWrite;}
   internal bool Changed {get{return changes.Count!=0;}}
   internal void Put(string path,string name,string value){Change(path,name,new Snapshot{Exists=true,Kind=RegistryValueKind.String,Value=value});}
   internal void PutExpected(string path,string name,string value,Snapshot expected){if(!Snapshot.Read(scope,path,name).EqualsValue(expected))throw new IOException("文件预览注册已被其他程序修改，稍后重试。");Put(path,name,value);}
   internal void Remove(string path,string name){Change(path,name,new Snapshot());}
   void Change(string path,string name,Snapshot desired){var before=Snapshot.Read(scope,path,name);if(before.EqualsValue(desired))return;for(string p=path;!string.IsNullOrEmpty(p);p=p.Contains('\\')?p.Substring(0,p.LastIndexOf('\\')):null)if(!HasKey(scope,p))created.Add(p);var change=new Mutation{Path=path,Name=name,Before=before,After=desired};changes.Add(change);using(var key=scope.CreateSubKey(path)){if(desired.Exists)key.SetValue(name,desired.Value,desired.Kind);else key.DeleteValue(name,false);}if(fault!=null)fault(changes.Count);}
   internal void Rollback(){
    // Preserve concurrent third-party edits, including a competing provider.
    for(int i=changes.Count-1;i>=0;i--){var item=changes[i];try{if(!Snapshot.Read(scope,item.Path,item.Name).EqualsValue(item.After))continue;using(var key=scope.CreateSubKey(item.Path)){if(item.Before.Exists)key.SetValue(item.Name,item.Before.Value,item.Before.Kind);else key.DeleteValue(item.Name,false);}}catch(IOException){}catch(UnauthorizedAccessException){}catch(System.Security.SecurityException){}}
    foreach(string path in created.OrderByDescending(p=>p.Length))PruneEmpty(scope,path);
   }
  }
  static void PruneEmpty(RegistryKey scope,string path){try{using(var key=scope.OpenSubKey(path)){if(key==null||key.ValueCount!=0||key.SubKeyCount!=0)return;}scope.DeleteSubKey(path,false);}catch(IOException){}catch(UnauthorizedAccessException){}catch(System.Security.SecurityException){}catch(InvalidOperationException){}}
  static bool CanOwnClass(RegistryKey user,RegistryKey machine,string id,string root){return !HasKey(machine,ClassKey(id))&&(!HasKey(user,ClassKey(id))||OwnClass(user,id,root,false));}
  static void RegisterClass(Transaction tx,string id,string description,string root,string component){string key=ClassKey(id);tx.Put(key,"",description);tx.Put(key,"NookOwner",Owner);tx.Put(key,"NookProgramRoot",root);tx.Put(key,"NookComponentDirectory",component);tx.Put(key+"\\InprocServer32","",Path.Combine(component,DllName));tx.Put(key+"\\InprocServer32","ThreadingModel","Apartment");}
  static string Effective(RegistryKey user,RegistryKey machine,string path,string name){object value=Value(user,path,name);return value==null?Text(machine,path,name):value as string;}
  static bool Foreign(RegistryKey user,RegistryKey machine,string path,string allowed){foreach(var scope in new[]{user,machine}){object value=Value(scope,path,"");if(value!=null&&(!(value is string)||!Same((string)value,allowed)))return true;}return false;}
  static bool SafeProgId(string value){
   if(string.IsNullOrEmpty(value)||value.Length>240||value.IndexOfAny(new[]{'/',':','\0','\r','\n'})>=0)return false;
   // Windows also stores Applications\Mori.exe as a selected handler, rather
   // than a conventional dot-separated ProgID. Include that documented form.
   if(value.StartsWith("Applications\\",StringComparison.OrdinalIgnoreCase)){
    value=value.Substring("Applications\\".Length);
    if(value.Length<=4||!value.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||value.IndexOfAny(Path.GetInvalidFileNameChars())>=0)return false;
   }
   return value.Length!=0&&value.IndexOf('\\')<0;
  }
  // Windows installs this general image fallback even when SVG has no codec.
  // It is not evidence of an SVG-specific provider. Exempt only the verified
  // machine registration; per-user overrides and any custom server stay owned
  // by their existing provider. Dedicated SVG/ProgID paths never use this rule.
  static bool WindowsGenericImageFallback(RegistryKey user,RegistryKey machine,string handler){
   const string generic="Software\\Classes\\SystemFileAssociations\\image\\shellex\\"+ThumbnailCategory;
   const string windowsImage="{C7657C4A-9F68-40fa-A4DF-96BC08EB3551}";
   if(!Same(handler,generic)||Value(user,handler,"")!=null||!Same(Text(machine,handler,""),windowsImage))return false;
   string cls=ClassKey(windowsImage),server=cls+"\\InprocServer32";
   if(HasKey(user,cls)||HasKey(machine,cls+"\\TreatAs")||!Same(Text(machine,server,"ThreadingModel"),"Both"))return false;
   string path=Text(machine,server,"");if(string.IsNullOrWhiteSpace(path))return false;
   try{return Path.IsPathRooted(Environment.ExpandEnvironmentVariables(path))&&Same(Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)),Path.Combine(Environment.SystemDirectory,"PhotoMetadataHandler.dll"));}
   catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(IOException){return false;}
  }
  internal static bool HasForeignSvgProvider(RegistryKey user,RegistryKey machine){
   var paths=new HashSet<string>(Names){"Software\\Classes\\.svg","Software\\Classes\\SystemFileAssociations\\.svg","Software\\Classes\\SystemFileAssociations\\image"};
   string selected=Text(user,"Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FileExts\\.svg\\UserChoice","ProgId");string defaultId=Effective(user,machine,"Software\\Classes\\.svg","");
   foreach(string start in new[]{selected,defaultId}){string id=start;var seen=new HashSet<string>(Names);for(int i=0;i<8&&SafeProgId(id)&&seen.Add(id);i++){paths.Add("Software\\Classes\\"+id);id=Effective(user,machine,"Software\\Classes\\"+id+"\\CurVer","");}}
   string perceived=Effective(user,machine,"Software\\Classes\\.svg","PerceivedType");if(SafeProgId(perceived))paths.Add("Software\\Classes\\SystemFileAssociations\\"+perceived);
   return paths.Select(path=>path+"\\shellex\\"+ThumbnailCategory).Any(handler=>Foreign(user,machine,handler,SvgThumbnailClsid)&&!WindowsGenericImageFallback(user,machine,handler));
  }

  // Test callers pass disposable sibling HKCU subtrees for BOTH scopes. This
  // method never consults HKCR/HKLM implicitly, so fixtures cannot touch live keys.
  internal static bool RepairInScope(RegistryKey user,RegistryKey machine,string executable,string programRoot,Action notify,Action<int> afterWrite){
   if(user==null||machine==null)throw new ArgumentNullException();programRoot=Root(programRoot);
   using(var active=InstalledApplicationState.OpenActive(programRoot,1000)){
    if(!Names.Equals(Path.GetFullPath(executable),active.Executable))return false;
    string launcher=InstalledApplicationState.LauncherPath(programRoot);Plain(launcher);
    using(new InstalledApplicationState.PathLocks(Path.GetDirectoryName(launcher)))using(OpenFile(launcher))
    using(var mutex=new Mutex(false,OpenWithRegistration.MutexName(user))){bool held=false;try{try{held=mutex.WaitOne(1000);}catch(AbandonedMutexException){held=true;}if(!held)return false;
     if(Text(user,ApplicationKey,"FriendlyAppName")!="Nook"||Text(user,ApplicationKey+"\\shell\\open\\command","")!="\""+launcher+"\" \"%1\"")return false;
     bool icons=!Foreign(user,machine,IconHandlerKey,IconHandlerClsid)&&CanOwnClass(user,machine,IconHandlerClsid,programRoot);
     bool svg=!HasForeignSvgProvider(user,machine)&&CanOwnClass(user,machine,SvgThumbnailClsid,programRoot);
     if(!icons&&!svg)return false;
     string component;using(var source=new Component(Path.Combine(Path.GetDirectoryName(active.Executable),PayloadDirectory)))component=PublishComponent(programRoot,source);
     using(var verified=new Component(component)){
      if(verified.Digest!=Path.GetFileName(component))throw new InvalidDataException("现有文件图标组件身份不匹配。");
      // Another application's installer does not share our mutex. Re-evaluate
      // ownership after disk work, and compare targets again at the write.
      icons=icons&&!Foreign(user,machine,IconHandlerKey,IconHandlerClsid)&&CanOwnClass(user,machine,IconHandlerClsid,programRoot);
      svg=svg&&!HasForeignSvgProvider(user,machine)&&CanOwnClass(user,machine,SvgThumbnailClsid,programRoot);
      var expectedIcon=Snapshot.Read(user,IconHandlerKey,"");var expectedDefault=Snapshot.Read(user,ApplicationKey+"\\DefaultIcon","");var expectedSvg=Snapshot.Read(user,SvgHandlerKey,"");
      var tx=new Transaction(user,afterWrite);try{
       if(icons){RegisterClass(tx,IconHandlerClsid,"Nook file format icons",programRoot,component);tx.Put(ApplicationKey+"\\Application","ApplicationIcon","\""+launcher+"\",0");if(Foreign(user,machine,IconHandlerKey,IconHandlerClsid))throw new IOException("另一个程序刚刚更改了文件图标，已保留其设置。");tx.PutExpected(IconHandlerKey,"",IconHandlerClsid,expectedIcon);tx.PutExpected(ApplicationKey+"\\DefaultIcon","","%1",expectedDefault);}
       if(svg){if(!CanOwnClass(user,machine,SvgThumbnailClsid,programRoot))throw new IOException("缩略图组件注册刚刚改变，已保留其设置。");RegisterClass(tx,SvgThumbnailClsid,"Nook SVG thumbnails",programRoot,component);if(HasForeignSvgProvider(user,machine))throw new IOException("另一个程序刚刚添加了 SVG 缩略图，已保留其设置。");tx.PutExpected(SvgHandlerKey,"",SvgThumbnailClsid,expectedSvg);}
      }catch{tx.Rollback();throw;}
      if(tx.Changed&&notify!=null)notify();return tx.Changed;
     }
    }finally{if(held)mutex.ReleaseMutex();}}
   }
  }

  // Future uninstall/repair hook. Registry only: never delete a DLL that
  // Explorer or its isolated thumbnail process could still have loaded.
  internal static bool RemoveOwnedInScope(RegistryKey user,string programRoot,Action notify,Action<int> afterWrite){
   programRoot=Root(programRoot);using(InstalledApplicationState.AcquireLease(programRoot,1000))using(var mutex=new Mutex(false,OpenWithRegistration.MutexName(user))){bool held=false;try{try{held=mutex.WaitOne(1000);}catch(AbandonedMutexException){held=true;}if(!held)return false;
    var tx=new Transaction(user,afterWrite);var prune=new List<string>();try{
     foreach(string id in new[]{IconHandlerClsid,SvgThumbnailClsid}){
      if(!OwnClass(user,id,programRoot,false))continue;string key=ClassKey(id),handler=id==IconHandlerClsid?IconHandlerKey:SvgHandlerKey;
      if(Same(Text(user,handler,""),id)){tx.Remove(handler,"");prune.Add(handler);if(id==IconHandlerClsid&&Text(user,ApplicationKey+"\\DefaultIcon","")=="%1")tx.Put(ApplicationKey+"\\DefaultIcon","","\""+InstalledApplicationState.LauncherPath(programRoot)+"\",0");}
      foreach(string name in new[]{"","NookOwner","NookProgramRoot","NookComponentDirectory"})tx.Remove(key,name);foreach(string name in new[]{"","ThreadingModel"})tx.Remove(key+"\\InprocServer32",name);prune.Add(key+"\\InprocServer32");prune.Add(key);
     }
    }catch{tx.Rollback();throw;}
    foreach(string key in prune.OrderByDescending(p=>p.Length))PruneEmpty(user,key);if(tx.Changed&&notify!=null)notify();return tx.Changed;
   }finally{if(held)mutex.ReleaseMutex();}}
  }
 }
}
