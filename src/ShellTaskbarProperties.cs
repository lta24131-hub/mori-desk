using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace MoriDesk {
 // Shared by the window and installer so a shortcut and its running window
 // never acquire different identities. The bootstrap has no taskbar window.
 internal static class ShellTaskbarProperties {
  internal const string AppId="Nook.Desktop";
  internal const uint RelaunchCommand=2,RelaunchIcon=3,RelaunchName=4,ApplicationId=5;
  static readonly Guid Format=new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
  [StructLayout(LayoutKind.Sequential)]struct PropertyKey {internal Guid Format;internal uint Id;internal PropertyKey(uint id){Format=ShellTaskbarProperties.Format;Id=id;}}
  // Both release executables are x64; native PROPVARIANT occupies 24 bytes.
  [StructLayout(LayoutKind.Explicit,Size=24)]struct Variant {
   [FieldOffset(0)]internal ushort Type;
   [FieldOffset(8)]internal IntPtr Text;
  }
  [ComImport,Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IPropertyStore {
   [PreserveSig]int GetCount(out uint count);
   [PreserveSig]int GetAt(uint index,out PropertyKey key);
   [PreserveSig]int GetValue(ref PropertyKey key,out Variant value);
   [PreserveSig]int SetValue(ref PropertyKey key,ref Variant value);
   [PreserveSig]int Commit();
  }
  [ComImport,Guid("00021401-0000-0000-C000-000000000046")]class ShellLink {}
  [DllImport("shell32.dll")]static extern int SHGetPropertyStoreForWindow(IntPtr window,ref Guid iid,[MarshalAs(UnmanagedType.Interface)]out IPropertyStore store);
  [DllImport("ole32.dll")]static extern int PropVariantClear(ref Variant value);
  static void Require(int hr){if(hr<0)Marshal.ThrowExceptionForHR(hr);}
  static IPropertyStore WindowStore(IntPtr window){if(window==IntPtr.Zero)throw new ArgumentException("Window handle is required");IPropertyStore store;Guid iid=typeof(IPropertyStore).GUID;Require(SHGetPropertyStoreForWindow(window,ref iid,out store));return store;}
  static void Set(IPropertyStore store,uint id,string text){
   var key=new PropertyKey(id);var value=new Variant();
   try{if(text!=null){value.Type=31;value.Text=Marshal.StringToCoTaskMemUni(text);}Require(store.SetValue(ref key,ref value));}
   finally{PropVariantClear(ref value);}
  }
  static string Read(IPropertyStore store,uint id){
   var key=new PropertyKey(id);Variant value;Require(store.GetValue(ref key,out value));
   try{if(value.Type==0)return null;if(value.Type!=31)throw new InvalidDataException("Unexpected taskbar property type");return Marshal.PtrToStringUni(value.Text);}
   finally{PropVariantClear(ref value);}
  }
  internal static string LauncherCommand(string launcher){
   if(string.IsNullOrWhiteSpace(launcher)||launcher.Length<4||!char.IsLetter(launcher[0])||launcher[1]!=':'||(launcher[2]!='\\'&&launcher[2]!='/')||launcher.IndexOfAny(new[]{'"','\r','\n','\0'})>=0||launcher.IndexOf(':',2)>=0)throw new ArgumentException("A local launcher path is required");
   launcher=Path.GetFullPath(launcher);
   if(!string.Equals(Path.GetFileName(launcher),"Mori.exe",StringComparison.OrdinalIgnoreCase)||!string.Equals(Path.GetFileName(Path.GetDirectoryName(launcher)),"Launcher",StringComparison.OrdinalIgnoreCase))throw new ArgumentException("A stable launcher path is required");
   return "\""+launcher+"\"";
  }
  internal static void SetWindow(IntPtr window,string launcher){
   string command=LauncherCommand(launcher);var store=WindowStore(window);
   try{
    // The name and command must be written together, before AppID makes them
    // visible to the shell. A literal brand name has no localized resource.
    Set(store,RelaunchCommand,command);Set(store,RelaunchName,"Nook");Set(store,RelaunchIcon,command+",0");Set(store,ApplicationId,AppId);
   }catch{Clear(store);throw;}finally{Marshal.FinalReleaseComObject(store);}
  }
  static void Clear(IPropertyStore store){foreach(uint id in new[]{ApplicationId,RelaunchCommand,RelaunchName,RelaunchIcon})try{Set(store,id,null);}catch{}}
  // Window stores apply SetValue immediately. Microsoft requires VT_EMPTY
  // before HWND destruction; Commit has no effect for a window store.
  // https://learn.microsoft.com/windows/win32/api/shellapi/nf-shellapi-shgetpropertystoreforwindow
  internal static void ClearWindow(IntPtr window){var store=WindowStore(window);try{Clear(store);}finally{Marshal.FinalReleaseComObject(store);}}
  internal static string ReadWindowProperty(IntPtr window,uint id){var store=WindowStore(window);try{return Read(store,id);}finally{Marshal.FinalReleaseComObject(store);}}
  internal static void SetShortcutAppId(string path){
   if(!string.Equals(Path.GetExtension(path),".lnk",StringComparison.OrdinalIgnoreCase)||!File.Exists(path))throw new ArgumentException("An existing shortcut is required");
   object link=new ShellLink();try{var file=(IPersistFile)link;file.Load(path,2);var store=(IPropertyStore)link;Set(store,ApplicationId,AppId);Require(store.Commit());file.Save(path,true);}
   finally{Marshal.FinalReleaseComObject(link);}
   if(ReadShortcutAppId(path)!=AppId)throw new IOException("快捷方式身份未能保存，原入口未替换。");
  }
  internal static string ReadShortcutAppId(string path){object link=new ShellLink();try{((IPersistFile)link).Load(path,0);return Read((IPropertyStore)link,ApplicationId);}finally{Marshal.FinalReleaseComObject(link);}}
 }
}
