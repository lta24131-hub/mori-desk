using System;
using System.Collections.Generic;
using System.Text;

namespace Mori {
 // Parse once for a refresh. Matching reads only already-loaded metadata: a
 // missing file, offline share or removable drive must never block search.
 internal sealed class AssetSearch {
  static readonly AssetSearch Empty=new AssetSearch(new string[0]);
  readonly string[] terms;
  AssetSearch(string[] terms){this.terms=terms;}
  internal bool IsEmpty {get{return terms.Length==0;}}

  internal static AssetSearch Parse(string text){
   if(string.IsNullOrWhiteSpace(text))return Empty;
   var terms=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   var token=new StringBuilder();bool quoted=false;
   for(int i=0;i<text.Length;i++){
    char value=text[i];
    if(value=='"'){quoted=!quoted;continue;}
    if(!quoted&&char.IsWhiteSpace(value)){AddTerm(token,terms,seen);continue;}
    // Backslashes and URL punctuation are literal, not query escape syntax.
    token.Append(value);
   }
   // An unfinished quote is usable while typing; its remaining text is a phrase.
   AddTerm(token,terms,seen);
   return terms.Count==0?Empty:new AssetSearch(terms.ToArray());
  }
  static void AddTerm(StringBuilder token,List<string> terms,HashSet<string> seen){
   if(token.Length==0)return;
   string value=token.ToString();token.Clear();
   if(!string.IsNullOrWhiteSpace(value)&&seen.Add(value))terms.Add(value);
  }
  internal bool Matches(Asset asset){
   if(asset==null)return false;
   foreach(string term in terms){
    // Each term can match a different field, but a phrase cannot cross a field
    // boundary. Do not concatenate large notes/paths for every visible asset.
    if(!Contains(asset.Name,term)&&!Contains(asset.Tags,term)&&
       !Contains(asset.Notes,term)&&!Contains(asset.Source,term)&&
       !Contains(asset.ReferencePath,term))return false;
   }
   return true;
  }
  static bool Contains(string value,string term){
   return value!=null&&value.IndexOf(term,StringComparison.OrdinalIgnoreCase)>=0;
  }
 }
}
