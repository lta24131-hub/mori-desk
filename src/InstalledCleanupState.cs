using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace MoriDesk {
 // Evidence only: this class never repairs registration, closes a process,
 // changes a shortcut, or deletes an installation. Re-capture before cleanup.
 internal sealed class InstalledCleanupSnapshot {
  internal bool Complete,EntrypointsSwitched;
  internal string[] RunningExecutables=new string[0],ReferencedExecutables=new string[0],ProtectedPaths=new string[0];
  internal string Reason="";
 }
 internal sealed class InstalledCleanupReference {
  internal string Target;
  internal bool RequireCurrent;
  internal InstalledCleanupReference(string target,bool requireCurrent){Target=target;RequireCurrent=requireCurrent;}
 }
 internal sealed class InstalledCleanupProcess {
  internal string Name,Executable;
  internal bool Exited;
 }
 internal sealed class InstalledCleanupEvidence {
  internal string DesktopTarget,StartMenuTarget;
  internal string CommittedExecutable,LauncherExecutable;
  internal readonly List<InstalledCleanupReference> References=new List<InstalledCleanupReference>();
  internal readonly List<InstalledCleanupProcess> Processes=new List<InstalledCleanupProcess>();
 }
 internal static class InstalledCleanupState {
  const string Host="app.mori.desk";
  const int MaximumLinks=4096;
  [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,int id);
  [DllImport("kernel32.dll",SetLastError=true)]static extern bool CloseHandle(IntPtr handle);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint size,uint flags);

  internal static InstalledCleanupSnapshot Capture(string currentExecutable,string programRoot,IEnumerable<string> protectedPaths=null){
   return CaptureWith(currentExecutable,programRoot,()=>{
    PhysicalUserContext.RequireDefault();
    if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")))throw new InvalidOperationException("alternate-data-context");
    string expected=Path.Combine(Path.GetDirectoryName(PhysicalUserContext.DefaultRoot),"Programs","MoriDesk");
    if(!Same(programRoot,expected))throw new InvalidDataException("unexpected-program-root");
    using(OpenPhysical(currentExecutable,0,null)){
     var identity=FileVersionInfo.GetVersionInfo(currentExecutable);
     if(!string.Equals(identity.OriginalFilename,"Mori.exe",StringComparison.OrdinalIgnoreCase)||(!string.Equals(identity.ProductName,"Nook",StringComparison.OrdinalIgnoreCase)&&!string.Equals(identity.ProductName,"Mori Desk",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("unexpected-product-identity");
    }
   },()=>{
    var evidence=Collect();var active=InstalledApplicationState.Read(programRoot);
    if(active!=null){
     evidence.CommittedExecutable=InstalledApplicationState.ResolveExecutable(programRoot,active);
     evidence.LauncherExecutable=InstalledApplicationState.LauncherPath(programRoot);
     using(OpenPhysical(evidence.LauncherExecutable,0,null)){
      var launcher=FileVersionInfo.GetVersionInfo(evidence.LauncherExecutable);
      if(launcher.ProductName!="Nook"||!string.Equals(launcher.OriginalFilename,"Mori.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("unexpected-launcher-identity");
     }
    }
    return evidence;
   },protectedPaths);
  }
  // A failed first installation has no current executable to authenticate.
  // Its candidate may already be exclusively locked or partly removed by the
  // manifest-backed cleanup engine. Gather evidence without reopening it;
  // this method does not attest ownership or authorize any deletion itself.
  internal static InstalledCleanupSnapshot CaptureBeforeFirstCommit(string candidateExecutable,string programRoot,IEnumerable<string> protectedPaths=null){
   return CaptureBeforeFirstCommitWith(candidateExecutable,programRoot,()=>{
    PhysicalUserContext.RequireInstalledContext();
    string expected=Path.Combine(Path.GetDirectoryName(PhysicalUserContext.DefaultRoot),"Programs","MoriDesk");
    ValidateBeforeFirstCommitPaths(candidateExecutable,programRoot,expected);NoLinks(programRoot);
    string folder=Path.GetDirectoryName(candidateExecutable);if(Exists(folder))NoLinks(folder);
   },()=>Exists(Path.Combine(programRoot,InstalledApplicationState.FileName)),Collect,protectedPaths);
  }
  internal static void ValidateBeforeFirstCommitPaths(string candidateExecutable,string programRoot,string expectedRoot){
   // .NET Framework GetFullPath removes traversal segments and trailing dots
   // before returning. Reject ambiguous raw components first, otherwise an
   // invalid first-commit cleanup target can look valid after normalization.
   RequireUnambiguousFirstCommitPath(candidateExecutable);RequireUnambiguousFirstCommitPath(programRoot);RequireUnambiguousFirstCommitPath(expectedRoot);
   string candidate=InstalledApplicationState.Root(candidateExecutable),root=InstalledApplicationState.Root(programRoot),expected=InstalledApplicationState.Root(expectedRoot);
   string folder=Path.GetFileName(Path.GetDirectoryName(candidate));Version version;
   if(!Same(root,expected)||!IsApplication(candidate,root)||folder.Length==0||folder[0]<'0'||folder[0]>'9'||folder.Any(c=>!char.IsLetterOrDigit(c)&&c!='.'&&c!='-')||!Version.TryParse(folder.Split('-')[0],out version)||version.Build<0)throw new InvalidDataException("unexpected-uncommitted-candidate");
  }
  static void RequireUnambiguousFirstCommitPath(string path){
   if(string.IsNullOrWhiteSpace(path)||path.Length<4||!((path[0]>='A'&&path[0]<='Z')||(path[0]>='a'&&path[0]<='z'))||path[1]!=':'||(path[2]!='\\'&&path[2]!='/')||path.IndexOf(':',2)>=0||path.Any(char.IsControl)||path.IndexOf('"')>=0)throw new InvalidDataException("invalid-uncommitted-path");
   if(path.Substring(3).Split('\\','/').Any(part=>part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal)))throw new InvalidDataException("ambiguous-uncommitted-path");
  }
  // Synthetic evidence seam only. The production caller above has no bypass
  // switch and always verifies the physical installed context and absence of
  // the ledger. A record appearing during collection also vetoes this mode.
  internal static InstalledCleanupSnapshot CaptureBeforeFirstCommitWith(string candidateExecutable,string programRoot,Action requireContext,Func<bool> committedRecordExists,Func<InstalledCleanupEvidence> collect,IEnumerable<string> protectedPaths=null){
   return CaptureWith(candidateExecutable,programRoot,()=>{
    if(requireContext==null||committedRecordExists==null||collect==null)throw new ArgumentNullException();
    requireContext();if(committedRecordExists())throw new InvalidDataException("installation-already-committed");
   },()=>{
    var evidence=collect();if(evidence==null||!string.IsNullOrEmpty(evidence.CommittedExecutable)||!string.IsNullOrEmpty(evidence.LauncherExecutable)||committedRecordExists())throw new InvalidDataException("installation-committed-during-capture");return evidence;
   },protectedPaths);
  }
  // The seam supplies synthetic evidence, never a production bypass flag.
  internal static InstalledCleanupSnapshot CaptureWith(string currentExecutable,string programRoot,Action requireContext,Func<InstalledCleanupEvidence> collect,IEnumerable<string> protectedPaths=null){
   var result=new InstalledCleanupSnapshot();
   try{
    if(requireContext==null||collect==null)throw new ArgumentNullException();
    requireContext();
    string current=LocalPath(currentExecutable),root=LocalPath(programRoot);
    if(!IsApplication(current,root))throw new InvalidDataException("unexpected-current-executable");
    var evidence=collect();if(evidence==null)throw new InvalidDataException("missing-evidence");
    var running=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var referenced=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var protectedSet=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    string publicEntry=current;
    if(!string.IsNullOrEmpty(evidence.CommittedExecutable)||!string.IsNullOrEmpty(evidence.LauncherExecutable)){
     if(!Same(evidence.CommittedExecutable,current)||!Same(evidence.LauncherExecutable,Path.Combine(root,"Launcher","Mori.exe")))throw new InvalidDataException("active-installation-mismatch");
     publicEntry=LocalPath(evidence.LauncherExecutable);
    }
    bool switched=Same(evidence.DesktopTarget,publicEntry)&&Same(evidence.StartMenuTarget,publicEntry);
    foreach(string target in new[]{evidence.DesktopTarget,evidence.StartMenuTarget})if(!string.IsNullOrEmpty(target)&&Inside(target,root))referenced.Add(LocalPath(target));
    foreach(var reference in evidence.References){
     if(reference==null)throw new InvalidDataException("missing-reference");
     if(string.IsNullOrEmpty(reference.Target)){if(reference.RequireCurrent)switched=false;continue;}
     string target=LocalPath(reference.Target);bool managed=Inside(target,root);
     if(managed)referenced.Add(target);
     if(reference.RequireCurrent&&!Same(target,publicEntry))switched=false;
    }
    foreach(var process in evidence.Processes){
     if(process==null)throw new InvalidDataException("missing-process-evidence");
     if(process.Exited)continue;
     if(string.IsNullOrEmpty(process.Executable)){
      if(string.IsNullOrEmpty(process.Name)||RelevantProcessName(process.Name))throw new InvalidDataException("unverified-related-process");
      continue; // Protected, unrelated Windows processes need not be inspected.
     }
     string target=LocalPath(process.Executable);if(Inside(target,root))running.Add(target);
    }
    foreach(string path in protectedPaths??Enumerable.Empty<string>()){
     if(string.IsNullOrWhiteSpace(path))continue;
     string target=LocalPath(path);if(Inside(target,root)||Same(target,root)||Inside(root,target))protectedSet.Add(target);
    }
    result.RunningExecutables=running.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
    result.ReferencedExecutables=referenced.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
    result.ProtectedPaths=protectedSet.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
    result.Complete=true;result.EntrypointsSwitched=switched;
    result.Reason=switched?"ready":"entrypoints-not-current";
   }catch(Exception error){
    result.Complete=false;result.EntrypointsSwitched=false;
    // Do not persist raw registry values, manifests, command lines or paths.
    result.Reason="evidence-unavailable:"+error.GetType().Name;
   }
   return result;
  }
  internal static string LocalPath(string value){
   if(string.IsNullOrWhiteSpace(value)||value.Length<3||!char.IsLetter(value[0])||value[1]!=':'||(value[2]!='\\'&&value[2]!='/')||value.IndexOf(':',2)>=0||value.Any(char.IsControl)||value.IndexOf('"')>=0)throw new InvalidDataException("invalid-local-path");
   string normalized=value.Replace('/','\\');
   if(normalized.Substring(3).Split('\\').Any(x=>x=="."||x==".."))throw new InvalidDataException("relative-path-component");
   string full=Path.GetFullPath(normalized);return full.Length>3?full.TrimEnd('\\'):full;
  }
  static bool Same(string left,string right){return !string.IsNullOrEmpty(left)&&!string.IsNullOrEmpty(right)&&string.Equals(LocalPath(left),LocalPath(right),StringComparison.OrdinalIgnoreCase);}
  static bool Inside(string path,string root){return LocalPath(path).StartsWith(LocalPath(root).TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}
  static bool IsApplication(string path,string root){return string.Equals(Path.GetFileName(path),"Mori.exe",StringComparison.OrdinalIgnoreCase)&&Same(Path.GetDirectoryName(Path.GetDirectoryName(path)),root);}
  internal static bool RelevantProcessName(string name){
   string value=Path.GetFileNameWithoutExtension(name??"").ToLowerInvariant();
   return value.Contains("nook")||value.Contains("mori")||new[]{"ffmpeg","ffprobe","soffice","soffice.bin","magick","convert","mutool","pdftoppm","pdftocairo","tesseract","gswin64c","gswin32c","node","python","pythonw"}.Contains(value);
  }
  static bool Exists(string path){try{File.GetAttributes(path);return true;}catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}}
  static void NoLinks(string path){
   if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("linked-path");
   for(var at=new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)));at!=null;at=at.Parent)if((at.Attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("linked-parent");
  }
  static string ActualPath(FileStream file){var text=new StringBuilder(32768);uint size=GetFinalPathNameByHandle(file.SafeFileHandle,text,(uint)text.Capacity,0);if(size==0||size>=text.Capacity)throw new IOException("unverified-physical-file");return text.ToString();}
  static FileStream OpenPhysical(string path,long maximum,Func<FileStream,string> observe){
   path=LocalPath(path);NoLinks(path);var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
   try{if(!PhysicalUserContext.SamePhysicalFile(path,(observe??ActualPath)(file))||(maximum>0&&file.Length>maximum))throw new InvalidDataException("unexpected-physical-file");return file;}catch{file.Dispose();throw;}
  }
  internal static string ReadPhysicalText(string path,long maximum,Func<FileStream,string> observe=null){using(var file=OpenPhysical(path,maximum,observe))using(var reader=new StreamReader(file,Encoding.UTF8,true))return reader.ReadToEnd();}
  internal static string ReadShortcut(string path,Func<string,string> readTarget=null,Func<FileStream,string> observe=null){
   using(OpenPhysical(path,4*1024*1024,observe)){
    if(readTarget!=null)return readTarget(path);
    object shell=null,link=null;
    try{shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));link=((dynamic)shell).CreateShortcut(path);return Convert.ToString(((dynamic)link).TargetPath);}
    finally{if(link!=null&&Marshal.IsComObject(link))Marshal.FinalReleaseComObject(link);if(shell!=null&&Marshal.IsComObject(shell))Marshal.FinalReleaseComObject(shell);}
   }
  }
  internal static string CommandTarget(string command,string suffix){
   if(string.IsNullOrEmpty(command)||command[0]!='"')throw new InvalidDataException("unexpected-command");
   int end=command.IndexOf('"',1);if(end<=1||command.Substring(end+1)!=suffix)throw new InvalidDataException("unexpected-command");
   return LocalPath(command.Substring(1,end-1));
  }
  internal static string NativeTarget(string text){
   if(text==null||text.Length>65536)throw new InvalidDataException("invalid-native-manifest");
   var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(text);object name,type,path;
   if(data==null||!data.TryGetValue("name",out name)||!Equals(name,Host)||!data.TryGetValue("type",out type)||!Equals(type,"stdio")||!data.TryGetValue("path",out path)||!(path is string))throw new InvalidDataException("invalid-native-manifest");
   return LocalPath((string)path);
  }
  static string ReadString(RegistryKey key,string name){object value=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);if(!(value is string)||key.GetValueKind(name)!=RegistryValueKind.String)throw new InvalidDataException("unexpected-registry-value");return (string)value;}
  static void Registrations(InstalledCleanupEvidence result){
   foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})using(var root=RegistryKey.OpenBaseKey(hive,view)){
    using(var run=root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run",false))if(run!=null&&run.GetValueNames().Contains("MoriDesk",StringComparer.OrdinalIgnoreCase))result.References.Add(new InstalledCleanupReference(CommandTarget(ReadString(run,"MoriDesk")," --startup"),true));
    using(var app=root.OpenSubKey(@"Software\Classes\Applications\Mori.exe",false))if(app!=null)using(var command=app.OpenSubKey(@"shell\open\command",false)){
     if(command==null)throw new InvalidDataException("incomplete-open-with");result.References.Add(new InstalledCleanupReference(CommandTarget(ReadString(command,"")," \"%1\""),true));
    }
    foreach(string browser in new[]{@"Google\Chrome",@"Microsoft\Edge"})using(var host=root.OpenSubKey(@"Software\"+browser+@"\NativeMessagingHosts\"+Host,false))if(host!=null){
     string path=LocalPath(ReadString(host,""));result.References.Add(new InstalledCleanupReference(NativeTarget(ReadPhysicalText(path,65536)),true));
    }
   }
  }
  static void Links(InstalledCleanupEvidence result,string directory,bool recurse,bool requireCurrent,HashSet<string> seen,ref int count){
   if(string.IsNullOrEmpty(directory)||!Exists(directory))return;
   NoLinks(directory);
   foreach(string file in Directory.EnumerateFiles(directory,"*.lnk",SearchOption.TopDirectoryOnly)){
    string path=LocalPath(file);if(!seen.Add(path))continue;if(++count>MaximumLinks)throw new InvalidDataException("too-many-shortcuts");
    string target=ReadShortcut(path);if(string.IsNullOrWhiteSpace(target))continue;
    // Shell namespace/packaged-app links do not name a local executable.
    if(target.Length<3||!char.IsLetter(target[0])||target[1]!=':')continue;
    result.References.Add(new InstalledCleanupReference(target,requireCurrent&&IsManagedDefaultTarget(target)));
   }
   if(recurse)foreach(string child in Directory.EnumerateDirectories(directory))Links(result,child,true,requireCurrent,seen,ref count);
  }
  static bool IsManagedDefaultTarget(string target){return Inside(target,Path.Combine(Path.GetDirectoryName(PhysicalUserContext.DefaultRoot),"Programs","MoriDesk"));}
  static IEnumerable<InstalledCleanupProcess> Processes(){
   var result=new List<InstalledCleanupProcess>();
   foreach(var process in Process.GetProcesses())using(process){
    int id=process.Id;if(id==0||id==4)continue;
    var item=new InstalledCleanupProcess();try{item.Name=process.ProcessName;}catch(InvalidOperationException){item.Exited=true;}catch(Win32Exception){}
    IntPtr handle=OpenProcess(0x1000,false,id);
    if(handle!=IntPtr.Zero)try{var text=new StringBuilder(32768);uint length=(uint)text.Capacity;if(QueryFullProcessImageName(handle,0,text,ref length)){item.Executable=text.ToString();item.Exited=false;}}finally{CloseHandle(handle);}
    if(string.IsNullOrEmpty(item.Executable))try{item.Exited=process.HasExited;}catch(InvalidOperationException){item.Exited=true;}catch(Win32Exception){}
    // A related executable reached through a junction/alias must not evade
    // protection by appearing outside the managed root. Refuse that snapshot.
    if(!item.Exited&&!string.IsNullOrEmpty(item.Executable)&&(RelevantProcessName(item.Name)||IsManagedDefaultTarget(item.Executable)))using(OpenPhysical(item.Executable,0,null)){}
    result.Add(item);
   }
   return result;
  }
  static InstalledCleanupEvidence Collect(){
   var result=new InstalledCleanupEvidence();
   string desktop=Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),programs=Environment.GetFolderPath(Environment.SpecialFolder.Programs);
   string desktopLink=Path.Combine(desktop,"Nook.lnk"),startLink=Path.Combine(programs,"Nook","Nook.lnk");
   if(Exists(desktopLink))result.DesktopTarget=ReadShortcut(desktopLink);
   if(Exists(startLink))result.StartMenuTarget=ReadShortcut(startLink);
   int count=0;var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   Links(result,desktop,false,true,seen,ref count);
   Links(result,Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),false,true,seen,ref count);
   Links(result,programs,true,true,seen,ref count);
   Links(result,Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),true,true,seen,ref count);
   string pinned=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),@"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
   Links(result,pinned,false,false,seen,ref count);
   Registrations(result);result.Processes.AddRange(Processes());return result;
  }
 }
}
