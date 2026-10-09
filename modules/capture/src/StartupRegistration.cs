using System;
using System.IO;
using System.Runtime.InteropServices;

namespace QingJie {
    public sealed class StartupRegistration {
        readonly string folder,executable;
        public StartupRegistration(string folder,string executable){this.folder=folder;this.executable=Path.GetFullPath(executable);}
        // Reuse legacy registration so Windows' disabled-startup preference survives.
        public string ShortcutPath {get{var current=Path.Combine(folder,"Luma.lnk");var legacy=Path.Combine(folder,"轻截.lnk");return !File.Exists(current)&&File.Exists(legacy)?legacy:current;}}
        public static StartupRegistration Current {get{return new StartupRegistration(Environment.GetFolderPath(Environment.SpecialFolder.Startup),typeof(Program).Assembly.Location);}}
        bool Owns(string target){if(string.IsNullOrWhiteSpace(target))return false;try{var full=Path.GetFullPath(target);return string.Equals(full,executable,StringComparison.OrdinalIgnoreCase)||string.Equals(full,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","QingJie","QingJie.exe"),StringComparison.OrdinalIgnoreCase);}catch(ArgumentException){return false;}catch(NotSupportedException){return false;}}
        public bool Enabled {get{return File.Exists(ShortcutPath)&&Owns(ReadTarget());}}
        string ReadTarget(){object shell=null,shortcut=null;try{shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));shortcut=((dynamic)shell).CreateShortcut(ShortcutPath);return (string)((dynamic)shortcut).TargetPath;}finally{Release(shortcut);Release(shell);}}
        static void Release(object value){if(value!=null&&Marshal.IsComObject(value))Marshal.FinalReleaseComObject(value);}
        public void SetEnabled(bool enabled){
            if(File.Exists(ShortcutPath)&&!Owns(ReadTarget()))throw new InvalidOperationException("启动文件夹里有同名但指向其他程序的快捷方式，未修改它。请先检查该快捷方式。");
            if(!enabled){if(File.Exists(ShortcutPath))File.Delete(ShortcutPath);return;}
            Directory.CreateDirectory(folder);object shell=null,shortcut=null;
            try{shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));shortcut=((dynamic)shell).CreateShortcut(ShortcutPath);dynamic link=shortcut;link.TargetPath=executable;link.Arguments="--background";link.WorkingDirectory=Path.GetDirectoryName(executable);link.Description="登录后启动 Luma 并恢复贴图";link.IconLocation=executable+",0";link.Save();}
            finally{Release(shortcut);Release(shell);}
        }
    }
}
