using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;

namespace MoriDesk {
 internal static class InstalledTaskbarIdentityTests {
  internal static void Run(string fixtures,Action<bool,string> check){
   string root=Path.Combine(fixtures,"taskbar installation"),active=Path.Combine(root,"0.1.33-active","Mori.exe"),old=Path.Combine(root,"0.1.32-old","Mori.exe"),launcher=InstalledApplicationState.LauncherPath(root);
   foreach(string file in new[]{active,old,launcher}){Directory.CreateDirectory(Path.GetDirectoryName(file));File.Copy(typeof(Entry).Assembly.Location,file);}
   InstalledApplicationState.Commit(root,null,InstalledApplicationState.CreateDescriptor(root,active,new string('a',64)));
   int contexts=0;Action require=()=>contexts++;
   check(InstalledTaskbarIdentity.ResolveInScope(root,active,false,true,null,false,require)==launcher&&contexts==1,"active installed main window resolves only the stable launcher, including paths containing spaces");
   check(InstalledTaskbarIdentity.ResolveInScope(root,old,false,true,null,false,require)==null,"stale managed payload cannot assign itself the active taskbar identity");
   int baseline=contexts;
   check(InstalledTaskbarIdentity.ResolveInScope(root,active,true,true,null,false,require)==null&&InstalledTaskbarIdentity.ResolveInScope(root,active,false,false,null,false,require)==null&&InstalledTaskbarIdentity.ResolveInScope(root,active,false,true,"fixture",false,require)==null&&InstalledTaskbarIdentity.ResolveInScope(root,typeof(Entry).Assembly.Location,false,true,null,false,require)==null&&contexts==baseline,"preview, non-shell, isolated data and portable builds never probe or claim the production identity");
   check(InstalledTaskbarIdentity.ResolveInScope(root,old,false,true,null,true,require)==launcher,"validated pending candidate binds to the stable launcher before the active record changes, never to its candidate path");
   string saved=launcher+".fixture";File.Move(launcher,saved);
   try{check(InstalledTaskbarIdentity.ResolveInScope(root,active,false,true,null,false,require)==null,"missing launcher cannot create a pin pointing to a replaceable payload");}finally{File.Move(saved,launcher);}
   foreach(string invalid in new[]{"Mori.exe",old,"https://example.com/Mori.exe",launcher+"\" --library other",launcher+":stream"}){bool rejected=false;try{ShellTaskbarProperties.LauncherCommand(invalid);}catch(ArgumentException){rejected=true;}check(rejected,"taskbar relaunch rejects commands and non-stable targets: "+invalid);}
   WindowProperties(launcher,check);
  }
  static void WindowProperties(string launcher,Action<bool,string> check){
   // EnsureHandle creates only an invisible fixture HWND. It never shows or
   // pins a test window, launches an executable or writes a real shortcut.
   var window=new Window{ShowInTaskbar=false,Title="Taskbar identity fixture"};int reads=0;bool committed=false;
   var identity=new InstalledTaskbarIdentity(window,()=>false,()=>{reads++;return committed?launcher:null;});
   try{
    IntPtr handle=new WindowInteropHelper(window).EnsureHandle();
    check(!window.IsVisible&&reads==1&&ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.ApplicationId)==null,"temporarily unavailable launch identity leaves an invisible fixture unpublished");
    committed=true;identity.Refresh();
    check(ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.ApplicationId)==ShellTaskbarProperties.AppId&&ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.RelaunchCommand)=="\""+launcher+"\"","service refresh publishes the explicit window identity and stable relaunch command after transient unavailability");
    check(ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.RelaunchName)=="Nook"&&ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.RelaunchIcon)=="\""+launcher+"\",0","real window property store retains the Nook label and stable icon");
    identity.Refresh();check(reads==2,"repeated service initialization does not rehash the installed payload or rewrite taskbar properties");
    System.ComponentModel.CancelEventHandler cancel=(sender,args)=>args.Cancel=true;window.Closing+=cancel;window.Close();window.Closing-=cancel;
    check(ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.ApplicationId)==ShellTaskbarProperties.AppId,"canceling a close preserves the running taskbar identity");
    ShellTaskbarProperties.ClearWindow(handle);
    check(ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.ApplicationId)==null&&ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.RelaunchCommand)==null&&ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.RelaunchName)==null&&ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.RelaunchIcon)==null,"window cleanup writes native VT_EMPTY for all four owned properties");
    ShellTaskbarProperties.SetWindow(handle,launcher);
   }finally{window.Close();}
   var preview=new Window{ShowInTaskbar=false};bool previewMode=false;int previewReads=0;
   new InstalledTaskbarIdentity(preview,()=>previewMode,()=>{previewReads++;return launcher;});previewMode=true;
   try{IntPtr handle=new WindowInteropHelper(preview).EnsureHandle();check(previewReads==0&&ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.ApplicationId)==null,"PreviewMode assigned after construction is checked at HWND creation before any production lookup");}finally{preview.Close();}
   var failed=new Window{ShowInTaskbar=false};int failures=0;var retry=new InstalledTaskbarIdentity(failed,()=>false,()=>{if(failures++==0)throw new IOException("fixture state unavailable");return launcher;});
   try{IntPtr handle=new WindowInteropHelper(failed).EnsureHandle();check(ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.ApplicationId)==null,"identity read failure leaves the window alive without claiming installation");retry.Refresh();check(ShellTaskbarProperties.ReadWindowProperty(handle,ShellTaskbarProperties.ApplicationId)==ShellTaskbarProperties.AppId,"a later service handoff can recover a failed identity lookup");}finally{failed.Close();}
  }
 }
}
