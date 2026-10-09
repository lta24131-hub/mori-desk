using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mori {
 public static class BrowserBookmarkImport {
  static string Get(Dictionary<string,object> data,string key){object value;return data.TryGetValue(key,out value)?Convert.ToString(value):"";}
  sealed class Entry {public string Url,Name;public List<string> Path;}
  public static object Handle(Library library,Dictionary<string,object> request){
   object raw;if(!request.TryGetValue("items",out raw)||!(raw is IEnumerable)||raw is string)throw new InvalidDataException("书签列表无效");
   var entries=new List<Entry>();int skipped=0,total=0;
   foreach(object item in (IEnumerable)raw){
    if(++total>200)throw new InvalidDataException("每批最多导入 200 个书签");
    var data=item as Dictionary<string,object>;if(data==null){skipped++;continue;}
    string url;try{url=Library.BookmarkAddress(Get(data,"url"));}catch(ArgumentException){skipped++;continue;}
    var path=new List<string>();object segments;if(data.TryGetValue("path",out segments)&&segments is IEnumerable&&!(segments is string))foreach(object segment in (IEnumerable)segments){string label=Convert.ToString(segment).Trim();if(label.Length>120)label=label.Substring(0,120);if(label.Any(char.IsControl))throw new InvalidDataException("书签分类名称无效");if(label!="")path.Add(label);if(path.Count>32)throw new InvalidDataException("书签层级过深");}
    entries.Add(new Entry{Url=url,Name=Get(data,"name"),Path=path});
   }
   string root=Get(request,"root");var folders=library.Folders(true);
   if(root!=""&&!folders.Any(f=>f.Id==root))throw new InvalidDataException("导入分类已不存在，请重新导入");
   int added=0,reused=0;
   if(entries.Count>0){
    if(root==""){string label=Get(request,"rootName");if(string.IsNullOrWhiteSpace(label))label="Chrome 书签";if(label.Length>120)label=label.Substring(0,120);var prior=folders.FirstOrDefault(f=>f.Parent==""&&f.Name==label);root=prior==null?library.CreateFolder(label,"",true):prior.Id;folders=library.Folders(true);}
    // Cache metadata once per small batch. Deliberately leave discarded URLs in
    // the trash rather than resurrecting them on every browser import.
    var discarded=new HashSet<string>(library.All().Where(a=>a.IsBookmark&&a.Deleted).Select(a=>a.Source));
    foreach(var entry in entries){
     if(discarded.Contains(entry.Url)){skipped++;continue;}
     string folder=root;foreach(string segment in entry.Path){var found=folders.FirstOrDefault(f=>f.Parent==folder&&f.Name==segment);if(found==null){found=new Category{Id=library.CreateFolder(segment,folder,true),Name=segment,Parent=folder,Scope="bookmark"};folders.Add(found);}folder=found.Id;}
     var result=library.ImportBookmark(entry.Url,entry.Name,folder,"","","reuse");if(result.Duplicate)reused++;else added++;
    }
   }
   return new{ok=true,root=root,added=added,reused=reused,skipped=skipped};
  }
 }
}
