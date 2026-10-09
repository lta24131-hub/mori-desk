using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Mori {
 internal sealed class BookmarkSource {public string Name,Path;public override string ToString(){return Name;}}
 internal sealed class BookmarkFileResult {public readonly List<Dictionary<string,object>> Items=new List<Dictionary<string,object>>();public int Skipped;}
 internal static class BookmarkFileImport {
  internal static List<BookmarkSource> Sources(string local){
   var result=new List<BookmarkSource>();foreach(string browser in new[]{"Google\\Chrome","Microsoft\\Edge"}){string root=Path.Combine(local,browser,"User Data");if(!Directory.Exists(root))continue;
    try{foreach(string dir in Directory.GetDirectories(root)){string name=System.IO.Path.GetFileName(dir);if(name!="Default"&&!name.StartsWith("Profile ",StringComparison.Ordinal))continue;string path=System.IO.Path.Combine(dir,"Bookmarks");if(File.Exists(path))result.Add(new BookmarkSource{Name=(browser.StartsWith("Google")?"Chrome":"Edge")+" · "+(name=="Default"?"默认用户":name),Path=path});}}catch(UnauthorizedAccessException){}
   }return result.OrderBy(s=>s.Name).ToList();
  }
  internal static BookmarkFileResult Read(string path){
   string text;using(var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){if(file.Length>32*1024*1024)throw new InvalidDataException("书签文件超过 32 MB，请分批导入。");using(var reader=new StreamReader(file))text=reader.ReadToEnd();}
   return text.TrimStart().StartsWith("{")?ParseJson(text):ParseHtml(text);
  }
  static string Str(Dictionary<string,object> node,string key){object value;return node.TryGetValue(key,out value)?Convert.ToString(value):"";}
  static void Add(BookmarkFileResult result,string url,string name,IEnumerable<string> path){
   try{url=Library.BookmarkAddress(url);}catch(ArgumentException){result.Skipped++;return;}
   if(result.Items.Count>=20000)throw new InvalidDataException("单次最多导入 20000 个网址。");result.Items.Add(new Dictionary<string,object>{{"url",url},{"name",name},{"path",path.ToArray()}});
  }
  internal static BookmarkFileResult ParseJson(string text){
   var json=new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=100};var root=json.Deserialize<Dictionary<string,object>>(text);object raw;var result=new BookmarkFileResult();int count=0;
   if(root==null||!root.TryGetValue("roots",out raw)||!(raw is Dictionary<string,object>))throw new InvalidDataException("不是 Chrome / Edge 书签文件。");
   Action<Dictionary<string,object>,List<string>> walk=null;walk=delegate(Dictionary<string,object> node,List<string> path){if(path.Count>32||++count>40000)throw new InvalidDataException("书签数量或层级过多。");if(Str(node,"type")=="url"){Add(result,Str(node,"url"),Str(node,"name"),path);return;}
    object children;if(!node.TryGetValue("children",out children)||!(children is IEnumerable))return;var branch=new List<string>(path);string name=Str(node,"name").Trim();if(name!="")branch.Add(name.Length>120?name.Substring(0,120):name);foreach(var child in (IEnumerable)children){var item=child as Dictionary<string,object>;if(item!=null)walk(item,branch);}
   };foreach(var node in ((Dictionary<string,object>)raw).Values.OfType<Dictionary<string,object>>())walk(node,new List<string>());return result;
  }
  // Bounded tokenizer for Netscape bookmark exports, not a browser/HTML runtime.
  internal static BookmarkFileResult ParseHtml(string text){
   if(text.IndexOf("NETSCAPE-Bookmark-file",StringComparison.OrdinalIgnoreCase)<0)throw new InvalidDataException("请选择浏览器导出的 HTML 书签文件。");
   var result=new BookmarkFileResult();var path=new List<string>();var levels=new Stack<bool>();string pending=null;
   var tokens=new Regex(@"<H3\b[^>]*>(?<folder>.*?)</H3\s*>|<A\b(?<attrs>[^>]*)>(?<title>.*?)</A\s*>|(?<close></DL\s*>)|(?<open><DL\b[^>]*>)",RegexOptions.IgnoreCase|RegexOptions.Singleline,TimeSpan.FromSeconds(2));
   foreach(Match match in tokens.Matches(text)){
    if(match.Groups["folder"].Success)pending=Plain(match.Groups["folder"].Value);
    else if(match.Groups["open"].Success){bool push=!string.IsNullOrWhiteSpace(pending);levels.Push(push);if(push)path.Add(pending.Length>120?pending.Substring(0,120):pending);pending=null;if(levels.Count>33)throw new InvalidDataException("书签层级过深。");}
    else if(match.Groups["close"].Success){if(levels.Count>0&&levels.Pop()&&path.Count>0)path.RemoveAt(path.Count-1);}
    else {string href=Attribute(match.Groups["attrs"].Value,"href");Add(result,href,Plain(match.Groups["title"].Value),path);}
   }return result;
  }
  internal static string Attribute(string tag,string name){var m=Regex.Match(tag,@"(?:^|\s)"+Regex.Escape(name)+@"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",RegexOptions.IgnoreCase,TimeSpan.FromMilliseconds(200));return WebUtility.HtmlDecode(m.Groups["v"].Value);}
  static string Plain(string text){return WebUtility.HtmlDecode(Regex.Replace(text,"<[^>]*>","",RegexOptions.None,TimeSpan.FromMilliseconds(200))).Trim();}
 }
}
