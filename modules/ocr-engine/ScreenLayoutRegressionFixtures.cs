using System.Text.Json;
using SkiaSharp;

// Invoked only behind Program's dedicated-VM evaluation guard. These fixtures
// exercise production grouping without loading OCR models or opening windows.
internal static class ScreenLayoutRegressionFixtures
{
    private sealed record CheckResult(string Name, bool Passed, string Actual);

    internal static void Run()
    {
        var checks = new List<CheckResult>();
        void Check(string name, bool passed, ScreenPage page) => checks.Add(new(name, passed,
            string.Join(" | ", page.Paragraphs.Select(p => p.Text.Replace("\n", " / ")))));
        static ScreenWord Word(string text, int line, float x, float y, float width, float height, string prefix = "") =>
            new(text, prefix, line, SKRect.Create(x, y, width, height), .95);
        static int Occurrences(string text, string token) => text.Split(token, StringSplitOptions.None).Length - 1;

        var membership = ScreenOcr.Layout([
            Word("First", 0, 0, 0, 100, 40), Word("last", 1, 145, 0, 100, 40),
            Word("版本", 2, 105, 10, 35, 20)]);
        Check("inline Chinese label belongs to exactly one paragraph",
            membership.Paragraphs.Count(p => p.Text == "版本") == 1 &&
            membership.Paragraphs.Count(p => p.Text == "First last") == 1 &&
            Occurrences(string.Join("\n", membership.Paragraphs.Select(p => p.Text)), "版本") == 1, membership);
        Check("paragraph ownership also controls selectable reading lines",
            membership.Words.Single(w => w.Text == "版本").Line != membership.Words.Single(w => w.Text == "First").Line &&
            membership.Words.Length == 3, membership);

        foreach (bool separateDetectorLines in new[] { false, true })
        {
            var mixed = ScreenOcr.Layout([
                Word("Read", 0, 10, 10, 80, 40),
                Word("before installing", separateDetectorLines ? 1 : 0, 95, 32, 160, 18, separateDetectorLines ? "" : " ")]);
            Check("same-baseline mixed sizes retain sentence context; separate detector lines=" + separateDetectorLines,
                mixed.Paragraphs.Length == 1 && mixed.Paragraphs[0].Lines.Length == 1 &&
                mixed.Paragraphs[0].Text == "Read before installing" && mixed.Words.Select(w => w.Line).Distinct().Count() == 1, mixed);
        }

        var heading = ScreenOcr.Layout([
            Word("Read", 0, 10, 10, 80, 40), Word("before installing this application", 1, 10, 65, 260, 18)]);
        Check("a large heading above small body text stays separate",
            heading.Paragraphs.Length == 2 && heading.Paragraphs.Any(p => p.Text == "Read") &&
            heading.Paragraphs.Any(p => p.Text == "before installing this application"), heading);

        var wrapped = ScreenOcr.Layout([
            Word("A wrapped line continues", 0, 10, 10, 200, 18),
            Word("on a distinct second row", 1, 10, 34, 200, 18)]);
        Check("ordinary wrapped lines retain distinct physical rows",
            wrapped.Paragraphs.Sum(p => p.Lines.Length) == 2 && wrapped.Words.Select(w => w.Line).Distinct().Count() == 2, wrapped);

        var repeated = ScreenOcr.Layout([
            Word("again", 0, 10, 10, 40, 18), Word("again", 0, 56, 10, 40, 18, " ")]);
        Check("genuine repeated words survive membership tracking",
            repeated.Words.Length == 2 && repeated.Paragraphs.Single().Text == "again again", repeated);

        var numberedWords = new[] {
            Word("1. First", 0, 0, 0, 200, 16), Word("2. Second", 0, 1, 28, 200, 16, "\n"),
            Word("版本", 1, 100, 18, 30, 8) };
        var numbered = ScreenOcr.Layout(numberedWords.ToList());
        var numberedOrder = new[] { "1. First", "版本", "2. Second" };
        Check("numbered items sharing one detector source remain independent around a separate label",
            numbered.Paragraphs.Select(p => p.Text).SequenceEqual(numberedOrder), numbered);
        Check("selectable numbered words retain first-item label second-item reading order",
            numbered.Words.Select(w => w.Text).SequenceEqual(numberedOrder) &&
            numbered.Words.Select(w => w.Line).Distinct().Count() == 3, numbered);
        string numberedParagraphs = string.Join("\n", numbered.Paragraphs.Select(p => p.Text));
        Check("every numbered source word appears once in paragraphs and selection with its original bounds",
            numberedWords.All(word => Occurrences(numberedParagraphs, word.Text) == 1 &&
                numbered.Words.Count(actual => actual.Text == word.Text && actual.Box == word.Box) == 1) &&
            numbered.Paragraphs.All(p => p.Lines.Length == 1 && numberedWords.Any(word => word.Text == p.Text && word.Box == p.Box)), numbered);

        var unsplitNumbered = ScreenOcr.Layout([
            Word("1. First\n2. Second", 0, 0, 0, 200, 40), Word("版本", 1, 100, 8, 30, 10)]);
        Check("an indivisible multiline source word retains its complete text and bounds without stealing a label",
            unsplitNumbered.Words.Length == 2 && unsplitNumbered.Paragraphs.Count(p => p.Text == "版本") == 1 &&
            unsplitNumbered.Paragraphs.Count(p => p.Text == "1. First\n2. Second" && p.Box == SKRect.Create(0, 0, 200, 40)) == 1 &&
            Occurrences(string.Join("\n", unsplitNumbered.Paragraphs.Select(p => p.Text)), "版本") == 1, unsplitNumbered);

        var denseWords = new List<ScreenWord>();
        for (int row = 0; row < 40; row++) for (int column = 0; column < 2; column++)
        {
            int index = row * 2 + column;
            float x = column * 360, y = row * 60;
            denseWords.Add(Word("Alpha" + index.ToString("D3"), index * 3, x, y, 100, 40));
            denseWords.Add(Word("Omega" + index.ToString("D3"), index * 3 + 1, x + 145, y, 100, 40));
            denseWords.Add(Word("版本" + index.ToString("D3"), index * 3 + 2, x + 105, y + 10, 35, 20));
        }
        var dense = ScreenOcr.Layout(denseWords.AsEnumerable().Reverse().ToList());
        string denseParagraphs = string.Join("\n", dense.Paragraphs.Select(p => p.Text));
        Check("dense two-column paragraphs contain every source token exactly once",
            denseWords.All(word => Occurrences(denseParagraphs, word.Text) == 1), dense);
        Check("dense input preserves all selectable words and coordinates",
            dense.Words.Length == denseWords.Count && denseWords.All(word => dense.Words.Count(actual =>
                actual.Text == word.Text && actual.Box == word.Box) == 1), dense);

        File.WriteAllText("layout-regressions.json", JsonSerializer.Serialize(new
        {
            passed = checks.Count(check => check.Passed), failed = checks.Count(check => !check.Passed), checks
        }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var check in checks) Console.WriteLine((check.Passed ? "PASS " : "FAIL ") + check.Name);
        if (checks.Any(check => !check.Passed)) throw new InvalidOperationException("Screen layout regression fixture failed; see layout-regressions.json.");
    }
}
