using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace QingJie {
    internal static class GoogleTranslation {
        // Created on first Google request. Use Windows' existing proxy configuration;
        // never enable a VPN, modify proxy settings or fall back to another provider.
        static readonly Lazy<HttpClient> client=new Lazy<HttpClient>(()=>{ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;return new HttpClient(new HttpClientHandler {UseProxy=true}){Timeout=TimeSpan.FromSeconds(12),MaxResponseContentBufferSize=1024*1024};});
        internal static string Parse(string json){
            try{var root=new JavaScriptSerializer().DeserializeObject(json) as IList;var sentences=root!=null&&root.Count>0?root[0] as IList:null;if(sentences==null||sentences.Count==0)throw new FormatException();var result=new StringBuilder();foreach(var item in sentences){var row=item as IList;if(row==null||row.Count==0||!(row[0] is string))throw new FormatException();result.Append((string)row[0]);}if(string.IsNullOrWhiteSpace(result.ToString()))throw new FormatException();return result.ToString();}
            catch{throw new InvalidOperationException("Google 返回了无效结果，未替换原文。请稍后重试。");}
        }
        internal static IList<string> Chunks(string text){
            var parts=new List<string>();int offset=0;
            while(offset<text.Length){int length=Math.Min(700,text.Length-offset);if(offset+length<text.Length){int end=offset+length;int boundary=text.LastIndexOfAny(new[]{'\n',' ','。','！','？','.','!','?'},end-1,length/2);if(boundary>=offset+length/2)end=boundary+1;foreach(Match marker in Regex.Matches(text.Substring(Math.Max(offset,end-12),Math.Min(text.Length-end+12,24)),@"\[{1,3}\d{4}\]{1,3}")){int start=Math.Max(offset,end-12)+marker.Index;if(start<end&&start+marker.Length>end)end=start;}if(end>offset&&char.IsHighSurrogate(text[end-1]))end--;length=end-offset;}parts.Add(text.Substring(offset,length));offset+=length;}return parts;
        }
        internal static async Task<string> Translate(string text,string target,CancellationToken cancel,SemaphoreSlim slots){
            var result=new List<string>();foreach(var part in Chunks(text)){
                cancel.ThrowIfCancellationRequested();string uri="https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl="+(target=="zh"?"zh-CN":"en")+"&dt=t&q="+Uri.EscapeDataString(part);
                await slots.WaitAsync(cancel);
                try{using(var response=await client.Value.GetAsync(uri,cancel)){if(!response.IsSuccessStatusCode)throw new InvalidOperationException("Google 翻译暂时不可用（"+(int)response.StatusCode+"）。请确认能访问 Google，或手动切回腾讯。");result.Add(Parse(await response.Content.ReadAsStringAsync()));}}
                catch(HttpRequestException){throw new InvalidOperationException("无法连接 Google。请检查现有代理或网络；未改用其他翻译服务。");}finally{slots.Release();}
            }return string.Join("\n",result);
        }
    }
}
