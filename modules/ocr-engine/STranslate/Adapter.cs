// Nook adapter for the unmodified MIT-licensed STranslate layout analyzer.
namespace STranslate.Plugin
{
    internal sealed class BoxPoint(float x, float y)
    {
        public float X { get; set; } = x;
        public float Y { get; set; } = y;
    }
    internal sealed class OcrContent
    {
        public string Text { get; set; } = "";
        public List<BoxPoint> BoxPoints { get; set; } = [];
    }
    internal sealed class OcrParagraph
    {
        public List<BoxPoint> BoxPoints { get; set; } = [];
        public List<OcrContent> Lines { get; set; } = [];
    }
    internal sealed class OcrRegion { public List<OcrParagraph> Paragraphs { get; set; } = []; }
    internal sealed class OcrResult
    {
        public List<OcrContent> OcrContents { get; set; } = [];
        public List<OcrRegion> Regions { get; set; } = [];
    }
    internal static class TextHelper
    {
        internal static bool IsCjk(char c) => c is >= '\u3400' and <= '\u9fff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af';
    }
}
namespace STranslate.Core
{
    internal enum LayoutAnalysisMode { Auto, Provider, Smart, NoMerge }
    internal static class Utilities
    {
        internal static bool HasBoxPoints(Plugin.OcrResult result) => result.OcrContents.Any(c => c.BoxPoints.Count > 0) || result.Regions.Any(r => r.Paragraphs.Any(p => p.Lines.Any(l => l.BoxPoints.Count > 0)));
    }
}
