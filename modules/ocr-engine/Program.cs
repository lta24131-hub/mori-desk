using System.Diagnostics;
using System.Text.Json;
using RapidOcrNet;
using SkiaSharp;
using STranslate.Core;
using STranslate.Plugin;

// The benchmark entry is deliberately guest-only. Product IPC is added only
// after the real screenshot fixtures pass the candidate-engine evaluation.
internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 1 && args[0].StartsWith("--ocr-worker=", StringComparison.Ordinal)) return PipeWorker.Execute(args[0][13..]);
        try
        {
            if (args.Length != 1 || (args[0] != "--benchmark" && args[0] != "--screen" && args[0] != "--tiles" && args[0] != "--url-seam" && args[0] != "--layout-fixtures") ||
                !Environment.UserName.Equals("NookTester", StringComparison.OrdinalIgnoreCase) ||
                !AppContext.BaseDirectory.StartsWith(@"C:\NookLab\Audit\apps\", StringComparison.OrdinalIgnoreCase) ||
                !(Environment.GetEnvironmentVariable("MORI_DESK_DATA") ?? "").StartsWith(@"C:\NookLab\Audit\data\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This evaluation requires the dedicated Nook test VM.");
            if (args[0] == "--screen") ScreenBenchmark();
            else if(args[0]=="--tiles") TileBenchmark();
            else if(args[0]=="--url-seam") UrlSeamBenchmark();
            else if(args[0]=="--layout-fixtures") LayoutFixtures();
            else Benchmark();
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText("ocr-error.txt", error.ToString());
            return 1;
        }
    }

    static string Transcript(ScreenPage page) => string.Join("\n",page.Words.GroupBy(w=>w.Line).Select(g=>string.Concat(g.Select(w=>w.Prefix+w.Text))));
    static void UrlSeamBenchmark()
    {
        using var original=SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory,"samples","solidcraft.png"));
        using var image=SKImage.FromBitmap(original);using var input=new SKBitmap(original.Width,360);
        using(var canvas=new SKCanvas(input)){
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image,new SKRect(0,580,original.Width,680),new SKRect(0,40,original.Width,140));
        }
        if(ScreenOcr.Tiles(input.Width,input.Height).Count()!=2)throw new Exception("Fixture must straddle exactly two OCR tiles.");
        using var engine=new ScreenOcr();var timer=Stopwatch.StartNew();var page=engine.Read(input,CancellationToken.None);string text=Transcript(page);
        File.WriteAllText("url-seam-result.json",JsonSerializer.Serialize(new{text,elapsedMs=timer.ElapsedMilliseconds}));
        foreach(string url in new[]{"https://solidcraft.io/app","https://github.com/tangency-labs/solidcraft/issues"}){
            int first=text.IndexOf(url,StringComparison.Ordinal);
            if(first<0||text.IndexOf(url,first+url.Length,StringComparison.Ordinal)>=0)throw new Exception("Real OCR must return each seam URL exactly once: "+url);
        }
    }
    static void LayoutFixtures()
    {
        var seam=ScreenOcr.MergeTileFragments([
            new("browser](https://solidcraft.io","",0,SKRect.Create(837,587,184,18),.95,0),
            new("(https://solidcraft.io/app)-","",1,SKRect.Create(896,586,170.5f,20.5f),.95,1),
            new("[issues](https://github.com/tangency-labs","",2,SKRect.Create(746,620.5f,278,20),.95,0),
            new("com/tangency-labs/solidcraft/issues)-","",3,SKRect.Create(898,621.5f,254.5f,18),.95,1),
            new("again","",4,SKRect.Create(20,700,40,20),.95,0),new("again"," ",4,SKRect.Create(70,700,40,20),.95,0)]);
        if(seam.Count!=4||!seam.Any(w=>w.Text=="browser](https://solidcraft.io/app)-")||
            !seam.Any(w=>w.Text=="[issues](https://github.com/tangency-labs/solidcraft/issues)-")||seam.Count(w=>w.Text=="again")!=2)
            throw new Exception("Overlapping tile fragments duplicate a URL or remove a genuine repeated word.");
        var results=new List<object>();
        foreach(var name in new[]{"short-card","craft","solidcraft"})
        {
            using var input=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"samples",name+"-screen.json")));
            var words=input.RootElement.GetProperty("words").EnumerateArray().Select(w=>new ScreenWord(w.GetProperty("text").GetString()!,w.GetProperty("prefix").GetString()!,w.GetProperty("line").GetInt32(),
                SKRect.Create(w.GetProperty("x").GetSingle(),w.GetProperty("y").GetSingle(),w.GetProperty("width").GetSingle(),w.GetProperty("height").GetSingle()),w.GetProperty("score").GetDouble())).ToList();
              using var image=SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory,"samples",name+".png"));
              bool tiled=ScreenOcr.Tiles(image.Width,image.Height).Skip(1).Any();int columns=(image.Width+959)/960;
              int rawCount=words.Count;
              words=ScreenOcr.MergeTileFragments(words.Select(w=>w with {Tile=tiled?(int)(w.Box.MidY/384)*columns+(int)(w.Box.MidX/960):0}).ToList());
              words=words.Select(w=>w with {Line=w.Line*23+7}).Reverse().ToList();
              var page=ScreenOcr.Layout(words);var text=Transcript(page);
            if(page.Words.Length!=words.Count)throw new Exception("Layout lost or duplicated a word.");
            if(name=="short-card"&&!text.Contains("The built-in engines still show you everything they find."))throw new Exception("Fragment reading order failed.");
            if(name=="short-card"&&(page.Paragraphs.Length!=2||page.Paragraphs[0].Lines.Length!=1))throw new Exception("Heading was swallowed by description.");
            if(name=="craft"&&!text.Contains("版本0.2.0·2026年11月1日"))throw new Exception("Version punctuation failed.");
              if(name=="solidcraft"&&text.IndexOf("[roadmap",StringComparison.Ordinal)>text.IndexOf("labs/solidcraft/blob/main/ROADMAP",StringComparison.Ordinal))throw new Exception("Wrapped URL tail was read before its head.");
            WritePage(name + "-screen.json", page);
              results.Add(new{name,text,words=page.Words.Length,mergedTileFragments=rawCount-words.Count});
        }
        File.WriteAllText("layout-fixtures.json",JsonSerializer.Serialize(results));
    }
    static void TileBenchmark()
    {
        using var bitmap=new SKBitmap(1920,768,SKColorType.Bgra8888,SKAlphaType.Opaque);
        using(var canvas=new SKCanvas(bitmap))using(var face=SKTypeface.FromFamilyName("Arial"))using(var font=new SKFont(face,14))using(var paint=new SKPaint{Color=SKColors.Black,IsAntialias=true})
        {
            canvas.Clear(SKColors.White);font.Size=42;canvas.DrawText("Large heading",60,60,font,paint);font.Size=14;
            canvas.DrawText("Small text must remain readable.",80,120,font,paint);
            canvas.DrawText("Across the horizontal tile boundary",880,250,font,paint);
            canvas.DrawText("Across the vertical tile boundary",140,384,font,paint);
            canvas.DrawText("Corner seam keeps every word.",880,384,font,paint);
            canvas.DrawText("Bottom row is retained.",1100,720,font,paint);
            using var chinese=new SKFont(SKTypeface.FromFamilyName("Microsoft YaHei UI"),20);canvas.DrawText("版本 0.2.0 · 2026年11月2日",80,700,chinese,paint);
        }
        var timer=Stopwatch.StartNew();using var engine=new ScreenOcr();var page=engine.Read(bitmap,CancellationToken.None);var text=Transcript(page);
        File.WriteAllText("tile-result.json",JsonSerializer.Serialize(new{text,elapsedMs=timer.ElapsedMilliseconds,peakBytes=Process.GetCurrentProcess().PeakWorkingSet64,paragraphs=page.Paragraphs.Select(p=>p.Text)}));
        foreach(var expected in new[]{"Large heading","Small text must remain readable.","Across the horizontal tile boundary","Across the vertical tile boundary","Corner seam keeps every word.","Bottom row is retained."})
            if(!text.Contains(expected,StringComparison.OrdinalIgnoreCase))throw new Exception("Missing seam/size fixture: "+expected);
        if(!text.Contains("2026")||!text.Contains("版本"))throw new Exception("Chinese version label missing.");
        if(page.Words.Length != page.Paragraphs.Sum(p=>p.Lines.Sum(l=>page.Words.Count(w=>l.Box.Contains(w.Box.MidX,w.Box.MidY)))))throw new Exception("A seam word is duplicated or orphaned.");
        if(text.Split('\n').Length!=7)throw new Exception("Tile output must contain exactly the seven drawn lines.");
    }

    static void ScreenBenchmark()
    {
        var timer = Stopwatch.StartNew();using var engine = new ScreenOcr();long startup = timer.ElapsedMilliseconds;
        var results = new List<object>();
        foreach (var name in new[] { "short-card", "craft", "solidcraft" })
        {
            using var bitmap = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "samples", name + ".png"));
            timer.Restart();var page = engine.Read(bitmap, CancellationToken.None);
            WritePage(name + "-screen.json", page);
            results.Add(new { name, elapsedMs=timer.ElapsedMilliseconds, words=page.Words.Length, paragraphs=page.Paragraphs.Length });
        }
        File.WriteAllText("screen-summary.json", JsonSerializer.Serialize(new {startup, results, peakBytes=Process.GetCurrentProcess().PeakWorkingSet64}));
    }

    static void WritePage(string path, ScreenPage page) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { words = page.Words.Select(w => new { text=w.Text, prefix=w.Prefix, line=w.Line, x=w.Box.Left, y=w.Box.Top, width=w.Box.Width, height=w.Box.Height, score=w.Confidence }), paragraphs = page.Paragraphs.Select(p => new {text=p.Text,x=p.Box.Left,y=p.Box.Top,width=p.Box.Width,height=p.Box.Height,lines=p.Lines.Select(l=>new {text=l.Text,x=l.Box.Left,y=l.Box.Top,width=l.Box.Width,height=l.Box.Height})}) }));

    static void Benchmark()
    {
        var results = new List<object>();
        foreach (var variant in new[] { "ch", "latin" })
        {
        using var engine = new RapidOcr();
        var timer = Stopwatch.StartNew();
        var model = Path.Combine(AppContext.BaseDirectory, "models", "ch-v5");
        engine.InitModels(
            Path.Combine(model, "ch_PP-OCRv5_mobile_det.onnx"),
            Path.Combine(model, "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
            Path.Combine(model, variant == "ch" ? "ch_PP-OCRv5_rec_mobile_infer.onnx" : "latin_PP-OCRv5_rec_mobile_infer.onnx"),
            Path.Combine(model, variant == "ch" ? "ppocrv5_dict.txt" : "ppocrv5_latin_dict.txt"), 2);
        var startupMs = timer.ElapsedMilliseconds;
        foreach (var name in new[] { "short-card", "craft" })
        {
            using var bitmap = SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "samples", name + ".png"));
            if (bitmap == null) throw new InvalidDataException("Missing sample " + name);
            // Preserve screenshot resolution rather than compressing an entire
            // desktop to the library's 1024 px default.
            using var enlarged = bitmap.Resize(new SKImageInfo(bitmap.Width * 2, bitmap.Height * 2), new SKSamplingOptions(SKCubicResampler.Mitchell));
            var options = RapidOcrOptions.Default with
            {
                ImgResize = Math.Max(2048, Math.Max(enlarged.Width, enlarged.Height)),
                UnClipRatio = 1.2f,
                DoAngle = false,
                ReturnWordBox = true,
                TextScore = 0.5f
            };
            timer.Restart();
            var result = engine.Detect(enlarged, options);
            var sample = new
            {
                variant, name, width = bitmap.Width, height = bitmap.Height,
                elapsedMs = timer.ElapsedMilliseconds, text = result.StrRes,
                blocks = result.TextBlocks.Select(b => new
                {
                    text = b.Text, score = b.CharScores?.Average(),
                    box = b.BoxPoints.Select(p => new[] { p.X / 2f, p.Y / 2f }),
                    words = b.WordResults?.Select(w => new
                    {
                        text = w.Text, score = w.Score,
                        box = w.BoxPoints.Select(p => new[] { p.X / 2f, p.Y / 2f })
                    })
                })
            };
            File.WriteAllText(variant + "-" + name + "-rapid.json", JsonSerializer.Serialize(sample));
            var contents = result.TextBlocks.Where(b => b.Text.Any(char.IsLetterOrDigit)).Select(b => new OcrContent { Text = b.Text, BoxPoints = b.BoxPoints.Select(p => new BoxPoint(p.X / 2f, p.Y / 2f)).ToList() }).ToArray();
            var paragraphs = contents.GroupBy(c => c.Text.Count(ch => ch is >= '\u3400' and <= '\u9fff') >= 2).SelectMany(g => OcrLayoutAnalyzer.AnalyzeBlocks(g.ToArray(), LayoutAnalysisMode.Smart)).OrderBy(b => b.BoxPoints.Min(p => p.Y)).ThenBy(b => b.BoxPoints.Min(p => p.X)).ToArray();
            File.WriteAllText(variant + "-" + name + "-layout.json", JsonSerializer.Serialize(paragraphs));
            results.Add(new { variant, name, startupMs, sample.elapsedMs, lines = result.TextBlocks.Length });
        }
        }
        File.WriteAllText("rapid-summary.json", JsonSerializer.Serialize(new { results, peakBytes = Process.GetCurrentProcess().PeakWorkingSet64 }));
    }
}
