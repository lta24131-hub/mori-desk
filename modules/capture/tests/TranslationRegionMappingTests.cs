using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QingJie;

internal static class TranslationRegionMappingTests {
    internal static async Task Run(Action<bool,string> check){
        var sources=new[]{"No AI for now","版本 0.2.0 · 2026年11月1日","The built-in engines still show you everything they find.","[[[0000]]] is literal content","2026-11-02"};
        var sent=new List<string>();
        var output=await Translation.TranslateImageCore(sources,CancellationToken.None,(batch,language,token)=>{
            sent.AddRange(batch);return Task.FromResult(batch.Select(text=>"译文："+text).ToArray());
        });
        check(sent.SequenceEqual(new[]{sources[0],sources[2],sources[3]}),"Chinese labels/dates bypass translation; literal brackets are sent unchanged");
        check(output.SequenceEqual(new[]{"译文："+sources[0],sources[1],"译文："+sources[2],"译文："+sources[3],sources[4]}),"skipped source labels cannot shift the following translated region");
        int active=0,peak=0,calls=0;var groups=new[]{"first English","제품 사용","last English"};
        var ordered=await Translation.TranslateImageCore(groups,CancellationToken.None,async(batch,language,token)=>{
            calls++;active++;peak=Math.Max(peak,active);
            try{await Task.Delay(batch[0]==groups[0]?50:1,token);return batch.Select(t=>t+" 译").ToArray();}finally{active--;}
        });
        check(calls==3&&peak==2&&ordered.SequenceEqual(groups.Select(t=>t+" 译")),"out-of-order language responses retain indexed placement with at most two requests");
        var many=Enumerable.Range(0,85).Select(i=>"entry "+i+new string('a',230)).ToArray();int batches=0;
        var paged=await Translation.TranslateImageCore(many,CancellationToken.None,(batch,language,token)=>{
            batches++;check(batch.Count<=40&&batch.Sum(t=>t.Length)<=9500,"request respects text-count and character budgets");
            return Task.FromResult(batch.Select(t=>t+"译").ToArray());
        });
        check(batches>=3&&paged.SequenceEqual(many.Select(t=>t+"译")),"batch boundaries neither duplicate nor drop dense-page regions");
        var duplicates=await Translation.TranslateImageCore(new[]{"Open","Open"},CancellationToken.None,(batch,language,token)=>Task.FromResult(new[]{"打开一","打开二"}));
        check(duplicates.SequenceEqual(new[]{"打开一","打开二"}),"identical source strings remain separate indexed regions");
        foreach(var malformed in new[]{new string[0],new[]{"ok","extra"},new[]{""},null}){
            bool failed=false;try{await Translation.TranslateImageCore(new[]{"source"},CancellationToken.None,(batch,language,token)=>Task.FromResult(malformed));}catch(InvalidOperationException){failed=true;}
            check(failed,"missing/extra/empty/null response is rejected before rendering");
        }
        using(var cancel=new CancellationTokenSource()){
            bool stopped=false;try{await Translation.TranslateImageCore(new[]{"source"},cancel.Token,(batch,language,token)=>{cancel.Cancel();return Task.FromResult(new[]{"stale"});});}catch(OperationCanceledException){stopped=true;}
            check(stopped,"cancellation after a provider reply never returns stale success");
        }
        calls=0;var preserve=await Translation.TranslateImageCore(new[]{"中文","0.2.0","2026年11月2日"},CancellationToken.None,(batch,language,token)=>{calls++;throw new Exception("should not request");});
        check(calls==0&&preserve.SequenceEqual(new[]{"中文","0.2.0","2026年11月2日"}),"already-Chinese output completes without a network request");
        var brands=new[]{"GITHUB A","GITHUB π","GitLab","OpenAI","macOS","SolidCraft","Discord"};
        var names=await Translation.TranslateImageCore(brands,CancellationToken.None,(batch,language,token)=>{throw new Exception("brand label must not become unrelated content");});
        check(names.SequenceEqual(brands),"standalone platform/brand labels retain their names instead of provider hallucinations");
        check(!Translation.PreserveImageRegion("Open your file in Windows","zh")&&!Translation.PreserveImageRegion("SAVE FILE","zh"),"ordinary instructions remain translatable even when they mention a brand or use capitals");
        sent.Clear();var links=new[]{"Read https://example.com/a?q=1 now.","[issues](https://github.com/tangency-\nlabs/solidcraft/issues) and help", "https://example.com/only"};
        var linked=await Translation.TranslateImageCore(links,CancellationToken.None,(batch,language,token)=>{
            sent.AddRange(batch);return Task.FromResult(batch.Select(t=>t.Replace("Read","阅读").Replace("now.","现在。").Replace("issues","问题").Replace("and help","并帮助")).ToArray());
        });
        check(sent.All(t=>!t.Contains("https://")&&!t.Contains("labs/solidcraft")),"URLs and wrapped URL paths never reach a translation provider");
        check(linked[0]=="阅读 https://example.com/a?q=1 现在。"&&linked[1]=="[问题](https://github.com/tangency-\nlabs/solidcraft/issues) 并帮助"&&linked[2]==links[2],"translated prose retains exact URL bytes, order and surrounding spaces");
        calls=0;var onlyLinks=await Translation.TranslateImageCore(new[]{"https://a.example/path","www.example.com"},CancellationToken.None,(batch,language,token)=>{calls++;throw new Exception("literal links must not request translation");});
        check(calls==0&&onlyLinks[0]=="https://a.example/path","URL-only regions complete without a provider request");
        sent.Clear();var withNames=await Translation.TranslateImageCore(new[]{"Use SolidCraft and Discord for support."},CancellationToken.None,(batch,language,token)=>{
            sent.AddRange(batch);return Task.FromResult(batch.Select(t=>t.Replace("Use","使用").Replace("and","和").Replace("for support.","获取帮助。")).ToArray());
        });
        check(sent.SequenceEqual(new[]{"Use SolidCraft and Discord for support."})&&withNames[0]=="使用 SolidCraft 和 Discord 获取帮助。","inline names retain full sentence context in a single provider request");
        sent.Clear();var natural=await Translation.TranslateImageCore(new[]{"Unlike Discord, GitHub is not where I chat with friends."},CancellationToken.None,(batch,language,token)=>{
            sent.AddRange(batch);return Task.FromResult(new[]{"我在Discord上与朋友聊天，而GitHub并不是聊天的地方。"});
        });
        check(sent.Count==1&&natural[0]=="我在Discord上与朋友聊天，而GitHub并不是聊天的地方。","natural reordered translation with Chinese touching names needs no fallback");
        foreach(var damaged in new[]{"用开放人工智能检查。","用MyOpenAI检查。","用OpenAIs检查。","用OpenAI和OpenAI检查。","用openai检查。"}){
            calls=0;sent.Clear();var repaired=await Translation.TranslateImageCore(new[]{"Use OpenAI to check.","Save the file."},CancellationToken.None,(batch,language,token)=>{
                calls++;sent.AddRange(batch);return Task.FromResult(calls==1?new[]{damaged,"保存文件。"}:batch.Select(t=>t=="Use"?"使用":t=="to check."?"检查。":"意外请求").ToArray());
            });
            check(calls==2&&repaired.SequenceEqual(new[]{"使用 OpenAI 检查。","保存文件。"})&&sent.SequenceEqual(new[]{"Use OpenAI to check.","Save the file.","Use","to check."}),"only the region with renamed, embedded, repeated or recased names falls back");
        }
        calls=0;var repeated=await Translation.TranslateImageCore(new[]{"OpenAI works with OpenAI."},CancellationToken.None,(batch,language,token)=>{
            calls++;return Task.FromResult(calls==1?new[]{"OpenAI合作。"}:new[]{"合作"});
        });
        check(calls==2&&repeated[0]=="OpenAI 合作 OpenAI.","a missing second name is restored without collapsing repeated occurrences");
        calls=0;using(var cancel=new CancellationTokenSource()){
            bool stopped=false;try{await Translation.TranslateImageCore(new[]{"Use OpenAI to check."},cancel.Token,(batch,language,token)=>{calls++;cancel.Cancel();return Task.FromResult(new[]{"损坏名称"});});}catch(OperationCanceledException){stopped=true;}
            check(stopped&&calls==1,"cancellation never starts literal fallback after a damaged provider reply");
        }
        sent.Clear();var wrapped=new[]{"Sketch, constrain and build solids on an editable\nfeature timeline: change a dimension and the model rebuilds.","First paragraph.\n\nSecond paragraph.","- First item\n  continues here.\n- Second item","参数化模型\n可以修改尺寸"};
        await Translation.TranslateImageCore(wrapped,CancellationToken.None,(batch,language,token)=>{sent.AddRange(batch);return Task.FromResult(batch.Select(t=>"译："+t).ToArray());});
        check(sent[0]=="Sketch, constrain and build solids on an editable feature timeline: change a dimension and the model rebuilds.","physical OCR line wraps reach the provider as one complete sentence");
        check(sent[1]==wrapped[1]&&sent[2]=="- First item continues here.\n- Second item","blank paragraphs and separate list entries remain distinct while list continuations join");
        check(wrapped[0].Contains("\n")&&Translation.ImageProse(wrapped[3])=="参数化模型可以修改尺寸","translation normalization leaves the copied OCR text unchanged and joins CJK without an extra space");
        check(Translation.ImageProse("parameter-\ndriven")=="parameter-driven"&&Translation.ImageProse("| first |\n| second |")=="| first |\n| second |","wrapped hyphenated words and table rows retain their intended structure");
    }
}
