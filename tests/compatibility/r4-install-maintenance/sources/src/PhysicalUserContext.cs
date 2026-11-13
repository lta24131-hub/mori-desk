using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MoriDesk {
 // A Desktop Bridge descendant can report NO_PACKAGE while its AppData writes
 // still land in the parent's private LocalCache. Directory.Exists and a
 // directory handle do not prove that Explorer or Chrome can see a file.
 // Verify a real file handle before opening settings, a catalog, or registry.
 internal static class PhysicalUserContext {
  internal const string FailureMessage="当前启动环境把 Nook 的配置重定向到了隔离目录，浏览器无法读取。已停止本次操作，未把它报告为安装成功。请从桌面上的 Nook 快捷方式或资源管理器重新打开程序。";
  static readonly object Gate=new object();
  static readonly HashSet<string> VerifiedRoots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  static bool defaultVerified;
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
  static extern int GetCurrentPackageFullName(ref uint length,StringBuilder name);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
  static extern uint GetFinalPathNameByHandle(SafeFileHandle file,StringBuilder path,uint length,uint flags);
  [DllImport("shell32.dll")]
  static extern int SHGetKnownFolderPath(ref Guid folder,uint flags,IntPtr token,out IntPtr path);
  internal static string DefaultRoot {get{return Path.Combine(PhysicalLocalAppData(),"MoriDesk");}}
  static string PhysicalLocalAppData(){
   Guid local=new Guid("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");IntPtr path=IntPtr.Zero;
   // KF_FLAG_NO_PACKAGE_REDIRECTION asks for the user's actual known folder;
   // this does not disable I/O virtualization, which the probe below detects.
   try{int error=SHGetKnownFolderPath(ref local,0x00010000,IntPtr.Zero,out path);if(error!=0)Marshal.ThrowExceptionForHR(error);string value=Marshal.PtrToStringUni(path);if(string.IsNullOrWhiteSpace(value))throw new IOException("无法确定用户配置目录。");return Path.GetFullPath(value);}
   finally{if(path!=IntPtr.Zero)Marshal.FreeCoTaskMem(path);}
  }
  internal static string DataRoot(){
   string configured=Environment.GetEnvironmentVariable("MORI_DESK_DATA");
   if(string.IsNullOrWhiteSpace(configured)){RequireDefault();return DefaultRoot;}
   string root=Full(configured);
   if(string.Equals(root,Full(DefaultRoot),StringComparison.OrdinalIgnoreCase))RequireDefault();
   else RequireRoot(root); // Explicit isolated data paths must also be real.
   return root;
  }
  // Installed updates must use exactly the physical user's established data.
  // An isolated/test override is not permission to install against that user:
  // reject it before probing, staging or clearing any environment variable.
  internal static void RequireInstalledContext(){
   if(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MORI_DESK_DATA")))throw new InvalidOperationException("当前进程使用独立数据目录，不能安装或确认正式版更新。请从桌面上的 Nook 快捷方式或资源管理器打开正式版后重试；原图库和路径设置未改动。");
   RequireDefault();
  }
  internal static void RequireDefault(){
   lock(Gate){if(defaultVerified)return;uint length=0;int package=GetCurrentPackageFullName(ref length,null);if(package!=15700)throw new InvalidOperationException(FailureMessage);
    RequireRoot(DefaultRoot);defaultVerified=true;
   }
  }
  static string Full(string path){
   if(string.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path)||path.StartsWith("\\\\",StringComparison.Ordinal))throw new IOException("Nook 配置需要本机绝对目录。");
   return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
  }
  internal static bool SamePhysicalFile(string expected,string observed){
   if(string.IsNullOrEmpty(observed))return false;
   if(observed.StartsWith(@"\\?\UNC\",StringComparison.OrdinalIgnoreCase))return false;
   if(observed.StartsWith(@"\\?\",StringComparison.Ordinal))observed=observed.Substring(4);
   try{return string.Equals(Full(expected),Full(observed),StringComparison.OrdinalIgnoreCase);}catch{return false;}
  }
  static void NoLinks(string path){
   for(var at=new DirectoryInfo(path);at!=null;at=at.Parent)if(at.Exists&&(at.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Nook 配置目录包含重定向链接，无法确认浏览器读取的是同一份配置。");
  }
  internal static void RequireRoot(string path){
   string root=Full(path);
   lock(Gate){if(VerifiedRoots.Contains(root))return;
    Probe(root,ReadFinalPath);VerifiedRoots.Add(root);
   }
  }
  // The test seam substitutes only the OS observation. It cannot mark a root
  // trusted or bypass production calls; failures are never cached as success.
  internal static void Probe(string root,Func<FileStream,string> observe){
   root=Full(root);NoLinks(root);Directory.CreateDirectory(root);NoLinks(root);
   string marker=Path.Combine(root,".nook-physical-"+Guid.NewGuid().ToString("N")+".tmp");
   using(var file=new FileStream(marker,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.Read|FileShare.Delete,1,FileOptions.DeleteOnClose)){
    if(!SamePhysicalFile(marker,observe(file)))throw new InvalidOperationException(FailureMessage);
   }
  }
  static string ReadFinalPath(FileStream file){
   var result=new StringBuilder(32768);uint length=GetFinalPathNameByHandle(file.SafeFileHandle,result,(uint)result.Capacity,0);
   if(length==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"无法验证 Nook 配置的实际保存位置。");
   if(length>=result.Capacity)throw new IOException("Nook 配置路径超过可验证范围。");return result.ToString();
  }
 }
}
