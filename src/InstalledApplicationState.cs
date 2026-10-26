using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

namespace MoriDesk {
 public sealed class BusyInstallationException:IOException {
  internal BusyInstallationException():base("另一次 Nook 更新正在进行，请稍后重试。"){}
 }
 public sealed class InstalledApplicationDescriptor {
  public int schemaVersion=1;
  public string generation,executable,version,sha256,payloadSha256;
 }
 // Committed state only. Staging/rollback journal ownership stays with the
 // installer. A missing record is a legacy install; an invalid record is NOT.
 public static class InstalledApplicationState {
  public const string FileName="active-install.json";
  const int MaximumRecordBytes=4096;
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint size,uint flags);
  public static string ProgramRoot {get{PhysicalUserContext.RequireDefault();return Path.Combine(Path.GetDirectoryName(PhysicalUserContext.DefaultRoot),"Programs","MoriDesk");}}
  public static string LauncherPath(string root){return Path.Combine(Root(root),"Launcher","Mori.exe");}
  public static string ResolveExecutable(string root,InstalledApplicationDescriptor value){Validate(value);return Path.Combine(Root(root),value.executable);}
  internal static string Root(string root){
   if(string.IsNullOrWhiteSpace(root)||root.Length<4||!char.IsLetter(root[0])||root[1]!=':'||(root[2]!='\\'&&root[2]!='/')||root.IndexOfAny(new[]{'"','\r','\n','\0'})>=0||root.IndexOf(':',2)>=0)throw new InvalidDataException("安装目录必须是本机绝对地址。");
   string full=Path.GetFullPath(root).TrimEnd('\\','/');
   if(full.Length<4||full.Substring(3).Split('\\','/').Any(p=>p.Length==0||p=="."||p==".."||p.EndsWith(".",StringComparison.Ordinal)||p.EndsWith(" ",StringComparison.Ordinal)))throw new InvalidDataException("安装目录不明确。");
   return full;
  }
  static bool Hex(string text,int length){return text!=null&&text.Length==length&&text.All(c=>(c>='0'&&c<='9')||(c>='a'&&c<='f'));}
  static Version VersionOf(string text){Version value;if(!Version.TryParse(text,out value)||value.Build<0||value.Revision<0||value.ToString(4)!=text)throw new InvalidDataException("安装版本记录无效。");return value;}
  static void Validate(InstalledApplicationDescriptor value){
   if(value==null||value.schemaVersion!=1||!Hex(value.generation,32)||!Hex(value.sha256,64)||!Hex(value.payloadSha256,64))throw new InvalidDataException("安装身份记录无效。");
   VersionOf(value.version);
   if(string.IsNullOrEmpty(value.executable)||value.executable.Length>240)throw new InvalidDataException("安装程序地址无效。");
   string[] parts=value.executable.Split('\\');
   if(parts.Length!=2||parts[1]!="Mori.exe"||parts[0].Length==0||parts[0].Length>180||parts[0]=="Launcher"||parts[0][0]<'0'||parts[0][0]>'9'||parts[0].Any(c=>!((c>='0'&&c<='9')||(c>='a'&&c<='z')||(c>='A'&&c<='Z')||c=='.'||c=='-'))||parts[0].EndsWith(".",StringComparison.Ordinal))throw new InvalidDataException("安装程序必须属于单层版本目录。");
  }
  static InstalledApplicationDescriptor Copy(InstalledApplicationDescriptor value){return value==null?null:new InstalledApplicationDescriptor{schemaVersion=value.schemaVersion,generation=value.generation,executable=value.executable,version=value.version,sha256=value.sha256,payloadSha256=value.payloadSha256};}
  sealed class Lease:IDisposable {Mutex mutex;internal Lease(Mutex value){mutex=value;}public void Dispose(){if(mutex==null)return;var value=mutex;mutex=null;value.ReleaseMutex();value.Dispose();}}
  internal static string MutexName(string root){
   root=Root(root);string sid;using(var user=WindowsIdentity.GetCurrent())sid=user.User.Value;
   string key;using(var sha=SHA256.Create())key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(sid+"|"+root.ToUpperInvariant()))).Replace("-","");
   // A user can have more than one Windows session sharing this installation.
   // A session-local mutex would allow both installers to commit concurrently.
   return "Global\\Nook.ActiveInstall."+key;
  }
  public static IDisposable AcquireLease(string root,int timeoutMs=30000){
   if(timeoutMs<0||timeoutMs>120000)throw new ArgumentOutOfRangeException("timeoutMs");var mutex=new Mutex(false,MutexName(root));bool held=false;try{try{held=mutex.WaitOne(timeoutMs);}catch(AbandonedMutexException){held=true;}if(!held)throw new BusyInstallationException();return new Lease(mutex);}catch{mutex.Dispose();throw;}
  }
  internal sealed class PathLocks:IDisposable {
   readonly List<SafeFileHandle> handles=new List<SafeFileHandle>();
   internal PathLocks(string path){try{var paths=new Stack<string>();for(string p=path;!string.IsNullOrEmpty(p);p=Path.GetDirectoryName(p))paths.Push(p);while(paths.Count!=0){string p=paths.Pop();FileAttributes attributes;try{attributes=File.GetAttributes(p);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}if((attributes&FileAttributes.ReparsePoint)!=0||(attributes&FileAttributes.Directory)==0)throw new IOException("安装目录含有链接或无效路径。");var handle=CreateFile(p,0,3,IntPtr.Zero,3,0x02000000,IntPtr.Zero);if(handle.IsInvalid){handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}handles.Add(handle);Physical(handle,p);}}catch{Dispose();throw;}}
   public void Dispose(){for(int i=handles.Count-1;i>=0;i--)handles[i].Dispose();handles.Clear();}
  }
  static void Physical(SafeFileHandle handle,string expected){var text=new StringBuilder(32768);uint n=GetFinalPathNameByHandle(handle,text,(uint)text.Capacity,0);if(n==0||n>=text.Capacity||!PhysicalUserContext.SamePhysicalFile(expected,text.ToString()))throw new IOException("无法确认安装文件的实际位置。");}
  static FileStream OpenFile(string file){var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);try{if((File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0)throw new IOException("安装文件不能是链接。");Physical(stream.SafeFileHandle,file);return stream;}catch{stream.Dispose();throw;}}
  static string Hash(Stream stream){stream.Position=0;using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
  static void Identity(string executable,string expectedVersion){var info=FileVersionInfo.GetVersionInfo(executable);if(info.ProductName!="Nook"||!string.Equals(info.OriginalFilename,"Mori.exe",StringComparison.OrdinalIgnoreCase)||VersionOf(info.FileVersion).ToString(4)!=expectedVersion)throw new InvalidDataException("安装程序身份或版本不匹配。");}
  static FileStream OpenExecutable(string root,InstalledApplicationDescriptor value){string file=ResolveExecutable(root,value);var input=OpenFile(file);try{Identity(file,value.version);if(Hash(input)!=value.sha256)throw new InvalidDataException("安装程序校验失败，未启动或切换版本。");return input;}catch{input.Dispose();throw;}}
  // Flat scalar JSON only, with duplicate keys rejected before deserialization.
  internal static Dictionary<string,object> ParseFlatRecord(string text){
   if(string.IsNullOrWhiteSpace(text)||text.Length>MaximumRecordBytes)throw new InvalidDataException("安装记录大小无效。");
   var json=new JavaScriptSerializer{MaxJsonLength=MaximumRecordBytes,RecursionLimit=4};int at=0;Action space=()=>{while(at<text.Length&&char.IsWhiteSpace(text[at]))at++;};Func<string> token=()=>{int begin=at;if(at>=text.Length)throw new InvalidDataException("安装记录不完整。");if(text[at]=='"'){at++;bool closed=false;while(at<text.Length){char c=text[at++];if(c=='\\'){if(at>=text.Length)break;at++;}else if(c=='"'){closed=true;break;}}if(!closed)throw new InvalidDataException("安装记录字符串不完整。");}else while(at<text.Length&&text[at]!=','&&text[at]!='}'&&!char.IsWhiteSpace(text[at]))at++;return text.Substring(begin,at-begin);};
   var fields=new Dictionary<string,object>(StringComparer.Ordinal);space();if(at>=text.Length||text[at++]!='{')throw new InvalidDataException("安装记录无效。");space();while(at<text.Length&&text[at]!='}'){
    if(text[at]!='"')throw new InvalidDataException("安装记录字段无效。");string name=json.Deserialize<string>(token());space();if(at>=text.Length||text[at++]!=':')throw new InvalidDataException("安装记录字段无效。");space();string raw=token();object value=json.DeserializeObject(raw);if(!(value is string||value is int)||fields.ContainsKey(name))throw new InvalidDataException("安装记录含重复或无效字段。");fields.Add(name,value);space();if(at<text.Length&&text[at]==','){at++;space();if(at>=text.Length||text[at]=='}')throw new InvalidDataException("安装记录无效。");continue;}break;
   }
   if(at>=text.Length||text[at++]!='}')throw new InvalidDataException("安装记录无效。");space();if(at!=text.Length)throw new InvalidDataException("安装记录含多余内容。");return fields;
  }
  static InstalledApplicationDescriptor ReadRecord(string root){
   string file=Path.Combine(root,FileName);FileStream input;try{input=OpenFile(file);}catch(FileNotFoundException){return null;}catch(DirectoryNotFoundException){return null;}
   using(input){if(input.Length<1||input.Length>MaximumRecordBytes)throw new InvalidDataException("安装记录大小无效。");string text;using(var reader=new StreamReader(input,new UTF8Encoding(false,true),false,1024,true))text=reader.ReadToEnd();var fields=ParseFlatRecord(text);string[] keys={"schemaVersion","generation","executable","version","sha256","payloadSha256"};if(fields.Count!=keys.Length||keys.Any(key=>!fields.ContainsKey(key))||!(fields["schemaVersion"] is int)||keys.Skip(1).Any(key=>!(fields[key] is string)))throw new InvalidDataException("安装记录字段不完整。");var result=new InstalledApplicationDescriptor{schemaVersion=(int)fields["schemaVersion"],generation=(string)fields["generation"],executable=(string)fields["executable"],version=(string)fields["version"],sha256=(string)fields["sha256"],payloadSha256=(string)fields["payloadSha256"]};Validate(result);return result;}
  }
  // Read never waits for the installer mutex: a staged candidate must be able
  // to inspect committed state while the installer waits for its receipt.
  public static InstalledApplicationDescriptor Read(string root){root=Root(root);using(new PathLocks(root)){var value=ReadRecord(root);if(value==null)return null;using(new PathLocks(Path.GetDirectoryName(ResolveExecutable(root,value))))using(OpenExecutable(root,value))return Copy(value);}}
  // Installer-only recovery evidence. This does not attest that the old
  // executable is usable and must never be used to launch or register it.
  public static InstalledApplicationDescriptor ReadRecordForRepair(string root){root=Root(root);using(new PathLocks(root))return Copy(ReadRecord(root));}
  public static bool ValidateTransition(InstalledApplicationDescriptor previous,InstalledApplicationDescriptor desired){
   Validate(desired);if(previous==null)return true;Validate(previous);
   int compare=VersionOf(desired.version).CompareTo(VersionOf(previous.version));if(compare<0)throw new InvalidDataException("不能用旧版本替换当前版本。");
   if(compare==0){if(previous.payloadSha256==desired.payloadSha256&&previous.sha256==desired.sha256)return false;throw new InvalidDataException("相同版本号对应不同安装包，必须先提高版本号。");}
   if(desired.generation==previous.generation)throw new InvalidDataException("新版本必须使用新的安装身份。");return true;
  }
  public static InstalledApplicationDescriptor CreateDescriptor(string root,string executable,string payloadSha256){
   root=Root(root);if(!Hex(payloadSha256,64))throw new InvalidDataException("安装包校验值无效。");string file=Path.GetFullPath(executable);if(!file.StartsWith(root+"\\",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("程序不属于此安装目录。");
   using(AcquireLease(root))using(new PathLocks(Path.GetDirectoryName(file)))using(var input=OpenFile(file)){
    var info=FileVersionInfo.GetVersionInfo(file);var value=new InstalledApplicationDescriptor{generation=Guid.NewGuid().ToString("N"),executable=file.Substring(root.Length+1),version=VersionOf(info.FileVersion).ToString(4),sha256=Hash(input),payloadSha256=payloadSha256};Validate(value);Identity(file,value.version);return value;
   }
  }
  public static InstalledApplicationDescriptor Commit(string root,string expectedGeneration,InstalledApplicationDescriptor desired){return CommitCore(root,expectedGeneration,desired,false);}
  // Only for a verified installation archive and explicit repair transaction.
  // Repair replaces the active pointer, never the damaged original files.
  public static InstalledApplicationDescriptor Repair(string root,string expectedGeneration,InstalledApplicationDescriptor desired){return CommitCore(root,expectedGeneration,desired,true);}
  static InstalledApplicationDescriptor CommitCore(string root,string expectedGeneration,InstalledApplicationDescriptor desired,bool repair){
   root=Root(root);desired=Copy(desired);Validate(desired);if(expectedGeneration!=null&&!Hex(expectedGeneration,32))throw new InvalidDataException("预期安装身份无效。");
   using(AcquireLease(root))using(new PathLocks(root)){
    var previous=ReadRecord(root);if((previous==null?null:previous.generation)!=expectedGeneration)throw new IOException("当前安装已改变，未覆盖另一项更新。");
    if(repair&&(previous==null||desired.generation==previous.generation||string.Equals(desired.executable,previous.executable,StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("修复必须使用新的暂存程序和已确认的原安装身份。");
    using(new PathLocks(Path.GetDirectoryName(ResolveExecutable(root,desired))))using(OpenExecutable(root,desired)){
     if(previous!=null){if(!repair){using(new PathLocks(Path.GetDirectoryName(ResolveExecutable(root,previous))))using(OpenExecutable(root,previous)){} }
      if(!ValidateTransition(previous,desired)&&!repair)return Copy(previous);
     }
     string target=Path.Combine(root,FileName),temporary=Path.Combine(root,".active-install-"+Guid.NewGuid().ToString("N")+".tmp");byte[] bytes=new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(desired));
     try{using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){Physical(output.SafeFileHandle,temporary);output.Write(bytes,0,bytes.Length);output.Flush(true);}if(previous==null)File.Move(temporary,target);else File.Replace(temporary,target,null);}finally{if(File.Exists(temporary))File.Delete(temporary);}
     return Copy(desired);
    }
   }
  }
  // Holds the verified payload and its directory against replacement until
  // Process.Start has consumed the selected path. Do not cache this target.
  public sealed class LaunchTarget:IDisposable {
   IDisposable lease;PathLocks paths;FileStream executable;
   public InstalledApplicationDescriptor Descriptor {get;private set;}
   public string Executable {get;private set;}
   internal LaunchTarget(string root,int timeoutMs){try{lease=AcquireLease(root,timeoutMs);using(new PathLocks(root)){Descriptor=ReadRecord(root);if(Descriptor==null)throw new FileNotFoundException("Nook 尚未完成安装，请运行安装器修复。");Executable=ResolveExecutable(root,Descriptor);paths=new PathLocks(Path.GetDirectoryName(Executable));executable=OpenExecutable(root,Descriptor);}}catch{Dispose();throw;}}
   public void Dispose(){if(executable!=null){executable.Dispose();executable=null;}if(paths!=null){paths.Dispose();paths=null;}if(lease!=null){lease.Dispose();lease=null;}}
  }
  public static LaunchTarget OpenActive(string root,int timeoutMs=30000){return new LaunchTarget(Root(root),timeoutMs);}
 }
}
