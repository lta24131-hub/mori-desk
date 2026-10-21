#if OCR_PASS_PLAN_TESTS
// Standalone contract tests: compile this file together with the unchanged
// production EnglishOcr.cs, not with the application or native OCR assemblies.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using QingJie;
using Tesseract;
using Rect = System.Windows.Rect;

internal static class OcrPassPlanTests {
    static int checks, cases, expectedEnglishPasses;
    static void Check(bool condition, string name) {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
    static void Run(string name, Action test) {
        StubOcr.Reset(); OcrLanguagePacks.Reset();
        Console.WriteLine("CASE " + name);
        try { test(); }
        finally { Console.WriteLine("TRACE " + name + ": " + string.Join("|", StubOcr.Events)); }
        Check(StubOcr.ActiveEngines == 0 && StubOcr.MaximumEngines <= 1, name + ": no resident or simultaneous engines");
        Check(StubOcr.EngineCreates == StubOcr.EngineDisposes, name + ": every engine disposed");
        Check(StubOcr.PixCreates == 1 && StubOcr.PixDisposes == 1, name + ": single inert pixel owner disposed");
        Check(StubOcr.PageCreates == StubOcr.PageDisposes && StubOcr.IteratorCreates == StubOcr.IteratorDisposes,
            name + ": every inert page and iterator disposed");
        cases++;
    }
    static OcrPage Original(string text = "fallback words", double width = 100) {
        return new OcrPage { Words = new List<WordBox> {
            new WordBox { Text = text, Box = new Rect(0, 0, width, 20), Line = 0 } } };
    }
    static StubLine Line(string text, float confidence = 95, int x = 0, int y = 0, int width = 100, int height = 20) {
        return new StubLine(text, confidence, new StubWord(text, x, y, width, height));
    }
    static OcrPage Improve(OcrPage original, string languages = "", double ratio = 1) {
        return EnglishOcr.Improve(new OcrPixels(), ratio, original, languages).GetAwaiter().GetResult();
    }
    static void Plan(string language, PageSegMode mode, params StubLine[] lines) { StubOcr.Set(language, mode, lines); }
    static void ExpectedText(OcrPage result, string text, string name) {
        Check(result.Text == text, name + ": caller-visible transcript");
        Check(result.Words.Count > 0 && result.Words.All(w => w.TextStart >= 0 &&
            w.TextStart + w.Text.Length <= result.Text.Length &&
            result.Text.Substring(w.TextStart, w.Text.Length) == w.Text), name + ": transcript indices remain valid");
    }
    static string EnglishModes() {
        return expectedEnglishPasses == 1 ? "process:eng:SparseText" : "process:eng:SparseText|process:eng:Auto";
    }
    static void OptionalConfidence(string text, float confidence, bool accepted) {
        OcrLanguagePacks.Install("kor"); Plan("kor", PageSegMode.Auto, Line(text, confidence));
        ExpectedText(Improve(Original(), "kor"), accepted ? text : "fallback words", "optional threshold " + text + " " + confidence);
    }
    static void Tests() {
        Check(typeof(TesseractEngine).Assembly == typeof(OcrPassPlanTests).Assembly,
            "OCR engine implementation is compiled inert stub, never a native model runtime");
        Check(!typeof(OcrPassPlanTests).Assembly.GetReferencedAssemblies().Any(a =>
            a.Name == "Tesseract" || a.Name == "PresentationFramework" || a.Name == "PresentationCore" || a.Name == "Mori"),
            "host references no application, native OCR wrapper, or UI framework");
        Run("English pass contract", () => {
            Plan("eng", PageSegMode.SparseText, Line("new English"));
            ExpectedText(Improve(Original("old English")), "new English", "English replacement");
            string observed = string.Join("|", StubOcr.Events.Where(e => e.StartsWith("process:")));
            Check(observed == EnglishModes(), "English uses exactly " + expectedEnglishPasses +
                " explicitly expected passes; observed=" + observed);
            Check(string.Join("|", StubOcr.Languages) == "eng", "default creates only English engine");
            Check(StubOcr.Events.Last() == "pix:dispose", "pixels outlive all engines and pages");
        });
        Run("Optional Korean and Japanese plans", () => {
            OcrLanguagePacks.Install("kor", "jpn");
            ExpectedText(Improve(Original(), "jpn+kor+kor+unknown"), "fallback words", "empty optional outputs preserve fallback");
            Check(string.Join("|", StubOcr.Events.Where(e => e.StartsWith("process:"))) ==
                EnglishModes() + "|process:kor:Auto|process:kor:SparseText|process:jpn:Auto|process:jpn:SparseText",
                "enabled Korean and Japanese retain Auto then Sparse independently");
            Check(string.Join("|", StubOcr.Languages) == "eng|kor|jpn", "canonical optional selection does not load duplicate or unknown IDs");
            Check(StubOcr.Events.IndexOf("engine:dispose:eng") < StubOcr.Events.IndexOf("engine:create:kor") &&
                StubOcr.Events.IndexOf("engine:dispose:kor") < StubOcr.Events.IndexOf("engine:create:jpn"),
                "optional engines are created only after previous engine is released");
            Check(StubOcr.Variables.Count(v => v == "kor:thresholding_method=2" || v == "kor:thresholding_kfactor=0.04" ||
                v == "jpn:thresholding_method=2" || v == "jpn:thresholding_kfactor=0.04") == 4,
                "both optional languages retain threshold configuration");
        });
        Run("Missing optional models", () => {
            ExpectedText(Improve(Original(), "kor+jpn"), "fallback words", "uninstalled languages preserve fallback");
            Check(string.Join("|", StubOcr.Languages) == "eng", "uninstalled optional models never create an engine");
            Check(string.Join("|", OcrLanguagePacks.InstallChecks) == "kor|jpn", "installation check covers enabled languages only");
        });
        Run("Installed but disabled models", () => {
            OcrLanguagePacks.Install("kor", "jpn", "fra"); Improve(Original());
            Check(string.Join("|", StubOcr.Languages) == "eng" && OcrLanguagePacks.InstallChecks.Count == 0,
                "merely installed languages do not load or perform installation probes");
        });
        Run("Chinese protection", () => {
            Plan("eng", PageSegMode.SparseText, Line("English false replacement", 99));
            Plan("eng", PageSegMode.Auto, Line("another false replacement", 99));
            ExpectedText(Improve(Original("中文段落内容")), "中文段落内容", "English must not overwrite genuine Chinese");
        });
        Run("Numeric-only fallback protection", () => {
            Plan("eng", PageSegMode.SparseText, Line("1920x1080", 99));
            Plan("eng", PageSegMode.Auto, Line("1920 x 1080", 99));
            ExpectedText(Improve(Original("1920 × 1080")), "1920 × 1080", "numeric-only guesses do not erase fallback digits or multiplication symbol");
        });
        Run("Independent numeric line preservation", () => {
            Plan("eng", PageSegMode.SparseText, Line("repaired heading", 95));
            var original = Original("bad heading");
            var number = new WordBox { Text = "2026-10-16 08:30", Box = new Rect(0, 50, 150, 20), Line = 1 };
            original.Words.Add(number);
            var result = Improve(original);
            ExpectedText(result, "repaired heading" + Environment.NewLine + "2026-10-16 08:30",
                "repairing another line retains date and time exactly");
            Check(object.ReferenceEquals(result.Words.Last(), number) && number.Line == 1,
                "unmatched numeric fallback keeps original word object, geometry and line position");
        });
        Run("English Auto fallback contract", () => {
            Plan("eng", PageSegMode.SparseText, Line("weak sparse guess", 69));
            Plan("eng", PageSegMode.Auto, Line("Auto recovery 2026", 90));
            ExpectedText(Improve(Original()), expectedEnglishPasses == 2 ? "Auto recovery 2026" : "fallback words",
                "explicit pass plan controls recovery from weak Sparse output");
        });
        Run("Rejected Sparse fragment cannot suppress unrelated complete Auto correction", () => {
            Plan("eng", PageSegMode.SparseText, Line("wrong", 99, 0, 0, 40));
            Plan("eng", PageSegMode.Auto, Line("correct recovered phrase", 94));
            ExpectedText(Improve(Original("bad original transcript")), expectedEnglishPasses == 2 ? "correct recovered phrase" : "bad original transcript",
                "a high-confidence fragment rejected by merge cannot suppress a complete non-prefix Auto correction");
        });
        Run("Rejected single-letter Sparse cannot suppress complete Auto", () => {
            Plan("eng", PageSegMode.SparseText, Line("X", 99));
            Plan("eng", PageSegMode.Auto, Line("recovered English phrase", 94));
            ExpectedText(Improve(Original()), expectedEnglishPasses == 2 ? "recovered English phrase" : "fallback words",
                "a rejected single-letter candidate never claims geometry for Auto suppression");
        });
        Run("Rejected unmatched Sparse cannot suppress confident Auto supplement", () => {
            Plan("eng", PageSegMode.SparseText, Line("uncertain extra words", 84, 0, 50));
            Plan("eng", PageSegMode.Auto, Line("confident new sentence", 95, 0, 50));
            ExpectedText(Improve(Original()), expectedEnglishPasses == 2 ? "fallback words" + Environment.NewLine + "confident new sentence" : "fallback words",
                "an unmatched Sparse candidate below supplement threshold does not hide a valid Auto addition");
        });
        Run("Real Independent heading fragment preserves complete fallback", () => {
            // Exact 3x pixel coordinates/confidence from the authorized robot
            // card: Sparse returned only Independ, despite a full Windows line.
            Plan("eng", PageSegMode.SparseText, Line("Independ", 91.6131f, 2266, 1361, 233, 57));
            var original = new OcrPage { Words = new List<WordBox> {
                new WordBox { Text = "lndependept", Box = new Rect(755, 1360.0/3, 106, 76.0/3), Line = 0 } } };
            var result = Improve(original, "", 3);
            ExpectedText(result, "lndependept", "73-percent-width English fragment cannot erase a complete heading");
            Check(result.Words.Single().Box.Right == 861, "retained heading keeps rightmost letters' original extent");
        });
        Run("Real Independent Auto complete heading recovery", () => {
            Plan("eng", PageSegMode.SparseText, Line("Independ", 91.6131f, 2266, 1361, 233, 57));
            Plan("eng", PageSegMode.Auto, Line("Independent", 96.35918f, 2265, 1356, 318, 83));
            var original = new OcrPage { Words = new List<WordBox> {
                new WordBox { Text = "lndependept", Box = new Rect(755, 1360.0/3, 106, 76.0/3), Line = 0 } } };
            var result = Improve(original, "", 3);
            ExpectedText(result, expectedEnglishPasses == 2 ? "Independent" : "lndependept",
                "high-confidence complete Auto heading is not discarded just because Sparse overlaps");
            Check(result.Words.Single().Box.Right == 861, "complete heading recovery never truncates its right edge");
        });
        Run("Real wheel travel Sparse tail preservation", () => {
            Plan("eng", PageSegMode.SparseText, Line("wheel", 96.57138f, 2262, 1426, 147, 46));
            var original = new OcrPage { Words = new List<WordBox> {
                new WordBox { Text = "wheel travel.", Box = new Rect(2261.0/3, 1426.0/3, 107, 47.0/3), Line = 0 } } };
            ExpectedText(Improve(original, "", 3), "wheel travel.",
                "96-confidence Sparse word cannot discard complete Windows second word");
        });
        Run("Real wheel travel Auto complete line recovery", () => {
            Plan("eng", PageSegMode.SparseText, Line("wheel", 96.57138f, 2262, 1426, 147, 46));
            Plan("eng", PageSegMode.Auto, Line("wheel travel,", 87.98167f, 2261, 1426, 321, 46));
            var original = new OcrPage { Words = new List<WordBox> {
                new WordBox { Text = "wheel travel.", Box = new Rect(2261.0/3, 1426.0/3, 107, 47.0/3), Line = 0 } } };
            ExpectedText(Improve(original, "", 3), expectedEnglishPasses == 2 ? "wheel travel," : "wheel travel.",
                "complete Auto line can restore the missing word even below Sparse fragment confidence");
        });
        Run("Complete Auto extension repairs surviving Sparse fragment", () => {
            Plan("eng", PageSegMode.SparseText, Line("wheel", 96, 0, 0, 49));
            Plan("eng", PageSegMode.Auto, Line("wheel travel", 88, 0, 0, 107));
            ExpectedText(Improve(Original("wheel", 49)), expectedEnglishPasses == 2 ? "wheel travel" : "wheel",
                "Auto extension may replace an accepted fragment, not only a Windows fallback");
        });
        Run("Auto cross-column extension safety", () => {
            Plan("eng", PageSegMode.SparseText, Line("Fold for the", 96.68751f, 1346, 1113, 298, 71));
            Plan("eng", PageSegMode.Auto, Line("Fold for the fhe", 99, 1346, 1108, 1482, 80));
            var original = new OcrPage { Words = new List<WordBox> {
                new WordBox { Text = "Fold for the", Box = new Rect(1346.0/3, 1117.0/3, 298.0/3, 47.0/3), Line = 0 } } };
            ExpectedText(Improve(original, "", 3), "Fold for the",
                "even high-confidence distant-column concatenation cannot replace a valid Sparse line");
        });
        Run("Shorter complete English correction remains allowed", () => {
            Plan("eng", PageSegMode.SparseText, Line("spelling", 95, 0, 0, 100));
            ExpectedText(Improve(Original("spelllling", 100)), "spelling",
                "shorter transcript with complete geometric coverage can fix duplicated letters");
        });
        Run("Unrelated Auto overlap keeps established English", () => {
            Plan("eng", PageSegMode.SparseText, Line("stable heading", 95));
            Plan("eng", PageSegMode.Auto, Line("different longer hallucination", 99));
            ExpectedText(Improve(Original()), "stable heading",
                "overlap exception requires a literal prefix or suffix, not merely more characters");
        });
        Run("English confidence below threshold", () => {
            Plan("eng", PageSegMode.SparseText, Line("low confidence", 69));
            ExpectedText(Improve(Original()), "fallback words", "English confidence 69 rejected");
        });
        Run("English confidence at threshold", () => {
            Plan("eng", PageSegMode.SparseText, Line("accepted English", 70));
            ExpectedText(Improve(Original()), "accepted English", "English confidence 70 replaces overlapping fallback");
        });
        Run("Unmatched English low confidence", () => {
            Plan("eng", PageSegMode.SparseText, Line("extra English", 84, 0, 50));
            ExpectedText(Improve(Original()), "fallback words", "unmatched confidence 84 cannot invent line");
        });
        Run("Unmatched English high confidence", () => {
            Plan("eng", PageSegMode.SparseText, Line("extra English", 85, 0, 50));
            ExpectedText(Improve(Original()), "fallback words" + Environment.NewLine + "extra English",
                "unmatched confidence 85 can supplement without losing existing line");
        });
        Run("Single letter rejection", () => {
            Plan("eng", PageSegMode.SparseText, Line("X", 99));
            ExpectedText(Improve(Original()), "fallback words", "isolated single letter cannot replace full line");
        });
        Run("Coordinate scaling", () => {
            Plan("eng", PageSegMode.SparseText, Line("scaled English", 95, 20, 40, 200, 40));
            var result = Improve(new OcrPage { Words = new List<WordBox> {
                new WordBox { Text = "old words", Box = new Rect(10, 20, 100, 20), Line = 0 } } }, "", 2);
            ExpectedText(result, "scaled English", "inverse OCR coordinate mapping");
            Check(result.Words.Single().Box == new Rect(10, 20, 100, 20), "recognized coordinates map back by exact ratio");
        });
        Run("Optional wrong script", () => {
            OcrLanguagePacks.Install("kor"); Plan("kor", PageSegMode.Auto, Line("wrong Latin guess", 99));
            ExpectedText(Improve(Original(), "kor"), "fallback words", "Korean model must produce Korean script");
        });
        Run("Optional four-script below threshold", () => OptionalConfidence("가나다라", 84, false));
        Run("Optional four-script at threshold", () => OptionalConfidence("가나다라", 85, true));
        Run("Optional short-script below threshold", () => OptionalConfidence("가나다", 89, false));
        Run("Optional short-script at threshold", () => OptionalConfidence("가나다", 90, true));
        Run("Optional single-script rejection", () => OptionalConfidence("가", 99, false));
        Run("Optional majority guard", () => OptionalConfidence("가나다abcde", 99, false));
        Run("Japanese mixed-script allowance", () => {
            OcrLanguagePacks.Install("jpn"); Plan("jpn", PageSegMode.Auto, Line("かなABCDE", 95));
            ExpectedText(Improve(Original(), "jpn"), "かなABCDE", "Japanese kana may supplement mixed Latin line");
        });
        Run("Cross-script claim safety", () => {
            OcrLanguagePacks.Install("kor", "jpn");
            Plan("kor", PageSegMode.Auto, Line("가나다라", 90));
            Plan("jpn", PageSegMode.Auto, Line("かなかな", 99));
            ExpectedText(Improve(Original(), "kor+jpn"), "가나다라", "later unrelated script cannot erase confidently claimed Korean");
        });
        Run("Latin accent safety", () => {
            OcrLanguagePacks.Install("fra", "deu");
            Plan("fra", PageSegMode.Auto, Line("café", 95)); Plan("deu", PageSegMode.Auto, Line("cafe", 99));
            ExpectedText(Improve(Original(), "fra+deu"), "café", "later Latin model cannot erase confidently recognized accent");
        });
        Run("Latin higher-confidence correction", () => {
            OcrLanguagePacks.Install("fra", "deu");
            Plan("fra", PageSegMode.Auto, Line("bonjour", 90)); Plan("deu", PageSegMode.Auto, Line("gutenTag", 95));
            ExpectedText(Improve(Original(), "fra+deu"), "gutenTag", "compatible Latin claim can improve at higher confidence");
        });
        Run("Optional small fragment safety", () => {
            OcrLanguagePacks.Install("kor"); Plan("kor", PageSegMode.Auto, Line("가나다라", 95, 0, 0, 50));
            ExpectedText(Improve(Original("complete fallback line", 200), "kor"), "complete fallback line",
                "small optional fragment cannot erase a complete fallback line");
        });
        Run("Sparse optional fragment joining", () => {
            OcrLanguagePacks.Install("kor");
            Plan("kor", PageSegMode.SparseText, Line("가나다라", 95, 0, 0, 40), Line("마바사아", 95, 50, 0, 40));
            var result = Improve(Original(), "kor");
            ExpectedText(result, "가나다라 마바사아", "same-baseline sparse fragments combine safely");
            Check(result.Words.Select(w => w.Line).Distinct().Count() == 1, "joined sparse fragments share one caller-visible line");
        });
        Run("Joined Korean syllable words", () => {
            OcrLanguagePacks.Install("kor");
            Plan("kor", PageSegMode.Auto, new StubLine("가나다라", 95,
                new StubWord("가나", 0, 0, 40, 20), new StubWord("다라", 40, 0, 40, 20)));
            var result = Improve(Original(), "kor");
            ExpectedText(result, "가나다라", "line transcript controls Korean word joining");
            Check(result.Words.Count == 1 && result.Words[0].Box == new Rect(0, 0, 80, 20),
                "joined syllables retain one word and union of their original boxes");
        });
        Run("English failure releases resources", () => {
            StubOcr.ThrowOnProcess = "eng:SparseText";
            bool failed = false;
            try { Improve(Original()); } catch (InvalidOperationException error) { failed = error.Message == "inert process failure"; }
            Check(failed, "engine processing failure reaches caller without swallowing the error");
        });
        Run("Optional failure releases resources", () => {
            OcrLanguagePacks.Install("kor"); StubOcr.ThrowOnProcess = "kor:Auto";
            bool failed = false;
            try { Improve(Original(), "kor"); } catch (InvalidOperationException error) { failed = error.Message == "inert process failure"; }
            Check(failed, "optional processing failure reaches caller and closes English and optional engines");
        });
    }
    public static int Main(string[] args) {
        try {
            Console.OutputEncoding = new UTF8Encoding(false);
            if (args.Length != 1 || !args[0].StartsWith("--expected-english-passes=", StringComparison.Ordinal) ||
                !int.TryParse(args[0].Substring("--expected-english-passes=".Length), out expectedEnglishPasses) ||
                (expectedEnglishPasses != 1 && expectedEnglishPasses != 2))
                throw new ArgumentException("Specify exactly --expected-english-passes=1 (new) or =2 (legacy).");
            Tests();
            Console.WriteLine("PASS " + checks + " assertions in " + cases + " deterministic cases; expected English passes=" + expectedEnglishPasses);
            Console.WriteLine("SAFETY: inert pixels, language installation and OCR runtime; no models, image decode, UI, application entry point, settings, library, native calls or network.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine("FAIL after " + checks + " assertions: " + error); return 1; }
    }
}

// Minimal data/script helpers, intentionally without production settings, file
// probing, installation, image decoding or Windows OCR. The behavior under test
// (pass scheduling, confidence/script gates and merge) is real EnglishOcr.cs.
namespace QingJie {
    public sealed class WordBox { public string Text; public Rect Box; public int Line, TextStart; }
    public sealed class OcrPage { public string Text; public List<WordBox> Words = new List<WordBox>(); }
    internal sealed class OcrPixels { public Pix TesseractBitmap() { return new Pix(); } }
    public static class Geometry {
        public static bool IsHangul(char c) { return (c >= '\uac00' && c <= '\ud7a3') || (c >= '\u1100' && c <= '\u11ff') || (c >= '\u3130' && c <= '\u318f'); }
        public static bool NeedsWordSpace(char previous, char next) { return (previous < 0x2e80 && next < 0x2e80) || IsHangul(previous) || IsHangul(next); }
        public static string JoinWords(IEnumerable<WordBox> words) {
            var result = new StringBuilder(); int line = -1;
            foreach (var word in words) {
                if (result.Length > 0) {
                    if (line != word.Line) result.AppendLine();
                    else if (word.Text.Length > 0 && NeedsWordSpace(result[result.Length - 1], word.Text[0])) result.Append(' ');
                }
                result.Append(word.Text); line = word.Line;
            }
            return result.ToString();
        }
    }
    public static class OcrService {
        public static void IndexText(OcrPage page) {
            var text = new StringBuilder(); int line = -1;
            foreach (var word in page.Words) {
                if (text.Length > 0) {
                    if (line != word.Line) text.AppendLine();
                    else if (word.Text.Length > 0 && Geometry.NeedsWordSpace(text[text.Length - 1], word.Text[0])) text.Append(' ');
                }
                word.TextStart = text.Length; text.Append(word.Text); line = word.Line;
            }
            page.Text = text.ToString();
        }
    }
    public static class OcrLanguagePacks {
        static readonly string[] available = { "kor", "jpn", "rus", "fra", "deu", "spa", "por", "ita", "ara", "tha", "vie" };
        static readonly HashSet<string> installed = new HashSet<string>();
        internal static readonly List<string> InstallChecks = new List<string>();
        public static string Folder { get { return "INERT-OPTIONAL-MODELS-NO-FILES"; } }
        internal static void Reset() { installed.Clear(); InstallChecks.Clear(); }
        internal static void Install(params string[] languages) { foreach (var language in languages) installed.Add(language); }
        public static string[] ParseSelection(string selection) {
            var requested = new HashSet<string>((selection ?? "").Split('+'));
            return available.Where(requested.Contains).ToArray();
        }
        public static bool Installed(string id) { InstallChecks.Add(id); return installed.Contains(id); }
        public static bool IsLatin(string id) { return id == "fra" || id == "deu" || id == "spa" || id == "por" || id == "ita" || id == "vie"; }
        public static bool IsScript(char c, string id) {
            if (id == "kor") return Geometry.IsHangul(c);
            if (id == "jpn") return c >= '\u3040' && c <= '\u30ff';
            if (id == "rus") return c >= '\u0400' && c <= '\u052f';
            if (id == "ara") return (c >= '\u0600' && c <= '\u06ff') || (c >= '\ufb50' && c <= '\ufeff');
            if (id == "tha") return c >= '\u0e00' && c <= '\u0e7f';
            return IsLatin(id) && char.IsLetter(c) && c < 0x2e80;
        }
    }
}

namespace Tesseract {
    public enum EngineMode { LstmOnly }
    public enum PageSegMode { Auto, SparseText }
    public enum PageIteratorLevel { TextLine, Word }
    public struct Rect {
        public int X1, Y1, Width, Height;
        public Rect(int x, int y, int width, int height) { X1 = x; Y1 = y; Width = width; Height = height; }
    }
    internal sealed class StubWord {
        public readonly string Text; public readonly Rect Bounds;
        public StubWord(string text, int x, int y, int width, int height) { Text = text; Bounds = new Rect(x, y, width, height); }
    }
    internal sealed class StubLine {
        public readonly string Text; public readonly float Confidence; public readonly StubWord[] Words;
        public StubLine(string text, float confidence, params StubWord[] words) { Text = text; Confidence = confidence; Words = words; }
    }
    internal static class StubOcr {
        internal static readonly List<string> Events = new List<string>(), Languages = new List<string>(), Variables = new List<string>();
        static readonly Dictionary<string, StubLine[]> plans = new Dictionary<string, StubLine[]>();
        internal static int EngineCreates, EngineDisposes, ActiveEngines, MaximumEngines, PixCreates, PixDisposes,
            PageCreates, PageDisposes, IteratorCreates, IteratorDisposes;
        internal static string ThrowOnProcess;
        internal static void Reset() {
            Events.Clear(); Languages.Clear(); Variables.Clear(); plans.Clear(); ThrowOnProcess = null;
            EngineCreates = EngineDisposes = ActiveEngines = MaximumEngines = PixCreates = PixDisposes =
                PageCreates = PageDisposes = IteratorCreates = IteratorDisposes = 0;
        }
        internal static void Set(string language, PageSegMode mode, StubLine[] lines) { plans[language + ":" + mode] = lines; }
        internal static StubLine[] Read(string key) { StubLine[] result; return plans.TryGetValue(key, out result) ? result : new StubLine[0]; }
    }
    public sealed class Pix : IDisposable {
        bool disposed;
        public Pix() { StubOcr.PixCreates++; StubOcr.Events.Add("pix:create"); }
        public static Pix LoadFromMemory(byte[] ignored) { return new Pix(); }
        public void Dispose() { if (disposed) throw new InvalidOperationException("pixel double-dispose"); disposed = true; StubOcr.PixDisposes++; StubOcr.Events.Add("pix:dispose"); }
    }
    public sealed class TesseractEngine : IDisposable {
        readonly string language; bool disposed;
        public TesseractEngine(string ignoredFolder, string language, EngineMode mode) {
            this.language = language; StubOcr.Languages.Add(language); StubOcr.EngineCreates++; StubOcr.ActiveEngines++;
            StubOcr.MaximumEngines = Math.Max(StubOcr.MaximumEngines, StubOcr.ActiveEngines);
            StubOcr.Events.Add("engine:create:" + language);
            if (StubOcr.ActiveEngines > 1) throw new InvalidOperationException("simultaneously resident engines");
        }
        public bool SetVariable(string name, object value) {
            if (disposed) throw new ObjectDisposedException(language);
            StubOcr.Variables.Add(language + ":" + name + "=" + Convert.ToString(value, CultureInfo.InvariantCulture)); return true;
        }
        public Page Process(Pix ignored, PageSegMode mode) {
            if (disposed) throw new ObjectDisposedException(language);
            string key = language + ":" + mode; StubOcr.Events.Add("process:" + key);
            if (StubOcr.ThrowOnProcess == key) throw new InvalidOperationException("inert process failure");
            return new Page(StubOcr.Read(key));
        }
        public void Dispose() {
            if (disposed) throw new InvalidOperationException("engine double-dispose"); disposed = true;
            StubOcr.EngineDisposes++; StubOcr.ActiveEngines--; StubOcr.Events.Add("engine:dispose:" + language);
        }
    }
    public sealed class Page : IDisposable {
        readonly StubLine[] lines; bool disposed;
        internal Page(StubLine[] lines) { this.lines = lines; StubOcr.PageCreates++; }
        public ResultIterator GetIterator() { if (disposed) throw new ObjectDisposedException("page"); return new ResultIterator(lines); }
        public void Dispose() { if (disposed) throw new InvalidOperationException("page double-dispose"); disposed = true; StubOcr.PageDisposes++; }
    }
    public sealed class ResultIterator : IDisposable {
        readonly StubLine[] lines; int line, word; bool disposed;
        internal ResultIterator(StubLine[] lines) { this.lines = lines; StubOcr.IteratorCreates++; }
        public void Begin() { line = word = 0; }
        public bool IsAtBeginningOf(PageIteratorLevel ignored) { return word == 0; }
        public float GetConfidence(PageIteratorLevel ignored) { return line < lines.Length ? lines[line].Confidence : 0; }
        public string GetText(PageIteratorLevel level) {
            if (line >= lines.Length) return null;
            return level == PageIteratorLevel.TextLine ? lines[line].Text : lines[line].Words[word].Text;
        }
        public bool TryGetBoundingBox(PageIteratorLevel ignored, out Rect bounds) {
            bounds = line < lines.Length ? lines[line].Words[word].Bounds : new Rect(); return line < lines.Length;
        }
        public bool Next(PageIteratorLevel ignored) {
            if (line >= lines.Length) return false;
            if (++word < lines[line].Words.Length) return true;
            word = 0; return ++line < lines.Length;
        }
        public void Dispose() { if (disposed) throw new InvalidOperationException("iterator double-dispose"); disposed = true; StubOcr.IteratorDisposes++; }
    }
}
#endif
