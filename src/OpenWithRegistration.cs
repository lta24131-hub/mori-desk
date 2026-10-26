using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace MoriDesk {
 // Explorer remembers Applications\\Mori.exe separately from executable
 // branding. Keep this stable identity, but never choose a default application.
 public static class OpenWithRegistration {
  internal const string ApplicationKey="Software\\Classes\\Applications\\Mori.exe";
  internal static readonly string[] SupportedExtensions=FileFormats.PreviewExtensions();
  static string ManagedRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","MoriDesk");}}
  [DllImport("shell32.dll")] static extern void SHChangeNotify(uint eventId,uint flags,IntPtr item1,IntPtr item2);
  static void NotifyShell(){SHChangeNotify(0x08000000,0,IntPtr.Zero,IntPtr.Zero);}
  internal static bool TryRepairCurrentInstallation(){
   // A fixture, alternate-data window, worker or portable copy must not claim
   // Explorer's application registration. Call only from the installed UI path.
   if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")))return false;
   try{return RepairInstalled(Assembly.GetExecutingAssembly().Location);}
   catch(ArgumentException){return false;}catch(IOException){return false;}catch(UnauthorizedAccessException){return false;}catch(System.Security.SecurityException){return false;}catch(InvalidOperationException){return false;}catch(System.ComponentModel.Win32Exception){return false;}
  }
  public static bool RepairInstalled(string executable){
   PhysicalUserContext.RequireInstalledContext();
   // A candidate may render for health checking but has no authority to take
   // Windows registrations until its install transaction commits.
   if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOOK_UPDATE_REQUEST"))||!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOOK_UPDATE_EXPECTED_VERSION")))return false;
   return RepairInScope(Registry.CurrentUser,executable,ManagedRoot,NotifyShell);
  }
  // Installer-only operation: the caller validates the embedded launcher
  // payload before invoking this, under the installation transaction lease.
  public static bool RepairStableInstalled(string launcher){
   PhysicalUserContext.RequireInstalledContext();string root=InstalledApplicationState.ProgramRoot;
   if(!string.Equals(Path.GetFullPath(launcher),InstalledApplicationState.LauncherPath(root),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("固定打开入口地址无效。");
   return RepairInScopeCore(Registry.CurrentUser,launcher,root,NotifyShell,true);
  }
  internal static bool ManagedPath(string executable,string programRoot){
   if(string.IsNullOrWhiteSpace(executable)||string.IsNullOrWhiteSpace(programRoot))return false;
   try{
    string path=Path.GetFullPath(executable),root=Path.GetFullPath(programRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
    if(!string.Equals(Path.GetFileName(path),"Mori.exe",StringComparison.OrdinalIgnoreCase))return false;
    var version=Directory.GetParent(path);
    return version!=null&&version.Parent!=null&&string.Equals(version.Parent.FullName.TrimEnd('\\'),root,StringComparison.OrdinalIgnoreCase);
   }catch(ArgumentException){return false;}catch(NotSupportedException){return false;}catch(PathTooLongException){return false;}
  }
  static bool Identity(string executable,string programRoot,bool current,out Version version){
   version=null;if(!ManagedPath(executable,programRoot)||!File.Exists(executable))return false;
   // Do not follow junctions/symlinks out of the controlled install tree.
   var info=new FileInfo(executable);
   for(FileSystemInfo node=info;node!=null;node=node is FileInfo?((FileInfo)node).Directory:((DirectoryInfo)node).Parent){
    if((node.Attributes&FileAttributes.ReparsePoint)!=0)return false;
    if(string.Equals(node.FullName.TrimEnd('\\'),Path.GetFullPath(programRoot).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))break;
   }
   var metadata=FileVersionInfo.GetVersionInfo(executable);
   if(!string.Equals(metadata.OriginalFilename,"Mori.exe",StringComparison.OrdinalIgnoreCase))return false;
   if(!string.Equals(metadata.ProductName,"Nook",StringComparison.OrdinalIgnoreCase)&&(current||!string.Equals(metadata.ProductName,"Mori Desk",StringComparison.OrdinalIgnoreCase)))return false;
   version=new Version(Math.Max(0,metadata.FileMajorPart),Math.Max(0,metadata.FileMinorPart),Math.Max(0,metadata.FileBuildPart),Math.Max(0,metadata.FilePrivatePart));return true;
  }
  internal static string CommandTarget(string command){
   // Only migrate the exact historical/direct-open command, never a wrapper,
   // arbitrary arguments, malformed quote sequence or another application's key.
   if(string.IsNullOrEmpty(command)||command[0]!='"')return null;
   int end=command.IndexOf('"',1);
   if(end<=1||!string.Equals(command.Substring(end+1).Trim(),"\"%1\"",StringComparison.Ordinal))return null;
   return command.Substring(1,end-1);
  }
  internal static bool RetainExisting(string existing,Version existingVersion,string candidate,Version candidateVersion){
   int version=existingVersion.CompareTo(candidateVersion);if(version!=0)return version>0;
   // Install folders include their creation timestamp. The equal-version guard
   // also stops an older resident copy from undoing an in-version replacement.
   return string.Compare(Path.GetDirectoryName(existing),Path.GetDirectoryName(candidate),StringComparison.OrdinalIgnoreCase)>0;
  }
  static bool Put(RegistryKey root,string path,string name,string value){
   using(var key=root.CreateSubKey(path)){
    object before=key.GetValue(name,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
    if(before is string&&string.Equals((string)before,value,StringComparison.Ordinal)&&key.GetValueKind(name)==RegistryValueKind.String)return false;
    key.SetValue(name,value,RegistryValueKind.String);return true;
   }
  }
  // scopeRoot is the actual HKCU in production. Tests pass a disposable nested
  // HKCU key; every path below remains inside that scope, including sentinels.
  internal static bool RepairInScope(RegistryKey scopeRoot,string executable,string programRoot,Action notify){
   return RepairInScopeCore(scopeRoot,executable,programRoot,notify,false);
  }
  static bool RepairInScopeCore(RegistryKey scopeRoot,string executable,string programRoot,Action notify,bool stableInstaller){
   if(scopeRoot==null)throw new ArgumentNullException("scopeRoot");
   // Same lock order as the installer: install identity, then shell registry.
   using(InstalledApplicationState.AcquireLease(programRoot,1000))
   using(var mutex=new Mutex(false,MutexName(scopeRoot))){
    bool held=false;
    try{
     try{held=mutex.WaitOne(1000);}catch(AbandonedMutexException){held=true;}
     // Registration is an optional startup repair, not a reason to stall the UI.
     if(!held)return false;
     return RepairLocked(scopeRoot,executable,programRoot,notify,stableInstaller);
    }finally{if(held)mutex.ReleaseMutex();}
   }
  }
  internal static string MutexName(RegistryKey scopeRoot){
   string sid;using(var identity=WindowsIdentity.GetCurrent())sid=identity.User.Value;
   // Fixture scopes get a different mutex from the real HKCU registration.
   using(var hash=SHA256.Create())return "Local\\Nook.OpenWith."+BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(sid+"|"+scopeRoot.Name.ToUpperInvariant()))).Replace("-","");
  }
  static bool RepairLocked(RegistryKey scopeRoot,string executable,string programRoot,Action notify,bool stableInstaller){
   Version candidateVersion;
   if(!Identity(executable,programRoot,true,out candidateVersion))return false;
   string selected=Path.GetFullPath(executable);
   bool authoritative=stableInstaller;
   if(!stableInstaller){
    var active=InstalledApplicationState.Read(programRoot);
    if(active!=null){
     // Equal-version timestamps and a running old binary cannot overrule the
     // one committed payload identity. The registered route never changes.
     if(!string.Equals(InstalledApplicationState.ResolveExecutable(programRoot,active),selected,StringComparison.OrdinalIgnoreCase))return false;
     selected=InstalledApplicationState.LauncherPath(programRoot);Version launcherVersion;
     if(!Identity(selected,programRoot,true,out launcherVersion))return false;
     authoritative=true;
    }
   }
   using(var application=scopeRoot.OpenSubKey(ApplicationKey)){
    if(application!=null){
     var friendly=application.GetValue("FriendlyAppName") as string;
     if(!string.IsNullOrEmpty(friendly)&&friendly!="Nook"&&friendly!="Mori Desk")return false;
     using(var command=application.OpenSubKey("shell\\open\\command")){
      if(command!=null){
       string old=CommandTarget(command.GetValue("") as string);Version oldVersion;
       // Preserve unknown commands, unrelated products and external Mori copies.
       if(old==null||!Identity(old,programRoot,false,out oldVersion))return false;
       if(!authoritative&&RetainExisting(old,oldVersion,selected,candidateVersion))selected=Path.GetFullPath(old);
      }
     }
    }
   }
   bool changed=false;string icon="\""+selected+"\",0";
   changed|=Put(scopeRoot,ApplicationKey,"FriendlyAppName","Nook");
   changed|=Put(scopeRoot,ApplicationKey+"\\Application","ApplicationName","Nook");
   changed|=Put(scopeRoot,ApplicationKey+"\\Application","ApplicationDescription","素材、截图、翻译与文件预览");
   changed|=Put(scopeRoot,ApplicationKey+"\\Application","ApplicationIcon",icon);
   // A verified per-file handler supplies format icons without changing the
   // user's default application. Keep the application branding separately.
   changed|=Put(scopeRoot,ApplicationKey+"\\DefaultIcon","",ShellVisualRegistration.IsOwnedIconHandler(scopeRoot,programRoot)?"%1":icon);
   changed|=Put(scopeRoot,ApplicationKey+"\\shell\\open\\command","","\""+selected+"\" \"%1\"");
   foreach(string extension in SupportedExtensions)changed|=Put(scopeRoot,ApplicationKey+"\\SupportedTypes",extension,"");
   if(changed&&notify!=null)notify();return changed;
  }
 }
}
