using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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
        static readonly HashSet<string> uiWords=new HashSet<string>(("settings language translation translate translated original image images text recognition recognize clipboard download upload file files folder folders browser screenshot screenshots preview select selected selection copy paste save cancel close open update install remove search display window toolbar privacy password account notification notifications appearance preferences connection advanced general enabled disabled").Split(' '),StringComparer.Ordinal);
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
                        // prose. The full vocabulary only excludes real words;
                        // automatic candidates are reviewed interface vocabulary.
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
            return ModelWord(word)&&(word.Confidence<.92||((word.Text??"").IndexOf("Al",StringComparison.Ordinal)>=0&&tokens.Matches(word.Text).Cast<Match>().Any(m=>TechnologyToken(m.Value))));
        }
        internal static Task<string[]> PrepareAsync(OcrPage page,IList<TranslationRegion> regions,CancellationToken cancel){
            if(page==null||regions==null)throw new ArgumentNullException();
            cancel.ThrowIfCancellationRequested();
            if(!page.Words.Any(NeedsAnalysis))return Task.FromResult(regions.Select(r=>r.Text).ToArray());
            return Task.Run(()=>Prepare(page,regions,cancel),cancel);
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
            Lexicon dict;
            try{dict=lexicon.Value;}catch(IOException){return word;}catch(TypeInitializationException){return word;}
            string lower=word.ToLowerInvariant();
            if(dict.Known.Contains(lower))return word;
            var candidates=dict.Candidates.Lookup(lower,SymSpell.Verbosity.Closest,1)
                .Where(c=>VisualConfusion(word,c.term)).Select(c=>c.term).Distinct().Take(2).ToArray();
            if(candidates.Length!=1)return word;
            return candidates[0];
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
