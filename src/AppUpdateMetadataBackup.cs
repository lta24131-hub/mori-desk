using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MoriDesk {
 // Shared by the in-app updater and manual installer. This is a bounded,
 // metadata-only snapshot, not an automatic schema rollback or media backup.
 internal static class AppUpdateMetadataBackup {
  internal const long MaximumBytes=128L*1024*1024,MaximumFileBytes=64L*1024*1024;
  static readonly string[] LibraryKeys=("LibraryPath FileConversionOutputPath Theme ThumbnailSize LeftSidebarWidth RightSidebarWidth WaterfallLayout ShowAssetNames ShowAssetDetails LeftSidebarVisible RightSidebarVisible StartupSection DefaultSort ThemeAccent CloseToTray ConfirmTrash BookmarkOpenInApp AnimateImages PreviewBackdrop MediaVolume MediaMuted MediaAutoPlay MediaLoop CanvasDefaultTopmost CanvasSaveDelay CanvasCacheMB CanvasGrid CanvasShowTools CanvasSnap CanvasZoomSpeed CanvasDash CanvasSize").Split(' ');
  static readonly string[] CaptureKeys=("Stroke Color OcrLanguage TranslationProvider SaveFolder").Split(' ');
  internal static string SanitizeSettings(string text,bool capture){
   var json=new JavaScriptSerializer{MaxJsonLength=4*1024*1024,RecursionLimit=16};var source=json.Deserialize<Dictionary<string,object>>(text);if(source==null)throw new InvalidDataException("设置备份内容为空。");
   var output=new Dictionary<string,object>();
   foreach(string key in capture?CaptureKeys:LibraryKeys){object value;if(source.TryGetValue(key,out value)&&(value==null||value is string||value is bool||value is int||value is long||value is decimal||value is double))output[key]=value;}
   if(capture){object shortcuts;if(source.TryGetValue("Shortcuts",out shortcuts)){
    var set=shortcuts as Dictionary<string,object>;var clean=new Dictionary<string,object>();
    if(set!=null)foreach(string name in new[]{"Capture","Pin","Translate","TogglePins"}){object shortcut;if(!set.TryGetValue(name,out shortcut))continue;var fields=shortcut as Dictionary<string,object>;object key,mod;if(fields!=null&&fields.TryGetValue("Key",out key)&&key is int&&fields.TryGetValue("Modifiers",out mod)&&mod is int)clean[name]=new{Key=(int)key,Modifiers=(int)mod};}
    output["Shortcuts"]=clean;
   }}
   return json.Serialize(output);
  }
  // Local absolute paths only. Inspect every existing ancestor and reject
  // junctions/symlinks before any source read or destination creation.
  internal static string PlainPath(string path){
   if(string.IsNullOrWhiteSpace(path)||path.Length<3||!char.IsLetter(path[0])||path[1]!=':'||(path[2]!='\\'&&path[2]!='/')||path.IndexOf(':',2)>=0||path.IndexOfAny(new[]{'\0','\r','\n','"'})>=0)throw new InvalidDataException("更新备份必须使用本机绝对路径。");
   foreach(string part in path.Substring(3).Split('\\','/'))if(part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal))throw new InvalidDataException("更新备份路径不明确。");
   string full=Path.GetFullPath(path);
   for(string part=full;!string.IsNullOrEmpty(part);part=Path.GetDirectoryName(part)){
    FileAttributes attributes;try{attributes=File.GetAttributes(part);}catch(FileNotFoundException){continue;}catch(DirectoryNotFoundException){continue;}
    if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("更新备份路径不能包含链接或重定向目录。");
    if(Path.GetPathRoot(part)==part)break;
   }
   return full;
  }
  internal static void Create(string output,string dataRoot,string libraryRoot,Action<string> databaseSnapshot){
   if(databaseSnapshot==null)throw new ArgumentNullException("databaseSnapshot");
   output=PlainPath(output);dataRoot=PlainPath(dataRoot);libraryRoot=PlainPath(libraryRoot);
   if(Path.GetFileName(output)!="metadata-backup"||Directory.Exists(output)||File.Exists(output))throw new IOException("更新备份目录已使用或无效。");
   string database=Path.Combine(libraryRoot,".mori","library.db");CheckFile(database,MaximumFileBytes);Directory.CreateDirectory(output);long total=0;
   string dbTarget=Path.Combine(output,"library.db");databaseSnapshot(dbTarget);CheckFile(dbTarget,MaximumFileBytes);total+=new FileInfo(dbTarget).Length;
   CopySettings(Path.Combine(dataRoot,"settings.json"),Path.Combine(output,"settings.json"),false,ref total);
   CopySettings(Path.Combine(dataRoot,"capture","settings.json"),Path.Combine(output,"capture-settings.json"),true,ref total);
   CopyMetadata(Path.Combine(dataRoot,"capture","pins","pins.json"),Path.Combine(output,"pins.json"),ref total);
   string canvases=Path.Combine(dataRoot,"free-canvas");
   if(ExistsAs(canvases,true)){
    PlainPath(canvases);int count=0;
    foreach(string folder in Directory.EnumerateDirectories(canvases)){
     Guid id;if(!Guid.TryParseExact(Path.GetFileName(folder),"N",out id))continue;if(++count>10000)throw new IOException("画布索引过多，更新已停止；请先手动备份。");PlainPath(folder);
     foreach(string name in new[]{"project.json","mature-canvas.json"}){string source=Path.Combine(folder,name);if(!ExistsAs(source,false))continue;string target=Path.Combine(output,"canvas-"+id.ToString("N")+"-"+name);CopyMetadata(source,target,ref total);}
    }
   }
   // This marker is written last. Failed/partial snapshots never advertise
   // completion; neither caller may replace the app after a backup failure.
   string marker=new JavaScriptSerializer().Serialize(new{schemaVersion=1,completedUtc=DateTime.UtcNow.ToString("O"),metadataOnly=true,sourceMediaIncluded=false,credentialsIncluded=false,bytes=total});
   if(Encoding.UTF8.GetByteCount(marker)>MaximumBytes-total)throw new IOException("元数据备份已达到总容量上限，未生成完成标记；请先手动备份。");
   Write(Path.Combine(output,"complete.json"),marker);
  }
  static void CheckFile(string file,long maximum){PlainPath(file);if(!File.Exists(file)||new FileInfo(file).Length>maximum)throw new IOException("更新前元数据备份缺失或超过安全上限；本次更新已停止。");}
  // Exists() hides access-denied and I/O failures as "missing". Only a proven
  // absent optional record may be skipped; unreadable metadata vetoes update.
  static bool ExistsAs(string path,bool directory){
   PlainPath(path);FileAttributes attributes;try{attributes=File.GetAttributes(path);}catch(FileNotFoundException){return false;}catch(DirectoryNotFoundException){return false;}
   if(((attributes&FileAttributes.Directory)!=0)!=directory)throw new IOException("更新前元数据类型异常，未将不完整备份报告为成功。");return true;
  }
  static void AddBytes(long count,ref long total){if(count<0||count>MaximumFileBytes||total>MaximumBytes-count)throw new IOException("更新前元数据备份超过安全上限；请先手动备份。");total+=count;}
  static void CopySettings(string source,string target,bool capture,ref long total){if(!ExistsAs(source,false))return;CheckFile(source,4*1024*1024);string text=SanitizeSettings(File.ReadAllText(source,Encoding.UTF8),capture);AddBytes(Encoding.UTF8.GetByteCount(text),ref total);Write(target,text);}
  static void CopyMetadata(string source,string target,ref long total){if(!ExistsAs(source,false))return;CheckFile(source,MaximumFileBytes);using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read)){AddBytes(input.Length,ref total);using(var destination=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(destination);destination.Flush(true);}}}
  static void Write(string file,string text){byte[] bytes=new UTF8Encoding(false).GetBytes(text);using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}}
 }
}
