using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

[assembly:System.Runtime.CompilerServices.InternalsVisibleTo("QingJie.Tests")]

namespace QingJie {
    public static class Translation {
        static readonly Lazy<HttpClient> client=new Lazy<HttpClient>(CreateClient);
        static readonly TranslationCache cache=new TranslationCache();
        static readonly SemaphoreSlim requests=new SemaphoreSlim(2,2);
        static readonly string clientKey="browser-chrome-120.0.0-Windows-"+Guid.NewGuid().ToString();
        public static string Provider {get{return AppState.Settings.TranslationProvider=="offline"?"offline":AppState.Settings.TranslationProvider=="google"?"google":"tencent";}}
        public static string ProviderName {get{return Provider=="offline"?"离线中英翻译":Provider=="google"?"Google 翻译":"腾讯翻译";}}
        public static string ProgressText {get{return Provider=="offline"?"正在本机翻译 · 不联网…":"正在翻译 · 只发送识别文字…";}}
        static string ResolveProvider(string value){value=value??Provider;if(value!="tencent"&&value!="google"&&value!="offline")throw new ArgumentException("未知翻译服务。");return value;}
        static string ProviderCacheKey(string provider){provider=ResolveProvider(provider);return provider=="offline"?provider+":"+OfflinePacks.CacheIdentity:provider;}
        static HttpClient CreateClient(){
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            var c=new HttpClient(new HttpClientHandler{UseProxy=false}){
                Timeout=TimeSpan.FromSeconds(12),MaxResponseContentBufferSize=1024*1024
            };
            // These bounded JSON requests can send their body with the headers.
            // A short translation need not wait for an interim HTTP 100 response.
            c.DefaultRequestHeaders.ExpectContinue=false;
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            c.DefaultRequestHeaders.Referrer=new Uri("https://transmart.qq.com/");
            return c;
        }
        internal static string TargetLanguage(string target){if(target!="zh"&&target!="en")throw new ArgumentException("请选择译成中文或英文。","target");return target;}
        internal static string TextCacheKey(string text,string target,string provider=null){return "text:keep-abbreviations-v1:"+ProviderCacheKey(provider)+":"+TargetLanguage(target)+":"+SourceLanguage(text)+":"+text;}
        // Image terminology policy is part of the result, not just the provider.
        // Old cached mistranslations must not bypass an updated context policy.
        internal const string ImageContextVersion="indexed-regions-v13-model-label";
        internal static readonly Regex ModelLabelHeader=new Regex(@"^\s*(?<tag>AI|Al|A1)[ \t]+model[ \t]+(?<number>[0-9]{1,4})[ \t]*[:：]",RegexOptions.CultureInvariant);
        static readonly Regex MissingChineseModelLabel=new Regex(@"^\s*(?:模型|型号)[ \t]*(?<number>[0-9]{1,4})[ \t]*[:：]",RegexOptions.CultureInvariant);
        const string ImageLiteralPattern=@"(?i:https?://|www\.)[^\s<>\[\]()""]+(?:\r?\n[^\s<>\[\]()""]*/[^\s<>\[\]()""]*)?|(?<![\p{L}\p{N}_])(?:GitHub|GitLab|OpenAI|ChatGPT|Discord|macOS|FreeBSD|[A-Z][a-z]+(?:[A-Z][a-z]+)+)(?![\p{L}\p{N}_])";
        static readonly string[][] imageAbbreviations={
            new[]{"AI","artificial intelligence","人工智能"},
            new[]{"UI","user interface","用户界面"},
            new[]{"UX","user experience","用户体验"},
            new[]{"API","application programming interface","应用程序编程接口","应用程序接口","应用编程接口"},
            new[]{"CPU","central processing unit","中央处理单元","中央处理器"},
            new[]{"GPU","graphics processing unit","图形处理单元","图形处理器"},
            new[]{"PDF","portable document format","可移植文档格式","便携式文档格式"},
            new[]{"USB","universal serial bus","通用串行总线"}
        };
        internal static string ImageCacheKey(IList<string> texts,string target,string provider=null){
            var prefix="image:"+ImageContextVersion+":"+ProviderCacheKey(provider)+":"+TargetLanguage(target)+":";
            if(texts.Count==1)return prefix+"one:"+texts[0];
            var key=new StringBuilder(prefix).Append("many:");
            foreach(var text in texts)key.Append(text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(text);
            return key.ToString();
        }
        public static async Task<string> Translate(string text,CancellationToken cancel,string targetLanguage="zh",string provider=null) {
            cancel.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(text))throw new InvalidOperationException("请先选择要翻译的文字。");
            provider=ResolveProvider(provider);string key=TextCacheKey(text,targetLanguage,provider);var saved=cache.Get(key);if(saved!=null)return saved[0];
            var result=await TranslateTextCore(text,cancel,(batch,language,token)=>TranslateMany(batch,token,language,targetLanguage,provider),targetLanguage);cancel.ThrowIfCancellationRequested();cache.Put(key,new[]{result});return result;
        }
        internal static Task<string> TranslateScreenshotSelection(string text,CancellationToken cancel,string targetLanguage="zh",string provider=null){
            var selected=ResolveProvider(provider);
            if(!cancel.IsCancellationRequested&&text!=null&&text.Length<=32){
                string local=ExactScreenshotCommand(text,targetLanguage,selected);
                if(local!=null&&!cancel.IsCancellationRequested)return Task.FromResult(local);
            }
            return Translate(text,cancel,targetLanguage,selected);
        }
        internal static async Task<string> TranslateTextCore(string text,CancellationToken cancel,Func<IList<string>,string,CancellationToken,Task<string[]>> translate,string targetLanguage="zh"){
            cancel.ThrowIfCancellationRequested();TargetLanguage(targetLanguage);
            if(string.IsNullOrWhiteSpace(text))throw new InvalidOperationException("请先选择要翻译的文字。");
            if(text.Length>10000)throw new InvalidOperationException("文字区域太大，请分区域翻译。");
            if(imageAbbreviations.Any(term=>text.Trim()==term[0]))return text;
            // General text keeps its spelling and paragraph breaks. OCR callers
            // normalize their own payload before sharing this preference.
            var source=new[]{text};var output=await translate(source,SourceLanguage(text),cancel);cancel.ThrowIfCancellationRequested();
            if(output==null||output.Length!=1||string.IsNullOrWhiteSpace(output[0]))throw new InvalidOperationException("译文不完整，请重试。");
            return KeepAbbreviations(source,output,targetLanguage)[0];
        }
        public static string SourceLanguage(string text){
            if(text.All(c=>c<128))return "auto";
            if(text.Count(Geometry.IsHangul)>=2)return "ko";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"jpn"))>=2)return "ja";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"rus"))>=2)return "ru";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"ara"))>=2)return "ar";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"tha"))>=2)return "th";
            return "auto";
        }
        public static Task<string[]> TranslateImage(IList<string> texts,CancellationToken cancel,string targetLanguage="zh",string provider=null){
            // Freeze inputs before leaving the capture dispatcher: even a cache lookup
            // can read the offline model manifest. The rest of this pipeline is UI-free.
            var snapshot=texts==null?null:texts.ToArray();string selected=ResolveProvider(provider);
            if(!cancel.IsCancellationRequested){
                var local=ExactScreenshotCommands(snapshot,targetLanguage,selected);
                if(local!=null&&!cancel.IsCancellationRequested)return Task.FromResult(local);
            }
            return Task.Run(()=>TranslateImageOffDispatcher(snapshot,cancel,targetLanguage,selected),cancel);
        }
        // Curated complete UI commands, not example sentences or OCR spell repair.
        // Do not translate an isolated word like "Open", "File", "Paste" or "Back":
        // it may be a description, name or noun outside a menu. Never replace
        // substrings, split a sentence, strip punctuation or rewrite the source.
        static class CommandLabels {
            internal static readonly Dictionary<string,string> Zh=new Dictionary<string,string>(StringComparer.Ordinal);
            internal static readonly Dictionary<string,string> En=new Dictionary<string,string>(StringComparer.Ordinal);
            static CommandLabels(){
                Add("Select all","全选");Add("Deselect all","取消全选");
                Add("Save as","另存为");Add("Save all","全部保存");Add("Save changes","保存更改");Add("Discard changes","放弃更改");
                Add("New folder","新建文件夹");Add("New window","新建窗口");Add("New tab","新建标签页");Add("Close tab","关闭标签页");
                Add("Copy link","复制链接");Add("Copy image","复制图片");Add("Copy path","复制路径");Add("Copy text","复制文本");
                Add("Paste as plain text","粘贴为纯文本");Add("Clear selection","清除选择");
                Add("Open file location","打开文件位置");Add("Fit to window","适应窗口");
                Add("Check for updates","检查更新");Add("Show in folder","在文件夹中显示");
            }
            static void Add(string en,string zh){
                En.Add(zh,en);
                Zh[en]=zh;Zh[en.ToLowerInvariant()]=zh;Zh[en.ToUpperInvariant()]=zh;
                Zh[System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(en.ToLowerInvariant())]=zh;
            }
        }
        internal static IEnumerable<string> CanonicalEnglishScreenshotCommands(){foreach(var en in CommandLabels.En.Values)yield return en;}
        static class StandaloneWords {
            internal static readonly Dictionary<string,string> Meanings=new Dictionary<string,string>(StringComparer.Ordinal);
            static StandaloneWords(){
                Add("Open","打开 / 开放");Add("Copy","复制 / 副本");Add("Save","保存 / 挽救");Add("Close","关闭 / 接近");
                Add("Paste","粘贴 / 糨糊");Add("Settings","设置");Add("Folder","文件夹");Add("Search","搜索");
                Add("Delete","删除");Add("Cancel","取消");Add("Download","下载");Add("Upload","上传");
                Add("Install","安装");Add("Uninstall","卸载");Add("Rename","重命名");Add("Undo","撤销");
                Add("Redo","重做");Add("Retry","重试");
            }
            static void Add(string word,string meaning){
                Meanings.Add(word,meaning);
                Meanings.Add(word.ToLowerInvariant(),meaning);
            }
        }
        internal static IEnumerable<string> StandaloneScreenshotWords(){return StandaloneWords.Meanings.Keys;}
        // Only the whole-image caller can establish that a word has no sentence
        // context. Give common senses there; selected prose keeps its provider.
        internal static string StandaloneWordMeaning(string word,string target,string provider){
            string meaning;
            return target=="zh"&&provider=="tencent"&&word!=null&&StandaloneWords.Meanings.TryGetValue(word,out meaning)?meaning:null;
        }
        internal static string ExactScreenshotCommand(string label,string target,string provider){
            if(provider!="tencent"||(target!="zh"&&target!="en")||string.IsNullOrEmpty(label)||label.Length>32)return null;
            string suffix=label.EndsWith("…",StringComparison.Ordinal)?"…":label.EndsWith("...",StringComparison.Ordinal)?"...":"";
            string key=suffix.Length==0?label:label.Substring(0,label.Length-suffix.Length);
            string value;if(!(target=="zh"?CommandLabels.Zh:CommandLabels.En).TryGetValue(key,out value))return null;
            return suffix.Length==0?value:value+suffix;
        }
        internal static string[] ExactScreenshotCommands(IList<string> source,string target,string provider){
            // Respect an expressly selected alternative/offline service, and
            // keep mixed pages intact so their normal context stays available.
            if(source==null||source.Count==0||source.Count>12)return null;
            string[] result=null;
            for(int i=0;i<source.Count;i++){
                string value=ExactScreenshotCommand(source[i],target,provider);if(value==null)return null;
                if(result==null)result=new string[source.Count];result[i]=value;
            }
            return result;
        }
        static async Task<string[]> TranslateImageOffDispatcher(IList<string> texts,CancellationToken cancel,string targetLanguage,string provider){
            cancel.ThrowIfCancellationRequested();ValidateImage(texts);
            TargetLanguage(targetLanguage);
            if(texts.All(t=>PreserveImageRegion(t,targetLanguage)))return texts.ToArray();
            provider=ResolveProvider(provider);string key=ImageCacheKey(texts,targetLanguage,provider);var saved=cache.Get(key);if(saved!=null)return saved;
            var result=await TranslateImageCore(texts,cancel,(batch,language,token)=>TranslateMany(batch,token,language,targetLanguage,provider),targetLanguage,CreateImageEchoRetry(provider,targetLanguage),provider=="offline"?500:40);
            cancel.ThrowIfCancellationRequested();if(CanCacheImageTranslation(texts,result,targetLanguage))cache.Put(key,result);return result;
        }
        internal static bool PreserveImageRegion(string text,string target){
            // Translating a Chinese-only UI label back into Chinese can corrupt
            // dates, version punctuation and OCR's uncertain button outlines.
            // Keep genuine foreign-language content on the normal route.
            return imageAbbreviations.Any(term=>text.Trim()==term[0])||!text.Any(c=>char.IsLetter(c)&&(target!="zh"||c<'\u3400'||c>'\u9fff'))||
                Regex.IsMatch(text.Trim(),@"^(?:GitHub|GitLab|OpenAI|ChatGPT|macOS|Windows|Linux|FreeBSD|Discord)(?:\s+\S)?$",RegexOptions.IgnoreCase)||
                Regex.IsMatch(text.Trim(),@"^[A-Z][a-z]+(?:[A-Z][a-z]+)+$");
        }
        static string[] RestoreImageRegions(IList<string> source,IList<string> translated,string target){
            return translated.Select((text,i)=>PreserveImageRegion(source[i],target)?source[i]:text).ToArray();
        }
        static void ValidateImage(IList<string> texts){
            if(texts==null||texts.Count==0||texts.Count>500||texts.Any(string.IsNullOrWhiteSpace))throw new InvalidOperationException("没有可翻译的文字区域。");
            if(texts.Any(t=>t.Length>9450))throw new InvalidOperationException("文字区域太大，请分区域翻译。");
        }
        internal static Func<string,string,CancellationToken,Task<string>> CreateImageEchoRetry(string provider,string targetLanguage){
            // Tencent honors explicit source language. Google currently uses
            // auto detection, so do not repeat its unchanged automatic request.
            if(provider!="tencent"||targetLanguage!="zh")return null;
            return async(text,source,token)=>(await TranslateMany(new[]{text},token,source,targetLanguage,provider))[0];
        }
        // Provider arrays preserve region identity. Text, punctuation and literal
        // bracketed numbers are content, never control markers sent to a translator.
        internal static async Task<string[]> TranslateImageCore(IList<string> texts,CancellationToken cancel,Func<IList<string>,string,CancellationToken,Task<string[]>> translate,string targetLanguage="zh",Func<string,string,CancellationToken,Task<string>> retryUntranslated=null,int batchRegionLimit=40){
            cancel.ThrowIfCancellationRequested();ValidateImage(texts);TargetLanguage(targetLanguage);
            string[] translated;
            if(texts.Count==1&&!Regex.IsMatch(texts[0],ImageLiteralPattern,RegexOptions.CultureInvariant)){
                if(PreserveImageRegion(texts[0],targetLanguage))return new[]{texts[0]};
                translated=await SendImageBatch(new[]{ImageTranslationInput(texts[0])},translate,cancel);
                if(targetLanguage=="zh"&&retryUntranslated!=null)translated=await RetryUntranslatedEnglish(texts,translated,cancel,retryUntranslated);
            }else{
                translated=await TranslateImageRegions(texts,cancel,translate,targetLanguage,retryUntranslated,batchRegionLimit);
            }
            cancel.ThrowIfCancellationRequested();return RestoreImageRegions(texts,KeepImageAbbreviations(texts,targetLanguage=="zh"?DeviceTerms(texts,translated):translated,targetLanguage),targetLanguage);
        }
        static async Task<string[]> TranslateImageRegions(IList<string> texts,CancellationToken cancel,Func<IList<string>,string,CancellationToken,Task<string[]>> translate,string targetLanguage,Func<string,string,CancellationToken,Task<string>> retryUntranslated,int batchRegionLimit){
            var matches=texts.Select(text=>Regex.Matches(text,ImageLiteralPattern,RegexOptions.CultureInvariant).Cast<Match>().ToArray()).ToArray();
            var translated=texts.ToArray();
            // Translate complete sentences, including product names, first.
            // URLs stay local; rejected/missing names alone use the exact-literal
            // fallback. No invented placeholders or sentence-specific rewrites.
            var direct=Enumerable.Range(0,texts.Count).Where(i=>!matches[i].Any(m=>IsImageLink(m.Value))).ToArray();
            if(direct.Length>0){
                var values=await TranslateImageBatches(direct.Select(i=>texts[i]).ToArray(),cancel,translate,targetLanguage,retryUntranslated,batchRegionLimit);
                for(int i=0;i<direct.Length;i++)translated[direct[i]]=values[i];
            }
            var fallback=Enumerable.Range(0,texts.Count).Where(i=>matches[i].Any(m=>IsImageLink(m.Value))||!ImageNamesIntact(matches[i],translated[i])).ToArray();
            if(fallback.Length>0){
                var values=await TranslateLiteralImageRegions(fallback.Select(i=>texts[i]).ToArray(),cancel,translate,targetLanguage,retryUntranslated,batchRegionLimit);
                for(int i=0;i<fallback.Length;i++)translated[fallback[i]]=values[i];
            }
            return translated;
        }
        static bool IsImageLink(string text){return text.StartsWith("http://",StringComparison.OrdinalIgnoreCase)||text.StartsWith("https://",StringComparison.OrdinalIgnoreCase)||text.StartsWith("www.",StringComparison.OrdinalIgnoreCase);}
        static bool ImageNamesIntact(IEnumerable<Match> names,string translated){
            foreach(var group in names.GroupBy(m=>m.Value,StringComparer.Ordinal)){
                // Chinese translation can touch a Latin name without a space.
                // Latin identifier boundaries still reject renamed/embedded tokens.
                string pattern=@"(?<![A-Za-z0-9_])"+Regex.Escape(group.Key)+@"(?![A-Za-z0-9_])";
                if(Regex.Matches(translated,pattern,RegexOptions.CultureInvariant).Count!=group.Count())return false;
            }
            return true;
        }
        static async Task<string[]> TranslateLiteralImageRegions(IList<string> texts,CancellationToken cancel,Func<IList<string>,string,CancellationToken,Task<string[]>> translate,string targetLanguage,Func<string,string,CancellationToken,Task<string>> retryUntranslated,int batchRegionLimit){
            // Links are literal data. Keep them locally rather than trusting a
            // provider to preserve placeholder markers or URL spelling. Only
            // prose segments go through the same indexed, bounded batching.
            var pieces=new List<string>();var joins=new List<int[]>();var literal=new HashSet<int>();
            foreach(string source in texts){
                var indices=new List<int>();int end=0;
                foreach(Match match in Regex.Matches(source,ImageLiteralPattern,RegexOptions.CultureInvariant)){
                    if(match.Index>end){indices.Add(pieces.Count);pieces.Add(source.Substring(end,match.Index-end));}
                    indices.Add(pieces.Count);literal.Add(pieces.Count);pieces.Add(match.Value);end=match.Index+match.Length;
                }
                if(end<source.Length){indices.Add(pieces.Count);pieces.Add(source.Substring(end));}
                joins.Add(indices.ToArray());
            }
            if(literal.Count>0){
                var result=pieces.ToArray();var prose=Enumerable.Range(0,pieces.Count).Where(i=>!literal.Contains(i)&&!string.IsNullOrWhiteSpace(pieces[i])).ToArray();
                // 500 is the public region limit; splitting links may produce
                // more segments, so process bounded groups without truncation.
                for(int start=0;start<prose.Length;start+=500){
                    var group=prose.Skip(start).Take(500).ToArray();
                    var values=await TranslateImageCore(group.Select(i=>pieces[i].Trim()).ToArray(),cancel,translate,targetLanguage,retryUntranslated,batchRegionLimit);
                    for(int j=0;j<group.Length;j++){
                        string source=pieces[group[j]];
                        result[group[j]]=source.Substring(0,source.Length-source.TrimStart().Length)+values[j]+source.Substring(source.TrimEnd().Length);
                    }
                }
                cancel.ThrowIfCancellationRequested();return joins.Select(indices=>string.Concat(indices.Select(i=>result[i]))).ToArray();
            }
            return await TranslateImageBatches(texts,cancel,translate,targetLanguage,retryUntranslated,batchRegionLimit);
        }
        static async Task<string[]> TranslateImageBatches(IList<string> texts,CancellationToken cancel,Func<IList<string>,string,CancellationToken,Task<string[]>> translate,string targetLanguage,Func<string,string,CancellationToken,Task<string>> retryUntranslated,int batchRegionLimit){
            var translated=texts.ToArray();var batches=new List<int[]>();var current=new List<int>();int length=0;string language=null;
            for(int i=0;i<texts.Count;i++){
                if(PreserveImageRegion(texts[i],targetLanguage))continue;
                string next=SourceLanguage(texts[i]);
                // The offline model loads once per job. Forty short labels is an online
                // API limit, not a model limit; keep the same character budget per job.
                if(current.Count>0&&(next!=language||current.Count==batchRegionLimit||length+texts[i].Length>9500)){
                    batches.Add(current.ToArray());current.Clear();length=0;
                }
                current.Add(i);length+=texts[i].Length;language=next;
            }
            if(current.Count>0)batches.Add(current.ToArray());
            using(var stop=CancellationTokenSource.CreateLinkedTokenSource(cancel))using(var slots=new SemaphoreSlim(2,2)){
                await Task.WhenAll(batches.Select(indices=>TranslateIndexedBatch(texts,translated,indices,translate,slots,stop)));
            }
            cancel.ThrowIfCancellationRequested();
            if(targetLanguage=="zh"&&retryUntranslated!=null)translated=await RetryUntranslatedEnglish(texts,translated,cancel,retryUntranslated);
            cancel.ThrowIfCancellationRequested();return translated;
        }
        internal static string[] KeepImageAbbreviations(IList<string> sources,IList<string> translated,string targetLanguage){
            return KeepAbbreviations(sources.Select(ImageTranslationInput).ToArray(),translated,targetLanguage);
        }
        static string[] KeepAbbreviations(IList<string> sources,IList<string> translated,string targetLanguage){
            // Send the complete sentence to the provider, so abbreviations do
            // not split its grammar/context. Restore only known expansions of
            // abbreviations actually present in this source region. Ambiguous
            // occurrence counts or explicit long-form wording stay untouched.
            return translated.Select((value,index)=>{
                if(targetLanguage!="zh")return value;
                string source=sources[index];
                foreach(var term in imageAbbreviations){
                    if(source.IndexOf(term[0],StringComparison.Ordinal)<0)continue;
                    string pattern=@"(?<![A-Za-z0-9_])"+term[0]+@"(?![A-Za-z0-9_])";
                    int expected=Regex.Matches(source,pattern).Count;
                    if(expected==0||source.IndexOf(term[1],StringComparison.OrdinalIgnoreCase)>=0||term.Skip(2).Any(source.Contains))continue;
                    string expansions=string.Join("|",term.Skip(2).Select(Regex.Escape));
                    int expanded=Regex.Matches(value,expansions).Count;
                    if(expanded>0&&expanded+Regex.Matches(value,pattern).Count==expected)value=Regex.Replace(value,expansions,term[0]);
                }
                // Some services translate a numbered header's noun and exact number
                // but drop the leading identifier. Restore only in that matching
                // empty slot; never guess whether an unverified Al or A1 meant AI.
                var header=ModelLabelHeader.Match(source);if(header.Success){
                    var slot=MissingChineseModelLabel.Match(value);
                    if(slot.Success&&slot.Groups["number"].Value==header.Groups["number"].Value&&
                        !Regex.IsMatch(value,@"(?<![A-Za-z0-9_])(?:AI|Al|A1)(?![A-Za-z0-9_])",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))
                        value=header.Groups["tag"].Value+" "+value.TrimStart();
                }
                return value;
            }).ToArray();
        }
        static async Task TranslateIndexedBatch(IList<string> sources,string[] destination,int[] indices,Func<IList<string>,string,CancellationToken,Task<string[]>> translate,SemaphoreSlim slots,CancellationTokenSource stop){
            await slots.WaitAsync(stop.Token);
            try{
                var input=indices.Select(i=>ImageTranslationInput(sources[i])).ToArray();
                var output=await SendImageBatch(input,translate,stop.Token);
                for(int i=0;i<indices.Length;i++)destination[indices[i]]=output[i];
            }catch{stop.Cancel();throw;}finally{slots.Release();}
        }
        static async Task<string[]> SendImageBatch(string[] input,Func<IList<string>,string,CancellationToken,Task<string[]>> translate,CancellationToken cancel){
            var output=await translate(input,SourceLanguage(input[0]),cancel);
            cancel.ThrowIfCancellationRequested();
            if(output==null||output.Length!=input.Length||output.Any(string.IsNullOrWhiteSpace))throw new InvalidOperationException("译文不完整，已保留原图，请重试。");
            return output;
        }
        internal static string ImageTranslationInput(string source){
            // Spelling belongs to OcrTranslationInput, where model evidence is
            // available. String-only/selected/manual text must not be guessed.
            return ImageProse(source);
        }
        internal static string ImageProse(string source){
            // OCR rows describe screen geometry, not sentence boundaries. Feed
            // a complete paragraph to the provider; retain genuine list items,
            // blank paragraphs and code/table rows. Literal URLs have already
            // been separated locally, and the original OCR text is untouched.
            if(source.IndexOf('\r')<0&&source.IndexOf('\n')<0)return source;
            var lines=source.Replace("\r\n","\n").Replace('\r','\n').Split('\n');
            var result=new StringBuilder(lines[0]);
            for(int i=1;i<lines.Length;i++){
                string previous=lines[i-1].TrimEnd(),next=lines[i].TrimStart();
                bool boundary=previous.Length==0||next.Length==0||
                    Regex.IsMatch(next,@"^(?:[-*•]\s|\d+[.)]\s|#{1,6}\s|```|\|)")||
                    Regex.IsMatch(previous,@"^(?:```|\|)");
                if(boundary){result.Append('\n').Append(lines[i]);continue;}
                while(result.Length>0&&(result[result.Length-1]==' '||result[result.Length-1]=='\t'))result.Length--;
                char a=previous[previous.Length-1],b=next[0];
                bool compact=(a>='\u3400'&&a<='\u9fff'&&b>='\u3400'&&b<='\u9fff')||
                    (a=='-'&&previous.Length>1&&char.IsLetter(previous[previous.Length-2])&&char.IsLetter(b));
                if(!compact)result.Append(' ');result.Append(next);
            }
            return result.ToString();
        }
        static string EnglishPhraseKey(string text){return Regex.Replace(text.Replace('\u2019','\''),@"\s+"," ").Trim().Trim('.','!','?',':',';').ToLowerInvariant();}
        internal static bool IsUntranslatedEnglishPhrase(string source,string translated){
            // Deliberately conservative: no accented Latin, other scripts, URLs,
            // one-word names or multiword names without an English grammar clue.
            if(string.IsNullOrWhiteSpace(source)||string.IsNullOrWhiteSpace(translated)||source.Length>240||
                translated.Any(c=>c>='\u3400'&&c<='\u9fff')||
                !Regex.IsMatch(source,@"^[A-Za-z0-9\s.,!?;:'""()&\-\u2019]+$")||source.IndexOfAny(new[]{'/','\\','@'})>=0||
                Regex.IsMatch(source,@"www\.|\.[A-Za-z0-9]{1,8}\s*$",RegexOptions.IgnoreCase))return false;
            int words=Regex.Matches(source,@"[A-Za-z]+(?:['\u2019][A-Za-z]+)*").Count;
            return words>=2&&words<=24&&Regex.IsMatch(source,@"\b(the|your|you['\u2019]re|we['\u2019]re|our|this|these|that|those|with|from|into|onto|please|don['\u2019]t|doesn['\u2019]t|can['\u2019]t|won['\u2019]t)\b",RegexOptions.IgnoreCase)&&
                EnglishPhraseKey(source)==EnglishPhraseKey(translated);
        }
        internal static bool CanCacheImageTranslation(IList<string> sources,IList<string> translated,string targetLanguage){
            if(sources==null||translated==null||sources.Count==0||sources.Count!=translated.Count||translated.Any(string.IsNullOrWhiteSpace))return false;
            // A recoverable echo must remain retryable immediately, rather than
            // being treated as a completed translation for the cache lifetime.
            return targetLanguage!="zh"||!Enumerable.Range(0,sources.Count).Any(i=>IsUntranslatedEnglishPhrase(sources[i],translated[i]));
        }
        static async Task<string[]> RetryUntranslatedEnglish(IList<string> sources,string[] translated,CancellationToken cancel,Func<string,string,CancellationToken,Task<string>> retry){
            cancel.ThrowIfCancellationRequested();var indices=Enumerable.Range(0,sources.Count).Where(i=>IsUntranslatedEnglishPhrase(sources[i],translated[i])).Take(2).ToArray();
            if(indices.Length==0)return translated;
            // One shared four-second budget, at most two requests concurrently.
            // The callback closes over the original provider; never switch it.
            using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel)){
                deadline.CancelAfter(TimeSpan.FromSeconds(4));
                var replacements=await Task.WhenAll(indices.Select(i=>RetryImagePhrase(sources[i],translated[i],deadline.Token,cancel,retry)));
                cancel.ThrowIfCancellationRequested();var result=(string[])translated.Clone();for(int i=0;i<indices.Length;i++)result[indices[i]]=replacements[i];return result;
            }
        }
        static async Task<string> RetryImagePhrase(string source,string previous,CancellationToken deadline,CancellationToken cancel,Func<string,string,CancellationToken,Task<string>> retry){
            try{
                deadline.ThrowIfCancellationRequested();string query=source.TrimEnd();if(!Regex.IsMatch(query,@"[.!?]$"))query+=".";
                var translated=await retry(query,"en",deadline);cancel.ThrowIfCancellationRequested();
                if(!deadline.IsCancellationRequested&&!string.IsNullOrWhiteSpace(translated)&&translated.Length<=Math.Max(256,source.Length*8)&&
                    Regex.IsMatch(translated,@"[\u3400-\u9fff]")&&!Regex.IsMatch(translated,@"\[{1,3}\d{4}\]{1,3}"))return translated.Trim();
            }catch(OperationCanceledException){cancel.ThrowIfCancellationRequested();}
            catch(HttpRequestException){}catch(InvalidOperationException){}
            return previous;
        }
        public static string[] DeviceTerms(IList<string> texts,IList<string> translated){
            string context=string.Join("\n",texts);
            var options=RegexOptions.IgnoreCase;
            // A single "shot" or "follow" is ambiguous. Without an explicit
            // device, require independent framing, photography and mechanism
            // clues. Exclusions win even when a camera appears in the text.
            bool photographicMechanism=Regex.IsMatch(context,@"\bframing\b",options)&&
                Regex.IsMatch(context,@"\b(shots?|shoots?)\b",options)&&
                Regex.IsMatch(context,@"\b(height\s+adjustment|wheel\s+travel)\b",options);
            if(!photographicMechanism&&!Regex.IsMatch(context,@"\b(gimbals?|tripods?|cameras?|hinges?|masts?)\b",options))return translated.ToArray();
            if(Regex.IsMatch(context,@"\b(poker|cards?|bets?|betting|origami|guns?|rifles?|firearms?|shooting|bullets?|ammunition)\b",options))return translated.ToArray();
            bool camera=Regex.IsMatch(context,@"\b(gimbals?|cameras?)\b",options);
            bool photographicShot=(photographicMechanism||(camera&&Regex.IsMatch(context,@"\b(framing|tripods?|gimbals?)\b",options)))&&
                !Regex.IsMatch(context,@"\b(games?|sports?|soccer|football|basketball|hockey|golf|tennis)\b",options);
            Func<string,bool> label=value=>texts.Any(t=>Regex.IsMatch(t.Trim(),@"^"+value+@"[.!:]?$",options));
            int motionLabels=(label("Follow")?1:0)+(label("Parallel")?1:0)+(label("Orbit")?1:0);
            bool motionModes=(camera||photographicMechanism)&&motionLabels>=2;
            return translated.Select((text,i)=>{string source=texts[i];
                if(Regex.IsMatch(source,@"\bgimbals?\b",RegexOptions.IgnoreCase))text=Regex.Replace(text,@"万向节|平衡环|万向架|台子|\bgimbals?\b","云台",RegexOptions.IgnoreCase);
                if(Regex.IsMatch(source,@"\b(fold|folds|folded|folding)\b",RegexOptions.IgnoreCase))text=Regex.Replace(text,@"盖牌|弃牌|\b(fold|folds|folded|folding)\b","折叠",RegexOptions.IgnoreCase);
                if(Regex.IsMatch(source,@"\b(shots?|shoots?)\b",options))text=text.Replace("射击","拍摄");
                if((camera||photographicMechanism)&&Regex.IsMatch(source,@"\bframing\b",options))text=text.Replace("框定","构图");
                // A standalone photography slogan can use "shot" as a frame,
                // not a turn in a game. Do not rewrite ordinary shot sentences.
                if(photographicShot&&Regex.IsMatch(source,@"(?:^|[.!?\r\n])\s*your\s+shot\s*[.!?]?(?=$|[\r\n])",options))
                    text=Regex.Replace(text,@"(^|[。.!！?？\r\n])([ \t]*)(?:该你了|轮到你了|轮到你)(?=[。.!！?？\r\n]|$)","$1$2你的镜头");
                // Only standalone source mode labels, never ordinary sentences
                // such as "follow the instructions" or an astronomical orbit.
                if(motionModes&&Regex.IsMatch(source.Trim(),@"^Follow[.!:]?$",options))text=Regex.Replace(text,@"^(\s*)(?:遵循|遵从|follow)(模式)?([。.!！]?\s*)$","$1跟随$2$3",options);
                if(motionModes&&Regex.IsMatch(source.Trim(),@"^Orbit[.!:]?$",options))text=Regex.Replace(text,@"^(\s*)(?:轨道|orbit)(模式)?([。.!！]?\s*)$","$1环绕$2$3",options);
                if(Regex.IsMatch(source,@"\bmasts?\b",RegexOptions.IgnoreCase))text=Regex.Replace(text,@"桅杆|\bmasts?\b","立柱",RegexOptions.IgnoreCase);
                if(Regex.IsMatch(source,@"\bbody\b",RegexOptions.IgnoreCase))text=text.Replace("身体","机身");return text;
            }).ToArray();
        }
        public static string[] ParseResponse(string response,int count){
                var data=new JavaScriptSerializer().DeserializeObject(response) as Dictionary<string,object>;
                object raw,head;var ok=data!=null&&data.TryGetValue("header",out head)?head as Dictionary<string,object>:null;
                if(ok==null||!ok.ContainsKey("ret_code")||Convert.ToString(ok["ret_code"])!="succ"||!data.TryGetValue("auto_translation",out raw))throw new InvalidOperationException("翻译接口返回异常或被限流。截图功能不受影响。");
                var list=raw as IList;
                if(list==null||list.Count!=count||list.Cast<object>().Any(v=>!(v is string)||string.IsNullOrWhiteSpace((string)v)))throw new InvalidOperationException("翻译结果不完整，请稍后重试。");
                return list.Cast<string>().ToArray();
        }
        static void AppendJsonValue(StringBuilder json,string value){
            if(value==null){json.Append("null");return;}
            // Unescaped text is common in single-line OCR. Delegate escaping
            // to the existing serializer for quotes, controls and surrogates.
            foreach(char c in value)if(c<' '||c=='"'||c=='\\'||c=='\u2028'||c=='\u2029'||char.IsSurrogate(c)){
                json.Append(new JavaScriptSerializer().Serialize(value));return;
            }
            json.Append('"').Append(value).Append('"');
        }
        internal static string RequestJson(IList<string> texts,string sourceLanguage,string targetLanguage){
            var json=new StringBuilder("{\"header\":{\"fn\":\"auto_translation\",\"client_key\":\"").Append(clientKey)
                .Append("\"},\"type\":\"plain\",\"model_category\":\"normal\",\"source\":{\"lang\":");
            AppendJsonValue(json,sourceLanguage);json.Append(",\"text_list\":[");
            for(int i=0;i<texts.Count;i++){if(i!=0)json.Append(',');AppendJsonValue(json,texts[i]);}
            return json.Append("]},\"target\":{\"lang\":\"").Append(TargetLanguage(targetLanguage)).Append("\"}}").ToString();
        }
        internal static async Task<string[]> TranslateIndependentRegions(IList<string> texts,CancellationToken cancel,Func<string,CancellationToken,Task<string>> send){
            // Google's public endpoint accepts a single independent paragraph. Keep two
            // lanes busy without allocating a pending HTTP task per label. The transport
            // still uses the same global two-slot gate across all windows and batches.
            cancel.ThrowIfCancellationRequested();
            // Each request contains only one paragraph. Identical labels in the
            // same batch use the same reply, but casing/whitespace never collapse.
            if(texts.Count==1){var only=await send(texts[0],cancel).ConfigureAwait(false);cancel.ThrowIfCancellationRequested();return new[]{only};}
            var groups=texts.Select((value,index)=>new {value,index}).GroupBy(x=>x.value,StringComparer.Ordinal).ToArray();
            var output=new string[texts.Count];int next=-1;
            using(var stop=CancellationTokenSource.CreateLinkedTokenSource(cancel)){
                Func<Task> run=async()=>{
                    try{while(true){stop.Token.ThrowIfCancellationRequested();int i=Interlocked.Increment(ref next);if(i>=groups.Length)return;
                        var group=groups[i];string value=await send(group.Key,stop.Token).ConfigureAwait(false);
                        foreach(var item in group)output[item.index]=value;
                    }}catch{stop.Cancel();throw;}
                };
                await Task.WhenAll(run(),run()).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();return output;
            }
        }
        public static async Task<string[]> TranslateMany(IList<string> texts,CancellationToken cancel,string sourceLanguage="auto",string targetLanguage="zh",string provider=null){
            cancel.ThrowIfCancellationRequested();TargetLanguage(targetLanguage);
            if(texts==null||texts.Count==0||texts.Any(string.IsNullOrWhiteSpace))throw new InvalidOperationException("请先选择要翻译的文字。");
            if(texts.Count>500||texts.Any(t=>t.Length>10000))throw new InvalidOperationException("文字区域太大，请分区域翻译。");
            var result=new List<string>();
            if(ResolveProvider(provider)=="offline")return await OfflineTranslation.Translate(texts,targetLanguage,cancel);
            if(ResolveProvider(provider)=="google")return await TranslateIndependentRegions(texts,cancel,(text,token)=>GoogleTranslation.Translate(text,targetLanguage,token,requests));
            for(int offset=0;offset<texts.Count;){
                cancel.ThrowIfCancellationRequested();var batch=new List<string>();int length=0;
                while(offset<texts.Count&&batch.Count<40&&(batch.Count==0||length+texts[offset].Length<=10000)){length+=texts[offset].Length;batch.Add(texts[offset++]);}
                await requests.WaitAsync(cancel);
                try{using(var body=new StringContent(RequestJson(batch,sourceLanguage,targetLanguage),Encoding.UTF8,"application/json"))
                using(var response=await client.Value.PostAsync("https://transmart.qq.com/api/imt",body,cancel)) {
                    if(!response.IsSuccessStatusCode)throw new InvalidOperationException("腾讯翻译暂时不可用（"+(int)response.StatusCode+"），请稍后重试。");
                    result.AddRange(ParseResponse(await response.Content.ReadAsStringAsync(),batch.Count));
                }}catch(HttpRequestException){throw new InvalidOperationException("无法连接腾讯翻译，请检查网络后重试。原图仍保留。");}finally{requests.Release();}
            }
            return result.ToArray();
        }
    }
}
