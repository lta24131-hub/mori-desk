#if TRANSLATION_CONTEXT_TESTS
// Isolated deterministic tests of production Translation.cs and
// TranslationCache.cs. No application, windows, network, images or models.
// Run only through tests/run-translation-context.ps1; normal capture builds
// must not include the dependency adapters below.
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using QingJie;

internal static class TranslationContextTests {
    static int checks;
    static void Check(bool condition,string message){checks++;if(!condition)throw new InvalidOperationException(message);Console.WriteLine("PASS "+message);}
    static string[] Terms(string[] source,string[] translated){return Translation.DeviceTerms(source,translated);}
    static void Unchanged(string[] source,string[] translated,string message){Check(Terms(source,translated).SequenceEqual(translated),message);}
    static string Response(string[] values){return string.Join("\n\n",values.Select((value,i)=>"[[["+i.ToString("D4")+"]]]\n"+value));}
    static string[] Core(string[] source,string[] translated,Func<string,string,CancellationToken,Task<string>> retry,string target="zh",CancellationToken cancel=default(CancellationToken)){
        return Translation.TranslateImageCore(source,cancel,(request,token)=>Task.FromResult(Response(translated)),target,retry).GetAwaiter().GetResult();
    }
    static Task<string> Failed(Exception error){var result=new TaskCompletionSource<string>();result.SetException(error);return result.Task;}
    static void RetryTests(){
        Check(Translation.CreateImageEchoRetry("tencent","zh")!=null&&Translation.CreateImageEchoRetry("google","zh")==null&&Translation.CreateImageEchoRetry("offline","zh")==null&&Translation.CreateImageEchoRetry("tencent","en")==null,"production retry factory is Tencent-to-Chinese only without switching or repeating unsupported providers");
        foreach(var phrase in new[]{"Choose your path","Please don't move.","Please don\u2019t move.","This is your view."})Check(Translation.IsUntranslatedEnglishPhrase(phrase,phrase),"English retry candidate includes grammar and contractions: "+phrase);
        Check(Translation.IsUntranslatedEnglishPhrase("Choose your path."," choose YOUR path! "),"only whitespace case and terminal punctuation differences still count as untranslated");
        foreach(var phrase in new[]{"Follow","Orbit","Nook","New York","OpenAI ChatGPT","Bonjour le monde","Elige tu camino","Choisissez votre chemin","Please visit caf\u00e9","\u8bf7\u9009\u62e9\u8def\u5f84","\uc81c\ud488 \uc0ac\uc6a9","https://example.com/the/path","C:\\the\\path.png","C:/your/path.png","./the/file.png","your picture.png","your.name@example.com","www.example.com/the/path"})Check(!Translation.IsUntranslatedEnglishPhrase(phrase,phrase),"single labels names non-English scripts accented Latin URLs email and file paths are not forced to English: "+phrase);
        Check(!Translation.IsUntranslatedEnglishPhrase("Choose your path","\u9009\u62e9\u4f60\u7684\u8def\u5f84")&&!Translation.IsUntranslatedEnglishPhrase("Choose your path",""),"already translated or blank results are not classified as echoed English");
        int calls=0,active=0,peak=0;var gate=new object();var languages=new System.Collections.Generic.List<string>();var queries=new System.Collections.Generic.List<string>();
        var source=new[]{"Choose your path","Please open your camera","This is your frame","Follow","Keep this file"};
        var originalSource=(string[])source.Clone();
        var initial=new[]{source[0],source[1],source[2],source[3],"\u4fdd\u7559\u6b64\u6587\u4ef6"};
        var result=Core(source,initial,async(text,language,token)=>{lock(gate){calls++;active++;peak=Math.Max(peak,active);languages.Add(language);queries.Add(text);}try{await Task.Delay(20,token);return text==source[0]+"."?"\u9009\u62e9\u4f60\u7684\u8def\u5f84":"\u8bf7\u6253\u5f00\u76f8\u673a";}finally{lock(gate)active--;}});
        Check(calls==2&&peak==2&&languages.All(language=>language=="en"),"real image-core retry route is capped at two concurrent forced-English requests");
        Check(queries.SequenceEqual(new[]{source[0]+".",source[1]+"."}),"only the provider retry payload gains a terminal period for echoed unpunctuated phrases");
        Check(result.SequenceEqual(new[]{"\u9009\u62e9\u4f60\u7684\u8def\u5f84","\u8bf7\u6253\u5f00\u76f8\u673a",source[2],"Follow","\u4fdd\u7559\u6b64\u6587\u4ef6"}),"only bounded failed phrases change; success single labels ordering and excess failures survive");
        Check(initial.SequenceEqual(new[]{source[0],source[1],source[2],source[3],"\u4fdd\u7559\u6b64\u6587\u4ef6"}),"retry never mutates first-pass response storage");
        Check(source.SequenceEqual(originalSource),"provider payload normalization never changes original source or OCR region strings");
        foreach(var phrase in new[]{"Please don't move","Please don\u2019t move","Please don't move.","Please don't move!","Please don't move?"}){
            string payload=null;var normalized=Core(new[]{phrase},new[]{phrase},(text,language,token)=>{payload=text;return Task.FromResult("\u8bf7\u4e0d\u8981\u79fb\u52a8\u3002");});
            string expected=phrase.EndsWith(".",StringComparison.Ordinal)||phrase.EndsWith("!",StringComparison.Ordinal)||phrase.EndsWith("?",StringComparison.Ordinal)?phrase:phrase+".";
            Check(payload==expected&&normalized[0]=="\u8bf7\u4e0d\u8981\u79fb\u52a8\u3002","retry payload preserves contractions and any existing sentence punctuation: "+phrase);
        }
        string trimmedPayload=null;Core(new[]{"Choose your path  "},new[]{"Choose your path"},(text,language,token)=>{trimmedPayload=text;return Task.FromResult("\u9009\u62e9\u8def\u5f84\u3002");});
        Check(trimmedPayload=="Choose your path.","terminal punctuation is added after trimming only the provider payload trailing whitespace");
        calls=0;var english=Core(new[]{"Choose your path"},new[]{"Choose your path"},(text,language,token)=>{calls++;return Task.FromResult("\u4e0d\u5e94\u4f7f\u7528");},"en");
        Check(calls==0&&english[0]=="Choose your path","English target never enters Chinese echo retry");
        foreach(var retryValue in new[]{"Choose your path",""," ","[[[0000]]]\n\u9009\u62e9\u8def\u5f84",new string('\u4e2d',257)}){
            calls=0;var unchanged=Core(new[]{"Choose your path"},new[]{"Choose your path"},(text,language,token)=>{calls++;return Task.FromResult(retryValue);});
            Check(calls==1&&unchanged[0]=="Choose your path","echo empty marker-contaminated or excessive retry is not accepted: "+retryValue.Length);
        }
        foreach(var error in new Exception[]{new InvalidOperationException("provider unavailable"),new System.Net.Http.HttpRequestException("network failed"),new OperationCanceledException("retry deadline")}){
            var partial=Core(new[]{"Choose your path","Please keep your file"},new[]{"Choose your path","Please keep your file"},(text,language,token)=>text.StartsWith("Choose",StringComparison.Ordinal)?Failed(error):Task.FromResult("\u8bf7\u4fdd\u7559\u6587\u4ef6"));
            Check(partial.SequenceEqual(new[]{"Choose your path","\u8bf7\u4fdd\u7559\u6587\u4ef6"}),"recoverable retry failure preserves first pass and independent retry success: "+error.GetType().Name);
        }
        using(var stop=new CancellationTokenSource()){
            bool cancelled=false;try{Core(new[]{"Choose your path"},new[]{"Choose your path"},(text,language,token)=>{stop.Cancel();token.ThrowIfCancellationRequested();return Task.FromResult("\u9009\u62e9");},"zh",stop.Token);}catch(OperationCanceledException){cancelled=true;}
            Check(cancelled,"caller cancellation escapes retry rather than reporting stale success");
        }
        var elapsed=System.Diagnostics.Stopwatch.StartNew();var tokens=new System.Collections.Generic.List<CancellationToken>();
        var timeout=Core(new[]{"Choose your path","Please keep your file"},new[]{"Choose your path","Please keep your file"},async(text,language,token)=>{lock(tokens)tokens.Add(token);await Task.Delay(Timeout.Infinite,token);return "\u4e0d\u53ef\u5230\u8fbe";});
        Check(tokens.Count==2&&tokens[0]==tokens[1]&&tokens[0].IsCancellationRequested,"both retry requests share one actual cancellable four-second deadline");
        Check(elapsed.Elapsed.TotalSeconds<6&&timeout.SequenceEqual(new[]{"Choose your path","Please keep your file"}),"total retry timeout is one budget rather than two sequential waits and keeps echoed originals");
        calls=0;var withNoRetry=Core(new[]{"Nook","Choose your path"},new[]{"Nook","\u9009\u62e9\u8def\u5f84"},(text,language,token)=>{calls++;return Task.FromResult("\u9519\u8bef");});
        Check(calls==0&&withNoRetry.SequenceEqual(new[]{"Nook","\u9009\u62e9\u8def\u5f84"}),"successful ordinary translations add no retry request");
        Check(!Translation.CanCacheImageTranslation(new[]{"Choose your path"},new[]{"Choose your path"},"zh"),"an unresolved echoed English phrase cannot block immediate retry through the image cache");
        Check(!Translation.CanCacheImageTranslation(new[]{"Choose your path","Please keep your file"},new[]{"\u9009\u62e9\u8def\u5f84","Please keep your file"},"zh"),"a partially successful image with one unresolved echo is not cached as complete");
        Check(Translation.CanCacheImageTranslation(new[]{"Nook","Choose your path"},new[]{"Nook","\u9009\u62e9\u8def\u5f84"},"zh"),"complete translations and intentionally unchanged one-word names remain cacheable");
        Check(Translation.CanCacheImageTranslation(new[]{"Choose your path"},new[]{"Choose your path"},"en"),"English output deliberately equal to English source remains cacheable");
        Check(!Translation.CanCacheImageTranslation(new[]{"Choose your path"},new string[0],"zh")&&!Translation.CanCacheImageTranslation(new[]{"Choose your path"},new[]{""},"zh"),"incomplete or blank image responses are never cache-eligible");
    }
    static void MainTests(){
        var source=new[]{"Made for your next shot.","Independent\nframing.","Choose your path.","Stay with the action.","Fold for the\nnext shoot.","Follow","Parallel","Orbit","Your height.\nYour shot.","Manual height adjustment","Independent\nwheel travel."};
        var output=new[]{"为你的下一个镜头做准备。","独立\n框定。","选择路径。","继续行动。","盖牌\n下一次射击。","遵循","平行","轨道","你的身高。\n该你了。","手动高度调节","独立轮行程。"};
        var result=Terms(source,output);
        Check(result.Length==source.Length,"card context keeps exact result count");
        Check(result[4]=="折叠\n下一次拍摄。","multiple independent card clues fix Fold and shoot without rewriting the advertisement");
        Check(result[1]=="独立\n构图。","photography framing correction is tied to its own source term");
        Check(result[5]=="跟随"&&result[6]=="平行"&&result[7]=="环绕","standalone camera-motion labels get domain-specific meanings");
        Check(result[8]=="你的身高。\n你的镜头。","multiple photography clues correct a standalone your-shot game sense without replacing its other line");
        Check(result[0]==output[0]&&result[3]==output[3]&&result[10]==output[10],"unrelated advertising wording and wheel translation are not replaced by canned copy");
        Check(output[4]=="盖牌\n下一次射击。","term policy does not mutate provider array");
        Check(Terms(new[]{"gimbal camera mast body Fold next shoot"},new[]{"万向节 桅杆 身体 弃牌 下一次射击"})[0]=="云台 立柱 机身 折叠 下一次拍摄","legacy explicit camera context retains all original device terms");
        Check(Terms(new[]{"A camera", "Fold"},new[]{"相机","盖牌"})[1]=="折叠","new Fold spelling also corrected in old camera context");
        Check(Terms(new[]{"framing", "next shot", "wheel\ntravel", "Fold"},new[]{"构图","下个镜头","车轮行程","弃牌"})[3]=="折叠","independent motion clue can span recognized source lines");
        Unchanged(new[]{"one shot", "Fold"},new[]{"一次机会","盖牌"},"single shot does not activate photography glossary");
        Unchanged(new[]{"framing", "Fold"},new[]{"框定","弃牌"},"framing without other independent clues does not activate glossary");
        Unchanged(new[]{"framing", "shot", "Fold"},new[]{"框定","镜头","盖牌"},"framing and shot without mechanism remain ambiguous");
        Unchanged(new[]{"height adjustment", "shot", "Fold"},new[]{"高度调节","机会","盖牌"},"height plus shot without framing remain ambiguous");
        foreach(var excluded in new[]{"poker","cards","card","bet","bets","betting","origami","gun","guns","rifle","rifles","firearm","firearms","shooting","bullet","bullets","ammunition"}){
            Unchanged(new[]{"camera framing next shot wheel travel",excluded,"Fold next shoot","Follow","Orbit"},new[]{"相机","排除领域","盖牌 下一次射击","遵循","轨道"},"explicit competing domain blocks corrections: "+excluded);
        }
        Unchanged(new[]{"Fold your poker cards."},new[]{"弃牌。"},"ordinary poker meaning remains unchanged");
        Unchanged(new[]{"Please follow the instructions for your camera.","The satellite will orbit Earth."},new[]{"请遵循相机说明。","卫星绕地球轨道运行。"},"ordinary Follow and Orbit sentences keep their legitimate meaning");
        Unchanged(new[]{"camera", "Follow"},new[]{"相机","遵循"},"a single standalone label is insufficient for a mode menu");
        Unchanged(new[]{"Follow","Parallel","Orbit"},new[]{"遵循","平行","轨道"},"mode words without camera or independent photographic mechanism stay unchanged");
        Unchanged(new[]{"hinge","Follow","Orbit"},new[]{"铰链","遵循","轨道"},"generic equipment alone cannot establish camera motion modes");
        var labels=Terms(new[]{"GIMBAL", " Follow. ", "Orbit:"},new[]{"云台","  遵循模式。 ","轨道模式！"});
        Check(labels[1]=="  跟随模式。 "&&labels[2]=="环绕模式！","mode label correction preserves whitespace punctuation and optional mode suffix");
        Unchanged(new[]{"camera", "Follow the route", "Orbit Mars"},new[]{"相机","遵循路线","火星轨道"},"non-label phrases are never rewritten as exact camera modes");
        Unchanged(new[]{"camera", "Follow","Orbit"},new[]{"相机","遵循你选择的规则","轨道数据"},"longer translated sentences are not blindly rewritten by label mapping");
        Unchanged(new[]{"camera", "Your shot."},new[]{"相机","该你了。"},"camera alone does not establish a standalone shot photography correction");
        Unchanged(new[]{"camera framing", "Your shot is next."},new[]{"相机构图","轮到你了。"},"ordinary shot sentences are not treated as a standalone photography slogan");
        Unchanged(new[]{"camera framing", "poker", "Your shot."},new[]{"相机构图","扑克","该你了。"},"competing game domain blocks the shot correction even with camera clues");
        var shot=Terms(new[]{"camera framing", "Your shot."},new[]{"相机构图"," 轮到你了！"});
        Check(shot[1]==" 你的镜头！","narrow photography shot correction preserves punctuation and whitespace");
        Unchanged(new[]{"camera framing", "Your shot."},new[]{"相机构图","这是你的机会和你的选择。"},"longer legitimate Chinese sentence is never broadly replaced by shot policy");
        foreach(var intentional in new[]{"你的机会。","你的射门。"})Unchanged(new[]{"camera framing", "Your shot."},new[]{"相机构图",intentional},"potential advertising pun or sports meaning is not silently rewritten: "+intentional);
        foreach(var domain in new[]{"game","games","sport","sports","soccer","football","basketball","hockey","golf","tennis"})Unchanged(new[]{"camera framing",domain,"Your shot."},new[]{"相机构图",domain,"该你了。"},"competing sports or game domain blocks shot-only correction: "+domain);
        var targetEnglish=Translation.TranslateImageCore(new[]{"camera gimbal Fold next shoot","Follow","Orbit"},CancellationToken.None,(request,token)=>Task.FromResult("[[[0000]]]\ngimbal Fold shot\n\n[[[0001]]]\nFollow\n\n[[[0002]]]\nOrbit"),"en").GetAwaiter().GetResult();
        Check(targetEnglish.SequenceEqual(new[]{"gimbal Fold shot","Follow","Orbit"}),"English target bypasses all Chinese device corrections in the real core pipeline");
        var targetChinese=Translation.TranslateImageCore(new[]{"camera Fold next shoot","Follow","Orbit"},CancellationToken.None,(request,token)=>Task.FromResult("[[[0000]]]\n盖牌 下一次射击\n\n[[[0001]]]\n遵循\n\n[[[0002]]]\n轨道"),"zh").GetAwaiter().GetResult();
        Check(targetChinese.SequenceEqual(new[]{"折叠 下一次拍摄","跟随","环绕"}),"Chinese target applies policy only after complete marker-aligned injected response");
        var serializer=new JavaScriptSerializer();
        string oldKey="image:device-context-v2:tencent:zh:"+serializer.Serialize(source);
        string newKey=Translation.ImageCacheKey(source,"zh","tencent");
        Check(newKey.StartsWith("image:device-context-v3:",StringComparison.Ordinal)&&newKey!=oldKey,"image cache identity changes for bounded retry and revised photography policy");
        var cache=new TranslationCache();cache.Put(oldKey,output);
        Check(cache.Get(newKey)==null,"old wrong cached translation cannot satisfy the new policy key");
        cache.Put(newKey,result);Check(cache.Get(newKey).SequenceEqual(result),"new-policy result remains cacheable");
        Check(Translation.ImageCacheKey(source,"en","tencent")!=newKey&&Translation.ImageCacheKey(source,"zh","google")!=newKey,"image cache still isolates target languages and providers");
        Check(Translation.TextCacheKey("hello","zh","tencent")=="text:tencent:zh:auto:hello","ordinary text-translation cache identity is unchanged");
        RetryTests();
        var lazyClient=typeof(Translation).GetField("client",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).GetValue(null);
        Check(!(bool)lazyClient.GetType().GetProperty("IsValueCreated").GetValue(lazyClient,null),"the production HTTP client was never constructed during these checks");
    }
    static int Main(){try{MainTests();Console.WriteLine("PASS "+checks+" isolated translation-context checks; no network, UI or models");return 0;}catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}

// Compile-time adapters for dependencies not used by these tests. Every access
// that could touch app state, offline packs or another provider fails closed.
namespace QingJie {
    public sealed class RegressionSettings {public string TranslationProvider{get{throw new InvalidOperationException("Real app settings are forbidden in this runner.");}}}
    public static class AppState {public static RegressionSettings Settings{get{throw new InvalidOperationException("Real app state is forbidden in this runner.");}}}
    public static class OfflinePacks {public static string CacheIdentity{get{throw new InvalidOperationException("Offline packs are forbidden in this runner.");}}}
    public static class OfflineTranslation {public static Task<string[]> Translate(System.Collections.Generic.IList<string> texts,string target,CancellationToken token){throw new InvalidOperationException("Offline inference is forbidden in this runner.");}}
    public static class GoogleTranslation {public static Task<string> Translate(string text,string target,CancellationToken token,SemaphoreSlim slots){throw new InvalidOperationException("Google requests are forbidden in this runner.");}}
    public static class Geometry {public static bool IsHangul(char c){return c>=0xAC00&&c<=0xD7AF;}}
    public static class OcrLanguagePacks {public static bool IsScript(char c,string language){return language=="jpn"?(c>=0x3040&&c<=0x30FF):language=="rus"?(c>=0x0400&&c<=0x04FF):language=="ara"?(c>=0x0600&&c<=0x06FF):language=="tha"?(c>=0x0E00&&c<=0x0E7F):false;}}
}
#endif
