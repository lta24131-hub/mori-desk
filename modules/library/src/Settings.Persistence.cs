using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Mori {
 public partial class Settings {
  string loadedPath;
  Dictionary<string,object> loadedValues;
  Dictionary<string,object> Values(){return new Dictionary<string,object>{{"LibraryPath",LibraryPath},{"Theme",Theme},{"ThumbnailSize",ThumbnailSize},{"LeftSidebarWidth",LeftSidebarWidth},{"RightSidebarWidth",RightSidebarWidth},{"WaterfallLayout",WaterfallLayout},{"ShowAssetNames",ShowAssetNames},{"ShowAssetDetails",ShowAssetDetails},{"LeftSidebarVisible",LeftSidebarVisible},{"RightSidebarVisible",RightSidebarVisible}};}
  void RememberLoaded(string path){loadedPath=path;loadedValues=Values();}
  Dictionary<string,object> ValuesToSave(string path){
   var values=Values();
   // An editor may outlive a library switch or another preferences editor.
   // Only its changed fields belong to this write; preserve newer disk values.
   if(loadedValues!=null&&string.Equals(loadedPath,path,StringComparison.OrdinalIgnoreCase)&&File.Exists(path)){
    var current=LoadUnlocked(path).Values();
    foreach(var pair in values)if(!object.Equals(pair.Value,loadedValues[pair.Key]))current[pair.Key]=pair.Value;
    return current;
   }
   return values;
  }
  void ApplySaved(Dictionary<string,object> values){
   LibraryPath=(string)values["LibraryPath"];Theme=(string)values["Theme"];ThumbnailSize=(int)values["ThumbnailSize"];
   LeftSidebarWidth=(int)values["LeftSidebarWidth"];RightSidebarWidth=(int)values["RightSidebarWidth"];WaterfallLayout=(bool)values["WaterfallLayout"];
   ShowAssetNames=(bool)values["ShowAssetNames"];ShowAssetDetails=(bool)values["ShowAssetDetails"];LeftSidebarVisible=(bool)values["LeftSidebarVisible"];RightSidebarVisible=(bool)values["RightSidebarVisible"];
  }
  internal static void RememberLibrary(string root){RememberLibraryAt(Path.Combine(ConfigDir,"settings.json"),root);}
  internal static void RememberLibraryAt(string path,string root){
   path=Path.GetFullPath(path);root=Path.GetFullPath(root);
   // Explicit selection is atomic with the latest settings read. SaveTo's
   // named mutex is recursive on this thread, so it uses the same transaction.
   using(SettingsFileLock.Acquire(path)){
    var settings=LoadUnlocked(path);
    // Native collection holds this lease through its final settings recheck
    // and write. Never change the route while that old-root write is active.
    // This acquisition must stay nonblocking: native collection may need the
    // settings lock while already holding the old-root lease.
    using(var lease=LibraryTransfer.TryAcquireWrite(settings.LibraryPath)){
     if(lease==null)throw new IOException("原图库正在接收收藏或迁移，暂未切换保存位置。请等待完成后重试。");
     settings.LibraryPath=root;settings.SaveTo(path);
    }
   }
  }
 }
 internal sealed class SettingsFileLock : IDisposable {
  readonly Mutex mutex;
  SettingsFileLock(Mutex value){mutex=value;}
  internal static SettingsFileLock Acquire(string path){
   string key;using(var hash=SHA256.Create())key=BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()))).Replace("-","");
   var mutex=new Mutex(false,"Local\\MoriDesk-Settings-"+key);
   try{
    bool held;try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){held=true;}
    if(!held)throw new IOException("Mori 设置正在由另一个窗口保存，请稍后重试。");
    return new SettingsFileLock(mutex);
   }catch{mutex.Dispose();throw;}
  }
  public void Dispose(){try{mutex.ReleaseMutex();}finally{mutex.Dispose();}}
 }
}
