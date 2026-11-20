using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace QingJie {
    // A translation-only copy. Never edit OCR text, geometry, clipboard text or cache.
    internal static class OcrTranslationInput {
        internal const string Policy="symspell-6.7.3-ocr-evidence-v3";
        static readonly Regex tokens=new Regex(@"[A-Za-z0-9]+",RegexOptions.CultureInvariant);
        static bool LiteralToken(Match part){
            // Scan each whitespace-delimited token once. Unanchored \S*@ / \S*/
            // patterns backtrack quadratically on long URLs or unbroken OCR text.
            string value=part.Value;
            return value.IndexOfAny(new[]{'@','/','\\','_'})>=0||Regex.IsMatch(value,@"\.[A-Za-z0-9]");
        }
        sealed class ProtectedRange {internal int Start,End;}
        static IEnumerable<ProtectedRange> QuotedRanges(string text){
            // One pass also handles escaped quotes and incomplete OCR strings
            // without retrying a regex from every quote in a long code fragment.
            for(int i=0;i<text.Length;i++){
                char opening=text[i];
                if(opening!='`'&&opening!='"'&&opening!='\''&&opening!='\u201C'&&opening!='\u2018'&&opening!='\u300C'&&opening!='\u300E')continue;
                // Contractions, possessives and unit marks are not open strings.
                if(opening=='\''&&i>0&&(char.IsLetterOrDigit(text[i-1])||text[i-1]=='_'))continue;
                int start=i;char closing=opening=='\u201C'?'\u201D':opening=='\u2018'?'\u2019':opening=='\u300C'?'\u300D':opening=='\u300E'?'\u300F':opening;
                for(i++;i<text.Length;i++){
                    if((opening=='"'||opening=='\''||opening=='`')&&text[i]=='\\'&&i+1<text.Length){i++;continue;}
                    if(text[i]==closing)break;
                }
                // An unfinished literal is safer left verbatim to the region end.
                yield return new ProtectedRange{Start=start,End=Math.Min(text.Length,i+1)};
            }
        }
        const string ReviewedUiTerms="settings language translation translate translated original image images text recognition recognize clipboard download upload file files folder folders browser screenshot screenshots preview select selected selection copy paste save cancel close open update install remove search display window toolbar privacy password account notification notifications appearance preferences connection advanced general enabled disabled";
        static readonly HashSet<string> uiWords=new HashSet<string>(ReviewedUiTerms.Split(' '),StringComparer.Ordinal);
        static readonly HashSet<string> visualSpellings=VisualSpellings();
        static HashSet<string> VisualSpellings(){
            var spellings=new HashSet<string>(StringComparer.Ordinal);
            foreach(string term in uiWords){
                if(term.Length<5||term.Length>24)continue;
                var chars=term.ToCharArray();
                for(int i=0;i<chars.Length;i++){
                    if(term[i]!='l'&&term[i]!='i')continue;
                    foreach(char c in "liI"){
                        chars[i]=c;string source=new string(chars);
                        if(VisualConfusion(source,term))spellings.Add(source);
                    }
                    chars[i]=term[i];
                }
            }
            return spellings;
        }
        sealed class Lexicon {
            internal readonly HashSet<string> Known=new HashSet<string>(StringComparer.Ordinal);
            internal readonly SymSpell Candidates=new SymSpell(128,1,7);
            internal Lexicon(){
                using(var stream=typeof(OcrTranslationInput).Assembly.GetManifestResourceStream("QingJie.EnglishFrequency.txt")){
                    if(stream==null)throw new InvalidDataException("Missing OCR dictionary.");
                    using(var reader=new StreamReader(stream))for(string line;(line=reader.ReadLine())!=null;){
                        int split=line.LastIndexOf(' ');long count;
                        if(split<=0||!long.TryParse(line.Substring(split+1),NumberStyles.Integer,CultureInfo.InvariantCulture,out count))continue;
                        string word=line.Substring(0,split);Known.Add(word);
                        // Frequency cannot distinguish names (e.g. Michael) from
                        // prose. This is the exact upstream projection needed for
                        // candidate generation and the known-real-word veto.
                        if(word.Length>=5&&word.Length<=24&&uiWords.Contains(word))Candidates.CreateDictionaryEntry(word,count);
                    }
                }
            }
        }
        static readonly Lazy<Lexicon> lexicon=new Lazy<Lexicon>(()=>new Lexicon());
        static bool ModelWord(WordBox word){return word.ConfidenceSource==OcrConfidenceSource.Model&&word.Confidence>=.4&&word.Confidence<=1;}
        static bool TechnologyToken(string text){return text=="Al"||text=="OpenAl";}
        static bool NeedsAnalysis(WordBox word){
            // OCR can be very confident about the wrong I/l glyph. Only these
            // exact candidates may use local semantic evidence above the usual
            // spelling threshold; confidence alone never authorizes a change.
            if(!ModelWord(word))return false;
            string value=word.Text;if(string.IsNullOrEmpty(value))return false;
            // The evidence pass only accepts a box with one ASCII-alphanumeric
            // token. Punctuation is allowed; a score shared by two tokens isn't.
            int first=-1,end=0;
            for(int i=0;i<value.Length;i++){
                char c=value[i];if(!((c>='A'&&c<='Z')||(c>='a'&&c<='z')||(c>='0'&&c<='9')))continue;
                if(first<0)first=i;else if(end!=i)return false;
                end=i+1;
            }
            if(first<0||end-first>24)return false;
            string token=first==0&&end==value.Length?value:value.Substring(first,end-first);
            return TechnologyToken(token)||(word.Confidence<.90&&CouldCorrectSpelling(token));
        }
        internal static Task<string[]> PrepareAsync(OcrPage page,IList<TranslationRegion> regions,CancellationToken cancel){
            if(page==null||regions==null)throw new ArgumentNullException();
            cancel.ThrowIfCancellationRequested();
            if(!page.Words.Any(NeedsAnalysis))return Task.FromResult(regions.Select(r=>r.Text).ToArray());
            return Task.Run(()=>Prepare(page,regions,cancel),cancel);
        }
        // Only the small ambiguous model label needs a second reading. The rest
        // of the page, including tiny footnotes, always uses the primary OCR.
        internal static Task<string[]> PrepareImageAsync(BitmapSource source,OcrPage page,IList<TranslationRegion> regions,CancellationToken cancel){
            return PrepareImageWithEvidenceAsync(source,page,regions,cancel,ConfirmModelLabel);
        }
        internal static async Task<string[]> PrepareImageWithEvidenceAsync(BitmapSource source,OcrPage page,IList<TranslationRegion> regions,CancellationToken cancel,Func<BitmapSource,TranslationRegion,WordBox[],string,CancellationToken,Task<bool>> verify){
            var values=await PrepareAsync(page,regions,cancel).ConfigureAwait(false);
            if(source==null||!source.IsFrozen)return values;
            int index=-1;Match match=null;
            for(int i=0;i<values.Length;i++){
                if(values[i].IndexOf("Al model ",StringComparison.Ordinal)<0)continue;
                var header=Translation.ModelLabelHeader.Match(values[i]);
                if(!header.Success||header.Groups["tag"].Value!="Al"||!Regex.IsMatch(values[i],@"\b(?:files?|documents?|prompts?|chatbots?|inference|training|tokens?)\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)||
                   Regex.IsMatch(values[i],@"\b(?:alloy|alumin(?:ium|um)|oxide|metal|surname|Gore|Pacino|Smith)\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant))continue;
                if(index>=0)return values;index=i;match=header;
            }
            if(index<0)return values;
            var region=regions[index];var words=page.Words.Where(w=>OcrCorrection.Contains(region,w)).Take(3).ToArray();string number=match.Groups["number"].Value;
            if(words.Length!=3||words.Any(w=>!ModelWord(w)||w.Line!=words[0].Line)||words[0].Text!="Al"||words[1].Text!="model"||
                !Regex.IsMatch(words[2].Text??"", "^"+number+@"(?:[:：])?$")||regions.Count(r=>OcrCorrection.Contains(r,words[0]))!=1)return values;
            if(await verify(source,region,words,number,cancel).ConfigureAwait(false)){
                int pos=match.Groups["tag"].Index;values[index]=values[index].Remove(pos,2).Insert(pos,"AI");
            }
            cancel.ThrowIfCancellationRequested();return values;
        }
        static readonly SemaphoreSlim verificationSlot=new SemaphoreSlim(1,1);
        static async Task<bool> ConfirmModelLabel(BitmapSource image,TranslationRegion region,WordBox[] words,string number,CancellationToken cancel){
            cancel.ThrowIfCancellationRequested();if(!verificationSlot.Wait(0))return false;
            var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancel);var token=lifetime.Token;
            // A first-use OS recognizer can exceed two seconds on the software
            // VM. Only this already-qualified ambiguous header pays this budget.
            const int budget=3000;lifetime.CancelAfter(budget);
            var work=Task.Run(async()=>{
                try{
                    token.ThrowIfCancellationRequested();int x=0,y=0,width=image.PixelWidth,height=image.PixelHeight;
                    if(width>640||height>200){
                        var b=region.Box;if(b.IsEmpty||!(b.Width<=600&&b.Height<=160))return false;
                        x=Math.Max(0,(int)Math.Floor(b.Left)-12);y=Math.Max(0,(int)Math.Floor(b.Top)-12);
                        width=Math.Min(image.PixelWidth,(int)Math.Ceiling(b.Right)+12)-x;height=Math.Min(image.PixelHeight,(int)Math.Ceiling(b.Bottom)+12)-y;
                    }
                    if(width<=0||height<=0||width>640||height>200)return false;
                    var installed=Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages;
                    var preferred=installed.Where(l=>l.LanguageTag.StartsWith("zh",StringComparison.OrdinalIgnoreCase)).Concat(installed.Where(l=>l.LanguageTag.StartsWith("en",StringComparison.OrdinalIgnoreCase)));
                    var engine=preferred.Select(l=>Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(l)).FirstOrDefault(e=>e!=null);
                    if(engine==null||Math.Max(width,height)*3>Windows.Media.Ocr.OcrEngine.MaxImageDimension)return false;
                    BitmapSource slice=image;if(x!=0||y!=0||width!=image.PixelWidth||height!=image.PixelHeight){var crop=new CroppedBitmap(image,new Int32Rect(x,y,width,height));crop.Freeze();slice=crop;}
                    token.ThrowIfCancellationRequested();var pix=new OcrPixels(OcrService.Prepare(slice,3));
                    using(var bitmap=pix.WindowsBitmap()){
                        var op=engine.RecognizeAsync(bitmap);var reply=new TaskCompletionSource<Windows.Media.Ocr.OcrResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                        op.Completed=(a,s)=>{try{if(s==Windows.Foundation.AsyncStatus.Canceled)reply.TrySetCanceled();else reply.TrySetResult(a.GetResults());}catch(Exception e){reply.TrySetException(e);}};
                        // Some OS versions complete/dispose the operation while
                        // a timeout callback is being dispatched. Cancellation
                        // is best effort; never throw out of a timer callback.
                        using(token.Register(()=>{try{op.Cancel();}catch(Exception){}})){
                            var result=await reply.Task.ConfigureAwait(false);token.ThrowIfCancellationRequested();
                            foreach(var line in result.Lines)for(int i=0;i+2<line.Words.Count;i++){
                                if(line.Words[i].Text!="AI")continue;
                                var boxes=new Rect[3];for(int k=0;k<3;k++){var b=line.Words[i+k].BoundingRect;boxes[k]=new Rect(x+b.X/3,y+b.Y/3,b.Width/3,b.Height/3);}
                                if(ConfirmsModelHeader(words,number,line.Words[i].Text,line.Words[i+1].Text,line.Words[i+2].Text,boxes))return true;
                            }
                        }
                    }
                    return false;
                }catch(OperationCanceledException){return false;}
                // This is optional visual corroboration, never the primary OCR.
                // A missing capability, damaged local engine, or rejected input
                // means "can't verify", not a failure to translate the rest.
                catch(Exception){return false;}
                finally{lifetime.Dispose();verificationSlot.Release();}
            });
            // A platform recognition which is slow to acknowledge cancellation
            // must neither delay the user nor allocate a second native recognizer.
            var done=await Task.WhenAny(work,Task.Delay(budget,cancel)).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();return done==work&&await work.ConfigureAwait(false);
        }
        internal static bool ConfirmsModelHeader(WordBox[] words,string number,string tag,string noun,string seenNumber,Rect[] local){
            if(tag!="AI"||noun!="model"||seenNumber.TrimEnd(':','：')!=number||words.Length!=3||local.Length!=3)return false;
            for(int k=0;k<3;k++){
                var original=words[k].Box;var box=local[k];var overlap=Rect.Intersect(original,box);
                if(overlap.IsEmpty||overlap.Width<=0||overlap.Height<=0||
                    overlap.Width<.45*Math.Max(original.Width,box.Width)||overlap.Height<.45*Math.Max(original.Height,box.Height))return false;
            }
            return true;
        }
        internal static string[] Prepare(OcrPage page,IList<TranslationRegion> regions,CancellationToken cancel){
            if(page==null||regions==null)throw new ArgumentNullException();
            cancel.ThrowIfCancellationRequested();
            if(!page.Words.Any(NeedsAnalysis))return regions.Select(r=>r.Text).ToArray();
            var members=regions.Select(r=>page.Words.Where(w=>OcrCorrection.Contains(r,w)).ToArray()).ToArray();
            var owners=members.SelectMany(words=>words).GroupBy(w=>w).ToDictionary(g=>g.Key,g=>g.Count());
            var result=new string[regions.Count];
            for(int i=0;i<regions.Count;i++){
                cancel.ThrowIfCancellationRequested();var region=regions[i];result[i]=region.Text;
                var matches=tokens.Matches(region.Text).Cast<Match>().ToArray();
                var evidence=members[i].SelectMany(w=>{var parts=tokens.Matches(w.Text??"").Cast<Match>().ToArray();return parts.Select(m=>new {Text=m.Value,Word=w,WordScore=parts.Length==1&&owners[w]==1});}).ToArray();
                // Overlapping regions, different reading order or partial ownership:
                // without exact occurrence alignment, guessing a score is unsafe.
                if(matches.Length!=evidence.Length||!matches.Select(m=>m.Value).SequenceEqual(evidence.Select(e=>e.Text)))continue;
                var protectedRanges=Regex.Matches(region.Text,@"\S+").Cast<Match>().Where(LiteralToken)
                    .Select(p=>new ProtectedRange{Start=p.Index,End=p.Index+p.Length}).Concat(QuotedRanges(region.Text)).ToArray();
                var changed=new StringBuilder();int end=0;
                for(int j=0;j<matches.Length;j++){
                    cancel.ThrowIfCancellationRequested();var match=matches[j];var word=evidence[j].Word;
                    string replacement=match.Value;
                    if(evidence[j].WordScore&&ModelWord(word)&&(word.Confidence<.92||TechnologyToken(match.Value))&&
                        !protectedRanges.Any(p=>match.Index<p.End&&match.Index+match.Length>p.Start))
                        replacement=Correct(match.Value,region.Text,match.Index,word.Confidence,matches,j);
                    changed.Append(region.Text,end,match.Index-end).Append(replacement);end=match.Index+match.Length;
                }
                changed.Append(region.Text,end,region.Text.Length-end);result[i]=changed.ToString();
            }
            cancel.ThrowIfCancellationRequested();return result;
        }
        static bool ClauseBoundary(string text,int index){
            char c=text[index];
            if(c=='.'&&index>0&&index+1<text.Length&&char.IsDigit(text[index-1])&&char.IsDigit(text[index+1]))return false;
            return ".!?;\r\n\u3002\uFF01\uFF1F\uFF1B".IndexOf(c)>=0;
        }
        static string LocalContext(string context,Match[] contextTokens,int occurrence){
            var current=contextTokens[occurrence];
            int first=Math.Max(0,occurrence-6),last=Math.Min(contextTokens.Length-1,occurrence+6);
            int floor=first==0?0:contextTokens[first].Index;
            int ceiling=last==contextTokens.Length-1?context.Length:contextTokens[last].Index+contextTokens[last].Length;
            int left=current.Index,right=current.Index+current.Length;
            // Reuse the region's one token scan. Each occurrence examines at
            // most six neighboring tokens per side, then stops at punctuation;
            // a long clause never gets tokenized again for every ambiguous word.
            while(left>floor&&!ClauseBoundary(context,left-1))left--;
            while(right<ceiling&&!ClauseBoundary(context,right))right++;
            return context.Substring(left,right-left);
        }
        static bool LiteralPosition(string context,int start){
            int previous=start-1;bool newLine=false;
            while(previous>=0&&char.IsWhiteSpace(context[previous])){if(context[previous]=='\r'||context[previous]=='\n')newLine=true;previous--;}
            if(previous>=0&&("([={,+*/%&|^!<>".IndexOf(context[previous])>=0||context[previous]=='.'&&!newLine))return true;
            int end=previous+1;string before=context.Substring(Math.Max(0,end-32),Math.Min(end,32));
            return Regex.IsMatch(before,@"\b(?:return|throw|new|typeof|nameof|await|class|struct|interface|enum|namespace|record|def|function|using|import)$")||
                Regex.IsMatch(before,@"\b(?:Mr|Mrs|Ms|Dr|Prof)\.$",RegexOptions.IgnoreCase);
        }
        static string RuleTail(string context,int start){
            int next=start;while(next<context.Length&&char.IsWhiteSpace(context[next]))next++;
            // Every rule is anchored to the next word/operator. Retaining the
            // rest of a long paragraph per word would still allocate quadratically.
            return context.Substring(start,Math.Min(context.Length-start,next-start+64));
        }
        static string Correct(string word,string context,int start,double confidence,Match[] contextTokens,int occurrence){
            if(start>0&&(char.IsLetterOrDigit(context[start-1])||"_/\\.@-'\u2019".IndexOf(context[start-1])>=0))return word;
            // Very short acronyms cannot be resolved by edit distance. Require a
            // specific local construction plus independent word-level evidence.
            string tail=RuleTail(context,start+word.Length);
            if(tail.Length>0&&(char.IsLetterOrDigit(tail[0])||tail[0]=='_'))return word;
            // An OCR token can be a code identifier even without an underscore
            // or path. Keep assignment targets, calls and indexes verbatim.
            if(Regex.IsMatch(tail,@"^\s*(?:[=(\[]|\+\+|--|[+\-*/%&|^]=|\?\?=)"))return word;
            if(TechnologyToken(word)&&Regex.IsMatch(tail,@"^\s*[,\)\]}+*/%&|^!?<>:\-]"))return word;
            if(LiteralPosition(context,start))return word;
            if(TechnologyToken(word)){
                string local=LocalContext(context,contextTokens,occurrence);
                bool material=Regex.IsMatch(local,@"\b(?:alloy|alloys|aluminium|aluminum|oxide|oxides|hydroxide|bauxite|metal|metals|element|atomic|ions?)\b",RegexOptions.IgnoreCase)||
                    Regex.IsMatch(local,@"\bAl(?:[0-9\u2080-\u2089]|\s+\d{3,4}\b)");
                bool person=Regex.IsMatch(local,@"\b(?:Gore|Pacino|Smith|said|says|person|surname)\b",RegexOptions.IgnoreCase);
                // A noun compound is evidence; "Al generated art" and "Al
                // powered the engine" are also ordinary sentences about Al.
                if(word=="Al"&&!material&&!person&&Regex.IsMatch(tail,@"^[^\S\r\n]+(?i:responses?|chatbots?|assistants?|summary|summaries)\b"))return "AI";
                if(word=="OpenAl"&&!tail.StartsWith("-",StringComparison.Ordinal)&&!material&&!person&&
                    Regex.IsMatch(local,@"\b(?:ChatGPT|GPT|chatbots?|artificial\s+intelligence|language\s+models?)\b",RegexOptions.IgnoreCase)&&
                    !Regex.IsMatch(local,@"\b(?:audio|sound)\b",RegexOptions.IgnoreCase)&&!Regex.IsMatch(local,@"\bOpenAL\b"))return "OpenAI";
                return word;
            }
            if(word.Length<5||word.Length>24||confidence>=.90||word.Any(char.IsDigit))return word;
            // Keep title case, CamelCase, all caps, possessives and identifiers.
            // Internal capital I is allowed only as the familiar OCR l/I confusion.
            if(word[0]<'a'||word[0]>'z'||word.Any(c=>!(c>='a'&&c<='z')&&c!='I')||
                (start>0&&(context[start-1]=='\''||context[start-1]=='\u2019'||context[start-1]=='-'))||tail.StartsWith("-",StringComparison.Ordinal)||
                Regex.IsMatch(tail,@"^['\u2019]s\b"))return word;
            // Only reviewed UI words can ever be returned by the dictionary, and
            // the final filter requires exactly one visual substitution. Do the
            // necessary membership check first. A low score alone does not
            // warrant a spelling lookup, even on pages with one eligible word.
            if(!CouldCorrectSpelling(word))return word;
            Lexicon dict;
            try{dict=lexicon.Value;}catch(IOException){return word;}catch(TypeInitializationException){return word;}
            string lower=word.ToLowerInvariant();
            if(dict.Known.Contains(lower))return word;
            var candidates=dict.Candidates.Lookup(lower,SymSpell.Verbosity.Closest,1)
                .Where(c=>VisualConfusion(word,c.term)).Select(c=>c.term).Distinct().Take(2).ToArray();
            if(candidates.Length!=1)return word;
            return candidates[0];
        }
        internal static bool CouldCorrectSpelling(string source){
            return visualSpellings.Contains(source);
        }
        internal static bool VisualConfusion(string source,string candidate){
            // One visually plausible substitution, not arbitrary spellchecking.
            if(source.Length!=candidate.Length)return false;
            int differences=0;
            for(int i=0;i<source.Length;i++)if(source[i]!=candidate[i]){
                char a=source[i],b=candidate[i];
                if(++differences>1||!((a=='l'&&b=='i')||(a=='i'&&b=='l')||(a=='I'&&(b=='l'||b=='i'))))return false;
            }
            return differences==1;
        }
    }
}
