using System;

namespace Mori {
 public static class SourceLink {
  public static bool TryParse(string value,out Uri uri){
   uri=null;if(string.IsNullOrWhiteSpace(value))return false;
   value=value.Trim();
   foreach(char c in value)if(char.IsControl(c))return false;
   Uri parsed;
   if(!Uri.TryCreate(value,UriKind.Absolute,out parsed)||
    (parsed.Scheme!=Uri.UriSchemeHttp&&parsed.Scheme!=Uri.UriSchemeHttps)||
    string.IsNullOrWhiteSpace(parsed.Host)||!string.IsNullOrEmpty(parsed.UserInfo))return false;
   uri=parsed;return true;
  }
  public static Uri Parse(string value){Uri uri;if(!TryParse(value,out uri))throw new ArgumentException("来源链接必须是有效的 HTTP 或 HTTPS 网址，且不能包含用户名或密码。","value");return uri;}
 }
}
