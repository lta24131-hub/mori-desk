#if OCR_LEXICON_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using QingJie;

public static class OcrLexiconTests {
    static int checks;
    static readonly List<object> evidence=new List<object>();
    static void Check(bool condition,string message){checks++;if(!condition)throw new Exception(message);}
    static OcrPage Page(string source,double confidence=.75,OcrConfidenceSource origin=OcrConfidenceSource.Model){
        var page=new OcrPage{Text=source};int index=0;
        foreach(Match token in Regex.Matches(source,@"\S+"))page.Words.Add(new WordBox{Text=token.Value,Line=0,TextStart=token.Index,Prefix=index==0?"":" ",Confidence=confidence,ConfidenceSource=origin,Box=new Rect(index++*100,0,90,20)});
        page.TranslationRegions.Add(new TranslationRegion{Text=source,Box=new Rect(0,0,Math.Max(1,index)*100,20)});return page;
    }
    static string Prepare(OcrPage page){return OcrTranslationInput.Prepare(page,ImageTranslation.Regions(page),CancellationToken.None)[0];}
    static bool SameSource(OcrPage page,OcrPage copy){
        return page.Text==copy.Text&&page.Notice==copy.Notice&&page.Words.Count==copy.Words.Count&&page.TranslationRegions.Count==copy.TranslationRegions.Count&&
            page.Words.Zip(copy.Words,(a,b)=>a.Text==b.Text&&a.Box==b.Box&&a.Line==b.Line&&a.TextStart==b.TextStart&&a.Prefix==b.Prefix&&a.Confidence.Equals(b.Confidence)&&a.ConfidenceSource==b.ConfidenceSource).All(s=>s)&&
            page.TranslationRegions.Zip(copy.TranslationRegions,(a,b)=>a.Text==b.Text&&a.Box==b.Box&&a.Lines==b.Lines&&a.SourceLines.Count==b.SourceLines.Count&&a.SourceLines.Zip(b.SourceLines,(x,y)=>x.Text==y.Text&&x.Box==y.Box).All(s=>s)).All(s=>s);
    }
    static void Case(string source,string expected,double score=.75,OcrConfidenceSource origin=OcrConfidenceSource.Model){
        var page=Page(source,score,origin);var copy=OcrResultCache.Copy(page);string result=Prepare(page);
        Check(result==expected,source+" => "+result+" expected "+expected);
        Check(SameSource(page,copy),"source OCR text, word evidence and geometry never changed: "+source);
        evidence.Add(new{source,expected,result,score=double.IsNaN(score)||double.IsInfinity(score)?(object)score.ToString(System.Globalization.CultureInfo.InvariantCulture):score,origin=origin.ToString()});
    }
    static void MainChecks(){
        foreach(var pair in new[]{new[]{"settlngs","settings"},new[]{"translatlon","translation"},new[]{"originaI","original"},new[]{"recognitlon","recognition"},new[]{"cllpboard","clipboard"},new[]{"prevlew","preview"},new[]{"notlfications","notifications"},new[]{"prlvacy","privacy"},new[]{"lnstall","install"},new[]{"dispIay","display"}})Case(pair[0],pair[1]);
        Case("Open the translatlon settlngs","Open the translation settings");
        Case("Al generated art","Al generated art");Case("Al-generated art","Al-generated art");Case("I keep getting Al responses. I asked for cancellation and refund","I keep getting AI responses. I asked for cancellation and refund");
        Case("OpenAl works with ChatGPT","OpenAI works with ChatGPT");
        foreach(double score in new[]{.4,.75,.92,.9570439457893372,.9922722578048706,1}){
            Case("the Al summary arrived at noon.","the AI summary arrived at noon.",score);
            Case("Al responses and Al assistants","AI responses and AI assistants",score);
            Case("OpenAl provides language models; OpenAL is used for audio.","OpenAI provides language models; OpenAL is used for audio.",score);
            Case("OpenAL is used for audio. OpenAl provides language models.","OpenAL is used for audio. OpenAl provides language models.",score);
            Case("OpenAL is used for audio\nOpenAl provides language models","OpenAL is used for audio\nOpenAI provides language models",score);
            Case("Al will check the final draft.\nOpenAl provides language models","Al will check the final draft.\nOpenAI provides language models",score);
        }
        // Adjacent paragraphs do not license a title repair, and an OCR score
        // does not distinguish a person's action from a technology adjective.
        foreach(var text in new[]{"No Al for now\nThe built-in engines still show you everything they find.","Al will check the final draft.","Al took a train to the office.","Al can help summarize a document.","Al generated art","Al assisted the designer","Al powered the engine","Al's summary arrived","Al\u2019s responses","Al summary for aluminum","Al responses in an aluminium alloy","Al summary of oxide layers","Al summary for Al 6061","Al summary for Al2O3","Al summary for Al\u2082O\u2083","Al summary of 99.5% aluminum","Mr. Al summary","Al Summary said hello","OpenAL language models for ChatGPT","OpenAl audio models with GPT","OpenAl Sound with ChatGPT","OpenAl provides language models while OpenAL handles audio","OpenAl helps readers; ChatGPT provides language models","OpenAl has one two three four five six ChatGPT","Al\nresponses"})Case(text,text,.99);
        foreach(double score in new[]{0,.2,.399,1.01,double.PositiveInfinity,double.NegativeInfinity,double.NaN}){
            Case("Al summary","Al summary",score);Case("OpenAl language models","OpenAl language models",score);
        }
        Case("Al took a train to the office; the Al summary arrived at noon.","Al took a train to the office; the AI summary arrived at noon.",.99);
        Case("Al summary for an alloy; the Al summary arrived at noon.","Al summary for an alloy; the AI summary arrived at noon.",.99);
        // Observable code/name boundaries: OCR confidence must not authorize
        // renaming an identifier or changing a quoted literal before translation.
        foreach(var text in new[]{"const OpenAl = ChatGPT;","OpenAl(ChatGPT)","OpenAl [ChatGPT]","settlngs = true","settlngs += 1","settlngs ??= defaults","settlngs()","settlngs [0]","settlngs++","settlngs--","Use \"settlngs\" as the key","Use \"OpenAl\" with ChatGPT","Use \u201COpenAl\u201D with ChatGPT","OpenAl = ChatGPT; translate \"settlngs\""})Case(text,text);
        foreach(var text in new[]{"Use(OpenAl, ChatGPT)","Use(value, OpenAl, ChatGPT)","return OpenAl with ChatGPT","var result = OpenAl with ChatGPT","Use \"OpenAl language models\"","Use 'Al summary'","Use `Al responses`","Use \u201CAl summary\u201D","https://example.com/OpenAl with ChatGPT","C:\\OpenAl\\ChatGPT","OpenAl_name with ChatGPT","lib-OpenAl with ChatGPT","OpenAl.dll with ChatGPT"})Case(text,text,.99);
        foreach(var text in new[]{"Use \u2018Al summary\u2019","Use \u300COpenAl language models\u300D","Use \u300EAl responses\u300F","Use \u2018Al summary and OpenAl language models"})Case(text,text,.99);
        foreach(var text in new[]{"provider . OpenAl + GPT","provider. OpenAl + ChatGPT","provider.\nOpenAl + GPT","provider.\nOpenAl /* GPT */","class OpenAl : ChatGPT {}","namespace OpenAl { class ChatGPT {} }","import OpenAl with ChatGPT","(Al summary)","(OpenAl language models)"})Case(text,text,.99);
        var spacedAssignment="OpenAl"+new string(' ',200)+"= ChatGPT";Case(spacedAssignment,spacedAssignment,.99);
        var spacedClass="class"+new string(' ',200)+"OpenAl : ChatGPT {}";Case(spacedClass,spacedClass,.99);
        Case("Al"+new string(' ',200)+"summary","AI"+new string(' ',200)+"summary",.99);
        Case("Use \"settlngs\" and open translatlon settlngs","Use \"settlngs\" and open translation settings");
        Case("Use \"a \\\"settlngs\\\" key\"; open translatlon settlngs","Use \"a \\\"settlngs\\\" key\"; open translation settings");
        Case("Use \"settlngs and translatlon","Use \"settlngs and translatlon");
        Case("const label = 'open settlngs'; open translatlon settlngs","const label = 'open settlngs'; open translation settings");
        Case("const label = 'don\\'t change settlngs'; open translatlon","const label = 'don\\'t change settlngs'; open translation");
        Case("const label = `open \\` settlngs`; open translatlon settlngs","const label = `open \\` settlngs`; open translation settings");
        Case("don't change Al's translatlon settlngs","don't change Al's translation settings");
        Case("don\u2019t change Al\u2019s translatlon settlngs","don\u2019t change Al\u2019s translation settings");
        Case("After 5' open translatlon settlngs","After 5' open translation settings");
        Case("'settlngs' translatlon","'settlngs' translation");
        var splitCall=Page("OpenAl(ChatGPT)");splitCall.Words.Clear();
        splitCall.Words.Add(new WordBox{Text="OpenAl",Line=0,Confidence=.75,ConfidenceSource=OcrConfidenceSource.Model,Box=new Rect(0,0,40,20)});
        splitCall.Words.Add(new WordBox{Text="ChatGPT",Line=0,Confidence=.75,ConfidenceSource=OcrConfidenceSource.Model,Box=new Rect(45,0,45,20)});
        Check(Prepare(splitCall)==splitCall.Text,"function name keeps its spelling when OCR gives both arguments independent word scores");
        // These are observed OCR outputs from the fixed mixed-size comparison.
        // They are ambiguous names, high-score errors or missing spacing, not
        // permission to add sentence patches or unconstrained spellchecking.
        foreach(var text in new[]{"Al can help summarize a document. Al will check the final draft.","Please keep this page for yourrecords.","Platform 2 | Seat08A | Departs09:15 | Ticet C-2026-1113","会议时间：2026-11-1314:30，地点：三楼会议室。","Bring a notebook, charge the laptop. and save your changes.","上海Shanghai一杭州 Hangzhou |票价：CNY 73.00"})Case(text,text);
        Case("OpenAl provides language models","OpenAI provides language models",.9570439457893372);
        Case("Ticet C-2026-1113","Ticet C-2026-1113",.97523033618927);
        foreach(var text in new[]{"No Al for now","Al said hello","Al's responses","Al Gore","OpenAL AUDIO with ChatGPT","OpenAl Sound with ChatGPT","Al alloy","please ask michaei for the file","Your public image","This is the pubic bone","Settlngs","TRANSLATlON","my-cllpboard","--cllpboard","cllpboard-utils","transIation/assets","C:\\cllpboard\\file.png","https://example.com/cllpboard","cllpboard@example.com","cllpboard_name","settlngs.json","my-Al-generated","lib-OpenAl used with ChatGPT","cllpboard变量","变量cllpboard","Al₂O₃","v1.2.3","2026-11-13","`cllpboard`","cllpboard's","valid OpenAI API CPU GPU"})Case(text,text);
        foreach(double score in new[]{0,.2,.399,.9,.92,1,double.NaN})Case("settlngs","settlngs",score);
        foreach(var origin in new[]{OcrConfidenceSource.Unknown,OcrConfidenceSource.User}){Case("settlngs","settlngs",.75,origin);Case("Al-generated art","Al-generated art",.75,origin);Case("Al summary","Al summary",.99,origin);Case("OpenAl language models","OpenAl language models",.99,origin);}
        Case("settlngs","settings",.4);Case("settlngs","settings",.899);
        var whole=Page("the translatlon settlngs");whole.Words.Clear();whole.Words.Add(new WordBox{Text=whole.Text,Box=whole.TranslationRegions[0].Box,Confidence=.75,ConfidenceSource=OcrConfidenceSource.Model});Check(Prepare(whole)==whole.Text,"line-average confidence is not word evidence");
        var overlap=Page("settlngs");overlap.TranslationRegions.Add(new TranslationRegion{Text="settlngs",Box=overlap.TranslationRegions[0].Box});Check(OcrTranslationInput.Prepare(overlap,ImageTranslation.Regions(overlap),CancellationToken.None).All(s=>s=="settlngs"),"ambiguous ownership does not repair either occurrence");
        var mismatch=Page("settlngs");mismatch.TranslationRegions[0].Text="settlngs extra";Check(Prepare(mismatch)=="settlngs extra","unmatched occurrence alignment is left unchanged");
        var repeated=Page("settlngs settlngs");repeated.Words[1].Confidence=.99;Check(Prepare(repeated)=="settings settlngs","repeated tokens retain individual evidence");
        var identifier=Page("OpenAl-helper for ChatGPT");identifier.Words[0].Text="OpenAl";identifier.Words.Insert(1,new WordBox{Text="helper",Box=new Rect(0,0,90,20),Confidence=.75,ConfidenceSource=OcrConfidenceSource.Model});Check(Prepare(identifier)==identifier.Text,"split OCR words inside a hyphenated identifier retain original spelling");
        var known=Page("settlngs",1);var ready=OcrTranslationInput.PrepareAsync(known,known.TranslationRegions,CancellationToken.None);Check(ready.IsCompleted&&ready.Result[0]==known.Text,"high-confidence text bypasses dictionary work and background scheduling");
        var manual=OcrCorrection.Apply(Page("Al-generated art"),0,"Al-generated art");Check(Prepare(manual)=="Al-generated art","user-confirmed spelling wins over the dictionary");
        manual=OcrCorrection.Apply(Page("Al summary"),0,"Al summary");Check(Prepare(manual)=="Al summary","user-confirmed technology-shaped name is never corrected");
        var shortWhole=Page("Al summary",.99);shortWhole.Words.Clear();shortWhole.Words.Add(new WordBox{Text=shortWhole.Text,Box=shortWhole.TranslationRegions[0].Box,Confidence=.99,ConfidenceSource=OcrConfidenceSource.Model});Check(Prepare(shortWhole)==shortWhole.Text,"high-score line averages are not independent acronym evidence");
        var shortOverlap=Page("Al summary",.99);shortOverlap.TranslationRegions.Add(new TranslationRegion{Text=shortOverlap.Text,Box=shortOverlap.TranslationRegions[0].Box});Check(OcrTranslationInput.Prepare(shortOverlap,shortOverlap.TranslationRegions,CancellationToken.None).All(s=>s==shortOverlap.Text),"high-score overlapping ownership preserves acronyms");
        var shortMismatch=Page("Al summary",.99);shortMismatch.TranslationRegions[0].Text="Al summary extra";Check(Prepare(shortMismatch)=="Al summary extra","high-score acronym occurrence mismatch is preserved");
        using(var stop=new CancellationTokenSource()){stop.Cancel();bool cancelled=false;try{OcrTranslationInput.Prepare(Page("settlngs"),ImageTranslation.Regions(Page("settlngs")),stop.Token);}catch(OperationCanceledException){cancelled=true;}Check(cancelled,"cancel before preparation fails before any provider call");}
        var original=Page("OpenAl works with ChatGPT",.9570439457893372);var input=OcrTranslationInput.Prepare(original,ImageTranslation.Regions(original),CancellationToken.None);int calls=0;
        var result=Translation.TranslateImageCore(input,CancellationToken.None,(batch,language,token)=>{calls++;Check(batch[0]=="OpenAI works with ChatGPT","provider receives the corrected complete sentence");return Task.FromResult(new[]{"OpenAI 与 ChatGPT 配合使用"});}).GetAwaiter().GetResult();
        Check(calls==1&&result[0]=="OpenAI 与 ChatGPT 配合使用"&&original.Text.Contains("OpenAl"),"names validation uses the same corrected copy without source mutation or a redundant request");
        Check(Translation.ImageCacheKey(new[]{"OpenAl"},"zh","google")!=Translation.ImageCacheKey(new[]{"OpenAI"},"zh","google"),"corrected and unchanged spellings cannot share cached results");
        Check(Translation.ImageTranslationInput("Al-generated art")=="Al-generated art","string-only and selected text do not bypass word evidence");
        Check(Translation.ImageTranslationInput("Al summary")=="Al summary","string-only technology compounds do not bypass word evidence");
        var fixturePath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"frozen-short-card.json");
        var fixture=new JavaScriptSerializer().Deserialize<Fixture>(File.ReadAllText(fixturePath));var frozen=new OcrPage{Text=fixture.text};
        foreach(var word in fixture.words)frozen.Words.Add(new WordBox{Text=word.text,Line=word.line,Prefix=word.prefix,Confidence=word.confidence,ConfidenceSource=OcrConfidenceSource.Model,Box=new Rect(word.x,word.y,word.width,word.height)});
        foreach(var region in fixture.paragraphs)frozen.TranslationRegions.Add(new TranslationRegion{Text=region.text,Box=new Rect(region.x,region.y,region.width,region.height)});
        Check(OcrTranslationInput.Prepare(frozen,ImageTranslation.Regions(frozen),CancellationToken.None).SequenceEqual(fixture.paragraphs.Select(r=>r.text)),"real short-card ambiguous AI/Al is not hidden by a sentence patch");
        var mixed=new JavaScriptSerializer().Deserialize<Fixture>(File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"frozen-mixed-terms.json")));
        Check(mixed.words.All(w=>w.confidenceSource=="Model"),"mixed fixture carries actual model provenance for every word");
        var real=new OcrPage{Text=mixed.text};
        foreach(var word in mixed.words)real.Words.Add(new WordBox{Text=word.text,Line=word.line,Prefix=word.prefix,Confidence=word.confidence,ConfidenceSource=OcrConfidenceSource.Model,Box=new Rect(word.x,word.y,word.width,word.height)});
        foreach(var region in mixed.paragraphs)real.TranslationRegions.Add(new TranslationRegion{Text=region.text,Box=new Rect(region.x,region.y,region.width,region.height)});
        var realCopy=OcrResultCache.Copy(real);var realResult=OcrTranslationInput.Prepare(real,real.TranslationRegions,CancellationToken.None);
        Check(realResult.SequenceEqual(new[]{"Al can help summarize a document. Al will check the final draft.\nOpenAI provides language models; OpenAL is used for audio.","Al took a train to the office; the AI summary arrived at noon."}),"real mixed output changes only the two supported technology occurrences, including high-score errors");
        Check(SameSource(real,realCopy),"real frozen words, source scores, positions and raw paragraphs are untouched");
        evidence.Add(new{source=mixed.text,result=string.Join("\n",realResult),fixture="frozen-mixed-terms.json",policy=OcrTranslationInput.Policy});
    }
    public sealed class Fixture {public string text;public Word[] words;public Region[] paragraphs;}
    public sealed class Word {public string text,prefix,confidenceSource;public int line;public double x,y,width,height,confidence;}
    public sealed class Region {public string text;public double x,y,width,height;}
    [STAThread] public static int Main(){
        var report=new Dictionary<string,object>();string root=AppDomain.CurrentDomain.BaseDirectory;
        try{
            if(Environment.UserName!="NookTester"||!root.StartsWith(@"C:\NookLab\Audit\apps\",StringComparison.OrdinalIgnoreCase))throw new Exception("VM only");
            report["policy"]=OcrTranslationInput.Policy;report["startedUtc"]=DateTime.UtcNow.ToString("O");
            var first=Page("Open the translatlon settlngs");GC.Collect();long before=GC.GetTotalMemory(true);var timer=Stopwatch.StartNew();string prepared=Prepare(first);timer.Stop();report["firstLoadMs"]=timer.Elapsed.TotalMilliseconds;report["retainedManagedBytes"]=GC.GetTotalMemory(true)-before;Check(prepared=="Open the translation settings","cold dictionary pass corrects known UI words");
            MainChecks();
            timer.Restart();for(int i=0;i<1000;i++)Prepare(first);timer.Stop();report["warmPerSmallRegionMs"]=timer.Elapsed.TotalMilliseconds/1000;
            var large=new OcrPage();for(int i=0;i<100;i++){var r=new TranslationRegion{Text="Open the translatlon settlngs",Box=new Rect(0,i*30,400,20)};large.TranslationRegions.Add(r);var row=Page(r.Text);foreach(var w in row.Words){w.Box=new Rect(w.Box.X,i*30,w.Box.Width,20);w.Line=i;large.Words.Add(w);}}
            timer.Restart();var output=OcrTranslationInput.Prepare(large,large.TranslationRegions,CancellationToken.None);timer.Stop();report["hundredRegions400WordsMs"]=timer.Elapsed.TotalMilliseconds;Check(output.All(s=>s=="Open the translation settings"),"dense 100-region page retains region count and correction identity");
            foreach(var word in large.Words)word.Confidence=.99;timer.Restart();OcrTranslationInput.Prepare(large,large.TranslationRegions,CancellationToken.None);timer.Stop();report["highConfidenceBypassMs"]=timer.Elapsed.TotalMilliseconds;
            var unbroken=Page(new string('x',9000));timer.Restart();Check(Prepare(unbroken)==unbroken.Text,"long unknown token is preserved without a spelling scan");timer.Stop();report["unbroken9000CharactersMs"]=timer.Elapsed.TotalMilliseconds;
            var quoted=Page("\""+string.Concat(Enumerable.Repeat("\\\" ",3000))+"settlngs");timer.Restart();Check(Prepare(quoted)==quoted.Text,"long incomplete literal with escaped quotes is preserved");timer.Stop();report["escapedQuote9000CharactersMs"]=timer.Elapsed.TotalMilliseconds;
            var localTimings=new List<object>();
            foreach(int count in new[]{128,512}){
                string repeated=string.Concat(Enumerable.Repeat("Al summary and ",count));var many=Page(repeated,.99);var beforeMany=OcrResultCache.Copy(many);
                timer.Restart();string preparedMany=Prepare(many);timer.Stop();
                Check(preparedMany==string.Concat(Enumerable.Repeat("AI summary and ",count)),"long unpunctuated clause preserves every occurrence-local correction: "+count);
                Check(SameSource(many,beforeMany),"long clause never changes raw OCR or geometry: "+count);
                localTimings.Add(new{occurrences=count,characters=repeated.Length,words=many.Words.Count,elapsedMs=timer.Elapsed.TotalMilliseconds});
            }
            report["boundedTechnologyContextTimings"]=localTimings;
            report["passed"]=true;return 0;
        }catch(Exception ex){report["passed"]=false;report["error"]=ex.ToString();return 1;}
        finally{report["checks"]=checks;report["cases"]=evidence;File.WriteAllText(Path.Combine(root,"lexicon-tests.json"),new JavaScriptSerializer().Serialize(report));}
    }
}
#endif
