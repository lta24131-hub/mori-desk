using RapidOcrNet;
using SkiaSharp;
using STranslate.Core;
using STranslate.Plugin;

internal sealed record ScreenWord(string Text, string Prefix, int Line, SKRect Box, double Confidence, int Tile = -1);
internal sealed record ScreenLine(string Text, SKRect Box);
internal sealed record ScreenParagraph(string Text, SKRect Box, ScreenLine[] Lines);
internal sealed record ScreenPage(ScreenWord[] Words, ScreenParagraph[] Paragraphs);

internal sealed class ScreenOcr : IDisposable
{
    internal const string Identity = "rapidocrnet-4.2.0-ppocrv5-ch-screen2x-layout4";
    private readonly RapidOcr engine = new();

    internal ScreenOcr()
    {
        var model = Path.Combine(AppContext.BaseDirectory, "models", "ch-v5");
        VerifyModels(model);
        engine.InitModels(Path.Combine(model, "ch_PP-OCRv5_mobile_det.onnx"),
            Path.Combine(model, "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
            Path.Combine(model, "ch_PP-OCRv5_rec_mobile_infer.onnx"),
            Path.Combine(model, "ppocrv5_dict.txt"), 2);
    }

    internal ScreenPage Read(SKBitmap source, CancellationToken cancel)
    {
        using var sourceImage = SKImage.FromBitmap(source);
        var words = new List<ScreenWord>();
        int lineId = 0,tileId=0;
        // Dense large screenshots are never shrunk to a thumbnail. A bounded
        // tile with context keeps small text at the same 2x detection scale.
        foreach (var core in Tiles(source.Width, source.Height))
        {
            cancel.ThrowIfCancellationRequested();
            int left = Math.Max(0, core.Left - 64), top = Math.Max(0, core.Top - 64);
            var region = new SKRectI(left, top, Math.Min(source.Width, core.Right + 64), Math.Min(source.Height, core.Bottom + 64));
            using var tile = new SKBitmap(region.Width * 2, region.Height * 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(tile))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawImage(sourceImage, region, new SKRect(0, 0, tile.Width, tile.Height), new SKSamplingOptions(SKCubicResampler.Mitchell));
            }
            var result = engine.Detect(tile, RapidOcrOptions.Default with
            {
                ImgResize = Math.Max(tile.Width, tile.Height) + 100,
                UnClipRatio = 1.2f, DoAngle = false, ReturnWordBox = true, TextScore = .5f
            }, cancel);
            foreach (var block in result.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text) || !block.Text.Any(char.IsLetterOrDigit)) continue;
                int end = 0, count = words.Count;
                if (block.WordResults is { Length: > 0 })
                {
                    foreach (var word in block.WordResults)
                    {
                        var box = Bounds(word.BoxPoints, left, top);
                        int start = block.Text.IndexOf(word.Text, end, StringComparison.Ordinal);
                        string prefix = start >= end ? block.Text[end..start] : " ";
                        if (start >= end) end = start + word.Text.Length;
                        if (!Owns(core, box)) continue;
                        words.Add(new(word.Text, words.Count == count ? "" : prefix, lineId, box, word.Score,tileId));
                    }
                }
                else
                {
                    var box = Bounds(block.BoxPoints, left, top);
                    if (Owns(core, box)) words.Add(new(block.Text, "", lineId, box, block.CharScores?.Average() ?? 1,tileId));
                }
                if (words.Count > count) lineId++;
            }
            tileId++;
        }
        return Layout(MergeTileFragments(words));
    }

    internal static List<ScreenWord> MergeTileFragments(List<ScreenWord> words)
    {
        var result=new List<ScreenWord>();
        foreach(var word in words.OrderBy(w=>w.Box.MidY).ThenBy(w=>w.Box.Left))
        {
            bool merged=false;
            for(int n=result.Count-1;n>=0;n--)
            {
                var other=result[n];
                if(word.Box.MidY-other.Box.MidY>Math.Max(128,word.Box.Height))break;
                if(word.Tile<0||other.Tile<0||word.Tile==other.Tile)continue;
                float smaller=Math.Min(word.Box.Height,other.Box.Height);
                if(Math.Abs(word.Box.MidY-other.Box.MidY)>smaller*.45f||
                    Math.Min(word.Box.Right,other.Box.Right)-Math.Max(word.Box.Left,other.Box.Left)<3)continue;
                var left=word.Box.Left<other.Box.Left?word:other;var right=ReferenceEquals(left,word)?other:word;
                int overlap=0;
                for(int length=Math.Min(left.Text.Length,right.Text.Length);length>=Math.Min(3,Math.Min(left.Text.Length,right.Text.Length));length--)
                    if(left.Text.EndsWith(right.Text[..length],StringComparison.Ordinal)){overlap=length;break;}
                if(overlap==0)continue;
                // Centres are owned by one tile, but a long token cut by both
                // context edges has two different centres. Join only agreeing,
                // geometrically overlapping fragments from different tiles.
                result[n]=left with {Text=left.Text+right.Text[overlap..],Box=new(Math.Min(left.Box.Left,right.Box.Left),Math.Min(left.Box.Top,right.Box.Top),Math.Max(left.Box.Right,right.Box.Right),Math.Max(left.Box.Bottom,right.Box.Bottom)),Confidence=Math.Min(left.Confidence,right.Confidence)};
                merged=true;break;
            }
            if(!merged)result.Add(word);
        }
        return result;
    }

    internal static IEnumerable<SKRectI> Tiles(int width, int height)
    {
        if ((long)width * height <= 450000 && Math.Max(width, height) <= 1800)
        {
            yield return new(0, 0, width, height); yield break;
        }
        for (int y = 0; y < height; y += 384)
            for (int x = 0; x < width; x += 960)
                yield return new(x, y, Math.Min(width, x + 960), Math.Min(height, y + 384));
    }

    private static bool Owns(SKRectI core, SKRect box) => box.MidX >= core.Left && box.MidX < core.Right && box.MidY >= core.Top && box.MidY < core.Bottom;
    private static SKRect Bounds(IEnumerable<SKPointI> points, int left, int top)
    {
        var p = points.ToArray();
        return new(p.Min(p => p.X) / 2f + left, p.Min(p => p.Y) / 2f + top, p.Max(p => p.X) / 2f + left, p.Max(p => p.Y) / 2f + top);
    }
    private static SKRect Bounds(IEnumerable<BoxPoint> points)
    {
        var p = points.ToArray(); return new(p.Min(p => p.X), p.Min(p => p.Y), p.Max(p => p.X), p.Max(p => p.Y));
    }
    private static List<BoxPoint> Corners(SKRect b) => [new(b.Left, b.Top), new(b.Right, b.Top), new(b.Right, b.Bottom), new(b.Left, b.Bottom)];

    internal static ScreenPage Layout(List<ScreenWord> words)
    {
        var ordered = words.GroupBy(w => w.Line).OrderBy(g => g.Min(w => w.Box.Top)).ThenBy(g => g.Min(w => w.Box.Left)).ToArray();
        var contents = new List<OcrContent>();
        foreach (var group in ordered)
        {
            var line = group.OrderBy(w => w.Box.Left).ToArray();
            var box = new SKRect(line.Min(w => w.Box.Left), line.Min(w => w.Box.Top), line.Max(w => w.Box.Right), line.Max(w => w.Box.Bottom));
            contents.Add(new() { Text = string.Concat(line.Select(w => w.Prefix + w.Text)).Trim(), BoxPoints = Corners(box) });
        }
        // Chinese version/date labels must not get absorbed into an English
        // description. The upstream analyzer still decides columns/paragraphs.
        var blocks = contents.GroupBy(c => c.Text.Count(ch => ch is >= '\u3400' and <= '\u9fff') >= 2)
            .SelectMany(group => OcrLayoutAnalyzer.AnalyzeBlocks(group.ToArray(), LayoutAnalysisMode.Smart))
            .OrderBy(b => b.BoxPoints.Min(p => p.Y)).ThenBy(b => b.BoxPoints.Min(p => p.X));
        var paragraphs = blocks.Select(b =>
        {
            var lines = b.LineBoxPoints.Select(Bounds).OrderBy(box=>box.Top).ThenBy(box=>box.Left)
                .SelectMany(box=>PhysicalLines(words.Where(w=>box.Contains(w.Box.MidX,w.Box.MidY))))
                .OrderBy(l=>l.Box.Top).ThenBy(l=>l.Box.Left).ToArray();
            return new ScreenParagraph(string.Join("\n",lines.Select(l=>l.Text)),Bounds(b.BoxPoints),lines);
        }).SelectMany(SplitHeading).ToArray();
        // Detector blocks are not physical reading lines: a single sentence
        // can arrive as six fragments with slightly different top coordinates.
        // Use the analyzed line boxes for both selection and the OCR transcript.
        var readingWords = new List<ScreenWord>();var used = new HashSet<ScreenWord>();int readingLine = 0;
        foreach (var paragraph in paragraphs) foreach (var line in paragraph.Lines)
        {
            ScreenWord? previous = null;
            foreach (var word in words.Where(w => !used.Contains(w) && line.Box.Contains(w.Box.MidX,w.Box.MidY)).OrderBy(w => w.Box.Left))
            {
                string prefix = previous == null ? "" : previous.Line == word.Line ? word.Prefix :
                    NeedsSpace(previous.Text.LastOrDefault(),word.Text.FirstOrDefault()) ? " " : "";
                readingWords.Add(word with { Line = readingLine, Prefix = prefix });used.Add(word);previous=word;
            }
            readingLine++;
        }
        foreach(var word in words.Where(w => !used.Contains(w)))readingWords.Add(word with { Line=readingLine++,Prefix="" });
        return new(readingWords.ToArray(), paragraphs);
    }
    // A line in another column can bridge two nearby rows in an upstream
    // aggregate box. Recheck physical baselines after column segmentation;
    // sorting that whole box by X would put a wrapped URL's tail before its head.
    private static IEnumerable<ScreenLine> PhysicalLines(IEnumerable<ScreenWord> source)
    {
        var rows=new List<List<ScreenWord>>();
        foreach(var word in source.OrderBy(w=>w.Box.MidY).ThenBy(w=>w.Box.Left))
        {
            var row=rows.LastOrDefault();
            var anchor=row==null?null:row.OrderBy(w=>w.Box.MidY).ElementAt(row.Count/2);
            if(anchor==null||Math.Abs(word.Box.MidY-anchor.Box.MidY)>Math.Min(word.Box.Height,anchor.Box.Height)*.45f)
            {row=new();rows.Add(row);}
            row!.Add(word);
        }
        foreach(var row in rows)
        {
            var ordered=row.OrderBy(w=>w.Box.Left).ToArray();ScreenWord? previous=null;
            string text=string.Concat(ordered.Select(word=>{
                string prefix=previous==null?"":previous.Line==word.Line?word.Prefix:
                    NeedsSpace(previous.Text.LastOrDefault(),word.Text.FirstOrDefault())?" ":"";
                previous=word;return prefix+word.Text;
            }));
            yield return new(text,new(row.Min(w=>w.Box.Left),row.Min(w=>w.Box.Top),row.Max(w=>w.Box.Right),row.Max(w=>w.Box.Bottom)));
        }
    }
    private static bool NeedsSpace(char previous,char next) => (previous < 0x2e80 && next < 0x2e80) ||
        previous is >= '\uac00' and <= '\ud7a3' || next is >= '\uac00' and <= '\ud7a3';
    private static IEnumerable<ScreenParagraph> SplitHeading(ScreenParagraph paragraph)
    {
        if(paragraph.Lines.Length>1)
        {
            var first=paragraph.Lines[0];var next=paragraph.Lines[1];
            if(first.Text.Length is >0 and <=60 && next.Text.Length>first.Text.Length*1.8 &&
                first.Box.Width<next.Box.Width*.65 && first.Box.Height>=next.Box.Height*.8 &&
                !".!?。！？,，;；".Contains(first.Text.TrimEnd().Last()))
            {
                yield return new(first.Text,first.Box,[first]);
                var rest=paragraph.Lines.Skip(1).ToArray();
                yield return new(string.Join("\n",rest.Select(l=>l.Text)),new(rest.Min(l=>l.Box.Left),rest.Min(l=>l.Box.Top),rest.Max(l=>l.Box.Right),rest.Max(l=>l.Box.Bottom)),rest);
                yield break;
            }
        }
        yield return paragraph;
    }
    private static void VerifyModels(string directory)
    {
        using var manifest=typeof(ScreenOcr).Assembly.GetManifestResourceStream("Nook.Ocr.Models.json")!;
        using var metadata=System.Text.Json.JsonDocument.Parse(manifest);
        foreach(var model in metadata.RootElement.EnumerateArray())
        {
            string name=model.GetProperty("name").GetString()!;
            using var file=File.OpenRead(Path.Combine(directory,name));
            if(file.Length!=model.GetProperty("bytes").GetInt64()||
                !Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(file)).Equals(model.GetProperty("sha256").GetString(),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("文字识别模型不完整，请修复或更新 Nook。原图已保留。");
        }
    }
    public void Dispose() => engine.Dispose();
}
