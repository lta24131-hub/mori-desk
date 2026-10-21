using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mori;

namespace MoriDesk {
 // A conversion belongs to the library and source metadata captured when it was
 // opened, not to whichever category happens to be visible when it finishes.
 internal sealed class FileConversionCollectionSession {
  internal sealed class SourceInfo {
   internal string Name,Extension,Source,Tags,Notes,Detail,Thumbnail,Created;
   internal bool Favorite,HiddenFromAll;
   internal string[] Folders;
  }
  readonly Library library;
  readonly bool preserveSourceCopy;
  readonly object collectionGate=new object();
  readonly Dictionary<string,SourceInfo> sources=new Dictionary<string,SourceInfo>(StringComparer.OrdinalIgnoreCase);
  readonly Dictionary<string,string> collected=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  internal string LibraryRoot {get{return library.Root;}}
  internal FileConversionCollectionSession(Library originatingLibrary,IEnumerable<Asset> sourceAssets,bool preserveSourceCopy=false){
   if(originatingLibrary==null)throw new ArgumentNullException("originatingLibrary");
   library=originatingLibrary;this.preserveSourceCopy=preserveSourceCopy;
   var mediaFolders=new HashSet<string>(library.Folders(false).Select(folder=>folder.Id),StringComparer.Ordinal);
   var memberships=library.Db.Query("SELECT asset,folder FROM memberships").Where(row=>mediaFolders.Contains(row["folder"])).ToLookup(row=>row["asset"],row=>row["folder"]);
   foreach(var asset in sourceAssets??Enumerable.Empty<Asset>()){
    if(asset==null||asset.IsBookmark||asset.IsDirectory||asset.Deleted)continue;
    try{
     string path=Path.GetFullPath(library.FullPath(asset));if(sources.ContainsKey(path))continue;
     sources.Add(path,new SourceInfo{Name=asset.Name,Extension=asset.Ext,Source=asset.Source,Tags=asset.Tags,Notes=asset.Notes,
      Detail=asset.TypeLabel+" · "+asset.Detail,Thumbnail=library.ThumbnailPath(asset),Created=asset.Created,
      Favorite=asset.Favorite,HiddenFromAll=asset.HiddenFromAll,
      Folders=asset.IsReference?library.AssetFolders(asset.Id).Where(folder=>mediaFolders.Contains(folder.Id)).Select(folder=>folder.Id).Distinct().ToArray():memberships[asset.Id].Distinct().ToArray()});
    }catch(IOException){}catch(ArgumentException){}catch(UnauthorizedAccessException){}
   }
  }
  internal SourceInfo FindSource(string path){
   if(string.IsNullOrWhiteSpace(path))return null;SourceInfo found;
   try{return sources.TryGetValue(Path.GetFullPath(path),out found)?found:null;}catch(ArgumentException){return null;}catch(NotSupportedException){return null;}
  }
  internal string CollectedAssetId(string output){lock(collected){string id;return collected.TryGetValue(output,out id)?id:null;}}
  internal string[] CollectedAssetIds(){
   // A caller may retain this task's receipt while another result is collected.
   // Never expose the mutable map or include source records in that snapshot.
   lock(collected)return collected.Values.Where(id=>!string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
  }
  internal Task<ConversionCollectionResult> CollectAsync(IList<ConvertedFile> results){
   return Task.Run(()=>Collect(results));
  }
  static void VerifyExistingCopy(string published,string existing){
   // The database hash may describe bytes that an external editor has since
   // replaced. Reusing that row is success only if its actual file still matches.
   // Read locks prevent either file changing during the comparison.
   using(var incoming=new FileStream(published,FileMode.Open,FileAccess.Read,FileShare.Read))
   using(var current=new FileStream(existing,FileMode.Open,FileAccess.Read,FileShare.Read)){
    if(incoming.Length!=current.Length)throw new IOException("图库中已有素材的原文件已变化，未覆盖；转换结果仍保留");
    var left=new byte[65536];var right=new byte[65536];int read;
    while((read=incoming.Read(left,0,left.Length))>0){
     int copied=0;while(copied<read){int count=current.Read(right,copied,read-copied);if(count==0)throw new IOException("已有素材不可完整读取，转换结果仍保留");copied+=count;}
     for(int i=0;i<read;i++)if(left[i]!=right[i])throw new IOException("图库中已有素材的原文件已变化，未覆盖；转换结果仍保留");
    }
   }
  }
  ConversionCollectionResult Collect(IList<ConvertedFile> results){
   // A repeated completion or retry cannot publish the same output twice even
   // when two callers arrive before the first import has finished.
   lock(collectionGate)return CollectCore(results);
  }
  ConversionCollectionResult CollectCore(IList<ConvertedFile> results){
   var done=new List<string>();int added=0,reused=0,failed=0,unfiled=0;string firstError=null;
   var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var result in results??new List<ConvertedFile>()){
    if(result==null||result.Outputs==null)continue;
    var source=FindSource(result.Source);
    for(int index=0;index<result.Outputs.Length;index++){
     string path=result.Outputs[index];if(string.IsNullOrWhiteSpace(path)||!seen.Add(path))continue;
     try{
      if(CollectedAssetId(path)!=null){done.Add(path);reused++;continue;}
      if(!File.Exists(path)||Directory.Exists(path))throw new IOException("转换结果已移动或不可读取");
      // Publication already assigned a stable, sanitized source alias and page
      // suffix. A failed-only collection retry may contain a subset of pages:
      // deriving a new ordinal from that subset would rename page 12 as page 1.
      string name=ConversionFormats.NameWithoutExtension(path);
      // Quick conversion explicitly promises a new same-category copy. A
      // byte-identical record elsewhere must not redirect that result or have
      // its metadata changed. The older optional collection flow still reuses
      // existing active records without changing them.
      var imported=library.ImportConvertedFile(path,name,source==null?"":source.Source,source==null?"":source.Tags,source==null?"":source.Notes,
       preserveSourceCopy&&source!=null?source.Created:null,preserveSourceCopy?"keep":"ask");
      if(imported.ChoiceRequired){
       if(imported.Item==null)throw new IOException("无法确认已收藏的结果");
       if(imported.Item.Deleted){
        // A matching item in the recycle bin is not silently resurrected.
        imported=library.ImportConvertedFile(path,name,source==null?"":source.Source,source==null?"":source.Tags,source==null?"":source.Notes,null,"keep");
       }else VerifyExistingCopy(path,library.FullPath(imported.Item));
      }
      if(imported==null||imported.Item==null||imported.Skipped)throw new IOException("结果尚未收藏");
      bool isNew=!imported.Duplicate||imported.KeptCopy;
      if(isNew&&source!=null&&(source.Folders.Length>0||preserveSourceCopy)){
       try{
        library.Db.Transaction(delegate{
         if(preserveSourceCopy){
          imported.Item.Favorite=source.Favorite;imported.Item.HiddenFromAll=source.HiddenFromAll;
          library.Update(imported.Item);
          library.Db.Exec("UPDATE assets SET hidden_all="+(source.HiddenFromAll?1:0)+" WHERE id="+Database.Q(imported.Item.Id));
         }
         var available=new HashSet<string>(library.Folders(false).Select(folder=>folder.Id),StringComparer.Ordinal);
         var folders=source.Folders.Where(available.Contains).ToArray();
         foreach(string folder in folders)library.AddToFolder(new[]{imported.Item.Id},folder);
         if(folders.Length<source.Folders.Length)unfiled++;
        });
       }catch(Exception){
        // The file is already committed. Do not report a failed conversion or
        // delete it because a category changed concurrently.
        unfiled++;
       }
      }
      lock(collected)collected[path]=imported.Item.Id;
      done.Add(path);if(isNew)added++;else reused++;
     }catch(Exception error){failed++;if(firstError==null)firstError=Path.GetFileName(path)+"："+error.Message;}
    }
   }
   string message=done.Count==0&&failed==0?"没有可收藏的结果。":"已加入素材库 "+added+" 项"+(reused>0?" · 已有 "+reused+" 项":"");
   if(unfiled>0)message+="；"+unfiled+" 项的原分类已变化，请在素材库检查分类";
   if(failed>0)message+="；"+failed+" 项未加入："+firstError+"。请检查结果文件后重试收藏，不会重复收藏已完成项";
   return new ConversionCollectionResult{Message=message,CollectedPaths=done.ToArray()};
  }
 }
}
