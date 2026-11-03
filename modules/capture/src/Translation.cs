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
        static HttpClient CreateClient(){ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;var c=new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(12),MaxResponseContentBufferSize=1024*1024};c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");c.DefaultRequestHeaders.Referrer=new Uri("https://transmart.qq.com/");return c;}
        internal static string TargetLanguage(string target){if(target!="zh"&&target!="en")throw new ArgumentException("请选择译成中文或英文。","target");return target;}
        internal static string TextCacheKey(string text,string target,string provider=null){return "text:"+ProviderCacheKey(provider)+":"+TargetLanguage(target)+":"+SourceLanguage(text)+":"+text;}
        // Image terminology policy is part of the result, not just the provider.
        // Old cached mistranslations must not bypass an updated context policy.
        const string ImageContextVersion="indexed-regions-v8-softwraps";
        internal static string ImageCacheKey(IList<string> texts,string target,string provider=null){return "image:"+ImageContextVersion+":"+ProviderCacheKey(provider)+":"+TargetLanguage(target)+":"+new JavaScriptSerializer().Serialize(texts);}
        public static async Task<string> Translate(string text,CancellationToken cancel,string targetLanguage="zh",string provider=null) {
            cancel.ThrowIfCancellationRequested();if(string.IsNullOrWhiteSpace(text))throw new InvalidOperationException("请先选择要翻译的文字。");
            provider=ResolveProvider(provider);string key=TextCacheKey(text,targetLanguage,provider);var saved=cache.Get(key);if(saved!=null)return saved[0];
            var result=await TranslateMany(new[]{text},cancel,SourceLanguage(text),targetLanguage,provider);cancel.ThrowIfCancellationRequested();cache.Put(key,result);return result[0];
        }
        public static string SourceLanguage(string text){
            if(text.Count(Geometry.IsHangul)>=2)return "ko";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"jpn"))>=2)return "ja";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"rus"))>=2)return "ru";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"ara"))>=2)return "ar";
            if(text.Count(c=>OcrLanguagePacks.IsScript(c,"tha"))>=2)return "th";
            return "auto";
        }
        public static async Task<string[]> TranslateImage(IList<string> texts,CancellationToken cancel,string targetLanguage="zh",string provider=null){
            cancel.ThrowIfCancellationRequested();ValidateImage(texts);
            TargetLanguage(targetLanguage);
            if(texts.All(t=>PreserveImageRegion(t,targetLanguage)))return texts.ToArray();
            provider=ResolveProvider(provider);string key=ImageCacheKey(texts,targetLanguage,provider);var saved=cache.Get(key);if(saved!=null)return saved;
            var result=await TranslateImageCore(texts,cancel,(batch,language,token)=>TranslateMany(batch,token,language,targetLanguage,provider),targetLanguage,CreateImageEchoRetry(provider,targetLanguage));
            cancel.ThrowIfCancellationRequested();if(CanCacheImageTranslation(texts,result,targetLanguage))cache.Put(key,result);return result;
        }
        internal static bool PreserveImageRegion(string text,string target){
            // Translating a Chinese-only UI label back into Chinese can corrupt
            // dates, version punctuation and OCR's uncertain button outlines.
            // Keep genuine foreign-language content on the normal route.
            return !text.Any(c=>char.IsLetter(c)&&(target!="zh"||c<'\u3400'||c>'\u9fff'))||
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
        internal static async Task<string[]> TranslateImageCore(IList<string> texts,CancellationToken cancel,Func<IList<string>,string,CancellationToken,Task<string[]>> translate,string targetLanguage="zh",Func<string,string,CancellationToken,Task<string>> retryUntranslated=null){
            cancel.ThrowIfCancellationRequested();ValidateImage(texts);TargetLanguage(targetLanguage);
            // Links are literal data. Keep them locally rather than trusting a
            // provider to preserve placeholder markers or URL spelling. Only
            // prose segments go through the same indexed, bounded batching.
            var pieces=new List<string>();var joins=new List<int[]>();var literal=new HashSet<int>();
            foreach(string source in texts){
                var indices=new List<int>();int end=0;
                foreach(Match match in Regex.Matches(source,@"(?i:https?://|www\.)[^\s<>\[\]()""]+(?:\r?\n[^\s<>\[\]()""]*/[^\s<>\[\]()""]*)?|(?<![\p{L}\p{N}_])(?:GitHub|GitLab|OpenAI|ChatGPT|Discord|macOS|FreeBSD|[A-Z][a-z]+(?:[A-Z][a-z]+)+)(?![\p{L}\p{N}_])",RegexOptions.CultureInvariant)){
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
                    var values=await TranslateImageCore(group.Select(i=>pieces[i].Trim()).ToArray(),cancel,translate,targetLanguage,retryUntranslated);
                    for(int j=0;j<group.Length;j++){
                        string source=pieces[group[j]];
                        result[group[j]]=source.Substring(0,source.Length-source.TrimStart().Length)+values[j]+source.Substring(source.TrimEnd().Length);
                    }
                }
                cancel.ThrowIfCancellationRequested();return joins.Select(indices=>string.Concat(indices.Select(i=>result[i]))).ToArray();
            }
            var translated=texts.ToArray();var batches=new List<int[]>();var current=new List<int>();int length=0;string language=null;
            for(int i=0;i<texts.Count;i++){
                if(PreserveImageRegion(texts[i],targetLanguage))continue;
                string next=SourceLanguage(texts[i]);
                if(current.Count>0&&(next!=language||current.Count==40||length+texts[i].Length>9500)){
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
            cancel.ThrowIfCancellationRequested();return RestoreImageRegions(texts,targetLanguage=="zh"?DeviceTerms(texts,translated):translated,targetLanguage);
        }
        static async Task TranslateIndexedBatch(IList<string> sources,string[] destination,int[] indices,Func<IList<string>,string,CancellationToken,Task<string[]>> translate,SemaphoreSlim slots,CancellationTokenSource stop){
            await slots.WaitAsync(stop.Token);
            try{
                var input=indices.Select(i=>ImageProse(sources[i])).ToArray();
                var output=await translate(input,SourceLanguage(input[0]),stop.Token);stop.Token.ThrowIfCancellationRequested();
                if(output==null||output.Length!=indices.Length||output.Any(string.IsNullOrWhiteSpace))throw new InvalidOperationException("译文不完整，已保留原图，请重试。");
                for(int i=0;i<indices.Length;i++)destination[indices[i]]=output[i];
            }catch{stop.Cancel();throw;}finally{slots.Release();}
        }
        internal static string ImageProse(string source){
            // OCR rows describe screen geometry, not sentence boundaries. Feed
            // a complete paragraph to the provider; retain genuine list items,
            // blank paragraphs and code/table rows. Literal URLs have already
            // been separated locally, and the original OCR text is untouched.
            var lines=source.Replace("\r\n","\n").Replace('\r','\n').Split('\n');
            if(lines.Length==1)return source;
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
            bool excluded=Regex.IsMatch(context,@"\b(poker|cards?|bets?|betting|origami|guns?|rifles?|firearms?|shooting|bullets?|ammunition)\b",options);
            bool camera=Regex.IsMatch(context,@"\b(gimbals?|cameras?)\b",options);
            bool photographicMechanism=Regex.IsMatch(context,@"\bframing\b",options)&&
                Regex.IsMatch(context,@"\b(shots?|shoots?)\b",options)&&
                Regex.IsMatch(context,@"\b(height\s+adjustment|wheel\s+travel)\b",options);
            bool equipment=!excluded&&(Regex.IsMatch(context,@"\b(gimbals?|tripods?|cameras?|hinges?|masts?)\b",options)||photographicMechanism);
            bool photographicShot=(photographicMechanism||(camera&&Regex.IsMatch(context,@"\b(framing|tripods?|gimbals?)\b",options)))&&
                !Regex.IsMatch(context,@"\b(games?|sports?|soccer|football|basketball|hockey|golf|tennis)\b",options);
            Func<string,bool> label=value=>texts.Any(t=>Regex.IsMatch(t.Trim(),@"^"+value+@"[.!:]?$",options));
            int motionLabels=(label("Follow")?1:0)+(label("Parallel")?1:0)+(label("Orbit")?1:0);
            bool motionModes=equipment&&(camera||photographicMechanism)&&motionLabels>=2;
            return translated.Select((text,i)=>{if(!equipment)return text;string source=texts[i];
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
                var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(response);
                object raw,head;var ok=data!=null&&data.TryGetValue("header",out head)?head as Dictionary<string,object>:null;
                if(ok==null||!ok.ContainsKey("ret_code")||Convert.ToString(ok["ret_code"])!="succ"||!data.TryGetValue("auto_translation",out raw))throw new InvalidOperationException("翻译接口返回异常或被限流。截图功能不受影响。");
                var list=raw as IList;
                if(list==null||list.Count!=count||list.Cast<object>().Any(v=>!(v is string)||string.IsNullOrWhiteSpace((string)v)))throw new InvalidOperationException("翻译结果不完整，请稍后重试。");
                return list.Cast<string>().ToArray();
        }
        internal static string RequestJson(IList<string> texts,string sourceLanguage,string targetLanguage){return new JavaScriptSerializer().Serialize(new {header=new {fn="auto_translation",client_key=clientKey},type="plain",model_category="normal",source=new {lang=sourceLanguage,text_list=texts},target=new {lang=TargetLanguage(targetLanguage)}});}
        public static async Task<string[]> TranslateMany(IList<string> texts,CancellationToken cancel,string sourceLanguage="auto",string targetLanguage="zh",string provider=null){
            cancel.ThrowIfCancellationRequested();TargetLanguage(targetLanguage);
            if(texts==null||texts.Count==0||texts.Any(string.IsNullOrWhiteSpace))throw new InvalidOperationException("请先选择要翻译的文字。");
            if(texts.Count>500||texts.Any(t=>t.Length>10000))throw new InvalidOperationException("文字区域太大，请分区域翻译。");
            var result=new List<string>();
            if(ResolveProvider(provider)=="offline")return await OfflineTranslation.Translate(texts,targetLanguage,cancel);
            if(ResolveProvider(provider)=="google"){foreach(var text in texts)result.Add(await GoogleTranslation.Translate(text,targetLanguage,cancel,requests));return result.ToArray();}
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
